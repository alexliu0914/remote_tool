using System.Text;

namespace RemoteTool.Shared;

/// <summary>主控台輸入輔助（Host 與 Controller 共用，以連結檔方式編入）。</summary>
internal static class ConsoleInput
{
    public static string Prompt(string label)
    {
        Console.Write(label);
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    /// <summary>讀取密碼，輸入時以 * 顯示；輸入被重新導向時改為一般讀行。</summary>
    public static string PromptSecret(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }

    public static bool Confirm(string label)
    {
        string answer = Prompt(label).ToLowerInvariant();
        return answer is "y" or "yes";
    }

    /// <summary>解析 --port N，未指定回傳預設值。</summary>
    public static int ParsePort(string[] args, int defaultPort)
    {
        int i = Array.IndexOf(args, "--port");
        if (i < 0) return defaultPort;
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int port) || port is < 1 or > 65535)
            throw new ArgumentException("--port 後面要接 1~65535 的數字");
        return port;
    }
}
