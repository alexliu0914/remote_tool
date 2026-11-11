using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class FrameStreamTests
{
    /// <summary>每次 Read 最多回傳 1 位元組，模擬 TCP 分段。</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task WriteThenRead_MultipleFrames_InOrder()
    {
        var ms = new MemoryStream();
        var writer = new FrameStream(ms);
        await writer.WriteAsync(ChannelKind.Control, new Heartbeat(1));
        await writer.WriteAsync(ChannelKind.Control, new Heartbeat(2));

        var reader = new FrameStream(new TrickleStream(ms.ToArray()));
        var f1 = await reader.ReadAsync();
        var f2 = await reader.ReadAsync();
        var end = await reader.ReadAsync();

        Assert.Equal(1, MessageCodec.Decode<Heartbeat>(f1!.Value).Sequence);
        Assert.Equal(2, MessageCodec.Decode<Heartbeat>(f2!.Value).Sequence);
        Assert.Null(end);
    }

    [Fact]
    public async Task Read_TruncatedBody_ThrowsEndOfStream()
    {
        var bytes = FrameCodec.Encode(ChannelKind.Video, MessageType.VideoFrame, new byte[] { 1, 2, 3 });
        var reader = new FrameStream(new MemoryStream(bytes[..^1]));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task Read_TruncatedLength_ThrowsEndOfStream()
    {
        var reader = new FrameStream(new MemoryStream(new byte[] { 0, 0 }));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task Read_OversizedLength_ThrowsInvalidData()
    {
        var reader = new FrameStream(new MemoryStream(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1, 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());
    }
}
