using System.Security.Cryptography;
using RemoteTool.Host.Core;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;
using Xunit;

namespace RemoteTool.Tests.Protocol;

public class MessageSerializerTests
{
    [Fact]
    public void RoundTrip_KeyEvent()
    {
        var bytes = MessageSerializer.Serialize(new KeyEvent(3, 0x1D, true, true));
        var back = MessageSerializer.Deserialize<KeyEvent>(bytes);
        Assert.Equal(new KeyEvent(3, 0x1D, true, true), back);
    }

    [Fact]
    public void RoundTrip_HelloWithNonce()
    {
        var nonce = RandomNumberGenerator.GetBytes(ProtocolConstants.NonceSize);
        var back = MessageSerializer.Deserialize<HelloAck>(MessageSerializer.Serialize(new HelloAck(1, HelloStatus.Ok, nonce)));
        Assert.Equal(nonce, back.Nonce);
    }

    [Fact]
    public void Deserialize_Garbage_ThrowsInvalidData()
        => Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize<LoginRequest>(new byte[] { 0xC1, 0xFF, 0x00 }));

    [Fact]
    public void Deserialize_TrailingBytes_ThrowsInvalidData()
    {
        var bytes = MessageSerializer.Serialize(new Heartbeat(1)).Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize<Heartbeat>(bytes));
    }

    [Fact]
    public void Deserialize_OverlongUsername_ThrowsInvalidData()
    {
        // 直接以 MessagePack 產生不合法內容（繞過 Serialize 的送出前檢查）
        var bytes = MessagePack.MessagePackSerializer.Serialize(new object[] { new string('a', 65), "password123" });
        Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize<LoginRequest>(bytes));
    }

    [Fact]
    public void Deserialize_UndefinedEnum_ThrowsInvalidData()
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(new object[] { 1u, (byte)99, true });
        Assert.Throws<InvalidDataException>(() => MessageSerializer.Deserialize<MouseButtonEvent>(bytes));
    }

    [Fact]
    public void Read_WrongFrameType_Throws()
    {
        var frame = new Frame(MessageType.Heartbeat, 0, 1, MessageSerializer.Serialize(new Heartbeat(1)));
        Assert.Throws<InvalidDataException>(() => MessageSerializer.Read<HeartbeatAck>(frame));
    }

    [Fact]
    public void LoginRequest_ToString_DoesNotLeakPassword()
        => Assert.DoesNotContain("secret-pass", new LoginRequest("alex", "secret-pass").ToString());
}

public class HostFingerprintTests
{
    [Fact]
    public void Fingerprint_IsSha256OfSpki()
    {
        using var id = HostIdentity.CreateNew();
        var expected = SHA256.HashData(id.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(expected, id.Fingerprint);
    }

    [Fact]
    public void ReissuedCertificateWithSameKey_KeepsFingerprint()
    {
        using var a = HostIdentity.CreateNew();
        using var b = HostIdentity.FromPkcs8(a.ExportPkcs8());
        Assert.NotEqual(a.Certificate.RawData, b.Certificate.RawData); // 憑證不同
        Assert.Equal(a.Fingerprint, b.Fingerprint);                   // 指紋相同
    }

    [Fact]
    public void Format_GroupsHexByFour_AndParsesBack()
    {
        var fp = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var text = HostFingerprint.Format(fp);
        Assert.StartsWith("0001 0203 ", text);
        Assert.Equal(16, text.Split(' ').Length);
        Assert.Equal(fp, HostFingerprint.Parse(text));
    }
}

public class SubConnectProofTests
{
    private static readonly byte[] Token = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Nonce = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Verify_Correct() =>
        Assert.True(SubConnectProof.Verify(Token, Nonce, ChannelRole.Video, SubConnectProof.Compute(Token, Nonce, ChannelRole.Video)));

    [Fact]
    public void Verify_WrongRole_Fails() =>
        Assert.False(SubConnectProof.Verify(Token, Nonce, ChannelRole.File, SubConnectProof.Compute(Token, Nonce, ChannelRole.Video)));

    [Fact]
    public void Verify_WrongNonce_Fails() =>
        Assert.False(SubConnectProof.Verify(Token, RandomNumberGenerator.GetBytes(32), ChannelRole.Video,
            SubConnectProof.Compute(Token, Nonce, ChannelRole.Video)));
}
