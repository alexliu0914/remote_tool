namespace RemoteTool.Video;

/// <summary>
/// BGRA 與 NV12 的轉換（BT.709、有限範圍 16–235 / 16–240，H.264 標準 HD 色彩）。
/// NV12：Y 平面（寬 × 高）之後接交錯的 UV 平面（寬 × 高 / 2，每 2×2 像素一組 U、V）。寬高必須為偶數。
/// </summary>
public static class ColorConverter
{
    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    /// <summary>BGRA → NV12（緊密排列，stride = 寬）。</summary>
    public static void BgraToNv12(FrameBuffer source, Span<byte> nv12)
    {
        int w = source.Width, h = source.Height;
        if ((w & 1) != 0 || (h & 1) != 0) throw new ArgumentException("NV12 需要偶數寬高");
        if (nv12.Length < Nv12Size(w, h)) throw new ArgumentException("NV12 緩衝區太小");

        var src = source.Pixels;
        var dst = nv12;
        int uvBase = w * h;
        for (int y = 0; y < h; y += 2)
        {
            int row0 = y * w * 4, row1 = (y + 1) * w * 4;
            int yRow0 = y * w, yRow1 = (y + 1) * w;
            int uvRow = uvBase + (y / 2) * w;
            for (int x = 0; x < w; x += 2)
            {
                int i00 = row0 + x * 4, i01 = i00 + 4, i10 = row1 + x * 4, i11 = i10 + 4;
                int b00 = src[i00], g00 = src[i00 + 1], r00 = src[i00 + 2];
                int b01 = src[i01], g01 = src[i01 + 1], r01 = src[i01 + 2];
                int b10 = src[i10], g10 = src[i10 + 1], r10 = src[i10 + 2];
                int b11 = src[i11], g11 = src[i11 + 1], r11 = src[i11 + 2];

                dst[yRow0 + x] = Luma(r00, g00, b00);
                dst[yRow0 + x + 1] = Luma(r01, g01, b01);
                dst[yRow1 + x] = Luma(r10, g10, b10);
                dst[yRow1 + x + 1] = Luma(r11, g11, b11);

                int r = (r00 + r01 + r10 + r11 + 2) >> 2, g = (g00 + g01 + g10 + g11 + 2) >> 2, b = (b00 + b01 + b10 + b11 + 2) >> 2;
                dst[uvRow + x] = Clamp(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);
                dst[uvRow + x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
            }
        }
    }

    /// <summary>
    /// NV12 → BGRA。<paramref name="stride"/> 為每列位元組數（解碼器常有對齊）；
    /// <paramref name="planeRows"/> 為 Y 平面的列數（解碼器常把高度補到 16 的倍數，例如 1088），UV 平面緊接其後。
    /// 只輸出左上角 <paramref name="width"/> × <paramref name="height"/>。
    /// </summary>
    public static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int stride, int planeRows, int width, int height, FrameBuffer target)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 需要偶數寬高");
        if (stride < width || planeRows < height) throw new ArgumentException("NV12 stride 或列數不合法");
        long needed = (long)stride * planeRows + (long)stride * (height / 2);
        if (nv12.Length < needed) throw new ArgumentException("NV12 資料不足");

        target.Resize(width, height);
        var dst = target.Pixels;
        int uvBase = stride * planeRows;
        for (int y = 0; y < height; y++)
        {
            int yRow = y * stride, uvRow = uvBase + (y / 2) * stride, o = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int c = 298 * (nv12[yRow + x] - 16);
                int uvi = uvRow + (x & ~1);
                int d = nv12[uvi] - 128, e = nv12[uvi + 1] - 128;
                dst[o++] = Clamp((c + 541 * d + 128) >> 8);            // B
                dst[o++] = Clamp((c - 55 * d - 136 * e + 128) >> 8);   // G
                dst[o++] = Clamp((c + 459 * e + 128) >> 8);            // R
                dst[o++] = 255;
            }
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
