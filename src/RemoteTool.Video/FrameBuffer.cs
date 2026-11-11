namespace RemoteTool.Video;

/// <summary>一張 BGRA 畫面（每像素 4 位元組，逐列緊密排列，stride = Width × 4）。可重複使用以避免每張配置。</summary>
public sealed class FrameBuffer
{
    public const int BytesPerPixel = 4;
    /// <summary>寬高上限，超過視為不合法（防惡意或損毀資料）。</summary>
    public const int MaxDimension = 8192;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public int Stride => Width * BytesPerPixel;

    public FrameBuffer() { }

    public FrameBuffer(int width, int height) => Resize(width, height);

    /// <summary>調整大小；大小不變時保留內容，改變時內容清為 0。</summary>
    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), $"畫面大小 {width}×{height} 不合法");
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Pixels = new byte[width * height * BytesPerPixel];
    }

    public bool SameSizeAs(FrameBuffer other) => Width == other.Width && Height == other.Height;

    public void CopyTo(FrameBuffer target)
    {
        target.Resize(Width, Height);
        Pixels.AsSpan().CopyTo(target.Pixels);
    }
}

/// <summary>像素矩形。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        int r = Math.Max(X + Width, other.X + other.Width), b = Math.Max(Y + Height, other.Y + other.Height);
        return new PixelRect(x, y, r - x, b - y);
    }
}
