using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace RemoteTool.Connection;

/// <summary>Argon2id 參數。初始值依規劃書核准：19456 KiB、2 次、平行度 1（D-037）。</summary>
public sealed record Argon2Parameters(int MemoryKiB = 19456, int Iterations = 2, int Parallelism = 1)
{
    public static Argon2Parameters Default { get; } = new();

    // 合理範圍：低於下限太弱，高於上限可被惡意設定檔用來耗盡記憶體或 CPU。
    public const int MinMemoryKiB = 8 * 1024;
    public const int MaxMemoryKiB = 1024 * 1024; // 1 GiB
    public const int MaxIterations = 16;
    public const int MaxParallelism = 16;

    /// <summary>檢查參數；<paramref name="allowWeak"/> 只供測試使用較小記憶體。</summary>
    public void Validate(bool allowWeak = false)
    {
        int minMemory = allowWeak ? 8 * Math.Max(1, Parallelism) : MinMemoryKiB;
        if (Parallelism < 1 || Parallelism > MaxParallelism)
            throw new InvalidDataException($"Argon2 平行度 {Parallelism} 不合法");
        if (Iterations < 1 || Iterations > MaxIterations)
            throw new InvalidDataException($"Argon2 次數 {Iterations} 不合法");
        if (MemoryKiB < minMemory || MemoryKiB > MaxMemoryKiB || MemoryKiB < 8 * Parallelism)
            throw new InvalidDataException($"Argon2 記憶體 {MemoryKiB} KiB 不合法");
    }
}

/// <summary>
/// 被控端一組帳密。只存 Argon2id 雜湊、獨立隨機鹽與參數，不存明文密碼。
/// <see cref="Format"/> 用來辨識舊格式（PBKDF2）並要求重新設定。
/// </summary>
public sealed record CredentialRecord(
    int Format,
    string Username,
    string Algorithm,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    byte[] Salt,
    byte[] Hash)
{
    public const int CurrentFormat = 2;
    public const string Argon2id = "argon2id";

    public Argon2Parameters Parameters => new(MemoryKiB, Iterations, Parallelism);

    /// <summary>檢查格式與參數；不合法拋出 <see cref="InvalidDataException"/>。</summary>
    public void Validate(bool allowWeak = false)
    {
        if (Format != CurrentFormat || Algorithm != Argon2id)
            throw new CredentialFormatException();
        if (string.IsNullOrEmpty(Username))
            throw new InvalidDataException("帳號為空");
        if (Salt is null || Salt.Length < PasswordHasher.SaltSize || Salt.Length > 64)
            throw new InvalidDataException("鹽長度不合法");
        if (Hash is null || Hash.Length != PasswordHasher.HashSize)
            throw new InvalidDataException("雜湊長度不合法");
        Parameters.Validate(allowWeak);
    }
}

/// <summary>帳密檔為舊格式（例如 PBKDF2）或演算法不符，需重新執行 --setup。</summary>
public sealed class CredentialFormatException()
    : Exception("帳密檔為舊格式或不支援的演算法，請重新執行 Host.exe --setup 設定帳號密碼。");

public static class PasswordHasher
{
    /// <summary>密碼最短長度（PLAN §6）。</summary>
    public const int MinPasswordLength = 10;
    public const int SaltSize = 16;
    public const int HashSize = 32;

    public static CredentialRecord Create(string username, string password, Argon2Parameters? parameters = null,
        bool allowWeakParameters = false)
    {
        if (string.IsNullOrEmpty(username)) throw new ArgumentException("帳號不可空白", nameof(username));
        if (password is null || password.Length < MinPasswordLength)
            throw new ArgumentException($"密碼至少 {MinPasswordLength} 字元", nameof(password));

        var p = parameters ?? Argon2Parameters.Default;
        p.Validate(allowWeakParameters);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize); // 每組密碼獨立的隨機鹽
        return new CredentialRecord(CredentialRecord.CurrentFormat, username, CredentialRecord.Argon2id,
            p.MemoryKiB, p.Iterations, p.Parallelism, salt, Derive(password, salt, p));
    }

    /// <summary>
    /// 以固定時間比對帳號與密碼；帳號錯誤時仍計算雜湊，避免以回應時間猜帳號。
    /// 參數或格式不合法時拋出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static bool Verify(CredentialRecord record, string username, string password, bool allowWeakParameters = false)
    {
        record.Validate(allowWeakParameters);
        // Argon2 套件不接受空密碼；空密碼仍以替代值做完整運算（時間一致），結果一律為失敗。
        bool empty = string.IsNullOrEmpty(password);
        byte[] actual = Derive(empty ? "\0" : password!, record.Salt, record.Parameters);
        try
        {
            bool passwordOk = CryptographicOperations.FixedTimeEquals(actual, record.Hash) & !empty;
            bool userOk = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(username ?? string.Empty)),
                SHA256.HashData(Encoding.UTF8.GetBytes(record.Username)));
            return passwordOk & userOk;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static byte[] Derive(string password, byte[] salt, Argon2Parameters p)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = p.MemoryKiB,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon.GetBytes(HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes); // 不留明文密碼位元組
        }
    }
}

public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 讀取帳密檔；不存在回傳 null。舊格式拋出 <see cref="CredentialFormatException"/>，
    /// 內容或參數不合法拋出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static CredentialRecord? Load(string path)
    {
        if (!File.Exists(path)) return null;
        CredentialRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("帳密檔格式錯誤", ex);
        }
        if (record is null) throw new InvalidDataException("帳密檔為空");
        record.Validate();
        return record;
    }

    public static void Save(string path, CredentialRecord record)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }
}
