using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteTool.Connection;

/// <summary>對某個位址所連到主機的信任判斷。</summary>
public enum HostTrust
{
    /// <summary>此公鑰已信任。</summary>
    Trusted,
    /// <summary>從未見過此公鑰，且此位址沒有紀錄：須請使用者核對。</summary>
    Unknown,
    /// <summary>此位址上次是另一把已信任的公鑰，這次換成未信任的公鑰：拒絕（PLAN §6：金鑰變了就拒絕）。</summary>
    Changed,
}

/// <summary>
/// 控制端的主機信任（PLAN §6、PLAN_GAPS G-06）：記住的是**主機公鑰（SPKI 指紋）**，不是 IP。
/// <list type="bullet">
/// <item>已信任的指紋不論從哪個位址連到都直接信任（IP 變動不影響信任）。</item>
/// <item>另記「位址 → 上次的指紋」作為提示：同一位址出現未信任的新金鑰時判定為 <see cref="HostTrust.Changed"/> 並拒絕。</item>
/// </list>
/// <paramref name="path"/> 為 null 時只存在記憶體（測試用）。檔案以 <see cref="SecretProtector"/> 保護（G-03）。
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string? _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Data _data;

    private sealed class Data
    {
        public int Version { get; set; } = 2;
        /// <summary>已信任的指紋 → 資訊。</summary>
        public Dictionary<string, TrustedHost> Trusted { get; set; } = new();
        /// <summary>位址:埠 → 上次在此位址看到的已信任指紋。</summary>
        public Dictionary<string, string> AddressHints { get; set; } = new();
    }

    public sealed class TrustedHost
    {
        public DateTime TrustedAtUtc { get; set; }
        public string FirstAddress { get; set; } = "";
    }

    public KnownHostsStore(string? path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector ?? SecretProtector.Default;
        _data = path is not null && File.Exists(path) ? Load(path) : new Data();
    }

    public static string KeyFor(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    /// <summary>判斷連到 <paramref name="hostKey"/> 看到 <paramref name="fingerprint"/> 時是否可信任。</summary>
    public HostTrust Evaluate(string hostKey, string fingerprint)
    {
        lock (_gate)
        {
            if (_data.Trusted.ContainsKey(fingerprint)) return HostTrust.Trusted;
            return _data.AddressHints.ContainsKey(hostKey) ? HostTrust.Changed : HostTrust.Unknown;
        }
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_gate) return _data.Trusted.ContainsKey(fingerprint);
    }

    /// <summary>此位址上次看到的指紋；沒有紀錄為 null。</summary>
    public string? HintFor(string hostKey)
    {
        lock (_gate) return _data.AddressHints.TryGetValue(hostKey, out var fp) ? fp : null;
    }

    /// <summary>使用者核對後信任此公鑰，並記錄位址提示。</summary>
    public void Trust(string hostKey, string fingerprint)
    {
        lock (_gate)
        {
            if (!_data.Trusted.ContainsKey(fingerprint))
                _data.Trusted[fingerprint] = new TrustedHost { TrustedAtUtc = DateTime.UtcNow, FirstAddress = hostKey };
            _data.AddressHints[hostKey] = fingerprint;
            Save();
        }
    }

    /// <summary>連線成功後更新位址提示（已信任的公鑰出現在新位址時）。</summary>
    public void RecordAddress(string hostKey, string fingerprint)
    {
        lock (_gate)
        {
            if (!_data.Trusted.ContainsKey(fingerprint)) return;
            if (_data.AddressHints.TryGetValue(hostKey, out var current) && current == fingerprint) return;
            _data.AddressHints[hostKey] = fingerprint;
            Save();
        }
    }

    /// <summary>
    /// 「移除此主機的指紋記錄」：移除此位址目前對應的公鑰信任，以及所有指向該公鑰的位址提示。
    /// 回傳是否有移除任何紀錄。
    /// </summary>
    public bool Forget(string hostKey)
    {
        lock (_gate)
        {
            if (!_data.AddressHints.TryGetValue(hostKey, out var fp)) return false;
            _data.Trusted.Remove(fp);
            foreach (var key in _data.AddressHints.Where(kv => kv.Value == fp).Select(kv => kv.Key).ToList())
                _data.AddressHints.Remove(key);
            Save();
            return true;
        }
    }

    private Data Load(string path)
    {
        string json = SecretFile.ReadText(path, _protector, out bool wasPlain);
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("known-hosts 格式錯誤");

        Data data;
        if (node.ContainsKey("Version"))
        {
            data = node.Deserialize<Data>(JsonOptions) ?? new Data();
        }
        else
        {
            // 舊格式（G-06 之前）：{ "位址:埠": "指紋" } —— 每個指紋轉為已信任公鑰並保留位址提示。
            data = new Data();
            foreach (var (key, value) in node)
            {
                if (value?.GetValue<string>() is not { } fp) continue;
                data.Trusted.TryAdd(fp, new TrustedHost { TrustedAtUtc = DateTime.UtcNow, FirstAddress = key });
                data.AddressHints[key] = fp;
            }
            wasPlain = true; // 轉存為新格式
        }

        _data = data;
        if (wasPlain) Save(); // 舊的明文檔一律改存為受保護格式
        return data;
    }

    private void Save()
    {
        if (_path is null) return;
        SecretFile.WriteText(_path, JsonSerializer.Serialize(_data, JsonOptions), _protector);
    }
}
