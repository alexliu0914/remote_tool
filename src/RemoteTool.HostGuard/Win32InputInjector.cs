using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteTool.HostGuard.Core;
using RemoteTool.Windows;
using static RemoteTool.Windows.Win32;

namespace RemoteTool.HostGuard;

/// <summary>
/// SendInput 注入（第五節）：鍵盤用 KEYEVENTF_SCANCODE；滑鼠用 ABSOLUTE + VIRTUALDESK，座標 0–65535。
/// 待 G0-02 / G0-03 實機驗證；被 UIPI 擋下時 SendInput 不一定回報失敗。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32InputInjector : IInputInjector
{
    /// <summary>標記由 RemoteTool 注入的事件（dwExtraInfo），便於日後在低階 hook 中辨識。</summary>
    private const nint ExtraInfoTag = 0x52544F4C; // "RTOL"

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public bool Key(ushort scanCode, bool extended, bool down)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        return Send([Keyboard(0, scanCode, flags)]);
    }

    public bool MouseMove(ushort x, ushort y)
        => Send([Mouse(x, y, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK)]);

    public bool MouseButton(GuardMouseButton button, bool down)
    {
        (uint flags, uint data) = button switch
        {
            GuardMouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            GuardMouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            GuardMouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            GuardMouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
            GuardMouseButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
            _ => (0u, 0u),
        };
        return flags != 0 && Send([Mouse(0, 0, data, flags)]);
    }

    public bool MouseWheel(short delta, bool horizontal)
        => Send([Mouse(0, 0, unchecked((uint)delta), horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL)]);

    public bool UnicodeText(string text)
    {
        // 每個 UTF-16 單元送出按下與放開（增補字元以兩個代理單元送出），不留下按住的鍵
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[2 * i] = Keyboard(0, text[i], KEYEVENTF_UNICODE);
            inputs[2 * i + 1] = Keyboard(0, text[i], KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
        return Send(inputs);
    }

    private static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = ExtraInfoTag } },
    };

    private static INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = ExtraInfoTag } },
    };

    private static bool Send(INPUT[] inputs)
        => inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;
}

/// <summary>HostGuard 自己的桌面檢查（不只依賴 Host.exe 通知）。結果快取 50 ms 以降低每次注入的成本。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopProbe : IDesktopProbe
{
    private long _checkedAt;
    private bool _last;

    public bool IsControllable()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _checkedAt) < 50)
            return _last;
        _last = DesktopState.IsControllable(DesktopState.CurrentSessionId);
        Volatile.Write(ref _checkedAt, now);
        return _last;
    }
}
