using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>
/// 在一條位元組串流（通常是 SslStream）上讀寫 <see cref="Frame"/>。
/// 讀取同一時間只能有一個呼叫端；寫入可多執行緒並行（內部序列化）。
/// </summary>
public sealed class FrameStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _lengthBuffer = new byte[FrameCodec.LengthPrefixSize];

    public FrameStream(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>
    /// 讀下一個封包。對方在封包邊界正常關閉時回傳 null；
    /// 封包讀到一半斷線拋出 <see cref="EndOfStreamException"/>；長度欄位不合法拋出 <see cref="InvalidDataException"/>。
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
        FrameCodec.ValidateLength(lengthValue);

        var body = new byte[lengthValue];
        try
        {
            await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new EndOfStreamException("封包內容讀到一半連線即中斷", ex);
        }

        return new Frame((ChannelKind)body[0], (MessageType)body[1], body.AsMemory(FrameCodec.HeaderSize));
    }

    public async ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        byte[] bytes = FrameCodec.Encode(frame);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask WriteAsync<T>(ChannelKind channel, T message, CancellationToken cancellationToken = default)
        where T : IMessage
        => WriteAsync(MessageCodec.Encode(channel, message), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
