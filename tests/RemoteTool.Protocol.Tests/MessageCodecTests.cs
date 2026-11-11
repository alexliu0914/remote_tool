using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class MessageCodecTests
{
    private static T RoundTrip<T>(T message) where T : IMessage
    {
        var frame = new Frame(T.Type, 0, 1, MessageCodec.Serialize(message));
        Assert.True(FrameCodec.TryDecode(FrameCodec.Encode(frame), out var decoded, out _));
        return MessageCodec.Decode<T>(decoded);
    }

    [Fact]
    public void Hello_RoundTrips_WithCapabilities()
    {
        var d = RoundTrip(new Hello(1, ChannelKind.Video, ["h264", "tiles"]));
        Assert.Equal((1, ChannelKind.Video), (d.ProtocolVersion, d.Role));
        Assert.Equal(new[] { "h264", "tiles" }, d.Capabilities);
    }

    [Fact]
    public void LoginRequest_RoundTrips_WithUnicode()
        => Assert.Equal(new LoginRequest("使用者", "密碼 p@ss"), RoundTrip(new LoginRequest("使用者", "密碼 p@ss")));

    [Fact]
    public void LoginResult_RoundTrips()
        => Assert.Equal(new LoginResult(false, AuthError.TooManyAttempts, 30), RoundTrip(new LoginResult(false, AuthError.TooManyAttempts, 30)));

    [Fact]
    public void TokenMessages_RoundTrip()
    {
        var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var g = RoundTrip(new SessionTokenGrant(ChannelKind.File, token, 30));
        Assert.Equal((ChannelKind.File, 30), (g.Role, g.ExpiresInSeconds));
        Assert.Equal(token, g.Token);
        Assert.Equal(ChannelKind.Video, RoundTrip(new SessionTokenRequest(ChannelKind.Video)).Role);
        Assert.Equal(token, RoundTrip(new SubConnectChallenge(token)).Nonce);
        Assert.Equal(token, RoundTrip(new SubConnectAuth(token)).Mac);
        Assert.Equal(new SubConnectResult(true, AuthError.None), RoundTrip(new SubConnectResult(true, AuthError.None)));
    }

    [Fact]
    public void Heartbeat_Keyframe_Error_RoundTrip()
    {
        Assert.Equal(new Heartbeat(long.MaxValue), RoundTrip(new Heartbeat(long.MaxValue)));
        Assert.Equal(new KeyframeRequest(9), RoundTrip(new KeyframeRequest(9)));
        Assert.Equal(new ErrorMessage(ErrorCode.VersionMismatch, "錯誤"), RoundTrip(new ErrorMessage(ErrorCode.VersionMismatch, "錯誤")));
    }

    [Fact]
    public void Decode_WrongType_Throws()
    {
        var frame = new Frame(MessageType.Heartbeat, 0, 1, MessageCodec.Serialize(new Heartbeat(1)));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode<LoginRequest>(frame));
    }

    [Fact]
    public void Decode_GarbagePayload_Throws()
        => Assert.Throws<InvalidDataException>(() =>
            MessageCodec.Decode<LoginRequest>(new Frame(MessageType.LoginRequest, 0, 1, new byte[] { 0xC1, 0xFF, 0x00 })));

    [Fact]
    public void Decode_TrailingBytes_Throws()
    {
        var payload = MessageCodec.Serialize(new Heartbeat(5)).Append((byte)0).ToArray();
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode<Heartbeat>(new Frame(MessageType.Heartbeat, 0, 1, payload)));
    }

    [Fact]
    public void Decode_HugeDeclaredArray_IsRejectedWithoutAllocating()
    {
        // VideoTileUpdate：[FrameId, W, H, EndOfFrame, Tiles]，Tiles 宣稱有 2^32-1 個元素，實際只有幾個位元組
        var payload = new byte[] { 0x95, 0x01, 0x01, 0x01, 0xC3, 0xDD, 0xFF, 0xFF, 0xFF, 0xFF, 0x90 };
        Assert.Throws<InvalidDataException>(() =>
            MessageCodec.Decode<VideoTileUpdate>(new Frame(MessageType.VideoTileUpdate, 0, 1, payload)));
    }

    [Fact]
    public void Decode_HugeDeclaredString_IsRejected()
    {
        // LoginRequest：[Username, Password]，Username 宣稱 2^32-1 位元組
        var payload = new byte[] { 0x92, 0xDB, 0xFF, 0xFF, 0xFF, 0xFF, 0x41 };
        Assert.Throws<InvalidDataException>(() =>
            MessageCodec.Decode<LoginRequest>(new Frame(MessageType.LoginRequest, 0, 1, payload)));
    }

    [Fact]
    public void Decode_DeepNesting_IsRejected()
    {
        // Hello 的 Capabilities 位置放入 100 層巢狀陣列
        var payload = new List<byte> { 0x93, 0x01, 0x01 };
        for (int i = 0; i < 100; i++) payload.Add(0x91);
        payload.Add(0xC0);
        Assert.Throws<InvalidDataException>(() =>
            MessageCodec.Decode<Hello>(new Frame(MessageType.Hello, 0, 1, payload.ToArray())));
    }

    [Fact]
    public void SubConnectProof_DependsOnTokenNonceAndRole()
    {
        var token = new byte[32]; token[0] = 1;
        var nonce = new byte[32]; nonce[0] = 2;
        var mac = SubConnectProof.Compute(token, nonce, ChannelKind.Video);
        Assert.Equal(32, mac.Length);
        Assert.Equal(mac, SubConnectProof.Compute(token, nonce, ChannelKind.Video));
        Assert.NotEqual(mac, SubConnectProof.Compute(token, nonce, ChannelKind.File));
        var otherNonce = (byte[])nonce.Clone(); otherNonce[1] = 9;
        Assert.NotEqual(mac, SubConnectProof.Compute(token, otherNonce, ChannelKind.Video));
        var otherToken = (byte[])token.Clone(); otherToken[1] = 9;
        Assert.NotEqual(mac, SubConnectProof.Compute(otherToken, nonce, ChannelKind.Video));
    }
}
