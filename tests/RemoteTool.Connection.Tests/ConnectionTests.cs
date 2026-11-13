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
    public async Task FailedLogin_RequiresProgressiveWait_ButDoesNotLockOut()
    {
        await using var host = new TestHost(new LoginThrottle(baseDelay: TimeSpan.FromMilliseconds(600)));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => ControllerClient.ConnectAsync(host.Options(pass: "wrong-password")));

        // 等待期間：連正確密碼也先拒絕，並告知等待秒數
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => ControllerClient.ConnectAsync(host.Options()));
        Assert.Equal(AuthError.TooManyAttempts, ex.Error);
        Assert.True(ex.RetryAfterSeconds >= 1);

        // 等待結束後即可登入（不是鎖定）
        await Task.Delay(800);
        await using var session = await ControllerClient.ConnectAsync(host.Options());
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task TooManyUnauthenticatedConnections_FromOneAddress_AreRefused()
    {
        await using var host = new TestHost(maxPendingPerAddress: 2);
        using var a = new System.Net.Sockets.TcpClient();
        using var b = new System.Net.Sockets.TcpClient();
        await a.ConnectAsync("127.0.0.1", host.Server.Port);
        await b.ConnectAsync("127.0.0.1", host.Server.Port);
        await Task.Delay(200);

        using var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync("127.0.0.1", host.Server.Port);
        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(5000);
        int n = await c.GetStream().ReadAsync(buffer, cts.Token); // 被控端立即關閉 → 讀到 0
        Assert.Equal(0, n);
        await TestHost.WaitUntilAsync(() => host.Logs.Any(l => l.Contains("未驗證連線過多")));
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
        Assert.Null(known.HintFor(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port)));
        Assert.False(known.IsTrusted(shown!));
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
        Assert.Equal(host.Server.Fingerprint, known.HintFor(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port)));
        Assert.True(known.IsTrusted(host.Server.Fingerprint));
    }

    [Fact]
    public async Task SlowFingerprintConfirmation_LongerThanHostHandshakeTimeout_StillConnects()
    {
        // 握手時限要留足夠餘裕給「確認後重新連線並登入」（含 TLS 與 Argon2）；
        // 原本 300 ms 在 CPU 滿載（與長時間測試並行）時偶爾不夠，造成誤判失敗（T-037）。
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(1500));
        await using var session = await ControllerClient.ConnectAsync(host.Options(confirm: (_, _) =>
        {
            Thread.Sleep(3000); // 使用者慢慢核對，超過被控端握手時限
            return true;
        }));
        Assert.True(host.Server.HasActiveSession);
    }

    [Fact]
    public async Task SlowFingerprintConfirmation_LongerThanControllerConnectTimeout_StillConnects()
    {
        // 時限留餘裕給「確認後重新連線並登入」（CPU 滿載時 300 ms / 1 秒偶爾不夠，T-037）
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(1500));
        var options = host.Options(confirm: (_, _) =>
        {
            Thread.Sleep(4000); // 比控制端連線時限（2 秒）與被控端握手時限都久
            return true;
        });
        options = new ControllerConnectOptions
        {
            Host = options.Host, Port = options.Port, Username = options.Username, Password = options.Password,
            KnownHosts = options.KnownHosts, ConfirmNewHost = options.ConfirmNewHost,
            ConnectTimeout = TimeSpan.FromSeconds(2),
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
        await using var host = new TestHost(handshakeTimeout: TimeSpan.FromMilliseconds(1500));
        var known = new KnownHostsStore(null);
        string fp = await ControllerClient.ProbeFingerprintAsync("127.0.0.1", host.Server.Port);
        await Task.Delay(2500); // 使用者慢慢核對，超過被控端握手時限
        known.Trust(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port), fp);

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
        known.Trust(key, bogus); // 此位址上次是另一把（已信任的）金鑰
        bool asked = false;

        var ex = await Assert.ThrowsAsync<FingerprintMismatchException>(() => ControllerClient.ConnectAsync(
            host.Options(known: known, confirm: (_, _) => asked = true)));

        Assert.Equal(bogus, ex.Expected);
        Assert.Equal(host.Server.Fingerprint, ex.Actual);
        Assert.False(asked);
        Assert.Equal(bogus, known.HintFor(key)); // 不可自動覆寫
        Assert.False(known.IsTrusted(host.Server.Fingerprint));
        await Task.Delay(200);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    // ---------------------------------------------------------------- 以公鑰識別主機（G-06）

    [Fact]
    public async Task TrustedKey_AtNewAddress_IsAcceptedWithoutPrompt()
    {
        await using var host = new TestHost();
        var known = new KnownHostsStore(null);
        known.Trust("192.168.1.20:52100", host.Server.Fingerprint); // 之前在別的 IP 信任過這台
        bool asked = false;
        await using var session = await ControllerClient.ConnectAsync(host.Options(known: known, confirm: (_, _) => asked = true));
        Assert.False(asked);
        Assert.Equal(host.Server.Fingerprint, known.HintFor(KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port)));
    }

    [Fact]
    public async Task NewUntrustedKey_AtKnownAddress_IsRejected_EvenIfAnotherAddressTrustedIt()
    {
        await using var host = new TestHost();
        var known = new KnownHostsStore(null);
        string key = KnownHostsStore.KeyFor("127.0.0.1", host.Server.Port);
        string otherHost = string.Join(':', Enumerable.Repeat("AB", 32));
        known.Trust(key, otherHost);
        await Assert.ThrowsAsync<FingerprintMismatchException>(() => ControllerClient.ConnectAsync(host.Options(known: known)));

        // 移除此位址的紀錄後，重新詢問
        Assert.True(known.Forget(key));
        bool asked = false;
        await using var session = await ControllerClient.ConnectAsync(host.Options(known: known, confirm: (_, _) => asked = true));
        Assert.True(asked);
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

    // ---------------------------------------------------------------- 子連線 token（G-07、PLAN §7）

    [Fact]
    public async Task SubChannelToken_IsSingleUse()
    {
        await using var host = new TestHost();
        var (control, videoToken, _) = await host.LoginRawAsync();
        await using var _c = control;

        var (ok, s1, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken);
        Assert.True(ok.Success);
        await s1.DisposeAsync();

        var (again, s2, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken);
        await using var _s2 = s2;
        Assert.False(again.Success);
        Assert.Equal(AuthError.InvalidToken, again.Error);
        await TestHost.WaitUntilAsync(() => host.Logs.Any(l => l.Contains("子連線驗證失敗") && l.Contains("127.0.0.1")));
    }

    [Fact]
    public async Task VideoToken_CannotOpenFileChannel()
    {
        await using var host = new TestHost();
        var (control, videoToken, _) = await host.LoginRawAsync();
        await using var _c = control;
        // 用 Video token、以 File 角色計算證明
        var (r, s, _, _) = await host.SubConnectRawAsync(ChannelKind.File, videoToken);
        await using var _s = s;
        Assert.False(r.Success);
    }

    [Fact]
    public async Task ProofForWrongRole_IsRejected()
    {
        await using var host = new TestHost();
        var (control, _, fileToken) = await host.LoginRawAsync();
        await using var _c = control;
        // File token，但證明裡填 Video 角色
        var (r, s, _, _) = await host.SubConnectRawAsync(ChannelKind.File, fileToken, proofRole: ChannelKind.Video);
        await using var _s = s;
        Assert.False(r.Success);
    }

    [Fact]
    public async Task ReplayedProof_WithNewNonce_IsRejected()
    {
        await using var host = new TestHost();
        var (control, videoToken, _) = await host.LoginRawAsync();
        await using var _c = control;

        // 竊聽者錄下一次失敗前的證明（模擬：取得一組 nonce 的正確 MAC 但不使用）
        var probe = await host.OpenRawAsync(ChannelKind.Video);
        var oldChallenge = await TestHost.ReadAsync<SubConnectChallenge>(probe);
        byte[] recorded = SubConnectProof.Compute(videoToken, oldChallenge.Nonce, ChannelKind.Video);
        await probe.DisposeAsync();

        // 新連線的 nonce 不同，重放舊證明失敗
        var (r, s, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken, macOverride: recorded);
        await using var _s = s;
        Assert.False(r.Success);

        // token 本身仍可正常使用（重放不會耗掉它）
        var (ok, s2, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken);
        await using var _s2 = s2;
        Assert.True(ok.Success);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        await using var host = new TestHost(tokenLifetime: TimeSpan.FromMilliseconds(300));
        var (control, videoToken, _) = await host.LoginRawAsync();
        await using var _c = control;
        await Task.Delay(500);
        var (r, s, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken);
        await using var _s = s;
        Assert.False(r.Success);
        await TestHost.WaitUntilAsync(() => host.Logs.Any(l => l.Contains("Expired")));
    }

    [Fact]
    public async Task TokenRequest_OnControl_IssuesFreshToken_AndInvalidatesOld()
    {
        await using var host = new TestHost();
        var (control, oldVideo, _) = await host.LoginRawAsync();
        await using var _c = control;

        uint id = control.NextRequestId();
        await control.WriteAsync(new SessionTokenRequest(ChannelKind.Video), id);
        var frame = (await control.ReadAsync())!.Value;
        Assert.Equal(MessageType.SessionTokenGrant, frame.Type);
        Assert.Equal(id, frame.RequestId);
        var grant = MessageCodec.Decode<SessionTokenGrant>(frame);
        Assert.Equal((ChannelKind.Video, 30), (grant.Role, grant.ExpiresInSeconds));
        Assert.NotEqual(oldVideo, grant.Token);

        var (oldResult, s1, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, oldVideo);
        await s1.DisposeAsync();
        Assert.False(oldResult.Success);
        var (newResult, s2, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, grant.Token);
        await using var _s2 = s2;
        Assert.True(newResult.Success);
    }

    [Fact]
    public async Task ControllerSession_CanRequestReconnectToken()
    {
        await using var host = new TestHost();
        await using var session = await ControllerClient.ConnectAsync(host.Options());
        var grant = await session.RequestSubChannelTokenAsync(ChannelKind.File);
        Assert.Equal(ChannelKind.File, grant.Role);
        Assert.Equal(ProtocolInfo.SessionTokenSize, grant.Token.Length);
    }

    [Fact]
    public async Task SubChannel_WithoutSession_IsRejected()
    {
        await using var host = new TestHost();
        var (r, s, _, _) = await host.SubConnectRawAsync(ChannelKind.Video, new byte[ProtocolInfo.SessionTokenSize]);
        await using var _s = s;
        Assert.False(r.Success);
    }

    [Fact]
    public async Task TokenIsNeverSentOnSubConnection()
    {
        // 結構性檢查：子連線上控制端唯一送出的驗證資料是 32 位元組 MAC，與 token 不同
        await using var host = new TestHost();
        var (control, videoToken, _) = await host.LoginRawAsync();
        await using var _c = control;
        var (r, s, nonce, mac) = await host.SubConnectRawAsync(ChannelKind.Video, videoToken);
        await using var _s = s;
        Assert.True(r.Success);
        Assert.NotEqual(videoToken, mac);
        Assert.Equal(32, nonce.Length);
    }

    // ---------------------------------------------------------------- 子連線獨立重連（NET-03、PLAN §7）

    /// <summary>第 n 條指定角色的子連線：前幾條立即結束（模擬中斷），之後保持。</summary>
    private static Func<SubChannelContext, Task> DropFirst(ChannelKind role, int drops, Action? onConnected = null)
    {
        int count = 0;
        return ctx =>
        {
            if (ctx.Channel != role) return Task.Delay(Timeout.Infinite, ctx.Token);
            onConnected?.Invoke();
            return Interlocked.Increment(ref count) <= drops
                ? Task.Delay(300, ctx.Token)                 // handler 結束 → 被控端關閉此子連線（稍候，讓測試先訂閱事件）
                : Task.Delay(Timeout.Infinite, ctx.Token);
        };
    }

    [Theory]
    [InlineData(ChannelKind.Video)]
    [InlineData(ChannelKind.File)]
    public async Task SubChannelDrop_Reconnects_WithoutEndingSession(ChannelKind role)
    {
        int connections = 0;
        await using var host = new TestHost(subChannelHandler: DropFirst(role, drops: 1, () => Interlocked.Increment(ref connections)));
        await using var session = await ControllerClient.ConnectAsync(host.Options());
        var states = new List<SubChannelState>();
        session.SubChannelStateChanged += (r, st, _) => { if (r == role) lock (states) states.Add(st); };

        await TestHost.WaitUntilAsync(() => session.ReconnectCount >= 1);
        Assert.Equal(SubChannelState.Connected, session.GetState(role));
        Assert.False(session.Completion.IsCompleted);   // Control 保持、遠端桌面不關閉
        Assert.True(host.Server.HasActiveSession);
        Assert.Equal(2, Volatile.Read(ref connections));
        lock (states) Assert.Equal(new[] { SubChannelState.Reconnecting, SubChannelState.Connected }, states);
        await TestHost.WaitUntilAsync(() => host.Logs.Any(l => l.Contains($"重新發出 {role} token")));
    }

    [Fact]
    public async Task SubChannelReconnect_FailsAfterWindow_ThenManualRetrySucceeds()
    {
        await using var host = new TestHost(subChannelHandler: DropFirst(ChannelKind.Video, drops: 1));
        var options = host.Options();
        options = new ControllerConnectOptions
        {
            Host = options.Host, Port = options.Port, Username = options.Username, Password = options.Password,
            KnownHosts = options.KnownHosts, ConfirmNewHost = options.ConfirmNewHost,
            HeartbeatInterval = options.HeartbeatInterval,
            SubChannelReconnectInitialDelay = TimeSpan.FromMilliseconds(100),
            SubChannelReconnectWindow = TimeSpan.FromMilliseconds(800),
        };
        host.Server.SetAcceptPaused(true); // 之後的新連線都被拒，重連會失敗
        // 先讓 Control / 子連線建立：暫停前需要先連上
        host.Server.SetAcceptPaused(false);
        var connected = ControllerClient.ConnectAsync(options);
        await using var session = await connected;
        host.Server.SetAcceptPaused(true);

        await TestHost.WaitUntilAsync(() => session.GetState(ChannelKind.Video) == SubChannelState.Failed);
        Assert.False(session.Completion.IsCompleted); // 失敗也不關閉遠端桌面

        host.Server.SetAcceptPaused(false);
        session.RetrySubChannel(ChannelKind.Video);
        await TestHost.WaitUntilAsync(() => session.GetState(ChannelKind.Video) == SubChannelState.Connected);
        Assert.Equal(1, session.ReconnectCount);
    }

    [Fact]
    public async Task ControlDrop_StillEndsWholeSession()
    {
        var host = new TestHost();
        await using var session = await ControllerClient.ConnectAsync(host.Options());
        await host.DisposeAsync();
        var finished = await Task.WhenAny(session.Completion, Task.Delay(10_000));
        Assert.Same(session.Completion, finished);
    }

    // ---------------------------------------------------------------- 心跳與協定錯誤

    [Fact]
    public async Task MissingHeartbeat_EndsSession()
    {
        await using var host = new TestHost(heartbeatTimeout: TimeSpan.FromMilliseconds(500));
        var (control, _, _) = await host.LoginRawAsync();
        await using var _c = control;
        Assert.True(host.Server.HasActiveSession);
        // 不送心跳
        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession, 5000);
    }

    [Fact]
    public void HeartbeatDefaults_MatchPlan()
    {
        Assert.Equal(2, ProtocolInfo.HeartbeatIntervalSeconds);
        Assert.Equal(6, ProtocolInfo.HeartbeatTimeoutSeconds);
        var o = new ControllerConnectOptions { Host = "x", Username = "u", Password = "p", KnownHosts = new KnownHostsStore(null), ConfirmNewHost = (_, _) => false };
        Assert.Equal(TimeSpan.FromSeconds(6), o.HeartbeatTimeout);
    }

    [Fact]
    public async Task ProtocolVersionMismatch_GetsVersionError()
    {
        await using var host = new TestHost();
        await using var control = await host.OpenRawAsync(ChannelKind.Control, version: 99, readHello: false);
        var error = await TestHost.ReadAsync<ErrorMessage>(control);
        Assert.Equal(ErrorCode.VersionMismatch, error.Code);
        Assert.Contains("版本", error.Message);
    }

    [Fact]
    public async Task UnknownMessageType_GetsErrorAndDisconnect()
    {
        await using var host = new TestHost();
        var (control, _, _) = await host.LoginRawAsync();
        await using var _c = control;

        // 直接寫入未知類型 199 的原始封包（序號接續：Hello=1, Login=2 → 3）
        var stream = GetInnerStream(control);
        var raw = FrameCodec.Encode(new Frame((MessageType)199, 0, 3, Array.Empty<byte>()));
        await stream.WriteAsync(raw);
        await stream.FlushAsync();

        var error = await TestHost.ReadAsync<ErrorMessage>(control);
        Assert.Equal(ErrorCode.UnknownMessageType, error.Code);
        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession);
    }

    [Fact]
    public async Task WrongSequence_GetsErrorAndDisconnect()
    {
        await using var host = new TestHost();
        var (control, _, _) = await host.LoginRawAsync();
        await using var _c = control;

        var stream = GetInnerStream(control);
        var raw = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 1, 99, MessageCodec.Serialize(new Heartbeat(0))));
        await stream.WriteAsync(raw);
        await stream.FlushAsync();

        var error = await TestHost.ReadAsync<ErrorMessage>(control);
        Assert.Equal(ErrorCode.ProtocolViolation, error.Code);
        await TestHost.WaitUntilAsync(() => !host.Server.HasActiveSession);
    }

    [Fact]
    public async Task OversizedFrame_IsRejected()
    {
        await using var host = new TestHost();
        await using var control = await host.OpenRawAsync(ChannelKind.Control);
        var stream = GetInnerStream(control);
        await stream.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0, 2 }); // Length ≈ 2 GiB
        await stream.FlushAsync();

        var error = await TestHost.ReadAsync<ErrorMessage>(control);
        Assert.Equal(ErrorCode.ProtocolViolation, error.Code);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    [Fact]
    public async Task LoginOnVideoConnection_IsRejected()
    {
        await using var host = new TestHost();
        await using var video = await host.OpenRawAsync(ChannelKind.Video);
        await TestHost.ReadAsync<SubConnectChallenge>(video);
        var stream = GetInnerStream(video);
        // Video 連線上送 LoginRequest（序號 2）
        var raw = FrameCodec.Encode(new Frame(MessageType.LoginRequest, 1, 2, MessageCodec.Serialize(new LoginRequest(TestHost.User, TestHost.Pass))));
        await stream.WriteAsync(raw);
        await stream.FlushAsync();
        var error = await TestHost.ReadAsync<ErrorMessage>(video);
        Assert.Equal(ErrorCode.ProtocolViolation, error.Code);
        Assert.Equal(0, host.Server.LoginAttempts);
    }

    private static Stream GetInnerStream(FrameStream fs)
        => (Stream)typeof(FrameStream).GetField("_stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fs)!;
}
