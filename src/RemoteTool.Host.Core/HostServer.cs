using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Host.Core;

public sealed class HostServerOptions
{
    public required IPEndPoint Endpoint { get; init; }
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Host.exe 所在的 Windows 工作階段 ID（Token 綁定用）。</summary>
    public int WindowsSessionId { get; init; }

    /// <summary>尚未登入的同時連線上限。</summary>
    public int MaxPendingConnections { get; init; } = 8;

    public TimeSpan HandshakeTimeout { get; init; } = ProtocolConstants.HandshakeTimeout;
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatTimeout { get; init; } = ProtocolConstants.HeartbeatTimeout;
    public TimeSpan EvaluateInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ResumeStableDuration { get; init; } = ProtocolConstants.ResumeStableDuration;

    /// <summary>單一 Control 連線上的登入嘗試上限（另有每來源漸進延遲）。</summary>
    public int MaxLoginAttemptsPerConnection { get; init; } = 5;
}

/// <summary>
/// 被控端 TLS 伺服器（第三、七節）：同一埠上接受 Control / Video / File 三種角色的連線，
/// 同一時間只允許一名控制者。
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly HostServerOptions _options;
    private readonly AccountStore _accounts;
    private readonly LoginThrottle _throttle;
    private readonly SessionTokenService _tokens;
    private readonly IGuardChannel _guard;
    private readonly IDesktopStateSource _desktop;
    private readonly IVideoSource _video;
    private readonly IHostLog _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _pending;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenCts;
    private Task? _acceptTask;
    private HostSession? _session;
    private long _connectionIds;
    private uint _generationFloor; // 已用過的最大世代；新工作階段從其後開始，HostGuard 只接受遞增的世代

    public HostServer(HostServerOptions options, AccountStore accounts, LoginThrottle throttle,
        SessionTokenService tokens, IGuardChannel guard, IDesktopStateSource desktop, IVideoSource video,
        IHostLog? log = null, TimeProvider? time = null)
    {
        _options = options;
        _accounts = accounts;
        _throttle = throttle;
        _tokens = tokens;
        _guard = guard;
        _desktop = desktop;
        _video = video;
        _log = log ?? NullHostLog.Instance;
        _time = time ?? TimeProvider.System;
        _pending = new SemaphoreSlim(options.MaxPendingConnections, options.MaxPendingConnections);

        _guard.Faulted += OnGuardFaulted;
        _desktop.SessionEnding += OnSessionEnding;
        _desktop.ConsoleActiveChanged += OnConsoleActiveChanged;
    }

    /// <summary>目前的控制工作階段（系統列「被操控提示」用）。</summary>
    public HostSession? CurrentSession
    {
        get { lock (_lock) return _session; }
    }

    /// <summary>實際監聽的位址；未監聽時為 null。</summary>
    public IPEndPoint? LocalEndpoint
    {
        get { lock (_lock) return _listener?.LocalEndpoint as IPEndPoint; }
    }

    public bool IsListening
    {
        get { lock (_lock) return _listener is not null; }
    }

    public event Action<HostSession>? SessionStarted;
    public event Action<HostSession, DisconnectReason>? SessionEnded;

    /// <summary>開始監聽（若尚未監聽）。</summary>
    public void StartListening()
    {
        lock (_lock)
        {
            if (_listener is not null)
                return;
            var listener = new TcpListener(_options.Endpoint);
            if (OperatingSystem.IsWindows())
                listener.ExclusiveAddressUse = true; // 防止其他程序共用同一埠攔截連線
            if (_options.Endpoint.Address.Equals(IPAddress.IPv6Any))
                listener.Server.DualMode = true;     // 同時接受 IPv4
            try
            {
                listener.Start();
            }
            catch
            {
                listener.Dispose();
                throw;
            }
            _listener = listener;
            _listenCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _acceptTask = AcceptLoopAsync(listener, _listenCts.Token);
        }
        _log.Info($"開始監聽 {LocalEndpoint}");
    }

    /// <summary>
    /// 開始監聽；埠暫時被占用（例如切換使用者時另一使用者的 Host 尚未讓出）時每 500 ms 重試，直到逾時。
    /// </summary>
    public async Task StartListeningWithRetryAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        long start = _time.GetTimestamp();
        while (true)
        {
            try
            {
                StartListening();
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                if (_time.GetElapsedTime(start) > timeout)
                    throw;
                _log.Warn($"埠 {_options.Endpoint.Port} 被占用，稍後重試");
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>停止監聽並讓出埠（切換使用者時）。不影響既有連線；呼叫端另行中止工作階段。</summary>
    public async Task StopListeningAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? accept;
        lock (_lock)
        {
            listener = _listener;
            cts = _listenCts;
            accept = _acceptTask;
            _listener = null;
            _listenCts = null;
            _acceptTask = null;
        }
        if (listener is null)
            return;
        cts!.Cancel();
        listener.Stop();
        try { await accept!.ConfigureAwait(false); } catch (OperationCanceledException) { }
        cts.Dispose();
        _log.Info("已停止監聽並讓出埠");
    }

    /// <summary>中止目前的控制工作階段（例如本機按「中斷」）。</summary>
    public Task DisconnectAsync(DisconnectReason reason)
        => CurrentSession?.EndAsync(reason) ?? Task.CompletedTask;

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested)
                    break;
                _log.Warn($"接受連線失敗：{ex.SocketErrorCode}");
                continue;
            }

            if (!_pending.Wait(0))
            {
                _log.Warn($"未登入連線過多，拒絕 {client.Client.RemoteEndPoint}");
                client.Dispose();
                continue;
            }
            _ = HandleConnectionAsync(client);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        bool pendingHeld = true;
        void ReleasePending()
        {
            if (pendingHeld)
            {
                pendingHeld = false;
                _pending.Release();
            }
        }

        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        if (remote.Address.IsIPv4MappedToIPv6)
            remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
        FramedConnection? conn = null;
        try
        {
            client.NoDelay = true;
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            conn = new FramedConnection(ssl);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);

            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.Certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, handshakeCts.Token).ConfigureAwait(false);

            var first = await conn.ReceiveAsync(handshakeCts.Token).ConfigureAwait(false);
            if (first is null)
                return;
            var hello = MessageSerializer.Read<Hello>(first.Value);

            if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                await conn.SendAsync(new HelloAck(ProtocolConstants.ProtocolVersion, HelloStatus.VersionMismatch, null),
                    cancellationToken: handshakeCts.Token).ConfigureAwait(false);
                return;
            }

            if (hello.Role == ChannelRole.Control)
            {
                var session = await LoginAsync(conn, remote).ConfigureAwait(false);
                if (session is null)
                    return;
                ReleasePending();
                await session.RunControlAsync(conn).ConfigureAwait(false);
            }
            else
            {
                var session = await AuthenticateSubConnectionAsync(conn, hello.Role, remote, handshakeCts.Token)
                    .ConfigureAwait(false);
                if (session is null)
                    return;
                ReleasePending();
                if (hello.Role == ChannelRole.Video)
                {
                    conn.MaxPayloadSize = ProtocolConstants.MaxVideoPayloadSize;
                    await session.RunVideoAsync(conn).ConfigureAwait(false);
                }
                else
                {
                    await session.RunFileAsync(conn).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException
                                       or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            _log.Info($"連線 {remote} 結束：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            ReleasePending();
            if (conn is not null)
                await conn.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }

    private async Task<HostSession?> LoginAsync(FramedConnection conn, IPEndPoint remote)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        cts.CancelAfter(_options.LoginTimeout);
        var ct = cts.Token;

        if (CurrentSession is not null)
        {
            await conn.SendAsync(new HelloAck(ProtocolConstants.ProtocolVersion, HelloStatus.Busy, null), cancellationToken: ct)
                .ConfigureAwait(false);
            return null;
        }
        await conn.SendAsync(new HelloAck(ProtocolConstants.ProtocolVersion, HelloStatus.Ok, null), cancellationToken: ct)
            .ConfigureAwait(false);

        for (int attempt = 0; attempt < _options.MaxLoginAttemptsPerConnection; attempt++)
        {
            var frame = await conn.ReceiveAsync(ct).ConfigureAwait(false);
            if (frame is null)
                return null;
            var request = MessageSerializer.Read<LoginRequest>(frame.Value);

            var wait = _throttle.TryBeginAttempt(remote.Address);
            if (wait > TimeSpan.Zero)
            {
                await conn.SendAsync(new LoginResult(LoginStatus.Throttled, (int)Math.Ceiling(wait.TotalMilliseconds), 0),
                    frame.Value.RequestId, ct).ConfigureAwait(false);
                continue;
            }

            // Argon2 很耗 CPU，不佔用 I/O 執行緒
            var account = await Task.Run(() => _accounts.Verify(request.Username, request.Password), ct)
                .ConfigureAwait(false);
            if (account is null)
            {
                _throttle.RecordFailure(remote.Address);
                // 登入失敗只記來源 IP 與時間，不記帳號與密碼
                _log.Audit($"登入失敗，來源 {remote.Address}");
                await conn.SendAsync(new LoginResult(LoginStatus.InvalidCredentials, 0, 0), frame.Value.RequestId, ct)
                    .ConfigureAwait(false);
                continue;
            }
            _throttle.RecordSuccess(remote.Address);

            if (!_guard.IsConnected)
            {
                _log.Warn("HostGuard 未連線，拒絕遠端控制");
                await conn.SendAsync(new LoginResult(LoginStatus.NotAllowed, 0, 0), frame.Value.RequestId, ct)
                    .ConfigureAwait(false);
                return null;
            }

            HostSession? session = null;
            lock (_lock)
            {
                if (_session is null)
                {
                    session = new HostSession(this, account, remote.Address, Interlocked.Increment(ref _connectionIds),
                        _generationFloor + 1, _options, _tokens, _guard, _desktop, _video, _log, _time);
                    _session = session;
                }
            }
            if (session is null)
            {
                await conn.SendAsync(new LoginResult(LoginStatus.Busy, 0, 0), frame.Value.RequestId, ct).ConfigureAwait(false);
                return null;
            }

            await conn.SendAsync(new LoginResult(LoginStatus.Ok, 0, session.Generation), frame.Value.RequestId, ct)
                .ConfigureAwait(false);
            _log.Audit($"登入成功，帳號 {account}，來源 {remote.Address}");
            SessionStarted?.Invoke(session);
            return session;
        }
        return null;
    }

    private async Task<HostSession?> AuthenticateSubConnectionAsync(FramedConnection conn, ChannelRole role,
        IPEndPoint remote, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(ProtocolConstants.NonceSize);
        await conn.SendAsync(new HelloAck(ProtocolConstants.ProtocolVersion, HelloStatus.Ok, nonce), cancellationToken: ct)
            .ConfigureAwait(false);

        var frame = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        if (frame is null)
            return null;
        var auth = MessageSerializer.Read<SubConnectAuth>(frame.Value);

        TokenBinding? binding = auth.Role == role ? _tokens.Redeem(role, nonce, auth.Proof, remote.Address) : null;
        var session = CurrentSession;
        bool ok = binding is not null && session is not null && !session.IsEnded
                  && binding.ControlConnectionId == session.ControlConnectionId
                  && binding.WindowsSessionId == _options.WindowsSessionId;

        await conn.SendAsync(new SubConnectResult(ok), frame.Value.RequestId, ct).ConfigureAwait(false);
        if (!ok)
        {
            _log.Audit($"拒絕 {role} 子連線，來源 {remote.Address}");
            return null;
        }
        _log.Info($"{role} 子連線已建立，來源 {remote.Address}");
        return session;
    }

    internal void OnSessionEnded(HostSession session, DisconnectReason reason)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_session, session))
                _session = null;
            _generationFloor = Math.Max(_generationFloor, session.FinalGeneration);
        }
        _log.Audit($"工作階段結束，帳號 {session.Account}，來源 {session.RemoteAddress}，原因 {reason}");
        SessionEnded?.Invoke(session, reason);
    }

    private void OnGuardFaulted()
    {
        _log.Warn("HostGuard IPC 中斷：立即停止遠端控制");
        _ = DisconnectAsync(DisconnectReason.GuardUnavailable);
    }

    private void OnSessionEnding(DisconnectReason reason) => _ = DisconnectAsync(reason);

    private void OnConsoleActiveChanged(bool active)
    {
        if (active)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await StartListeningWithRetryAsync(TimeSpan.FromSeconds(60), _disposeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    _log.Warn($"無法重新監聽：{ex.Message}");
                }
            });
            return;
        }
        _ = Task.Run(async () =>
        {
            await DisconnectAsync(DisconnectReason.UserSwitched).ConfigureAwait(false);
            await StopListeningAsync().ConfigureAwait(false);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _guard.Faulted -= OnGuardFaulted;
        _desktop.SessionEnding -= OnSessionEnding;
        _desktop.ConsoleActiveChanged -= OnConsoleActiveChanged;
        await DisconnectAsync(DisconnectReason.HostShuttingDown).ConfigureAwait(false);
        await StopListeningAsync().ConfigureAwait(false);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
