using System.IO.Compression;
using RemoteTool.Protocol;

namespace RemoteTool.Video;

/// <summary>
/// 控制端解碼畫布：套用 <see cref="VideoTileUpdate"/>，累積本張畫面的變動範圍。
/// 所有輸入視為不受信任：大小、邊界、解壓後長度不符即拋 <see cref="InvalidDataException"/>。
/// 讀寫像素時請鎖定 <see cref="SyncRoot"/>。
/// </summary>
public sealed class FrameCanvas
{
    /// <summary>單一區塊寬高上限（編碼端用 64，保留餘裕）。</summary>
    public const int MaxTileDimension = 256;

    private readonly FrameBuffer _buffer = new();
    private readonly byte[] _scratch = new byte[MaxTileDimension * MaxTileDimension * FrameBuffer.BytesPerPixel + 1];
    private PixelRect _pendingDirty;

    public object SyncRoot { get; } = new();
    public FrameBuffer Buffer => _buffer;
    public long LastFrameId { get; private set; }
    public int CompletedFrames { get; private set; }
    /// <summary>最近完成的一張畫面是否為完整畫面（keyframe）。</summary>
    public bool LastCompletedWasKeyframe { get; private set; }

    /// <summary>套用一個封包。回傳 true 表示一張畫面完成，<paramref name="dirty"/> 為該張的變動範圍。</summary>
    public bool Apply(VideoTileUpdate update, out PixelRect dirty)
    {
        dirty = default;
        if (update.FrameWidth < 1 || update.FrameHeight < 1
            || update.FrameWidth > FrameBuffer.MaxDimension || update.FrameHeight > FrameBuffer.MaxDimension)
            throw new InvalidDataException($"畫面大小 {update.FrameWidth}×{update.FrameHeight} 不合法");
        if (update.Tiles is null)
            throw new InvalidDataException("缺少區塊資料");

        lock (SyncRoot)
        {
            if (update.FrameWidth != _buffer.Width || update.FrameHeight != _buffer.Height)
            {
                _buffer.Resize(update.FrameWidth, update.FrameHeight);
                _pendingDirty = new PixelRect(0, 0, update.FrameWidth, update.FrameHeight);
            }

            foreach (var tile in update.Tiles)
                ApplyTile(tile);

            LastFrameId = update.FrameId;
            if (!update.EndOfFrame) return false;

            dirty = _pendingDirty;
            _pendingDirty = default;
            CompletedFrames++;
            LastCompletedWasKeyframe = update.IsKeyframe;
            return true;
        }
    }

    private void ApplyTile(VideoTile tile)
    {
        if (tile is null || tile.Data is null)
            throw new InvalidDataException("區塊為空");
        if (tile.Width < 1 || tile.Height < 1 || tile.Width > MaxTileDimension || tile.Height > MaxTileDimension
            || tile.X < 0 || tile.Y < 0 || tile.X > _buffer.Width - tile.Width || tile.Y > _buffer.Height - tile.Height)
            throw new InvalidDataException($"區塊 ({tile.X},{tile.Y},{tile.Width}×{tile.Height}) 超出畫面");

        int rowBytes = tile.Width * 4;
        int expected = rowBytes * tile.Height;

        // 多讀 1 位元組：若還讀得到，代表解壓後比預期長（壓縮炸彈或錯誤資料）。
        int total = 0;
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(tile.Data, writable: false), CompressionMode.Decompress);
            int n;
            while (total < expected + 1 && (n = deflate.Read(_scratch, total, expected + 1 - total)) > 0)
                total += n;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("區塊解壓失敗", ex);
        }
        if (total != expected)
            throw new InvalidDataException($"區塊解壓後長度 {total}，預期 {expected}");

        int stride = _buffer.Stride;
        for (int y = 0; y < tile.Height; y++)
            _scratch.AsSpan(y * rowBytes, rowBytes).CopyTo(_buffer.Pixels.AsSpan((tile.Y + y) * stride + tile.X * 4));

        _pendingDirty = _pendingDirty.Union(new PixelRect(tile.X, tile.Y, tile.Width, tile.Height));
    }
}
