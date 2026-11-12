using System.Collections.Concurrent;
using SharpGen.Runtime;
using RemoteTool.Video;
using Vortice.MediaFoundation;
using static RemoteTool.Media.MediaFoundationRuntime;

namespace RemoteTool.Media;

/// <summary>
/// 硬體 H.264 編碼器（A2a-2，PLAN §4：啟動時探測 Media Foundation 硬體編碼）。
/// 硬體 MFT 為非同步：以事件（METransformNeedInput / METransformHaveOutput）驅動，由專用執行緒接收事件。
/// 輸入為系統記憶體 NV12（不使用 D3D 裝置管理員）；不支援時建立或第一次編碼即失敗，由呼叫端改用軟體編碼器。
/// </summary>
public sealed class MfH264HardwareEncoder : IVideoEncoder
{
    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator _events;
    private readonly object _mftLock = new();
    private readonly SemaphoreSlim _needInput = new(0, int.MaxValue);
    private readonly ManualResetEventSlim _haveOutput = new(false);
    private readonly ConcurrentQueue<(byte[] Data, bool Keyframe)> _outputs = new();
    private readonly Thread _pump;
    private readonly Action<string>? _log;
    private readonly byte[] _nv12;
    private readonly long _duration;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private volatile Exception? _pumpError;
    private volatile bool _stopping;
    private long _time;
    private bool _disposed;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    public MfH264HardwareEncoder(int width, int height, int fps, int bitrateKbps, Action<string>? log = null)
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
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter),
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

        Exception? last = null;
        foreach (var activate in activates)
        {
            IMFTransform? mft = null;
            IMFMediaEventGenerator? events = null;
            try
            {
                mft = ActivateDetached(activate);
                mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u); // 非同步 MFT 須先解鎖
                MfH264Encoder.ConfigureTypes(mft, width, height, fps, bitrateKbps, _log);
                events = mft.QueryInterface<IMFMediaEventGenerator>();

                var info = mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples
                                                       | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
                _outputSize = info.Size > 0 ? info.Size : Math.Max(width * height * 2, 1 << 20);

                mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                _mft = mft;
                _events = events;
                Name = $"H.264（硬體：{FriendlyName(activate)}）";
                _pump = new Thread(Pump) { IsBackground = true, Name = "H.264 硬體編碼事件" };
                _pump.Start();
                return;
            }
            catch (SharpGenException ex)
            {
                last = ex;
                events?.Dispose();
                mft?.Dispose(); // activation 物件由列舉集合釋放
            }
        }
        throw new InvalidOperationException(last is null ? "這台電腦沒有硬體 H.264 編碼器" : $"硬體 H.264 編碼器無法使用：{last.Message}", last);
    }

    /// <summary>事件執行緒：需要輸入時放行 <see cref="Encode"/>；有輸出時取出並排入佇列。</summary>
    private void Pump()
    {
        try
        {
            while (!_stopping)
            {
                using var ev = _events.GetEvent(0); // 阻塞直到有事件；關閉時以 MFShutdownObject 解除
                switch (ev.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        DrainOneOutput();
                        _haveOutput.Set();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or ObjectDisposedException)
        {
            if (!_stopping) _pumpError = ex;
        }
        finally
        {
            try { _needInput.Release(); } catch (ObjectDisposedException) { }
            try { _haveOutput.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private void DrainOneOutput()
    {
        lock (_mftLock)
        {
            using IMFSample? provided = _providesSamples ? null : CreateEmptySample(_outputSize);
            Result hr = ProcessOutput(_mft, provided, out IMFSample? produced);
            if (hr == ResultCode.TransformStreamChange)
            {
                using var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                _sequenceHeader = null;
                return;
            }
            if (hr == ResultCode.TransformNeedMoreInput) return;
            hr.CheckError();
            using (produced)
            {
                var sample = produced ?? provided!;
                bool key = false;
                try { key = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch (SharpGenException) { }
                _outputs.Enqueue((ReadSample(sample), key));
            }
        }
    }

    public EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != Width || frame.Height != Height) throw new ArgumentException("畫面大小與編碼器不符");
        if (_pumpError is { } err) throw new InvalidOperationException($"硬體編碼器事件錯誤：{err.Message}", err);

        if (!_needInput.Wait(TimeSpan.FromSeconds(1)))
            throw new TimeoutException("硬體編碼器超過 1 秒沒有要求輸入");
        if (_pumpError is { } err2) throw new InvalidOperationException($"硬體編碼器事件錯誤：{err2.Message}", err2);

        if (forceKeyframe && !CodecApi.SetUInt32(_mft, CodecApi.AVEncVideoForceKeyFrame, 1))
            _log?.Invoke("硬體編碼器不支援強制關鍵幀；將在下一個 GOP 開頭產生");

        ColorConverter.BgraToNv12(frame, _nv12);
        _haveOutput.Reset();
        using (var sample = CreateSample(_nv12, _time, _duration))
        {
            _time += _duration;
            lock (_mftLock) _mft.ProcessInput(0, sample, 0);
        }

        // 低延遲模式通常在一張畫面時間內輸出；等不到就下次一併送出（最多延遲一張）
        _haveOutput.Wait(TimeSpan.FromTicks(_duration * 2));

        var parts = new List<byte[]>();
        bool keyframe = false;
        while (_outputs.TryDequeue(out var o))
        {
            parts.Add(o.Data);
            keyframe |= o.Keyframe;
        }
        if (parts.Count == 0) return null;

        byte[] data = parts.Count == 1 ? parts[0] : parts.SelectMany(p => p).ToArray();
        if (!keyframe) keyframe = H264.ContainsIdr(data);
        if (keyframe && !H264.ContainsSps(data))
        {
            _sequenceHeader ??= TryGetSequenceHeader();
            if (_sequenceHeader is { Length: > 0 }) data = [.. _sequenceHeader, .. data];
        }
        return new EncodedFrame(data, keyframe);
    }

    private byte[]? TryGetSequenceHeader()
    {
        try
        {
            lock (_mftLock)
            {
                using var type = _mft.GetOutputCurrentType(0);
                return type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            }
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
        _stopping = true;
        try
        {
            lock (_mftLock)
            {
                _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
        }
        catch (SharpGenException) { }
        try { MediaFactory.MFShutdownObject(_mft); } catch (SharpGenException) { } // 讓 GetEvent 結束等待
        _pump.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _mft.Dispose();
        _needInput.Dispose();
        _haveOutput.Dispose();
    }
}

/// <summary>H.264 編碼器的選擇方式。</summary>
public enum H264EncoderMode
{
    /// <summary>硬體優先，不可用或故障時改用軟體。</summary>
    Auto,
    Hardware,
    Software,
}

/// <summary>
/// 依模式建立 H.264 編碼器；Auto 時硬體優先，**硬體在建立或串流中故障時自動改用軟體**（不退回區塊差異）。
/// </summary>
public sealed class ResilientH264Encoder : IVideoEncoder
{
    private readonly int _fps, _bitrate;
    private readonly H264EncoderMode _mode;
    private readonly Action<string>? _log;
    private IVideoEncoder _current;

    public ResilientH264Encoder(int width, int height, int fps, int bitrateKbps, H264EncoderMode mode, Action<string>? log = null)
    {
        Width = width;
        Height = height;
        _fps = fps;
        _bitrate = bitrateKbps;
        _mode = mode;
        _log = log;
        _current = Create(width, height);
    }

    public string Name => _current.Name;
    public int Width { get; }
    public int Height { get; }

    private IVideoEncoder Create(int width, int height)
    {
        if (_mode == H264EncoderMode.Software) return new MfH264Encoder(width, height, _fps, _bitrate, _log);
        try
        {
            return new MfH264HardwareEncoder(width, height, _fps, _bitrate, _log);
        }
        catch (Exception ex) when (_mode == H264EncoderMode.Auto)
        {
            _log?.Invoke($"硬體 H.264 不可用（{ex.Message}），改用軟體編碼");
            return new MfH264Encoder(width, height, _fps, _bitrate, _log);
        }
    }

    public EncodedFrame? Encode(FrameBuffer frame, bool forceKeyframe)
    {
        try
        {
            return _current.Encode(frame, forceKeyframe);
        }
        catch (Exception ex) when (_mode == H264EncoderMode.Auto && _current is MfH264HardwareEncoder)
        {
            _log?.Invoke($"硬體 H.264 編碼失敗（{ex.Message}），改用軟體編碼");
            _current.Dispose();
            _current = new MfH264Encoder(Width, Height, _fps, _bitrate, _log);
            return _current.Encode(frame, forceKeyframe: true);
        }
    }

    /// <summary>
    /// 啟動時探測：實際建立編碼器並編碼一張畫面（硬體非同步路徑只有實際編碼才能確認可用）。
    /// 回傳可用時的編碼器名稱。
    /// </summary>
    public static bool Probe(H264EncoderMode mode, out string detail, Action<string>? log = null)
    {
        try
        {
            using var encoder = new ResilientH264Encoder(640, 480, 30, 1000, mode, log);
            var frame = new FrameBuffer(640, 480);
            for (int i = 0; i < 3 && encoder.Encode(frame, i == 0) is null; i++) { }
            detail = encoder.Name;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public void Dispose() => _current.Dispose();
}
