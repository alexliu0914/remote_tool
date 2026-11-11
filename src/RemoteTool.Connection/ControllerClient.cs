using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
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
    /// 首次連到某主機時呼叫：參數為「位址:埠」與指紋，回傳 true 表示使用者核對無誤、願意信任。
    /// 回傳 false 則中止連線，帳密不會送出。
    /// </summary>
    public required Func<string, string, bool> ConfirmNewHost { get; init; }

    /// <summary>從開始連線到三條連線都建立完成的時限。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>多久沒收到被控端的心跳回覆就判定斷線。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// 控制端連線流程：
///   1. 開 Control TLS 1.3 連線，取得被控端 SPKI 指紋
///   2. 與已知指紋比對；不同即中止，首次則請使用者確認 —— 兩者都在送出帳密「之前」
///   3. Hello → LoginRequest → LoginResult → 收 Video / File 的一次性 token
///   4. 以 token 開 Video、File 連線（指紋必須與 Control 相同）
///   5. 背景送心跳、接收補發的 token
/// </summary>
public static class ControllerClient
{
    public static async Task<ControllerSession> ConnectAsync(ControllerConnectOptions options, CancellationToken cancellationToken = default)
    {
        string hostKey = KnownHostsStore.KeyFor(options.Host, options.Port);
        string? known = options.KnownHosts.Get(hostKey);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(options.ConnectTimeout);
        var ct = connectCts.Token;

        var opened = new List<FrameStream>();
        try
        {
            // 1~2. Control 連線與指紋核對
            var (control, fingerprint) = await OpenTlsAsync(options.Host, options.Port, known, hostKey, ct).ConfigureAwait(false);
            opened.Add(control);

            if (known is null)
            {
                if (!options.ConfirmNewHost(hostKey, fingerprint))
                    throw new FingerprintRejectedException(hostKey, fingerprint);
                options.KnownHosts.Set(hostKey, fingerprint);
            }

            // 3. Hello 與登入
            await ExchangeHelloAsync(control, ChannelKind.Control, ct).ConfigureAwait(false);
            await control.WriteAsync(ChannelKind.Control, new LoginRequest(options.Username, options.Password), ct).ConfigureAwait(false);
            var result = await control.ReadMessageAsync<LoginResult>(ChannelKind.Control, ct).ConfigureAwait(false);
            if (!result.Success)
                throw new AuthenticationFailedException(result.Error);

            var tokens = new Dictionary<ChannelKind, byte[]>();
            while (tokens.Count < 2)
            {
                var grant = await control.ReadMessageAsync<SessionTokenGrant>(ChannelKind.Control, ct).ConfigureAwait(false);
                ValidateGrant(grant);
                tokens[grant.Channel] = grant.Token;
            }

            // 4. 子連線
            var video = await OpenSubChannelAsync(options, hostKey, fingerprint, ChannelKind.Video, tokens[ChannelKind.Video], ct).ConfigureAwait(false);
            opened.Add(video);
            var file = await OpenSubChannelAsync(options, hostKey, fingerprint, ChannelKind.File, tokens[ChannelKind.File], ct).ConfigureAwait(false);
            opened.Add(file);

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

    private static void ValidateGrant(SessionTokenGrant grant)
    {
        if (grant.Channel is not (ChannelKind.Video or ChannelKind.File)
            || grant.Token is null || grant.Token.Length != ProtocolInfo.SessionTokenSize)
            throw new ProtocolViolationException("SessionTokenGrant 內容不合法");
    }

    private static async Task<FrameStream> OpenSubChannelAsync(
        ControllerConnectOptions options, string hostKey, string fingerprint,
        ChannelKind channel, byte[] token, CancellationToken ct)
    {
        var (stream, _) = await OpenTlsAsync(options.Host, options.Port, fingerprint, hostKey, ct).ConfigureAwait(false);
        try
        {
            await ExchangeHelloAsync(stream, channel, ct).ConfigureAwait(false);
            await stream.WriteAsync(channel, new SubConnectAuth(token), ct).ConfigureAwait(false);
            var result = await stream.ReadMessageAsync<SubConnectResult>(channel, ct).ConfigureAwait(false);
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

    private static async Task ExchangeHelloAsync(FrameStream stream, ChannelKind channel, CancellationToken ct)
    {
        await stream.WriteAsync(channel, new Hello(ProtocolInfo.Version, channel), ct).ConfigureAwait(false);
        var reply = await stream.ReadMessageAsync<Hello>(channel, ct).ConfigureAwait(false);
        if (reply.ProtocolVersion != ProtocolInfo.Version || reply.Channel != channel)
            throw new ProtocolViolationException($"Hello 不符：版本 {reply.ProtocolVersion}，通道 {reply.Channel}");
    }

    /// <summary>
    /// 建立 TCP + TLS 1.3。<paramref name="expectedFingerprint"/> 非 null 時，指紋不同即在握手階段中止。
    /// </summary>
    private static async Task<(FrameStream Stream, string Fingerprint)> OpenTlsAsync(
        string host, int port, string? expectedFingerprint, string hostKey, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        string? observed = null;
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
                    return expectedFingerprint is null || observed == expectedFingerprint;
                },
            }, ct).ConfigureAwait(false);

            return (new FrameStream(ssl), observed!);
        }
        catch (AuthenticationException) when (expectedFingerprint is not null && observed is not null && observed != expectedFingerprint)
        {
            Dispose();
            throw new FingerprintMismatchException(hostKey, expectedFingerprint, observed);
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

/// <summary>已登入的工作階段：三條連線與背景心跳。</summary>
public sealed class ControllerSession : IAsyncDisposable
{
    private readonly ControllerConnectOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<ChannelKind, byte[]> _reconnectTokens = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loops;
    private long _lastRoundTripTicks = -1;

    internal ControllerSession(string hostKey, string fingerprint,
        FrameStream control, FrameStream video, FrameStream file, ControllerConnectOptions options)
    {
        HostKey = hostKey;
        Fingerprint = fingerprint;
        ControlChannel = control;
        VideoChannel = video;
        FileChannel = file;
        _options = options;
    }

    public string HostKey { get; }
    public string Fingerprint { get; }
    public FrameStream ControlChannel { get; }
    public FrameStream VideoChannel { get; }
    public FrameStream FileChannel { get; }

    /// <summary>工作階段結束時完成，結果為結束原因。</summary>
    public Task<string> Completion => _closed.Task;

    /// <summary>最近一次心跳的來回時間；尚未量到為 null。</summary>
    public TimeSpan? LastRoundTrip
    {
        get
        {
            long t = Interlocked.Read(ref _lastRoundTripTicks);
            return t < 0 ? null : TimeSpan.FromTicks(t);
        }
    }

    /// <summary>被控端最近補發、可供重連使用的 token。</summary>
    public bool HasReconnectToken(ChannelKind channel)
    {
        lock (_gate) return _reconnectTokens.ContainsKey(channel);
    }

    internal void Start()
    {
        _loops = Task.WhenAll(
            RunAsync(ControlLoopAsync, "Control"),
            RunAsync(ct => DrainAsync(VideoChannel, ChannelKind.Video, ct), "Video"),
            RunAsync(ct => DrainAsync(FileChannel, ChannelKind.File, ct), "File"));
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> loop, string name)
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

        // 任一條連線結束即結束整個工作階段（子通道重連於後續里程碑實作）。
        _closed.TrySetResult(reason);
        _cts.Cancel();
    }

    private async Task<string> ControlLoopAsync(CancellationToken ct)
    {
        var pending = new Dictionary<long, long>();
        long sequence = 0;
        long lastReply = Environment.TickCount64;
        var timeoutMs = (long)_options.HeartbeatTimeout.TotalMilliseconds;

        var sender = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            do
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastReply) > timeoutMs)
                    throw new TimeoutException("被控端心跳逾時");
                long seq = Interlocked.Increment(ref sequence);
                lock (pending) pending[seq] = Environment.TickCount64;
                await ControlChannel.WriteAsync(ChannelKind.Control, new Heartbeat(seq), ct).ConfigureAwait(false);
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
                if (f.Channel != ChannelKind.Control)
                    throw new ProtocolViolationException($"Control 連線收到 {f.Channel} 封包");

                switch (f.Type)
                {
                    case MessageType.Heartbeat:
                        var hb = MessageCodec.Decode<Heartbeat>(f);
                        long sentAt;
                        bool found;
                        lock (pending) found = pending.Remove(hb.Sequence, out sentAt);
                        if (found)
                        {
                            Interlocked.Exchange(ref lastReply, Environment.TickCount64);
                            Interlocked.Exchange(ref _lastRoundTripTicks,
                                TimeSpan.FromMilliseconds(Environment.TickCount64 - sentAt).Ticks);
                        }
                        break;
                    case MessageType.SessionTokenGrant:
                        var grant = MessageCodec.Decode<SessionTokenGrant>(f);
                        if (grant.Channel is ChannelKind.Video or ChannelKind.File && grant.Token?.Length == ProtocolInfo.SessionTokenSize)
                        {
                            lock (_gate) _reconnectTokens[grant.Channel] = grant.Token;
                        }
                        break;
                    case MessageType.Error:
                        return "被控端回報錯誤：" + MessageCodec.Decode<ErrorMessage>(f).Message;
                    default:
                        break; // 其他訊息於後續里程碑實作
                }
            }
        }, ct);

        var done = await Task.WhenAny(sender, reader).ConfigureAwait(false);
        if (done == reader) return await reader.ConfigureAwait(false);
        await sender.ConfigureAwait(false); // 拋出 sender 的例外
        return "心跳停止";
    }

    private static async Task<string> DrainAsync(FrameStream stream, ChannelKind channel, CancellationToken ct)
    {
        while (true)
        {
            Frame? frame = await stream.ReadAsync(ct).ConfigureAwait(false);
            if (frame is null) return $"被控端已關閉 {channel} 連線";
            // 影像 / 檔案資料於後續里程碑處理。
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
