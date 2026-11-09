namespace RemoteTool.HostGuard.Core;

/// <summary>由 HostGuard 啟動的 Host.exe 程序。</summary>
public interface IHostProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>等待程序結束並回傳結束碼。HostGuard 持有程序 handle，結束碼可靠。</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>啟動 Host.exe（Windows 實作傳入 IPC 管道名稱）。</summary>
public interface IHostLauncher
{
    IHostProcess Start();
}

/// <summary>
/// 監看與復原（第三節）：啟動 Host.exe、等待結束、通知注入迴圈釋放按鍵，再依重啟政策處理。
/// </summary>
public sealed class HostSupervisor
{
    private readonly IHostLauncher _launcher;
    private readonly GuardLoop _loop;
    private readonly RestartPolicy _policy;
    private readonly IGuardLog _log;
    private readonly TimeProvider _time;
    private int _currentProcessId;
    private long _currentInstance;

    public HostSupervisor(IHostLauncher launcher, GuardLoop loop, RestartPolicy? policy = null,
        IGuardLog? log = null, TimeProvider? time = null)
    {
        _launcher = launcher;
        _loop = loop;
        _policy = policy ?? new RestartPolicy();
        _log = log ?? NullGuardLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>目前由本 HostGuard 啟動且仍在執行的 Host.exe 程序 ID；沒有時為 0。IPC 只接受此程序。</summary>
    public int CurrentProcessId => Volatile.Read(ref _currentProcessId);

    /// <summary>目前 Host 實例在 <see cref="GuardLoop"/> 中的編號。</summary>
    public long CurrentInstance => Interlocked.Read(ref _currentInstance);

    /// <summary>
    /// 執行直到 Host 正常結束、放棄重啟或取消。回傳最後的決定。
    /// </summary>
    public async Task<RestartAction> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long instance = _loop.BeginHostInstance();
            int exitCode;
            using (var process = _launcher.Start())
            {
                Interlocked.Exchange(ref _currentInstance, instance);
                Volatile.Write(ref _currentProcessId, process.ProcessId);
                _log.Info($"已啟動 Host.exe（PID {process.ProcessId}，實例 {instance}）");
                try
                {
                    exitCode = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _currentProcessId, 0);
                    _loop.HostGone(instance);
                }
            }

            var decision = _policy.OnExit(exitCode, _time.GetUtcNow());
            switch (decision.Action)
            {
                case RestartAction.Stop:
                    _log.Info("Host.exe 正常結束，HostGuard 一併結束");
                    return RestartAction.Stop;
                case RestartAction.GiveUp:
                    _log.Warn($"Host.exe 在 10 分鐘內崩潰 {decision.CrashesInWindow} 次，不再重啟（結束碼 0x{exitCode:X8}）");
                    return RestartAction.GiveUp;
                default:
                    _log.Warn($"Host.exe 異常結束（結束碼 0x{exitCode:X8}），{decision.Delay.TotalSeconds:0} 秒後重啟（10 分鐘內第 {decision.CrashesInWindow} 次）");
                    await Task.Delay(decision.Delay, _time, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }
}
