using RemoteTool.Connection;
using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Connection.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task Login_Succeeds_OpensAllThreeChannels_AndHeartbeats()
    {
        await using var host = new TestHost();
        await using var session = await ControllerClient.ConnectAsync(host.Options());

        Assert.Equal(host.Server.Fingerprint, session.Fingerprint);
        Assert.True(host.Server.HasActiveSession);

        await TestHost.WaitUntilAsync(() => session.LastRoundTrip is not null);
        // 子連線建立後被控端會補發 token 供重連
        await TestHost.WaitUntilAsync(() => session.HasReconnectToken(ChannelKind.Video) && session.HasReconnectToken(ChannelKind.File));
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        await using var host = new TestHost();
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => ControllerClient.ConnectAsync(host.Options(pass: "wrong")));
        Assert.Equal(AuthError.BadCredentials, ex.Error);
        Assert.False(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task WrongUsername_IsRejected()
    {
        await using var host = new TestHost();
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => ControllerClient.ConnectAsync(host.Options(user: "someone")));
        Assert.Equal(AuthError.BadCredentials, ex.Error);
    }

    [Fact]
    public async Task TooManyFailures_LocksOut_EvenWithCorrectPassword()
    {
        await using var host = new TestHost(new LoginThrottle(maxFailures: 3));
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => ControllerClient.ConnectAsync(host.Options(pass: "wrong")));

        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => ControllerClient.ConnectAsync(host.Options()));
        Assert.Equal(AuthError.TooManyAttempts, ex.Error);
    }

    [Fact]
    public async Task UnknownHost_Declined_DoesNotSendCredentials_OrRemember()
    {
        await using var host = new TestHost();
        var known = new KnownHostsStore(null);
        string? shown = null;

        await Assert.ThrowsAsync<FingerprintRejectedException>(() => ControllerClient.ConnectAsync(
            host.Options(known: known, confirm: (_, fp) => { shown = fp; return false; })));

        Assert.Equal(host.Server.Fingerprint, shown);
        Assert.Null(known.Get(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port)));
        await Task.Delay(200);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    [Fact]
    public async Task UnknownHost_Accepted_IsRemembered_AndNotAskedAgain()
    {
        await using var host = new TestHost();
        var known = new KnownHostsStore(null);
        int asked = 0;
        Func<string, string, bool> confirm = (_, _) => { asked++; return true; };

        await using (await ControllerClient.ConnectAsync(host.Options(known: known, confirm: confirm))) { }
        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession);
        await using (await ControllerClient.ConnectAsync(host.Options(known: known, confirm: confirm))) { }

        Assert.Equal(1, asked);
        Assert.Equal(host.Server.Fingerprint, known.Get(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port)));
    }

    [Fact]
    public async Task SlowFingerprintConfirmation_LongerThanHostHandshakeTimeout_StillConnects()
    {
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        await using var session = await ControllerClient.ConnectAsync(host.Options(confirm: (_, _) =>
        {
            Thread.Sleep(800); // 使用者慢慢核對
            return true;
        }));
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task SlowFingerprintConfirmation_LongerThanControllerConnectTimeout_StillConnects()
    {
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        var options = host.Options(confirm: (_, _) =>
        {
            Thread.Sleep(2500); // 比控制端連線時限（1 秒）還久
            return true;
        });
        options = new ControllerConnectOptions
        {
            Host = options.Host, Port = options.Port, Username = options.Username, Password = options.Password,
            KnownHosts = options.KnownHosts, ConfirmNewHost = options.ConfirmNewHost,
            ConnectTimeout = TimeSpan.FromSeconds(1),
        };
        await using var session = await ControllerClient.ConnectAsync(options);
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task Probe_ReturnsFingerprint_WithoutLoginAttempt()
    {
        await using var host = new TestHost();
        string fp = await ControllerClient.ProbeFingerprintAsync("127.0.0.1", host.Server.Port);
        Assert.Equal(host.Server.Fingerprint, fp);
        await Task.Delay(200);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    [Fact]
    public async Task ProbeThenTrust_ThenConnect_DoesNotAskAgain()
    {
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        var known = new KnownHostsStore(null);
        string fp = await ControllerClient.ProbeFingerprintAsync("127.0.0.1", host.Server.Port);
        await Task.Delay(1000); // 使用者慢慢核對，超過被控端握手時限
        known.Set(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port), fp);

        bool asked = false;
        await using var session = await ControllerClient.ConnectAsync(host.Options(known: known, confirm: (_, _) => asked = true));
        Assert.False(asked);
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task FingerprintMismatch_Aborts_BeforeSendingCredentials()
    {
        await using var host = new TestHost();
        var known = new KnownHostsStore(null);
        string key = KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port);
        string bogus = string.Join(':', Enumerable.Repeat("00", 32));
        known.Set(key, bogus);
        bool asked = false;

        var ex = await Assert.ThrowsAsync<FingerprintMismatchException>(() => ControllerClient.ConnectAsync(
            host.Options(known: known, confirm: (_, _) => asked = true)));

        Assert.Equal(bogus, ex.Expected);
        Assert.Equal(host.Server.Fingerprint, ex.Actual);
        Assert.False(asked);
        Assert.Equal(bogus, known.Get(key)); // 不可自動覆寫
        await Task.Delay(200);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    [Fact]
    public async Task SecondController_IsBusy()
    {
        await using var host = new TestHost();
        await using var first = await ControllerClient.ConnectAsync(host.Options());

        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => ControllerClient.ConnectAsync(host.Options()));
        Assert.Equal(AuthError.Busy, ex.Error);
        Assert.False(first.Completion.IsCompleted);
    }

    [Fact]
    public async Task ControllerDisconnect_EndsSession_AndAllowsNewLogin()
    {
        await using var host = new TestHost();
        var session = await ControllerClient.ConnectAsync(host.Options());
        await session.DisposeAsync();

        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession);
        await using var again = await ControllerClient.ConnectAsync(host.Options());
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task HostShutdown_CompletesControllerSession()
    {
        var host = new TestHost();
        await using var session = await ControllerClient.ConnectAsync(host.Options());
        await host.DisposeAsync();

        var finished = await Task.WhenAny(session.Completion, Task.Delay(10_000));
        Assert.Same(session.Completion, finished);
    }

    [Fact]
    public async Task SubChannelToken_IsSingleUse()
    {
        await using var host = new TestHost();
        await using var control = await host.OpenRawAsync(ChannelKind.Control);
        await TestHost.ReadAsync<Hello>(control);
        await control.WriteAsync(ChannelKind.Control, new LoginRequest(TestHost.User, TestHost.Pass));
        Assert.True((await TestHost.ReadAsync<LoginResult>(control)).Success);

        var grants = new[] { await TestHost.ReadAsync<SessionTokenGrant>(control), await TestHost.ReadAsync<SessionTokenGrant>(control) };
        byte[] videoToken = grants.Single(g => g.Channel == ChannelKind.Video).Token;

        await using (var video = await host.OpenRawAsync(ChannelKind.Video))
        {
            await TestHost.ReadAsync<Hello>(video);
            await video.WriteAsync(ChannelKind.Video, new SubConnectAuth(videoToken));
            Assert.True((await TestHost.ReadAsync<SubConnectResult>(video)).Success);
        }

        await using var replay = await host.OpenRawAsync(ChannelKind.Video);
        await TestHost.ReadAsync<Hello>(replay);
        await replay.WriteAsync(ChannelKind.Video, new SubConnectAuth(videoToken));
        var result = await TestHost.ReadAsync<SubConnectResult>(replay);
        Assert.False(result.Success);
        Assert.Equal(AuthError.InvalidToken, result.Error);
    }

    [Fact]
    public async Task VideoToken_CannotOpenFileChannel()
    {
        await using var host = new TestHost();
        await using var control = await host.OpenRawAsync(ChannelKind.Control);
        await TestHost.ReadAsync<Hello>(control);
        await control.WriteAsync(ChannelKind.Control, new LoginRequest(TestHost.User, TestHost.Pass));
        await TestHost.ReadAsync<LoginResult>(control);
        var grants = new[] { await TestHost.ReadAsync<SessionTokenGrant>(control), await TestHost.ReadAsync<SessionTokenGrant>(control) };

        await using var file = await host.OpenRawAsync(ChannelKind.File);
        await TestHost.ReadAsync<Hello>(file);
        await file.WriteAsync(ChannelKind.File, new SubConnectAuth(grants.Single(g => g.Channel == ChannelKind.Video).Token));
        Assert.False((await TestHost.ReadAsync<SubConnectResult>(file)).Success);
    }

    [Fact]
    public async Task SubChannel_WithoutSession_IsRejected()
    {
        await using var host = new TestHost();
        await using var video = await host.OpenRawAsync(ChannelKind.Video);
        await TestHost.ReadAsync<Hello>(video);
        await video.WriteAsync(ChannelKind.Video, new SubConnectAuth(new byte[ProtocolInfo.SessionTokenSize]));
        Assert.False((await TestHost.ReadAsync<SubConnectResult>(video)).Success);
    }

    [Fact]
    public async Task MissingHeartbeat_EndsSession()
    {
        await using var host = new TestHost(heartbeatTimeout: TimeSpan.FromMilliseconds(500));
        await using var control = await host.OpenRawAsync(ChannelKind.Control);
        await TestHost.ReadAsync<Hello>(control);
        await control.WriteAsync(ChannelKind.Control, new LoginRequest(TestHost.User, TestHost.Pass));
        Assert.True((await TestHost.ReadAsync<LoginResult>(control)).Success);
        Assert.True(host.Server.HasActiveSession);

        // 不送心跳
        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession, 5000);
    }

    [Fact]
    public async Task ProtocolVersionMismatch_GetsError()
    {
        await using var host = new TestHost();
        await using var control = await host.OpenRawAsync(ChannelKind.Control, version: 99);
        var error = await TestHost.ReadAsync<ErrorMessage>(control);
        Assert.Contains("版本", error.Message);
    }

    [Fact]
    public async Task FrameOnWrongChannel_ClosesConnection()
    {
        await using var host = new TestHost();
        // Control 連線上送出標示為 Video 通道的封包
        await using var stream = await host.OpenRawAsync(ChannelKind.Control);
        await TestHost.ReadAsync<Hello>(stream);
        await stream.WriteAsync(ChannelKind.Video, new LoginRequest(TestHost.User, TestHost.Pass));
        using var cts = new CancellationTokenSource(5000);
        // 被控端應直接關閉連線
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var f = await stream.ReadAsync(cts.Token);
            if (f is null) throw new EndOfStreamException();
        });
        Assert.False(host.Server.HasActiveSession);
    }
}
