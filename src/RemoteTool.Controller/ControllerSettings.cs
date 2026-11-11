using System.IO;
using System.Text.Json;
using RemoteTool.Connection;
using RemoteTool.Protocol;

namespace RemoteTool.Controller;

/// <summary>記住上次輸入的位址、埠與帳號（不存密碼）。</summary>
internal sealed class ControllerSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    public string Username { get; set; } = "";

    public static ControllerSettings Load()
    {
        try
        {
            string path = AppPaths.ControllerSettingsFile;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ControllerSettings>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.ControllerSettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記不住設定不影響連線
        }
    }
}
