using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteTool.Connection;

/// <summary>主機指紋 = SHA-256(SubjectPublicKeyInfo)，以冒號分隔的大寫十六進位表示。</summary>
public static class Fingerprint
{
    public static string Compute(X509Certificate2 certificate)
    {
        byte[] spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Format(SHA256.HashData(spki));
    }

    public static string Format(ReadOnlySpan<byte> hash) => string.Join(':', hash.ToArray().Select(b => b.ToString("X2")));

    /// <summary>給人眼比對用：每 8 組換行。</summary>
    public static string ForDisplay(string fingerprint)
    {
        var parts = fingerprint.Split(':');
        return string.Join(Environment.NewLine, parts.Chunk(8).Select(c => string.Join(':', c)));
    }
}
