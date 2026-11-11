namespace RemoteTool.Video;

/// <summary>整數倍縮小（方框平均）。A1 用來限制解析度。</summary>
public static class FrameScaler
{
    /// <summary>讓寬度不超過 <paramref name="maxWidth"/> 的最小整數縮小倍數（1 = 不縮小）。</summary>
    public static int FactorFor(int width, int maxWidth)
        => maxWidth <= 0 || width <= maxWidth ? 1 : (width + maxWidth - 1) / maxWidth;

    public static void Downscale(FrameBuffer source, int factor, FrameBuffer target)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1)
        {
            source.CopyTo(target);
            return;
        }

        int w = Math.Max(1, source.Width / factor), h = Math.Max(1, source.Height / factor);
        target.Resize(w, h);
        byte[] src = source.Pixels, dst = target.Pixels;
        int srcStride = source.Stride, area = factor * factor;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * srcStride + x * factor * 4;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = row + dx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                    }
                }
                int o = (y * w + x) * 4;
                dst[o] = (byte)(b / area); dst[o + 1] = (byte)(g / area);
                dst[o + 2] = (byte)(r / area); dst[o + 3] = (byte)(a / area);
            }
        }
    }
}
