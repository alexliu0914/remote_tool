using System.Runtime.InteropServices;
using System.Windows.Interop;
using static RemoteTool.Controller.NativeMethods;

namespace RemoteTool.Controller;

/// <summary>畫面區收到的輸入（G0-04 驗證；B1 起轉送到被控端）。</summary>
internal sealed record ScreenInput(string Kind, string Detail, int? RemoteX = null, int? RemoteY = null);

/// <summary>要轉送到被控端的輸入（B1）。座標為遠端畫面正規化座標 0～65535。</summary>
internal abstract record RemoteInput;
internal sealed record RemoteKey(ushort ScanCode, bool Extended, bool Down) : RemoteInput;
internal sealed record RemoteMouseMove(int X, int Y) : RemoteInput;
internal sealed record RemoteMouseButton(RemoteTool.Protocol.MouseButton Button, bool Down, int X, int Y) : RemoteInput;
internal sealed record RemoteMouseWheel(int Delta, bool Horizontal, int X, int Y) : RemoteInput;
internal sealed record RemoteReleaseAll : RemoteInput;

/// <summary>
/// 畫面區（A2b，PLAN §4 / D-013）：以 HwndHost 嵌入 Win32 子視窗，D3D11 交換鏈直接繪製在這個視窗上。
/// HwndHost 內的 WPF 收不到滑鼠、鍵盤與拖放，因此由這個視窗的 WndProc 與 RegisterDragDrop 自己處理。
/// 建立 D3D11 失敗時 <see cref="Renderer"/> 為 null，由呼叫端改用 WPF 顯示。
/// </summary>
internal sealed class RemoteScreenHost : HwndHost
{
    private const string ClassName = "RemoteToolScreen";
    private static bool _classRegistered;
    private IntPtr _hwnd;
    private FileDropTarget? _dropTarget;

    public D3DRenderer? Renderer { get; private set; }
    public string? RendererError { get; private set; }

    /// <summary>畫面區收到輸入時（UI 執行緒）。</summary>
    public event Action<ScreenInput>? InputReceived;
    /// <summary>從檔案總管拖入檔案或資料夾時（UI 執行緒）：路徑清單與放下位置（遠端座標，可能為 null）。</summary>
    public event Action<IReadOnlyList<string>, int?, int?>? FilesDropped;
    /// <summary>需要重畫（大小改變、重新取得裝置）。</summary>
    public event Action? RedrawRequested;
    /// <summary>要轉送到被控端的輸入（UI 執行緒，B1）。</summary>
    public event Action<RemoteInput>? RemoteInputReceived;

    /// <summary>因帶有 HostGuard 注入標記而忽略的輸入數（單機測試時的迴圈防護）。</summary>
    public long IgnoredInjected { get; private set; }

    private bool _capturing;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr instance = GetModuleHandle(null);
        if (!_classRegistered)
        {
            // 視窗程序用 DefWindowProc；HwndHost 會把訊息轉給下面的 WndProc 覆寫
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _classRegistered = true;
        }

        _hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("無法建立畫面區視窗");

        try
        {
            Renderer = new D3DRenderer(_hwnd, 1, 1);
        }
        catch (Exception ex)
        {
            Renderer = null;
            RendererError = ex.Message;
            DiagnosticLog.Write("D3D11 畫面區無法建立，改用 WPF 顯示", ex);
        }

        // 畫面區不使用本機輸入法：按鍵以掃描碼送到遠端，由遠端輸入法處理中文（PLAN §5.2）
        ImmAssociateContext(_hwnd, IntPtr.Zero);

        OleInitialize(IntPtr.Zero); // WPF 執行緒為 STA；已初始化時回傳 S_FALSE
        _dropTarget = new FileDropTarget(_hwnd, OnDrop);
        int hr = RegisterDragDrop(_hwnd, _dropTarget);
        if (hr < 0) DiagnosticLog.Write($"畫面區 RegisterDragDrop 失敗（0x{hr:X8}）");

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        RevokeDragDrop(hwnd.Handle);
        Renderer?.Dispose();
        Renderer = null;
        DestroyWindow(hwnd.Handle);
    }

    private static bool IsInputMessage(int msg) =>
        msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK or WM_RBUTTONDOWN or WM_RBUTTONUP
            or WM_MBUTTONDOWN or WM_MBUTTONUP or WM_MOUSEWHEEL or WM_MOUSEHWHEEL or WM_XBUTTONDOWN or WM_XBUTTONUP
            or WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP;

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 單機測試時，HostGuard 注入的輸入會回到控制端；帶有注入標記的一律不轉送，避免迴圈
        if (IsInputMessage(msg) && (uint)(long)GetMessageExtraInfo() == RemoteTool.Protocol.GuardRecord.InjectedMarker)
        {
            IgnoredInjected++;
            if (msg is WM_SYSKEYDOWN or WM_SYSKEYUP) { handled = true; return IntPtr.Zero; }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        switch (msg)
        {
            case WM_SIZE:
                Renderer?.Resize(LoWord(lParam) & 0xFFFF, HiWord(lParam) & 0xFFFF);
                RedrawRequested?.Invoke();
                break;
            case WM_ERASEBKGND:
                handled = true;
                return (IntPtr)1; // 由 D3D 繪製，不讓 GDI 先塗背景造成閃爍
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                RedrawRequested?.Invoke();
                handled = true;
                return IntPtr.Zero;
            case WM_GETDLGCODE:
                handled = true; // 讓方向鍵、Tab 等也送到畫面區（不被 WPF 拿去切換焦點）
                return (IntPtr)(DLGC_WANTALLKEYS | DLGC_WANTCHARS | DLGC_WANTARROWS | DLGC_WANTTAB);

            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_LBUTTONDBLCLK or WM_XBUTTONDOWN:
            {
                SetFocus(hwnd); // 點畫面區即取得鍵盤焦點
                var button = ButtonOf(msg, wParam);
                Report(button switch
                {
                    RemoteTool.Protocol.MouseButton.Left => msg == WM_LBUTTONDBLCLK ? "左鍵雙擊" : "左鍵按下",
                    RemoteTool.Protocol.MouseButton.Right => "右鍵按下",
                    RemoteTool.Protocol.MouseButton.Middle => "中鍵按下",
                    _ => "側鍵按下",
                }, lParam);
                if (ToRemoteNormalized(LoWord(lParam), HiWord(lParam), clamp: false) is { } p)
                {
                    SetCapture(hwnd); // 拖曳到畫面區外仍收得到放開
                    _capturing = true;
                    RemoteInputReceived?.Invoke(new RemoteMouseButton(button, true, p.X, p.Y));
                }
                break;
            }
            case WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP:
            {
                var button = ButtonOf(msg, wParam);
                Report(button switch
                {
                    RemoteTool.Protocol.MouseButton.Left => "左鍵放開",
                    RemoteTool.Protocol.MouseButton.Right => "右鍵放開",
                    RemoteTool.Protocol.MouseButton.Middle => "中鍵放開",
                    _ => "側鍵放開",
                }, lParam);
                if (ToRemoteNormalized(LoWord(lParam), HiWord(lParam), clamp: true) is { } p)
                    RemoteInputReceived?.Invoke(new RemoteMouseButton(button, false, p.X, p.Y));
                if (_capturing && (int)wParam == 0) { _capturing = false; ReleaseCapture(); } // 所有按鍵都放開
                break;
            }
            case WM_MOUSEMOVE:
            {
                Report("滑鼠移動", lParam);
                // 拖曳中（已擷取）超出畫面時貼齊邊緣；一般移動在黑邊時不送
                if (ToRemoteNormalized(LoWord(lParam), HiWord(lParam), clamp: _capturing) is { } p)
                    RemoteInputReceived?.Invoke(new RemoteMouseMove(p.X, p.Y));
                break;
            }
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
            {
                int delta = HiWord(wParam);
                var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) }; // 滾輪座標為螢幕座標
                ScreenToClient(hwnd, ref pt);
                var (rx, ry) = ToRemote(pt.X, pt.Y);
                InputReceived?.Invoke(new ScreenInput(msg == WM_MOUSEWHEEL ? "滾輪" : "水平滾輪", $"{delta:+0;-0}", rx, ry));
                if (ToRemoteNormalized(pt.X, pt.Y, clamp: false) is { } p)
                    RemoteInputReceived?.Invoke(new RemoteMouseWheel(delta, msg == WM_MOUSEHWHEEL, p.X, p.Y));
                break;
            }
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
            {
                int scan = (int)(((long)lParam >> 16) & 0xFF);
                bool extended = (((long)lParam >> 24) & 1) != 0;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                InputReceived?.Invoke(new ScreenInput(down ? "按鍵按下" : "按鍵放開",
                    $"VK 0x{(int)wParam:X2}、掃描碼 0x{scan:X2}{(extended ? "（延伸）" : "")}"));
                if (scan != 0) RemoteInputReceived?.Invoke(new RemoteKey((ushort)scan, extended, down));
                if (msg is WM_SYSKEYDOWN or WM_SYSKEYUP) { handled = true; return IntPtr.Zero; } // Alt 組合鍵不交給 WPF 選單
                break;
            }
            case WM_CHAR:
            {
                char c = (char)(int)wParam;
                InputReceived?.Invoke(new ScreenInput("字元", char.IsControl(c) ? $"U+{(int)c:X4}" : $"「{c}」 U+{(int)c:X4}"));
                break;
            }
            case WM_SETFOCUS:
                InputReceived?.Invoke(new ScreenInput("焦點", "畫面區取得鍵盤焦點"));
                break;
            case WM_KILLFOCUS:
                InputReceived?.Invoke(new ScreenInput("焦點", "畫面區失去鍵盤焦點"));
                RemoteInputReceived?.Invoke(new RemoteReleaseAll()); // 失去焦點：放開遠端按住的鍵
                break;
            case WM_CAPTURECHANGED:
                _capturing = false;
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static RemoteTool.Protocol.MouseButton ButtonOf(int msg, IntPtr wParam) => msg switch
    {
        WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK => RemoteTool.Protocol.MouseButton.Left,
        WM_RBUTTONDOWN or WM_RBUTTONUP => RemoteTool.Protocol.MouseButton.Right,
        WM_MBUTTONDOWN or WM_MBUTTONUP => RemoteTool.Protocol.MouseButton.Middle,
        _ => HiWord(wParam) == 1 ? RemoteTool.Protocol.MouseButton.X1 : RemoteTool.Protocol.MouseButton.X2,
    };

    /// <summary>畫面區座標 → 遠端正規化座標（0～65535）；<paramref name="clamp"/> 為 false 時畫面外回傳 null。</summary>
    public (int X, int Y)? ToRemoteNormalized(int x, int y, bool clamp)
    {
        var r = Renderer;
        if (r is null || r.FrameWidth < 2 || r.FrameHeight < 2 || r.Scale <= 0) return null;
        var d = r.DestinationRect;
        if (!clamp && (x < d.Left || y < d.Top || x >= d.Right || y >= d.Bottom)) return null;
        double fx = Math.Clamp((x - d.Left) / r.Scale, 0, r.FrameWidth - 1);
        double fy = Math.Clamp((y - d.Top) / r.Scale, 0, r.FrameHeight - 1);
        return ((int)Math.Round(fx * 65535 / (r.FrameWidth - 1)), (int)Math.Round(fy * 65535 / (r.FrameHeight - 1)));
    }

    /// <summary>
    /// 畫面區有焦點時，按鍵訊息會先經過 WPF（KeyboardNavigation 會把 Tab、方向鍵拿去切換焦點，G4-05）。
    /// 這裡直接把按鍵訊息翻譯並送給子視窗，回傳 true 讓 WPF 不再處理；WM_CHAR 仍由 TranslateMessage 產生（輸入法不受影響）。
    /// </summary>
    protected override bool TranslateAcceleratorCore(ref System.Windows.Interop.MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (msg.hwnd == _hwnd && msg.message is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            return true;
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    /// <summary>焦點在畫面區時，不讓 WPF 以 Tab 把焦點移出去。</summary>
    protected override bool TabIntoCore(System.Windows.Input.TraversalRequest request)
    {
        SetFocus(_hwnd);
        return true;
    }

    private void Report(string kind, IntPtr lParam)
    {
        int x = LoWord(lParam), y = HiWord(lParam);
        var (rx, ry) = ToRemote(x, y);
        InputReceived?.Invoke(new ScreenInput(kind, $"畫面區 ({x},{y})", rx, ry));
    }

    /// <summary>畫面區座標 → 遠端畫面座標（在畫面範圍外時為 null）。B1 以此換算後送出。</summary>
    public (int? X, int? Y) ToRemote(int x, int y)
    {
        var r = Renderer;
        if (r is null || r.FrameWidth == 0 || r.Scale <= 0) return (null, null);
        var d = r.DestinationRect;
        if (x < d.Left || y < d.Top || x >= d.Right || y >= d.Bottom) return (null, null);
        int rx = (int)((x - d.Left) / r.Scale), ry = (int)((y - d.Top) / r.Scale);
        return (Math.Clamp(rx, 0, r.FrameWidth - 1), Math.Clamp(ry, 0, r.FrameHeight - 1));
    }

    private void OnDrop(IReadOnlyList<string> files, POINT client)
    {
        var (rx, ry) = ToRemote(client.X, client.Y);
        FilesDropped?.Invoke(files, rx, ry);
    }
}
