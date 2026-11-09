using RemoteTool.HostGuard.Core;
using Xunit;

namespace RemoteTool.Tests.Guard;

internal sealed class FakeInjector : IInputInjector
{
    public readonly List<string> Calls = [];
    public bool FailDown { get; set; }
    public bool FailUp { get; set; }

    public bool Key(ushort scanCode, bool extended, bool down)
    {
        if (down ? FailDown : FailUp) return false;
        lock (Calls) Calls.Add($"key {scanCode:X2}{(extended ? "e" : "")} {(down ? "down" : "up")}");
        return true;
    }
    public bool MouseMove(ushort x, ushort y) { Calls.Add($"move {x},{y}"); return true; }
    public bool MouseButton(GuardMouseButton button, bool down)
    {
        if (down ? FailDown : FailUp) return false;
        lock (Calls) Calls.Add($"button {button} {(down ? "down" : "up")}");
        return true;
    }
    public bool MouseWheel(short delta, bool horizontal) { Calls.Add($"wheel {delta}"); return true; }
    public bool UnicodeText(string text) { Calls.Add($"text {text}"); return true; }
}

internal sealed class FakeProbe : IDesktopProbe
{
    public bool Controllable { get; set; } = true;
    public bool IsControllable() => Controllable;
}

public class InjectionEngineTests
{
    private readonly FakeInjector _injector = new();
    private readonly FakeProbe _probe = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InjectionEngine _engine;

    public InjectionEngineTests()
    {
        _engine = new InjectionEngine(_injector, _probe, time: _time);
        _engine.Handle(GuardMessage.Resume(1));
    }

    [Fact]
    public void BeforeFirstResume_DropsInput()
    {
        var engine = new InjectionEngine(_injector, _probe, time: _time);
        engine.Handle(GuardMessage.Key(0, 0x2A, false, true));
        Assert.Empty(_injector.Calls);
        Assert.Equal(1, engine.DroppedCount);
    }

    [Fact]
    public void KeyDown_SuccessAddsToTable_UpRemoves()
    {
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        Assert.Contains((ushort)0x2A, _engine.KeysDown.Select(k => k.Scan));
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, false));
        Assert.Empty(_engine.KeysDown);
        Assert.Equal(["key 2A down", "key 2A up"], _injector.Calls);
    }

    [Fact]
    public void KeyDown_InjectionFailure_NotRecorded()
    {
        _injector.FailDown = true;
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        Assert.Empty(_engine.KeysDown);
    }

    [Fact]
    public void KeyUp_ForKeyNotPressedByRemote_IsNotInjected()
    {
        // 本機使用者按住的鍵不應被遠端的「放開」影響
        _engine.Handle(GuardMessage.Key(1, 0x1D, false, false));
        Assert.Empty(_injector.Calls);
        Assert.Equal(1, _engine.DroppedCount);
    }

    [Fact]
    public void WrongGeneration_Dropped()
    {
        _engine.Handle(GuardMessage.Key(2, 0x2A, false, true));
        _engine.Handle(GuardMessage.Key(0, 0x2A, false, true));
        Assert.Empty(_injector.Calls);
        Assert.Equal(2, _engine.DroppedCount);
    }

    [Fact]
    public void Pause_ReleasesHeldKeysAndButtons_AndDropsOldGeneration()
    {
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        _engine.Handle(GuardMessage.Key(1, 0x1D, false, true));
        _engine.Handle(GuardMessage.MouseButtonEvent(1, GuardMouseButton.Left, true));
        _injector.Calls.Clear();

        _engine.Handle(GuardMessage.Pause(2));

        Assert.False(_engine.HasHeldInput);
        Assert.Contains("key 2A up", _injector.Calls);
        Assert.Contains("key 1D up", _injector.Calls);
        Assert.Contains("button Left up", _injector.Calls);

        _injector.Calls.Clear();
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true)); // 舊世代
        _engine.Handle(GuardMessage.Key(2, 0x2A, false, true)); // 新世代但尚未 Resume
        Assert.Empty(_injector.Calls);

        _engine.Handle(GuardMessage.Resume(2));
        _engine.Handle(GuardMessage.Key(2, 0x2A, false, true));
        Assert.Equal(["key 2A down"], _injector.Calls);
    }

    [Fact]
    public void StaleResume_Ignored()
    {
        _engine.Handle(GuardMessage.Pause(3));
        _engine.Handle(GuardMessage.Resume(2));
        Assert.True(_engine.IsPaused);
        Assert.Equal(3u, _engine.Generation);
    }

    [Fact]
    public void DesktopNotControllable_DropsAndReleases()
    {
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        _injector.Calls.Clear();
        _probe.Controllable = false;   // 例如鎖定或 UAC 安全桌面，即使 Host 尚未通知

        _engine.Handle(GuardMessage.Key(1, 0x1E, false, true));

        Assert.Equal(["key 2A up"], _injector.Calls);
        Assert.False(_engine.HasHeldInput);
    }

    [Fact]
    public void Tick_NoHeartbeatForOverOneSecond_WithKeysHeld_Releases()
    {
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        _time.Advance(TimeSpan.FromMilliseconds(900));
        _engine.Handle(GuardMessage.Heartbeat(1));
        _time.Advance(TimeSpan.FromMilliseconds(900));
        _engine.Tick();
        Assert.True(_engine.HasHeldInput);   // 心跳仍在

        _time.Advance(TimeSpan.FromMilliseconds(200));
        _engine.Tick();
        Assert.False(_engine.HasHeldInput);  // 1.1 秒無心跳 → 釋放
    }

    [Fact]
    public void Tick_NoKeysHeld_DoesNothing()
    {
        _time.Advance(TimeSpan.FromSeconds(10));
        _engine.Tick();
        Assert.Empty(_injector.Calls);
    }

    [Fact]
    public void ReleaseFailure_KeepsKeyForRetry()
    {
        _engine.Handle(GuardMessage.Key(1, 0x2A, false, true));
        _injector.FailUp = true;
        _engine.OnHostGone();
        Assert.True(_engine.HasHeldInput);

        _injector.FailUp = false;
        _engine.ReleaseAll("retry");
        Assert.False(_engine.HasHeldInput);
    }

    [Fact]
    public void OnHostGone_ReleasesAndPauses()
    {
        _engine.Handle(GuardMessage.MouseButtonEvent(1, GuardMouseButton.Left, true));
        _engine.OnHostGone();
        Assert.False(_engine.HasHeldInput);
        Assert.True(_engine.IsPaused);
    }
}

public class GuardLoopTests
{
    [Fact]
    public async Task HostGone_DropsQueuedEventsOfThatInstance_AndReleases()
    {
        var injector = new FakeInjector();
        var engine = new InjectionEngine(injector, new FakeProbe());
        await using var loop = new GuardLoop(engine, tickInterval: TimeSpan.FromHours(1));

        long instance = loop.BeginHostInstance();
        loop.Post(instance, GuardMessage.Resume(1));
        loop.Post(instance, GuardMessage.Key(1, 0x2A, false, true));
        await loop.FlushAsync();
        Assert.True(engine.HasHeldInput);

        loop.HostGone(instance);
        loop.Post(instance, GuardMessage.Key(1, 0x1E, false, true)); // 已結束實例的事件
        await loop.FlushAsync();

        Assert.False(engine.HasHeldInput);
        Assert.DoesNotContain("key 1E down", injector.Calls);

        // 新實例須重新 Resume
        long next = loop.BeginHostInstance();
        loop.Post(next, GuardMessage.Key(1, 0x1E, false, true));
        await loop.FlushAsync();
        Assert.DoesNotContain("key 1E down", injector.Calls);
    }
}

public class GuardCodecTests
{
    public static TheoryData<GuardMessage> AllMessages() =>
    [
        GuardMessage.Key(5, 0x2A, true, true),
        GuardMessage.MouseMove(5, 100, 65535),
        GuardMessage.MouseButtonEvent(5, GuardMouseButton.X2, false),
        GuardMessage.Wheel(5, -120, true),
        GuardMessage.Unicode(5, "注音 𠀀 abc"),
        GuardMessage.Heartbeat(5),
        GuardMessage.Pause(6),
        GuardMessage.Resume(6),
        GuardMessage.ReleaseAll(6),
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void RoundTrip(GuardMessage message)
    {
        var decoded = GuardCodec.Decode(GuardCodec.Encode(message), out var reason);
        Assert.Null(reason);
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void NonWhitelistedType_DroppedWithReason()
    {
        var bytes = GuardCodec.Encode(GuardMessage.Heartbeat(1));
        bytes[2] = 0x77;
        Assert.Null(GuardCodec.Decode(bytes, out var reason));
        Assert.Contains("白名單外", reason);
    }

    [Fact]
    public void WrongBodySize_Dropped()
    {
        var bytes = GuardCodec.Encode(GuardMessage.Heartbeat(1));
        bytes[2] = (byte)GuardMessageType.Key; // Key 需要 3 位元組 body
        Assert.Null(GuardCodec.Decode(bytes, out _));
    }

    [Fact]
    public async Task ReadFrame_OverMaxLength_Throws()
    {
        var header = new byte[] { 0xFF, 0xFF, 1, 0, 0, 0, 0 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuardCodec.ReadFrameAsync(new MemoryStream(header), CancellationToken.None));
    }
}

public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalExit_Stops()
        => Assert.Equal(RestartAction.Stop, new RestartPolicy().OnExit(0, T0).Action);

    [Fact]
    public void Crashes_BackoffThenGiveUpOnSixthWithinTenMinutes()
    {
        var policy = new RestartPolicy();
        var delays = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var d = policy.OnExit(unchecked((int)0xE0434352), T0.AddMinutes(i));
            Assert.Equal(RestartAction.Restart, d.Action);
            delays.Add(d.Delay.TotalSeconds);
        }
        Assert.Equal([1, 2, 4, 8, 16], delays);
        Assert.Equal(RestartAction.GiveUp, policy.OnExit(1, T0.AddMinutes(9)).Action);
    }

    [Fact]
    public void CrashesOutsideWindow_DoNotCount()
    {
        var policy = new RestartPolicy();
        for (int i = 0; i < 5; i++)
            policy.OnExit(1, T0.AddMinutes(i));
        // 第一次崩潰已超過 10 分鐘
        var d = policy.OnExit(1, T0.AddMinutes(10).AddSeconds(1));
        Assert.Equal(RestartAction.Restart, d.Action);
        Assert.Equal(5, d.CrashesInWindow);
    }
}

public class HostSupervisorTests
{
    private sealed class FakeProcess(int id, int exitCode, TimeSpan runFor) : IHostProcess
    {
        public int ProcessId => id;
        public async Task<int> WaitForExitAsync(CancellationToken ct) { await Task.Delay(runFor, ct); return exitCode; }
        public void Dispose() { }
    }

    private sealed class FakeLauncher(params int[] exitCodes) : IHostLauncher
    {
        public int Starts;
        public IHostProcess Start()
        {
            int i = Starts++;
            return new FakeProcess(1000 + i, exitCodes[Math.Min(i, exitCodes.Length - 1)], TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class ZeroDelayTime : TimeProvider
    {
        // 讓退避等待立即完成，測試只驗證流程
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }

    [Fact]
    public async Task CrashThenNormalExit_RestartsOnceThenStops()
    {
        var engine = new InjectionEngine(new FakeInjector(), new FakeProbe());
        await using var loop = new GuardLoop(engine, tickInterval: TimeSpan.FromHours(1));
        var launcher = new FakeLauncher(1, 0);
        var supervisor = new HostSupervisor(launcher, loop, time: new ZeroDelayTime());

        Assert.Equal(RestartAction.Stop, await supervisor.RunAsync(CancellationToken.None));
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(0, supervisor.CurrentProcessId);
    }

    [Fact]
    public async Task RepeatedCrashes_GiveUpAfterFiveRestarts()
    {
        var engine = new InjectionEngine(new FakeInjector(), new FakeProbe());
        await using var loop = new GuardLoop(engine, tickInterval: TimeSpan.FromHours(1));
        var launcher = new FakeLauncher(-1);
        var supervisor = new HostSupervisor(launcher, loop, time: new ZeroDelayTime());

        Assert.Equal(RestartAction.GiveUp, await supervisor.RunAsync(CancellationToken.None));
        Assert.Equal(6, launcher.Starts); // 首次啟動 + 5 次重啟
    }
}

public class GuardLoopNewInstanceTests
{
    [Fact]
    public async Task NewHostInstance_ResetsGeneration_SoItsFirstResumeIsAccepted()
    {
        var injector = new FakeInjector();
        var engine = new InjectionEngine(injector, new FakeProbe());
        await using var loop = new GuardLoop(engine, tickInterval: TimeSpan.FromHours(1));

        long first = loop.BeginHostInstance();
        loop.Post(first, GuardMessage.Resume(7));
        loop.Post(first, GuardMessage.Pause(8));
        loop.HostGone(first);

        long second = loop.BeginHostInstance();   // 重啟後的 Host 從世代 1 開始
        loop.Post(second, GuardMessage.Pause(1));
        loop.Post(second, GuardMessage.Resume(1));
        loop.Post(second, GuardMessage.Key(1, 0x2A, false, true));
        await loop.FlushAsync();

        Assert.Contains("key 2A down", injector.Calls);
    }
}
