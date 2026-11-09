using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Protocol;

namespace RemoteTool.Host.Core;

/// <summary>
/// 主機身分（第六節）：ECDSA P-256 金鑰對 + 自簽憑證。
/// 每次啟動都以同一把金鑰重新簽發憑證，指紋 SHA-256(SPKI) 不變。
/// </summary>
public sealed class HostIdentity : IDisposable
{
    private HostIdentity(ECDsa key, X509Certificate2 certificate)
    {
        Key = key;
        Certificate = certificate;
        Fingerprint = HostFingerprint.Compute(certificate);
    }

    public ECDsa Key { get; }
    public X509Certificate2 Certificate { get; }
    public byte[] Fingerprint { get; }
    public string FingerprintText => HostFingerprint.Format(Fingerprint);

    /// <summary>產生新的主機金鑰。</summary>
    public static HostIdentity CreateNew() => FromKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>由 PKCS#8 私鑰載入。</summary>
    public static HostIdentity FromPkcs8(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        if (key.KeySize != 256)
            throw new CryptographicException("主機金鑰必須為 P-256");
        return FromKey(key);
    }

    /// <summary>匯出 PKCS#8 私鑰（呼叫端須以 DPAPI 保護後才寫入磁碟）。</summary>
    public byte[] ExportPkcs8() => Key.ExportPkcs8PrivateKey();

    private static HostIdentity FromKey(ECDsa key)
    {
        var request = new CertificateRequest("CN=RemoteTool Host", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        // Windows SChannel 不接受只在記憶體的暫時金鑰作為伺服器憑證，需經 PFX 往返載入。
        var pfx = ephemeral.Export(X509ContentType.Pkcs12);
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, flags);
        CryptographicOperations.ZeroMemory(pfx);

        return new HostIdentity(key, certificate);
    }

    public void Dispose()
    {
        Certificate.Dispose();
        Key.Dispose();
    }
}
