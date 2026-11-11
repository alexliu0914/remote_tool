namespace RemoteTool.Connection;

/// <summary>
/// 本機資料位置。預設為 %LOCALAPPDATA%\RemoteTool（只有目前使用者可存取）；
/// 可用環境變數 REMOTETOOL_HOME 覆寫（測試或同一台電腦跑多組時用）。
/// </summary>
public static class AppPaths
{
    public const string HomeEnvironmentVariable = "REMOTETOOL_HOME";

    public static string DataDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            string dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteTool");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>被控端憑證與私鑰。</summary>
    public static string HostCertificateFile => Path.Combine(DataDirectory, "host-cert.pfx");

    /// <summary>被控端帳密（只存 PBKDF2 雜湊）。</summary>
    public static string HostCredentialsFile => Path.Combine(DataDirectory, "host-credentials.json");

    /// <summary>控制端已信任的主機指紋。</summary>
    public static string KnownHostsFile => Path.Combine(DataDirectory, "known-hosts.json");

    /// <summary>控制端上次輸入的位址、埠與帳號（不含密碼）。</summary>
    public static string ControllerSettingsFile => Path.Combine(DataDirectory, "controller-settings.json");
}
