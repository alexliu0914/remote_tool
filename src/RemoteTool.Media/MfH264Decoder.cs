using SharpGen.Runtime;
using RemoteTool.Video;
using Vortice.MediaFoundation;
using static RemoteTool.Media.MediaFoundationRuntime;

namespace RemoteTool.Media;

/// <summary>
/// Media Foundation H.264 解碼器（A2a：Windows 內建「Microsoft H264 Video Decoder」，CPU 解碼）。
/// 輸出 NV12 → 轉 BGRA。解碼器常把高度補到 16 的倍數（例如 1088）並有 stride 對齊，由 <see cref="ColorConverter.Nv12ToBgra"/> 裁切。
/// 第一張畫面時才以實際寬高設定（部分解碼器在不知道大小時不提供輸出格式）；大小改變時重建。
/// 硬體解碼與 D3D11 顯示於 A2b（HwndHost）一併導入。
/// </summary>
public sealed class MfH264Decoder : IVideoDecoder
{
    private IMFTransform? _mft;
    private bool _providesSamples;
    private int _outputSize, _stride, _planeRows;
    private int _configuredWidth, _configuredHeight;
    private long _time;
    private bool _disposed;
    private bool _producedAny;

    public string Name { get; private set; } = "H.264 解碼（軟體）";

    /// <summary>檢查這台電腦能否建立並設定 H.264 解碼器（控制端啟動時用來決定是否宣告 H.264 能力）。</summary>
    public static bool IsAvailable(out string detail)
    {
        try
        {
            using var probe = new MfH264Decoder();
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

    public MfH264Decoder() => MediaFoundationRuntime.EnsureStarted();

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
                Name = $"H.264 解碼（軟體：{FriendlyName(activate)}）";
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

    public bool Decode(byte[] data, int width, int height, FrameBuffer target)
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
                var nv12 = ReadSample(produced ?? provided!);
                int stride = _stride > 0 ? _stride : width;
                int rows = _planeRows > 0 ? _planeRows : height;
                // 依實際資料長度修正（部分解碼器的 stride 屬性與緩衝區不一致）
                if ((long)stride * rows * 3 / 2 > nv12.Length) rows = (int)(nv12.Length * 2L / 3 / stride);
                ColorConverter.Nv12ToBgra(nv12, stride, rows, width, height, target);
                _producedAny = true;
            }
        }
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
    }
}
