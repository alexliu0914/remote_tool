using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteTool.Connection;
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

    /// <summary>非 null 表示因連線中斷而關閉，內容為原因。</summary>
    public string? EndReason { get; private set; }

    internal RemoteWindow(ControllerSession session, FrameCanvas canvas)
    {
        InitializeComponent();
        _session = session;
        _canvas = canvas;
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
        _ = WatchSessionAsync();
    }

    private async Task WatchSessionAsync()
    {
        string reason = await _session.Completion;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing) return;
            EndReason = reason;
            Close();
        });
    }

    /// <summary>背景執行緒呼叫：一張畫面已解到畫布。</summary>
    internal void OnFrameCompleted(PixelRect dirty)
    {
        lock (_dirtyGate)
        {
            _pendingDirty = _pendingDirty.Union(dirty);
            if (_renderScheduled) return;
            _renderScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Render);
    }

    private void Render()
    {
        PixelRect dirty;
        lock (_dirtyGate)
        {
            dirty = _pendingDirty;
            _pendingDirty = default;
            _renderScheduled = false;
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

    private void OnStatsTick(object? sender, EventArgs e)
    {
        long bytes = _session.VideoBytesReceived;
        double kbps = (bytes - _bytesAtLastTick) * 8 / 1000.0;
        _bytesAtLastTick = bytes;

        string size = _bitmap is null ? "—" : $"{_bitmap.PixelWidth}×{_bitmap.PixelHeight}";
        string rtt = _session.LastRoundTrip is { } t ? $"{t.TotalMilliseconds:0} ms" : "—";
        StatusText.Text = $"{size}    {_framesSinceTick} FPS    {kbps:0} kbps    心跳 {rtt}    （A1 PoC：區塊差異 + Deflate，尚無鍵鼠）";
        _framesSinceTick = 0;
    }

    private void ApplyFit()
    {
        bool fit = FitCheck.IsChecked == true;
        ScreenImage.Stretch = fit ? Stretch.Uniform : Stretch.None;
        Scroller.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        if (ScreenImage is not null) ApplyFit();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
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
    }
}
