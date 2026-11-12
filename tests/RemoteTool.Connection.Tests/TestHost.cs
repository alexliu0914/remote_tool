using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Connection;
using RemoteTool.Protocol;

namespace RemoteTool.Connection.Tests;

/// <summary>在 127.0.0.1 隨機埠啟動一個真實 TLS 1.3 的 HostServer。</summary>
internal sealed class TestHost : IAsyncDisposable
{
    public const string User = "alex";
    public const string Pass = "correct horse";

    // 證書只產生一次，加快測試。
    private static readonly X509Certificate2 SharedCert = HostCertificate.CreateEphemeral();

    public HostServer Server { get; }
    public List<string> Logs { get; } = new();

    /// <summary>測試用的弱 Argon2 參數，讓測試快速執行（正式參數另有測試）。</summary>
    public static readonly Argon2Parameters WeakArgon2 = new(MemoryKiB: 256, Iterations: 1, Parallelism: 1);

    public TestHost(LoginThrottle? throttle = null, TimeSpan? heartbeatTimeout = null, TimeSpan? handshakeTimeout = null,
        int maxPendingPerAddress = 4, TimeSpan? tokenLifetime = null, Func<SubChannelContext, Task>? subChannelHandler = null)
    {
        Server = new HostServer(new HostServerOptions
        {
            Certificate = SharedCert,
            Credentials = PasswordHasher.Create(User, Pass, WeakArgon2, allowWeakParameters: true),
            AllowWeakCredentialParameters = true,
            MaxPendingConnectionsPerAddress = maxPendingPerAddress,
            SubChannelHandler = subChannelHandler,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(ProtocolInfo.HeartbeatTimeoutSeconds),
            SessionTokenLifetime = tokenLifetime ?? TimeSpan.FromSeconds(ProtocolInfo.SessionTokenLifetimeSeconds),
            HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15),
            // 預設不延遲，避免測試互相干擾；節流行為另有專門測試。
            Throttle = throttle ?? new LoginThrottle(baseDelay: TimeSpan.Zero, globalBurst: 1000, globalPerSecond: 1000),
            Log = m => { lock (Logs) Logs.Add(m); },
        });
        Server.Start();
    }

    public ControllerConnectOptions Options(string? user = null, string? pass = null,
        KnownHostsStore? known = null, Func<string, string, bool>? confirm = null) => new()
    {
        Host = "127.0.0.1",
        Port = Server.Port,
        Username = user ?? User,
        Password = pass ?? Pass,
        KnownHosts = known ?? new KnownHostsStore(null),
        ConfirmNewHost = confirm ?? ((_, _) => true),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        SubChannelReconnectInitialDelay = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>
    /// 不經 ControllerClient，直接開一條 TLS 連線並送出 Hello（用來測協定細節）。
    /// <paramref name="readHello"/> 為 true 時讀取被控端的 Hello 並設定連線角色。
    /// </summary>
    public async Task<FrameStream> OpenRawAsync(ChannelKind channel, int version = ProtocolInfo.Version, bool readHello = true)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Server.Port);
        var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        var stream = new FrameStream(ssl, Endpoint.Controller);
        await stream.WriteAsync(new Hello(version, channel, []));
        if (readHello)
        {
            var reply = await ReadAsync<Hello>(stream);
            if (reply.Role != channel) throw new InvalidOperationException("Hello 角色不符");
            stream.Role = channel;
        }
        return stream;
    }

    /// <summary>以原始協定登入，回傳 Control 連線與兩枚 token。</summary>
    public async Task<(FrameStream Control, byte[] VideoToken, byte[] FileToken)> LoginRawAsync()
    {
        var control = await OpenRawAsync(ChannelKind.Control);
        await control.WriteAsync(new LoginRequest(User, Pass), control.NextRequestId());
        var result = await ReadAsync<LoginResult>(control);
        if (!result.Success) throw new InvalidOperationException("登入失敗");
        var g1 = await ReadAsync<SessionTokenGrant>(control);
        var g2 = await ReadAsync<SessionTokenGrant>(control);
        var grants = new[] { g1, g2 };
        return (control, grants.Single(g => g.Role == ChannelKind.Video).Token, grants.Single(g => g.Role == ChannelKind.File).Token);
    }

    /// <summary>以原始協定開子連線並用 token 計算證明；回傳結果與連線。</summary>
    public async Task<(SubConnectResult Result, FrameStream Stream, byte[] Nonce, byte[] Mac)> SubConnectRawAsync(
        ChannelKind role, byte[] token, ChannelKind? proofRole = null, byte[]? macOverride = null)
    {
        var stream = await OpenRawAsync(role);
        var challenge = await ReadAsync<SubConnectChallenge>(stream);
        byte[] mac = macOverride ?? SubConnectProof.Compute(token, challenge.Nonce, proofRole ?? role);
        await stream.WriteAsync(new SubConnectAuth(mac));
        return (await ReadAsync<SubConnectResult>(stream), stream, challenge.Nonce, mac);
    }

    public static async Task<T> ReadAsync<T>(FrameStream stream) where T : IMessage
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = await stream.ReadAsync(cts.Token) ?? throw new EndOfStreamException();
        return MessageCodec.Decode<T>(frame);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("條件未在時限內成立");
            await Task.Delay(20);
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
