using System.Drawing;
using SharpGen.Runtime;
using RemoteTool.Video;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace RemoteTool.Controller;

/// <summary>
/// 畫面區繪製（A2b）：D3D11 裝置 + DXGI flip 交換鏈（綁定 HwndHost 子視窗），以 Direct2D 縮放繪製畫面，
/// DirectWrite 繪製覆蓋訊息（WPF 無法畫在 HwndHost 上方，見「airspace」限制）。
/// 裝置遺失（驅動更新、GPU 重設）時自動重建。只在 UI 執行緒呼叫。
/// </summary>
internal sealed class D3DRenderer : IDisposable
{
    private readonly IntPtr _hwnd;
    private ID3D11Device? _device;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _frame;
    private ID2D1SolidColorBrush? _dimBrush, _textBrush;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _textFormat;
    private int _width, _height;

    public D3DRenderer(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        CreateDevice();
    }

    /// <summary>顯示卡名稱（顯示用）。</summary>
    public string AdapterName { get; private set; } = "";

    /// <summary>true：縮放到畫面區；false：原尺寸（以 <see cref="ScrollX"/> / <see cref="ScrollY"/> 捲動）。</summary>
    public bool Fit { get; set; } = true;
    public int ScrollX { get; set; }
    public int ScrollY { get; set; }

    /// <summary>非 null 時把畫面變暗並顯示此訊息（例如「畫面重新連線中…」）。</summary>
    public string? Overlay { get; set; }

    /// <summary>最近一次繪製時，遠端畫面在畫面區中的位置（像素）與縮放比例；供滑鼠座標換算。</summary>
    public RectangleF DestinationRect { get; private set; }
    public float Scale { get; private set; } = 1;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    private void CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device, out ID3D11DeviceContext? context).CheckError();
        context?.Dispose();

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        }, null, null);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dimBrush = _dc.CreateSolidColorBrush(new Color4(0, 0, 0, 0.65f));
        _textBrush = _dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _textFormat = _dwrite.CreateTextFormat("Microsoft JhengHei UI", 22);
        _textFormat.TextAlignment = TextAlignment.Center;
        _textFormat.ParagraphAlignment = ParagraphAlignment.Center;
        CreateTarget();
    }

    private void CreateTarget()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _target = _dc!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1
        {
            PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
            DpiX = 96,
            DpiY = 96,
        });
        _dc.Target = _target;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        try
        {
            _dc!.Target = null;
            _target?.Dispose();
            _target = null;
            _swapChain!.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
            CreateTarget();
        }
        catch (SharpGenException)
        {
            Recreate();
        }
    }

    /// <summary>把畫布的變動範圍上傳到 GPU 點陣圖（大小改變時重建點陣圖並整張上傳）。</summary>
    public void Upload(FrameBuffer buffer, PixelRect dirty)
    {
        if (_dc is null || buffer.Width == 0) return;
        if (_frame is null || FrameWidth != buffer.Width || FrameHeight != buffer.Height)
        {
            _frame?.Dispose();
            _frame = _dc.CreateBitmap(new SizeI(buffer.Width, buffer.Height), new BitmapProperties1
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                BitmapOptions = BitmapOptions.None,
                DpiX = 96,
                DpiY = 96,
            });
            FrameWidth = buffer.Width;
            FrameHeight = buffer.Height;
            dirty = new PixelRect(0, 0, buffer.Width, buffer.Height);
        }
        int x = Math.Clamp(dirty.X, 0, buffer.Width), y = Math.Clamp(dirty.Y, 0, buffer.Height);
        int w = Math.Min(dirty.Width, buffer.Width - x), h = Math.Min(dirty.Height, buffer.Height - y);
        if (w <= 0 || h <= 0) return;
        int offset = y * buffer.Stride + x * 4;
        _frame.CopyFromMemory(new Rectangle(x, y, w, h),
            new ReadOnlySpan<byte>(buffer.Pixels, offset, buffer.Pixels.Length - offset), (uint)buffer.Stride).CheckError();
    }

    public void Draw()
    {
        if (_dc is null) return;
        try
        {
            _dc.BeginDraw();
            _dc.Clear(new Color4(0, 0, 0, 1));
            if (_frame is not null)
            {
                RectangleF dest;
                if (Fit)
                {
                    float scale = Math.Min((float)_width / FrameWidth, (float)_height / FrameHeight);
                    float w = FrameWidth * scale, h = FrameHeight * scale;
                    dest = new RectangleF((_width - w) / 2, (_height - h) / 2, w, h);
                    Scale = scale;
                }
                else
                {
                    dest = new RectangleF(-ScrollX, -ScrollY, FrameWidth, FrameHeight);
                    Scale = 1;
                }
                DestinationRect = dest;
                _dc.DrawBitmap(_frame, new RawRectF(dest.Left, dest.Top, dest.Right, dest.Bottom), 1f,
                    Fit ? Vortice.Direct2D1.InterpolationMode.HighQualityCubic : Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
                    null, null);
            }
            if (Overlay is { } text)
            {
                _dc.FillRectangle(new RawRectF(0, 0, _width, _height), _dimBrush!);
                _dc.DrawText(text, _textFormat!, new Rect(0, 0, _width, _height), _textBrush!);
            }
            var hr = _dc.EndDraw();
            if (hr.Failure) { Recreate(); return; }
            var present = _swapChain!.Present(1, PresentFlags.None);
            if (present.Failure) Recreate();
        }
        catch (SharpGenException)
        {
            Recreate();
        }
    }

    /// <summary>裝置遺失時重建所有資源（畫面點陣圖由下一次 Upload 重新建立）。</summary>
    private void Recreate()
    {
        ReleaseAll();
        try { CreateDevice(); }
        catch (SharpGenException) { /* 下次繪製再試 */ }
        FrameWidth = FrameHeight = 0;
        DeviceRecreated = true;
    }

    /// <summary>裝置重建後為 true；呼叫端應整張重新上傳畫面後設回 false。</summary>
    public bool DeviceRecreated { get; set; }

    private void ReleaseAll()
    {
        if (_dc is not null) _dc.Target = null;
        _frame?.Dispose(); _frame = null;
        _target?.Dispose(); _target = null;
        _textFormat?.Dispose(); _textFormat = null;
        _dwrite?.Dispose(); _dwrite = null;
        _dimBrush?.Dispose(); _dimBrush = null;
        _textBrush?.Dispose(); _textBrush = null;
        _dc?.Dispose(); _dc = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _swapChain?.Dispose(); _swapChain = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => ReleaseAll();
}
