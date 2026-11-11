using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteTool.Connection;

/// <summary>
/// 被控端自簽憑證（ECDSA P-256）。第一次啟動時產生並存成 PFX，之後沿用，指紋因此固定。
/// 控制端不檢查憑證鏈與到期日，只比對 SPKI 指紋。
/// </summary>
public static class HostCertificate
{
    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            byte[] pfx = CreatePfx();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, pfx);
        }

        return Load(File.ReadAllBytes(path));
    }

    /// <summary>只在記憶體中產生（測試用）。</summary>
    public static X509Certificate2 CreateEphemeral() => Load(CreatePfx());

    private static byte[] CreatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=RemoteTool Host", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return cert.Export(X509ContentType.Pfx);
    }

    // Windows 的 Schannel 不接受「暫時」私鑰，因此一律經由 PFX 匯入。
    private static X509Certificate2 Load(byte[] pfx)
        => X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
