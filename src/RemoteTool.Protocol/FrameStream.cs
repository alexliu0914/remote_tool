using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>協定錯誤：未知類型、類型不允許、序號錯誤等。收到後應回 Error 並關閉連線。</summary>
public sealed class ProtocolException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// 在一條位元組串流（通常是 SslStream）上讀寫 Protocol V1 封包，並強制執行：
/// <list type="bullet">
/// <item>Length 先驗證再配置記憶體</item>
/// <item>Sequence：每個方向從 1 開始逐一遞增，收到的序號不符即為協定錯誤</item>
/// <item>未知類型、或不允許在此連線角色 / 方向出現的類型，即為協定錯誤（見 <see cref="ProtocolRules"/>）</item>
/// </list>
/// 讀取同一時間只能有一個呼叫端；寫入可多執行緒並行（內部序列化）。
/// </summary>
public sealed class FrameStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Endpoint _local;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _lengthBuffer = new byte[FrameCodec.LengthPrefixSize];
    private uint _nextWriteSequence = 1;
    private uint _nextReadSequence = 1;
    private int _nextRequestId;

    /// <param name="stream">底層串流。</param>
    /// <param name="local">本端是控制端還是被控端（用來判斷收送方向）。</param>
    public FrameStream(Stream stream, Endpoint local)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _local = local;
    }

    private Endpoint Remote => _local == Endpoint.Host ? Endpoint.Controller : Endpoint.Host;

    /// <summary>連線角色。Hello 交換完成後設定；設定前只允許 Hello 與 Error。</summary>
    public ChannelKind? Role { get; set; }

    /// <summary>配發一個新的非 0 RequestId。</summary>
    public uint NextRequestId()
    {
        uint id = (uint)Interlocked.Increment(ref _nextRequestId);
        return id == 0 ? (uint)Interlocked.Increment(ref _nextRequestId) : id;
    }

    /// <summary>
    /// 讀下一個封包。對方在封包邊界正常關閉時回傳 null；封包讀到一半斷線拋出 <see cref="EndOfStreamException"/>；
    /// Length 不合法拋出 <see cref="InvalidDataException"/>；序號或類型違規拋出 <see cref="ProtocolException"/>。
    /// </summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        int read = 0;
        while (read < _lengthBuffer.Length)
        {
            int n = await _stream.ReadAsync(_lengthBuffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException("封包長度欄位讀到一半連線即中斷");
            }
            read += n;
        }

        uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(_lengthBuffer);
        FrameCodec.ValidateLength(lengthValue); // 先驗證，再配置

        var body = new byte[lengthValue];
        try
        {
            await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new EndOfStreamException("封包內容讀到一半連線即中斷", ex);
        }

        var frame = FrameCodec.DecodeBody(body);

        if (frame.Sequence != _nextReadSequence)
            throw new ProtocolException(ErrorCode.ProtocolViolation, $"序號錯誤：預期 {_nextReadSequence}，收到 {frame.Sequence}");
        _nextReadSequence++;
        if (_nextReadSequence == 0)
            throw new ProtocolException(ErrorCode.ProtocolViolation, "序號用盡，須重新連線");

        if (!ProtocolRules.IsKnown(frame.Type))
            throw new ProtocolException(ErrorCode.UnknownMessageType, $"未知訊息類型 {(ushort)frame.Type}");
        if (!ProtocolRules.IsAllowed(frame.Type, Role, Remote))
            throw new ProtocolException(ErrorCode.ProtocolViolation,
                $"{frame.Type} 不允許出現在 {(Role?.ToString() ?? "握手前")} 連線（{Remote} 送出）");

        return frame;
    }

    /// <summary>送出一則訊息。<paramref name="requestId"/> 為 0 表示不是請求 / 回應。</summary>
    public async ValueTask WriteAsync<T>(T message, uint requestId = 0, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        if (!ProtocolRules.IsAllowed(T.Type, Role, _local))
            throw new InvalidOperationException($"{T.Type} 不允許由 {_local} 在 {(Role?.ToString() ?? "握手前")} 連線送出");

        byte[] payload = MessageCodec.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            uint sequence = _nextWriteSequence;
            if (sequence == 0) throw new InvalidOperationException("序號用盡，須重新連線");
            byte[] bytes = FrameCodec.Encode(new Frame(T.Type, requestId, sequence, payload));
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _nextWriteSequence = sequence + 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
