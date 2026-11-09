using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteTool.Windows;

/// <summary>
/// 「桌面可控制」的前兩項檢查（第三節）：工作階段未鎖定且在主控台、輸入桌面為 Default。
/// 行為待 G0-03 於 Windows 11 實機驗證（尤其是 WTSSessionInfoEx 的鎖定旗標與 UAC 安全桌面時 OpenInputDesktop 的結果）。
/// </summary>
internal static class DesktopState
{
    public static int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;

    /// <summary>目前的輸入桌面是否為 Default（鎖定、UAC 時為 Winlogon，使用者權限開不了 → false）。</summary>
    public static bool IsInputDesktopDefault()
    {
        nint desktop = Win32.OpenInputDesktop(0, false, Win32.DESKTOP_READOBJECTS);
        if (desktop == 0)
            return false;
        try
        {
            Win32.GetUserObjectInformationW(desktop, Win32.UOI_NAME, null, 0, out uint needed);
            if (needed == 0 || needed > 1024)
                return false;
            var buffer = new char[needed / sizeof(char)];
            if (!Win32.GetUserObjectInformationW(desktop, Win32.UOI_NAME, buffer, needed, out _))
                return false;
            var name = new string(buffer).TrimEnd('\0');
            return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Win32.CloseDesktop(desktop);
        }
    }

    /// <summary>
    /// 工作階段是否鎖定。無法判定時視為鎖定（寧可暫停也不在不確定時注入）。
    /// </summary>
    public static bool IsSessionLocked(int sessionId)
    {
        if (!Win32.WTSQuerySessionInformationW(0, sessionId, Win32.WTSSessionInfoEx, out nint buffer, out uint bytes))
            return true;
        try
        {
            // WTSINFOEXW { DWORD Level; WTSINFOEX_LEVEL1_W { ULONG SessionId; WTS_CONNECTSTATE_CLASS SessionState; LONG SessionFlags; ... } }
            if (bytes < 16 || Marshal.ReadInt32(buffer, 0) != 1)
                return true;
            int flags = Marshal.ReadInt32(buffer, 12);
            return flags != Win32.WTS_SESSIONSTATE_UNLOCK;
        }
        finally
        {
            Win32.WTSFreeMemory(buffer);
        }
    }

    /// <summary>本工作階段是否為主控台上作用中的工作階段。</summary>
    public static bool IsOnConsole(int sessionId) => Win32.WTSGetActiveConsoleSessionId() == (uint)sessionId;

    public static bool IsControllable(int sessionId)
        => IsOnConsole(sessionId) && !IsSessionLocked(sessionId) && IsInputDesktopDefault();

    public static bool TryGetProcessSession(int processId, out int sessionId)
    {
        bool ok = Win32.ProcessIdToSessionId((uint)processId, out uint sid);
        sessionId = (int)sid;
        return ok;
    }
}
