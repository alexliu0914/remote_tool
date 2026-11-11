using System.IO;
using RemoteTool.Connection;

namespace RemoteTool.Controller;

/// <summary>
/// 控制端診斷記錄：%LOCALAPPDATA%\RemoteTool\controller.log。
/// 只記錄事件與例外內容，不記錄密碼、token 或畫面內容。超過 1 MB 時改名為 .old 重新開始。
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Gate = new();

    public static string FilePath => AppPaths.ControllerLogFile;

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);

                string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                if (ex is not null) text += ex + Environment.NewLine;
                File.AppendAllText(FilePath, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 記錄失敗不影響操作
        }
    }
}
