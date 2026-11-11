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

    public TestHost(LoginThrottle? throttle = null, TimeSpan? heartbeatTimeout = null)
    {
        Server = new HostServer(new HostServerOptions
        {
            Certificate = SharedCert,
            Credentials = PasswordHasher.Create(User, Pass, iterations: 1000),
            BindAddress = IPAddress.Loopback,
            Port = 0,
            FailedLoginDelay = TimeSpan.Zero,
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10),
            Throttle = throttle ?? new LoginThrottle(),
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
    };

    /// <summary>不經 ControllerClient，直接開一條 TLS 連線並送出 Hello（用來測協定細節）。</summary>
    public async Task<FrameStream> OpenRawAsync(ChannelKind channel, int version = ProtocolInfo.Version)
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
        var stream = new FrameStream(ssl);
        await stream.WriteAsync(channel, new Hello(version, channel));
        return stream;
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
