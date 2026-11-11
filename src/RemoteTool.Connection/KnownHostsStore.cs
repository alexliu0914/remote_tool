using System.Text.Json;

namespace RemoteTool.Connection;

/// <summary>
/// 控制端已信任的主機指紋，以「位址:埠」為鍵存成 JSON。
/// <paramref name="path"/> 為 null 時只存在記憶體（測試用）。
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string? _path;
    private readonly Dictionary<string, string> _entries;
    private readonly object _gate = new();

    public KnownHostsStore(string? path)
    {
        _path = path;
        _entries = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions) ?? new()
            : new();
    }

    public static string KeyFor(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public string? Get(string key)
    {
        lock (_gate) return _entries.TryGetValue(key, out var fp) ? fp : null;
    }

    public void Set(string key, string fingerprint)
    {
        lock (_gate) { _entries[key] = fingerprint; Save(); }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            bool removed = _entries.Remove(key);
            if (removed) Save();
            return removed;
        }
    }

    private void Save()
    {
        if (_path is null) return;
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
    }
}
