namespace RemoteTool.HostGuard.Core;

/// <summary>
/// 實際送進系統的輸入（Windows 實作為 SendInput）。回傳 true 代表 SendInput 回報成功。
/// 注意：輸入被 UIPI 擋下時 SendInput 不一定回報失敗（第三節「故障限制」）。
/// </summary>
public interface IInputInjector
{
    bool Key(ushort scanCode, bool extended, bool down);
    bool MouseMove(ushort x, ushort y);
    bool MouseButton(GuardMouseButton button, bool down);
    bool MouseWheel(short delta, bool horizontal);

    /// <summary>以 KEYEVENTF_UNICODE 逐字送出按下與放開；不留下按住的鍵。</summary>
    bool UnicodeText(string text);
}

/// <summary>
/// HostGuard 自己的「桌面可控制」檢查：未鎖定、在主控台、輸入桌面為 Default。
/// 不只依賴 Host.exe 的通知（第三節「不可控制時不注入」）。
/// </summary>
public interface IDesktopProbe
{
    bool IsControllable();
}

/// <summary>記錄（Windows 上寫入檔案與事件記錄）。</summary>
public interface IGuardLog
{
    void Info(string message);
    void Warn(string message);
}

/// <summary>不輸出的記錄器。</summary>
public sealed class NullGuardLog : IGuardLog
{
    public static readonly NullGuardLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
}
