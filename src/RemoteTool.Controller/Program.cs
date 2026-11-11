using System.Security.Authentication;
using System.Net.Sockets;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Shared;

// 控制端（你的電腦）進入點。
//
// 目前完成（M1 第一步）：以 IP 建立 TLS 1.3 連線、首次連線核對主機指紋 SHA-256(SPKI)、
// 指紋變更即中止、登入、以一次性 Session Token 開 Video / File 連線、心跳。
// 指紋核對一律在送出帳密「之前」完成。
//
// 目前為主控台版本，用於驗證連線層。WPF 介面、H.264 解碼顯示、鍵鼠擷取、
// 剪貼簿與檔案拖拉於後續里程碑實作。

return await ControllerProgram.RunAsync(args);

static class ControllerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
            {
                PrintHelp();
                return 0;
            }

            int port = ConsoleInput.ParsePort(args, ProtocolInfo.DefaultPort);
            var positional = Positional(args);

            if (args.Contains("--forget"))
            {
                if (positional.Count == 0) throw new ArgumentException("--forget 後面要接 IP");
                var key = KnownHostsStore.KeyFor(positional[0], port);
                bool removed = new KnownHostsStore(AppPaths.KnownHostsFile).Remove(key);
                Console.WriteLine(removed ? $"已移除 {key} 的指紋記錄。" : $"沒有 {key} 的指紋記錄。");
                return 0;
            }

            string host = positional.Count > 0 ? positional[0] : ConsoleInput.Prompt("被控端 IP：");
            if (host.Length == 0) throw new ArgumentException("沒有輸入 IP");
            return await ConnectAsync(host, port);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("參數錯誤：" + ex.Message);
            PrintHelp();
            return 2;
        }
    }

    private static List<string> Positional(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port") { i++; continue; }
            if (args[i].StartsWith("--")) continue;
            list.Add(args[i]);
        }
        return list;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            RemoteTool Controller（控制端）

            用法：
              Controller.exe <IP>                連線到被控端（預設埠 52100）
              Controller.exe <IP> --port <埠>    指定埠
              Controller.exe --forget <IP>       刪除該主機的指紋記錄（被控端重灌或換憑證後使用）
              Controller.exe                     執行後再輸入 IP
            """);
        Console.WriteLine($"資料位置：{AppPaths.DataDirectory}");
    }

    private static async Task<int> ConnectAsync(string host, int port)
    {
        string user = ConsoleInput.Prompt("帳號：");
        string pass = ConsoleInput.PromptSecret("密碼：");
        Console.WriteLine($"連線到 {host}:{port} …");

        ControllerSession session;
        try
        {
            session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
            {
                Host = host,
                Port = port,
                Username = user,
                Password = pass,
                KnownHosts = new KnownHostsStore(AppPaths.KnownHostsFile),
                ConfirmNewHost = ConfirmNewHost,
            });
        }
        catch (FingerprintMismatchException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("警告：主機指紋與上次不同！已中止連線，帳密未送出。");
            Console.WriteLine("可能是有人在中間攔截，或被控端重新產生了憑證（例如刪除了資料夾、重灌）。");
            Console.ResetColor();
            Console.WriteLine("上次記錄：");
            Console.WriteLine(Fingerprint.ForDisplay(ex.Expected));
            Console.WriteLine("這次收到：");
            Console.WriteLine(Fingerprint.ForDisplay(ex.Actual));
            Console.WriteLine();
            Console.WriteLine($"確認是被控端自己換了憑證後，才執行：Controller.exe --forget {host}" +
                              (port != ProtocolInfo.DefaultPort ? $" --port {port}" : ""));
            return 3;
        }
        catch (FingerprintRejectedException)
        {
            Console.WriteLine("未信任此主機，已中止連線（帳密未送出）。");
            return 3;
        }
        catch (AuthenticationFailedException ex)
        {
            Console.WriteLine("登入失敗：" + ex.Message);
            return 4;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or OperationCanceledException or ProtocolViolationException or InvalidDataException)
        {
            Console.WriteLine("連線失敗：" + ex.Message);
            Console.WriteLine("請確認：被控端 Host.exe 已啟動、IP 與埠正確、被控端防火牆已開放 TCP " + port + "。");
            return 5;
        }

        await using (session)
        {
            Console.WriteLine();
            Console.WriteLine("已連線並登入。Control / Video / File 三條連線皆已建立。");
            Console.WriteLine("（畫面與鍵鼠尚未實作，目前只驗證連線與心跳。）");
            Console.WriteLine("按 Enter 中斷連線。");
            Console.WriteLine();

            var enter = Task.Run(Console.ReadLine);
            while (true)
            {
                var done = await Task.WhenAny(enter, session.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
                if (done == enter)
                {
                    Console.WriteLine("已中斷連線。");
                    return 0;
                }
                if (done == session.Completion)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 連線結束：{await session.Completion}");
                    return 6;
                }
                var rtt = session.LastRoundTrip;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 連線中，心跳來回 {(rtt is null ? "—" : $"{rtt.Value.TotalMilliseconds:0} ms")}");
            }
        }
    }

    private static bool ConfirmNewHost(string hostKey, string fingerprint)
    {
        Console.WriteLine();
        Console.WriteLine($"第一次連到 {hostKey}。對方的主機指紋：");
        Console.WriteLine(Fingerprint.ForDisplay(fingerprint));
        Console.WriteLine("請到被控端畫面核對指紋完全相同。");
        return ConsoleInput.Confirm("相同並信任這台主機？(y/N)：");
    }
}
