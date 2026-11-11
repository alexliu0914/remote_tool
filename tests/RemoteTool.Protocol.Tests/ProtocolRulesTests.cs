using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class ProtocolRulesTests
{
    [Fact]
    public void UndefinedValues_AreUnknown()
    {
        Assert.True(ProtocolRules.IsKnown(MessageType.Heartbeat));
        Assert.False(ProtocolRules.IsKnown((MessageType)199));
        Assert.False(ProtocolRules.IsKnown((MessageType)0));
        Assert.False(ProtocolRules.IsKnown((MessageType)0xFFFF));
    }

    [Fact]
    public void BeforeHello_OnlyHelloAndError()
    {
        Assert.True(ProtocolRules.IsAllowed(MessageType.Hello, null, Endpoint.Controller));
        Assert.True(ProtocolRules.IsAllowed(MessageType.Error, null, Endpoint.Host));
        Assert.False(ProtocolRules.IsAllowed(MessageType.LoginRequest, null, Endpoint.Controller));
    }

    [Theory]
    [InlineData(MessageType.LoginRequest, ChannelKind.Control, Endpoint.Controller, true)]
    [InlineData(MessageType.LoginRequest, ChannelKind.Control, Endpoint.Host, false)]       // 方向錯
    [InlineData(MessageType.LoginRequest, ChannelKind.Video, Endpoint.Controller, false)]   // 角色錯
    [InlineData(MessageType.SubConnectAuth, ChannelKind.File, Endpoint.Controller, true)]
    [InlineData(MessageType.SubConnectAuth, ChannelKind.Control, Endpoint.Controller, false)]
    [InlineData(MessageType.SessionTokenGrant, ChannelKind.Video, Endpoint.Host, false)]    // token 只在 Control
    [InlineData(MessageType.VideoTileUpdate, ChannelKind.Video, Endpoint.Host, true)]
    [InlineData(MessageType.VideoTileUpdate, ChannelKind.Video, Endpoint.Controller, false)]
    [InlineData(MessageType.KeyframeRequest, ChannelKind.Video, Endpoint.Controller, true)]
    [InlineData(MessageType.KeyEvent, ChannelKind.File, Endpoint.Controller, false)]
    [InlineData(MessageType.Heartbeat, ChannelKind.Control, Endpoint.Host, true)]
    public void RoleAndDirection_AreEnforced(MessageType type, ChannelKind role, Endpoint sender, bool allowed)
        => Assert.Equal(allowed, ProtocolRules.IsAllowed(type, role, sender));

    [Fact]
    public void EveryDefinedType_HasARule()
    {
        foreach (var t in Enum.GetValues<MessageType>())
            Assert.True(ProtocolRules.IsKnown(t), $"{t} 沒有規則");
    }
}
