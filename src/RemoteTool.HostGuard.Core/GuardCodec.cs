using System.Buffers.Binary;
using System.Text;

namespace RemoteTool.HostGuard.Core;

/// <summary>
/// 本機 IPC 的固定二進位格式（不用 MessagePack，讓 HostGuard 保持極小、無外部相依）：
/// <code>
///   [BodyLength: u16 BE][Type: u8][Generation: u32 BE][Body: BodyLength]
/// </code>
/// 各類型 Body 大小固定（UnicodeText 除外，為 UTF-16LE、偶數長度）。
/// </summary>
public static class GuardCodec
{
    public const int HeaderSize = 2 + 1 + 4;
    public const int MaxUnicodeChars = 4096;
    public const int MaxBodySize = MaxUnicodeChars * 2;

    public static byte[] Encode(in GuardMessage m)
    {
        byte[] body = m.Type switch
        {
            GuardMessageType.Key => EncodeKey(m),
            GuardMessageType.MouseMove => EncodeMove(m),
            GuardMessageType.MouseButton => [(byte)m.Button, m.Down ? (byte)1 : (byte)0],
            GuardMessageType.MouseWheel => EncodeWheel(m),
            GuardMessageType.UnicodeText => EncodeText(m.Text),
            GuardMessageType.InputHeartbeat or GuardMessageType.Pause or GuardMessageType.Resume
                or GuardMessageType.ReleaseAll => [],
            _ => throw new ArgumentOutOfRangeException(nameof(m), "不在白名單內的訊息類型"),
        };

        var buffer = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)body.Length);
        buffer[2] = (byte)m.Type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(3), m.Generation);
        body.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    private static byte[] EncodeKey(in GuardMessage m)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(b, m.ScanCode);
        b[2] = (byte)((m.Extended ? 1 : 0) | (m.Down ? 2 : 0));
        return b;
    }

    private static byte[] EncodeMove(in GuardMessage m)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(b, m.X);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), m.Y);
        return b;
    }

    private static byte[] EncodeWheel(in GuardMessage m)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteInt16BigEndian(b, m.WheelDelta);
        b[2] = m.Horizontal ? (byte)1 : (byte)0;
        return b;
    }

    private static byte[] EncodeText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxUnicodeChars)
            throw new ArgumentOutOfRangeException(nameof(text));
        return Encoding.Unicode.GetBytes(text);
    }

    /// <summary>
    /// 解析一個完整訊息（含表頭）。
    /// 回傳 null 表示「格式正確但不在白名單或欄位不合法」：呼叫端丟棄並記錄，<paramref name="reason"/> 說明原因。
    /// </summary>
    public static GuardMessage? Decode(ReadOnlySpan<byte> frame, out string? reason)
    {
        reason = null;
        if (frame.Length < HeaderSize)
        {
            reason = "長度不足";
            return null;
        }
        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(frame);
        if (frame.Length != HeaderSize + bodyLength)
        {
            reason = "長度不符";
            return null;
        }
        var type = (GuardMessageType)frame[2];
        uint generation = BinaryPrimitives.ReadUInt32BigEndian(frame[3..]);
        var body = frame[HeaderSize..];

        switch (type)
        {
            case GuardMessageType.Key when body.Length == 3:
            {
                ushort scan = BinaryPrimitives.ReadUInt16BigEndian(body);
                byte flags = body[2];
                if (scan is 0 or > 0xFF || (flags & ~3) != 0)
                    break;
                return GuardMessage.Key(generation, scan, (flags & 1) != 0, (flags & 2) != 0);
            }
            case GuardMessageType.MouseMove when body.Length == 4:
                return GuardMessage.MouseMove(generation,
                    BinaryPrimitives.ReadUInt16BigEndian(body), BinaryPrimitives.ReadUInt16BigEndian(body[2..]));
            case GuardMessageType.MouseButton when body.Length == 2:
            {
                var button = (GuardMouseButton)body[0];
                if (!Enum.IsDefined(button) || body[1] > 1)
                    break;
                return GuardMessage.MouseButtonEvent(generation, button, body[1] == 1);
            }
            case GuardMessageType.MouseWheel when body.Length == 3:
                if (body[2] > 1)
                    break;
                return GuardMessage.Wheel(generation, BinaryPrimitives.ReadInt16BigEndian(body), body[2] == 1);
            case GuardMessageType.UnicodeText when body.Length > 0 && body.Length % 2 == 0 && body.Length <= MaxBodySize:
                return GuardMessage.Unicode(generation, Encoding.Unicode.GetString(body));
            case GuardMessageType.InputHeartbeat when body.Length == 0:
            case GuardMessageType.Pause when body.Length == 0:
            case GuardMessageType.Resume when body.Length == 0:
            case GuardMessageType.ReleaseAll when body.Length == 0:
                return new GuardMessage(type, generation);
        }

        reason = Enum.IsDefined(type) ? $"{type} 欄位不合法" : $"白名單外的類型 {(byte)type}";
        return null;
    }

    /// <summary>
    /// 從串流讀一個訊息框（不解析內容）。對方關閉時回傳 null。
    /// </summary>
    /// <exception cref="InvalidDataException">長度超過上限（視為對方異常，應中斷 IPC）。</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;
        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (bodyLength > MaxBodySize)
            throw new InvalidDataException($"IPC 訊息長度 {bodyLength} 超過上限");
        var frame = new byte[HeaderSize + bodyLength];
        header.CopyTo(frame, 0);
        if (!await ReadExactAsync(stream, frame.AsMemory(HeaderSize), cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("IPC 訊息在中途關閉");
        return frame;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0)
                    return false;
                throw new InvalidDataException("IPC 訊息在中途關閉");
            }
            total += n;
        }
        return true;
    }
}
