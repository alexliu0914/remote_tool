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

    /// <summary>記錄讀取了多少位元組（驗證「先檢查長度、再讀取 / 配置」）。</summary>
    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var r = base.ReadAsync(buffer, cancellationToken);
            BytesRead += r.Result;
            return r;
        }
    }

    private static byte[] Raw(MessageType type, uint seq, byte[]? payload = null, uint requestId = 0)
        => FrameCodec.Encode(new Frame(type, requestId, seq, payload ?? Array.Empty<byte>()));

    private static byte[] HelloBytes(uint seq = 1)
        => Raw(MessageType.Hello, seq, MessageCodec.Serialize(new Hello(1, ChannelKind.Control, [])));

    [Fact]
    public async Task Writer_AssignsSequence_FromOne_AndCarriesRequestId()
    {
        var ms = new MemoryStream();
        var writer = new FrameStream(ms, Endpoint.Controller) { Role = ChannelKind.Control };
        await writer.WriteAsync(new Heartbeat(1), requestId: 77);
        await writer.WriteAsync(new Heartbeat(2));

        var reader = new FrameStream(new TrickleStream(ms.ToArray()), Endpoint.Host) { Role = ChannelKind.Control };
        var f1 = (await reader.ReadAsync())!.Value;
        var f2 = (await reader.ReadAsync())!.Value;
        Assert.Equal((1u, 77u), (f1.Sequence, f1.RequestId));
        Assert.Equal((2u, 0u), (f2.Sequence, f2.RequestId));
        Assert.Equal(2, MessageCodec.Decode<Heartbeat>(f2).Timestamp);
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public void NextRequestId_IsNonZeroAndIncreasing()
    {
        var s = new FrameStream(new MemoryStream(), Endpoint.Controller);
        uint a = s.NextRequestId(), b = s.NextRequestId();
        Assert.NotEqual(0u, a);
        Assert.True(b > a);
    }

    [Theory]
    [InlineData(2u)]  // 跳號
    [InlineData(0u)]
    public async Task WrongSequence_IsProtocolViolation(uint seq)
    {
        var reader = new FrameStream(new MemoryStream(HelloBytes(seq)), Endpoint.Host);
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
        Assert.Equal(ErrorCode.ProtocolViolation, ex.Code);
    }

    [Fact]
    public async Task RepeatedSequence_IsProtocolViolation()
    {
        var data = HelloBytes(1).Concat(Raw(MessageType.Heartbeat, 1, MessageCodec.Serialize(new Heartbeat(0)))).ToArray();
        var reader = new FrameStream(new MemoryStream(data), Endpoint.Host);
        await reader.ReadAsync();
        reader.Role = ChannelKind.Control;
        await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task UnknownType_IsRejected()
    {
        var reader = new FrameStream(new MemoryStream(Raw((MessageType)199, 1)), Endpoint.Host) { Role = ChannelKind.Control };
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
        Assert.Equal(ErrorCode.UnknownMessageType, ex.Code);
    }

    [Fact]
    public async Task TypeBeforeHello_IsRejected()
    {
        var reader = new FrameStream(new MemoryStream(Raw(MessageType.LoginRequest, 1)), Endpoint.Host);
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
        Assert.Equal(ErrorCode.ProtocolViolation, ex.Code);
    }

    [Fact]
    public async Task TypeNotAllowedForRoleOrDirection_IsRejected()
    {
        // Video 連線上，控制端送出 VideoTileUpdate（只能由被控端送）
        var reader = new FrameStream(new MemoryStream(Raw(MessageType.VideoTileUpdate, 1)), Endpoint.Host) { Role = ChannelKind.Video };
        await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());

        // Video 連線上送 LoginRequest
        var reader2 = new FrameStream(new MemoryStream(Raw(MessageType.LoginRequest, 1)), Endpoint.Host) { Role = ChannelKind.Video };
        await Assert.ThrowsAsync<ProtocolException>(async () => await reader2.ReadAsync());
    }

    [Fact]
    public async Task Writer_RefusesDisallowedType()
    {
        var writer = new FrameStream(new MemoryStream(), Endpoint.Host) { Role = ChannelKind.Control };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.WriteAsync(new LoginRequest("a", "b")));
    }

    [Fact]
    public async Task OversizedLength_IsRejected_BeforeReadingBody()
    {
        var data = new byte[64];
        data[0] = 0x7F; data[1] = 0xFF; data[2] = 0xFF; data[3] = 0xFF;
        var stream = new CountingStream(data);
        var reader = new FrameStream(stream, Endpoint.Host);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());
        Assert.Equal(4, stream.BytesRead); // 只讀了 Length 欄位
    }

    [Fact]
    public async Task TruncatedBody_ThrowsEndOfStream()
    {
        var bytes = HelloBytes();
        var reader = new FrameStream(new MemoryStream(bytes[..^1]), Endpoint.Host);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task TruncatedLength_ThrowsEndOfStream()
    {
        var reader = new FrameStream(new MemoryStream(new byte[] { 0, 0 }), Endpoint.Host);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }
}
