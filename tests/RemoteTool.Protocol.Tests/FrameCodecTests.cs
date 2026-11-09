using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_Then_Decode_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = FrameCodec.Encode(ChannelKind.Control, MessageType.KeyEvent, payload);

        bool ok = FrameCodec.TryDecode(encoded, out var frame, out int consumed);

        Assert.True(ok);
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(ChannelKind.Control, frame.Channel);
        Assert.Equal(MessageType.KeyEvent, frame.Type);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void Encode_EmptyPayload_RoundTrips()
    {
        var encoded = FrameCodec.Encode(ChannelKind.Control, MessageType.Heartbeat, ReadOnlyMemory<byte>.Empty);

        bool ok = FrameCodec.TryDecode(encoded, out var frame, out int consumed);

        Assert.True(ok);
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(MessageType.Heartbeat, frame.Type);
        Assert.Empty(frame.Payload.ToArray());
    }

    [Fact]
    public void TryDecode_IncompleteLengthPrefix_ReturnsFalse()
    {
        var ok = FrameCodec.TryDecode(new byte[] { 0, 0 }, out _, out int consumed);
        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_IncompleteBody_ReturnsFalse()
    {
        var encoded = FrameCodec.Encode(ChannelKind.Video, MessageType.VideoFrame, new byte[] { 9, 9, 9, 9 });
        // 只餵前半段，應該回傳 false 等更多資料
        var partial = encoded.AsSpan(0, encoded.Length - 2).ToArray();

        var ok = FrameCodec.TryDecode(partial, out _, out int consumed);

        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_TwoFramesInBuffer_ConsumesFirstOnly()
    {
        var first = FrameCodec.Encode(ChannelKind.Control, MessageType.KeyEvent, new byte[] { 1 });
        var second = FrameCodec.Encode(ChannelKind.File, MessageType.FileChunk, new byte[] { 2, 3 });
        var buffer = first.Concat(second).ToArray();

        var ok = FrameCodec.TryDecode(buffer, out var frame, out int consumed);

        Assert.True(ok);
        Assert.Equal(first.Length, consumed);
        Assert.Equal(MessageType.KeyEvent, frame.Type);

        // 第二個封包從 consumed 之後解
        var ok2 = FrameCodec.TryDecode(buffer.AsSpan(consumed), out var frame2, out _);
        Assert.True(ok2);
        Assert.Equal(MessageType.FileChunk, frame2.Type);
        Assert.Equal(ChannelKind.File, frame2.Channel);
    }

    [Fact]
    public void Encode_PayloadOverLimit_Throws()
    {
        var tooBig = new byte[FrameCodec.MaxPayloadSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameCodec.Encode(ChannelKind.File, MessageType.FileChunk, tooBig));
    }

    [Fact]
    public void TryDecode_LengthOverLimit_Throws()
    {
        // 偽造一個長度欄位超過上限的封包標頭
        var bogus = new byte[8];
        uint huge = (uint)(FrameCodec.MaxPayloadSize + 100);
        bogus[0] = (byte)(huge >> 24);
        bogus[1] = (byte)(huge >> 16);
        bogus[2] = (byte)(huge >> 8);
        bogus[3] = (byte)huge;

        Assert.Throws<InvalidDataException>(() => FrameCodec.TryDecode(bogus, out _, out _));
    }
}
