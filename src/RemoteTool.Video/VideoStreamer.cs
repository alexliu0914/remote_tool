using System.Threading.Channels;
using RemoteTool.Protocol;

namespace RemoteTool.Video;

public sealed class VideoStreamerOptions
{
    /// <summary>FPS 上限（D-027 預設 10）。</summary>
    public int MaxFps { get; init; } = 10;
    /// <summary>寬度上限，超過以整數倍縮小（D-027 預設 1920）。</summary>
    public int MaxWidth { get; init; } = 1920;
    /// <summary>擷取暫時無法使用時的重試間隔。</summary>
    public TimeSpan UnavailableRetry { get; init; } = TimeSpan.FromSeconds(1);
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 被控端影像串流迴圈：擷取 → 縮放 → 區塊編碼 → 送出。
/// 不設佇列：一張送完才擷取下一張，網路慢時實際 FPS 自動下降，記憶體只保留目前與上一張。
/// </summary>
public static class VideoStreamer
{
    public static async Task RunAsync(IScreenCapture capture, FrameStream stream, ChannelReader<Frame> incoming,
        VideoStreamerOptions options, CancellationToken ct)
    {
        int keyframeRequested = 1; // 第一張一定送完整畫面
        var requestReader = Task.Run(async () =>
        {
            await foreach (var frame in incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.Type != MessageType.KeyframeRequest) continue;
                try
                {
                    MessageCodec.Decode<KeyframeRequest>(frame);
                    Volatile.Write(ref keyframeRequested, 1);
                }
                catch (InvalidDataException)
                {
                    options.Log?.Invoke("收到格式錯誤的 KeyframeRequest，忽略");
                }
            }
        }, ct);

        var encoder = new TileEncoder();
        var raw = new FrameBuffer();
        var scaled = new FrameBuffer();
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.MaxFps));
        bool wasUnavailable = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long started = Environment.TickCount64;
                var status = await Task.Run(() => capture.Capture(interval, raw), ct).ConfigureAwait(false);

                if (status == CaptureStatus.Unavailable)
                {
                    if (!wasUnavailable) options.Log?.Invoke("畫面暫時無法擷取，稍後重試");
                    wasUnavailable = true;
                    await Task.Delay(options.UnavailableRetry, ct).ConfigureAwait(false);
                    continue;
                }
                if (wasUnavailable)
                {
                    options.Log?.Invoke("畫面擷取恢復");
                    wasUnavailable = false;
                    Volatile.Write(ref keyframeRequested, 1);
                }

                IReadOnlyList<VideoTileUpdate> packets;
                if (status == CaptureStatus.NewFrame)
                {
                    bool full = Interlocked.Exchange(ref keyframeRequested, 0) == 1;
                    int factor = FrameScaler.FactorFor(raw.Width, options.MaxWidth);
                    FrameBuffer source = raw;
                    if (factor > 1)
                    {
                        FrameScaler.Downscale(raw, factor, scaled);
                        source = scaled;
                    }
                    packets = encoder.Encode(source, full);
                }
                else if (encoder.HasPrevious && Interlocked.Exchange(ref keyframeRequested, 0) == 1)
                {
                    packets = encoder.EncodePreviousFull();
                }
                else
                {
                    continue; // 沒變化，DXGI 的等待本身就是節流
                }

                foreach (var packet in packets)
                    await stream.WriteAsync(ChannelKind.Video, packet, ct).ConfigureAwait(false);

                var remaining = interval - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await requestReader.ConfigureAwait(false); } catch { /* 隨串流結束 */ }
        }
    }
}
