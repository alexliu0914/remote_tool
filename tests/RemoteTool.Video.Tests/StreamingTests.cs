using System.Net;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;
using Xunit;

namespace RemoteTool.Video.Tests;

/// <summary>假擷取來源：依序回傳排好的畫面，之後回報沒有變化。</summary>
internal sealed class FakeCapture : IScreenCapture
{
    private readonly Queue<(CaptureStatus Status, FrameBuffer? Frame)> _script = new();
    private readonly object _gate = new();
    public int Calls;
    public bool Disposed;

    public void Enqueue(FrameBuffer frame) { lock (_gate) _script.Enqueue((CaptureStatus.NewFrame, frame)); }
    public void EnqueueUnavailable() { lock (_gate) _script.Enqueue((CaptureStatus.Unavailable, null)); }

    public CaptureStatus Capture(TimeSpan timeout, FrameBuffer target)
    {
        Interlocked.Increment(ref Calls);
        (CaptureStatus, FrameBuffer?) next;
        lock (_gate)
        {
            if (_script.Count == 0)
            {
                Thread.Sleep(Math.Min(20, (int)timeout.TotalMilliseconds));
                return CaptureStatus.NoChange;
            }
            next = _script.Dequeue();
        }
        next.Item2?.CopyTo(target);
        return next.Item1;
    }

    public void Dispose() => Disposed = true;
}

public class StreamingTests
{
    private const string User = "u", Pass = "password123";

    private static (HostServer Server, FakeCapture Capture) StartHost(VideoStreamerOptions? options = null)
    {
        var capture = new FakeCapture();
        var server = new HostServer(new HostServerOptions
        {
            Certificate = HostCertificate.CreateEphemeral(),
            Credentials = PasswordHasher.Create(User, Pass, new Argon2Parameters(256, 1, 1), allowWeakParameters: true),
            AllowWeakCredentialParameters = true,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            SubChannelHandler = ctx => ctx.Channel == ChannelKind.Video
                ? VideoStreamer.RunAsync(capture, ctx.Stream, ctx.Incoming,
                    options ?? new VideoStreamerOptions { MaxFps = 30, UnavailableRetry = TimeSpan.FromMilliseconds(50) }, ctx.Token)
                : Task.Delay(Timeout.Infinite, ctx.Token),
        });
        server.Start();
        return (server, capture);
    }

    private static Task<ControllerSession> Connect(HostServer server, FrameCanvas canvas, Action<PixelRect>? onFrame = null)
        => ControllerClient.ConnectAsync(new ControllerConnectOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Username = User,
            Password = Pass,
            KnownHosts = new KnownHostsStore(null),
            ConfirmNewHost = (_, _) => true,
            VideoFrameReceived = f =>
            {
                if (canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(f), out var dirty)) onFrame?.Invoke(dirty);
            },
        });

    private static async Task WaitFor(Func<bool> cond, int ms = 10_000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static bool Matches(FrameCanvas canvas, FrameBuffer expected)
    {
        lock (canvas.SyncRoot)
            return canvas.Buffer.Width == expected.Width && canvas.Buffer.Pixels.AsSpan().SequenceEqual(expected.Pixels);
    }

    [Fact]
    public async Task Frames_FlowFromHostToController_PixelExact()
    {
        var (server, capture) = StartHost();
        await using var _ = server;
        var canvas = new FrameCanvas();

        var first = TestFrames.Gradient(1280, 720);
        capture.Enqueue(first);
        await using var session = await Connect(server, canvas);
        await WaitFor(() => Matches(canvas, first));

        var second = TestFrames.Gradient(1280, 720);
        TestFrames.Fill(second, 500, 300, 200, 100, 0x33);
        capture.Enqueue(second);
        await WaitFor(() => Matches(canvas, second));
    }

    [Fact]
    public async Task WideScreen_IsDownscaled()
    {
        var (server, capture) = StartHost();
        await using var _ = server;
        var canvas = new FrameCanvas();
        capture.Enqueue(TestFrames.Gradient(3840, 2160));
        await using var session = await Connect(server, canvas);
        await WaitFor(() => canvas.CompletedFrames > 0);
        lock (canvas.SyncRoot) Assert.Equal((1920, 1080), (canvas.Buffer.Width, canvas.Buffer.Height));
    }

    [Fact]
    public async Task KeyframeRequest_ResendsWholeFrame()
    {
        var (server, capture) = StartHost();
        await using var _ = server;
        var canvas = new FrameCanvas();
        var dirties = new List<PixelRect>();
        capture.Enqueue(TestFrames.Gradient(640, 480));
        await using var session = await Connect(server, canvas, d => { lock (dirties) dirties.Add(d); });
        await WaitFor(() => canvas.CompletedFrames == 1);

        await session.RequestKeyframeAsync(canvas.LastFrameId);
        await WaitFor(() => canvas.CompletedFrames == 2);
        lock (dirties) Assert.Equal(new PixelRect(0, 0, 640, 480), dirties[1]);
    }

    [Fact]
    public async Task CaptureUnavailable_ThenRecovers_WithFullFrame()
    {
        var (server, capture) = StartHost();
        await using var _ = server;
        var canvas = new FrameCanvas();
        var dirties = new List<PixelRect>();
        var a = TestFrames.Gradient(256, 256);
        capture.Enqueue(a);
        await using var session = await Connect(server, canvas, d => { lock (dirties) dirties.Add(d); });
        await WaitFor(() => canvas.CompletedFrames == 1);

        capture.EnqueueUnavailable();
        var b = TestFrames.Gradient(256, 256);
        TestFrames.Fill(b, 0, 0, 8, 8, 1); // 只改一小塊，但恢復後應送完整畫面
        capture.Enqueue(b);
        await WaitFor(() => canvas.CompletedFrames == 2);
        Assert.True(Matches(canvas, b));
        lock (dirties) Assert.Equal(new PixelRect(0, 0, 256, 256), dirties[1]);
    }

    [Fact]
    public async Task FpsCap_IsRespected()
    {
        var (server, capture) = StartHost(new VideoStreamerOptions { MaxFps = 5 });
        await using var _ = server;
        var canvas = new FrameCanvas();
        for (int i = 0; i < 100; i++) capture.Enqueue(TestFrames.Gradient(64, 64, shift: i));
        await using var session = await Connect(server, canvas);

        await Task.Delay(2000);
        // 5 FPS × 2 秒 ≈ 10 張，容許誤差
        Assert.InRange(canvas.CompletedFrames, 5, 14);
    }

    [Fact]
    public async Task ControllerDisconnect_StopsStreamer()
    {
        var (server, capture) = StartHost();
        await using var _ = server;
        var canvas = new FrameCanvas();
        capture.Enqueue(TestFrames.Gradient(64, 64));
        var session = await Connect(server, canvas);
        await WaitFor(() => canvas.CompletedFrames == 1);
        await session.DisposeAsync();
        await WaitFor(() => !server.HasActiveSession);

        int calls = Volatile.Read(ref capture.Calls);
        await Task.Delay(300);
        Assert.InRange(Volatile.Read(ref capture.Calls) - calls, 0, 1);
    }
}
