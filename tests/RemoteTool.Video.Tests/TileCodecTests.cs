using System.IO.Compression;
using RemoteTool.Protocol;
using RemoteTool.Video;
using Xunit;

namespace RemoteTool.Video.Tests;

public class TileCodecTests
{
    private static void ApplyAll(FrameCanvas canvas, IReadOnlyList<VideoTileUpdate> packets)
    {
        for (int i = 0; i < packets.Count; i++)
        {
            // 經過完整的序列化往返，確認線路格式可用
            var frame = new Frame(MessageType.VideoTileUpdate, 0, 1, MessageCodec.Serialize(packets[i]));
            Assert.True(frame.Payload.Length <= FrameCodec.MaxPayloadSize);
            bool done = canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(frame), out _);
            Assert.Equal(i == packets.Count - 1, done);
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(100, 37)]    // 非 64 倍數，邊緣區塊較小
    [InlineData(1, 1)]
    public void FullFrame_RoundTrips_Exactly(int w, int h)
    {
        var src = TestFrames.Gradient(w, h);
        var packets = new TileEncoder().Encode(src, forceFull: false);
        var canvas = new FrameCanvas();
        ApplyAll(canvas, packets);
        Assert.Equal(src.Pixels, canvas.Buffer.Pixels);
    }

    [Fact]
    public void IncompressibleFullHd_IsSplitIntoPacketsUnderLimit()
    {
        var src = TestFrames.Noise(1920, 1080, 1);
        var packets = new TileEncoder().Encode(src, false);
        Assert.True(packets.Count > 1);
        Assert.All(packets.Take(packets.Count - 1), p => Assert.False(p.EndOfFrame));
        Assert.True(packets[^1].EndOfFrame);
        Assert.Single(packets.Select(p => p.FrameId).Distinct());

        var canvas = new FrameCanvas();
        ApplyAll(canvas, packets);
        Assert.Equal(src.Pixels, canvas.Buffer.Pixels);
    }

    [Fact]
    public void OnlyChangedTiles_AreSent()
    {
        var enc = new TileEncoder();
        var canvas = new FrameCanvas();
        var a = TestFrames.Gradient(640, 480);
        ApplyAll(canvas, enc.Encode(a, false));

        var b = TestFrames.Gradient(640, 480);
        TestFrames.Fill(b, 70, 70, 10, 10, 0x55); // 只在區塊 (64,64) 內
        var packets = enc.Encode(b, false);

        var tile = Assert.Single(Assert.Single(packets).Tiles);
        Assert.Equal((64, 64), (tile.X, tile.Y));

        canvas.Apply(packets[0], out var dirty);
        Assert.Equal(new PixelRect(64, 64, 64, 64), dirty);
        Assert.Equal(b.Pixels, canvas.Buffer.Pixels);
    }

    [Fact]
    public void UnchangedFrame_ProducesNothing_UnlessForced()
    {
        var enc = new TileEncoder();
        var a = TestFrames.Gradient(300, 200);
        enc.Encode(a, false);
        Assert.Empty(enc.Encode(TestFrames.Gradient(300, 200), false));
        Assert.NotEmpty(enc.Encode(TestFrames.Gradient(300, 200), true));
        Assert.NotEmpty(enc.EncodePreviousFull());
    }

    [Fact]
    public void SizeChange_SendsFullFrame_AndCanvasResizes()
    {
        var enc = new TileEncoder();
        var canvas = new FrameCanvas();
        ApplyAll(canvas, enc.Encode(TestFrames.Gradient(320, 240), false));
        var big = TestFrames.Gradient(800, 600, shift: 3);
        ApplyAll(canvas, enc.Encode(big, false));
        Assert.Equal((800, 600), (canvas.Buffer.Width, canvas.Buffer.Height));
        Assert.Equal(big.Pixels, canvas.Buffer.Pixels);
    }

    [Fact]
    public void Downscale_HalvesAndAverages()
    {
        var src = new FrameBuffer(4, 2);
        // 左 2×2 區塊：值 0 與 100 → 平均 50；右 2×2：全 200
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                src.Pixels.AsSpan((y * 4 + x) * 4, 4).Fill(x < 2 ? (byte)(y == 0 ? 0 : 100) : (byte)200);
        var dst = new FrameBuffer();
        FrameScaler.Downscale(src, 2, dst);
        Assert.Equal((2, 1), (dst.Width, dst.Height));
        Assert.Equal(new byte[] { 50, 50, 50, 50, 200, 200, 200, 200 }, dst.Pixels);
    }

    [Theory]
    [InlineData(1920, 1920, 1)]
    [InlineData(2560, 1920, 2)]
    [InlineData(3840, 1920, 2)]
    [InlineData(5120, 1920, 3)]
    public void FactorFor_KeepsWidthUnderLimit(int width, int max, int expected)
        => Assert.Equal(expected, FrameScaler.FactorFor(width, max));

    // ---- 解碼端防護 ----

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Canvas_RejectsTileOutsideFrame()
    {
        var tile = new VideoTile(60, 0, 8, 8, Deflate(new byte[8 * 8 * 4]));
        Assert.Throws<InvalidDataException>(() => new FrameCanvas().Apply(new VideoTileUpdate(1, 64, 64, true, [tile]), out _));
    }

    [Fact]
    public void Canvas_RejectsNegativeOrHugeDimensions()
    {
        var canvas = new FrameCanvas();
        Assert.Throws<InvalidDataException>(() => canvas.Apply(new VideoTileUpdate(1, 0, 10, true, []), out _));
        Assert.Throws<InvalidDataException>(() => canvas.Apply(new VideoTileUpdate(1, 100_000, 10, true, []), out _));
        var neg = new VideoTile(-1, 0, 8, 8, Deflate(new byte[256]));
        Assert.Throws<InvalidDataException>(() => canvas.Apply(new VideoTileUpdate(1, 64, 64, true, [neg]), out _));
    }

    [Fact]
    public void Canvas_RejectsDecompressionBomb()
    {
        // 宣稱 8×8，實際解壓出 1 MB
        var bomb = new VideoTile(0, 0, 8, 8, Deflate(new byte[1024 * 1024]));
        Assert.Throws<InvalidDataException>(() => new FrameCanvas().Apply(new VideoTileUpdate(1, 64, 64, true, [bomb]), out _));
    }

    [Fact]
    public void Canvas_RejectsShortOrGarbageData()
    {
        var shortTile = new VideoTile(0, 0, 8, 8, Deflate(new byte[10]));
        Assert.Throws<InvalidDataException>(() => new FrameCanvas().Apply(new VideoTileUpdate(1, 64, 64, true, [shortTile]), out _));
        var garbage = new VideoTile(0, 0, 8, 8, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Throws<InvalidDataException>(() => new FrameCanvas().Apply(new VideoTileUpdate(1, 64, 64, true, [garbage]), out _));
    }
}
