using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteTool.Controller.Core;

public enum HostTrust
{
    /// <summary>此公鑰已核准。</summary>
    Trusted,

    /// <summary>第一次看到此位址與公鑰：須人工核對指紋（SEC-01）。</summary>
    FirstSeen,

    /// <summary>此位址先前核准的是另一把公鑰：直接拒絕（SEC-02）。</summary>
    KeyChanged,
}

public sealed class KnownHost
{
    /// <summary>SHA-256(SPKI)，16 進位。</summary>
    public required string Fingerprint { get; init; }
    public List<string> Addresses { get; init; } = [];
    public DateTimeOffset ApprovedAt { get; init; }
}

[JsonSerializable(typeof(List<KnownHost>))]
internal sealed partial class KnownHostJsonContext : JsonSerializerContext;

/// <summary>
/// 控制端記住的主機（第六節）：記住的是主機公鑰（裝置身分），不是 IP。
/// 同一公鑰換 IP 仍信任；同一位址出現不同公鑰則拒絕。
/// </summary>
public sealed class KnownHostStore
{
    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly List<KnownHost> _hosts;

    public KnownHostStore(string? path = null)
    {
        _path = path;
        _hosts = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(path), KnownHostJsonContext.Default.ListKnownHost) ?? []
            : [];
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteTool", "known_hosts.json");

    public IReadOnlyList<KnownHost> Hosts
    {
        get { lock (_lock) return _hosts.ToList(); }
    }

    public HostTrust Check(string address, ReadOnlySpan<byte> fingerprint)
    {
        var hex = Convert.ToHexString(fingerprint);
        var addr = NormalizeAddress(address);
        lock (_lock)
        {
            if (_hosts.Any(h => FixedEquals(h.Fingerprint, hex)))
                return HostTrust.Trusted;
            if (_hosts.Any(h => h.Addresses.Contains(addr)))
                return HostTrust.KeyChanged;
            return HostTrust.FirstSeen;
        }
    }

    /// <summary>使用者核對指紋後核准。若公鑰已知，只加入新位址。</summary>
    public void Approve(string address, ReadOnlySpan<byte> fingerprint, DateTimeOffset? now = null)
    {
        var hex = Convert.ToHexString(fingerprint);
        var addr = NormalizeAddress(address);
        lock (_lock)
        {
            // 位址只屬於一把公鑰
            foreach (var h in _hosts)
                h.Addresses.Remove(addr);
            var existing = _hosts.FirstOrDefault(h => FixedEquals(h.Fingerprint, hex));
            if (existing is null)
                _hosts.Add(new KnownHost { Fingerprint = hex, Addresses = [addr], ApprovedAt = now ?? DateTimeOffset.UtcNow });
            else if (!existing.Addresses.Contains(addr))
                existing.Addresses.Add(addr);
            SaveLocked();
        }
    }

    /// <summary>已核准的公鑰換了位址：記錄新位址（不改變信任）。</summary>
    public void RememberAddress(string address, ReadOnlySpan<byte> fingerprint)
    {
        var hex = Convert.ToHexString(fingerprint);
        var addr = NormalizeAddress(address);
        lock (_lock)
        {
            var existing = _hosts.FirstOrDefault(h => FixedEquals(h.Fingerprint, hex));
            if (existing is null || existing.Addresses.Contains(addr))
                return;
            foreach (var h in _hosts)
                h.Addresses.Remove(addr);
            existing.Addresses.Add(addr);
            SaveLocked();
        }
    }

    /// <summary>移除某位址的信任記錄（金鑰變更後人工重新核准的第一步）。</summary>
    public bool ForgetAddress(string address)
    {
        var addr = NormalizeAddress(address);
        lock (_lock)
        {
            bool removed = false;
            foreach (var h in _hosts)
                removed |= h.Addresses.Remove(addr);
            _hosts.RemoveAll(h => h.Addresses.Count == 0);
            SaveLocked();
            return removed;
        }
    }

    private void SaveLocked()
    {
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_hosts, KnownHostJsonContext.Default.ListKnownHost));
        File.Move(tmp, _path, overwrite: true);
    }

    private static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
}
