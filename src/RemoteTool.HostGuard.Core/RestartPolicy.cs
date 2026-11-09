namespace RemoteTool.HostGuard.Core;

public enum RestartAction
{
    /// <summary>正常結束（結束碼 0：系統列「結束」、登出、關機）：不重啟，HostGuard 一併結束。</summary>
    Stop,

    /// <summary>異常崩潰：延遲後重啟。</summary>
    Restart,

    /// <summary>10 分鐘內第 6 次崩潰：不再重啟，寫入事件記錄。</summary>
    GiveUp,
}

public readonly record struct RestartDecision(RestartAction Action, TimeSpan Delay, int CrashesInWindow);

/// <summary>
/// Host.exe 重啟政策（第三節）：區分正常結束與異常崩潰；
/// 異常時退避重啟 1、2、4、8、16 秒；10 分鐘內第 6 次不再重啟。
/// </summary>
public sealed class RestartPolicy
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaxRestartsInWindow = 5;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
    ];

    private readonly Queue<DateTimeOffset> _crashes = new();

    /// <summary>記錄一次結束並決定下一步。</summary>
    public RestartDecision OnExit(int exitCode, DateTimeOffset now)
    {
        if (exitCode == 0)
            return new RestartDecision(RestartAction.Stop, TimeSpan.Zero, _crashes.Count);

        while (_crashes.Count > 0 && now - _crashes.Peek() >= Window)
            _crashes.Dequeue();
        _crashes.Enqueue(now);

        int n = _crashes.Count;
        if (n > MaxRestartsInWindow)
            return new RestartDecision(RestartAction.GiveUp, TimeSpan.Zero, n);
        return new RestartDecision(RestartAction.Restart, Backoff[n - 1], n);
    }
}
