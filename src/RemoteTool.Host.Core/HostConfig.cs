using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteTool.Host.Core;

public sealed class AccountRecord
{
    public required string Username { get; init; }
    public required string PasswordHash { get; set; }
}

/// <summary>被控端設定（主機私鑰、帳號）。整份以 <see cref="ISecretProtector"/>（DPAPI）加密存放。</summary>
public sealed class HostSettings
{
    public int Version { get; set; } = 1;

    /// <summary>主機私鑰 PKCS#8（Base64）。整份檔案已加密。</summary>
    public string? HostKeyPkcs8 { get; set; }

    public List<AccountRecord> Accounts { get; set; } = [];

    /// <summary>關閉無人值守：每次連線需本機按「允許」（M1 系統列實作）。</summary>
    public bool RequireLocalApproval { get; set; }
}

[JsonSerializable(typeof(HostSettings))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class HostSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// 設定檔存放於使用者的 %LOCALAPPDATA%\RemoteTool\host.dat（第四節）。
/// 寫入時先寫暫存檔再取代，避免中途失敗損毀主機金鑰。
/// </summary>
public sealed class HostConfigStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;

    public HostConfigStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public string Path => _path;

    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteTool");

    public HostSettings Load()
    {
        if (!File.Exists(_path))
            return new HostSettings();
        var json = _protector.Unprotect(File.ReadAllBytes(_path));
        return JsonSerializer.Deserialize(json, HostSettingsJsonContext.Default.HostSettings)
               ?? throw new InvalidDataException("設定檔內容無效");
    }

    public void Save(HostSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, HostSettingsJsonContext.Default.HostSettings);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, _protector.Protect(json));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>載入主機身分；沒有就產生並儲存（第一次啟動）。</summary>
    public HostIdentity LoadOrCreateIdentity(HostSettings settings)
    {
        if (settings.HostKeyPkcs8 is { } b64)
            return HostIdentity.FromPkcs8(Convert.FromBase64String(b64));

        var identity = HostIdentity.CreateNew();
        settings.HostKeyPkcs8 = Convert.ToBase64String(identity.ExportPkcs8());
        Save(settings);
        return identity;
    }
}
