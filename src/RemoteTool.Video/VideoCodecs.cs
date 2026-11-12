using RemoteTool.Protocol;

namespace RemoteTool.Video;

/// <summary>一張編碼後的畫面。</summary>
public sealed record EncodedFrame(byte[] Data, bool IsKeyframe);

/// <summary>
/// 影像編碼器（A2：Media Foundation H.264，Windows 實作在 RemoteTool.Media）。
/// 建立時固定寬高（偶數）；寬高改變時由呼叫端重建。失敗時拋例外，呼叫端改用區塊差異備援。
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>顯示用名稱，例如「H.264（硬體：Intel）」。</summary>
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>編碼一張 BGRA 畫面；編碼器暫時沒有輸出時回傳 null。</summary>
    EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe);
}

/// <summary>影像解碼器（A2：Media Foundation H.264）。失敗時拋例外，呼叫端要求關鍵幀。</summary>
public interface IVideoDecoder : IDisposable
{
    string Name { get; }
    /// <summary>解一張畫面寫入 <paramref name="target"/>（BGRA）。解碼器暫時沒有輸出時回傳 false。</summary>
    bool Decode(byte[] data, int width, int height, FrameBuffer target);
}

/// <summary>
/// 把 VideoFrame 分塊組回整張畫面，並檢查所有欄位（不受信任資料）：
/// 分塊依序、同一張畫面欄位一致、塊數與總大小有上限、寬高為偶數且在範圍內。
/// </summary>
public sealed class VideoFrameAssembler
{
    private readonly List<byte[]> _chunks = new();
    private VideoFrame? _first;
    private int _total;

    /// <summary>加入一塊；一張畫面收齊時回傳 true 並輸出完整資料。</summary>
    public bool Add(VideoFrame chunk, out VideoFrame? first, out byte[]? data)
    {
        first = null;
        data = null;
        if (chunk.Data is null) throw new InvalidDataException("VideoFrame 缺少資料");
        if (chunk.ChunkCount < 1 || chunk.ChunkCount > ProtocolInfo.MaxVideoChunks
            || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            throw new InvalidDataException($"VideoFrame 分塊編號不合法（{chunk.ChunkIndex}/{chunk.ChunkCount}）");
        if (chunk.Width < 2 || chunk.Height < 2 || chunk.Width > FrameBuffer.MaxDimension || chunk.Height > FrameBuffer.MaxDimension
            || (chunk.Width & 1) != 0 || (chunk.Height & 1) != 0)
            throw new InvalidDataException($"VideoFrame 畫面大小 {chunk.Width}×{chunk.Height} 不合法");

        if (chunk.ChunkIndex == 0)
        {
            _chunks.Clear();
            _total = 0;
            _first = chunk;
        }
        else if (_first is null || chunk.FrameId != _first.FrameId || chunk.ChunkIndex != _chunks.Count
                 || chunk.ChunkCount != _first.ChunkCount || chunk.Width != _first.Width || chunk.Height != _first.Height)
        {
            _chunks.Clear();
            _first = null;
            throw new InvalidDataException("VideoFrame 分塊順序或內容不一致");
        }

        _total += chunk.Data.Length;
        if (_total > ProtocolInfo.MaxVideoFrameBytes)
        {
            _chunks.Clear();
            _first = null;
            throw new InvalidDataException("VideoFrame 超過大小上限");
        }
        _chunks.Add(chunk.Data);

        if (_chunks.Count < chunk.ChunkCount) return false;

        data = _chunks.Count == 1 ? _chunks[0] : _chunks.SelectMany(c => c).ToArray();
        first = _first;
        _chunks.Clear();
        _first = null;
        return true;
    }

    /// <summary>把一張編碼畫面切成 VideoFrame 封包。</summary>
    public static List<VideoFrame> Split(long frameId, int width, int height, EncodedFrame frame, string codec)
    {
        int max = ProtocolInfo.MaxVideoChunkBytes;
        int count = Math.Max(1, (frame.Data.Length + max - 1) / max);
        if (count > ProtocolInfo.MaxVideoChunks) throw new InvalidOperationException("編碼畫面過大");
        var list = new List<VideoFrame>(count);
        for (int i = 0; i < count; i++)
        {
            int start = i * max, len = Math.Min(max, frame.Data.Length - start);
            list.Add(new VideoFrame(frameId, width, height, frame.IsKeyframe, i, count,
                frame.Data.AsSpan(start, Math.Max(0, len)).ToArray(), codec));
        }
        return list;
    }
}

/// <summary>
/// 控制端的影像接收：同時處理 H.264（VideoFrame）與區塊差異（VideoTileUpdate），結果都寫入同一個 <see cref="FrameCanvas"/>。
/// 被控端可在串流中途改用備援格式，接收端不需另外協商。
/// </summary>
public sealed class VideoReceiver : IDisposable
{
    private readonly Func<IVideoDecoder?>? _decoderFactory;
    private readonly VideoFrameAssembler _assembler = new();
    private readonly FrameBuffer _decoded = new();
    private IVideoDecoder? _decoder;
    private bool _decoderFailed;

    public VideoReceiver(FrameCanvas canvas, Func<IVideoDecoder?>? decoderFactory)
    {
        Canvas = canvas;
        _decoderFactory = decoderFactory;
    }

    public FrameCanvas Canvas { get; }

    /// <summary>目前使用的格式（顯示用）。</summary>
    public string CurrentFormat { get; private set; } = "—";

    /// <summary>
    /// 處理一個 Video 封包。完成一張畫面時回傳 true。
    /// <paramref name="needKeyframe"/> 為 true 表示解碼失敗，呼叫端應要求被控端送關鍵幀。
    /// </summary>
    public bool Handle(Frame frame, out PixelRect dirty, out bool isKeyframe, out bool needKeyframe)
    {
        dirty = default;
        isKeyframe = false;
        needKeyframe = false;

        switch (frame.Type)
        {
            case MessageType.VideoTileUpdate:
                var tiles = MessageCodec.Decode<VideoTileUpdate>(frame);
                CurrentFormat = "區塊差異（A1）";
                if (!Canvas.Apply(tiles, out dirty)) return false;
                isKeyframe = Canvas.LastCompletedWasKeyframe;
                return true;

            case MessageType.VideoFrame:
                var chunk = MessageCodec.Decode<VideoFrame>(frame);
                if (!_assembler.Add(chunk, out var first, out var data)) return false;
                var decoder = EnsureDecoder();
                if (decoder is null)
                {
                    needKeyframe = false;
                    return false;
                }
                CurrentFormat = decoder.Name;
                try
                {
                    if (!decoder.Decode(data!, first!.Width, first.Height, _decoded)) return false;
                }
                catch (Exception) when (!first!.IsKeyframe)
                {
                    needKeyframe = true; // P 幀解不出來：從下一個關鍵幀重新開始
                    return false;
                }
                dirty = Canvas.ApplyFullFrame(_decoded, first.IsKeyframe);
                isKeyframe = first.IsKeyframe;
                return true;

            default:
                return false;
        }
    }

    private IVideoDecoder? EnsureDecoder()
    {
        if (_decoder is not null || _decoderFailed) return _decoder;
        try
        {
            _decoder = _decoderFactory?.Invoke();
        }
        catch (Exception)
        {
            _decoder = null;
        }
        _decoderFailed = _decoder is null;
        return _decoder;
    }

    public void Dispose() => _decoder?.Dispose();
}
