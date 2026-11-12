using System.Diagnostics;
using System.Net;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;
using Xunit;
using Xunit.Abstractions;

namespace RemoteTool.Video.Tests;

/// <summary>
/// 長時間串流的記憶體檢查（V-12）。畫面每張都整張改變（相當於單機「畫面中的畫面」的最壞情況），
/// 量測暖機後與結束時的受控堆積（強制 GC 後），兩者差距須在門檻內。
/// </summary>
public class SoakTests(ITestOutputHelper output)
{
    /// <summary>每次呼叫都回傳不同內容的整張畫面。</summary>
    private sealed class ChangingCapture(int width, int height) : IScreenCapture
    {
        private int _n;
        public CaptureStatus Capture(TimeSpan timeout, FrameBuffer target)
        {
            target.Resize(width, height);
            int n = Interlocked.Increment(ref _n);
            var px = target.Pixels;
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)(i * 7 + n); px[i + 1] = (byte)(i >> 9); px[i + 2] = (byte)(n * 3); px[i + 3] = 255;
            }
            return CaptureStatus.NewFrame;
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task ContinuousFullFrameChanges_ManagedMemoryStaysFlat()
    {
        const string user = "u", pass = "password123";
        await using var server = new HostServer(new HostServerOptions
        {
            Certificate = HostCertificate.CreateEphemeral(),
            Credentials = PasswordHasher.Create(user, pass, new Argon2Parameters(256, 1, 1), allowWeakParameters: true),
            AllowWeakCredentialParameters = true,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            SubChannelHandler = ctx => ctx.Channel == ChannelKind.Video
                ? VideoStreamer.RunAsync(new ChangingCapture(1280, 720), ctx.Stream, ctx.Incoming,
                    new VideoStreamerOptions { MaxFps = 30 }, ctx.Token)
                : Task.Delay(Timeout.Infinite, ctx.Token),
        });
        server.Start();

        var canvas = new FrameCanvas();
        await using var session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
        {
            Host = "127.0.0.1", Port = server.Port, Username = user, Password = pass,
            KnownHosts = new KnownHostsStore(null), ConfirmNewHost = (_, _) => true,
            VideoFrameReceived = f => canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(f), out _),
        });

        // 不強制 GC 的工作集取樣（觀察是否持平），之後才做強制 GC 的受控堆積比較
        var samples = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(2000);
            samples.Add(Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024);
        }
        output.WriteLine("工作集取樣（每 2 秒，MB）：" + string.Join(", ", samples));

        long baseline = Measure(out int f0);
        await Task.Delay(12000);
        long end = Measure(out int f1);

        int frames = f1 - f0;
        long growth = end - baseline;
        output.WriteLine($"12 秒 {frames} 張；受控堆積 {baseline / 1024 / 1024} MB → {end / 1024 / 1024} MB（差 {growth / 1024} KB）；" +
                         $"GC gen0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}；" +
                         $"工作集 {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB");
        Assert.True(frames > 50, "串流太慢，測試無效");
        Assert.InRange(growth, long.MinValue, 16L * 1024 * 1024);

        long Measure(out int completed)
        {
            completed = canvas.CompletedFrames;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true);
        }
    }
}
