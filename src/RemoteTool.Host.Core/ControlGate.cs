using RemoteTool.Protocol.Messages;

namespace RemoteTool.Host.Core;

/// <summary>「桌面可控制」判定的輸入（第三節）。</summary>
public readonly record struct GateConditions(
    DesktopSnapshot Desktop,
    bool CaptureHealthy,
    bool VideoConnected)
{
    public bool AllSatisfied =>
        !Desktop.Locked && Desktop.OnConsole && Desktop.InputDesktopIsDefault && CaptureHealthy && VideoConnected;
}

public enum GateState
{
    /// <summary>暫停：不注入、不送畫面與剪貼簿。</summary>
    Paused,

    /// <summary>已送出 ResumeControl 與 IDR，等控制端回覆新世代。</summary>
    Resuming,

    /// <summary>可遠端控制。</summary>
    Active,
}

public enum GateActionKind { None, Pause, BeginResume }

public readonly record struct GateAction(GateActionKind Kind, uint Generation, PauseReason Reason)
{
    public static readonly GateAction None = default;
}

/// <summary>
/// 安全暫停與恢復（第三節，G0-03 必要）。非執行緒安全；由 <see cref="HostSession"/> 以鎖保護。
/// <list type="bullet">
/// <item>任一條件不成立 → 暫停；輸入世代編號加 1，舊世代事件一律丟棄。</item>
/// <item>全部條件連續成立 1 秒以上 → 開始恢復（送 IDR + ResumeControl）。</item>
/// <item>控制端顯示該世代的 IDR 後回覆 ResumeAck，才接受新世代的鍵鼠。</item>
/// </list>
/// 登入後初始狀態為暫停（原因 NotStarted），第一次恢復同樣走完整程序。
/// </summary>
public sealed class ControlGate
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _stableDuration;
    private long? _stableSince;
    private bool _everActive;

    public ControlGate(TimeProvider? time = null, TimeSpan? stableDuration = null, uint initialGeneration = 1)
    {
        _time = time ?? TimeProvider.System;
        _stableDuration = stableDuration ?? Protocol.ProtocolConstants.ResumeStableDuration;
        Generation = initialGeneration;
        Reason = PauseReason.NotStarted;
    }

    public GateState State { get; private set; } = GateState.Paused;
    public uint Generation { get; private set; }
    public PauseReason Reason { get; private set; }

    /// <summary>以目前條件重新判定，回傳需要執行的動作。</summary>
    public GateAction Evaluate(in GateConditions c)
    {
        if (!c.AllSatisfied)
        {
            _stableSince = null;
            var reason = ReasonFor(c);
            if (State != GateState.Paused)
            {
                Generation++;
                State = GateState.Paused;
                Reason = reason;
                return new GateAction(GateActionKind.Pause, Generation, reason);
            }
            if (reason != Reason)
            {
                // 仍在暫停，只更新提示訊息；世代不變
                Reason = reason;
                return new GateAction(GateActionKind.Pause, Generation, reason);
            }
            return GateAction.None;
        }

        if (State != GateState.Paused)
            return GateAction.None;

        long now = _time.GetTimestamp();
        _stableSince ??= now;
        if (_time.GetElapsedTime(_stableSince.Value, now) < _stableDuration)
            return GateAction.None;

        State = GateState.Resuming;
        _stableSince = null;
        return new GateAction(GateActionKind.BeginResume, Generation, Reason);
    }

    /// <summary>控制端回覆已顯示新世代的 IDR。回傳 true 表示進入可控制。</summary>
    public bool OnResumeAck(uint generation)
    {
        if (State != GateState.Resuming || generation != Generation)
            return false;
        State = GateState.Active;
        _everActive = true;
        return true;
    }

    /// <summary>是否接受此世代的鍵鼠事件。</summary>
    public bool AcceptsInput(uint generation) => State == GateState.Active && generation == Generation;

    private PauseReason ReasonFor(in GateConditions c)
    {
        if (c.Desktop.Locked)
            return PauseReason.Locked;
        if (!c.Desktop.OnConsole)
            return PauseReason.DesktopUnavailable;
        if (!c.Desktop.InputDesktopIsDefault)
            // 只有可靠辨識 UAC 時才顯示專用訊息，否則一律通用訊息（G0-07）
            return c.Desktop.ElevationPromptDetected ? PauseReason.ElevationPrompt : PauseReason.DesktopUnavailable;
        if (!c.VideoConnected)
            return _everActive ? PauseReason.VideoLost : PauseReason.NotStarted;
        return PauseReason.CaptureFailed;
    }
}
