using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RemoteTool.Connection;

/// <summary>保護（加密）本機秘密檔案內容的方式。</summary>
public interface ISecretProtector
{
    /// <summary>寫入檔頭的名稱，例如 DPAPI。</summary>
    string Name { get; }
    byte[] Protect(byte[] data, string purpose);
    /// <exception cref="CryptographicException">無法解密（不同使用者、不同電腦或資料損毀）。</exception>
    byte[] Unprotect(byte[] data, string purpose);
}

/// <summary>
/// Windows DPAPI，範圍為**目前使用者**（PLAN §4、§6）。只有同一台電腦上的同一個 Windows 使用者能解密；
/// <c>purpose</c> 作為額外熵，讓不同用途的檔案不能互相替換。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    public string Name => "DPAPI";

    public byte[] Protect(byte[] data, string purpose)
        => ProtectedData.Protect(data, Entropy(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, string purpose)
        => ProtectedData.Unprotect(data, Entropy(purpose), DataProtectionScope.CurrentUser);

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("RemoteTool/" + purpose);
}

/// <summary>
/// 不加密（只在非 Windows 平台，例如 CI 與開發用 Linux 測試環境）。產品只在 Windows 執行，因此正式環境一律為 DPAPI。
/// </summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Name => "PLAIN";
    public byte[] Protect(byte[] data, string purpose) => data;
    public byte[] Unprotect(byte[] data, string purpose) => data;
}

public static class SecretProtector
{
    /// <summary>Windows 為 DPAPI（目前使用者），其他平台為不加密。</summary>
    public static ISecretProtector Default { get; } =
        OperatingSystem.IsWindows() ? new DpapiProtector() : new PlainProtector();
}

/// <summary>
/// 秘密檔案格式：<c>RTSEC1:&lt;保護方式&gt;\n</c> + 受保護內容。
/// 沒有檔頭的檔案視為舊版明文（G-03 之前），讀取後由呼叫端改存為受保護格式。
/// </summary>
public static class SecretFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RTSEC1:");

    public static byte[] ReadBytes(string path, ISecretProtector protector, out bool needsRewrite)
    {
        byte[] raw = File.ReadAllBytes(path);
        string purpose = Path.GetFileName(path);

        if (!raw.AsSpan().StartsWith(Magic))
        {
            needsRewrite = true; // 舊版明文
            return raw;
        }

        int newline = Array.IndexOf(raw, (byte)'\n', Magic.Length);
        if (newline < 0) throw new InvalidDataException($"{purpose} 檔頭格式錯誤");
        string name = Encoding.ASCII.GetString(raw, Magic.Length, newline - Magic.Length);
        byte[] body = raw[(newline + 1)..];

        if (name == protector.Name)
        {
            needsRewrite = false;
            try
            {
                return protector.Unprotect(body, purpose);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"{purpose} 無法解密：只有建立它的同一台電腦、同一個 Windows 使用者能讀取。若是從別處複製來的，請刪除後重新設定。", ex);
            }
        }
        if (name == "PLAIN")
        {
            needsRewrite = true; // 在非 Windows 建立後帶到 Windows：改存為 DPAPI
            return body;
        }
        throw new InvalidDataException($"{purpose} 使用本機不支援的保護方式（{name}）");
    }

    public static void WriteBytes(string path, byte[] data, ISecretProtector protector)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        byte[] header = Encoding.ASCII.GetBytes($"RTSEC1:{protector.Name}\n");
        byte[] body = protector.Protect(data, Path.GetFileName(path));

        // 先寫暫存檔再取代，避免寫到一半斷電留下損毀檔
        string temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header);
            fs.Write(body);
            fs.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static string ReadText(string path, ISecretProtector protector, out bool needsRewrite)
        => Encoding.UTF8.GetString(ReadBytes(path, protector, out needsRewrite));

    public static void WriteText(string path, string text, ISecretProtector protector)
        => WriteBytes(path, Encoding.UTF8.GetBytes(text), protector);
}
