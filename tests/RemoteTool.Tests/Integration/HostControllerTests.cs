using System.Net;
using RemoteTool.Controller.Core;
using RemoteTool.Host.Core;
using RemoteTool.HostGuard.Core;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;
using Xunit;

namespace RemoteTool.Tests.Integration;

/// <summary>
/// 以 loopback 執行真實 TLS 1.3 的端對端測試（G0-01、G0-03 的協定部分）。
/// 注意：這不能取代規劃書要求的兩台實體 Windows 11 實測，結果不得據此標記 Gate 為 PASS。
/// </summary>
public sealed class HostControllerTests : IAsyncLifetime
{
    private const string User = "alex";
    private const string Password = "correct-horse-1";

    private readonly HostIdentity _identity = HostIdentity.CreateNew();
    private readonly FakeGuardChannel _guard = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeVideoSource _video = new();
    private readonly SessionTokenService _tokens = new();
    private HostServer _server = null!;
    private int _port;

    public Task InitializeAsync()
    {
        var accounts = new AccountStore(new HostSettings(), new PasswordHasher(new Argon2Parameters(1024, 1, 1)));
        accounts.SetPassword(User, Password);
        _server = new HostServer(new HostServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                Certificate = _identity.Certificate,
                WindowsSessionId = 1,
                HeartbeatTimeout = TimeSpan.FromSeconds(6),
                EvaluateInterval = TimeSpan.FromMilliseconds(20),
                ResumeStableDuration = TimeSpan.FromMilliseconds(100),
            },
            accounts, new LoginThrottle(globalAttemptsPerSecond: 1000), _tokens, _guard, _desktop, _video);
        _server.StartListening();
        _port = _server.LocalEndpoint!.Port;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _identity.Dispose();
    }

    private ControllerClientOptions Options(KnownHostStore? known = null, string password = Password,
        Func<FingerprintPrompt, CancellationToken, Task<bool>>? confirm = null) => new()
    {
        Host = "127.0.0.1",
        Port = _port,
        Username = User,
        Password = password,
        KnownHosts = known ?? new KnownHostStore(),
        ConfirmFingerprint = confirm ?? ((_, _) => Task.FromResult(true)),
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
    };

    private async Task<ControllerClient> ConnectAndResumeAsync(KnownHostStore? known = null)
    {
        var client = await ControllerClient.ConnectAsync(Options(known));
        await Wait.UntilAsync(() => client.CanSendInput, because: "應完成首次恢復（IDR + ResumeAck）");
        return client;
    }

    [Fact]
    public async Task FirstConnection_ShowsSpkiFingerprint_ThreeChannels_ThenResumesAndForwardsInput()
    {
        FingerprintPrompt? prompt = null;
        var known = new KnownHostStore();
        await using var client = await ControllerClient.ConnectAsync(Options(known, confirm: (p, _) =>
        {
            prompt = p;
            return Task.FromResult(true);
        }));

        Assert.NotNull(prompt);
        Assert.Equal(_identity.Fingerprint, prompt!.Fingerprint);           // 兩端相同的 SHA-256(SPKI)
        Assert.Equal(_identity.FingerprintText, prompt.FingerprintText);
        Assert.True(client.IsVideoConnected);
        Assert.True(client.IsFileConnected);

        await Wait.UntilAsync(() => client.CanSendInput);
        var session = _server.CurrentSession!;
        Assert.Equal(GateState.Active, session.State);

        Assert.True(await client.SendKeyAsync(0x2A, false, true));
        await Wait.UntilAsync(() => _guard.Messages.Any(m => m.Type == GuardMessageType.Key && m.ScanCode == 0x2A));
        var resume = _guard.Messages.Last(m => m.Type == GuardMessageType.Resume);
        Assert.Equal(client.Generation, resume.Generation);
    }

    [Fact]
    public async Task UnconfirmedFingerprint_NoLogin()
    {
        await Assert.ThrowsAsync<FingerprintNotConfirmedException>(() =>
            ControllerClient.ConnectAsync(Options(confirm: (_, _) => Task.FromResult(false))));
        Assert.Null(_server.CurrentSession);
    }

    [Fact]
    public async Task HostKeyChanged_Rejected_BeforeCredentialsSent()
    {
        var known = new KnownHostStore();
        known.Approve("127.0.0.1", new byte[32]); // 先前核准的是另一把金鑰
        bool prompted = false;
        await Assert.ThrowsAsync<HostKeyChangedException>(() =>
            ControllerClient.ConnectAsync(Options(known, confirm: (_, _) => { prompted = true; return Task.FromResult(true); })));
        Assert.False(prompted);
        Assert.Null(_server.CurrentSession);
    }

    [Fact]
    public async Task TrustedHost_NoPromptOnSecondConnection()
    {
        var known = new KnownHostStore();
        await using (var first = await ControllerClient.ConnectAsync(Options(known)))
            await first.CloseAsync();
        await Wait.UntilAsync(() => _server.CurrentSession is null);

        int prompts = 0;
        await using var second = await ControllerClient.ConnectAsync(Options(known, confirm: (_, _) =>
        {
            prompts++;
            return Task.FromResult(true);
        }));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task WrongPassword_InvalidCredentials_ThenThrottled()
    {
        var ex1 = await Assert.ThrowsAsync<LoginFailedException>(() => ControllerClient.ConnectAsync(Options(password: "wrong-password")));
        Assert.Equal(LoginStatus.InvalidCredentials, ex1.Status);

        var ex2 = await Assert.ThrowsAsync<LoginFailedException>(() => ControllerClient.ConnectAsync(Options(password: "wrong-password")));
        Assert.Equal(LoginStatus.Throttled, ex2.Status);
        Assert.True(ex2.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Unreachable_ReportedDistinctlyFromAuthFailure()
    {
        var options = Options() ;
        var unreachable = new ControllerClientOptions
        {
            Host = "127.0.0.1", Port = 1, Username = User, Password = Password,
            KnownHosts = options.KnownHosts, ConfirmFingerprint = options.ConfirmFingerprint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        await Assert.ThrowsAsync<HostUnreachableException>(() => ControllerClient.ConnectAsync(unreachable));
    }

    [Fact]
    public async Task SecondController_RejectedAsBusy()
    {
        await using var first = await ControllerClient.ConnectAsync(Options());
        await Assert.ThrowsAsync<HostBusyException>(() => ControllerClient.ConnectAsync(Options()));
    }

    [Fact]
    public async Task VideoDrop_PausesWithNewGeneration_ReconnectsAndResumes_ControlStays()
    {
        await using var client = await ConnectAndResumeAsync();
        var session = _server.CurrentSession!;
        uint before = client.Generation;
        var paused = new TaskCompletionSource<PauseReason>();
        client.Paused += (r, _) => paused.TrySetResult(r);

        await client.SendKeyAsync(0x1D, false, true);
        await client.DropSubConnectionAsync(ChannelRole.Video);

        Assert.Equal(PauseReason.VideoLost, await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // 被控端：HostGuard 收到新世代的 Pause（會釋放按鍵）
        await Wait.UntilAsync(() => _guard.Messages.Any(m => m.Type == GuardMessageType.Pause && m.Generation > before));

        await Wait.UntilAsync(() => client.CanSendInput && client.IsVideoConnected, because: "Video 應自動重連並恢復");
        Assert.True(client.Generation > before);
        Assert.Same(session, _server.CurrentSession); // Control 保持，同一工作階段
    }

    [Fact]
    public async Task FileDrop_ReconnectsIndependently_VideoAndInputUnaffected()
    {
        await using var client = await ConnectAndResumeAsync();
        uint gen = client.Generation;
        await client.DropSubConnectionAsync(ChannelRole.File);
        await Wait.UntilAsync(() => !client.IsFileConnected || _server.CurrentSession!.HasFile);
        await Wait.UntilAsync(() => client.IsFileConnected && _server.CurrentSession!.HasFile);
        Assert.True(client.CanSendInput);
        Assert.Equal(gen, client.Generation);
    }

    [Fact]
    public async Task ControlDisconnect_RevokesSessionTokensAndReleasesInput()
    {
        var client = await ConnectAndResumeAsync();
        var session = _server.CurrentSession!;
        await client.CloseAsync();

        var reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DisconnectReason.Normal, reason);
        Assert.Null(_server.CurrentSession);
        Assert.Equal(0, _tokens.OutstandingCount);
        Assert.Equal(GuardMessageType.Pause, _guard.Messages[^1].Type); // 結束時 HostGuard 暫停並釋放
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Lock_PausesAndDropsInput_UnlockResumesWithNewGeneration()
    {
        await using var client = await ConnectAndResumeAsync();
        uint before = client.Generation;
        var reasons = new List<PauseReason>();
        client.Paused += (r, _) => { lock (reasons) reasons.Add(r); };

        _desktop.Locked = true;
        await Wait.UntilAsync(() => !client.CanSendInput);
        lock (reasons) Assert.Contains(PauseReason.Locked, reasons);
        Assert.False(await client.SendKeyAsync(0x1E, false, true)); // 控制端不送

        _desktop.Locked = false;
        await Wait.UntilAsync(() => client.CanSendInput, because: "本機解鎖後應自動恢復");
        Assert.Equal(before + 1, client.Generation);
    }

    [Fact]
    public async Task StaleGenerationInput_DroppedByHost()
    {
        await using var client = await ConnectAndResumeAsync();
        var session = _server.CurrentSession!;
        _desktop.InputDesktopIsDefault = false; // 例如 UAC 安全桌面
        await Wait.UntilAsync(() => session.State == GateState.Paused);
        _desktop.InputDesktopIsDefault = true;
        await Wait.UntilAsync(() => client.CanSendInput);

        // 直接以舊世代送出（模擬暫停前在途的事件）
        long dropped = session.DroppedInputCount;
        var countBefore = _guard.Messages.Count(m => m.Type == GuardMessageType.Key);
        var field = typeof(ControllerClient).GetField("_control", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var control = (FramedConnection)field.GetValue(client)!;
        await control.SendAsync(new KeyEvent(client.Generation - 1, 0x30, false, true));
        await Wait.UntilAsync(() => session.DroppedInputCount > dropped);
        Assert.Equal(countBefore, _guard.Messages.Count(m => m.Type == GuardMessageType.Key));
    }

    [Fact]
    public async Task UserLoggedOff_EndsSessionWithReason()
    {
        await using var client = await ConnectAndResumeAsync();
        _desktop.RaiseSessionEnding(DisconnectReason.UserLoggedOff);
        Assert.Equal(DisconnectReason.UserLoggedOff, await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UserSwitch_EndsSession_YieldsPort_AndRelistensWithoutInheritingAuth()
    {
        await using var client = await ConnectAndResumeAsync();
        _desktop.RaiseConsoleActive(false);
        Assert.Equal(DisconnectReason.UserSwitched, await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Wait.UntilAsync(() => !_server.IsListening);

        // 埠已讓出：另一個程序（另一使用者的 Host）可以綁定
        using (var other = new System.Net.Sockets.TcpListener(IPAddress.Loopback, _port))
        {
            other.Start();
            other.Stop();
        }

        // A 回到主控台後重新監聽，必須重新登入；舊 Token 全部失效
        _desktop.RaiseConsoleActive(true);
        await Wait.UntilAsync(() => _server.IsListening);
        Assert.Equal(0, _tokens.OutstandingCount);
    }

    [Fact]
    public async Task Relisten_RetriesWhilePortHeldByAnotherProcess()
    {
        // 取得一個固定埠（本類別的主伺服器用的是隨機埠，重新監聽會換埠）
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var desktop = new FakeDesktop { OnConsole = false };
        var accounts = new AccountStore(new HostSettings(), new PasswordHasher(new Argon2Parameters(1024, 1, 1)));
        await using var server = new HostServer(
            new HostServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port), Certificate = _identity.Certificate },
            accounts, new LoginThrottle(), new SessionTokenService(), new FakeGuardChannel(), desktop, new FakeVideoSource());

        // 模擬另一使用者的 Host 尚未讓出埠（明確關閉位址重用；Linux 上 .NET 預設開 SO_REUSEPORT）
        var other = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        if (OperatingSystem.IsWindows())
            other.ExclusiveAddressUse = true;
        else
            other.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, false);
        other.Bind(new IPEndPoint(IPAddress.Loopback, port));
        other.Listen();

        desktop.RaiseConsoleActive(true);
        await Task.Delay(300);
        Assert.False(server.IsListening);
        other.Dispose();
        await Wait.UntilAsync(() => server.IsListening, because: "埠釋放後應重試成功");
        Assert.Equal(port, server.LocalEndpoint!.Port);
    }

    [Fact]
    public async Task GuardFault_StopsRemoteControl_AndRefusesNewLogins()
    {
        await using var client = await ConnectAndResumeAsync();
        _guard.Fault();
        Assert.Equal(DisconnectReason.GuardUnavailable, await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => ControllerClient.ConnectAsync(Options()));
        Assert.Equal(LoginStatus.NotAllowed, ex.Status);
    }

    [Fact]
    public async Task TokenReplay_OnNewSubConnection_Rejected()
    {
        await using var client = await ConnectAndResumeAsync();
        // 手動申請一枚 Token，使用一次後重放
        var session = _server.CurrentSession!;
        var token = _tokens.Issue(new TokenBinding(User, 1, session.ControlConnectionId, ChannelRole.File));
        Assert.True(await RawSubConnectAsync(token, ChannelRole.File));
        Assert.False(await RawSubConnectAsync(token, ChannelRole.File));
    }

    private async Task<bool> RawSubConnectAsync(byte[] token, ChannelRole role)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        await using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls13, ssl.SslProtocol);
        var conn = new FramedConnection(ssl);
        await conn.SendAsync(new Hello(ProtocolConstants.ProtocolVersion, role, []));
        var ack = MessageSerializer.Read<HelloAck>((await conn.ReceiveAsync())!.Value);
        await conn.SendAsync(new SubConnectAuth(role, SubConnectProof.Compute(token, ack.Nonce!, role)));
        return MessageSerializer.Read<SubConnectResult>((await conn.ReceiveAsync())!.Value).Success;
    }

    [Fact]
    public async Task OversizedFrame_DisconnectsWithoutCrashingServer()
    {
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _port);
            await using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            await ssl.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0, 0, 0, 0, 1 });
            var buf = new byte[16];
            int n;
            try { n = await ssl.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { n = 0; }
            Assert.Equal(0, n); // 伺服器斷線
        }
        // 伺服器仍正常服務
        await using var client = await ControllerClient.ConnectAsync(Options());
        Assert.True(client.IsVideoConnected);
    }
}
