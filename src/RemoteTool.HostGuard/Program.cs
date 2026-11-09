using System.Diagnostics;
using System.Runtime.Versioning;
using RemoteTool.HostGuard;
using RemoteTool.HostGuard.Core;
using RemoteTool.Windows;

// HostGuard.exe — 登入時由排程工作以使用者權限啟動（第三節）。
// 啟動 Host.exe、經 Named Pipe 接收已授權的鍵鼠事件並以 SendInput 注入、追蹤按鍵狀態、監看與重啟 Host.exe。
// 不連網、不讀剪貼簿、不處理畫面或檔案，沒有 SYSTEM 權限。
//
// 結束碼：0 = Host 正常結束；3 = 放棄重啟；4 = 同一工作階段已有 HostGuard。

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("HostGuard 只支援 Windows 11。");
    return 1;
}
return await GuardMain.RunAsync();

[SupportedOSPlatform("windows")]
internal static class GuardMain
{
    public static async Task<int> RunAsync()
    {
        var log = new GuardFileLog(new FileLog("hostguard"));

        using var mutex = new Mutex(initiallyOwned: true, @"Local\RemoteTool.HostGuard", out bool created);
        if (!created)
        {
            log.Warn("同一工作階段已有 HostGuard 執行中，結束");
            return 4;
        }

        var hostPath = Path.Combine(AppContext.BaseDirectory, "Host.exe");
        if (!File.Exists(hostPath))
        {
            log.Warn($"找不到 {hostPath}");
            return 1;
        }

        var engine = new InjectionEngine(new Win32InputInjector(), new WindowsDesktopProbe(), log);
        await using var loop = new GuardLoop(engine, log);
        string pipeName = GuardPipeServer.NewPipeName();
        var supervisor = new HostSupervisor(new HostLauncher(hostPath, pipeName), loop, log: log);
        var pipeServer = new GuardPipeServer(pipeName, supervisor, loop, log);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        log.Info($"HostGuard 啟動（PID {Environment.ProcessId}，工作階段 {DesktopState.CurrentSessionId}）");
        var pipeTask = pipeServer.RunAsync(cts.Token);
        RestartAction result;
        try
        {
            result = await supervisor.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = RestartAction.Stop;
        }
        cts.Cancel();
        try { await pipeTask; } catch (OperationCanceledException) { }

        if (result == RestartAction.GiveUp)
        {
            log.Event("Host.exe 在 10 分鐘內異常結束 6 次，HostGuard 停止重啟。", EventLogEntryType.Error);
            return 3;
        }
        return 0;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class HostLauncher(string hostPath, string pipeName) : IHostLauncher
{
    public IHostProcess Start()
    {
        var psi = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
        };
        psi.ArgumentList.Add("--guard-pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--guard-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var process = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 Host.exe");
        return new HostProcess(process);
    }

    private sealed class HostProcess(Process process) : IHostProcess
    {
        public int ProcessId => process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Dispose() => process.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class GuardFileLog(FileLog file) : IGuardLog
{
    public void Info(string message) => file.Write("INFO", message);
    public void Warn(string message) => file.Write("WARN", message);
    public void Event(string message, EventLogEntryType type)
    {
        file.Write("EVENT", message);
        file.WriteEvent(message, type);
    }
}
