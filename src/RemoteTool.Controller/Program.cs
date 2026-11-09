using System.Text;
using RemoteTool.Controller.Core;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;

// Controller.exe — 控制端（Spike 0 主控台版，用於 G0-01 / G0-03 雙機驗證）。
//
//   Controller.exe <被控端 IP 或網域> --user <帳號> [--port 52100]
//   Controller.exe --forget <位址>          移除該位址的主機信任記錄（金鑰變更後人工重新核准）
//
// 連線後可輸入：
//   text <文字>          以「貼上 Unicode 文字」模式送出
//   key <掃描碼16進位> [e]  按下並放開一個鍵（e = extended），例：key 1C
//   hold <掃描碼16進位>     按住一個鍵（測試按鍵釋放：之後拔網路線或結束 Host）
//   release <掃描碼16進位>  放開
//   move <x> <y>          移動滑鼠到虛擬桌面座標（0–65535，例：move 32768 32768 為中央）
//   click <left|right|middle>   按一下滑鼠
//   mdown / mup <left|right|middle>  按住／放開滑鼠按鈕
//   wheel <量>            滾輪（120 為一格，負數向下）
//   drop video|file       強制中斷子連線，驗證獨立重連（NET-03）
//   status                顯示狀態
//   quit                  中斷連線

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var known = new KnownHostStore(KnownHostStore.DefaultPath);

if (Arg("--forget") is { } forget)
{
    Console.WriteLine(known.ForgetAddress(forget) ? $"已移除 {forget} 的信任記錄。" : "沒有該位址的記錄。");
    return 0;
}

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) || Arg("--user") is not { } user)
{
    Console.Error.WriteLine("用法：Controller.exe <被控端 IP> --user <帳號> [--port 52100]");
    return 1;
}

var host = args[0];
int port = int.TryParse(Arg("--port"), out int p) ? p : ProtocolConstants.DefaultPort;
Console.Write("密碼：");
var password = ReadPassword();

ControllerClient client;
try
{
    client = await ControllerClient.ConnectAsync(new ControllerClientOptions
    {
        Host = host,
        Port = port,
        Username = user,
        Password = password,
        KnownHosts = known,
        ConfirmFingerprint = (prompt, _) =>
        {
            Console.WriteLine();
            Console.WriteLine($"第一次連線到 {prompt.Address}。主機指紋 SHA-256(SPKI)：");
            Console.WriteLine($"    {prompt.FingerprintText}");
            Console.WriteLine("請在被控端執行 Host.exe --fingerprint（或看 Host 的記錄）逐字核對。");
            Console.Write("兩邊完全相同嗎？輸入 yes 繼續：");
            return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
        },
    });
}
catch (ConnectException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

int frames = 0;
client.Paused += (reason, gen) => Console.WriteLine($"[暫停，非即時畫面] {Describe(reason)}（世代 {gen}）");
client.Resumed += gen => Console.WriteLine($"[可控制] 世代 {gen}");
client.FrameReceived += _ => Interlocked.Increment(ref frames);
client.SubConnectionChanged += (role, up) => Console.WriteLine(up ? $"[{role} 已連線]" : $"[{role} 中斷，重新連線中…]");
client.ReconnectFailed += role => Console.WriteLine($"[{role} 30 秒內未能重連，輸入 drop {role.ToString().ToLowerInvariant()} 可重試]");
_ = client.Completion.ContinueWith(t => Console.WriteLine($"[連線已結束：{t.Result}]"), TaskScheduler.Default);

Console.WriteLine("已連線。輸入 status / text / key / hold / release / move / click / mdown / mup / wheel / drop / quit。");
while (!client.Completion.IsCompleted)
{
    var line = await Task.Run(Console.ReadLine);
    if (line is null)
        break;
    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        continue;
    try
    {
        switch (parts[0])
        {
            case "quit":
                await client.DisposeAsync();
                return 0;
            case "status":
                Console.WriteLine($"可控制：{client.CanSendInput}，世代 {client.Generation}，Video：{client.IsVideoConnected}，File：{client.IsFileConnected}，已收畫面 {frames}");
                break;
            case "text" when parts.Length == 2:
                Report(await client.SendUnicodeTextAsync(parts[1]));
                break;
            case "key" when parts.Length == 2:
            {
                var (scan, ext) = ParseKey(parts[1]);
                Report(await client.SendKeyAsync(scan, ext, true) && await client.SendKeyAsync(scan, ext, false));
                break;
            }
            case "hold" when parts.Length == 2:
            {
                var (scan, ext) = ParseKey(parts[1]);
                Report(await client.SendKeyAsync(scan, ext, true));
                break;
            }
            case "release" when parts.Length == 2:
            {
                var (scan, ext) = ParseKey(parts[1]);
                Report(await client.SendKeyAsync(scan, ext, false));
                break;
            }
            case "move" when parts.Length == 2:
            {
                var xy = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Report(await client.SendMouseMoveAsync(ushort.Parse(xy[0]), ushort.Parse(xy[1])));
                break;
            }
            case "click" when parts.Length == 2:
            {
                var b = ParseButton(parts[1]);
                Report(await client.SendMouseButtonAsync(b, true) && await client.SendMouseButtonAsync(b, false));
                break;
            }
            case "mdown" when parts.Length == 2:
                Report(await client.SendMouseButtonAsync(ParseButton(parts[1]), true));
                break;
            case "mup" when parts.Length == 2:
                Report(await client.SendMouseButtonAsync(ParseButton(parts[1]), false));
                break;
            case "wheel" when parts.Length == 2:
                Report(await client.SendMouseWheelAsync(short.Parse(parts[1]), false));
                break;
            case "drop" when parts.Length == 2:
                var role = parts[1] == "video" ? ChannelRole.Video : ChannelRole.File;
                if ((role == ChannelRole.Video ? client.IsVideoConnected : client.IsFileConnected))
                    await client.DropSubConnectionAsync(role);
                else
                    await client.ReconnectAsync(role);
                break;
            default:
                Console.WriteLine("未知指令。");
                break;
        }
    }
    catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException or ConnectException or IOException)
    {
        Console.WriteLine(ex.Message);
    }
}
await client.DisposeAsync();
return 0;

static void Report(bool sent) => Console.WriteLine(sent ? "已送出" : "目前不可控制，未送出");

static (ushort Scan, bool Extended) ParseKey(string text)
{
    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return (Convert.ToUInt16(parts[0], 16), parts.Length > 1 && parts[1] == "e");
}

static MouseButton ParseButton(string text) => text.Trim() switch
{
    "left" => MouseButton.Left,
    "right" => MouseButton.Right,
    "middle" => MouseButton.Middle,
    _ => throw new FormatException("按鈕須為 left、right 或 middle"),
};

static string Describe(PauseReason reason) => reason switch
{
    PauseReason.Locked => "遠端已鎖定，等待本機解鎖",
    PauseReason.ElevationPrompt => "遠端出現系統管理員提示，需本機處理",
    PauseReason.VideoLost => "畫面重新連線中",
    PauseReason.NotStarted => "等待畫面",
    _ => "遠端桌面暫時無法操作",
};

static string ReadPassword()
{
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
