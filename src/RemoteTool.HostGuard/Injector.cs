using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteTool.Protocol;

namespace RemoteTool.HostGuard;

/// <summary>
/// 唯一呼叫 SendInput 的地方（PLAN §3）。注入與按鍵狀態表在**同一個執行緒**內更新：
/// SendInput 成功後才把按下的鍵 / 按鈕加入狀態表，放開成功後移除；釋放時只放開狀態表中的項目，不碰本機使用者按的其他鍵。
/// </summary>
internal sealed class Injector : IDisposable
{
    private readonly BlockingCollection<GuardRecord?> _queue = new(boundedCapacity: 1024);
    private readonly Thread _thread;
    private readonly Action<string> _log;
    private readonly HashSet<(ushort Scan, bool Extended)> _keysDown = new();
    private readonly HashSet<MouseButton> _buttonsDown = new();
    private long _injected, _failed;
    private bool _reportedFailure;

    public Injector(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "HostGuard 注入" };
        _thread.Start();
    }

    public long Injected => Interlocked.Read(ref _injected);

    /// <summary>排入一筆已驗證的記錄（佇列滿時丟棄，輸入不可無限堆積）。</summary>
    public void Enqueue(GuardRecord record)
    {
        if (!_queue.TryAdd(record)) Interlocked.Increment(ref _failed);
    }

    /// <summary>放開狀態表中的所有鍵與按鈕（Host 結束、Pipe 中斷時）。</summary>
    public void ReleaseAll() => _queue.TryAdd(GuardRecord.ReleaseAll(0), 1000);

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item is not { } r) break;
            try { Apply(r); }
            catch (Exception ex) { _log($"注入失敗：{ex.Message}"); }
        }
        ReleaseAllNow();
    }

    private void Apply(GuardRecord r)
    {
        switch (r.Type)
        {
            case GuardRecordType.Key:
            {
                uint flags = KEYEVENTF_SCANCODE | (r.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (r.Down ? 0 : KEYEVENTF_KEYUP);
                if (!SendKey(r.Value16, flags)) return;
                if (r.Down) _keysDown.Add((r.Value16, r.Extended));
                else _keysDown.Remove((r.Value16, r.Extended));
                break;
            }
            case GuardRecordType.MouseMove:
                SendMouse(r.X, r.Y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0);
                break;
            case GuardRecordType.MouseButton:
            {
                var button = (MouseButton)r.Value16;
                var (flags, data) = ButtonFlags(button, r.Down);
                if (!SendMouse(r.X, r.Y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | flags, data)) return;
                if (r.Down) _buttonsDown.Add(button);
                else _buttonsDown.Remove(button);
                break;
            }
            case GuardRecordType.MouseWheel:
                SendMouse(r.X, r.Y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                                    | (r.Horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL), unchecked((uint)(int)(short)r.Value16));
                break;
            case GuardRecordType.ReleaseAll:
                ReleaseAllNow();
                break;
            default:
                break; // B1b / B1d 之後的類型
        }
    }

    private void ReleaseAllNow()
    {
        int keys = _keysDown.Count, buttons = _buttonsDown.Count;
        foreach (var (scan, ext) in _keysDown.ToList())
            SendKey(scan, KEYEVENTF_SCANCODE | (ext ? KEYEVENTF_EXTENDEDKEY : 0) | KEYEVENTF_KEYUP);
        foreach (var b in _buttonsDown.ToList())
        {
            var (flags, data) = ButtonFlags(b, down: false);
            SendMouseRelative(flags, data);
        }
        _keysDown.Clear();
        _buttonsDown.Clear();
        if (keys + buttons > 0) _log($"已放開遠端按住的 {keys} 個鍵、{buttons} 個滑鼠按鍵");
    }

    private static (uint Flags, uint Data) ButtonFlags(MouseButton b, bool down) => b switch
    {
        MouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
        MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
        MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
        MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
        MouseButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        _ => (0u, 0u),
    };

    // ------------------------------------------------------------------ SendInput

    private bool SendKey(ushort scan, uint flags)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags, dwExtraInfo = (IntPtr)GuardRecord.InjectedMarker };
        return Send(ref input);
    }

    private bool SendMouse(int x, int y, uint flags, uint data)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags, dwExtraInfo = (IntPtr)GuardRecord.InjectedMarker };
        return Send(ref input);
    }

    private bool SendMouseRelative(uint flags, uint data)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = data, dwFlags = flags, dwExtraInfo = (IntPtr)GuardRecord.InjectedMarker };
        return Send(ref input);
    }

    private bool Send(ref INPUT input)
    {
        uint sent = SendInput(1, ref input, Marshal.SizeOf<INPUT>());
        if (sent == 1)
        {
            Interlocked.Increment(ref _injected);
            return true;
        }
        Interlocked.Increment(ref _failed);
        if (!_reportedFailure)
        {
            // 常見原因：目標視窗權限較高（UIPI）、安全桌面（UAC / 鎖定）。PLAN §3 故障限制
            _log($"SendInput 失敗（錯誤碼 {Marshal.GetLastWin32Error()}）：可能是前景視窗為系統管理員權限或位於安全桌面");
            _reportedFailure = true;
        }
        return false;
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint XBUTTON1 = 1, XBUTTON2 = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref INPUT input, int size);

    public void Dispose()
    {
        _queue.TryAdd(null, 1000); // 結束訊號：執行緒會先放開所有按鍵
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
