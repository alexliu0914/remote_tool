using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteTool.Media;

/// <summary>Media Foundation 啟動（整個程序一次）與共用工具。</summary>
internal static class MediaFoundationRuntime
{
    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            _started = true;
        }
    }

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
    public static (uint High, uint Low) Unpack(ulong v) => ((uint)(v >> 32), (uint)v);

    // 未包含在 Vortice 常數表中的 GUID
    public static readonly Guid MftFriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // MF_LOW_LATENCY / CODECAPI_AVLowLatencyMode

    public static string FriendlyName(IMFActivate activate)
    {
        try { return activate.GetString(MftFriendlyName); }
        catch (SharpGenException) { return "未知 MFT"; }
    }

    public const uint InterlaceProgressive = 2;
    public const uint H264ProfileMain = 77;
    public const uint H264ProfileBaseline = 66;

    /// <summary>以位元組陣列建立含單一緩衝區的 sample。</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        try
        {
            buffer.Lock(out IntPtr ptr, out _, out _);
            try
            {
                unsafe { data.CopyTo(new Span<byte>((void*)ptr, data.Length)); }
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.CurrentLength = data.Length;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>建立空的輸出 sample（MFT 不自行提供 sample 時）。</summary>
    public static IMFSample CreateEmptySample(int size)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(size, 1));
        try
        {
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    /// <summary>
    /// 以 COM vtable 直接呼叫 IMFTransform::ProcessOutput（第 25 個方法），明確掌握 sample 的參考計數：
    /// 呼叫端提供 sample 時，輸出寫入該 sample（<paramref name="produced"/> 為 null）；
    /// MFT 自行提供 sample 時，<paramref name="produced"/> 擁有其參考，用完須 Dispose。
    /// </summary>
    public static unsafe Result ProcessOutput(IMFTransform mft, IMFSample? callerSample, out IMFSample? produced)
    {
        produced = null;
        var buffer = new MftOutputDataBuffer { StreamId = 0, Sample = callerSample?.NativePointer ?? IntPtr.Zero };
        uint status;
        var vtbl = *(IntPtr**)mft.NativePointer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MftOutputDataBuffer*, uint*, int>)vtbl[25];
        int hr = fn(mft.NativePointer, 0, 1, &buffer, &status);
        if (buffer.Events != IntPtr.Zero) Marshal.Release(buffer.Events);
        if (hr >= 0 && callerSample is null && buffer.Sample != IntPtr.Zero)
            produced = new IMFSample(buffer.Sample);
        return new Result(hr);
    }

    /// <summary>
    /// 從列舉結果取出 MFT：啟用後與 activation 物件分離，之後 MFT 的生命週期只由回傳的物件決定，
    /// 列舉集合可以安全釋放。
    /// </summary>
    public static IMFTransform ActivateDetached(IMFActivate activate)
    {
        var mft = activate.ActivateObject<IMFTransform>();
        try { activate.DetachObject(); } catch (SharpGenException) { /* 不支援時由集合釋放 activation 自己的參考 */ }
        return mft;
    }

    /// <summary>把 sample 的內容複製成位元組陣列。</summary>
    public static byte[] ReadSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            return data;
        }
        finally
        {
            buffer.Unlock();
        }
    }
}

/// <summary>
/// ICodecAPI（Vortice 未包裝）：以 COM vtable 直接呼叫 SetValue。用來設定低延遲、位元率、GOP 與強制關鍵幀。
/// 設定失敗不視為致命錯誤（不同編碼器支援的屬性不同），由呼叫端記錄。
/// </summary>
internal static unsafe class CodecApi
{
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint RateControlCbr = 0;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public short boolVal;
    }

    private const ushort VT_UI4 = 19, VT_BOOL = 11;

    public static bool SetUInt32(ComObject mft, Guid api, uint value)
        => Set(mft, api, new Variant { vt = VT_UI4, ulVal = value });

    public static bool SetBool(ComObject mft, Guid api, bool value)
        => Set(mft, api, new Variant { vt = VT_BOOL, boolVal = value ? (short)-1 : (short)0 });

    private static bool Set(ComObject mft, Guid api, Variant value)
    {
        Guid iid = IID_ICodecAPI;
        if (Marshal.QueryInterface(mft.NativePointer, in iid, out IntPtr codec) < 0 || codec == IntPtr.Zero)
            return false;
        try
        {
            // ICodecAPI vtable：IUnknown(0..2)、IsSupported 3、IsModifiable 4、GetParameterRange 5、
            // GetParameterValues 6、GetDefaultValue 7、GetValue 8、SetValue 9
            var vtbl = *(IntPtr**)codec;
            var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
            Guid key = api;
            int hr = setValue(codec, &key, &value);
            return hr >= 0;
        }
        finally
        {
            Marshal.Release(codec);
        }
    }
}
