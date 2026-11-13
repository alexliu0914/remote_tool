using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;

namespace RemoteTool.Controller;

/// <summary>
/// 遠端畫面視窗。A2b 起畫面區為 HwndHost 嵌入的 Win32 子視窗，以 D3D11 / Direct2D 繪製（D-013），
/// 鍵鼠與拖放由該視窗自己接收（G0-04）；D3D11 無法建立時改用 WPF WriteableBitmap 顯示。
/// 背景執行緒把封包解到 <see cref="FrameCanvas"/>；UI 執行緒只上傳變動範圍，跟不上時多張合併成一次，不排隊。
/// </summary>
public partial class RemoteWindow : Window
{
    private readonly ControllerSession _session;
    private readonly VideoReceiver _receiver;
    private readonly FrameCanvas _canvas;
    private readonly DispatcherTimer _statsTimer;
    private readonly object _dirtyGate = new();
    private readonly RemoteScreenHost _screen = new();
    private PixelRect _pendingDirty;
    private bool _renderScheduled;
    private bool _useD3D;
    private WriteableBitmap? _bitmap;
    private int _frameWidth, _frameHeight;
    private int _framesSinceTick;
    private long _bytesAtLastTick;
    private bool _closing;
    private bool _fit = true;
    private bool _refreshRequested;
    private bool _pendingKeyframe;
    private string _note = "";
    private string _fileNote = "";
    private string _stats = "等待畫面…";
    private string? _overlay;

    // G0-04 驗證：畫面區收到的輸入統計
    private long _mouseEvents, _keyEvents, _drops;

    // B1：遠端控制。連到本機（127.0.0.1）時預設關閉，避免單機測試時滑鼠被自己注入的移動拉走
    private bool _remoteControl;
    private long _sentInputs;
    private const uint Generation = 1; // 輸入世代（B1d 起由被控端暫停 / 恢復時更新）
    private string _lastInput = "";
    private long _lastInputUiUpdate;

    /// <summary>非 null 表示因連線中斷而關閉，內容為原因。</summary>
    public string? EndReason { get; private set; }

    internal RemoteWindow(ControllerSession session, VideoReceiver receiver)
    {
        InitializeComponent();
        _session = session;
        _receiver = receiver;
        _canvas = receiver.Canvas;
        Title = $"RemoteTool — {session.HostKey}";

        HostContainer.Child = _screen;
        _screen.InputReceived += OnScreenInput;
        _screen.RemoteInputReceived += OnRemoteInput;
        _remoteControl = !IsLoopback(session.HostKey);
        ApplyControlButton();
        _screen.FilesDropped += OnFilesDropped;
        _screen.RedrawRequested += () => OnFrameCompleted(default);
        HostContainer.SizeChanged += (_, _) => UpdateScrollBars();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnStatsTick, Dispatcher);
        Loaded += (_, _) =>
        {
            _useD3D = _screen.Renderer is not null;
            if (!_useD3D)
            {
                D3DPanel.Visibility = Visibility.Collapsed;
                WpfPanel.Visibility = Visibility.Visible;
                _note = $"顯示：WPF（D3D11 無法使用：{_screen.RendererError}）";
            }
            else
            {
                DiagnosticLog.Write($"畫面區：D3D11 / Direct2D（{_screen.Renderer!.AdapterName}）");
            }
            _statsTimer.Start();
            // 視窗開啟前收到的畫面整張繪出
            lock (_canvas.SyncRoot)
            {
                if (_canvas.Buffer.Width > 0)
                    OnFrameCompleted(new PixelRect(0, 0, _canvas.Buffer.Width, _canvas.Buffer.Height));
            }
        };
        _session.SubChannelStateChanged += (role, state, detail) =>
            Dispatcher.BeginInvoke(() => OnSubChannelState(role, state, detail));
        _ = WatchSessionAsync();
    }

    // ------------------------------------------------------------------ 子連線狀態

    private void OnSubChannelState(ChannelKind role, SubChannelState state, string? detail)
    {
        DiagnosticLog.Write($"{role} 子連線：{state}{(detail is null ? "" : "（" + detail + "）")}");
        if (role == ChannelKind.Video)
        {
            _overlay = state switch
            {
                SubChannelState.Reconnecting => "畫面重新連線中…\n（目前顯示的是最後一張畫面，非即時）",
                SubChannelState.Failed => $"畫面連線失敗：{detail}\n控制連線仍在，可按工具列的「重試畫面連線」。",
                _ => null,
            };
            VideoRetryButton.Visibility = state == SubChannelState.Failed ? Visibility.Visible : Visibility.Collapsed;
            // WPF 備援路徑用 WPF 覆蓋層；D3D 路徑在繪製時畫上去（WPF 無法疊在 HwndHost 上）
            VideoOverlay.Visibility = _overlay is null ? Visibility.Collapsed : Visibility.Visible;
            VideoOverlayText.Text = _overlay ?? "";
            if (_screen.Renderer is { } r)
            {
                r.Overlay = _overlay;
                OnFrameCompleted(default);
            }
        }
        else
        {
            _fileNote = state switch
            {
                SubChannelState.Reconnecting => "檔案連線重新連線中…",
                SubChannelState.Failed => "檔案連線失敗",
                _ => "",
            };
            if (state == SubChannelState.Failed) _session.RetrySubChannel(ChannelKind.File); // 檔案連線自動再試
        }
        UpdateStatus();
    }

    private void OnRetryVideo(object sender, RoutedEventArgs e) => _session.RetrySubChannel(ChannelKind.Video);

    private async Task WatchSessionAsync()
    {
        string reason = await _session.Completion;
        DiagnosticLog.Write($"工作階段結束 {_session.HostKey}：{reason}");
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing) return;
            EndReason = reason;
            Close();
        });
    }

    // ------------------------------------------------------------------ 繪製

    /// <summary>背景執行緒呼叫：一張畫面已解到畫布（dirty 為空表示只需重畫）。</summary>
    internal void OnFrameCompleted(PixelRect dirty, bool isKeyframe = false)
    {
        lock (_dirtyGate)
        {
            _pendingDirty = _pendingDirty.Union(dirty);
            _pendingKeyframe |= isKeyframe;
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Render);
    }

    private void Render()
    {
        PixelRect dirty;
        bool keyframe;
        lock (_dirtyGate)
        {
            dirty = _pendingDirty;
            keyframe = _pendingKeyframe;
            _pendingDirty = default;
            _pendingKeyframe = false;
            _renderScheduled = false;
        }
        if (keyframe && _refreshRequested)
        {
            _refreshRequested = false;
            _note = $"畫面已重新整理（{DateTime.Now:HH:mm:ss}）";
            UpdateStatus();
        }

        if (_useD3D && _screen.Renderer is { } renderer)
        {
            bool sizeChanged;
            lock (_canvas.SyncRoot)
            {
                var buffer = _canvas.Buffer;
                if (buffer.Width > 0)
                {
                    if (renderer.DeviceRecreated)
                    {
                        dirty = new PixelRect(0, 0, buffer.Width, buffer.Height);
                        renderer.DeviceRecreated = false;
                    }
                    if (!dirty.IsEmpty || renderer.FrameWidth != buffer.Width) renderer.Upload(buffer, dirty);
                }
                sizeChanged = buffer.Width != _frameWidth || buffer.Height != _frameHeight;
                _frameWidth = buffer.Width;
                _frameHeight = buffer.Height;
            }
            if (sizeChanged) UpdateScrollBars();
            renderer.Draw();
            if (!dirty.IsEmpty) _framesSinceTick++;
            return;
        }

        lock (_canvas.SyncRoot)
        {
            var buffer = _canvas.Buffer;
            if (buffer.Width == 0) return;

            if (_bitmap is null || _bitmap.PixelWidth != buffer.Width || _bitmap.PixelHeight != buffer.Height)
            {
                // DXGI 的 alpha 不可靠，以 Bgr32 顯示（忽略 alpha）。
                _bitmap = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgr32, null);
                ScreenImage.Source = _bitmap;
                ApplyFit();
                dirty = new PixelRect(0, 0, buffer.Width, buffer.Height);
            }
            _frameWidth = buffer.Width;
            _frameHeight = buffer.Height;

            if (dirty.IsEmpty) return;
            int x = Math.Clamp(dirty.X, 0, buffer.Width), y = Math.Clamp(dirty.Y, 0, buffer.Height);
            int w = Math.Min(dirty.Width, buffer.Width - x), h = Math.Min(dirty.Height, buffer.Height - y);
            if (w <= 0 || h <= 0) return;

            _bitmap.WritePixels(new Int32Rect(x, y, w, h), buffer.Pixels, buffer.Stride, x, y);
        }
        _framesSinceTick++;
    }

    // ------------------------------------------------------------------ 顯示模式與捲軸

    private void ApplyFit()
    {
        FitButton.Content = _fit ? "顯示：符合視窗" : "顯示：原尺寸";
        ScreenImage.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        Scroller.HorizontalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (_screen.Renderer is { } r) r.Fit = _fit;
        UpdateScrollBars();
        OnFrameCompleted(default);
    }

    private void OnFitToggle(object sender, RoutedEventArgs e)
    {
        _fit = !_fit;
        ApplyFit();
    }

    /// <summary>原尺寸時依畫面區大小（實際像素）設定捲軸範圍。</summary>
    private void UpdateScrollBars()
    {
        if (!_useD3D || _screen.Renderer is not { } r) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int viewW = (int)(HostContainer.ActualWidth * dpi.DpiScaleX), viewH = (int)(HostContainer.ActualHeight * dpi.DpiScaleY);
        bool needH = !_fit && _frameWidth > viewW, needV = !_fit && _frameHeight > viewH;
        HScroll.Visibility = needH ? Visibility.Visible : Visibility.Collapsed;
        VScroll.Visibility = needV ? Visibility.Visible : Visibility.Collapsed;
        HScroll.Maximum = Math.Max(0, _frameWidth - viewW);
        VScroll.Maximum = Math.Max(0, _frameHeight - viewH);
        HScroll.ViewportSize = viewW;
        VScroll.ViewportSize = viewH;
        HScroll.LargeChange = viewW * 0.9;
        VScroll.LargeChange = viewH * 0.9;
        HScroll.SmallChange = VScroll.SmallChange = 40;
        if (!needH) HScroll.Value = 0;
        if (!needV) VScroll.Value = 0;
        r.ScrollX = (int)HScroll.Value;
        r.ScrollY = (int)VScroll.Value;
    }

    private void OnScroll(object sender, ScrollEventArgs e)
    {
        if (_screen.Renderer is not { } r) return;
        r.ScrollX = (int)HScroll.Value;
        r.ScrollY = (int)VScroll.Value;
        OnFrameCompleted(default);
    }

    // ------------------------------------------------------------------ B1：遠端控制

    private static bool IsLoopback(string hostKey)
    {
        string host = hostKey[..hostKey.LastIndexOf(':')].Trim('[', ']');
        return host is "localhost" or "::1" || host.StartsWith("127.");
    }

    private void ApplyControlButton()
        => ControlButton.Content = _remoteControl ? "遠端控制：開" : "遠端控制：關";

    private void OnControlToggle(object sender, RoutedEventArgs e)
    {
        _remoteControl = !_remoteControl;
        if (!_remoteControl) _session.SendInput(new ReleaseAllInput(Generation)); // 關閉時放開遠端按住的鍵
        ApplyControlButton();
        UpdateInputText();
        DiagnosticLog.Write($"遠端控制：{(_remoteControl ? "開" : "關")}");
    }

    private void OnRemoteInput(RemoteInput input)
    {
        if (!_remoteControl && input is not RemoteReleaseAll) return;
        switch (input)
        {
            case RemoteKey k: _session.SendInput(new KeyEvent(Generation, k.ScanCode, k.Extended, k.Down)); break;
            case RemoteMouseMove m: _session.SendInput(new MouseMoveEvent(Generation, m.X, m.Y)); break;
            case RemoteMouseButton b: _session.SendInput(new MouseButtonEvent(Generation, b.Button, b.Down, b.X, b.Y)); break;
            case RemoteMouseWheel w: _session.SendInput(new MouseWheelEvent(Generation, w.Delta, w.Horizontal, w.X, w.Y)); break;
            case RemoteReleaseAll: _session.SendInput(new ReleaseAllInput(Generation)); break;
        }
        _sentInputs++;
    }

    // ------------------------------------------------------------------ G0-04：畫面區輸入與拖放

    private void OnScreenInput(ScreenInput input)
    {
        if (input.Kind.StartsWith("按鍵") || input.Kind == "字元") _keyEvents++;
        else if (input.Kind != "焦點") _mouseEvents++;

        string where = input.RemoteX is int rx ? $" → 遠端 ({rx},{input.RemoteY})" : "";
        _lastInput = $"{input.Kind}：{input.Detail}{where}";

        // 滑鼠移動很頻繁，畫面最多每 100 毫秒更新一次；其他事件立即更新
        long now = Environment.TickCount64;
        if (input.Kind == "滑鼠移動" && now - _lastInputUiUpdate < 100) return;
        _lastInputUiUpdate = now;
        UpdateInputText();
    }

    private void UpdateInputText()
    {
        string mode = _remoteControl ? $"遠端控制：開（已送出 {_sentInputs} 個事件）" : "遠端控制：關（只顯示，不送出）";
        string loop = _screen.IgnoredInjected > 0 ? $"、忽略本機注入 {_screen.IgnoredInjected} 次" : "";
        InputText.Text = $"畫面區輸入｜{mode}｜{_lastInput}　｜　滑鼠 {_mouseEvents} 次、鍵盤 {_keyEvents} 次、拖放 {_drops} 次{loop}";
    }

    private async void OnFilesDropped(IReadOnlyList<string> paths, int? remoteX, int? remoteY)
    {
        _drops++;
        UpdateInputText();
        int files = paths.Count(File.Exists), folders = paths.Count(Directory.Exists);
        string time = DateTime.Now.ToString("HH:mm:ss");
        DropText.Text = $"最近一次拖放（{time}）：{files} 個檔案、{folders} 個資料夾（計算中…）";

        // 資料夾內的檔案數在背景計算（上限 100,000，避免拖入整顆磁碟時卡住）
        long total = await Task.Run(() => CountFiles(paths, 100_000));
        string where = remoteX is int rx ? $"，放在遠端 ({rx},{remoteY})" : "";
        string first = paths.Count > 0 ? Path.GetFileName(paths[0].TrimEnd('\\')) : "";
        DropText.Text = $"最近一次拖放（{time}）：{files} 個檔案、{folders} 個資料夾，共 {total:N0} 個檔案{(total >= 100_000 ? " 以上" : "")}{where}" +
                        $"｜第一項：{first}｜累計 {_drops} 次（傳輸於 M3 實作）";
        DiagnosticLog.Write($"畫面區拖放：{files} 個檔案、{folders} 個資料夾、共 {total} 個檔案；第一項：{paths.FirstOrDefault()}");
    }

    private static long CountFiles(IReadOnlyList<string> paths, long limit)
    {
        long count = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p)) count++;
            else if (Directory.Exists(p))
            {
                try
                {
                    foreach (var _ in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        if (++count >= limit) return count;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (count >= limit) break;
        }
        return count;
    }

    // ------------------------------------------------------------------ 狀態列

    private int _ticks;

    /// <summary>
    /// 每 60 秒把記憶體用量寫入 controller.log（HD-06）：私有記憶體、GC 已提交、原生（估計）。
    /// 原生包含 D3D11 / Direct2D / 硬體解碼器的緩衝區（內顯時位於共用系統記憶體）。
    /// </summary>
    private void LogMemory()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long priv = p.PrivateMemorySize64 / 1048576, committed = info.TotalCommittedBytes / 1048576;
        DiagnosticLog.Write($"記憶體：工作集 {p.WorkingSet64 / 1048576} MB、私有 {priv} MB｜GC 已提交 {committed} MB、" +
            $"受控堆積（GC 後）{info.HeapSizeBytes / 1048576} MB｜原生（估計）{priv - committed} MB｜" +
            $"GC 0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}｜畫面區 {(int)HostContainer.ActualWidth}×{(int)HostContainer.ActualHeight}");
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_ticks++ % 60 == 0) LogMemory();
        long bytes = _session.VideoBytesReceived;
        double kbps = (bytes - _bytesAtLastTick) * 8 / 1000.0;
        _bytesAtLastTick = bytes;

        string size = _frameWidth == 0 ? "—" : $"{_frameWidth}×{_frameHeight}";
        string rtt = _session.LastRoundTrip is { } t ? $"{t.TotalMilliseconds:0} ms" : "—";
        string display = _useD3D ? "D3D11" : "WPF";
        _stats = $"{size}    {_framesSinceTick} FPS    {kbps:0} kbps    心跳 {rtt}    {_receiver.CurrentFormat}    顯示：{display}";
        _framesSinceTick = 0;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string> { _stats };
        if (_note.Length > 0) parts.Add(_note);
        if (_fileNote.Length > 0) parts.Add(_fileNote);
        // 重連次數一直顯示（重連通常 1～2 秒就完成，提示容易錯過）
        int v = _session.GetReconnectCount(ChannelKind.Video), f = _session.GetReconnectCount(ChannelKind.File);
        if (v + f > 0) parts.Add($"已重連：畫面 {v} 次、檔案 {f} 次");
        StatusText.Text = string.Join("    ｜ ", parts);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        _refreshRequested = true;
        _note = "已要求重新整理…";
        UpdateStatus();
        try
        {
            await _session.RequestKeyframeAsync(_canvas.LastFrameId);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 連線已中斷，WatchSessionAsync 會處理
        }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        _statsTimer.Stop();
        DiagnosticLog.Write($"畫面區輸入統計：滑鼠 {_mouseEvents} 次、鍵盤 {_keyEvents} 次、拖放 {_drops} 次；送出 {_sentInputs} 個遠端事件；忽略本機注入 {_screen.IgnoredInjected} 次");
        await _session.DisposeAsync();
        _receiver.Dispose();
    }
}
