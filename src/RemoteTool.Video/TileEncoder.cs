using System.IO.Compression;
using RemoteTool.Protocol;

namespace RemoteTool.Video;

/// <summary>
/// A1 PoC 編碼器（D-027）：畫面切成 64×64 區塊，與上一張比對，只送變動的區塊；
/// 每塊 Deflate 壓縮，打包成每個不超過 <see cref="MaxPacketBytes"/> 的 <see cref="VideoTileUpdate"/>。
/// 只保留上一張畫面一份複本。
/// </summary>
public sealed class TileEncoder
{
    public const int TileSize = 64;
    public const int MaxPacketBytes = 512 * 1024;
    private const int PerTileOverhead = 32;

    private readonly FrameBuffer _previous = new();
    private readonly byte[] _tileScratch = new byte[TileSize * TileSize * FrameBuffer.BytesPerPixel];
    private bool _hasPrevious;
    private long _frameId;

    public bool HasPrevious => _hasPrevious;

    /// <summary>
    /// 編碼一張新畫面。<paramref name="forceFull"/> 或大小改變時送出全部區塊。
    /// 沒有任何變動時回傳空清單（不必送）。
    /// </summary>
    public IReadOnlyList<VideoTileUpdate> Encode(FrameBuffer frame, bool forceFull)
    {
        bool full = forceFull || !_hasPrevious || !frame.SameSizeAs(_previous);
        var tiles = new List<VideoTile>();

        for (int ty = 0; ty < frame.Height; ty += TileSize)
        {
            int th = Math.Min(TileSize, frame.Height - ty);
            for (int tx = 0; tx < frame.Width; tx += TileSize)
            {
                int tw = Math.Min(TileSize, frame.Width - tx);
                if (full || TileChanged(frame, tx, ty, tw, th))
                    tiles.Add(CompressTile(frame, tx, ty, tw, th));
            }
        }

        frame.CopyTo(_previous);
        _hasPrevious = true;

        return tiles.Count == 0 ? Array.Empty<VideoTileUpdate>() : Pack(frame.Width, frame.Height, tiles);
    }

    /// <summary>以上一張畫面重送完整畫面（收到 KeyframeRequest 但沒有新畫面時用）。</summary>
    public IReadOnlyList<VideoTileUpdate> EncodePreviousFull()
    {
        if (!_hasPrevious) return Array.Empty<VideoTileUpdate>();
        var copy = new FrameBuffer();
        _previous.CopyTo(copy);
        return Encode(copy, forceFull: true);
    }

    private bool TileChanged(FrameBuffer frame, int tx, int ty, int tw, int th)
    {
        int stride = frame.Stride, rowBytes = tw * 4;
        for (int y = ty; y < ty + th; y++)
        {
            int offset = y * stride + tx * 4;
            if (!frame.Pixels.AsSpan(offset, rowBytes).SequenceEqual(_previous.Pixels.AsSpan(offset, rowBytes)))
                return true;
        }
        return false;
    }

    private VideoTile CompressTile(FrameBuffer frame, int tx, int ty, int tw, int th)
    {
        int stride = frame.Stride, rowBytes = tw * 4;
        for (int y = 0; y < th; y++)
            frame.Pixels.AsSpan((ty + y) * stride + tx * 4, rowBytes).CopyTo(_tileScratch.AsSpan(y * rowBytes));

        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(_tileScratch, 0, rowBytes * th);
        return new VideoTile(tx, ty, tw, th, ms.ToArray());
    }

    private List<VideoTileUpdate> Pack(int width, int height, List<VideoTile> tiles)
    {
        long id = ++_frameId;
        var packets = new List<VideoTileUpdate>();
        var current = new List<VideoTile>();
        int size = 0;

        foreach (var tile in tiles)
        {
            int tileSize = tile.Data.Length + PerTileOverhead;
            if (current.Count > 0 && size + tileSize > MaxPacketBytes)
            {
                packets.Add(new VideoTileUpdate(id, width, height, false, current.ToArray()));
                current.Clear();
                size = 0;
            }
            current.Add(tile);
            size += tileSize;
        }
        packets.Add(new VideoTileUpdate(id, width, height, true, current.ToArray()));
        return packets;
    }
}
