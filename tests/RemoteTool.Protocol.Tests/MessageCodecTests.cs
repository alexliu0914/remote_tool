using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class MessageCodecTests
{
    private static T RoundTrip<T>(ChannelKind channel, T message) where T : IMessage
    {
        var frame = MessageCodec.Encode(channel, message);
        Assert.Equal(T.Type, frame.Type);
        Assert.Equal(channel, frame.Channel);

        // 經過線路格式再解回來
        Assert.True(FrameCodec.TryDecode(FrameCodec.Encode(frame), out var decodedFrame, out _));
        return MessageCodec.Decode<T>(decodedFrame);
    }

    [Fact]
    public void Hello_RoundTrips()
        => Assert.Equal(new Hello(1, ChannelKind.Video), RoundTrip(ChannelKind.Video, new Hello(1, ChannelKind.Video)));

    [Fact]
    public void LoginRequest_RoundTrips_WithUnicode()
    {
        var msg = new LoginRequest("使用者", "密碼 p@ss");
        Assert.Equal(msg, RoundTrip(ChannelKind.Control, msg));
    }

    [Fact]
    public void LoginResult_RoundTrips()
    {
        var msg = new LoginResult(false, AuthError.TooManyAttempts, 30);
        Assert.Equal(msg, RoundTrip(ChannelKind.Control, msg));
    }

    [Fact]
    public void SessionTokenGrant_RoundTrips()
    {
        var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var decoded = RoundTrip(ChannelKind.Control, new SessionTokenGrant(ChannelKind.File, token));
        Assert.Equal(ChannelKind.File, decoded.Channel);
        Assert.Equal(token, decoded.Token);
    }

    [Fact]
    public void SubConnect_And_Heartbeat_And_Error_RoundTrip()
    {
        Assert.Equal(new byte[] { 7, 8 }, RoundTrip(ChannelKind.Video, new SubConnectAuth(new byte[] { 7, 8 })).Token);
        Assert.Equal(new SubConnectResult(true, AuthError.None), RoundTrip(ChannelKind.File, new SubConnectResult(true, AuthError.None)));
        Assert.Equal(new Heartbeat(long.MaxValue), RoundTrip(ChannelKind.Control, new Heartbeat(long.MaxValue)));
        Assert.Equal(new ErrorMessage("錯誤"), RoundTrip(ChannelKind.Control, new ErrorMessage("錯誤")));
    }

    [Fact]
    public void Decode_WrongType_Throws()
    {
        var frame = MessageCodec.Encode(ChannelKind.Control, new Heartbeat(1));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode<LoginRequest>(frame));
    }

    [Fact]
    public void Decode_GarbagePayload_Throws()
    {
        var frame = new Frame(ChannelKind.Control, MessageType.LoginRequest, new byte[] { 0xC1, 0xFF, 0x00 });
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode<LoginRequest>(frame));
    }

    [Fact]
    public void Decode_TrailingBytes_Throws()
    {
        var good = MessageCodec.Encode(ChannelKind.Control, new Heartbeat(5));
        var padded = new Frame(good.Channel, good.Type, good.Payload.ToArray().Append((byte)0).ToArray());
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode<Heartbeat>(padded));
    }

    [Fact]
    public void IsKnownType_RejectsUndefined()
    {
        Assert.True(MessageCodec.IsKnownType(MessageType.Heartbeat));
        Assert.False(MessageCodec.IsKnownType((MessageType)199));
    }
}
