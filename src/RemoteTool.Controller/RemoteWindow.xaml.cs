using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;

namespace RemoteTool.Controller;

/// <summary>
/// 遠端畫面視窗（A1 PoC：WriteableBitmap，見 D-028）。
/// 背景執行緒把封包解到 <see cref="FrameCanvas"/>；每張畫面完成時累積變動範圍，
/// UI 執行緒只把變動範圍複製進 WriteableBitmap。UI 跟不上時多張合併成一次更新，不排隊。
/// </summary>
public partial class RemoteWindow : Window
{
    private readonly ControllerSession _session;
    private readonly FrameCanvas _canvas;
    private readonly DispatcherTimer _statsTimer;
    private readonly object _dirtyGate = new();
    private PixelRect _pendingDirty;
    private bool _renderScheduled;
    private WriteableBitmap? _bitmap;
    private int _framesSinceTick;
    private long _bytesAtLastTick;
    private bool _closing;
    private bool _fit = true;
    private bool _refreshRequested;
    private bool _pendingKeyframe;
    private string _note = "";

    /// <summary>非 null 表示因連線中斷而關閉，內容為原因。</summary>
    public string? EndReason { get; private set; }

    private readonly VideoReceiver _receiver;

    internal RemoteWindow(ControllerSession session, VideoReceiver receiver)
    {
        InitializeComponent();
        _session = session;
        _receiver = receiver;
        _canvas = receiver.Canvas;
        Title = $"RemoteTool — {session.HostKey}";

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnStatsTick, Dispatcher);
        Loaded += (_, _) =>
        {
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

    private string _fileNote = "";

    private void OnSubChannelState(ChannelKind role, SubChannelState state, string? detail)
    {
        DiagnosticLog.Write($"{role} 子連線：{state}{(detail is null ? "" : "（" + detail + "）")}");
        if (role == ChannelKind.Video)
        {
            VideoOverlay.Visibility = state == SubChannelState.Connected ? Visibility.Collapsed : Visibility.Visible;
            VideoRetryButton.Visibility = state == SubChannelState.Failed ? Visibility.Visible : Visibility.Collapsed;
            VideoOverlayText.Text = state switch
            {
                SubChannelState.Reconnecting => "畫面重新連線中…\n（目前顯示的是最後一張畫面，非即時）",
                SubChannelState.Failed => $"畫面連線失敗：{detail}\n控制連線仍在，可按下方按鈕重試。",
                _ => "",
            };
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

    /// <summary>背景執行緒呼叫：一張畫面已解到畫布。</summary>
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

            if (dirty.IsEmpty) return;
            // 防禦：裁到畫面範圍內
            int x = Math.Clamp(dirty.X, 0, buffer.Width), y = Math.Clamp(dirty.Y, 0, buffer.Height);
            int w = Math.Min(dirty.Width, buffer.Width - x), h = Math.Min(dirty.Height, buffer.Height - y);
            if (w <= 0 || h <= 0) return;

            _bitmap.WritePixels(new Int32Rect(x, y, w, h), buffer.Pixels, buffer.Stride, x, y);
        }
        _framesSinceTick++;
    }

    private string _stats = "等待畫面…";

    private void OnStatsTick(object? sender, EventArgs e)
    {
        long bytes = _session.VideoBytesReceived;
        double kbps = (bytes - _bytesAtLastTick) * 8 / 1000.0;
        _bytesAtLastTick = bytes;

        string size = _bitmap is null ? "—" : $"{_bitmap.PixelWidth}×{_bitmap.PixelHeight}";
        string rtt = _session.LastRoundTrip is { } t ? $"{t.TotalMilliseconds:0} ms" : "—";
        _stats = $"{size}    {_framesSinceTick} FPS    {kbps:0} kbps    心跳 {rtt}    {_receiver.CurrentFormat}    （只能看，尚無鍵鼠）";
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

    private void ApplyFit()
    {
        ScreenImage.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        Scroller.HorizontalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        FitButton.Content = _fit ? "顯示：符合視窗" : "顯示：原尺寸";
    }

    private void OnFitToggle(object sender, RoutedEventArgs e)
    {
        _fit = !_fit;
        ApplyFit();
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
        await _session.DisposeAsync();
        _receiver.Dispose();
    }
}
