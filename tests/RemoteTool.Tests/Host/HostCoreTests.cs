using System.Net;
using RemoteTool.Host.Core;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;
using Xunit;

namespace RemoteTool.Tests.Host;

public class PasswordHasherTests
{
    internal static readonly Argon2Parameters Fast = new(1024, 1, 1);

    [Fact]
    public void HashThenVerify()
    {
        var hasher = new PasswordHasher(Fast);
        var hash = hasher.Hash("correct horse battery");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash);
        Assert.True(hasher.Verify("correct horse battery", hash));
        Assert.False(hasher.Verify("correct horse batterx", hash));
    }

    [Fact]
    public void SamePassword_DifferentSalt()
    {
        var hasher = new PasswordHasher(Fast);
        Assert.NotEqual(hasher.Hash("password-1234"), hasher.Hash("password-1234"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=99999999,t=1,p=1$AAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAA")] // 竄改成巨大記憶體
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$!!!$AAAA")]
    public void MalformedHash_RejectedWithoutThrowing(string encoded)
        => Assert.False(new PasswordHasher(Fast).Verify("whatever-123", encoded));
}

public class AccountStoreTests
{
    [Fact]
    public void Verify_CaseInsensitiveUsername_ReturnsCanonicalName()
    {
        var store = new AccountStore(new HostSettings(), new PasswordHasher(PasswordHasherTests.Fast));
        store.SetPassword("Alex", "0123456789");
        Assert.Equal("Alex", store.Verify("alex", "0123456789"));
        Assert.Null(store.Verify("alex", "0123456788"));
        Assert.Null(store.Verify("nobody", "0123456789"));
    }

    [Fact]
    public void ShortPassword_Rejected()
    {
        var store = new AccountStore(new HostSettings(), new PasswordHasher(PasswordHasherTests.Fast));
        Assert.Throws<ArgumentException>(() => store.SetPassword("alex", "123456789"));
    }
}

public class HostConfigStoreTests
{
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] p) => Protect(p);
    }

    [Fact]
    public void Identity_PersistsAcrossLoads()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new HostConfigStore(Path.Combine(dir.FullName, "host.dat"), new XorProtector());
            var settings = store.Load();
            using var first = store.LoadOrCreateIdentity(settings);

            var reloaded = store.Load();
            using var second = store.LoadOrCreateIdentity(reloaded);
            Assert.Equal(first.Fingerprint, second.Fingerprint);

            // 檔案內容經過保護，不是明文 JSON
            Assert.Equal(-1, File.ReadAllBytes(store.Path).AsSpan().IndexOf("HostKeyPkcs8"u8));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class LoginThrottleTests
{
    private static readonly IPAddress A = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress B = IPAddress.Parse("10.0.0.2");

    [Fact]
    public void ProgressiveDelay_DoublesUpTo60Seconds()
    {
        var time = new ManualTimeProvider();
        var throttle = new LoginThrottle(time, globalAttemptsPerSecond: 1000);
        var observed = new List<double>();
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(TimeSpan.Zero, throttle.TryBeginAttempt(A));
            throttle.RecordFailure(A);
            var wait = throttle.TryBeginAttempt(A);
            observed.Add(wait.TotalSeconds);
            time.Advance(wait);
        }
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], observed);
    }

    [Fact]
    public void OtherSource_NotAffected_AndSuccessResets()
    {
        var time = new ManualTimeProvider();
        var throttle = new LoginThrottle(time, globalAttemptsPerSecond: 1000);
        throttle.RecordFailure(A);
        Assert.True(throttle.TryBeginAttempt(A) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, throttle.TryBeginAttempt(B));

        throttle.RecordSuccess(A);
        Assert.Equal(TimeSpan.Zero, throttle.TryBeginAttempt(A));
    }

    [Fact]
    public void GlobalLimit_ThrottlesAcrossSources()
    {
        var time = new ManualTimeProvider();
        var throttle = new LoginThrottle(time, globalAttemptsPerSecond: 3);
        for (int i = 0; i < 3; i++)
            Assert.Equal(TimeSpan.Zero, throttle.TryBeginAttempt(IPAddress.Parse($"10.1.0.{i}")));
        Assert.True(throttle.TryBeginAttempt(IPAddress.Parse("10.1.0.9")) > TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, throttle.TryBeginAttempt(IPAddress.Parse("10.1.0.9")));
    }

    [Fact]
    public void IPv4MappedIPv6_TreatedAsSameSource()
    {
        var throttle = new LoginThrottle(new ManualTimeProvider());
        throttle.RecordFailure(A);
        Assert.True(throttle.TryBeginAttempt(A.MapToIPv6()) > TimeSpan.Zero);
    }
}

public class SessionTokenServiceTests
{
    private static readonly byte[] Nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private static TokenBinding Binding(ChannelRole role, long control = 1) => new("alex", 1, control, role);

    [Fact]
    public void Redeem_OneTimeOnly()
    {
        var svc = new SessionTokenService(new ManualTimeProvider());
        var token = svc.Issue(Binding(ChannelRole.Video));
        var proof = SubConnectProof.Compute(token, Nonce, ChannelRole.Video);

        Assert.NotNull(svc.Redeem(ChannelRole.Video, Nonce, proof));
        Assert.Null(svc.Redeem(ChannelRole.Video, Nonce, proof)); // 重放
    }

    [Fact]
    public void Redeem_CrossRole_Rejected()
    {
        var svc = new SessionTokenService(new ManualTimeProvider());
        var token = svc.Issue(Binding(ChannelRole.Video));
        Assert.Null(svc.Redeem(ChannelRole.File, Nonce, SubConnectProof.Compute(token, Nonce, ChannelRole.File)));
    }

    [Fact]
    public void Redeem_AfterExpiry_Rejected()
    {
        var time = new ManualTimeProvider();
        var svc = new SessionTokenService(time);
        var token = svc.Issue(Binding(ChannelRole.File));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(svc.Redeem(ChannelRole.File, Nonce, SubConnectProof.Compute(token, Nonce, ChannelRole.File)));
    }

    [Fact]
    public void RevokeAll_ForControlConnection()
    {
        var svc = new SessionTokenService(new ManualTimeProvider());
        var t1 = svc.Issue(Binding(ChannelRole.Video, control: 1));
        var t2 = svc.Issue(Binding(ChannelRole.Video, control: 2));
        svc.RevokeAll(1);
        Assert.Null(svc.Redeem(ChannelRole.Video, Nonce, SubConnectProof.Compute(t1, Nonce, ChannelRole.Video)));
        Assert.NotNull(svc.Redeem(ChannelRole.Video, Nonce, SubConnectProof.Compute(t2, Nonce, ChannelRole.Video)));
    }

    [Fact]
    public void NewToken_ReplacesOlderForSameRole()
    {
        var svc = new SessionTokenService(new ManualTimeProvider());
        var old = svc.Issue(Binding(ChannelRole.Video));
        svc.Issue(Binding(ChannelRole.Video));
        Assert.Equal(1, svc.OutstandingCount);
        Assert.Null(svc.Redeem(ChannelRole.Video, Nonce, SubConnectProof.Compute(old, Nonce, ChannelRole.Video)));
    }

    [Fact]
    public void Issue_ForControl_Throws()
        => Assert.Throws<ArgumentException>(() => new SessionTokenService().Issue(Binding(ChannelRole.Control)));
}

public class ControlGateTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ControlGate _gate;
    private static readonly GateConditions Good = new(DesktopSnapshot.Normal, true, true);

    public ControlGateTests() => _gate = new ControlGate(_time);

    private void ResumeFully()
    {
        _gate.Evaluate(Good);
        _time.Advance(TimeSpan.FromSeconds(1));
        var a = _gate.Evaluate(Good);
        Assert.Equal(GateActionKind.BeginResume, a.Kind);
        Assert.True(_gate.OnResumeAck(a.Generation));
    }

    [Fact]
    public void Initially_PausedNotStarted_RejectsInput()
    {
        Assert.Equal(GateState.Paused, _gate.State);
        Assert.Equal(PauseReason.NotStarted, _gate.Reason);
        Assert.False(_gate.AcceptsInput(_gate.Generation));
    }

    [Fact]
    public void Resume_RequiresOneSecondStable_ThenAck()
    {
        Assert.Equal(GateActionKind.None, _gate.Evaluate(Good).Kind);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(GateActionKind.None, _gate.Evaluate(Good).Kind);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var a = _gate.Evaluate(Good);
        Assert.Equal(GateActionKind.BeginResume, a.Kind);
        Assert.Equal(GateState.Resuming, _gate.State);
        Assert.False(_gate.AcceptsInput(a.Generation)); // 回覆前不接受

        Assert.False(_gate.OnResumeAck(a.Generation + 1));
        Assert.True(_gate.OnResumeAck(a.Generation));
        Assert.True(_gate.AcceptsInput(a.Generation));
    }

    [Fact]
    public void FlappingConditions_ResetStableTimer()
    {
        _gate.Evaluate(Good);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        _gate.Evaluate(Good with { Desktop = DesktopSnapshot.Normal with { Locked = true } });
        _time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(GateActionKind.None, _gate.Evaluate(Good).Kind);
    }

    [Fact]
    public void Lock_PausesAndIncrementsGeneration_OldInputRejected()
    {
        ResumeFully();
        uint g = _gate.Generation;
        var a = _gate.Evaluate(Good with { Desktop = DesktopSnapshot.Normal with { Locked = true } });
        Assert.Equal(GateActionKind.Pause, a.Kind);
        Assert.Equal(PauseReason.Locked, a.Reason);
        Assert.Equal(g + 1, a.Generation);
        Assert.False(_gate.AcceptsInput(g));
        Assert.False(_gate.AcceptsInput(g + 1));
    }

    [Fact]
    public void PauseDuringResuming_InvalidatesPendingAck()
    {
        _gate.Evaluate(Good);
        _time.Advance(TimeSpan.FromSeconds(1));
        var resume = _gate.Evaluate(Good);
        _gate.Evaluate(Good with { VideoConnected = false });
        Assert.False(_gate.OnResumeAck(resume.Generation));
        Assert.Equal(GateState.Paused, _gate.State);
    }

    [Fact]
    public void NonDefaultInputDesktop_WithoutReliableUacDetection_UsesGenericReason()
    {
        ResumeFully();
        var desk = DesktopSnapshot.Normal with { InputDesktopIsDefault = false };
        Assert.Equal(PauseReason.DesktopUnavailable, _gate.Evaluate(Good with { Desktop = desk }).Reason);
    }

    [Fact]
    public void NonDefaultInputDesktop_WithReliableUacDetection_UsesElevationReason()
    {
        ResumeFully();
        var desk = DesktopSnapshot.Normal with { InputDesktopIsDefault = false, ElevationPromptDetected = true };
        Assert.Equal(PauseReason.ElevationPrompt, _gate.Evaluate(Good with { Desktop = desk }).Reason);
    }

    [Fact]
    public void NotOnConsole_UsesGenericReason()
    {
        ResumeFully();
        var desk = DesktopSnapshot.Normal with { OnConsole = false };
        Assert.Equal(PauseReason.DesktopUnavailable, _gate.Evaluate(Good with { Desktop = desk }).Reason);
    }

    [Fact]
    public void CaptureFailure_AndVideoLost_Reasons()
    {
        ResumeFully();
        Assert.Equal(PauseReason.VideoLost, _gate.Evaluate(Good with { VideoConnected = false }).Reason);
        Assert.Equal(PauseReason.CaptureFailed, _gate.Evaluate(Good with { CaptureHealthy = false }).Reason);
    }
}
