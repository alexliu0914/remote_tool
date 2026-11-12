using SharpGen.Runtime;
using RemoteTool.Video;
using Vortice.MediaFoundation;
using static RemoteTool.Media.MediaFoundationRuntime;

namespace RemoteTool.Media;

/// <summary>
/// Media Foundation H.264 編碼器（A2a：同步軟體 MFT，Windows 內建「Microsoft H.264 Video Encoder」）。
/// 輸入 BGRA → 轉 NV12 → 編碼 → Annex B 位元串流。低延遲、CBR、無 B 幀、GOP 2 秒。
/// 任何 Media Foundation 錯誤都以例外拋出，由 <see cref="VideoStreamer"/> 改用區塊差異備援。
/// </summary>
public sealed class MfH264Encoder : IVideoEncoder
{
    private readonly IMFTransform _mft;
    private readonly Action<string>? _log;
    private readonly byte[] _nv12;
    private readonly long _duration;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private long _time;
    private bool _disposed;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>檢查這台電腦能否建立並設定 H.264 編碼器（Host 啟動時顯示）。</summary>
    public static bool IsAvailable(out string detail)
    {
        try
        {
            using var probe = new MfH264Encoder(640, 480, 10, 1000);
            detail = probe.Name;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <param name="bitrateKbps">目標位元率（kbps），規劃書目標 2–8 Mbps。</param>
    public MfH264Encoder(int width, int height, int fps, int bitrateKbps, Action<string>? log = null)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("H.264 需要偶數寬高");
        MediaFoundationRuntime.EnsureStarted();
        Width = width;
        Height = height;
        _log = log;
        fps = Math.Clamp(fps, 1, 60);
        _duration = 10_000_000L / fps;
        _nv12 = new byte[ColorConverter.Nv12Size(width, height)];

        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter),
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

        Exception? last = null;
        foreach (var activate in activates)
        {
            IMFTransform? mft = null;
            try
            {
                mft = ActivateDetached(activate);
                Configure(mft, width, height, fps, bitrateKbps);
                _mft = mft;
                Name = $"H.264（軟體：{FriendlyName(activate)}）";
                var info = mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples
                                                       | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
                _outputSize = info.Size > 0 ? info.Size : Math.Max(width * height * 2, 1 << 20);
                mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                return;
            }
            catch (SharpGenException ex)
            {
                last = ex;
                mft?.Dispose(); // activation 物件由列舉集合釋放
            }
        }
        throw new InvalidOperationException("找不到可用的 H.264 編碼器", last);
    }

    private void Configure(IMFTransform mft, int width, int height, int fps, int bitrateKbps)
    {
        try { mft.Attributes.Set(LowLatency, 1u); } catch (SharpGenException) { /* 不支援時以 ICodecAPI 再設一次 */ }

        // 編碼器須先設輸出型別、再設輸入型別
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(bitrateKbps * 1000));
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileMain);
            mft.SetOutputType(0, output, 0);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
            mft.SetInputType(0, input, 0);
        }

        var failed = new List<string>();
        if (!CodecApi.SetBool(mft, CodecApi.AVLowLatencyMode, true)) failed.Add("低延遲");
        if (!CodecApi.SetUInt32(mft, CodecApi.AVEncCommonRateControlMode, CodecApi.RateControlCbr)) failed.Add("CBR");
        if (!CodecApi.SetUInt32(mft, CodecApi.AVEncCommonMeanBitRate, (uint)(bitrateKbps * 1000))) failed.Add("位元率");
        if (!CodecApi.SetUInt32(mft, CodecApi.AVEncMPVGOPSize, (uint)(fps * 2))) failed.Add("GOP");
        if (!CodecApi.SetUInt32(mft, CodecApi.AVEncMPVDefaultBPictureCount, 0)) failed.Add("B 幀");
        if (failed.Count > 0) _log?.Invoke($"H.264 編碼器不支援部分設定：{string.Join("、", failed)}（不影響運作）");
    }

    public EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != Width || frame.Height != Height) throw new ArgumentException("畫面大小與編碼器不符");

        if (forceKeyframe && !CodecApi.SetUInt32(_mft, CodecApi.AVEncVideoForceKeyFrame, 1))
            _log?.Invoke("H.264 編碼器不支援強制關鍵幀；將在下一個 GOP 開頭產生");

        ColorConverter.BgraToNv12(frame, _nv12);
        using (var sample = CreateSample(_nv12, _time, _duration))
        {
            _time += _duration;
            _mft.ProcessInput(0, sample, 0);
        }

        var outputs = new List<byte[]>();
        bool keyframe = false;
        while (true)
        {
            using IMFSample? provided = _providesSamples ? null : CreateEmptySample(_outputSize);
            Result hr = ProcessOutput(_mft, provided, out IMFSample? produced);

            if (hr == ResultCode.TransformNeedMoreInput) break;
            if (hr == ResultCode.TransformStreamChange)
            {
                using var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                _sequenceHeader = null;
                continue;
            }
            hr.CheckError();

            using (produced)
            {
                var sample = produced ?? provided!;
                outputs.Add(ReadSample(sample));
                try { keyframe |= sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; }
                catch (SharpGenException) { /* 沒有標記 */ }
            }
        }

        if (outputs.Count == 0) return null;
        byte[] data = outputs.Count == 1 ? outputs[0] : outputs.SelectMany(o => o).ToArray();
        if (!keyframe) keyframe = H264.ContainsIdr(data);
        if (keyframe && !H264.ContainsSps(data))
        {
            // 關鍵幀前沒有 SPS / PPS 時補上序列標頭，讓解碼端能從這一幀開始
            _sequenceHeader ??= TryGetSequenceHeader();
            if (_sequenceHeader is { Length: > 0 }) data = [.. _sequenceHeader, .. data];
        }
        return new EncodedFrame(data, keyframe);
    }

    private byte[]? TryGetSequenceHeader()
    {
        try
        {
            using var type = _mft.GetOutputCurrentType(0);
            return type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException) { }
        _mft.Dispose();
    }
}

/// <summary>H.264 Annex B 位元串流的簡單檢查。</summary>
public static class H264
{
    /// <summary>列出所有 NAL 單元類型（nal_unit_type，低 5 位元）。</summary>
    public static IEnumerable<int> NalTypes(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || (data[i + 2] == 0 && i + 4 < data.Length && data[i + 3] == 1)))
            {
                int start = data[i + 2] == 1 ? i + 3 : i + 4;
                if (start < data.Length) yield return data[start] & 0x1F;
                i = start;
            }
        }
    }

    public static bool ContainsIdr(byte[] data) => NalTypes(data).Contains(5);
    public static bool ContainsSps(byte[] data) => NalTypes(data).Contains(7);
}
