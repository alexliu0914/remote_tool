using System.Net;
using RemoteTool.Controller.Core;
using RemoteTool.Host.Core;
using RemoteTool.HostGuard.Core;
using RemoteTool.Tests.Guard;
using Xunit;

namespace RemoteTool.Tests.Integration;

/// <summary>把 Host 的 IPC 直接接到真正的 <see cref="InjectionEngine"/>（取代 Named Pipe），驗證跨元件的世代與釋放行為。</summary>
internal sealed class EngineGuardChannel(InjectionEngine engine) : IGuardChannel
{
    private readonly Lock _lock = new();
    public bool IsConnected => true;
    public event Action? Faulted { add { } remove { } }

    public ValueTask<bool> SendAsync(GuardMessage message)
    {
        lock (_lock)
            engine.Handle(message);
        return ValueTask.FromResult(true);
    }
}

public sealed class RealEngineTests : IAsyncLifetime
{
    private readonly HostIdentity _identity = HostIdentity.CreateNew();
    private readonly FakeInjector _injector = new();
    private readonly FakeDesktop _desktop = new();
    private HostServer _server = null!;
    private int _port;

    public Task InitializeAsync()
    {
        var accounts = new AccountStore(new HostSettings(), new PasswordHasher(new Argon2Parameters(1024, 1, 1)));
        accounts.SetPassword("alex", "correct-horse-1");
        var engine = new InjectionEngine(_injector, new FakeProbe());
        _server = new HostServer(new HostServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                Certificate = _identity.Certificate,
                EvaluateInterval = TimeSpan.FromMilliseconds(20),
                ResumeStableDuration = TimeSpan.FromMilliseconds(100),
            },
            accounts, new LoginThrottle(globalAttemptsPerSecond: 1000), new SessionTokenService(),
            new EngineGuardChannel(engine), _desktop, new FakeVideoSource());
        _server.StartListening();
        _port = _server.LocalEndpoint!.Port;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _identity.Dispose();
    }

    private async Task<ControllerClient> ConnectAsync()
    {
        var client = await ControllerClient.ConnectAsync(new ControllerClientOptions
        {
            Host = "127.0.0.1", Port = _port, Username = "alex", Password = "correct-horse-1",
            KnownHosts = new KnownHostStore(), ConfirmFingerprint = (_, _) => Task.FromResult(true),
        });
        await Wait.UntilAsync(() => client.CanSendInput);
        return client;
    }

    private int CountCalls(string call)
    {
        lock (_injector.Calls) return _injector.Calls.Count(c => c == call);
    }

    [Fact]
    public async Task SecondSession_CanInject_AndDisconnectReleasesHeldKeys()
    {
        var first = await ConnectAsync();
        await first.SendKeyAsync(0x2A, false, true);
        await Wait.UntilAsync(() => CountCalls("key 2A down") == 1);
        await first.CloseAsync();
        await Wait.UntilAsync(() => CountCalls("key 2A up") == 1, because: "Control 中斷時被控端須釋放遠端按住的鍵");
        await first.DisposeAsync();
        await Wait.UntilAsync(() => _server.CurrentSession is null);

        await using var second = await ConnectAsync();
        await second.SendKeyAsync(0x1E, false, true);
        await Wait.UntilAsync(() => CountCalls("key 1E down") == 1, because: "第二個工作階段的世代須大於前一個");
    }

    [Fact]
    public async Task Lock_ReleasesHeldKeyAndButton_ThenResumes()
    {
        await using var client = await ConnectAsync();
        await client.SendKeyAsync(0x1D, false, true);
        await client.SendMouseButtonAsync(RemoteTool.Protocol.Messages.MouseButton.Left, true);
        await Wait.UntilAsync(() => CountCalls("button Left down") == 1);

        _desktop.Locked = true;
        await Wait.UntilAsync(() => CountCalls("key 1D up") == 1 && CountCalls("button Left up") == 1);
        await Wait.UntilAsync(() => !client.CanSendInput); // 控制端收到 PauseControl（在途的舊世代輸入會被丟棄）

        _desktop.Locked = false;
        await Wait.UntilAsync(() => client.CanSendInput);
        await client.SendKeyAsync(0x1D, false, true);
        await Wait.UntilAsync(() => CountCalls("key 1D down") == 2);
    }
}
