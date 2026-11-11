using System.Net;

namespace RemoteTool.Connection;

/// <summary>
/// 依來源 IP 限制登入失敗次數：時間窗內失敗達上限即暫時拒絕，不再比對密碼。
/// </summary>
public sealed class LoginThrottle
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<IPAddress, Queue<DateTime>> _failures = new();
    private readonly object _gate = new();

    public LoginThrottle(int maxFailures = 5, TimeSpan? window = null, Func<DateTime>? clock = null)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool IsBlocked(IPAddress address)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(Normalize(address), out var q)) return false;
            Prune(q);
            return q.Count >= _maxFailures;
        }
    }

    public void RecordFailure(IPAddress address)
    {
        lock (_gate)
        {
            var key = Normalize(address);
            if (!_failures.TryGetValue(key, out var q)) _failures[key] = q = new Queue<DateTime>();
            Prune(q);
            q.Enqueue(_clock());
        }
    }

    public void Reset(IPAddress address)
    {
        lock (_gate) _failures.Remove(Normalize(address));
    }

    private void Prune(Queue<DateTime> q)
    {
        var cutoff = _clock() - _window;
        while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
