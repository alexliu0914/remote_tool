using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;
using RemoteTool.Host.Core;
using RemoteTool.HostGuard.Core;
using RemoteTool.Protocol.Messages;
using RemoteTool.Windows;

namespace RemoteTool.Host;

/// <summary>DPAPI（目前使用者）保護設定檔與主機私鑰。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "RemoteTool.Host.v1"u8.ToArray();
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// 到 HostGuard 的 Named Pipe 用戶端。確認伺服器端程序就是啟動自己的 HostGuard；
/// 寫入失敗或 HostGuard 結束即觸發 <see cref="Faulted"/>（Host 須立即停止遠端控制）。待 GUARD-04、05 實機驗證。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GuardPipeClient : IGuardChannel, IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Process _guardProcess;
    private int _faulted;

    private GuardPipeClient(NamedPipeClientStream pipe, Process guardProcess)
    {
        _pipe = pipe;
        _guardProcess = guardProcess;
        _ = WatchGuardAsync();
    }

    public static async Task<GuardPipeClient> ConnectAsync(string pipeName, int guardPid, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            if (!Win32.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != (uint)guardPid)
                throw new UnauthorizedAccessException($"IPC 伺服器 PID {serverPid} 不是啟動 Host 的 HostGuard（{guardPid}）");

            return new GuardPipeClient(pipe, Process.GetProcessById(guardPid));
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool IsConnected => Volatile.Read(ref _faulted) == 0 && _pipe.IsConnected;

    public event Action? Faulted;

    public async ValueTask<bool> SendAsync(GuardMessage message)
    {
        if (!IsConnected)
            return false;
        var bytes = GuardCodec.Encode(message);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(bytes).ConfigureAwait(false);
            await _pipe.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            RaiseFaulted();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WatchGuardAsync()
    {
        try { await _guardProcess.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        RaiseFaulted();
    }

    private void RaiseFaulted()
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            Faulted?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _guardProcess.Dispose();
    }
}

/// <summary>
/// 工作階段狀態：SystemEvents.SessionSwitch（內部即 WTSRegisterSessionNotification）+ 每次判定時直接查詢
/// 鎖定、主控台與輸入桌面交叉確認（第九節對策）。事件時序待 G0-03 實機驗證。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopStateSource : IDesktopStateSource, IDisposable
{
    private readonly int _sessionId = DesktopState.CurrentSessionId;
    private readonly IHostLog _log;
    private bool _onConsole;

    public WindowsDesktopStateSource(IHostLog log)
    {
        _log = log;
        _onConsole = DesktopState.IsOnConsole(_sessionId);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    public bool IsOnConsole => DesktopState.IsOnConsole(_sessionId);

    public DesktopSnapshot GetSnapshot()
    {
        bool onConsole = DesktopState.IsOnConsole(_sessionId);
        // 輪詢交叉確認主控台狀態，避免漏接事件
        if (onConsole != _onConsole)
        {
            _onConsole = onConsole;
            _log.Info($"輪詢偵測到主控台狀態改變：{onConsole}");
            ConsoleActiveChanged?.Invoke(onConsole);
        }
        return new DesktopSnapshot(
            Locked: DesktopState.IsSessionLocked(_sessionId),
            OnConsole: onConsole,
            InputDesktopIsDefault: DesktopState.IsInputDesktopDefault(),
            ElevationPromptDetected: false); // G0-07 尚未找到可靠辨識方式，一律用通用訊息
    }

    public event Action<DisconnectReason>? SessionEnding;
    public event Action<bool>? ConsoleActiveChanged;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _log.Info($"工作階段事件：{e.Reason}");
        switch (e.Reason)
        {
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.ConsoleConnect:
                bool onConsole = DesktopState.IsOnConsole(_sessionId);
                if (onConsole != _onConsole)
                {
                    _onConsole = onConsole;
                    ConsoleActiveChanged?.Invoke(onConsole);
                }
                break;
            case SessionSwitchReason.SessionLogoff:
                SessionEnding?.Invoke(DisconnectReason.UserLoggedOff);
                break;
            // 鎖定與解鎖由每 100 ms 的判定直接查詢，不依賴事件
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _log.Info($"系統工作階段結束：{e.Reason}");
        SessionEnding?.Invoke(e.Reason == SessionEndReasons.Logoff ? DisconnectReason.UserLoggedOff : DisconnectReason.HostShuttingDown);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}

/// <summary>
/// G0-02 尚未實作（DXGI Desktop Duplication → Media Foundation H.264）。
/// 此替代來源每 500 ms 送一個空白「關鍵幀」，只用來驗證 G0-01 / G0-03 的恢復程序；
/// 它不代表擷取正常，G0-02 完成前「DXGI 擷取正常」這一項判定沒有實際意義。
/// </summary>
internal sealed class PlaceholderVideoSource : IVideoSource
{
    public bool IsCaptureHealthy => true;

    public void RequestKeyframe() { }

    public async IAsyncEnumerable<EncodedFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    yield break;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return new EncodedFrame(true, Environment.TickCount64 * 1000, []);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class HostFileLog(FileLog file) : IHostLog
{
    public void Info(string message) => file.Write("INFO", message);
    public void Warn(string message) => file.Write("WARN", message);
    public void Audit(string message)
    {
        file.Write("AUDIT", message);
        file.WriteEvent(message, EventLogEntryType.Information);
    }
}
