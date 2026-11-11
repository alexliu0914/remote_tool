using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteTool.Connection;
using RemoteTool.Host;
using RemoteTool.Protocol;
using RemoteTool.Shared;
using RemoteTool.Video;

// 被控端（遠端電腦）進入點。
//
// 目前完成：TLS 1.3 監聽、自簽憑證與 SPKI 指紋、帳密（PBKDF2）、登入失敗鎖定、
// 三條連線（Control / Video / File）與一次性 Session Token、心跳；
// A1：DXGI 擷取主螢幕，以區塊差異 + Deflate 經 Video 連線送出（PoC，見 D-027）。
//
// 尚未實作：H.264 編碼（A2）、經 IPC 交給 HostGuard 注入鍵鼠（B1）、剪貼簿、檔案傳輸、系統列。
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

            var video = new VideoStreamerOptions
            {
                MaxFps = ParseIntOption(args, "--fps", 10, 1, 30),
                MaxWidth = ParseIntOption(args, "--max-width", 1920, 320, 8192),
                Log = Log,
            };
            return await ServeAsync(ConsoleInput.ParsePort(args, ProtocolInfo.DefaultPort), video);
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

            畫面（A1 PoC）選項：
              --fps <1~30>              畫面更新上限，預設 10
              --max-width <寬>          畫面寬度上限，超過以整數倍縮小，預設 1920
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

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static int ParseIntOption(string[] args, string name, int defaultValue, int min, int max)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0) return defaultValue;
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int value) || value < min || value > max)
            throw new ArgumentException($"{name} 後面要接 {min}~{max} 的數字");
        return value;
    }

    /// <summary>每條 Video 子連線各自建立一個擷取器，連線結束時釋放。</summary>
    private static async Task RunVideoAsync(SubChannelContext ctx, VideoStreamerOptions options)
    {
        using var capture = new DxgiScreenCapture(Log);
        await VideoStreamer.RunAsync(capture, ctx.Stream, ctx.Incoming, options, ctx.Token);
    }

    private static async Task<int> ServeAsync(int port, VideoStreamerOptions video)
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
            Log = Log,
            SubChannelHandler = ctx => ctx.Channel == ChannelKind.Video
                ? RunVideoAsync(ctx, video)
                : Task.Delay(Timeout.Infinite, ctx.Token), // File 通道於檔案傳輸階段實作
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
        Console.WriteLine($"  畫面：上限 {video.MaxFps} FPS、寬 {video.MaxWidth}（A1 PoC）");
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
