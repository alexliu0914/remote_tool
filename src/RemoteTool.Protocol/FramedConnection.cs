
namespace RemoteTool.Protocol;

/// <summary>
/// 在一條（已完成 TLS 的）串流上收送封包。
/// <list type="bullet">
/// <item>送出時自動填 Sequence（每個方向從 1 遞增），多執行緒送出以鎖序列化。</item>
/// <item>收到時檢查 Sequence 連續；不連續、格式錯誤、超長即拋 <see cref="InvalidDataException"/>。</item>
/// <item>只允許單一讀取者。</item>
/// </list>
/// </summary>
public sealed class FramedConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private int _maxPayloadSize;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _readBuffer;
    private int _readCount;
    private uint _sendSequence;
    private uint _receiveSequence;
    private int _disposed;

    public FramedConnection(Stream stream, int maxPayloadSize = FrameCodec.DefaultMaxPayloadSize)
    {
        _stream = stream;
        _maxPayloadSize = maxPayloadSize;
        _readBuffer = new byte[16 * 1024];
    }

    /// <summary>
    /// 單一封包的 Payload 上限。握手確定角色後可調高（例如 Video 連線），不可調低。
    /// </summary>
    public int MaxPayloadSize
    {
        get => _maxPayloadSize;
        set
        {
            if (value < _maxPayloadSize)
                throw new ArgumentOutOfRangeException(nameof(value), "只能調高上限");
            _maxPayloadSize = value;
        }
    }

    /// <summary>底層串流（供呼叫端取得 TLS 資訊等）。</summary>
    public Stream Stream => _stream;

    /// <summary>送出一個訊息。</summary>
    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, uint requestId = 0,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = new Frame(type, requestId, ++_sendSequence, payload);
            var bytes = FrameCodec.Encode(frame, _maxPayloadSize);
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>序列化並送出一個訊息。</summary>
    public Task SendAsync<T>(T message, uint requestId = 0, CancellationToken cancellationToken = default)
        where T : notnull
        => SendAsync(MessageSerializer.TypeOf<T>(), MessageSerializer.Serialize(message), requestId, cancellationToken);

    /// <summary>
    /// 讀取下一個封包；對方正常關閉時回傳 null。
    /// </summary>
    /// <exception cref="InvalidDataException">格式錯誤或序號不連續。</exception>
    public async Task<Frame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryTakeFrame(out var frame))
                return frame;

            if (_readCount == _readBuffer.Length)
                Array.Resize(ref _readBuffer, Math.Min(_readBuffer.Length * 2,
                    FrameCodec.LengthPrefixSize + FrameCodec.HeaderSize + _maxPayloadSize));

            int read = await _stream.ReadAsync(_readBuffer.AsMemory(_readCount), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (_readCount != 0)
                    throw new InvalidDataException("連線在封包中途關閉");
                return null;
            }
            _readCount += read;
        }
    }

    private bool TryTakeFrame(out Frame frame)
    {
        frame = default;
        var span = _readBuffer.AsSpan(0, _readCount);
        int total = FrameCodec.PeekTotalSize(span, _maxPayloadSize);
        if (total < 0)
            return false;
        if (total > _readBuffer.Length)
        {
            Array.Resize(ref _readBuffer, total);
            return false;
        }
        if (!FrameCodec.TryDecode(span, out frame, out int consumed, _maxPayloadSize))
            return false;

        if (frame.Sequence != _receiveSequence + 1)
            throw new InvalidDataException($"序號不連續：預期 {_receiveSequence + 1}，收到 {frame.Sequence}");
        _receiveSequence = frame.Sequence;

        Buffer.BlockCopy(_readBuffer, consumed, _readBuffer, 0, _readCount - consumed);
        _readCount -= consumed;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
