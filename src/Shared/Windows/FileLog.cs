using System.Diagnostics;
using System.Text;

namespace RemoteTool.Windows;

/// <summary>
/// 簡易記錄：寫入 %LOCALAPPDATA%\RemoteTool\logs\&lt;name&gt;.log 與主控台；警告另嘗試寫入 Windows 事件記錄
/// （來源「RemoteTool」由安裝程式於 M4 註冊；未註冊時略過）。不得寫入密碼、Token 或剪貼簿內容。
/// </summary>
internal sealed class FileLog
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public FileLog(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteTool", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, name + ".log");
    }

    public string FilePath => _path;

    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Environment.ProcessId}] {level} {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch (IOException) { }
        }
    }

    public void WriteEvent(string message, EventLogEntryType type)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            if (EventLog.SourceExists("RemoteTool"))
                EventLog.WriteEntry("RemoteTool", message, type);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
