using System.Net;

namespace RemoteTool.Connection;

/// <summary>
/// 登入節流（PLAN §6、PLAN_GAPS G-01）：
/// <list type="bullet">
/// <item>每個來源<b>漸進延遲</b>：第 n 次失敗後，該來源須等 1、2、4…秒（上限 60 秒）才能再嘗試；
///   等待期間的嘗試直接拒絕、不比對密碼、也不延長等待。<b>不採鎖定</b>，避免被惡意鎖死。</item>
/// <item><b>全域節流</b>：所有來源合計的密碼比對次數以 token bucket 限制（Argon2id 每次約耗 19 MiB 記憶體）。</item>
/// </list>
/// 同時連線上限由 <see cref="HostServer"/> 負責。
/// </summary>
public sealed class LoginThrottle
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _forgetAfter;
    private readonly double _globalCapacity;
    private readonly double _globalPerSecond;
    private readonly int _maxTrackedSources;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<IPAddress, SourceState> _sources = new();
    private readonly object _gate = new();
    private double _globalTokens;
    private DateTime _globalUpdated;

    private sealed class SourceState
    {
        public int Failures;
        public DateTime LastFailure;
        public DateTime NextAllowed;
    }

    public LoginThrottle(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        TimeSpan? forgetAfter = null,
        int globalBurst = 10,
        double globalPerSecond = 2,
        int maxTrackedSources = 10_000,
        Func<DateTime>? clock = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        _forgetAfter = forgetAfter ?? TimeSpan.FromMinutes(15);
        _globalCapacity = globalBurst;
        _globalPerSecond = globalPerSecond;
        _maxTrackedSources = maxTrackedSources;
        _clock = clock ?? (() => DateTime.UtcNow);
        _globalTokens = globalBurst;
        _globalUpdated = _clock();
    }

    /// <summary>
    /// 這個來源現在可以比對密碼嗎？不行時 <paramref name="retryAfter"/> 為建議等待時間。
    /// 允許時會扣掉一個全域額度。
    /// </summary>
    public bool TryBegin(IPAddress address, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _clock();
            var key = Normalize(address);

            if (_sources.TryGetValue(key, out var state))
            {
                if (now - state.LastFailure > _forgetAfter)
                {
                    _sources.Remove(key);
                }
                else if (now < state.NextAllowed)
                {
                    retryAfter = state.NextAllowed - now;
                    return false;
                }
            }

            RefillGlobal(now);
            if (_globalTokens < 1)
            {
                retryAfter = TimeSpan.FromSeconds((1 - _globalTokens) / _globalPerSecond);
                return false;
            }
            _globalTokens -= 1;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void RecordFailure(IPAddress address)
    {
        lock (_gate)
        {
            var now = _clock();
            var key = Normalize(address);
            if (!_sources.TryGetValue(key, out var state))
            {
                if (_sources.Count >= _maxTrackedSources) PruneOldest();
                _sources[key] = state = new SourceState();
            }
            state.Failures++;
            state.LastFailure = now;
            double seconds = Math.Min(_maxDelay.TotalSeconds,
                _baseDelay.TotalSeconds * Math.Pow(2, Math.Min(30, state.Failures - 1)));
            state.NextAllowed = now + TimeSpan.FromSeconds(seconds);
        }
    }

    public void RecordSuccess(IPAddress address)
    {
        lock (_gate) _sources.Remove(Normalize(address));
    }

    /// <summary>目前此來源的失敗次數（測試與記錄用）。</summary>
    public int FailuresFor(IPAddress address)
    {
        lock (_gate) return _sources.TryGetValue(Normalize(address), out var s) ? s.Failures : 0;
    }

    private void RefillGlobal(DateTime now)
    {
        double elapsed = Math.Max(0, (now - _globalUpdated).TotalSeconds);
        _globalTokens = Math.Min(_globalCapacity, _globalTokens + elapsed * _globalPerSecond);
        _globalUpdated = now;
    }

    private void PruneOldest()
    {
        var cutoff = _clock() - _forgetAfter;
        foreach (var k in _sources.Where(kv => kv.Value.LastFailure < cutoff).Select(kv => kv.Key).ToList())
            _sources.Remove(k);
        if (_sources.Count >= _maxTrackedSources)
        {
            var oldest = _sources.MinBy(kv => kv.Value.LastFailure).Key;
            _sources.Remove(oldest);
        }
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
