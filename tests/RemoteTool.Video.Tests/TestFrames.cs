using RemoteTool.Video;

namespace RemoteTool.Video.Tests;

internal static class TestFrames
{
    /// <summary>可重現的雜訊畫面（壓縮率低，最壞情況）。</summary>
    public static FrameBuffer Noise(int w, int h, int seed)
    {
        var f = new FrameBuffer(w, h);
        new Random(seed).NextBytes(f.Pixels);
        return f;
    }

    /// <summary>漸層畫面（類似一般桌面，可壓縮）。</summary>
    public static FrameBuffer Gradient(int w, int h, int shift = 0)
    {
        var f = new FrameBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                f.Pixels[i] = (byte)(x + shift); f.Pixels[i + 1] = (byte)y; f.Pixels[i + 2] = (byte)(x ^ y); f.Pixels[i + 3] = 255;
            }
        return f;
    }

    public static void Fill(FrameBuffer f, int x, int y, int w, int h, byte value)
    {
        for (int yy = y; yy < y + h; yy++)
            f.Pixels.AsSpan((yy * f.Width + x) * 4, w * 4).Fill(value);
    }
}
