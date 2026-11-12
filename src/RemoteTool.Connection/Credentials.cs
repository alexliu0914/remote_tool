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

/// <summary>
/// 被控端的帳號集合（PLAN §6：可建立一或多組帳號，PLAN_GAPS G-04）。帳號名稱區分大小寫、不可重複。
/// </summary>
public sealed class CredentialSet
{
    private readonly List<CredentialRecord> _accounts;

    public CredentialSet(IEnumerable<CredentialRecord> accounts)
    {
        _accounts = accounts.ToList();
        var duplicate = _accounts.GroupBy(a => a.Username, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"帳號重複：{duplicate.Key}");
    }

    public static implicit operator CredentialSet(CredentialRecord single) => new([single]);

    public IReadOnlyList<CredentialRecord> Accounts => _accounts;
    public int Count => _accounts.Count;

    public bool Contains(string username) => _accounts.Any(a => a.Username == username);

    /// <summary>新增或取代同名帳號，回傳新的集合。</summary>
    public CredentialSet With(CredentialRecord record)
        => new(_accounts.Where(a => a.Username != record.Username).Append(record));

    /// <summary>移除帳號，回傳新的集合。</summary>
    public CredentialSet Without(string username) => new(_accounts.Where(a => a.Username != username));

    /// <summary>
    /// 驗證帳密。帳號不存在時仍以第一組帳號的參數完整計算一次雜湊（結果必為失敗），
    /// 讓「帳號存在但密碼錯」與「帳號不存在」的回應時間相同，避免以時間猜帳號。
    /// </summary>
    public bool Verify(string username, string password, bool allowWeakParameters = false)
    {
        if (_accounts.Count == 0) return false;
        var record = _accounts.FirstOrDefault(a => a.Username == username) ?? _accounts[0];
        return PasswordHasher.Verify(record, username, password, allowWeakParameters);
    }

    public void Validate(bool allowWeak = false)
    {
        foreach (var a in _accounts) a.Validate(allowWeak);
    }
}

/// <summary>帳密檔：<c>{ "Version": 1, "Accounts": [ ... ] }</c>，以 DPAPI（目前使用者）保護（G-03）。</summary>
public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<CredentialRecord> Accounts { get; set; } = new();
    }

    /// <summary>
    /// 讀取帳密檔；不存在回傳 null。舊格式（PBKDF2）拋出 <see cref="CredentialFormatException"/>，
    /// 內容、參數不合法或無法解密拋出 <see cref="InvalidDataException"/>。
    /// 舊的單一帳號明文檔會自動轉為新格式並加密保存。
    /// </summary>
    public static CredentialSet? Load(string path, ISecretProtector? protector = null)
    {
        protector ??= SecretProtector.Default;
        if (!File.Exists(path)) return null;

        string json = SecretFile.ReadText(path, protector, out bool needsRewrite);
        CredentialSet set;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
                       ?? throw new InvalidDataException("帳密檔為空");
            if (node.ContainsKey("Accounts"))
            {
                var model = node.Deserialize<FileModel>(JsonOptions) ?? throw new InvalidDataException("帳密檔為空");
                set = new CredentialSet(model.Accounts);
            }
            else
            {
                // G-04 之前的單一帳號格式
                var single = node.Deserialize<CredentialRecord>(JsonOptions) ?? throw new InvalidDataException("帳密檔為空");
                set = new CredentialSet([single]);
                needsRewrite = true;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("帳密檔格式錯誤", ex);
        }

        set.Validate();
        if (needsRewrite) Save(path, set, protector);
        return set;
    }

    public static void Save(string path, CredentialSet set, ISecretProtector? protector = null)
    {
        var model = new FileModel { Accounts = set.Accounts.ToList() };
        SecretFile.WriteText(path, JsonSerializer.Serialize(model, JsonOptions), protector ?? SecretProtector.Default);
    }
}
