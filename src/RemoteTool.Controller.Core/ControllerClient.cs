using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Controller.Core;

/// <summary>首次連線時交給使用者核對的資訊。</summary>
public sealed record FingerprintPrompt(string Address, byte[] Fingerprint)
{
    public string FingerprintText => HostFingerprint.Format(Fingerprint);
}

public sealed class ControllerClientOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = ProtocolConstants.DefaultPort;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required KnownHostStore KnownHosts { get; init; }

    /// <summary>
    /// 首次連線：顯示指紋，使用者在被控端螢幕或其他可信管道核對後回傳 true 才繼續。
    /// </summary>
    public required Func<FingerprintPrompt, CancellationToken, Task<bool>> ConfirmFingerprint { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = ProtocolConstants.HeartbeatInterval;
    public TimeSpan HeartbeatTimeout { get; init; } = ProtocolConstants.HeartbeatTimeout;
    public TimeSpan InputHeartbeatInterval { get; init; } = ProtocolConstants.InputHeartbeatInterval;

    /// <summary>子連線重連的總時限；超過就回報失敗，可手動重試。</summary>
    public TimeSpan ReconnectWindow { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// 控制端連線（第三、七節）。建立 Control 連線並登入後，以一次性 Token 開 Video 與 File 子連線。
/// Control 中斷 → 整個工作階段結束；Video、File 中斷 → 以指數退避各自重連。
/// </summary>
public sealed class ControllerClient : IAsyncDisposable
{
    private readonly ControllerClientOptions _options;
    private readonly byte[] _fingerprint;
    private readonly FramedConnection _control;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Frame>> _pending = new();
    private readonly TaskCompletionSource<DisconnectReason> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _sync = new();
    private readonly HashSet<(ushort, bool)> _keysHeld = [];
    private readonly HashSet<MouseButton> _buttonsHeld = [];

    private FramedConnection? _video;
    private FramedConnection? _file;
    private long _controlLastHeard, _videoLastHeard, _fileLastHeard;
    private uint _requestIds;
    private uint _generation;
    private uint? _pendingResume;
    private uint _lastKeyframeGeneration;
    private bool _canSendInput;
    private PauseReason _pauseReason = PauseReason.NotStarted;
    private int _closed;
    private Task _backgroundTasks = Task.CompletedTask;

    private ControllerClient(ControllerClientOptions options, byte[] fingerprint, FramedConnection control, uint generation)
    {
        _options = options;
        _fingerprint = fingerprint;
        _control = control;
        _generation = generation;
        _controlLastHeard = options.Time.GetTimestamp();
    }

    public byte[] HostFingerprintBytes => (byte[])_fingerprint.Clone();

    /// <summary>目前的輸入世代。</summary>
    public uint Generation { get { lock (_sync) return _generation; } }

    /// <summary>已收到該世代的 IDR 並回覆，才可送鍵鼠。</summary>
    public bool CanSendInput { get { lock (_sync) return _canSendInput; } }

    public PauseReason PauseReason { get { lock (_sync) return _pauseReason; } }
    public bool IsVideoConnected { get { lock (_sync) return _video is not null; } }
    public bool IsFileConnected { get { lock (_sync) return _file is not null; } }

    /// <summary>工作階段結束時完成。</summary>
    public Task<DisconnectReason> Completion => _completion.Task;

    /// <summary>進入暫停：把最後一張畫面變暗並蓋上「已暫停，非即時畫面」。</summary>
    public event Action<PauseReason, uint>? Paused;

    /// <summary>恢復可控制。</summary>
    public event Action<uint>? Resumed;

    public event Action<VideoFrame>? FrameReceived;

    /// <summary>Video / File 子連線狀態改變（connected = false 時顯示「畫面重新連線中」）。</summary>
    public event Action<ChannelRole, bool>? SubConnectionChanged;

    /// <summary>子連線在時限內重連失敗；可呼叫 <see cref="ReconnectAsync"/> 手動重試。</summary>
    public event Action<ChannelRole>? ReconnectFailed;

    // ---------------- 連線與登入 ----------------

    public static async Task<ControllerClient> ConnectAsync(ControllerClientOptions options, CancellationToken ct = default)
    {
        var (control, fingerprint) = await OpenTlsAsync(options, ChannelRole.Control, ct).ConfigureAwait(false);
        ControllerClient? client = null;
        try
        {
            // 首次連線須人工核對指紋，金鑰變了就拒絕 —— 都在送出帳密之前
            string address = options.Host;
            switch (options.KnownHosts.Check(address, fingerprint))
            {
                case HostTrust.KeyChanged:
                    throw new HostKeyChangedException(address, HostFingerprint.Format(fingerprint));
                case HostTrust.FirstSeen:
                    if (!await options.ConfirmFingerprint(new FingerprintPrompt(address, fingerprint), ct).ConfigureAwait(false))
                        throw new FingerprintNotConfirmedException();
                    options.KnownHosts.Approve(address, fingerprint);
                    break;
                case HostTrust.Trusted:
                    options.KnownHosts.RememberAddress(address, fingerprint);
                    break;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout);

            await control.SendAsync(new Hello(ProtocolConstants.ProtocolVersion, ChannelRole.Control, []),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var ack = MessageSerializer.Read<HelloAck>(await ReceiveRequiredAsync(control, timeout.Token).ConfigureAwait(false));
            ThrowIfHelloFailed(ack);

            await control.SendAsync(new LoginRequest(options.Username, options.Password), 1, timeout.Token).ConfigureAwait(false);
            var result = MessageSerializer.Read<LoginResult>(await ReceiveRequiredAsync(control, timeout.Token).ConfigureAwait(false));
            if (result.Status != LoginStatus.Ok)
                throw new LoginFailedException(result.Status, TimeSpan.FromMilliseconds(result.RetryAfterMs));

            client = new ControllerClient(options, fingerprint, control, result.InputGeneration) { _requestIds = 1 };
            client.Start();
            await client.OpenSubConnectionAsync(ChannelRole.Video, ct).ConfigureAwait(false);
            await client.OpenSubConnectionAsync(ChannelRole.File, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            else
                await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ThrowIfHelloFailed(HelloAck ack)
    {
        switch (ack.Status)
        {
            case HelloStatus.Ok: return;
            case HelloStatus.Busy: throw new HostBusyException();
            case HelloStatus.VersionMismatch: throw new ProtocolVersionException(ack.ProtocolVersion);
            default: throw new ConnectException("遠端拒絕連線。");
        }
    }

    private static async Task<(FramedConnection, byte[])> OpenTlsAsync(ControllerClientOptions options, ChannelRole role,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectTimeout);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new HostUnreachableException($"{options.Host}:{options.Port}", ex);
        }

        byte[]? fingerprint = null;
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // 自簽憑證：TLS 層只取出公鑰，信任與否在握手後以 SHA-256(SPKI) 釘選判斷
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null)
                        return false;
                    using var c2 = new X509Certificate2(cert);
                    fingerprint = HostFingerprint.Compute(c2);
                    return true;
                },
            }, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }

        if (fingerprint is null || ssl.SslProtocol != SslProtocols.Tls13)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new ConnectException("TLS 1.3 握手失敗。");
        }
        var conn = new FramedConnection(ssl,
            role == ChannelRole.Video ? ProtocolConstants.MaxVideoPayloadSize : FrameCodec.DefaultMaxPayloadSize);
        return (conn, fingerprint);
    }

    private static async Task<Frame> ReceiveRequiredAsync(FramedConnection conn, CancellationToken ct)
        => await conn.ReceiveAsync(ct).ConfigureAwait(false) ?? throw new ConnectException("遠端關閉了連線。");

    private void Start()
    {
        var ct = _cts.Token;
        _backgroundTasks = Task.WhenAll(
            Task.Run(() => ControlLoopAsync(ct)),
            Task.Run(() => HeartbeatLoopAsync(ct)),
            Task.Run(() => InputHeartbeatLoopAsync(ct)));
    }

    // ---------------- Control ----------------

    private async Task<Frame> RequestAsync<T>(T message, CancellationToken ct) where T : notnull
    {
        uint id = Interlocked.Increment(ref _requestIds);
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _control.SendAsync(message, id, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(_options.ConnectTimeout);
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        DisconnectReason reason = DisconnectReason.Normal;
        try
        {
            while (true)
            {
                var frame = await _control.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null)
                    break;
                var f = frame.Value;
                Volatile.Write(ref _controlLastHeard, _options.Time.GetTimestamp());

                if (f.RequestId != 0 && _pending.TryRemove(f.RequestId, out var tcs))
                {
                    tcs.TrySetResult(f);
                    continue;
                }

                switch (f.Type)
                {
                    case MessageType.HeartbeatAck:
                        break;
                    case MessageType.PauseControl:
                        OnPause(MessageSerializer.Read<PauseControl>(f));
                        break;
                    case MessageType.ResumeControl:
                        await OnResumeControlAsync(MessageSerializer.Read<ResumeControl>(f)).ConfigureAwait(false);
                        break;
                    case MessageType.Disconnect:
                        reason = MessageSerializer.Read<Disconnect>(f).Reason;
                        return;
                    case MessageType.Error:
                        MessageSerializer.Read<ErrorMessage>(f);
                        break;
                    default:
                        throw new InvalidDataException($"控制端不接受 {f.Type}");
                }
            }
        }
        catch (InvalidDataException)
        {
            reason = DisconnectReason.ProtocolError;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await CloseCoreAsync(reason, notifyHost: false).ConfigureAwait(false);
        }
    }

    private void OnPause(PauseControl p)
    {
        lock (_sync)
        {
            _generation = p.Generation;
            _canSendInput = false;
            _pendingResume = null;
            _pauseReason = p.Reason;
            // 被控端已釋放所有遠端按鍵；本地也清空尚未送出的輸入狀態
            _keysHeld.Clear();
            _buttonsHeld.Clear();
        }
        Paused?.Invoke(p.Reason, p.Generation);
    }

    private async Task OnResumeControlAsync(ResumeControl r)
    {
        bool ackNow;
        lock (_sync)
        {
            _pendingResume = r.Generation;
            ackNow = _lastKeyframeGeneration == r.Generation;
        }
        if (ackNow)
            await AckResumeAsync(r.Generation).ConfigureAwait(false);
    }

    /// <summary>已顯示該世代的 IDR：回覆新世代並清空尚未送出的輸入。</summary>
    private async Task AckResumeAsync(uint generation)
    {
        lock (_sync)
        {
            if (_pendingResume != generation)
                return;
            _pendingResume = null;
            _generation = generation;
            _keysHeld.Clear();
            _buttonsHeld.Clear();
        }
        await _control.SendAsync(new ResumeAck(generation), cancellationToken: _cts.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_generation != generation)
                return; // 回覆途中又被暫停
            _canSendInput = true;
        }
        Resumed?.Invoke(generation);
    }

    // ---------------- 子連線 ----------------

    private async Task OpenSubConnectionAsync(ChannelRole role, CancellationToken ct)
    {
        var grantFrame = await RequestAsync(new SessionTokenRequest(role), ct).ConfigureAwait(false);
        var grant = MessageSerializer.Read<SessionTokenGrant>(grantFrame);
        if (grant.Role != role)
            throw new InvalidDataException("Token 角色不符");

        var (conn, fingerprint) = await OpenTlsAsync(_options, role, ct).ConfigureAwait(false);
        try
        {
            // 子連線必須是同一主機 TLS 身分
            if (!fingerprint.AsSpan().SequenceEqual(_fingerprint))
                throw new HostKeyChangedException(_options.Host, HostFingerprint.Format(fingerprint));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.ConnectTimeout);
            await conn.SendAsync(new Hello(ProtocolConstants.ProtocolVersion, role, []), cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var ack = MessageSerializer.Read<HelloAck>(await ReceiveRequiredAsync(conn, timeout.Token).ConfigureAwait(false));
            ThrowIfHelloFailed(ack);
            if (ack.Nonce is null)
                throw new InvalidDataException("子連線缺少 nonce");

            var proof = SubConnectProof.Compute(grant.Token, ack.Nonce, role);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(grant.Token);
            await conn.SendAsync(new SubConnectAuth(role, proof), cancellationToken: timeout.Token).ConfigureAwait(false);
            var result = MessageSerializer.Read<SubConnectResult>(await ReceiveRequiredAsync(conn, timeout.Token).ConfigureAwait(false));
            if (!result.Success)
                throw new ConnectException($"{role} 子連線授權失敗。");
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_sync)
        {
            if (role == ChannelRole.Video)
            {
                _video = conn;
                _videoLastHeard = _options.Time.GetTimestamp();
            }
            else
            {
                _file = conn;
                _fileLastHeard = _options.Time.GetTimestamp();
            }
        }
        SubConnectionChanged?.Invoke(role, true);
        _ = Task.Run(() => role == ChannelRole.Video ? VideoLoopAsync(conn) : FileLoopAsync(conn));
    }

    private async Task VideoLoopAsync(FramedConnection conn)
    {
        try
        {
            while (true)
            {
                var frame = await conn.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Volatile.Write(ref _videoLastHeard, _options.Time.GetTimestamp());
                switch (frame.Value.Type)
                {
                    case MessageType.VideoFrame:
                        var vf = MessageSerializer.Read<VideoFrame>(frame.Value);
                        FrameReceived?.Invoke(vf);
                        if (vf.IsKeyframe)
                        {
                            bool ack;
                            lock (_sync)
                            {
                                _lastKeyframeGeneration = vf.Generation;
                                ack = _pendingResume == vf.Generation;
                            }
                            if (ack)
                                await AckResumeAsync(vf.Generation).ConfigureAwait(false);
                        }
                        break;
                    case MessageType.HeartbeatAck:
                        break;
                    case MessageType.Disconnect:
                        MessageSerializer.Read<Disconnect>(frame.Value);
                        return;
                    default:
                        throw new InvalidDataException($"Video 連線不接受 {frame.Value.Type}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await OnSubConnectionLostAsync(ChannelRole.Video, conn).ConfigureAwait(false);
        }
    }

    private async Task FileLoopAsync(FramedConnection conn)
    {
        try
        {
            while (true)
            {
                var frame = await conn.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Volatile.Write(ref _fileLastHeard, _options.Time.GetTimestamp());
                if (frame.Value.Type == MessageType.Disconnect)
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await OnSubConnectionLostAsync(ChannelRole.File, conn).ConfigureAwait(false);
        }
    }

    private async Task OnSubConnectionLostAsync(ChannelRole role, FramedConnection conn)
    {
        bool wasCurrent;
        lock (_sync)
        {
            if (role == ChannelRole.Video)
            {
                wasCurrent = ReferenceEquals(_video, conn);
                if (wasCurrent)
                {
                    _video = null;
                    // 「畫面重新連線中」：不送鍵鼠，等被控端的暫停與恢復程序
                    _canSendInput = false;
                    _keysHeld.Clear();
                    _buttonsHeld.Clear();
                }
            }
            else
            {
                wasCurrent = ReferenceEquals(_file, conn);
                if (wasCurrent)
                    _file = null;
            }
        }
        await conn.DisposeAsync().ConfigureAwait(false);
        if (!wasCurrent || IsClosed)
            return;

        SubConnectionChanged?.Invoke(role, false);
        await ReconnectWithBackoffAsync(role).ConfigureAwait(false);
    }

    private async Task ReconnectWithBackoffAsync(ChannelRole role)
    {
        var time = _options.Time;
        long start = time.GetTimestamp();
        var delay = _options.ReconnectInitialDelay;
        while (!IsClosed)
        {
            try
            {
                await Task.Delay(delay, time, _cts.Token).ConfigureAwait(false);
                await OpenSubConnectionAsync(role, _cts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ConnectException or IOException or InvalidDataException
                                           or OperationCanceledException or AuthenticationException or SocketException)
            {
            }

            if (time.GetElapsedTime(start) + delay > _options.ReconnectWindow)
            {
                ReconnectFailed?.Invoke(role);
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(8).Ticks));
        }
    }

    /// <summary>手動重試子連線（自動重連逾時後）。</summary>
    public Task ReconnectAsync(ChannelRole role, CancellationToken ct = default)
    {
        if (role == ChannelRole.Control)
            throw new ArgumentException("Control 中斷須重新登入", nameof(role));
        return OpenSubConnectionAsync(role, ct);
    }

    /// <summary>測試與診斷用：強制關閉某條子連線，觸發自動重連。</summary>
    public async Task DropSubConnectionAsync(ChannelRole role)
    {
        FramedConnection? conn;
        lock (_sync)
            conn = role == ChannelRole.Video ? _video : _file;
        if (conn is not null)
            await conn.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------- 心跳 ----------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var time = _options.Time;
                if (time.GetElapsedTime(Volatile.Read(ref _controlLastHeard)) > _options.HeartbeatTimeout)
                {
                    await CloseCoreAsync(DisconnectReason.HeartbeatTimeout, notifyHost: false).ConfigureAwait(false);
                    return;
                }
                var hb = new Heartbeat(time.GetUtcNow().ToUnixTimeMilliseconds());
                await TrySendAsync(_control, hb).ConfigureAwait(false);

                FramedConnection? video, file;
                lock (_sync)
                {
                    video = _video;
                    file = _file;
                }
                if (video is not null)
                {
                    if (time.GetElapsedTime(Volatile.Read(ref _videoLastHeard)) > _options.HeartbeatTimeout)
                        await video.DisposeAsync().ConfigureAwait(false);
                    else
                        await TrySendAsync(video, hb).ConfigureAwait(false);
                }
                if (file is not null)
                {
                    if (time.GetElapsedTime(Volatile.Read(ref _fileLastHeard)) > _options.HeartbeatTimeout)
                        await file.DisposeAsync().ConfigureAwait(false);
                    else
                        await TrySendAsync(file, hb).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>有按鍵按住時每 250 ms 送一次輸入心跳（第五節）。</summary>
    private async Task InputHeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.InputHeartbeatInterval, _options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                uint generation;
                lock (_sync)
                {
                    if (!_canSendInput || (_keysHeld.Count == 0 && _buttonsHeld.Count == 0))
                        continue;
                    generation = _generation;
                }
                await TrySendAsync(_control, new InputHeartbeat(generation)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------- 輸入 ----------------

    /// <summary>送出按鍵（掃描碼）。不可控制時丟棄並回傳 false。</summary>
    public Task<bool> SendKeyAsync(ushort scanCode, bool extended, bool down)
    {
        uint generation;
        lock (_sync)
        {
            if (!_canSendInput)
                return Task.FromResult(false);
            generation = _generation;
            if (down) _keysHeld.Add((scanCode, extended));
            else _keysHeld.Remove((scanCode, extended));
        }
        return SendInputAsync(new KeyEvent(generation, scanCode, extended, down));
    }

    public Task<bool> SendMouseMoveAsync(ushort x, ushort y)
    {
        uint generation;
        lock (_sync)
        {
            if (!_canSendInput)
                return Task.FromResult(false);
            generation = _generation;
        }
        return SendInputAsync(new MouseMoveEvent(generation, x, y));
    }

    public Task<bool> SendMouseButtonAsync(MouseButton button, bool down)
    {
        uint generation;
        lock (_sync)
        {
            if (!_canSendInput)
                return Task.FromResult(false);
            generation = _generation;
            if (down) _buttonsHeld.Add(button);
            else _buttonsHeld.Remove(button);
        }
        return SendInputAsync(new MouseButtonEvent(generation, button, down));
    }

    public Task<bool> SendMouseWheelAsync(short delta, bool horizontal)
    {
        uint generation;
        lock (_sync)
        {
            if (!_canSendInput)
                return Task.FromResult(false);
            generation = _generation;
        }
        return SendInputAsync(new MouseWheelEvent(generation, delta, horizontal));
    }

    /// <summary>「貼上 Unicode 文字」模式。</summary>
    public Task<bool> SendUnicodeTextAsync(string text)
    {
        uint generation;
        lock (_sync)
        {
            if (!_canSendInput)
                return Task.FromResult(false);
            generation = _generation;
        }
        return SendInputAsync(new UnicodeTextEvent(generation, text));
    }

    private async Task<bool> SendInputAsync<T>(T message) where T : notnull
    {
        try
        {
            await _control.SendAsync(message, cancellationToken: _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    // ---------------- 關閉 ----------------

    private bool IsClosed => Volatile.Read(ref _closed) != 0;

    public Task CloseAsync() => CloseCoreAsync(DisconnectReason.Normal, notifyHost: true);

    private async Task CloseCoreAsync(DisconnectReason reason, bool notifyHost)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        if (notifyHost)
            await TrySendAsync(_control, new Disconnect(DisconnectReason.Normal)).ConfigureAwait(false);

        lock (_sync)
            _canSendInput = false;
        _cts.Cancel();

        FramedConnection? video, file;
        lock (_sync)
        {
            video = _video;
            file = _file;
            _video = _file = null;
        }
        if (video is not null) await video.DisposeAsync().ConfigureAwait(false);
        if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _pending.Values)
            p.TrySetCanceled();
        _completion.TrySetResult(reason);
    }

    private static async Task TrySendAsync<T>(FramedConnection conn, T message) where T : notnull
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await conn.SendAsync(message, cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        try { await _backgroundTasks.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        _cts.Dispose();
    }
}
