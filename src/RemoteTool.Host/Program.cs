using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Shared;

// 被控端（遠端電腦）進入點。
//
// 目前完成（M1 第一步）：TLS 1.3 監聽、自簽憑證與 SPKI 指紋、帳密（PBKDF2）、
// 登入失敗鎖定、三條連線（Control / Video / File）與一次性 Session Token、心跳。
//
// 尚未實作：擷取與 H.264 編碼、經 IPC 交給 HostGuard 注入鍵鼠、剪貼簿、檔案傳輸、系統列。
// 依規劃書，日後由 HostGuard 啟動並監看本程式；目前可直接執行。
// 以使用者權限執行，無 Windows 服務、無 SYSTEM。

return await HostProgram.RunAsync(args);

static class HostProgram
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
            if (args.Contains("--setup"))
                return Setup();
            if (args.Contains("--fingerprint"))
            {
                using var cert = HostCertificate.LoadOrCreate(AppPaths.HostCertificateFile);
                Console.WriteLine(Fingerprint.ForDisplay(Fingerprint.Compute(cert)));
                return 0;
            }

            return await ServeAsync(ConsoleInput.ParsePort(args, ProtocolInfo.DefaultPort));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("參數錯誤：" + ex.Message);
            PrintHelp();
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            RemoteTool Host（被控端）

            用法：
              Host.exe --setup          設定（或重設）登入帳號與密碼
              Host.exe                  啟動被控端，等待控制端連線（預設埠 52100）
              Host.exe --port <埠>      以指定埠啟動
              Host.exe --fingerprint    顯示本機主機指紋
            """);
        Console.WriteLine($"資料位置：{AppPaths.DataDirectory}");
    }

    private static int Setup()
    {
        Console.WriteLine("設定被控端登入帳號密碼（控制端連線時要輸入這組）。");
        string user = ConsoleInput.Prompt("帳號：");
        if (user.Length == 0 || user.Length > ProtocolInfo.MaxUsernameLength)
        {
            Console.Error.WriteLine("帳號不可空白，且不可超過 256 字。");
            return 1;
        }

        string pass = ConsoleInput.PromptSecret("密碼（至少 8 字）：");
        if (pass.Length < 8 || pass.Length > ProtocolInfo.MaxPasswordLength)
        {
            Console.Error.WriteLine("密碼長度須為 8 ~ 1024 字。");
            return 1;
        }
        if (ConsoleInput.PromptSecret("再輸入一次密碼：") != pass)
        {
            Console.Error.WriteLine("兩次密碼不同，未儲存。");
            return 1;
        }

        CredentialStore.Save(AppPaths.HostCredentialsFile, PasswordHasher.Create(user, pass));
        Console.WriteLine($"已儲存（只存雜湊，不存明文）：{AppPaths.HostCredentialsFile}");
        return 0;
    }

    private static async Task<int> ServeAsync(int port)
    {
        var credentials = CredentialStore.Load(AppPaths.HostCredentialsFile);
        if (credentials is null)
        {
            Console.Error.WriteLine("尚未設定帳號密碼，請先執行：Host.exe --setup");
            return 1;
        }

        using var cert = HostCertificate.LoadOrCreate(AppPaths.HostCertificateFile);

        await using var server = new HostServer(new HostServerOptions
        {
            Certificate = cert,
            Credentials = credentials,
            Port = port,
            Log = m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"),
        });

        try
        {
            server.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Console.Error.WriteLine($"埠 {port} 已被占用（是否已經開了一個 Host？）。可改用 --port 指定其他埠。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("RemoteTool Host（被控端）已啟動");
        Console.WriteLine($"  帳號：{credentials.Username}");
        Console.WriteLine($"  埠：  {server.Port}");
        Console.WriteLine("  本機 IP（控制端連這個）：");
        foreach (var ip in LocalIPv4Addresses())
            Console.WriteLine($"    {ip}");
        Console.WriteLine("  主機指紋（控制端第一次連線時，核對畫面上的指紋與此相同）：");
        foreach (var line in Fingerprint.ForDisplay(server.Fingerprint).Split(Environment.NewLine))
            Console.WriteLine($"    {line}");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 結束。");
        Console.WriteLine();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;

        Console.WriteLine("正在結束…");
        return 0;
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        return list.Count > 0 ? list : [IPAddress.Loopback];
    }
}
