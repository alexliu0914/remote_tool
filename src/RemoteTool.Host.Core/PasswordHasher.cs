using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteTool.Host.Core;

/// <summary>Argon2id 參數。</summary>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>預設：64 MiB、3 次、平行度 2（初始值，依測試機調整到單次約 0.2–0.5 秒）。</summary>
    public static readonly Argon2Parameters Default = new(64 * 1024, 3, 2);
}

/// <summary>
/// 密碼以 Argon2id 雜湊存放（第六節），格式為 PHC 字串：
/// <c>$argon2id$v=19$m=65536,t=3,p=2$&lt;salt&gt;$&lt;hash&gt;</c>。
/// </summary>
public sealed class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly Argon2Parameters _parameters;

    public PasswordHasher(Argon2Parameters? parameters = null) => _parameters = parameters ?? Argon2Parameters.Default;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, _parameters, HashSize);
        return $"$argon2id$v=19$m={_parameters.MemoryKiB},t={_parameters.Iterations},p={_parameters.Parallelism}$" +
               $"{ToB64(salt)}${ToB64(hash)}";
    }

    /// <summary>驗證密碼；格式錯誤視為不符。以固定時間比較。</summary>
    public bool Verify(string password, string encoded)
    {
        if (!TryParse(encoded, out var parameters, out var salt, out var expected))
            return false;
        var actual = Compute(password, salt, parameters, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, Argon2Parameters p, int length)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = p.MemoryKiB,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryParse(string encoded, out Argon2Parameters parameters, out byte[] salt, out byte[] hash)
    {
        parameters = Argon2Parameters.Default;
        salt = hash = [];
        var parts = encoded.Split('$');
        // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
            return false;

        int m = 0, t = 0, par = 0;
        foreach (var kv in parts[3].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], out int v))
                return false;
            switch (pair[0])
            {
                case "m": m = v; break;
                case "t": t = v; break;
                case "p": par = v; break;
                default: return false;
            }
        }
        // 拒絕不合理的參數，避免竄改設定檔造成資源耗盡
        if (m is < 8 or > 1024 * 1024 || t is < 1 or > 20 || par is < 1 or > 16)
            return false;

        try
        {
            salt = FromB64(parts[4]);
            hash = FromB64(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length < 8 || hash.Length is < 16 or > 64)
            return false;

        parameters = new Argon2Parameters(m, t, par);
        return true;
    }

    private static string ToB64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static byte[] FromB64(string text)
    {
        int pad = (4 - text.Length % 4) % 4;
        return Convert.FromBase64String(text + new string('=', pad));
    }
}
