using System.Net;

namespace RemoteTool.Host.Core;

/// <summary>
/// 防暴力破解（第六節）：每個來源漸進延遲（1、2、4…秒，上限 60 秒）+ 全域節流。
/// 不採「鎖 15 分鐘」，避免被惡意鎖死無法登入。
/// </summary>
public sealed class LoginThrottle
{
    private readonly TimeProvider _time;
    private readonly int _globalAttemptsPerSecond;
    private readonly int _maxTrackedSources;
    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];
    private readonly Queue<DateTimeOffset> _globalAttempts = new();

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset NextAllowed;
        public DateTimeOffset LastSeen;
    }

    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public LoginThrottle(TimeProvider? time = null, int globalAttemptsPerSecond = 10, int maxTrackedSources = 10_000)
    {
        _time = time ?? TimeProvider.System;
        _globalAttemptsPerSecond = globalAttemptsPerSecond;
        _maxTrackedSources = maxTrackedSources;
    }

    /// <summary>
    /// 是否允許此來源現在嘗試登入。允許時回傳 <see cref="TimeSpan.Zero"/> 並計入全域次數；
    /// 否則回傳需等待的時間。
    /// </summary>
    public TimeSpan TryBeginAttempt(IPAddress source)
    {
        var now = _time.GetUtcNow();
        source = Normalize(source);
        lock (_lock)
        {
            if (_entries.TryGetValue(source, out var e) && e.NextAllowed > now)
                return e.NextAllowed - now;

            while (_globalAttempts.Count > 0 && now - _globalAttempts.Peek() >= TimeSpan.FromSeconds(1))
                _globalAttempts.Dequeue();
            if (_globalAttempts.Count >= _globalAttemptsPerSecond)
                return _globalAttempts.Peek() + TimeSpan.FromSeconds(1) - now;

            _globalAttempts.Enqueue(now);
            return TimeSpan.Zero;
        }
    }

    public void RecordFailure(IPAddress source)
    {
        var now = _time.GetUtcNow();
        source = Normalize(source);
        lock (_lock)
        {
            if (!_entries.TryGetValue(source, out var e))
            {
                EvictIfFull(now);
                e = new Entry();
                _entries[source] = e;
            }
            e.Failures++;
            double seconds = Math.Min(Math.Pow(2, Math.Min(e.Failures - 1, 30)), MaxDelay.TotalSeconds);
            e.NextAllowed = now + TimeSpan.FromSeconds(seconds);
            e.LastSeen = now;
        }
    }

    public void RecordSuccess(IPAddress source)
    {
        lock (_lock)
            _entries.Remove(Normalize(source));
    }

    private void EvictIfFull(DateTimeOffset now)
    {
        if (_entries.Count < _maxTrackedSources)
            return;
        // 先移除已過延遲且一小時未見的來源，仍滿則移除最久未見者
        foreach (var key in _entries.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
        if (_entries.Count >= _maxTrackedSources)
            _entries.Remove(_entries.MinBy(kv => kv.Value.LastSeen).Key);
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
