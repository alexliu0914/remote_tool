using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Protocol;

namespace RemoteTool.Connection;

public sealed class ControllerConnectOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required KnownHostsStore KnownHosts { get; init; }

    /// <summary>
    /// 對方公鑰從未信任、且此位址沒有紀錄時呼叫：參數為「位址:埠」與指紋，回傳 true 表示使用者核對無誤、願意信任。
    /// 回傳 false 則中止連線，帳密不會送出。介面通常先用 <see cref="ControllerClient.ProbeFingerprintAsync"/>
    /// 自行核對並記住，這裡只作為後備。
    /// </summary>
    public required Func<string, string, bool> ConfirmNewHost { get; init; }

    /// <summary>從開始連線到三條連線都建立完成的時限。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(ProtocolInfo.HeartbeatIntervalSeconds);
    /// <summary>多久沒收到被控端的心跳回覆就判定斷線（PLAN §7：6 秒）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(ProtocolInfo.HeartbeatTimeoutSeconds);

    /// <summary>Video / File 中斷後自動重連的時限（PLAN §7：30 秒內未成功即顯示失敗，可手動重試）。</summary>
    public TimeSpan SubChannelReconnectWindow { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>重連退避的第一個間隔（之後加倍：1、2、4…秒）。</summary>
    public TimeSpan SubChannelReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 收到 Video 封包時呼叫（在背景執行緒）。拋出例外會讓 Video 連線重連。
    /// <b>Payload 只在回呼執行期間有效</b>（讀取緩衝區會重複使用），需要保留請自行複製。
    /// </summary>
    public Action<Frame>? VideoFrameReceived { get; init; }
}

/// <summary>Video / File 子連線狀態。</summary>
public enum SubChannelState
{
    Connected,
    /// <summary>中斷後自動重連中（Control 仍在，遠端桌面不關閉）。</summary>
    Reconnecting,
    /// <summary>重連時限內未成功；可呼叫 <see cref="ControllerSession.RetrySubChannel"/> 手動重試。</summary>
    Failed,
}

/// <summary>
/// 控制端連線流程（Protocol V1）：
///   1. 開 Control TLS 1.3 連線，取得被控端 SPKI 指紋
///   2. 以公鑰判斷信任（G-06）：已信任直接繼續；同位址換了未信任的金鑰即中止；陌生則請使用者確認 —— 皆在送出帳密「之前」
///   3. Hello → LoginRequest → LoginResult → 收 Video / File 的一次性 token
///   4. 開 Video、File 連線（指紋必須與 Control 相同），以 nonce + HMAC 證明持有 token
///   5. 背景送心跳；Video / File 中斷時以新 token 自動重連（NET-03）
/// </summary>
public static class ControllerClient
{
    private static readonly string[] ClientCapabilities = [];

    public static async Task<ControllerSession> ConnectAsync(ControllerConnectOptions options, CancellationToken cancellationToken = default)
    {
        string hostKey = KnownHostsStore.KeyFor(options.Host, options.Port);
        var store = options.KnownHosts;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(options.ConnectTimeout);
        var ct = connectCts.Token;

        var opened = new List<FrameStream>();
        try
        {
            // 1~2. Control 連線與信任判斷；同位址換成未信任的金鑰時在握手階段即中止
            var (control, fingerprint) = await OpenTlsAsync(options.Host, options.Port,
                accept: fp => store.Evaluate(hostKey, fp) != HostTrust.Changed,
                onReject: fp => new FingerprintMismatchException(hostKey, store.HintFor(hostKey) ?? "", fp),
                ct).ConfigureAwait(false);
            opened.Add(control);

            if (store.Evaluate(hostKey, fingerprint) == HostTrust.Unknown)
            {
                // 後備路徑：使用者核對期間暫停逾時，確認後以釘選的指紋重新連線（D-032 / D-035）。
                connectCts.CancelAfter(Timeout.InfiniteTimeSpan);
                bool trusted = options.ConfirmNewHost(hostKey, fingerprint);
                await control.DisposeAsync().ConfigureAwait(false);
                opened.Remove(control);
                if (!trusted)
                    throw new FingerprintRejectedException(hostKey, fingerprint);
                store.Trust(hostKey, fingerprint);

                connectCts.CancelAfter(options.ConnectTimeout);
                ct.ThrowIfCancellationRequested();
                (control, _) = await OpenPinnedAsync(options, hostKey, fingerprint, ct).ConfigureAwait(false);
                opened.Add(control);
            }
            else
            {
                store.RecordAddress(hostKey, fingerprint); // 已信任的公鑰出現在此位址
            }

            // 3. Hello 與登入
            await ExchangeHelloAsync(control, ChannelKind.Control, ct).ConfigureAwait(false);
            uint loginId = control.NextRequestId();
            await control.WriteAsync(new LoginRequest(options.Username, options.Password), loginId, ct).ConfigureAwait(false);
            var (resultFrame, result) = await control.ReadMessageAsync<LoginResult>(ct).ConfigureAwait(false);
            if (resultFrame.RequestId != loginId)
                throw new ProtocolViolationException("LoginResult 的 RequestId 不符");
            if (!result.Success)
                throw new AuthenticationFailedException(result.Error, result.RetryAfterSeconds);

            var tokens = new Dictionary<ChannelKind, byte[]>();
            while (tokens.Count < 2)
            {
                var (_, grant) = await control.ReadMessageAsync<SessionTokenGrant>(ct).ConfigureAwait(false);
                ValidateGrant(grant);
                tokens[grant.Role] = grant.Token;
            }

            // 4. 子連線
            var video = await OpenSubChannelAsync(options, hostKey, fingerprint, ChannelKind.Video, tokens[ChannelKind.Video], ct).ConfigureAwait(false);
            opened.Add(video);
            var file = await OpenSubChannelAsync(options, hostKey, fingerprint, ChannelKind.File, tokens[ChannelKind.File], ct).ConfigureAwait(false);
            opened.Add(file);
            foreach (var t in tokens.Values) CryptographicOperations.ZeroMemory(t);

            // 5. 背景迴圈
            var session = new ControllerSession(hostKey, fingerprint, control, video, file, options);
            session.Start();
            return session;
        }
        catch
        {
            foreach (var s in opened)
            {
                try { await s.DisposeAsync().ConfigureAwait(false); } catch { /* 清理 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 只做 TLS 握手取得對方的主機指紋，隨即關閉連線，不送任何資料（不送帳密）。
    /// 供介面在「沒有任何進行中的連線」的狀態下請使用者核對指紋。
    /// </summary>
    public static async Task<string> ProbeFingerprintAsync(string host, int port, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        var (stream, fingerprint) = await OpenTlsAsync(host, port, accept: _ => true, onReject: null, cts.Token)
            .ConfigureAwait(false);
        try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* 只要指紋 */ }
        return fingerprint;
    }

    internal static void ValidateGrant(SessionTokenGrant grant)
    {
        if (grant.Role is not (ChannelKind.Video or ChannelKind.File)
            || grant.Token is null || grant.Token.Length != ProtocolInfo.SessionTokenSize)
            throw new ProtocolViolationException("SessionTokenGrant 內容不合法");
    }

    /// <summary>以已釘選的指紋開連線（子連線、重連，指紋不同即中止）。</summary>
    private static Task<(FrameStream Stream, string Fingerprint)> OpenPinnedAsync(
        ControllerConnectOptions options, string hostKey, string fingerprint, CancellationToken ct, bool reuseReadBuffer = false)
        => OpenTlsAsync(options.Host, options.Port,
            accept: fp => fp == fingerprint,
            onReject: fp => new FingerprintMismatchException(hostKey, fingerprint, fp),
            ct, reuseReadBuffer);

    internal static async Task<FrameStream> OpenSubChannelAsync(
        ControllerConnectOptions options, string hostKey, string fingerprint,
        ChannelKind role, byte[] token, CancellationToken ct)
    {
        // Video 封包大且多，收到後在回呼中同步處理完，因此重複使用讀取緩衝區（V-12）。
        var (stream, _) = await OpenPinnedAsync(options, hostKey, fingerprint, ct,
            reuseReadBuffer: role == ChannelKind.Video).ConfigureAwait(false);
        try
        {
            await ExchangeHelloAsync(stream, role, ct).ConfigureAwait(false);
            var (_, challenge) = await stream.ReadMessageAsync<SubConnectChallenge>(ct).ConfigureAwait(false);
            if (challenge.Nonce is null || challenge.Nonce.Length != ProtocolInfo.SubConnectNonceSize)
                throw new ProtocolViolationException("SubConnectChallenge 內容不合法");

            await stream.WriteAsync(new SubConnectAuth(SubConnectProof.Compute(token, challenge.Nonce, role)),
                cancellationToken: ct).ConfigureAwait(false);
            var (_, result) = await stream.ReadMessageAsync<SubConnectResult>(ct).ConfigureAwait(false);
            if (!result.Success)
                throw new AuthenticationFailedException(result.Error);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExchangeHelloAsync(FrameStream stream, ChannelKind role, CancellationToken ct)
    {
        await stream.WriteAsync(new Hello(ProtocolInfo.Version, role, ClientCapabilities), cancellationToken: ct).ConfigureAwait(false);
        var (_, reply) = await stream.ReadMessageAsync<Hello>(ct).ConfigureAwait(false);
        if (reply.ProtocolVersion != ProtocolInfo.Version)
            throw new RemoteErrorException(ErrorCode.VersionMismatch,
                $"協定版本不相容：被控端 V{reply.ProtocolVersion}，控制端 V{ProtocolInfo.Version}，請更新兩端程式");
        if (reply.Role != role)
            throw new ProtocolViolationException($"Hello 角色不符：預期 {role}，收到 {reply.Role}");
        stream.Role = role;
    }

    /// <summary>
    /// 建立 TCP + TLS 1.3。<paramref name="accept"/> 判斷對方指紋是否可接受；不接受時在握手階段中止，
    /// 並拋出 <paramref name="onReject"/> 產生的例外。
    /// </summary>
    private static async Task<(FrameStream Stream, string Fingerprint)> OpenTlsAsync(
        string host, int port, Func<string, bool> accept, Func<string, Exception>? onReject,
        CancellationToken ct, bool reuseReadBuffer = false)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        string? observed = null;
        bool rejected = false;
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // 自簽憑證：不看憑證鏈、名稱與到期日，只比對 SPKI 指紋。
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var cert2 = new X509Certificate2(certificate);
                    observed = Fingerprint.Compute(cert2);
                    rejected = !accept(observed);
                    return !rejected;
                },
            }, ct).ConfigureAwait(false);

            return (new FrameStream(ssl, Endpoint.Controller) { ReuseReadBuffer = reuseReadBuffer }, observed!);
        }
        catch (AuthenticationException) when (rejected && observed is not null && onReject is not null)
        {
            Dispose();
            throw onReject(observed);
        }
        catch
        {
            Dispose();
            throw;
        }

        void Dispose()
        {
            ssl?.Dispose();
            tcp.Dispose();
        }
    }
}

/// <summary>已登入的工作階段：三條連線、背景心跳、子連線自動重連。</summary>
public sealed class ControllerSession : IAsyncDisposable
{
    private readonly ControllerConnectOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<uint, TaskCompletionSource<SessionTokenGrant>> _tokenRequests = new();
    private readonly Dictionary<ChannelKind, SemaphoreSlim> _manualRetry = new()
    {
        [ChannelKind.Video] = new SemaphoreSlim(0, 1),
        [ChannelKind.File] = new SemaphoreSlim(0, 1),
    };
    private readonly Dictionary<ChannelKind, SubChannelState> _states = new()
    {
        [ChannelKind.Video] = SubChannelState.Connected,
        [ChannelKind.File] = SubChannelState.Connected,
    };
    private FrameStream _video;
    private FrameStream _file;
    private Task? _loops;
    private long _lastRoundTripTicks = -1;
    private long _videoBytesReceived;
    private int _reconnects;

    internal ControllerSession(string hostKey, string fingerprint,
        FrameStream control, FrameStream video, FrameStream file, ControllerConnectOptions options)
    {
        HostKey = hostKey;
        Fingerprint = fingerprint;
        ControlChannel = control;
        _video = video;
        _file = file;
        _options = options;
    }

    public string HostKey { get; }
    public string Fingerprint { get; }
    public FrameStream ControlChannel { get; }
    /// <summary>目前的 Video 連線（重連後會換成新的）。</summary>
    public FrameStream VideoChannel => Volatile.Read(ref _video);
    /// <summary>目前的 File 連線（重連後會換成新的）。</summary>
    public FrameStream FileChannel => Volatile.Read(ref _file);

    /// <summary>工作階段結束時完成，結果為結束原因。只有 Control 結束（或本端中斷）才會結束工作階段。</summary>
    public Task<string> Completion => _closed.Task;

    /// <summary>子連線狀態改變（在背景執行緒）：角色、新狀態、原因。</summary>
    public event Action<ChannelKind, SubChannelState, string?>? SubChannelStateChanged;

    public SubChannelState GetState(ChannelKind role)
    {
        lock (_states) return _states[role];
    }

    /// <summary>子連線成功重連的累計次數（測試與記錄用）。</summary>
    public int ReconnectCount => Volatile.Read(ref _reconnects);

    private int _videoReconnects, _fileReconnects;

    /// <summary>指定角色成功重連的累計次數。</summary>
    public int GetReconnectCount(ChannelKind role)
        => role == ChannelKind.Video ? Volatile.Read(ref _videoReconnects) : Volatile.Read(ref _fileReconnects);

    /// <summary>最近一次心跳的來回時間；尚未量到為 null。</summary>
    public TimeSpan? LastRoundTrip
    {
        get
        {
            long t = Interlocked.Read(ref _lastRoundTripTicks);
            return t < 0 ? null : TimeSpan.FromTicks(t);
        }
    }

    /// <summary>Video 連線累計收到的位元組數（統計用）。</summary>
    public long VideoBytesReceived => Interlocked.Read(ref _videoBytesReceived);

    /// <summary>
    /// 向 Control 申請新的子連線 token（子連線重連前使用，PLAN §7）。
    /// 回傳的 token 須在 <see cref="SessionTokenGrant.ExpiresInSeconds"/> 內使用。
    /// </summary>
    public async Task<SessionTokenGrant> RequestSubChannelTokenAsync(ChannelKind role, CancellationToken cancellationToken = default)
    {
        uint id = ControlChannel.NextRequestId();
        var tcs = new TaskCompletionSource<SessionTokenGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_tokenRequests) _tokenRequests[id] = tcs;
        try
        {
            await ControlChannel.WriteAsync(new SessionTokenRequest(role), id, cancellationToken).ConfigureAwait(false);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            using var closedReg = _cts.Token.Register(() => tcs.TrySetException(new IOException("工作階段已結束")));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_tokenRequests) _tokenRequests.Remove(id);
        }
    }

    /// <summary>要求被控端下一次送出完整畫面。</summary>
    public ValueTask RequestKeyframeAsync(long lastFrameId, CancellationToken cancellationToken = default)
        => VideoChannel.WriteAsync(new KeyframeRequest(lastFrameId), cancellationToken: cancellationToken);

    /// <summary>子連線重連失敗（<see cref="SubChannelState.Failed"/>）後手動重試。</summary>
    public void RetrySubChannel(ChannelKind role)
    {
        if (GetState(role) != SubChannelState.Failed) return;
        try { _manualRetry[role].Release(); } catch (SemaphoreFullException) { /* 已在重試 */ }
    }

    internal void Start()
    {
        _loops = Task.WhenAll(
            RunSessionCriticalAsync(ControlLoopAsync, "Control"),
            RunSessionCriticalAsync(ct => SubChannelLoopAsync(ChannelKind.Video, _options.VideoFrameReceived, ct), "Video"),
            RunSessionCriticalAsync(ct => SubChannelLoopAsync(ChannelKind.File, null, ct), "File"));
    }

    /// <summary>迴圈結束即結束整個工作階段。子連線迴圈只會因工作階段結束或未預期錯誤而結束。</summary>
    private async Task RunSessionCriticalAsync(Func<CancellationToken, Task<string>> loop, string name)
    {
        string reason;
        try
        {
            reason = await loop(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            reason = $"{name} 連線中斷：{ex.Message}";
        }

        _closed.TrySetResult(reason);
        _cts.Cancel();
    }

    private void SetState(ChannelKind role, SubChannelState state, string? detail)
    {
        lock (_states)
        {
            if (_states[role] == state) return;
            _states[role] = state;
        }
        SubChannelStateChanged?.Invoke(role, state, detail);
    }

    // ------------------------------------------------------------------ Control

    private async Task<string> ControlLoopAsync(CancellationToken ct)
    {
        var pending = new Dictionary<uint, long>();
        long lastReply = Environment.TickCount64;
        var timeoutMs = (long)_options.HeartbeatTimeout.TotalMilliseconds;

        var sender = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            do
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastReply) > timeoutMs)
                    throw new TimeoutException("被控端心跳逾時");
                uint id = ControlChannel.NextRequestId();
                lock (pending) pending[id] = Environment.TickCount64;
                await ControlChannel.WriteAsync(new Heartbeat(Environment.TickCount64), id, ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }, ct);

        var reader = Task.Run(async () =>
        {
            while (true)
            {
                Frame? frame = await ControlChannel.ReadAsync(ct).ConfigureAwait(false);
                if (frame is null) return "被控端已結束連線";
                var f = frame.Value;

                switch (f.Type)
                {
                    case MessageType.Heartbeat:
                        MessageCodec.Decode<Heartbeat>(f);
                        long sentAt;
                        bool found;
                        lock (pending) found = pending.Remove(f.RequestId, out sentAt);
                        if (found)
                        {
                            Interlocked.Exchange(ref lastReply, Environment.TickCount64);
                            Interlocked.Exchange(ref _lastRoundTripTicks,
                                TimeSpan.FromMilliseconds(Environment.TickCount64 - sentAt).Ticks);
                        }
                        break;
                    case MessageType.SessionTokenGrant:
                        var grant = MessageCodec.Decode<SessionTokenGrant>(f);
                        ControllerClient.ValidateGrant(grant);
                        TaskCompletionSource<SessionTokenGrant>? tcs;
                        lock (_tokenRequests) _tokenRequests.TryGetValue(f.RequestId, out tcs);
                        tcs?.TrySetResult(grant);
                        break;
                    case MessageType.Error:
                        var err = MessageCodec.Decode<ErrorMessage>(f);
                        return $"被控端回報錯誤（{err.Code}）：{err.Message}";
                    default:
                        break; // V1 已定義、尚未實作的訊息
                }
            }
        }, ct);

        var done = await Task.WhenAny(sender, reader).ConfigureAwait(false);
        if (done == reader) return await reader.ConfigureAwait(false);
        await sender.ConfigureAwait(false); // 拋出 sender 的例外
        return "心跳停止";
    }

    // ------------------------------------------------------------------ Video / File 與重連（NET-03）

    private ref FrameStream Slot(ChannelKind role) => ref role == ChannelKind.Video ? ref _video : ref _file;

    private async Task<string> SubChannelLoopAsync(ChannelKind role, Action<Frame>? handler, CancellationToken ct)
    {
        while (true)
        {
            string reason;
            try
            {
                reason = await DrainAsync(Volatile.Read(ref Slot(role)), role, handler, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }
            ct.ThrowIfCancellationRequested();

            // Control 仍在：只重連這一條，不關閉遠端桌面（PLAN §7）
            SetState(role, SubChannelState.Reconnecting, reason);
            while (true)
            {
                var stream = await TryReconnectAsync(role, ct).ConfigureAwait(false);
                if (stream is not null)
                {
                    var old = Interlocked.Exchange(ref Slot(role), stream);
                    try { await old.DisposeAsync().ConfigureAwait(false); } catch { /* 已斷 */ }
                    Interlocked.Increment(ref _reconnects);
                    if (role == ChannelKind.Video) Interlocked.Increment(ref _videoReconnects);
                    else Interlocked.Increment(ref _fileReconnects);
                    SetState(role, SubChannelState.Connected, null);
                    break;
                }
                SetState(role, SubChannelState.Failed, $"{(int)_options.SubChannelReconnectWindow.TotalSeconds} 秒內無法重新連線");
                await _manualRetry[role].WaitAsync(ct).ConfigureAwait(false);
                SetState(role, SubChannelState.Reconnecting, "手動重試");
            }
        }
    }

    /// <summary>在重連時限內以指數退避（1、2、4…秒）重試；每次先向 Control 申請新 token。失敗回傳 null。</summary>
    private async Task<FrameStream?> TryReconnectAsync(ChannelKind role, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)_options.SubChannelReconnectWindow.TotalMilliseconds;
        var delay = _options.SubChannelReconnectInitialDelay;
        while (true)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, remaining)), ct).ConfigureAwait(false);

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(10));
                var grant = await RequestSubChannelTokenAsync(role, attemptCts.Token).ConfigureAwait(false);
                try
                {
                    return await ControllerClient.OpenSubChannelAsync(_options, HostKey, Fingerprint, role, grant.Token, attemptCts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(grant.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (FingerprintMismatchException)
            {
                return null; // 主機金鑰在工作階段中改變：不重試
            }
            catch (Exception)
            {
                // 網路尚未恢復等：退避後再試
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 16_000));
        }
    }

    private async Task<string> DrainAsync(FrameStream stream, ChannelKind role, Action<Frame>? handler, CancellationToken ct)
    {
        while (true)
        {
            Frame? frame = await stream.ReadAsync(ct).ConfigureAwait(false);
            if (frame is null) return $"被控端已關閉 {role} 連線";
            var f = frame.Value;
            if (f.Type == MessageType.Error)
            {
                var err = MessageCodec.Decode<ErrorMessage>(f);
                return $"被控端回報錯誤（{err.Code}）：{err.Message}";
            }
            if (role == ChannelKind.Video)
                Interlocked.Add(ref _videoBytesReceived, f.Payload.Length + FrameCodec.LengthPrefixSize + FrameCodec.HeaderSize);
            handler?.Invoke(f);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed.TrySetResult("控制端已中斷連線");
        _cts.Cancel();
        if (_loops is not null)
        {
            try { await _loops.ConfigureAwait(false); } catch { /* 已記錄於 Completion */ }
        }
        await ControlChannel.DisposeAsync().ConfigureAwait(false);
        await VideoChannel.DisposeAsync().ConfigureAwait(false);
        await FileChannel.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
