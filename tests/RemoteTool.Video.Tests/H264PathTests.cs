using System.Net;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;
using Xunit;

namespace RemoteTool.Video.Tests;

/// <summary>以假編解碼器（直接傳原始像素）驗證 A2 的 H.264 傳輸路徑：協商、分塊、關鍵幀、備援。</summary>
public class H264PathTests
{
    /// <summary>假編碼器：輸出 = 原始 BGRA；可設定在第 N 次呼叫時拋例外。</summary>
    private sealed class FakeEncoder(int w, int h, int failAt = -1) : IVideoEncoder
    {
        private int _calls;
        public int Keyframes;
        public string Name => "假 H.264";
        public int Width => w;
        public int Height => h;
        public EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe)
        {
            if (++_calls == failAt) throw new InvalidOperationException("模擬編碼器故障");
            if (forceKeyframe) Keyframes++;
            return new EncodedFrame(frame.Pixels.ToArray(), forceKeyframe);
        }
        public void Dispose() { }
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        public string Name => "假 H.264 解碼";
        public bool Decode(byte[] data, int width, int height, FrameBuffer target)
        {
            target.Resize(width, height);
            data.AsSpan(0, width * height * 4).CopyTo(target.Pixels);
            return true;
        }
        public void Dispose() { }
    }

    private static async Task<(HostServer Server, FakeCapture Capture, List<FakeEncoder> Encoders, List<string> Logs)> StartHost(int failAt = -1)
    {
        var capture = new FakeCapture();
        var encoders = new List<FakeEncoder>();
        var logs = new List<string>();
        var server = new HostServer(new HostServerOptions
        {
            Certificate = HostCertificate.CreateEphemeral(),
            Credentials = PasswordHasher.Create("u", "password123", new Argon2Parameters(256, 1, 1), allowWeakParameters: true),
            AllowWeakCredentialParameters = true,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            SubChannelHandler = ctx => ctx.Channel == ChannelKind.Video
                ? VideoStreamer.RunAsync(capture, ctx.Stream, ctx.Incoming, new VideoStreamerOptions
                {
                    MaxFps = 30,
                    Log = m => { lock (logs) logs.Add(m); },
                    EncoderFactory = (w, h) => { var e = new FakeEncoder(w, h, failAt); lock (encoders) encoders.Add(e); return e; },
                }, ctx.Token, ctx.ClientCapabilities.Contains(ProtocolInfo.CapabilityVideoH264))
                : Task.Delay(Timeout.Infinite, ctx.Token),
        });
        server.Start();
        await Task.Yield();
        return (server, capture, encoders, logs);
    }

    private static Task<ControllerSession> Connect(HostServer server, VideoReceiver receiver, bool h264)
    {
        var t = ControllerClient.ConnectAsync(new ControllerConnectOptions
        {
            Host = "127.0.0.1", Port = server.Port, Username = "u", Password = "password123",
            KnownHosts = new KnownHostsStore(null), ConfirmNewHost = (_, _) => true,
            VideoCapabilities = h264
                ? [ProtocolInfo.CapabilityVideoH264, ProtocolInfo.CapabilityVideoTiles]
                : [ProtocolInfo.CapabilityVideoTiles],
            VideoFrameReceived = f => receiver.Handle(f, out _, out _, out _),
        });
        return t;
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 10_000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static bool Matches(FrameCanvas c, FrameBuffer f)
    {
        lock (c.SyncRoot) return c.Buffer.Width == f.Width && c.Buffer.Height == f.Height && c.Buffer.Pixels.AsSpan().SequenceEqual(f.Pixels);
    }

    [Fact]
    public async Task ClientWithH264_ReceivesVideoFrames_Chunked_PixelExact()
    {
        var (server, capture, encoders, _) = await StartHost();
        await using var _ = server;
        var receiver = new VideoReceiver(new FrameCanvas(), () => new FakeDecoder());
        var frame = TestFrames.Noise(1280, 720, 3);       // 3.6 MB → 8 塊
        capture.Enqueue(frame);
        await using var session = await Connect(server, receiver, h264: true);

        await WaitFor(() => Matches(receiver.Canvas, frame));
        Assert.Equal("假 H.264 解碼", receiver.CurrentFormat);
        Assert.True(receiver.Canvas.LastCompletedWasKeyframe);
        Assert.Single(encoders);
    }

    [Fact]
    public async Task ClientWithoutH264_GetsTiles()
    {
        var (server, capture, encoders, _) = await StartHost();
        await using var _ = server;
        var receiver = new VideoReceiver(new FrameCanvas(), null);
        var frame = TestFrames.Gradient(320, 240);
        capture.Enqueue(frame);
        await using var session = await Connect(server, receiver, h264: false);

        await WaitFor(() => Matches(receiver.Canvas, frame));
        Assert.Equal("區塊差異（A1）", receiver.CurrentFormat);
        Assert.Empty(encoders);
    }

    [Fact]
    public async Task EncoderFailure_FallsBackToTiles_WithFullFrame()
    {
        var (server, capture, _, logs) = await StartHost(failAt: 2);
        await using var _ = server;
        var receiver = new VideoReceiver(new FrameCanvas(), () => new FakeDecoder());
        var a = TestFrames.Gradient(320, 240);
        capture.Enqueue(a);
        await using var session = await Connect(server, receiver, h264: true);
        await WaitFor(() => Matches(receiver.Canvas, a));

        var b = TestFrames.Gradient(320, 240, shift: 7);
        capture.Enqueue(b); // 第 2 次編碼故障 → 改用區塊差異，整張送出
        await WaitFor(() => Matches(receiver.Canvas, b));
        Assert.Equal("區塊差異（A1）", receiver.CurrentFormat);
        lock (logs) Assert.Contains(logs, l => l.Contains("改用區塊差異"));
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task KeyframeRequest_WithoutNewFrame_ReencodesLastAsKeyframe()
    {
        var (server, capture, encoders, _) = await StartHost();
        await using var _ = server;
        var receiver = new VideoReceiver(new FrameCanvas(), () => new FakeDecoder());
        capture.Enqueue(TestFrames.Gradient(320, 240));
        await using var session = await Connect(server, receiver, h264: true);
        await WaitFor(() => receiver.Canvas.CompletedFrames == 1);

        await session.RequestKeyframeAsync(receiver.Canvas.LastFrameId);
        await WaitFor(() => receiver.Canvas.CompletedFrames == 2);
        Assert.True(receiver.Canvas.LastCompletedWasKeyframe);
        Assert.Equal(2, encoders[0].Keyframes);
    }

    [Fact]
    public async Task OddSizedScreen_IsCroppedToEven()
    {
        var (server, capture, encoders, _) = await StartHost();
        await using var _ = server;
        var receiver = new VideoReceiver(new FrameCanvas(), () => new FakeDecoder());
        capture.Enqueue(TestFrames.Gradient(321, 241));
        await using var session = await Connect(server, receiver, h264: true);
        await WaitFor(() => receiver.Canvas.CompletedFrames == 1);
        Assert.Equal((320, 240), (encoders[0].Width, encoders[0].Height));
    }
}

public class ColorAndAssemblerTests
{
    [Fact]
    public void BgraToNv12_ThenBack_IsClose()
    {
        var src = TestFrames.Gradient(64, 48);
        var nv12 = new byte[ColorConverter.Nv12Size(64, 48)];
        ColorConverter.BgraToNv12(src, nv12);
        var back = new FrameBuffer();
        ColorConverter.Nv12ToBgra(nv12, 64, 48, 64, 48, back);

        // 色度 2×2 取樣有損，平滑漸層的誤差應很小
        int maxDiff = 0;
        for (int i = 0; i < src.Pixels.Length; i += 4)
            for (int c = 0; c < 3; c++)
                maxDiff = Math.Max(maxDiff, Math.Abs(src.Pixels[i + c] - back.Pixels[i + c]));
        Assert.InRange(maxDiff, 0, 40);
    }

    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]        // 黑
    [InlineData(255, 255, 255, 235, 128, 128)] // 白
    public void KnownColors_MapToLimitedRange(byte r, byte g, byte b, int y, int u, int v)
    {
        var f = new FrameBuffer(2, 2);
        for (int i = 0; i < 16; i += 4) { f.Pixels[i] = b; f.Pixels[i + 1] = g; f.Pixels[i + 2] = r; f.Pixels[i + 3] = 255; }
        var nv12 = new byte[6];
        ColorConverter.BgraToNv12(f, nv12);
        Assert.InRange(nv12[0], y - 1, y + 1);
        Assert.InRange(nv12[4], u - 1, u + 1);
        Assert.InRange(nv12[5], v - 1, v + 1);
    }

    [Fact]
    public void Nv12ToBgra_HandlesStrideAndPaddedRows()
    {
        // 解碼器常見輸出：stride 64、高度補到 16 的倍數
        int w = 50, h = 30, stride = 64, rows = 32;
        var nv12 = new byte[stride * rows + stride * (h / 2)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) nv12[y * stride + x] = 235;
        for (int y = 0; y < h / 2; y++) for (int x = 0; x < w; x++) nv12[stride * rows + y * stride + x] = 128;
        var dst = new FrameBuffer();
        ColorConverter.Nv12ToBgra(nv12, stride, rows, w, h, dst);
        Assert.Equal((50, 30), (dst.Width, dst.Height));
        Assert.All(dst.Pixels.Where((_, i) => i % 4 != 3), p => Assert.InRange(p, 250, 255)); // 白
    }

    [Fact]
    public void OddDimensions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => ColorConverter.BgraToNv12(new FrameBuffer(3, 2), new byte[100]));
        Assert.Throws<ArgumentException>(() => ColorConverter.Nv12ToBgra(new byte[100], 4, 2, 3, 2, new FrameBuffer()));
    }

    private static VideoFrame Chunk(long id, int idx, int count, int len = 10, int w = 64, int h = 64)
        => new(id, w, h, idx == 0, idx, count, new byte[len], "h264");

    [Fact]
    public void Assembler_JoinsChunksInOrder()
    {
        var a = new VideoFrameAssembler();
        var frame = new EncodedFrame(Enumerable.Range(0, 1_200_000).Select(i => (byte)i).ToArray(), true);
        var parts = VideoFrameAssembler.Split(5, 64, 64, frame, "h264");
        Assert.Equal(3, parts.Count);
        Assert.False(a.Add(parts[0], out _, out _));
        Assert.False(a.Add(parts[1], out _, out _));
        Assert.True(a.Add(parts[2], out var first, out var data));
        Assert.Equal(frame.Data.ToArray(), data);
        Assert.True(first!.IsKeyframe);
    }

    [Fact]
    public void Split_SlicesWithoutCopying_AndRespectsLength()
    {
        var buffer = new byte[2_000_000];
        var frame = new EncodedFrame(buffer, 1_100_000, false, Pooled: true); // 借來的緩衝區比實際資料長
        var parts = VideoFrameAssembler.Split(1, 64, 64, frame, "h264");
        Assert.Equal(3, parts.Count);
        Assert.Equal(1_100_000, parts.Sum(p => p.Data.Length));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(parts[1].Data, out var seg));
        Assert.Same(buffer, seg.Array); // 同一個陣列，未複製
    }

    [Fact]
    public void Assembler_CopiesChunkData_SoReusedReadBuffersAreSafe()
    {
        var shared = new byte[100];
        var a = new VideoFrameAssembler();
        a.Add(new VideoFrame(1, 64, 64, true, 0, 2, shared.AsMemory(0, 50), "h264"), out _, out _);
        shared.AsSpan().Fill(9); // 模擬讀取緩衝區被下一個封包覆寫
        Assert.True(a.Add(new VideoFrame(1, 64, 64, true, 1, 2, new byte[50], "h264"), out _, out var data));
        Assert.All(data!.Take(50), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Assembler_RejectsOutOfOrder_Mismatched_AndOversized()
    {
        var a = new VideoFrameAssembler();
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(1, 1, 2), out _, out _));             // 沒有第 0 塊
        a.Add(Chunk(1, 0, 2), out _, out _);
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(2, 1, 2), out _, out _));             // 不同畫面
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(1, 0, 0), out _, out _));             // 塊數 0
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(1, 0, 65), out _, out _));            // 塊數過多
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(1, 0, 1, w: 63), out _, out _));      // 奇數寬
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(1, 0, 1, w: 100_000), out _, out _)); // 過大
        a.Add(Chunk(3, 0, 40, len: ProtocolInfo.MaxVideoChunkBytes), out _, out _);
        for (int i = 1; i < 32; i++) a.Add(Chunk(3, i, 40, len: ProtocolInfo.MaxVideoChunkBytes), out _, out _);
        Assert.Throws<InvalidDataException>(() => a.Add(Chunk(3, 32, 40, len: ProtocolInfo.MaxVideoChunkBytes), out _, out _)); // 超過 16 MiB
    }
}
