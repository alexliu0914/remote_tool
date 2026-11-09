using RemoteTool.Protocol;

namespace RemoteTool.Host.Core;

/// <summary>被控端自訂帳號（第六節）。名稱不分大小寫；密碼至少 10 字元。</summary>
public sealed class AccountStore
{
    private readonly HostSettings _settings;
    private readonly PasswordHasher _hasher;
    private readonly Lock _lock = new();
    private readonly string _dummyHash;

    public AccountStore(HostSettings settings, PasswordHasher hasher)
    {
        _settings = settings;
        _hasher = hasher;
        // 帳號不存在時也做一次雜湊，避免以回應時間探測帳號是否存在
        _dummyHash = hasher.Hash(Guid.NewGuid().ToString("N"));
    }

    public IReadOnlyList<string> Usernames
    {
        get { lock (_lock) return _settings.Accounts.Select(a => a.Username).ToList(); }
    }

    /// <summary>新增或更新帳號密碼。呼叫端負責儲存設定。</summary>
    public void SetPassword(string username, string password)
    {
        ValidateUsername(username);
        if (password.Length < ProtocolConstants.MinPasswordLength)
            throw new ArgumentException($"密碼至少 {ProtocolConstants.MinPasswordLength} 字元", nameof(password));
        if (password.Length > ProtocolConstants.MaxPasswordLength)
            throw new ArgumentException("密碼過長", nameof(password));

        var hash = _hasher.Hash(password);
        lock (_lock)
        {
            var existing = Find(username);
            if (existing is null)
                _settings.Accounts.Add(new AccountRecord { Username = username, PasswordHash = hash });
            else
                existing.PasswordHash = hash;
        }
    }

    public bool Remove(string username)
    {
        lock (_lock)
            return _settings.Accounts.RemoveAll(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>驗證帳密。成功時回傳正規化的帳號名稱。</summary>
    public string? Verify(string username, string password)
    {
        AccountRecord? record;
        lock (_lock)
            record = Find(username);

        if (record is null)
        {
            _hasher.Verify(password, _dummyHash);
            return null;
        }
        return _hasher.Verify(password, record.PasswordHash) ? record.Username : null;
    }

    private AccountRecord? Find(string username)
        => _settings.Accounts.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > ProtocolConstants.MaxUsernameLength)
            throw new ArgumentException("帳號名稱不合法", nameof(username));
        if (username.Any(char.IsControl))
            throw new ArgumentException("帳號名稱不可含控制字元", nameof(username));
    }
}
