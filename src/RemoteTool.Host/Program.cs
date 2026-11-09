using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using RemoteTool.Host;
using RemoteTool.Host.Core;
using RemoteTool.Protocol;
using RemoteTool.Windows;

// Host.exe — 被控端主程式（Spike 0 主控台版；系統列介面於 M1 實作）。
//
//   Host.exe --add-account <名稱>        建立或更新帳號（互動輸入密碼，至少 10 字元）
//   Host.exe --remove-account <名稱>
//   Host.exe --fingerprint               顯示主機指紋 SHA-256(SPKI)，供控制端首次連線時核對
//   Host.exe --guard-pipe <名稱> --guard-pid <PID> [--port 52100]
//                                        由 HostGuard 啟動時使用；不經 HostGuard 不提供遠端控制
//
// 結束碼：0 = 正常結束（不重啟）；2 = HostGuard IPC 中斷；1 = 設定錯誤；其他非 0 = 異常（HostGuard 依政策重啟）。

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Host 只支援 Windows 11。");
    return 1;
}
Console.OutputEncoding = Encoding.UTF8;
return await HostMain.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class HostMain
{
    public static async Task<int> RunAsync(string[] args)
    {
        var store = new HostConfigStore(Path.Combine(HostConfigStore.DefaultDirectory, "host.dat"), new DpapiProtector());
        var settings = store.Load();
        using var identity = store.LoadOrCreateIdentity(settings);
        var accounts = new AccountStore(settings, new PasswordHasher());

        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--add-account") is { } addName)
        {
            var password = ReadPassword("密碼（至少 10 字元）：");
            if (password != ReadPassword("再輸入一次："))
            {
                Console.Error.WriteLine("兩次輸入不一致。");
                return 1;
            }
            try
            {
                accounts.SetPassword(addName, password);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            store.Save(settings);
            Console.WriteLine($"已儲存帳號 {addName}。");
            return 0;
        }
        if (Arg("--remove-account") is { } removeName)
        {
            Console.WriteLine(accounts.Remove(removeName) ? "已移除。" : "找不到該帳號。");
            store.Save(settings);
            return 0;
        }
        if (args.Contains("--fingerprint"))
        {
            Console.WriteLine(identity.FingerprintText);
            return 0;
        }

        var pipeName = Arg("--guard-pipe");
        if (pipeName is null || !int.TryParse(Arg("--guard-pid"), out int guardPid))
        {
            Console.Error.WriteLine("Host.exe 須由 HostGuard.exe 啟動（遠端鍵鼠只由 HostGuard 注入）。請執行 HostGuard.exe。");
            return 1;
        }
        int port = int.TryParse(Arg("--port"), out int p) ? p : ProtocolConstants.DefaultPort;

        var log = new HostFileLog(new FileLog("host"));
        if (accounts.Usernames.Count == 0)
            log.Warn("尚未建立任何帳號，請先執行 Host.exe --add-account <名稱>");

        await using var guard = await GuardPipeClient.ConnectAsync(pipeName, guardPid, CancellationToken.None);
        using var desktop = new WindowsDesktopStateSource(log);

        var server = new HostServer(new HostServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.IPv6Any, port) ,
                Certificate = identity.Certificate,
                WindowsSessionId = DesktopState.CurrentSessionId,
            },
            accounts, new LoginThrottle(), new SessionTokenService(log: log), guard, desktop, new PlaceholderVideoSource(), log);
        await using var _ = server;

        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Faulted += () => exit.TrySetResult(2);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(0); };
        server.SessionStarted += s => log.Info($"【正在被遠端操控】帳號 {s.Account}，來源 {s.RemoteAddress}");
        server.SessionEnded += (s, r) => log.Info($"遠端操控已結束：{r}");

        if (desktop.IsOnConsole)
            await server.StartListeningWithRetryAsync(TimeSpan.FromSeconds(60));
        else
            log.Info("本工作階段不在主控台，暫不監聽");

        log.Info($"主機指紋 SHA-256(SPKI)：{identity.FingerprintText}");
        log.Info("按 Ctrl+C 結束（正常結束，HostGuard 不會重啟）。");
#if DEBUG
        StartTestHooks(exit, log);
#endif

        int code = await exit.Task;
        log.Info(code == 0 ? "Host 正常結束" : "HostGuard IPC 中斷，停止遠端控制並結束");
        return code;
    }

#if DEBUG
    /// <summary>
    /// 僅 Debug 建置：Spike 0 實測用按鍵（在 Host 主控台視窗按下）。
    ///   Q = 正常結束（結束碼 0，模擬系統列「結束」；GUARD-03）
    ///   X = 未處理例外（GUARD-01 / GUARD-03）
    /// </summary>
    private static void StartTestHooks(TaskCompletionSource<int> exit, IHostLog log)
    {
        if (Console.IsInputRedirected)
            return;
        log.Info("[Debug 測試按鍵] Q = 正常結束，X = 未處理例外");
        var thread = new Thread(() =>
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Q)
                {
                    log.Info("[測試] 正常結束");
                    exit.TrySetResult(0);
                    return;
                }
                if (key == ConsoleKey.X)
                {
                    log.Warn("[測試] 觸發未處理例外");
                    throw new InvalidOperationException("Spike 0 測試用未處理例外（GUARD-01）");
                }
            }
        }) { IsBackground = true, Name = "TestHooks" };
        thread.Start();
    }
#endif

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
