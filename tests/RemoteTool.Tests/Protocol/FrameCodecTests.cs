using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public void Encode_Then_Decode_RoundTripsAllHeaderFields()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = FrameCodec.Encode(new Frame(MessageType.KeyEvent, 0xAABBCCDD, 7, payload));

        Assert.True(FrameCodec.TryDecode(encoded, out var frame, out int consumed));
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(MessageType.KeyEvent, frame.Type);
        Assert.Equal(0xAABBCCDDu, frame.RequestId);
        Assert.Equal(7u, frame.Sequence);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void Header_LayoutMatchesSpec()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 1, 2, new byte[] { 9 }));
        // [Length=10][Type][RequestId][Sequence][Payload]
        Assert.Equal(new byte[] { 0, 0, 0, 10, (byte)MessageType.Heartbeat, 0, 0, 0, 1, 0, 0, 0, 2, 9 }, encoded);
    }

    [Fact]
    public void TryDecode_Incomplete_ReturnsFalse()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.VideoFrame, 0, 1, new byte[] { 9, 9, 9, 9 }));
        Assert.False(FrameCodec.TryDecode(encoded.AsSpan(0, 2), out _, out int c1));
        Assert.False(FrameCodec.TryDecode(encoded.AsSpan(0, encoded.Length - 1), out _, out int c2));
        Assert.Equal(0, c1);
        Assert.Equal(0, c2);
    }

    [Fact]
    public void TryDecode_TwoFrames_ConsumesFirstOnly()
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

    [Fact]
    public void Encode_PayloadOverLimit_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.Encode(new Frame(MessageType.FileChunk, 0, 1, new byte[FrameCodec.DefaultMaxPayloadSize + 1])));

    [Fact]
    public void TryDecode_LengthOverLimit_Throws()
    {
        var bogus = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bogus, FrameCodec.DefaultMaxPayloadSize + 100);
        Assert.Throws<InvalidDataException>(() => FrameCodec.TryDecode(bogus, out _, out _));
    }

    [Fact]
    public void TryDecode_LengthSmallerThanHeader_Throws()
    {
        var bogus = new byte[] { 0, 0, 0, 3, 1, 2, 3 };
        Assert.Throws<InvalidDataException>(() => FrameCodec.TryDecode(bogus, out _, out _));
    }

    [Fact]
    public void TryDecode_UnknownType_Throws()
    {
        var encoded = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0, 1, ReadOnlyMemory<byte>.Empty));
        encoded[4] = 0xEE;
        Assert.Throws<InvalidDataException>(() => FrameCodec.TryDecode(encoded, out _, out _));
    }

    [Fact]
    public async Task FramedConnection_RejectsSequenceGap()
    {
        var a = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0, 1, ReadOnlyMemory<byte>.Empty));
        var b = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0, 3, ReadOnlyMemory<byte>.Empty)); // 跳號（重放或注入）
        await using var conn = new FramedConnection(new MemoryStream(a.Concat(b).ToArray()));

        Assert.NotNull(await conn.ReceiveAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => conn.ReceiveAsync());
    }

    [Fact]
    public async Task FramedConnection_SendThenReceive_AssignsSequence()
    {
        var ms = new MemoryStream();
        await using (var writer = new FramedConnection(new NonClosingStream(ms)))
        {
            await writer.SendAsync(new RemoteTool.Protocol.Messages.Heartbeat(1));
            await writer.SendAsync(new RemoteTool.Protocol.Messages.Heartbeat(2), requestId: 5);
        }
        ms.Position = 0;
        await using var reader = new FramedConnection(ms);
        var f1 = await reader.ReceiveAsync();
        var f2 = await reader.ReceiveAsync();
        Assert.Equal(1u, f1!.Value.Sequence);
        Assert.Equal(2u, f2!.Value.Sequence);
        Assert.Equal(5u, f2.Value.RequestId);
        Assert.Null(await reader.ReceiveAsync());
    }

    [Fact]
    public async Task FramedConnection_ClosedMidFrame_Throws()
    {
        var a = FrameCodec.Encode(new Frame(MessageType.Heartbeat, 0, 1, new byte[] { 1, 2, 3 }));
        await using var conn = new FramedConnection(new MemoryStream(a[..^1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => conn.ReceiveAsync());
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
