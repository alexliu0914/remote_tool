using System.Threading.Channels;
using RemoteTool.Protocol;

namespace RemoteTool.Video;

public sealed class VideoStreamerOptions
{
    /// <summary>FPS 上限（D-027 預設 10；H.264 時可提高，規劃書目標 30）。</summary>
    public int MaxFps { get; init; } = 10;
    /// <summary>寬度上限，超過以整數倍縮小（D-027 預設 1920）。</summary>
    public int MaxWidth { get; init; } = 1920;
    /// <summary>擷取暫時無法使用時的重試間隔。</summary>
    public TimeSpan UnavailableRetry { get; init; } = TimeSpan.FromSeconds(1);
    public Action<string>? Log { get; init; }

    /// <summary>
    /// 建立 H.264 編碼器（參數為偶數寬高）。null 或回傳 null 表示使用區塊差異格式。
    /// 只有在控制端宣告能解 H.264 時才會呼叫。
    /// </summary>
    public Func<int, int, IVideoEncoder?>? EncoderFactory { get; init; }
}

/// <summary>
/// 被控端影像串流迴圈：擷取 → 縮放 → 編碼（H.264，失敗時改用區塊差異）→ 送出。
/// 不設佇列：一張送完才擷取下一張，網路慢時實際 FPS 自動下降；每張編碼後的畫面都完整送出，
/// 因此 H.264 參考鏈不會因丟幀而損壞（PLAN §5.1 的「落後時丟待送佇列並要求 IDR」在零佇列下不會發生，PLAN_GAPS G-10）。
/// </summary>
public static class VideoStreamer
{
    public static async Task RunAsync(IScreenCapture capture, FrameStream stream, ChannelReader<Frame> incoming,
        VideoStreamerOptions options, CancellationToken ct, bool clientSupportsH264 = false)
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

        var tiles = new TileEncoder();
        var raw = new FrameBuffer();
        var scaled = new FrameBuffer();
        var even = new FrameBuffer();
        var last = new FrameBuffer();     // H.264：最近一張來源畫面，供沒有新畫面時重送關鍵幀
        bool hasLast = false;
        IVideoEncoder? encoder = null;
        bool h264Allowed = clientSupportsH264 && options.EncoderFactory is not null;
        long frameId = 0;
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

                FrameBuffer? source = null;
                if (status == CaptureStatus.NewFrame)
                {
                    int factor = FrameScaler.FactorFor(raw.Width, options.MaxWidth);
                    source = raw;
                    if (factor > 1)
                    {
                        FrameScaler.Downscale(raw, factor, scaled);
                        source = scaled;
                    }
                }

                bool wantKeyframe = Volatile.Read(ref keyframeRequested) == 1;
                if (source is null && !wantKeyframe) continue; // 沒變化，DXGI 的等待本身就是節流

                List<object>? packets = null;

                // ---- H.264 ----
                if (h264Allowed)
                {
                    if (source is not null)
                    {
                        CropToEven(source, even);
                        even.CopyTo(last);
                        hasLast = true;
                    }
                    if (hasLast)
                    {
                        try
                        {
                            if (encoder is null || encoder.Width != last.Width || encoder.Height != last.Height)
                            {
                                encoder?.Dispose();
                                encoder = options.EncoderFactory!(last.Width, last.Height);
                                if (encoder is null) throw new InvalidOperationException("無可用的 H.264 編碼器");
                                options.Log?.Invoke($"影像格式：{encoder.Name}，{last.Width}×{last.Height}");
                                wantKeyframe = true;
                            }
                            bool force = Interlocked.Exchange(ref keyframeRequested, 0) == 1 || wantKeyframe;
                            var encoded = encoder.Encode(last, force);
                            if (encoded is not null)
                                packets = VideoFrameAssembler.Split(++frameId, last.Width, last.Height, encoded, "h264")
                                    .Cast<object>().ToList();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            options.Log?.Invoke($"H.264 編碼失敗（{ex.Message}），本連線改用區塊差異格式");
                            encoder?.Dispose();
                            encoder = null;
                            h264Allowed = false;
                            Volatile.Write(ref keyframeRequested, 1);
                            wantKeyframe = true;
                        }
                    }
                }

                // ---- 區塊差異（A1 格式，備援） ----
                if (!h264Allowed)
                {
                    IReadOnlyList<VideoTileUpdate> tileUpdates;
                    if (source is not null)
                        tileUpdates = tiles.Encode(source, Interlocked.Exchange(ref keyframeRequested, 0) == 1);
                    else if (tiles.HasPrevious && Interlocked.Exchange(ref keyframeRequested, 0) == 1)
                        tileUpdates = tiles.EncodePreviousFull();
                    else if (hasLast && Interlocked.Exchange(ref keyframeRequested, 0) == 1)
                        tileUpdates = tiles.Encode(last, forceFull: true); // 剛從 H.264 改為備援，沒有新畫面
                    else
                        tileUpdates = Array.Empty<VideoTileUpdate>();
                    packets = tileUpdates.Cast<object>().ToList();
                }

                if (packets is not null)
                {
                    foreach (var packet in packets)
                    {
                        if (packet is VideoFrame vf) await stream.WriteAsync(vf, 0, ct).ConfigureAwait(false);
                        else if (packet is VideoTileUpdate vt) await stream.WriteAsync(vt, 0, ct).ConfigureAwait(false);
                    }
                }

                var remaining = interval - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            encoder?.Dispose();
            try { await requestReader.ConfigureAwait(false); } catch { /* 隨串流結束 */ }
        }
    }

    /// <summary>H.264 / NV12 需要偶數寬高：奇數時裁掉最右一欄或最下一列。</summary>
    internal static void CropToEven(FrameBuffer source, FrameBuffer target)
    {
        int w = source.Width & ~1, h = source.Height & ~1;
        if (w == source.Width && h == source.Height)
        {
            source.CopyTo(target);
            return;
        }
        target.Resize(Math.Max(2, w), Math.Max(2, h));
        int rowBytes = target.Width * 4;
        for (int y = 0; y < target.Height; y++)
            source.Pixels.AsSpan(y * source.Stride, rowBytes).CopyTo(target.Pixels.AsSpan(y * rowBytes));
    }
}
