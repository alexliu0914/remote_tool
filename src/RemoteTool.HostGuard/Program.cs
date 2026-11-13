using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RemoteTool.HostGuard;
using RemoteTool.Protocol;

// HostGuard（被控端守護，B1a）：
//   - 以目前使用者權限啟動 Host.exe，經本機 Named Pipe 接收 Host 已驗證的鍵鼠事件
//   - 唯一呼叫 SendInput 的程式；注入與按鍵狀態表在同一執行緒更新（Injector）
//   - Pipe 只允許目前使用者，且只接受自己啟動的 Host.exe（檢查 client PID）
//   - Host 斷線或結束時放開所有遠端按住的鍵與按鈕
// 不連網、不讀剪貼簿、不處理畫面或檔案、無 SYSTEM。
// 尚未實作：輸入心跳逾時（B1b）、重啟政策（B1c）、工作階段 / 輸入桌面檢查與暫停（B1d）。

return HostGuardProgram.Run(args);

static class HostGuardProgram
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    private static void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [HostGuard] {m}");

    public static int Run(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-Monitor V2（PLAN §5.2）

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                RemoteTool HostGuard（被控端守護）

                用法：HostGuard.exe [Host 參數…]
                  啟動 Host.exe 並負責注入遠端鍵鼠。Host 的參數（例如 --memlog、--codec）會原樣轉給 Host。
                  帳密設定等請直接執行 Host.exe（例如 Host.exe --setup）。
                """);
            return 0;
        }

        string? hostPath = FindHost();
        if (hostPath is null)
        {
            Console.Error.WriteLine("找不到 Host.exe（應與 HostGuard.exe 放在同一個資料夾）。");
            return 1;
        }

        int session = Process.GetCurrentProcess().SessionId;
        string pipeName = $"RemoteTool.HostGuard.{session}.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var psi = new ProcessStartInfo(hostPath) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--guard-pipe");
        psi.ArgumentList.Add(pipeName);

        using var host = Process.Start(psi);
        if (host is null)
        {
            Console.Error.WriteLine("無法啟動 Host.exe");
            return 1;
        }
        Log($"已啟動 Host.exe（PID {host.Id}），等待連線…");

        using var injector = new Injector(Log);
        var pipeTask = Task.Run(() => ServePipe(server, host, injector));

        // 每 10 秒（有變化時）印出累計注入數，單機測試鍵盤時可確認事件有送到被控端
        long lastReported = 0;
        using var statTimer = new Timer(_ =>
        {
            long n = injector.Injected;
            if (n != lastReported) { lastReported = n; Log($"累計注入 {n} 個事件"); }
        }, null, 10_000, 10_000);

        // Ctrl+C 由同一個主控台的 Host 一起收到並正常結束；HostGuard 等 Host 結束
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        host.WaitForExit();
        injector.ReleaseAll();
        Log($"Host.exe 已結束（結束碼 {host.ExitCode}），已放開遠端按鍵。共注入 {injector.Injected} 個事件");
        try { server.Disconnect(); } catch { }
        return host.ExitCode;
    }

    private static string? FindHost()
    {
        string here = Path.Combine(AppContext.BaseDirectory, "Host.exe");
        if (File.Exists(here)) return here;
        // 開發時的建置目錄：src/RemoteTool.HostGuard/bin/<組態>/net10.0-windows/ → src/RemoteTool.Host/bin/<組態>/net10.0-windows/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? config = dir.Parent?.Name;
        string? src = dir.Parent?.Parent?.Parent?.Parent?.FullName;
        if (config is null || src is null) return null;
        string dev = Path.Combine(src, "RemoteTool.Host", "bin", config, dir.Name, "Host.exe");
        return File.Exists(dev) ? dev : null;
    }

    private static async Task ServePipe(NamedPipeServerStream server, Process host, Injector injector)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await server.WaitForConnectionAsync(timeout.Token);

            // 只接受自己啟動的 Host.exe（PLAN §3）；Pipe 已限定目前使用者（CurrentUserOnly）
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) || pid != (uint)host.Id)
            {
                Log($"拒絕非本程式啟動的 Pipe 連線（PID {pid}）");
                server.Disconnect();
                return;
            }
            Log("Host 已連線，開始接收遠端鍵鼠");

            var buffer = new byte[GuardRecord.Size];
            long rejected = 0;
            while (true)
            {
                try { await server.ReadExactlyAsync(buffer); }
                catch (EndOfStreamException) { break; }
                if (GuardRecord.TryRead(buffer, out var record)) injector.Enqueue(record);
                else if (++rejected <= 5) Log("丟棄白名單外或格式錯誤的訊息");
            }
        }
        catch (OperationCanceledException)
        {
            Log("Host 未在 30 秒內連上 Pipe（遠端鍵鼠停用）");
        }
        catch (IOException ex)
        {
            Log($"Pipe 中斷：{ex.Message}");
        }
        finally
        {
            injector.ReleaseAll(); // Host 斷線：放開遠端按住的鍵
            Log("Pipe 已關閉");
        }
    }
}
