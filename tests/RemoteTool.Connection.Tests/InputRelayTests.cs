using RemoteTool.Connection;
using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Connection.Tests;

public class InputRelayTests
{
    private sealed class Sink : IInputSink
    {
        public readonly List<GuardRecord> Records = new();
        public void Send(GuardRecord record) { lock (Records) Records.Add(record); }
    }

    // 主螢幕 1920×1080 在 (0,0)；右邊另一個 1920×1080（虛擬桌面 3840×1080）
    private static readonly ScreenGeometry TwoMonitors = new(0, 0, 1920, 1080, 0, 0, 3840, 1080);

    private static Frame F<T>(T m) where T : IMessage => new(T.Type, 0, 1, MessageCodec.Serialize(m));

    [Fact]
    public void Map_PrimaryOnLeftOfTwoMonitors()
    {
        var relay = new InputRelay(new Sink(), () => TwoMonitors);
        Assert.True(relay.Map(0, 0, out int x0, out int y0));
        Assert.Equal((0, 0), (x0, y0));
        Assert.True(relay.Map(65535, 65535, out int x1, out int y1));
        Assert.InRange(x1, 32750, 32780);  // 主螢幕右緣約在虛擬桌面一半
        Assert.Equal(65535, y1);
    }

    [Fact]
    public void Map_OutputWithOffset()
    {
        // 擷取的是右邊那個螢幕
        var relay = new InputRelay(new Sink(), () => new ScreenGeometry(1920, 0, 1920, 1080, 0, 0, 3840, 1080));
        Assert.True(relay.Map(0, 32768, out int x, out _));
        Assert.InRange(x, 32760, 32780);
    }

    [Fact]
    public void NoGeometry_MouseDropped_KeysStillForwarded()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => null);
        relay.Handle(F(new MouseMoveEvent(1, 100, 100)));
        relay.Handle(F(new KeyEvent(1, 0x1E, false, true)));
        Assert.Single(sink.Records);
        Assert.Equal(GuardRecordType.Key, sink.Records[0].Type);
        Assert.Equal(1, relay.Dropped);
    }

    [Fact]
    public void WrongGeneration_IsDropped()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => TwoMonitors) { Generation = 5 };
        relay.Handle(F(new KeyEvent(4, 0x1E, false, true)));
        Assert.Empty(sink.Records);
        relay.Handle(F(new KeyEvent(5, 0x1E, false, true)));
        Assert.Single(sink.Records);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x200)]
    public void InvalidScanCode_IsDropped(int scan)
    {
        var sink = new Sink();
        new InputRelay(sink, () => TwoMonitors).Handle(F(new KeyEvent(1, (ushort)scan, false, true)));
        Assert.Empty(sink.Records);
    }

    [Fact]
    public void OutOfRangeCoordinates_AndBadButtons_AreDropped()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => TwoMonitors);
        relay.Handle(F(new MouseMoveEvent(1, -1, 0)));
        relay.Handle(F(new MouseMoveEvent(1, 0, 70000)));
        relay.Handle(F(new MouseButtonEvent(1, (MouseButton)9, true, 0, 0)));
        relay.Handle(F(new MouseWheelEvent(1, 100_000, false, 0, 0)));
        Assert.Empty(sink.Records);
    }

    [Fact]
    public void ReleaseAll_IsForwardedRegardlessOfGeneration()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => TwoMonitors) { Generation = 7 };
        relay.Handle(F(new ReleaseAllInput(1)));
        Assert.Equal(GuardRecordType.ReleaseAll, Assert.Single(sink.Records).Type);
    }

    [Fact]
    public void NonInputMessage_IsNotHandled()
        => Assert.False(new InputRelay(new Sink(), () => TwoMonitors).Handle(F(new Heartbeat(1))));

    // ---- 經 TLS 的端對端：控制端 SendInput → 被控端 InputRelay ----

    [Fact]
    public async Task ControllerInput_ArrivesInOrder_AndSessionEndReleasesAll()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => TwoMonitors);
        await using var host = new TestHost(inputHandler: relay.Handle, sessionEnded: relay.ReleaseAll);
        var session = await ControllerClient.ConnectAsync(host.Options());

        for (int i = 0; i < 20; i++)
        {
            session.SendInput(new KeyEvent(1, (ushort)(0x10 + i), false, true));
            session.SendInput(new KeyEvent(1, (ushort)(0x10 + i), false, false));
        }
        session.SendInput(new MouseButtonEvent(1, MouseButton.Left, true, 100, 100));
        session.SendInput(new MouseButtonEvent(1, MouseButton.Left, false, 100, 100));

        await TestHost.WaitUntilAsync(() => { lock (sink.Records) return sink.Records.Count >= 42; });
        lock (sink.Records)
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal((ushort)(0x10 + i), sink.Records[i * 2].Value16);
                Assert.True(sink.Records[i * 2].Down);
                Assert.False(sink.Records[i * 2 + 1].Down);
            }
            Assert.Equal(GuardRecordType.MouseButton, sink.Records[40].Type);
        }

        await session.DisposeAsync();
        await TestHost.WaitUntilAsync(() => { lock (sink.Records) return sink.Records.Any(r => r.Type == GuardRecordType.ReleaseAll); });
    }

    [Fact]
    public async Task ManyMouseMoves_AreCoalesced_ButKeyOrderKept()
    {
        var sink = new Sink();
        var relay = new InputRelay(sink, () => TwoMonitors);
        await using var host = new TestHost(inputHandler: relay.Handle);
        await using var session = await ControllerClient.ConnectAsync(host.Options());

        for (int i = 0; i < 5000; i++) session.SendInput(new MouseMoveEvent(1, i % 65535, 100));
        session.SendInput(new KeyEvent(1, 0x1E, false, true));

        await TestHost.WaitUntilAsync(() => { lock (sink.Records) return sink.Records.Any(r => r.Type == GuardRecordType.Key); });
        await Task.Delay(200);
        lock (sink.Records)
        {
            int moves = sink.Records.Count(r => r.Type == GuardRecordType.MouseMove);
            Assert.InRange(moves, 1, 4999); // 有合併
            Assert.Equal(GuardRecordType.Key, sink.Records[^1].Type); // 最後一個移動在按鍵之前
        }
    }
}
