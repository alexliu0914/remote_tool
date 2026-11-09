using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>
/// 封包編解碼。線路格式（見規劃書第七節）：
///
///   [Length: 4 bytes, big-endian][Channel: 1][Type: 1][Payload: N]
///
/// Length = Channel + Type + Payload 的位元組數（即 2 + N）。
/// 單一封包的 Payload 上限 1 MB；超過即視為格式錯誤並拒絕，
/// 以免惡意或損毀的來源要求配置過大的緩衝區。
/// 此型別只處理分幀，不含任何平台專屬功能。
/// </summary>
public static class FrameCodec
{
    /// <summary>Payload 位元組上限（1 MB）。</summary>
    public const int MaxPayloadSize = 1024 * 1024;

    private const int LengthPrefixSize = 4;
    private const int HeaderSize = 2; // Channel + Type
    private const int MaxLengthValue = HeaderSize + MaxPayloadSize;

    /// <summary>把一個封包序列化成位元組。</summary>
    public static byte[] Encode(Frame frame)
    {
        int payloadLength = frame.Payload.Length;
        if (payloadLength > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"Payload {payloadLength} 超過上限 {MaxPayloadSize}");

        int lengthValue = HeaderSize + payloadLength;
        var buffer = new byte[LengthPrefixSize + lengthValue];

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, LengthPrefixSize), (uint)lengthValue);
        buffer[LengthPrefixSize] = (byte)frame.Channel;
        buffer[LengthPrefixSize + 1] = (byte)frame.Type;
        frame.Payload.Span.CopyTo(buffer.AsSpan(LengthPrefixSize + HeaderSize));

        return buffer;
    }

    /// <summary>便捷多載。</summary>
    public static byte[] Encode(ChannelKind channel, MessageType type, ReadOnlyMemory<byte> payload)
        => Encode(new Frame(channel, type, payload));

    /// <summary>
    /// 從緩衝區試著解出一個完整封包。
    /// 回傳 true 並透過 <paramref name="consumed"/> 回報已吃掉的位元組數；
    /// 資料還不夠一個完整封包時回傳 false（consumed = 0），呼叫端應讀更多再試。
    /// </summary>
    /// <exception cref="InvalidDataException">長度欄位超過允許上限（惡意或損毀來源）。</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;

        if (buffer.Length < LengthPrefixSize)
            return false;

        uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(buffer[..LengthPrefixSize]);

        if (lengthValue < HeaderSize)
            throw new InvalidDataException($"封包長度 {lengthValue} 小於標頭大小");
        if (lengthValue > MaxLengthValue)
            throw new InvalidDataException($"封包長度 {lengthValue} 超過上限 {MaxLengthValue}");

        int totalSize = LengthPrefixSize + (int)lengthValue;
        if (buffer.Length < totalSize)
            return false; // 尚未收齊

        var channel = (ChannelKind)buffer[LengthPrefixSize];
        var type = (MessageType)buffer[LengthPrefixSize + 1];

        int payloadLength = (int)lengthValue - HeaderSize;
        var payload = buffer.Slice(LengthPrefixSize + HeaderSize, payloadLength).ToArray();

        frame = new Frame(channel, type, payload);
        consumed = totalSize;
        return true;
    }
}
