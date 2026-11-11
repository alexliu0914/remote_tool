namespace RemoteTool.Video;

public enum CaptureStatus
{
    /// <summary>取得新畫面，已寫入目標緩衝區。</summary>
    NewFrame,
    /// <summary>時限內畫面沒有變化。</summary>
    NoChange,
    /// <summary>目前無法擷取（例如安全桌面、解析度切換中、裝置遺失），稍後再試。</summary>
    Unavailable,
}

/// <summary>螢幕擷取來源。Windows 實作為 DXGI Desktop Duplication；測試用假來源。</summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>最多等待 <paramref name="timeout"/> 取得新畫面，寫入 <paramref name="target"/>（BGRA）。</summary>
    CaptureStatus Capture(TimeSpan timeout, FrameBuffer target);
}
