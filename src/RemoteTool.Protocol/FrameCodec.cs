using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>
/// Protocol V1 分幀（所有 Control / Video / File 連線共用）。線路格式，全部 big-endian：
/// <code>
/// 位移  大小  欄位
///  0     4    Length       uint32  = 10 + Payload 長度（Length 欄位之後的位元組數）
///  4     2    MessageType  uint16
///  6     4    RequestId    uint32
/// 10     4    Sequence     uint32
/// 14     N    Payload      MessagePack，N ≤ 1 MiB
/// </code>
/// 接收端讀到 Length 後**先檢查範圍（10 ≤ Length ≤ 10 + 1 MiB）再配置記憶體**。
/// </summary>
public static class FrameCodec
{
    /// <summary>Payload 位元組上限（1 MiB）。超過的資料（例如大畫面）須由上層分塊。</summary>
    public const int MaxPayloadSize = 1024 * 1024;

    /// <summary>Length 欄位大小。</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Length 之後的固定表頭：MessageType(2) + RequestId(4) + Sequence(4)。</summary>
    public const int HeaderSize = 10;

    /// <summary>Length 欄位允許的最大值。</summary>
    public const uint MaxLengthValue = HeaderSize + MaxPayloadSize;

    /// <summary>完整封包（含 Length 欄位）的最大位元組數。</summary>
    public const int MaxFrameSize = LengthPrefixSize + HeaderSize + MaxPayloadSize;

    public static byte[] Encode(Frame frame)
    {
        int payloadLength = frame.Payload.Length;
        if (payloadLength > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(frame), $"Payload {payloadLength} 超過上限 {MaxPayloadSize}");

        var buffer = new byte[LengthPrefixSize + HeaderSize + payloadLength];
        WriteHeader(buffer, frame.Type, frame.RequestId, frame.Sequence, payloadLength);
        frame.Payload.Span.CopyTo(buffer.AsSpan(LengthPrefixSize + HeaderSize));
        return buffer;
    }

    /// <summary>把 14 位元組表頭（含 Length）寫到 <paramref name="destination"/> 開頭。</summary>
    public static void WriteHeader(Span<byte> destination, MessageType type, uint requestId, uint sequence, int payloadLength)
    {
        if ((uint)payloadLength > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(payloadLength), $"Payload {payloadLength} 超過上限 {MaxPayloadSize}");
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(HeaderSize + payloadLength));
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], (ushort)type);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], requestId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[10..], sequence);
    }

    /// <summary>
    /// 從緩衝區試著解出一個完整封包（不檢查 Sequence 與類型規則，那是 <see cref="FrameStream"/> 的工作）。
    /// 資料不足回傳 false（consumed = 0）。
    /// </summary>
    /// <exception cref="InvalidDataException">Length 超出允許範圍。</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (buffer.Length < LengthPrefixSize) return false;

        uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        ValidateLength(lengthValue);

        int total = LengthPrefixSize + (int)lengthValue;
        if (buffer.Length < total) return false;

        byte[] body = buffer.Slice(LengthPrefixSize, (int)lengthValue).ToArray();
        frame = DecodeBody(body, body.Length);
        consumed = total;
        return true;
    }

    /// <summary>解析 Length 之後的內容（長度已驗證）。</summary>
    internal static Frame DecodeBody(byte[] body, int length)
    {
        var span = body.AsSpan(0, length);
        var type = (MessageType)BinaryPrimitives.ReadUInt16BigEndian(span);
        uint requestId = BinaryPrimitives.ReadUInt32BigEndian(span[2..]);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(span[6..]);
        return new Frame(type, requestId, sequence, body.AsMemory(HeaderSize, length - HeaderSize));
    }

    /// <summary>檢查 Length 欄位。</summary>
    /// <exception cref="InvalidDataException">小於表頭或超過上限。</exception>
    public static void ValidateLength(uint lengthValue)
    {
        if (lengthValue < HeaderSize)
            throw new InvalidDataException($"封包長度 {lengthValue} 小於表頭大小 {HeaderSize}");
        if (lengthValue > MaxLengthValue)
            throw new InvalidDataException($"封包長度 {lengthValue} 超過上限 {MaxLengthValue}");
    }
}
