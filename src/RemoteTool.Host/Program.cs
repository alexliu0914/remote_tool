using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using RemoteTool.Connection;
using RemoteTool.Host;
using RemoteTool.Media;
using RemoteTool.Protocol;
using RemoteTool.Shared;
using RemoteTool.Video;

// 被控端（遠端電腦）進入點。
//
// 目前完成：TLS 1.3 監聽、自簽憑證與 SPKI 指紋、帳密（Argon2id）、登入節流（漸進延遲 + 全域節流 + 連線上限）、
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
            ValidateArguments(args);
            if (args.Contains("--setup") || args.Contains("--add-user"))
                return Setup();
            if (args.Contains("--list-users"))
                return ListUsers();
            int ri = Array.IndexOf(args, "--remove-user");
            if (ri >= 0)
            {
                if (ri + 1 >= args.Length) throw new ArgumentException("--remove-user 後面要接帳號");
                return RemoveUser(args[ri + 1]);
            }
            if (args.Contains("--fingerprint"))
            {
                using var cert = HostCertificate.LoadOrCreate(AppPaths.HostCertificateFile);
                Console.WriteLine(Fingerprint.ForDisplay(Fingerprint.Compute(cert)));
                return 0;
            }

            string codec = ParseChoice(args, "--codec", "auto", ["auto", "h264", "h264-hw", "h264-sw", "tiles"]);
            int bitrate = ParseIntOption(args, "--bitrate", 6000, 500, 50000);
            var mode = codec switch
            {
                "h264-hw" => H264EncoderMode.Hardware,
                "h264-sw" => H264EncoderMode.Software,
                _ => H264EncoderMode.Auto,
            };
            bool h264 = false;
            string h264Detail = "已用 --codec tiles 停用";
            if (codec != "tiles")
            {
                Console.WriteLine("檢查 H.264 編碼器…");
                h264 = ResilientH264Encoder.Probe(mode, out h264Detail, Log);
                if (!h264 && codec != "auto")
                {
                    Console.Error.WriteLine($"無法使用指定的 H.264 編碼器（{codec}）：{h264Detail}");
                    return 1;
                }
            }
            // H.264 時預設 30 FPS（規劃書目標），區塊差異（A1）維持 10 FPS
            int fps = ParseIntOption(args, "--fps", h264 ? 30 : 10, 1, 60);
            var video = new VideoStreamerOptions
            {
                MaxFps = fps,
                MaxWidth = ParseIntOption(args, "--max-width", 1920, 320, 8192),
                Log = Log,
                EncoderFactory = h264 ? (w, h) => new ResilientH264Encoder(w, h, fps, bitrate, mode, Log) : null,
            };
            _videoFormat = h264
                ? $"H.264 可用（{h264Detail}），{bitrate} kbps；控制端不支援時改用區塊差異"
                : $"區塊差異（A1）—— H.264 不可用：{h264Detail}";
            var drops = new TestDrops(
                ParseIntOption(args, "--test-drop-video", 0, 5, 3600),
                ParseIntOption(args, "--test-drop-file", 0, 5, 3600));
            return await ServeAsync(ConsoleInput.ParsePort(args, ProtocolInfo.DefaultPort), video, args.Contains("--memlog") || args.Contains("--memlog-fullgc"), drops, args.Contains("--memlog-fullgc"));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("參數錯誤：" + ex.Message);
            PrintHelp();
            return 2;
        }
    }

    /// <summary>不認得的參數一律報錯（避免打錯字被默默忽略，例如 --text-drop-video）。</summary>
    private static void ValidateArguments(string[] args)
    {
        string[] flags = ["--setup", "--add-user", "--list-users", "--fingerprint", "--memlog", "--memlog-fullgc"];
        string[] withValue = ["--port", "--fps", "--max-width", "--test-drop-video", "--test-drop-file", "--remove-user", "--codec", "--bitrate"];
        for (int i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i])) continue;
            if (withValue.Contains(args[i]))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 後面缺少值");
                i++;
                continue;
            }
            throw new ArgumentException($"不認得的參數「{args[i]}」，請檢查拼字");
        }
    }

    /// <summary>建置版本（含 git commit），用來確認執行的是不是最新建置。</summary>
    public static string BuildVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion is { } v
            ? (v.IndexOf('+') is int plus and >= 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v)
            : "未知";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            RemoteTool Host（被控端）

            用法：
              Host.exe --setup          新增帳號，或變更既有帳號的密碼（可建立多組帳號）
              Host.exe --list-users     列出帳號
              Host.exe --remove-user <帳號>  刪除帳號
              Host.exe                  啟動被控端，等待控制端連線（預設埠 52100）
              Host.exe --port <埠>      以指定埠啟動
              Host.exe --fingerprint    顯示本機主機指紋

            畫面選項：
              --codec <格式>            auto（預設：硬體 H.264 → 軟體 H.264 → 區塊差異）
                                        h264（必須 H.264，硬體優先）、h264-hw（只用硬體）、h264-sw（只用軟體）
                                        tiles（A1 區塊差異）
              --bitrate <kbps>          H.264 位元率，預設 6000
              --fps <1~60>              畫面更新上限，預設 H.264 為 30、區塊差異為 10
              --max-width <寬>          畫面寬度上限，超過以整數倍縮小，預設 1920

            診斷：
              --memlog                  每 60 秒顯示記憶體用量（工作集、私有、GC 已提交、原生估計、GC 次數）
              --memlog-fullgc           同上，但每次先做完整 GC（判斷是否真的洩漏；會短暫停頓）

            測試專用（NET-03 子連線重連，正式使用請勿加）：
              --test-drop-video <秒>    每條 Video 連線建立後經過指定秒數即由被控端關閉
              --test-drop-file <秒>     每條 File 連線建立後經過指定秒數即由被控端關閉
            """);
        Console.WriteLine($"資料位置：{AppPaths.DataDirectory}");
        Console.WriteLine($"版本：{BuildVersion}");
    }

    /// <summary>讀取帳號集合；檔案不存在回傳空集合，格式問題印出訊息並回傳 null。</summary>
    private static CredentialSet? LoadAccountsForEdit()
    {
        try
        {
            return CredentialStore.Load(AppPaths.HostCredentialsFile) ?? new CredentialSet([]);
        }
        catch (CredentialFormatException)
        {
            Console.WriteLine("現有帳密檔為舊格式（PBKDF2），將以新帳號取代。");
            return new CredentialSet([]);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"帳密檔無法讀取（{ex.Message}）。");
            Console.Error.WriteLine($"若要重新開始，請刪除 {AppPaths.HostCredentialsFile} 後再執行 --setup。");
            return null;
        }
    }

    private static int Setup()
    {
        var accounts = LoadAccountsForEdit();
        if (accounts is null) return 1;

        Console.WriteLine("新增帳號或變更密碼（控制端連線時輸入這組帳密）。");
        if (accounts.Count > 0)
            Console.WriteLine($"目前帳號：{string.Join("、", accounts.Accounts.Select(a => a.Username))}");
        string user = ConsoleInput.Prompt("帳號：");
        if (user.Length == 0 || user.Length > ProtocolInfo.MaxUsernameLength)
        {
            Console.Error.WriteLine("帳號不可空白，且不可超過 256 字。");
            return 1;
        }
        if (accounts.Contains(user) && !ConsoleInput.Confirm($"帳號「{user}」已存在，要變更它的密碼嗎？(y/N)："))
            return 0;

        string pass = ConsoleInput.PromptSecret($"密碼（至少 {PasswordHasher.MinPasswordLength} 字）：");
        if (pass.Length < PasswordHasher.MinPasswordLength || pass.Length > ProtocolInfo.MaxPasswordLength)
        {
            Console.Error.WriteLine($"密碼長度須為 {PasswordHasher.MinPasswordLength} ~ {ProtocolInfo.MaxPasswordLength} 字。");
            return 1;
        }
        if (ConsoleInput.PromptSecret("再輸入一次密碼：") != pass)
        {
            Console.Error.WriteLine("兩次密碼不同，未儲存。");
            return 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var record = PasswordHasher.Create(user, pass);
        sw.Stop();
        accounts = accounts.With(record);
        CredentialStore.Save(AppPaths.HostCredentialsFile, accounts);
        Console.WriteLine($"已儲存帳號「{user}」（Argon2id，只存雜湊與隨機鹽；檔案以 {SecretProtector.Default.Name} 保護）：{AppPaths.HostCredentialsFile}");
        Console.WriteLine($"雜湊參數：記憶體 {record.MemoryKiB} KiB、次數 {record.Iterations}、平行度 {record.Parallelism}；本機耗時 {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"目前共 {accounts.Count} 組帳號。");
        return 0;
    }

    private static int ListUsers()
    {
        var accounts = LoadAccountsForEdit();
        if (accounts is null) return 1;
        if (accounts.Count == 0)
        {
            Console.WriteLine("尚未設定任何帳號，請執行 Host.exe --setup。");
            return 0;
        }
        foreach (var a in accounts.Accounts) Console.WriteLine(a.Username);
        return 0;
    }

    private static int RemoveUser(string user)
    {
        var accounts = LoadAccountsForEdit();
        if (accounts is null) return 1;
        if (!accounts.Contains(user))
        {
            Console.Error.WriteLine($"沒有帳號「{user}」。");
            return 1;
        }
        if (!ConsoleInput.Confirm($"確定刪除帳號「{user}」？(y/N)：")) return 0;
        accounts = accounts.Without(user);
        CredentialStore.Save(AppPaths.HostCredentialsFile, accounts);
        Console.WriteLine($"已刪除。剩餘 {accounts.Count} 組帳號" + (accounts.Count == 0 ? "（Host 需至少一組帳號才能啟動）。" : "。"));
        return 0;
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>V-12 診斷：工作集（含原生記憶體）與受控堆積分開顯示，用來判斷成長來自哪裡。</summary>
    private static void LogMemory(bool fullGc = false)
    {
        if (fullGc)
        {
            // 診斷用：先做完整壓縮 GC，讓「受控堆積」代表真正仍在使用的記憶體（會短暫停頓，正式使用請勿開）
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long privateMb = p.PrivateMemorySize64 / 1048576;
        long committedMb = info.TotalCommittedBytes / 1048576;
        // 原生（估計）＝私有記憶體 − GC 已向系統取得的記憶體：用來判斷成長來自 .NET 堆積還是原生（DXGI、Media Foundation、驅動程式）
        Log($"記憶體：工作集 {p.WorkingSet64 / 1048576} MB、私有 {privateMb} MB｜GC 已提交 {committedMb} MB、" +
            $"受控堆積 {GC.GetTotalMemory(false) / 1048576} MB（GC 後 {info.HeapSizeBytes / 1048576} MB）｜原生（估計）{privateMb - committedMb} MB｜" +
            $"GC 0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    }

    private static string _videoFormat = "";

    private static string ParseChoice(string[] args, string name, string defaultValue, string[] choices)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0) return defaultValue;
        if (i + 1 >= args.Length || !choices.Contains(args[i + 1]))
            throw new ArgumentException($"{name} 後面要接 {string.Join(" / ", choices)}");
        return args[i + 1];
    }

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
        bool clientH264 = ctx.ClientCapabilities.Contains(ProtocolInfo.CapabilityVideoH264);
        if (options.EncoderFactory is not null && !clientH264)
            Log("控制端未宣告 H.264，此連線使用區塊差異格式");
        await VideoStreamer.RunAsync(capture, ctx.Stream, ctx.Incoming, options, ctx.Token, clientH264);
    }

    /// <summary>測試用：子連線建立後經過指定秒數就關閉（0 = 不關閉）。</summary>
    private sealed record TestDrops(int VideoSeconds, int FileSeconds);

    private static async Task HandleSubChannelAsync(SubChannelContext ctx, VideoStreamerOptions video, TestDrops drops)
    {
        int seconds = ctx.Channel == ChannelKind.Video ? drops.VideoSeconds : drops.FileSeconds;
        using var dropCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        if (seconds > 0) dropCts.CancelAfter(TimeSpan.FromSeconds(seconds));
        var scoped = new SubChannelContext(ctx.Channel, ctx.Stream, ctx.Incoming, ctx.Remote, dropCts.Token, ctx.ClientCapabilities);
        try
        {
            if (ctx.Channel == ChannelKind.Video) await RunVideoAsync(scoped, video);
            else await Task.Delay(Timeout.Infinite, dropCts.Token); // File 通道於檔案傳輸階段實作
        }
        catch (OperationCanceledException) when (seconds > 0 && !ctx.Token.IsCancellationRequested)
        {
            // 影像迴圈可能拋出取消，也可能正常結束；兩種情況都在下面記錄
        }
        // 影像串流在取消時會正常結束而不拋例外，所以以計時器狀態判斷是否為測試關閉
        if (seconds > 0 && dropCts.IsCancellationRequested && !ctx.Token.IsCancellationRequested)
            Log($"[測試] 依 --test-drop 設定關閉 {ctx.Channel} 子連線");
    }

    private static async Task<int> ServeAsync(int port, VideoStreamerOptions video, bool memlog, TestDrops drops, bool fullGc = false)
    {
        CredentialSet? credentials;
        try
        {
            credentials = CredentialStore.Load(AppPaths.HostCredentialsFile);
        }
        catch (CredentialFormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"帳密檔內容不合法（{ex.Message}），請重新執行 Host.exe --setup。");
            return 1;
        }
        if (credentials is null || credentials.Count == 0)
        {
            Console.Error.WriteLine("尚未設定帳號密碼，請先執行：Host.exe --setup");
            return 1;
        }

        X509Certificate2 cert;
        try
        {
            cert = HostCertificate.LoadOrCreate(AppPaths.HostCertificateFile);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"主機金鑰無法讀取（{ex.Message}）");
            return 1;
        }
        using var _cert = cert;

        await using var server = new HostServer(new HostServerOptions
        {
            Certificate = cert,
            Credentials = credentials,
            Port = port,
            Log = Log,
            SubChannelHandler = ctx => HandleSubChannelAsync(ctx, video, drops),
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
        Console.WriteLine($"RemoteTool Host（被控端）已啟動　版本 {BuildVersion}");
        Console.WriteLine($"  帳號：{string.Join("、", credentials.Accounts.Select(a => a.Username))}");
        Console.WriteLine($"  金鑰與設定保護：{SecretProtector.Default.Name}");
        Console.WriteLine($"  埠：  {server.Port}");
        Console.WriteLine($"  畫面：上限 {video.MaxFps} FPS、寬 {video.MaxWidth}");
        Console.WriteLine($"  影像格式：{_videoFormat}");
        if (drops.VideoSeconds > 0 || drops.FileSeconds > 0)
            Console.WriteLine($"  [測試模式] 子連線定時關閉：Video {drops.VideoSeconds} 秒、File {drops.FileSeconds} 秒");
        Console.WriteLine("  本機 IP（控制端連這個）：");
        foreach (var ip in LocalIPv4Addresses())
            Console.WriteLine($"    {ip}");
        Console.WriteLine("  主機指紋（控制端第一次連線時，核對畫面上的指紋與此相同）：");
        foreach (var line in Fingerprint.ForDisplay(server.Fingerprint).Split(Environment.NewLine))
            Console.WriteLine($"    {line}");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 結束。");
        Console.WriteLine();

        using var memTimer = memlog ? new Timer(_ => LogMemory(fullGc), null, TimeSpan.Zero, TimeSpan.FromSeconds(60)) : null;

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
