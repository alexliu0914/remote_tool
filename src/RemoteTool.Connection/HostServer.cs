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
    public required CredentialRecord Credentials { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    /// <summary>0 表示由系統挑選（測試用）。</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>從 TCP 連入到完成登入（或子連線驗證）的時限。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Control 連線多久沒收到心跳就結束工作階段。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>登入失敗後的延遲，拖慢暴力猜測。</summary>
    public TimeSpan FailedLoginDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public LoginThrottle Throttle { get; init; } = new();
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
    /// <summary>對方送來、已檢查通道的封包。</summary>
    public ChannelReader<Frame> Incoming { get; } = incoming;
    public IPEndPoint Remote { get; } = remote;
    /// <summary>子連線或工作階段結束時取消。</summary>
    public CancellationToken Token { get; } = token;
}

/// <summary>
/// 被控端的 TLS 1.3 監聽與工作階段管理。
///
/// 每條連線：TLS 握手 → 互送 Hello（宣告通道）→
///   Control：LoginRequest → LoginResult → 每個子通道一個 SessionTokenGrant → 心跳迴圈
///   Video / File：SubConnectAuth(一次性 token) → SubConnectResult → 待用
///
/// 同時只允許一個工作階段；Control 中斷即撤銷整個工作階段並關閉子連線。
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private static readonly ChannelKind[] SubChannels = [ChannelKind.Video, ChannelKind.File];

    private readonly HostServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private HostSession? _session;
    private Task? _acceptLoop;
    private int _loginAttempts;

    public HostServer(HostServerOptions options)
    {
        _options = options;
        _listener = new TcpListener(options.BindAddress, options.Port);
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
        Log($"監聽 {_options.BindAddress}:{Port}");
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

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        using var _ = client;
        client.NoDelay = true;

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            handshakeCts.CancelAfter(_options.HandshakeTimeout);
            var hct = handshakeCts.Token;

            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await using var stream = new FrameStream(ssl);

            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.Certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, hct).ConfigureAwait(false);

            // 第一個封包必須是 Hello，且封包通道與宣告的通道一致。
            Frame first = await stream.ReadAsync(hct).ConfigureAwait(false)
                ?? throw new EndOfStreamException("握手前即關閉");
            var hello = MessageCodec.Decode<Hello>(first);
            if (first.Channel != hello.Channel || !Enum.IsDefined(hello.Channel))
                throw new ProtocolViolationException("Hello 通道不合法");

            if (hello.ProtocolVersion != ProtocolInfo.Version)
            {
                await stream.WriteAsync(hello.Channel,
                    new ErrorMessage($"協定版本不符：被控端 {ProtocolInfo.Version}，控制端 {hello.ProtocolVersion}"), hct)
                    .ConfigureAwait(false);
                Log($"{remote}：協定版本不符（{hello.ProtocolVersion}）");
                return;
            }

            await stream.WriteAsync(hello.Channel, new Hello(ProtocolInfo.Version, hello.Channel), hct).ConfigureAwait(false);

            if (hello.Channel == ChannelKind.Control)
                await RunControlAsync(stream, remote, hct).ConfigureAwait(false);
            else
                await RunSubChannelAsync(stream, hello.Channel, remote, hct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // 工作階段撤銷造成的取消已在各迴圈內處理，走到這裡代表握手 / 登入逾時。
            Log($"{remote}：握手或登入逾時，已關閉連線");
        }
        catch (OperationCanceledException) { /* 伺服器停止 */ }
        catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException
                                       or ProtocolViolationException or SocketException or ObjectDisposedException)
        {
            Log($"{remote}：連線結束（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private async Task RunControlAsync(FrameStream stream, IPEndPoint remote, CancellationToken handshakeToken)
    {
        var login = await stream.ReadMessageAsync<LoginRequest>(ChannelKind.Control, handshakeToken).ConfigureAwait(false);
        Interlocked.Increment(ref _loginAttempts);

        AuthError error = CheckLogin(login, remote.Address);
        if (error == AuthError.BadCredentials)
        {
            _options.Throttle.RecordFailure(remote.Address);
            await Task.Delay(_options.FailedLoginDelay, handshakeToken).ConfigureAwait(false);
        }

        HostSession? session = null;
        if (error == AuthError.None)
        {
            _options.Throttle.Reset(remote.Address);
            lock (_gate)
            {
                if (_session is not null) error = AuthError.Busy;
                else _session = session = new HostSession(stream, remote, _cts.Token);
            }
        }

        if (session is null)
        {
            await stream.WriteAsync(ChannelKind.Control, new LoginResult(false, error), handshakeToken).ConfigureAwait(false);
            Log($"{remote}：登入被拒（{error}）");
            return;
        }

        try
        {
            await stream.WriteAsync(ChannelKind.Control, new LoginResult(true, AuthError.None), handshakeToken).ConfigureAwait(false);
            foreach (var channel in SubChannels)
            {
                await stream.WriteAsync(ChannelKind.Control,
                    new SessionTokenGrant(channel, session.IssueToken(channel)), handshakeToken).ConfigureAwait(false);
            }
            Log($"{remote}：登入成功，工作階段開始");

            await RunHeartbeatLoopAsync(stream, session).ConfigureAwait(false);
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

    private AuthError CheckLogin(LoginRequest login, IPAddress address)
    {
        if (_options.Throttle.IsBlocked(address)) return AuthError.TooManyAttempts;
        if (login.Username is null || login.Password is null
            || login.Username.Length > ProtocolInfo.MaxUsernameLength
            || login.Password.Length > ProtocolInfo.MaxPasswordLength)
            return AuthError.BadCredentials;
        return PasswordHasher.Verify(_options.Credentials, login.Username, login.Password)
            ? AuthError.None
            : AuthError.BadCredentials;
    }

    private async Task RunHeartbeatLoopAsync(FrameStream stream, HostSession session)
    {
        var ct = session.Token;
        long lastSeen = Environment.TickCount64;

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            var timeoutMs = (long)_options.HeartbeatTimeout.TotalMilliseconds;
            while (!watchdogCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, timeoutMs / 4 + 1)), watchdogCts.Token).ConfigureAwait(false);
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
                if (f.Channel != ChannelKind.Control)
                    throw new ProtocolViolationException($"Control 連線收到 {f.Channel} 封包");
                if (!MessageCodec.IsKnownType(f.Type))
                    throw new ProtocolViolationException($"未知訊息類型 {(byte)f.Type}");

                Interlocked.Exchange(ref lastSeen, Environment.TickCount64);

                switch (f.Type)
                {
                    case MessageType.Heartbeat:
                        var hb = MessageCodec.Decode<Heartbeat>(f);
                        await stream.WriteAsync(ChannelKind.Control, hb, ct).ConfigureAwait(false);
                        break;
                    default:
                        // 其他 Control 訊息（鍵鼠、剪貼簿…）於後續里程碑實作，目前忽略。
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

    private async Task RunSubChannelAsync(FrameStream stream, ChannelKind channel, IPEndPoint remote, CancellationToken handshakeToken)
    {
        var auth = await stream.ReadMessageAsync<SubConnectAuth>(channel, handshakeToken).ConfigureAwait(false);

        HostSession? session;
        lock (_gate) session = _session;

        if (session is null || auth.Token is null || !session.TryConsumeToken(channel, auth.Token))
        {
            await stream.WriteAsync(channel, new SubConnectResult(false, AuthError.InvalidToken), handshakeToken).ConfigureAwait(false);
            Log($"{remote}：{channel} 子連線 token 無效");
            return;
        }

        using var subCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        session.AttachSubChannel(channel, subCts);
        try
        {
            await stream.WriteAsync(channel, new SubConnectResult(true, AuthError.None), handshakeToken).ConfigureAwait(false);
            // 補發一個新 token，供此通道日後重連。
            await session.Control.WriteAsync(ChannelKind.Control,
                new SessionTokenGrant(channel, session.IssueToken(channel)), session.Token).ConfigureAwait(false);
            Log($"{remote}：{channel} 子連線已建立");

            await RunSubChannelDataAsync(stream, channel, remote, subCts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (subCts.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 工作階段結束或同通道重連取代，屬正常結束。
        }
        finally
        {
            session.DetachSubChannel(channel, subCts);
            Log($"{remote}：{channel} 子連線結束");
        }
    }

    /// <summary>讀取迴圈與 <see cref="HostServerOptions.SubChannelHandler"/> 並行；任一方結束即關閉子連線。</summary>
    private async Task RunSubChannelDataAsync(FrameStream stream, ChannelKind channel, IPEndPoint remote, CancellationTokenSource subCts)
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
                    if (frame.Value.Channel != channel)
                        throw new ProtocolViolationException($"{channel} 連線收到 {frame.Value.Channel} 封包");
                    if (!MessageCodec.IsKnownType(frame.Value.Type))
                        throw new ProtocolViolationException($"未知訊息類型 {(byte)frame.Value.Type}");
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
            : Task.Run(() => handler(new SubChannelContext(channel, stream, incoming.Reader, remote, subCts.Token)));

        await Task.WhenAny(readTask, handlerTask).ConfigureAwait(false);
        try { subCts.Cancel(); } catch (ObjectDisposedException) { }

        try { await handlerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (handler is not null)
        {
            Log($"{remote}：{channel} 處理發生錯誤（{ex.GetType().Name}: {ex.Message}）");
        }

        await readTask.ConfigureAwait(false); // 讀取端的例外交給上層記錄
    }

    /// <summary>一個已登入的工作階段：Control 連線、一次性 token、子連線。</summary>
    private sealed class HostSession
    {
        private readonly CancellationTokenSource _cts;
        private readonly Dictionary<ChannelKind, byte[]> _tokens = new();
        private readonly Dictionary<ChannelKind, CancellationTokenSource> _subChannels = new();
        private readonly object _gate = new();

        public HostSession(FrameStream control, IPEndPoint remote, CancellationToken serverToken)
        {
            Control = control;
            Remote = remote;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        }

        public FrameStream Control { get; }
        public IPEndPoint Remote { get; }
        public CancellationToken Token => _cts.Token;

        public byte[] IssueToken(ChannelKind channel)
        {
            byte[] token = RandomNumberGenerator.GetBytes(ProtocolInfo.SessionTokenSize);
            lock (_gate) _tokens[channel] = token;
            return token;
        }

        /// <summary>比對並作廢 token（一次性）。</summary>
        public bool TryConsumeToken(ChannelKind channel, byte[] presented)
        {
            lock (_gate)
            {
                if (_cts.IsCancellationRequested) return false;
                if (!_tokens.TryGetValue(channel, out var expected)) return false;
                if (presented.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(presented, expected))
                    return false;
                _tokens.Remove(channel);
                return true;
            }
        }

        /// <summary>登記子連線；同通道已有舊連線時將其關閉（重連取代）。</summary>
        public void AttachSubChannel(ChannelKind channel, CancellationTokenSource cts)
        {
            CancellationTokenSource? old;
            lock (_gate)
            {
                _subChannels.TryGetValue(channel, out old);
                _subChannels[channel] = cts;
            }
            try { old?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void DetachSubChannel(ChannelKind channel, CancellationTokenSource cts)
        {
            lock (_gate)
            {
                if (_subChannels.TryGetValue(channel, out var current) && ReferenceEquals(current, cts))
                    _subChannels.Remove(channel);
            }
        }

        /// <summary>撤銷工作階段：作廢所有 token、關閉所有子連線與 Control。</summary>
        public void Revoke()
        {
            lock (_gate) _tokens.Clear();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
