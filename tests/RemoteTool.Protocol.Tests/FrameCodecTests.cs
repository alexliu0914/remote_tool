using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

/// <summary>Protocol V1 分幀：欄位寬度、位元組順序、Length 計算與上限（docs/PROTOCOL_V1.md §2）。</summary>
public class FrameCodecTests
{
    [Fact]
    public void Header_Layout_IsExactlyAsSpecified()
    {
        var bytes = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0x01020304, 0x0A0B0C0D, new byte[] { 0xAA }));
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x0B,       // Length = 10 + 1（big-endian）
            0x00, 0x06,                   // MessageType = Heartbeat(6)
            0x01, 0x02, 0x03, 0x04,       // RequestId
            0x0A, 0x0B, 0x0C, 0x0D,       // Sequence
            0xAA,                         // Payload
        }, bytes);
    }

    [Fact]
    public void Encode_Then_Decode_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = FrameCodec.Encode(new Frame(MessageType.KeyEvent, 7, 42, payload));

        Assert.True(FrameCodec.TryDecode(encoded, out var frame, out int consumed));
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal((MessageType.KeyEvent, 7u, 42u), (frame.Type, frame.RequestId, frame.Sequence));
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void EmptyPayload_HasLengthTen()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0, 1, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(14, encoded.Length);
        Assert.Equal(10, encoded[3]);
        Assert.True(FrameCodec.TryDecode(encoded, out var frame, out _));
        Assert.Empty(frame.Payload.ToArray());
    }

    [Fact]
    public void MaxPayload_Exactly1MiB_IsAccepted()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.FileChunk, 0, 1, new byte[FrameCodec.MaxPayloadSize]));
        Assert.Equal(FrameCodec.MaxFrameSize, encoded.Length);
        Assert.True(FrameCodec.TryDecode(encoded, out _, out _));
    }

    [Fact]
    public void Encode_PayloadOverLimit_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.Encode(new Frame(MessageType.FileChunk, 0, 1, new byte[FrameCodec.MaxPayloadSize + 1])));

    [Fact]
    public void TryDecode_IncompleteLengthPrefix_ReturnsFalse()
    {
        Assert.False(FrameCodec.TryDecode(new byte[] { 0, 0 }, out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_IncompleteBody_ReturnsFalse()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.VideoFrame, 0, 1, new byte[] { 9, 9, 9, 9 }));
        Assert.False(FrameCodec.TryDecode(encoded.AsSpan(0, encoded.Length - 2), out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_TwoFramesInBuffer_ConsumesFirstOnly()
    {
        var first = FrameCodec.Encode(new Frame(MessageType.KeyEvent, 0, 1, new byte[] { 1 }));
        var second = FrameCodec.Encode(new Frame(MessageType.FileChunk, 0, 2, new byte[] { 2, 3 }));
        var buffer = first.Concat(second).ToArray();

        Assert.True(FrameCodec.TryDecode(buffer, out var f1, out int consumed));
        Assert.Equal(first.Length, consumed);
        Assert.Equal(MessageType.KeyEvent, f1.Type);
        Assert.True(FrameCodec.TryDecode(buffer.AsSpan(consumed), out var f2, out _));
        Assert.Equal(MessageType.FileChunk, f2.Type);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9u)]                                   // 小於表頭
    [InlineData(10u + 1024 * 1024 + 1)]                // 超過上限 1 位元組
    [InlineData(uint.MaxValue)]
    public void TryDecode_LengthOutOfRange_Throws(uint length)
    {
        var bogus = new byte[16];
        bogus[0] = (byte)(length >> 24); bogus[1] = (byte)(length >> 16); bogus[2] = (byte)(length >> 8); bogus[3] = (byte)length;
        Assert.Throws<InvalidDataException>(() => FrameCodec.TryDecode(bogus, out _, out _));
    }
}
