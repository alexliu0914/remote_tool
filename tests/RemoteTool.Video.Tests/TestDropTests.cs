using System.Net;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;
using Xunit;
using Xunit.Abstractions;

namespace RemoteTool.Video.Tests;

/// <summary>重現 Host.exe 的 --test-drop-video：影像串流以計時器取消後，控制端應自動重連（R-21）。</summary>
public class TestDropTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TimedVideoDrop_LikeHostTestSwitch_TriggersReconnect()
    {
        var logs = new List<string>();
        await using var server = new HostServer(new HostServerOptions
        {
            Certificate = HostCertificate.CreateEphemeral(),
            Credentials = PasswordHasher.Create("u", "password123", new Argon2Parameters(256, 1, 1), allowWeakParameters: true),
            AllowWeakCredentialParameters = true,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            Log = m => { lock (logs) logs.Add(m); },
            SubChannelHandler = async ctx =>
            {
                // 與 Host Program.HandleSubChannelAsync 相同的寫法
                using var dropCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
                dropCts.CancelAfter(TimeSpan.FromSeconds(1));
                var scoped = new SubChannelContext(ctx.Channel, ctx.Stream, ctx.Incoming, ctx.Remote, dropCts.Token);
                var capture = new FakeCapture();
                capture.Enqueue(TestFrames.Gradient(64, 64));
                try
                {
                    if (ctx.Channel == ChannelKind.Video)
                        await VideoStreamer.RunAsync(capture, scoped.Stream, scoped.Incoming, new VideoStreamerOptions(), scoped.Token);
                    else
                        await Task.Delay(Timeout.Infinite, dropCts.Token);
                }
                catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested)
                {
                    lock (logs) logs.Add($"[測試] 關閉 {ctx.Channel}");
                }
            },
        });
        server.Start();

        var canvas = new FrameCanvas();
        var events = new List<string>();
        await using var session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
        {
            Host = "127.0.0.1", Port = server.Port, Username = "u", Password = "password123",
            KnownHosts = new KnownHostsStore(null), ConfirmNewHost = (_, _) => true,
            VideoFrameReceived = f => canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(f), out _),
        });
        session.SubChannelStateChanged += (r, s, d) => { lock (events) events.Add($"{DateTime.Now:ss.fff} {r} {s} {d}"); };

        var end = Environment.TickCount64 + 8000;
        while (session.ReconnectCount < 4 && Environment.TickCount64 < end) await Task.Delay(50);

        lock (events) foreach (var e in events) output.WriteLine(e);
        lock (logs) foreach (var l in logs) output.WriteLine("HOST " + l);
        Assert.True(session.ReconnectCount >= 4, $"重連次數 {session.ReconnectCount}");
        Assert.True(session.GetReconnectCount(ChannelKind.Video) >= 2);
        Assert.True(session.GetReconnectCount(ChannelKind.File) >= 2);
        Assert.False(session.Completion.IsCompleted);
    }
}
