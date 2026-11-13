using System.Runtime.InteropServices;
using SharpGen.Runtime;
using RemoteTool.Video;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;
using static RemoteTool.Media.MediaFoundationRuntime;

namespace RemoteTool.Media;

/// <summary>
/// Media Foundation H.264 解碼器（A2a：Windows 內建「Microsoft H264 Video Decoder」，CPU 解碼）。
/// 輸出 NV12 → 轉 BGRA。解碼器常把高度補到 16 的倍數（例如 1088）並有 stride 對齊，由 <see cref="ColorConverter.Nv12ToBgra"/> 裁切。
/// 第一張畫面時才以實際寬高設定（部分解碼器在不知道大小時不提供輸出格式）；大小改變時重建。
/// <para>
/// <paramref name="hardware"/> 為 true 時（A2b-2）給解碼器 D3D11 裝置管理員，由 GPU（DXVA）解碼；
/// 輸出為 GPU 紋理，以 IMF2DBuffer 依實際 pitch 讀回再轉 BGRA。解碼器不支援 D3D11 時建立即失敗，由呼叫端改用軟體。
/// </para>
/// </summary>
public sealed class MfH264Decoder : IVideoDecoder
{
    private readonly bool _hardware;
    private ID3D11Device? _device;
    private IMFDXGIDeviceManager? _manager;
    private string _adapter = "";
    private byte[] _nv12 = Array.Empty<byte>();
    private IMFTransform? _mft;
    private bool _providesSamples;
    private int _outputSize, _stride, _planeRows;
    private int _configuredWidth, _configuredHeight;
    private long _time;
    private bool _disposed;
    private bool _producedAny;

    public string Name { get; private set; } = "H.264 解碼（軟體）";

    /// <summary>檢查這台電腦能否建立並設定 H.264 解碼器（控制端啟動時用來決定是否宣告 H.264 能力）。</summary>
    public static bool IsAvailable(out string detail, bool hardware = false)
    {
        try
        {
            using var probe = new MfH264Decoder(hardware);
            probe.EnsureConfigured(640, 480);
            detail = probe.Name;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public MfH264Decoder(bool hardware = false)
    {
        MediaFoundationRuntime.EnsureStarted();
        _hardware = hardware;
        if (hardware) CreateDeviceManager();
    }

    private void CreateDeviceManager()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device, out ID3D11DeviceContext? context).CheckError();
        context?.Dispose();
        // 解碼器會在自己的執行緒使用裝置，必須開啟多執行緒保護
        using (var mt = _device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        using (var dxgi = _device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            _adapter = adapter.Description.Description;
        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(_device).CheckError();
    }

    private void EnsureConfigured(int width, int height)
    {
        if (_mft is not null && width == _configuredWidth && height == _configuredHeight) return;
        Release();

        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter),
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 });

        Exception? last = null;
        foreach (var activate in activates)
        {
            IMFTransform? mft = null;
            try
            {
                mft = ActivateDetached(activate);
                try { mft.Attributes.Set(LowLatency, 1u); } catch (SharpGenException) { }
                if (_hardware)
                {
                    uint aware = 0;
                    try { aware = mft.Attributes.GetUInt32(TransformAttributeKeys.D3D11Aware); } catch (SharpGenException) { }
                    if (aware == 0) throw new InvalidOperationException("解碼器不支援 D3D11（無法硬體解碼）");
                    mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_manager!.NativePointer);
                }

                using (var input = MediaFactory.MFCreateMediaType())
                {
                    input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                    input.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
                    input.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
                    mft.SetInputType(0, input, 0);
                }
                SelectNv12Output(mft);

                var info = mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
                mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                _mft = mft;
                _configuredWidth = width;
                _configuredHeight = height;
                Name = _hardware
                    ? $"H.264 解碼（硬體：{_adapter}）"
                    : $"H.264 解碼（軟體：{FriendlyName(activate)}）";
                return;
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
            {
                last = ex;
                mft?.Dispose(); // activation 物件由列舉集合釋放
            }
        }
        throw new InvalidOperationException("找不到可用的 H.264 解碼器", last);
    }

    /// <summary>在可用的輸出型別中選 NV12，並記下 stride 與 Y 平面列數。</summary>
    private void SelectNv12Output(IMFTransform mft)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = mft.GetOutputAvailableType(0, i); }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.NoMoreTypes)
            {
                throw new InvalidOperationException("解碼器不支援 NV12 輸出");
            }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                mft.SetOutputType(0, type, 0);
                ReadOutputGeometry(type);
                return;
            }
        }
    }

    private void ReadOutputGeometry(IMFMediaType type)
    {
        var (w, h) = (0u, 0u);
        try { (w, h) = Unpack(type.GetUInt64(MediaTypeAttributeKeys.FrameSize)); } catch (SharpGenException) { }
        uint stride = 0;
        try { stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride); } catch (SharpGenException) { }
        _stride = (int)(stride > 0 ? stride : w);
        _planeRows = (int)h;
        _outputSize = w > 0 && h > 0 ? (int)(Math.Max(stride, w) * h * 3 / 2) : 0;
    }

    public bool Decode(ReadOnlySpan<byte> data, int width, int height, FrameBuffer target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConfigured(width, height);
        var mft = _mft!;

        using (var sample = CreateSample(data, _time, 333_333))
        {
            _time += 333_333;
            mft.ProcessInput(0, sample, 0);
        }

        _producedAny = false;
        while (true)
        {
            int size = _outputSize > 0 ? _outputSize : mft.GetOutputStreamInfo(0).Size;
            using IMFSample? provided = _providesSamples ? null : CreateEmptySample(Math.Max(size, width * height * 3 / 2));
            Result hr = ProcessOutput(mft, provided, out IMFSample? produced);

            if (hr == ResultCode.TransformNeedMoreInput) return _producedAny;
            if (hr == ResultCode.TransformStreamChange)
            {
                // 第一個 SPS 或解析度改變：重新選擇輸出型別（此時才知道實際大小與 stride）
                SelectNv12Output(mft);
                continue;
            }
            hr.CheckError();

            using (produced)
            {
                ConvertOutput(produced ?? provided!, width, height, target);
                _producedAny = true;
            }
        }
    }

    /// <summary>
    /// 讀取輸出 NV12 並轉 BGRA。優先用 IMF2DBuffer 取得實際 pitch（GPU 紋理的列寬常大於畫面寬度）；
    /// 不支援時用連續緩衝區與輸出型別的 stride。
    /// </summary>
    private void ConvertOutput(IMFSample sample, int width, int height, FrameBuffer target)
    {
        int rows = _planeRows > 0 ? _planeRows : height;
        using var buffer = sample.GetBufferByIndex(0);
        IMF2DBuffer? b2d = null;
        try { b2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>(); } catch (SharpGenException) { }
        if (b2d is not null)
        {
            using (b2d)
            {
                b2d.Lock2D(out IntPtr scan0, out int pitch);
                try
                {
                    if (pitch <= 0) throw new InvalidOperationException("解碼輸出不支援由上而下的排列");
                    int size = pitch * rows + pitch * (height / 2);
                    if (_nv12.Length < size) _nv12 = new byte[size];
                    // Y 平面 rows 列，UV 平面緊接在 pitch × rows 之後
                    Marshal.Copy(scan0, _nv12, 0, pitch * rows);
                    Marshal.Copy(scan0 + pitch * rows, _nv12, pitch * rows, pitch * (height / 2));
                    ColorConverter.Nv12ToBgra(_nv12.AsSpan(0, size), pitch, rows, width, height, target);
                }
                finally
                {
                    b2d.Unlock2D();
                }
                return;
            }
        }

        var nv12 = ReadSample(sample);
        int stride = _stride > 0 ? _stride : width;
        // 依實際資料長度修正（部分解碼器的 stride 屬性與緩衝區不一致）
        if ((long)stride * rows * 3 / 2 > nv12.Length) rows = (int)(nv12.Length * 2L / 3 / stride);
        ColorConverter.Nv12ToBgra(nv12, stride, rows, width, height, target);
    }

    private void Release()
    {
        if (_mft is null) return;
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException) { }
        _mft.Dispose();
        _mft = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _manager?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>
/// 控制端的 H.264 解碼器：硬體（DXVA）優先，**建立或解碼失敗時改用軟體**（A2b-2）。
/// 環境變數 REMOTETOOL_DECODER=sw 可強制軟體（比較用）。
/// </summary>
public sealed class ResilientH264Decoder : IVideoDecoder
{
    private readonly Action<string>? _log;
    private IVideoDecoder _current;
    private bool _isHardware;

    public ResilientH264Decoder(Action<string>? log = null)
    {
        _log = log;
        bool forceSoftware = string.Equals(Environment.GetEnvironmentVariable("REMOTETOOL_DECODER"), "sw", StringComparison.OrdinalIgnoreCase);
        if (!forceSoftware)
        {
            try
            {
                _current = new MfH264Decoder(hardware: true);
                _isHardware = true;
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"硬體 H.264 解碼不可用（{ex.Message}），改用軟體解碼");
            }
        }
        _current = new MfH264Decoder(hardware: false);
    }

    public string Name => _current.Name;

    public bool Decode(ReadOnlySpan<byte> data, int width, int height, FrameBuffer target)
    {
        try
        {
            return _current.Decode(data, width, height, target);
        }
        catch (Exception ex) when (_isHardware)
        {
            _log?.Invoke($"硬體 H.264 解碼失敗（{ex.Message}），改用軟體解碼");
            _current.Dispose();
            _current = new MfH264Decoder(hardware: false);
            _isHardware = false;
            return _current.Decode(data, width, height, target); // P 幀時可能失敗 → 呼叫端要求關鍵幀
        }
    }

    public void Dispose() => _current.Dispose();
}

