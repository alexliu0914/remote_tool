using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>
/// 封包編解碼。線路格式（第七節）：
/// <code>
///   [Length: u32 BE][MessageType: u8][RequestId: u32 BE][Sequence: u32 BE][Payload: N]
/// </code>
/// Length = 表頭其餘欄位 + Payload 的位元組數（9 + N）。
/// 長度超過上限、長度不足表頭、未知訊息類型都視為格式錯誤（拋出 <see cref="InvalidDataException"/>），
/// 呼叫端應立即斷線，不嘗試「跳過」壞資料。
/// </summary>
public static class FrameCodec
{
    /// <summary>預設 Payload 上限（1 MB）。大的畫面幀由上層分塊。</summary>
    public const int DefaultMaxPayloadSize = 1024 * 1024;

    /// <summary>Length 欄位大小。</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Length 之後的固定表頭：Type(1) + RequestId(4) + Sequence(4)。</summary>
    public const int HeaderSize = 1 + 4 + 4;

    /// <summary>把一個封包序列化成位元組。</summary>
    public static byte[] Encode(in Frame frame, int maxPayloadSize = DefaultMaxPayloadSize)
    {
        int payloadLength = frame.Payload.Length;
        if (payloadLength > maxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"Payload {payloadLength} 超過上限 {maxPayloadSize}");
        if (!Enum.IsDefined(frame.Type))
            throw new ArgumentOutOfRangeException(nameof(frame), $"未定義的訊息類型 {(byte)frame.Type}");

        int lengthValue = HeaderSize + payloadLength;
        var buffer = new byte[LengthPrefixSize + lengthValue];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)lengthValue);
        span[4] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], frame.RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(span[9..], frame.Sequence);
        frame.Payload.Span.CopyTo(span[(LengthPrefixSize + HeaderSize)..]);

        return buffer;
    }

    /// <summary>
    /// 只讀 Length 欄位並檢查範圍，回傳整個封包（含 Length 欄位）的大小。
    /// 資料不足 4 位元組時回傳 -1。
    /// </summary>
    /// <exception cref="InvalidDataException">長度不合法。</exception>
    public static int PeekTotalSize(ReadOnlySpan<byte> buffer, int maxPayloadSize = DefaultMaxPayloadSize)
    {
        if (buffer.Length < LengthPrefixSize)
            return -1;

        uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (lengthValue < HeaderSize)
            throw new InvalidDataException($"封包長度 {lengthValue} 小於表頭大小");
        if (lengthValue > (uint)(HeaderSize + maxPayloadSize))
            throw new InvalidDataException($"封包長度 {lengthValue} 超過上限 {HeaderSize + maxPayloadSize}");

        return LengthPrefixSize + (int)lengthValue;
    }

    /// <summary>
    /// 從緩衝區試著解出一個完整封包。資料不夠時回傳 false（consumed = 0），呼叫端讀更多再試。
    /// </summary>
    /// <exception cref="InvalidDataException">長度不合法或未知訊息類型。</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed,
        int maxPayloadSize = DefaultMaxPayloadSize)
    {
        frame = default;
        consumed = 0;

        int totalSize = PeekTotalSize(buffer, maxPayloadSize);
        if (totalSize < 0 || buffer.Length < totalSize)
            return false;

        var type = (MessageType)buffer[4];
        if (!Enum.IsDefined(type))
            throw new InvalidDataException($"未知的訊息類型 {(byte)type}");

        uint requestId = BinaryPrimitives.ReadUInt32BigEndian(buffer[5..]);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(buffer[9..]);
        var payload = buffer[(LengthPrefixSize + HeaderSize)..totalSize].ToArray();

        frame = new Frame(type, requestId, sequence, payload);
        consumed = totalSize;
        return true;
    }
}
