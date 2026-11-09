using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RemoteTool.Host.Core;
using RemoteTool.HostGuard.Core;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Tests.Integration;

internal sealed class FakeGuardChannel : IGuardChannel
{
    private readonly Lock _lock = new();
    private readonly List<GuardMessage> _messages = [];

    public bool IsConnected { get; set; } = true;
    public event Action? Faulted;

    public ValueTask<bool> SendAsync(GuardMessage message)
    {
        if (!IsConnected)
            return ValueTask.FromResult(false);
        lock (_lock)
            _messages.Add(message);
        return ValueTask.FromResult(true);
    }

    public IReadOnlyList<GuardMessage> Messages { get { lock (_lock) return _messages.ToList(); } }

    public void Fault()
    {
        IsConnected = false;
        Faulted?.Invoke();
    }
}

internal sealed class FakeDesktop : IDesktopStateSource
{
    public volatile bool Locked;
    public volatile bool InputDesktopIsDefault = true;
    public volatile bool OnConsole = true;

    public DesktopSnapshot GetSnapshot() => new(Locked, OnConsole, InputDesktopIsDefault);
    public event Action<DisconnectReason>? SessionEnding;
    public event Action<bool>? ConsoleActiveChanged;

    public void RaiseSessionEnding(DisconnectReason reason) => SessionEnding?.Invoke(reason);
    public void RaiseConsoleActive(bool active)
    {
        OnConsole = active;
        ConsoleActiveChanged?.Invoke(active);
    }
}

/// <summary>每 20 ms 產生一張畫面；要求關鍵幀時下一張為 IDR。</summary>
internal sealed class FakeVideoSource : IVideoSource
{
    private volatile bool _keyframeRequested = true;
    public volatile bool Healthy = true;
    public int KeyframeRequests;

    public bool IsCaptureHealthy => Healthy;

    public void RequestKeyframe()
    {
        Interlocked.Increment(ref KeyframeRequests);
        _keyframeRequested = true;
    }

    public async IAsyncEnumerable<EncodedFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long t = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20, ct); }
            catch (OperationCanceledException) { yield break; }
            bool key = _keyframeRequested;
            _keyframeRequested = false;
            yield return new EncodedFrame(key, t += 20_000, [key ? (byte)1 : (byte)0]);
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(because ?? "條件未在時限內成立");
            await Task.Delay(10);
        }
    }
}
