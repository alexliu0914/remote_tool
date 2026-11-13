using System.Runtime.InteropServices;
using RemoteTool.Connection;
using RemoteTool.Video;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteTool.Host;

/// <summary>
/// 以 DXGI Desktop Duplication 擷取主螢幕（A1：只擷取第一個顯示器、不含游標，見 D-029）。
///
/// 解析度變更、桌面切換（鎖定、UAC 安全桌面）會讓 duplication 失效（ACCESS_LOST），
/// 此時釋放並在下次呼叫時重建；重建失敗回報 <see cref="CaptureStatus.Unavailable"/>。
/// 同一時間只能由一個執行緒呼叫 <see cref="Capture"/>。
/// </summary>
internal sealed class DxgiScreenCapture : IScreenCapture
{
    private readonly Action<string>? _log;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _width, _height;
    private bool _hasFrame;
    private string? _lastError;

    public DxgiScreenCapture(Action<string>? log = null) => _log = log;

    /// <summary>目前擷取的螢幕在虛擬桌面上的位置（供遠端滑鼠座標換算，B1）。尚未擷取時為 null。</summary>
    public static ScreenGeometry? CurrentGeometry { get; private set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public CaptureStatus Capture(TimeSpan timeout, FrameBuffer target)
    {
        if (_duplication is null && !TryInitialize())
            return CaptureStatus.Unavailable;

        var dup = _duplication!;
        Result result = dup.AcquireNextFrame((uint)Math.Max(0, timeout.TotalMilliseconds), out OutduplFrameInfo info, out IDXGIResource? resource);

        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // 畫面沒變，但第一次（或重建後）還沒有任何畫面時，讓呼叫端繼續等待即可。
            return CaptureStatus.NoChange;
        }
        if (result.Failure)
        {
            ReportOnce($"AcquireNextFrame 失敗（0x{result.Code:X8}），重建擷取");
            ReleaseDuplication();
            return CaptureStatus.Unavailable;
        }

        try
        {
            // LastPresentTime 為 0 表示只有游標移動、畫面沒變（第一張除外）。
            if (info.LastPresentTime == 0 && _hasFrame)
                return CaptureStatus.NoChange;

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            _context!.CopyResource(_staging!, texture);

            MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                target.Resize(_width, _height);
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    Marshal.Copy(map.DataPointer + y * (nint)map.RowPitch, target.Pixels, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }

            _hasFrame = true;
            _lastError = null;
            return CaptureStatus.NewFrame;
        }
        catch (SharpGenException ex)
        {
            ReportOnce($"擷取畫面失敗（{ex.Message}），重建擷取");
            ReleaseDuplication();
            return CaptureStatus.Unavailable;
        }
        finally
        {
            resource?.Dispose();
            if (_duplication is not null)
            {
                try { _duplication.ReleaseFrame(); } catch (SharpGenException) { /* 已失效 */ }
            }
        }
    }

    private bool TryInitialize()
    {
        try
        {
            // 找出接著主螢幕的顯示卡（雙顯卡筆電的預設顯示卡不一定接著螢幕），在它上面建立裝置。
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? adapter = null;
            IDXGIOutput? output = null;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? candidate).Success; i++)
            {
                if (candidate!.EnumOutputs(0, out output).Success)
                {
                    adapter = candidate;
                    break;
                }
                candidate.Dispose();
            }
            if (adapter is null || output is null)
            {
                ReportOnce("找不到接著螢幕的顯示卡");
                return false;
            }

            using var _adapter = adapter;
            using var _output = output;
            var d = output.Description.DesktopCoordinates;
            // 虛擬桌面範圍（程式已宣告 Per-Monitor V2，因此為實際像素）
            CurrentGeometry = new ScreenGeometry(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top,
                GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out _device, out _context).CheckError();

            using var output1 = output.QueryInterface<IDXGIOutput1>();

            _duplication = output1.DuplicateOutput(_device!);
            var desc = _duplication.Description;
            _width = (int)desc.ModeDescription.Width;
            _height = (int)desc.ModeDescription.Height;

            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
            });

            _hasFrame = false;
            _log?.Invoke($"DXGI 擷取已啟動：{_width}×{_height}（{desc.ModeDescription.Format}）");
            _lastError = null;
            return true;
        }
        catch (SharpGenException ex)
        {
            ReportOnce($"無法啟動 DXGI 擷取：{ex.Message}");
            ReleaseDuplication();
            // 裝置本身可能也已失效（驅動程式更新、GPU 重設），一併重建。
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            return false;
        }
    }

    private void ReportOnce(string message)
    {
        if (message == _lastError) return;
        _lastError = message;
        _log?.Invoke(message);
    }

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    public void Dispose()
    {
        ReleaseDuplication();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
