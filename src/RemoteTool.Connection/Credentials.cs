using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteTool.Connection;

/// <summary>被控端帳密。只存 PBKDF2-SHA256 雜湊與鹽，不存明文密碼。</summary>
public sealed record CredentialRecord(string Username, byte[] Salt, byte[] Hash, int Iterations);

public static class PasswordHasher
{
    public const int DefaultIterations = 310_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static CredentialRecord Create(string username, string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(username)) throw new ArgumentException("帳號不可空白", nameof(username));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("密碼不可空白", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new CredentialRecord(username, salt, Derive(password, salt, iterations), iterations);
    }

    /// <summary>以固定時間比對帳號與密碼；帳號錯誤時仍計算雜湊，避免以回應時間猜帳號。</summary>
    public static bool Verify(CredentialRecord record, string username, string password)
    {
        byte[] actual = Derive(password, record.Salt, record.Iterations);
        bool passwordOk = CryptographicOperations.FixedTimeEquals(actual, record.Hash);
        bool userOk = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(username)),
            SHA256.HashData(Encoding.UTF8.GetBytes(record.Username)));
        return passwordOk & userOk;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashSize);
}

public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CredentialRecord? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(path), JsonOptions);
    }

    public static void Save(string path, CredentialRecord record)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }
}
