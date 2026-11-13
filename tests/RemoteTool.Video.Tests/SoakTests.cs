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

    /// <summary>H.264 路徑（借用緩衝區、分塊切片、接收端組裝）長時間執行，強制 GC 後受控堆積須持平（HW-06）。</summary>
    [Fact]
    public async Task H264Path_PooledBuffers_ManagedMemoryStaysFlat()
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
                    new VideoStreamerOptions { MaxFps = 60, EncoderFactory = (w, h) => new PooledFakeEncoder(w, h) },
                    ctx.Token, clientSupportsH264: true)
                : Task.Delay(Timeout.Infinite, ctx.Token),
        });
        server.Start();

        var receiver = new VideoReceiver(new FrameCanvas(), () => new SizeOnlyDecoder());
        await using var session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
        {
            Host = "127.0.0.1", Port = server.Port, Username = user, Password = pass,
            KnownHosts = new KnownHostsStore(null), ConfirmNewHost = (_, _) => true,
            VideoCapabilities = [ProtocolInfo.CapabilityVideoH264, ProtocolInfo.CapabilityVideoTiles],
            VideoFrameReceived = f => receiver.Handle(f, out _, out _, out _),
        });

        await Task.Delay(4000);
        long baseline = FullGc(), f0 = receiver.Canvas.CompletedFrames;
        await Task.Delay(15000);
        long end = FullGc(), frames = receiver.Canvas.CompletedFrames - f0;
        output.WriteLine($"H.264 路徑 15 秒 {frames} 張；強制 GC 後受控堆積 {baseline / 1024} KB → {end / 1024} KB（差 {(end - baseline) / 1024} KB）");
        Assert.True(frames > 200, "串流太慢，測試無效");
        Assert.InRange(end - baseline, long.MinValue, 8L * 1024 * 1024);

        static long FullGc()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return GC.GetTotalMemory(forceFullCollection: true);
        }
    }

    /// <summary>假編碼器：每張輸出 20～300 KB 的借用緩衝區（模擬 P 幀與關鍵幀大小）。</summary>
    private sealed class PooledFakeEncoder(int w, int h) : IVideoEncoder
    {
        private int _n;
        public string Name => "假 H.264（借用緩衝區）";
        public int Width => w;
        public int Height => h;
        public EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe)
        {
            int len = (++_n % 30 == 0 || forceKeyframe) ? 300_000 : 20_000 + (_n % 7) * 5_000;
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
            buf.AsSpan(0, len).Fill((byte)_n);
            return new EncodedFrame(buf, len, forceKeyframe, Pooled: true);
        }
        public void Dispose() { }
    }

    /// <summary>假解碼器：只輸出指定大小的畫面，不解析內容。</summary>
    private sealed class SizeOnlyDecoder : IVideoDecoder
    {
        public string Name => "假解碼";
        public bool Decode(byte[] data, int width, int height, FrameBuffer target)
        {
            target.Resize(width, height);
            return true;
        }
        public void Dispose() { }
    }
}
