using System.Security.Cryptography;

namespace RemoteTool.Protocol;

/// <summary>
/// 子連線授權證明：子連線不直接送出 Token，被控端先送一次性 nonce，
/// 控制端回 HMAC-SHA256(Token, nonce ‖ 角色)（第七節）。
/// TLS exporter（RFC 9266）綁定待 Spike 0 確認 .NET 是否可取得，目前未綁定。
/// </summary>
public static class SubConnectProof
{
    public const int Size = HMACSHA256.HashSizeInBytes;

    public static byte[] Compute(ReadOnlySpan<byte> token, ReadOnlySpan<byte> nonce, ChannelRole role)
    {
        if (token.Length != ProtocolConstants.SessionTokenSize)
            throw new ArgumentException("Token 長度錯誤", nameof(token));
        if (nonce.Length != ProtocolConstants.NonceSize)
            throw new ArgumentException("Nonce 長度錯誤", nameof(nonce));

        Span<byte> data = stackalloc byte[ProtocolConstants.NonceSize + 1];
        nonce.CopyTo(data);
        data[^1] = (byte)role;
        return HMACSHA256.HashData(token, data);
    }

    /// <summary>以固定時間比較驗證證明。</summary>
    public static bool Verify(ReadOnlySpan<byte> token, ReadOnlySpan<byte> nonce, ChannelRole role,
        ReadOnlySpan<byte> proof)
    {
        if (proof.Length != Size)
            return false;
        var expected = Compute(token, nonce, role);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }
}
