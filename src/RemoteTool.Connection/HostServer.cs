using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using RemoteTool.Protocol;

namespace RemoteTool.Connection;

public sealed class HostServerOptions
{
    public required X509Certificate2 Certificate { get; init; }
    /// <summary>可登入的帳號（一或多組，PLAN §6）。</summary>
    public required CredentialSet Credentials { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    /// <summary>0 表示由系統挑選（測試用）。</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>從 TCP 連入到完成登入（或子連線驗證）的時限。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Control 連線多久沒收到心跳就結束工作階段（PLAN §7：6 秒）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(ProtocolInfo.HeartbeatTimeoutSeconds);
    /// <summary>子連線 token 有效期限（PLAN §7：30 秒）。</summary>
    public TimeSpan SessionTokenLifetime { get; init; } = TimeSpan.FromSeconds(ProtocolInfo.SessionTokenLifetimeSeconds);
    /// <summary>登入節流：每來源漸進延遲 + 全域節流（PLAN §6）。</summary>
    public LoginThrottle Throttle { get; init; } = new();
    /// <summary>尚未完成登入 / 子連線驗證的連線總數上限（同時連線上限，PLAN §6）。</summary>
    public int MaxPendingConnections { get; init; } = 32;
    /// <summary>同一來源尚未完成驗證的連線數上限。</summary>
    public int MaxPendingConnectionsPerAddress { get; init; } = 4;
    /// <summary>同時進行的 Argon2id 比對上限（每次約 19 MiB 記憶體）。</summary>
    public int MaxConcurrentPasswordChecks { get; init; } = 2;
    /// <summary>僅供測試：允許低於正式下限的 Argon2 參數。</summary>
    public bool AllowWeakCredentialParameters { get; init; }
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Video / File 子連線驗證成功後呼叫，負責該通道的資料（例如影像串流）。
    /// 回傳的 Task 結束或連線中斷時，子連線關閉。null 表示不處理（只接收並丟棄）。
    /// </summary>
    public Func<SubChannelContext, Task>? SubChannelHandler { get; init; }
}

/// <summary>交給 <see cref="HostServerOptions.SubChannelHandler"/> 的子連線。</summary>
public sealed class SubChannelContext(ChannelKind channel, FrameStream stream, ChannelReader<Frame> incoming,
    IPEndPoint remote, CancellationToken token)
{
    public ChannelKind Channel { get; } = channel;
    /// <summary>用來送出封包（可與讀取並行）。不要從這裡讀取，對方送來的封包在 <see cref="Incoming"/>。</summary>
    public FrameStream Stream { get; } = stream;
    /// <summary>對方送來、已通過 Protocol V1 檢查（類型、角色、方向、序號）的封包。</summary>
    public ChannelReader<Frame> Incoming { get; } = incoming;
    public IPEndPoint Remote { get; } = remote;
    /// <summary>子連線或工作階段結束時取消。</summary>
    public CancellationToken Token { get; } = token;
}

/// <summary>
/// 被控端的 TLS 1.3 監聽與工作階段管理（Protocol V1，見 docs/PROTOCOL_V1.md）。
///
/// 每條連線：TLS 握手 → 互送 Hello（版本、角色、能力）→
///   Control：LoginRequest → LoginResult → Video / File 各一枚 SessionTokenGrant → 心跳迴圈
///   Video / File：SubConnectChallenge(nonce) → SubConnectAuth(HMAC) → SubConnectResult → 資料
///
/// 同時只允許一個工作階段；Control 中斷即撤銷整個工作階段並關閉子連線。
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private static readonly ChannelKind[] SubChannels = [ChannelKind.Video, ChannelKind.File];
    private static readonly string[] HostCapabilities = [];

    private readonly HostServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _passwordChecks;
    private readonly Dictionary<IPAddress, int> _pendingPerAddress = new();
    private HostSession? _session;
    private Task? _acceptLoop;
    private int _loginAttempts;
    private int _pendingTotal;
    private bool _acceptPaused;

    /// <summary>測試用：暫時拒絕所有新連線（已建立的連線不受影響），模擬子連線無法重連。</summary>
    internal void SetAcceptPaused(bool paused) => Volatile.Write(ref _acceptPaused, paused);

    public HostServer(HostServerOptions options)
    {
        _options = options;
        _listener = new TcpListener(options.BindAddress, options.Port);
        _passwordChecks = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPasswordChecks));
        Fingerprint = Connection.Fingerprint.Compute(options.Certificate);
    }

    public string Fingerprint { get; }

    /// <summary>實際監聽的埠。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>目前是否有已登入的工作階段。</summary>
    public bool HasActiveSession { get { lock (_gate) return _session is not null; } }

    /// <summary>收到的 LoginRequest 次數（測試用：確認拒絕指紋時帳密沒送出）。</summary>
    public int LoginAttempts => Volatile.Read(ref _loginAttempts);

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
        Log($"監聽 {_options.BindAddress}:{Port}（Protocol V{ProtocolInfo.Version}）");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* 已停止 */ }
        }
        HostSession? s;
        lock (_gate) s = _session;
        s?.Revoke();
        _cts.Dispose();
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    // ------------------------------------------------------------------ 接受連線與同時連線上限

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Log($"接受連線失敗：{ex.Message}");
                continue;
            }

            if (Volatile.Read(ref _acceptPaused))
            {
                client.Dispose();
                continue;
            }
            var address = Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
            if (!TryAddPending(address))
            {
                Log($"{address}：未驗證連線過多，拒絕");
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(client, address));
        }
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private bool TryAddPending(IPAddress address)
    {
        lock (_gate)
        {
            _pendingPerAddress.TryGetValue(address, out int n);
            if (_pendingTotal >= _options.MaxPendingConnections || n >= _options.MaxPendingConnectionsPerAddress)
                return false;
            _pendingPerAddress[address] = n + 1;
            _pendingTotal++;
            return true;
        }
    }

    private void ReleasePending(IPAddress address)
    {
        lock (_gate)
        {
            _pendingTotal--;
            if (_pendingPerAddress.TryGetValue(address, out int n))
            {
                if (n <= 1) _pendingPerAddress.Remove(address);
                else _pendingPerAddress[address] = n - 1;
            }
        }
    }

    /// <summary>一條連線佔用的「未驗證連線」名額；驗證成功或連線結束時釋放（只釋放一次）。</summary>
    private sealed class PendingSlot(HostServer server, IPAddress address)
    {
        private int _released;
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) server.ReleasePending(address);
        }
    }

    // ------------------------------------------------------------------ 每條連線

    private async Task HandleConnectionAsync(TcpClient client, IPAddress address)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        using var _ = client;
        client.NoDelay = true;
        var pending = new PendingSlot(this, address);
        FrameStream? stream = null;

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);
            var hct = handshakeCts.Token;

            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            stream = new FrameStream(ssl, Endpoint.Host);

            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.Certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, hct).ConfigureAwait(false);

            // 第一個封包必須是 Hello（FrameStream 已限制握手前只能收 Hello / Error）。
            Frame first = await stream.ReadAsync(hct).ConfigureAwait(false)
                ?? throw new EndOfStreamException("握手前即關閉");
            if (first.Type != MessageType.Hello)
                throw new ProtocolException(ErrorCode.ProtocolViolation, "第一個封包必須是 Hello");
            var hello = MessageCodec.Decode<Hello>(first);

            if (hello.ProtocolVersion != ProtocolInfo.Version)
            {
                await stream.WriteAsync(new ErrorMessage(ErrorCode.VersionMismatch,
                    $"協定版本不相容：被控端 V{ProtocolInfo.Version}，控制端 V{hello.ProtocolVersion}，請更新兩端程式"),
                    cancellationToken: hct).ConfigureAwait(false);
                Log($"{remote}：協定版本不相容（V{hello.ProtocolVersion}）");
                return;
            }
            ValidateHello(hello);

            stream.Role = hello.Role;
            await stream.WriteAsync(new Hello(ProtocolInfo.Version, hello.Role, HostCapabilities), cancellationToken: hct)
                .ConfigureAwait(false);

            if (hello.Role == ChannelKind.Control)
                await RunControlAsync(stream, remote, pending, hct).ConfigureAwait(false);
            else
                await RunSubChannelAsync(stream, hello.Role, remote, pending, hct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // 工作階段撤銷造成的取消已在各迴圈內處理，走到這裡代表握手 / 登入逾時。
            Log($"{remote}：握手或登入逾時，已關閉連線");
        }
        catch (OperationCanceledException) { /* 伺服器停止 */ }
        catch (ProtocolException ex)
        {
            Log($"{remote}：協定錯誤（{ex.Code}: {ex.Message}），關閉連線");
            await TrySendErrorAsync(stream, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            Log($"{remote}：內容格式錯誤（{ex.Message}），關閉連線");
            await TrySendErrorAsync(stream, ErrorCode.ProtocolViolation, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or RemoteErrorException
                                       or SocketException or ObjectDisposedException)
        {
            Log($"{remote}：連線結束（{ex.GetType().Name}: {ex.Message}）");
        }
        finally
        {
            pending.Release();
            if (stream is not null)
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* 已關閉 */ }
            }
        }
    }

    private static void ValidateHello(Hello hello)
    {
        if (!Enum.IsDefined(hello.Role))
            throw new ProtocolException(ErrorCode.ProtocolViolation, $"不合法的連線角色 {(byte)hello.Role}");
        var caps = hello.Capabilities;
        if (caps is not null && (caps.Length > ProtocolInfo.MaxCapabilities
                                 || caps.Any(c => c is null || c.Length > ProtocolInfo.MaxCapabilityLength)))
            throw new ProtocolException(ErrorCode.ProtocolViolation, "Capabilities 超出限制");
    }

    /// <summary>盡力通知對方錯誤原因後關閉；失敗不影響關閉。</summary>
    private static async Task TrySendErrorAsync(FrameStream? stream, ErrorCode code, string message)
    {
        if (stream is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await stream.WriteAsync(new ErrorMessage(code, message), cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch { /* 連線可能已斷 */ }
    }

    // ------------------------------------------------------------------ Control

    private async Task RunControlAsync(FrameStream stream, IPEndPoint remote, PendingSlot pending, CancellationToken handshakeToken)
    {
        var (loginFrame, login) = await stream.ReadMessageAsync<LoginRequest>(handshakeToken).ConfigureAwait(false);
        Interlocked.Increment(ref _loginAttempts);

        var (error, retryAfter) = await CheckLoginAsync(login, remote.Address, handshakeToken).ConfigureAwait(false);
        if (error == AuthError.BadCredentials)
            _options.Throttle.RecordFailure(remote.Address);

        HostSession? session = null;
        if (error == AuthError.None)
        {
            _options.Throttle.RecordSuccess(remote.Address);
            lock (_gate)
            {
                if (_session is not null) error = AuthError.Busy;
                else _session = session = new HostSession(stream, remote, login.Username, _cts.Token);
            }
        }

        if (session is null)
        {
            int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            await stream.WriteAsync(new LoginResult(false, error, seconds), loginFrame.RequestId, handshakeToken).ConfigureAwait(false);
            // 只記來源與原因，不記錄輸入的帳號或密碼（PLAN §6）
            Log($"{remote}：登入被拒（{error}{(seconds > 0 ? $"，{seconds} 秒後可再試" : "")}）");
            return;
        }

        pending.Release();
        try
        {
            await stream.WriteAsync(new LoginResult(true, AuthError.None, 0), loginFrame.RequestId, handshakeToken).ConfigureAwait(false);
            foreach (var role in SubChannels)
                await GrantTokenAsync(session, role, 0, handshakeToken).ConfigureAwait(false);
            // 稽核：成功時記錄帳號（PLAN §6）；失敗時不記錄輸入的帳號
            Log($"{remote}：帳號 {session.Username} 登入成功，工作階段開始（Windows 工作階段 {session.WindowsSessionId}）");

            await RunControlLoopAsync(stream, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 心跳逾時或被撤銷，屬正常結束。
        }
        finally
        {
            session.Revoke();
            lock (_gate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
            }
            Log($"{remote}：工作階段結束");
        }
    }

    private Task GrantTokenAsync(HostSession session, ChannelKind role, uint requestId, CancellationToken ct)
    {
        byte[] token = session.IssueToken(role);
        return session.Control.WriteAsync(
            new SessionTokenGrant(role, token, (int)_options.SessionTokenLifetime.TotalSeconds), requestId, ct).AsTask();
    }

    private async Task<(AuthError Error, TimeSpan RetryAfter)> CheckLoginAsync(LoginRequest login, IPAddress address, CancellationToken ct)
    {
        if (!_options.Throttle.TryBegin(address, out var retryAfter))
            return (AuthError.TooManyAttempts, retryAfter);
        if (login.Username is null || login.Password is null
            || login.Username.Length > ProtocolInfo.MaxUsernameLength
            || login.Password.Length > ProtocolInfo.MaxPasswordLength)
            return (AuthError.BadCredentials, TimeSpan.Zero);

        // Argon2id 很吃記憶體，限制同時比對數量；等不到就請對方稍後再試。
        if (!await _passwordChecks.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            return (AuthError.TooManyAttempts, TimeSpan.FromSeconds(1));
        try
        {
            bool ok = await Task.Run(() => _options.Credentials.Verify(login.Username, login.Password,
                _options.AllowWeakCredentialParameters), ct).ConfigureAwait(false);
            return (ok ? AuthError.None : AuthError.BadCredentials, TimeSpan.Zero);
        }
        finally
        {
            _passwordChecks.Release();
        }
    }

    private async Task RunControlLoopAsync(FrameStream stream, HostSession session)
    {
        var ct = session.Token;
        long lastSeen = Environment.TickCount64;

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            var timeoutMs = (long)_options.HeartbeatTimeout.TotalMilliseconds;
            while (!watchdogCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, timeoutMs / 4 + 1)), watchdogCts.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > timeoutMs)
                {
                    Log($"{session.Remote}：心跳逾時");
                    session.Revoke();
                    return;
                }
            }
        });

        try
        {
            while (true)
            {
                Frame? frame = await stream.ReadAsync(ct).ConfigureAwait(false);
                if (frame is null) return; // 控制端正常關閉
                var f = frame.Value;
                Interlocked.Exchange(ref lastSeen, Environment.TickCount64);

                switch (f.Type)
                {
                    case MessageType.Heartbeat:
                        var hb = MessageCodec.Decode<Heartbeat>(f);
                        await stream.WriteAsync(hb, f.RequestId, ct).ConfigureAwait(false);
                        break;

                    case MessageType.SessionTokenRequest:
                        var req = MessageCodec.Decode<SessionTokenRequest>(f);
                        if (req.Role is not (ChannelKind.Video or ChannelKind.File))
                            throw new ProtocolException(ErrorCode.ProtocolViolation, $"不能申請 {req.Role} token");
                        await GrantTokenAsync(session, req.Role, f.RequestId, ct).ConfigureAwait(false);
                        Log($"{session.Remote}：重新發出 {req.Role} token");
                        break;

                    case MessageType.Error:
                        var err = MessageCodec.Decode<ErrorMessage>(f);
                        Log($"{session.Remote}：控制端回報錯誤（{err.Code}: {err.Message}）");
                        return;

                    default:
                        // V1 已定義、但尚未實作的 Control 訊息（鍵鼠、剪貼簿…），目前忽略。
                        break;
                }
            }
        }
        finally
        {
            watchdogCts.Cancel();
            try { await watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    // ------------------------------------------------------------------ Video / File 子連線

    private async Task RunSubChannelAsync(FrameStream stream, ChannelKind role, IPEndPoint remote, PendingSlot pending,
        CancellationToken handshakeToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(ProtocolInfo.SubConnectNonceSize);
        await stream.WriteAsync(new SubConnectChallenge(nonce), cancellationToken: handshakeToken).ConfigureAwait(false);
        var (_, auth) = await stream.ReadMessageAsync<SubConnectAuth>(handshakeToken).ConfigureAwait(false);

        HostSession? session;
        lock (_gate) session = _session;

        var result = session is null
            ? TokenCheck.NoSession
            : session.TryConsumeToken(role, nonce, auth.Mac, _options.SessionTokenLifetime);

        if (result != TokenCheck.Ok)
        {
            await stream.WriteAsync(new SubConnectResult(false, AuthError.InvalidToken), cancellationToken: handshakeToken)
                .ConfigureAwait(false);
            // 重複使用、過期、跨角色一律拒絕並記錄來源 IP（PLAN §7 防重放）
            Log($"{remote}：{role} 子連線驗證失敗（{result}）");
            return;
        }

        pending.Release();
        using var subCts = CancellationTokenSource.CreateLinkedTokenSource(session!.Token);
        session.AttachSubChannel(role, subCts);
        try
        {
            await stream.WriteAsync(new SubConnectResult(true, AuthError.None), cancellationToken: handshakeToken).ConfigureAwait(false);
            Log($"{remote}：{role} 子連線已建立");
            await RunSubChannelDataAsync(stream, role, remote, subCts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (subCts.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 工作階段結束或同角色重連取代，屬正常結束。
        }
        finally
        {
            session.DetachSubChannel(role, subCts);
            Log($"{remote}：{role} 子連線結束");
        }
    }

    /// <summary>讀取迴圈與 <see cref="HostServerOptions.SubChannelHandler"/> 並行；任一方結束即關閉子連線。</summary>
    private async Task RunSubChannelDataAsync(FrameStream stream, ChannelKind role, IPEndPoint remote, CancellationTokenSource subCts)
    {
        var handler = _options.SubChannelHandler;
        var incoming = Channel.CreateBounded<Frame>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    Frame? frame = await stream.ReadAsync(subCts.Token).ConfigureAwait(false);
                    if (frame is null) return;
                    if (frame.Value.Type == MessageType.Error)
                    {
                        var err = MessageCodec.Decode<ErrorMessage>(frame.Value);
                        Log($"{remote}：{role} 控制端回報錯誤（{err.Code}: {err.Message}）");
                        return;
                    }
                    if (handler is not null)
                        await incoming.Writer.WriteAsync(frame.Value, subCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                incoming.Writer.TryComplete();
            }
        }

        var readTask = ReadLoopAsync();
        var handlerTask = handler is null
            ? Task.Delay(Timeout.Infinite, subCts.Token)
            : Task.Run(() => handler(new SubChannelContext(role, stream, incoming.Reader, remote, subCts.Token)));

        await Task.WhenAny(readTask, handlerTask).ConfigureAwait(false);
        try { subCts.Cancel(); } catch (ObjectDisposedException) { }

        try { await handlerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (handler is not null)
        {
            Log($"{remote}：{role} 處理發生錯誤（{ex.GetType().Name}: {ex.Message}）");
        }

        await readTask.ConfigureAwait(false); // 讀取端的例外交給上層記錄
    }

    // ------------------------------------------------------------------ 工作階段

    private enum TokenCheck { Ok, NoSession, NoToken, Expired, BadProof }

    /// <summary>
    /// 一個已登入的工作階段：Control 連線、一次性 token、子連線。
    /// token 存在工作階段物件中，因此天然綁定此帳號、此 Control 連線與此 Windows 工作階段（PLAN §7）。
    /// </summary>
    private sealed class HostSession
    {
        private readonly CancellationTokenSource _cts;
        private readonly Dictionary<ChannelKind, (byte[] Token, long IssuedAt)> _tokens = new();
        private readonly Dictionary<ChannelKind, CancellationTokenSource> _subChannels = new();
        private readonly object _gate = new();

        public HostSession(FrameStream control, IPEndPoint remote, string username, CancellationToken serverToken)
        {
            Control = control;
            Remote = remote;
            Username = username;
            WindowsSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public FrameStream Control { get; }
        public IPEndPoint Remote { get; }
        public string Username { get; }
        public int WindowsSessionId { get; }
        public CancellationToken Token => _cts.Token;

        /// <summary>發出新 token（同角色舊的未用 token 立即作廢）。</summary>
        public byte[] IssueToken(ChannelKind role)
        {
            byte[] token = RandomNumberGenerator.GetBytes(ProtocolInfo.SessionTokenSize);
            lock (_gate) _tokens[role] = (token, Environment.TickCount64);
            return token;
        }

        /// <summary>以 nonce + HMAC 驗證並作廢 token（一次性、限期、限角色）。</summary>
        public TokenCheck TryConsumeToken(ChannelKind role, byte[] nonce, byte[]? mac, TimeSpan lifetime)
        {
            lock (_gate)
            {
                if (_cts.IsCancellationRequested) return TokenCheck.NoSession;
                if (!_tokens.TryGetValue(role, out var entry)) return TokenCheck.NoToken;
                if (Environment.TickCount64 - entry.IssuedAt > lifetime.TotalMilliseconds)
                {
                    _tokens.Remove(role);
                    return TokenCheck.Expired;
                }
                byte[] expected = SubConnectProof.Compute(entry.Token, nonce, role);
                if (mac is null || mac.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(mac, expected))
                    return TokenCheck.BadProof;
                _tokens.Remove(role);
                return TokenCheck.Ok;
            }
        }

        /// <summary>登記子連線；同角色已有舊連線時將其關閉（重連取代）。</summary>
        public void AttachSubChannel(ChannelKind role, CancellationTokenSource cts)
        {
            CancellationTokenSource? old;
            lock (_gate)
            {
                _subChannels.TryGetValue(role, out old);
                _subChannels[role] = cts;
            }
            try { old?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void DetachSubChannel(ChannelKind role, CancellationTokenSource cts)
        {
            lock (_gate)
            {
                if (_subChannels.TryGetValue(role, out var current) && ReferenceEquals(current, cts))
                    _subChannels.Remove(role);
            }
        }

        /// <summary>撤銷工作階段：作廢所有 token、關閉所有子連線與 Control。</summary>
        public void Revoke()
        {
            lock (_gate)
            {
                foreach (var (token, _) in _tokens.Values) CryptographicOperations.ZeroMemory(token);
                _tokens.Clear();
            }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
