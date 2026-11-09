using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteTool.Protocol;

/// <summary>
/// 主機指紋：一律為 SHA-256(SubjectPublicKeyInfo)（第六節）。
/// 同一把金鑰重新簽發憑證時指紋不變；不使用憑證本身的指紋。
/// </summary>
public static class HostFingerprint
{
    /// <summary>計算憑證公鑰的 SHA-256(SPKI)。</summary>
    public static byte[] Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return SHA256.HashData(spki);
    }

    /// <summary>以 16 進位、每 4 字元一組、空白分隔顯示（例：<c>AB12 CD34 …</c>）。</summary>
    public static string Format(ReadOnlySpan<byte> fingerprint)
    {
        string hex = Convert.ToHexString(fingerprint);
        var sb = new StringBuilder(hex.Length + hex.Length / 4);
        for (int i = 0; i < hex.Length; i += 4)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(hex, i, Math.Min(4, hex.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>解析 <see cref="Format"/> 的輸出（容許空白與冒號）。</summary>
    public static byte[] Parse(string text)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length != SHA256.HashSizeInBytes)
            throw new FormatException("指紋長度必須為 32 位元組");
        return bytes;
    }
}
