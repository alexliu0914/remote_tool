using System.Threading.Channels;

namespace RemoteTool.HostGuard.Core;

/// <summary>
/// 單一執行緒執行 <see cref="InjectionEngine"/>：IPC 訊息、定時檢查、Host 結束都排進同一個佇列。
/// 每則 IPC 訊息帶有 Host 實例編號；Host 結束後，該實例尚在佇列中的事件一律丟棄
/// （第三節：「Host.exe 結束：立即丟棄佇列中的事件、釋放狀態表中的鍵與按鈕」）。
/// </summary>
public sealed class GuardLoop : IAsyncDisposable
{
    private readonly InjectionEngine _engine;
    private readonly IGuardLog _log;
    private readonly TimeSpan _tickInterval;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly ITimer _ticker;
    private long _instanceCounter;
    private long _activeInstance; // 0 = 目前沒有可接受訊息的 Host

    private readonly record struct Item(Kind Kind, long Instance, GuardMessage Message, TaskCompletionSource? Flush = null);
    private enum Kind { Message, Tick, HostGone, Flush }

    public GuardLoop(InjectionEngine engine, IGuardLog? log = null, TimeSpan? tickInterval = null, TimeProvider? time = null)
    {
        _engine = engine;
        _log = log ?? NullGuardLog.Instance;
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(100);
        _thread = new Thread(Run) { IsBackground = true, Name = "HostGuard.Injection" };
        _thread.Start();
        _ticker = (time ?? TimeProvider.System).CreateTimer(
            _ => _queue.Writer.TryWrite(new Item(Kind.Tick, 0, default)), null, _tickInterval, _tickInterval);
    }

    /// <summary>目前接受訊息的 Host 實例編號。</summary>
    public long ActiveInstance => Interlocked.Read(ref _activeInstance);

    /// <summary>新的 Host 實例開始；回傳其編號，IPC 收到的訊息以此標記。</summary>
    public long BeginHostInstance()
    {
        long id = Interlocked.Increment(ref _instanceCounter);
        Interlocked.Exchange(ref _activeInstance, id);
        return id;
    }

    /// <summary>把 IPC 收到的訊息排入佇列。</summary>
    public void Post(long instance, in GuardMessage message)
        => _queue.Writer.TryWrite(new Item(Kind.Message, instance, message));

    /// <summary>
    /// Host 實例結束或 IPC 中斷。立即使該實例的排隊事件失效，並排入釋放動作。
    /// </summary>
    public void HostGone(long instance)
    {
        // 讓該實例的訊息立刻失效（即使排在 HostGone 之前也會被丟棄）
        Interlocked.CompareExchange(ref _activeInstance, 0, instance);
        _queue.Writer.TryWrite(new Item(Kind.HostGone, instance, default));
    }

    /// <summary>等待目前排入的項目都處理完（測試用）。</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Item(Kind.Flush, 0, default, tcs)))
            tcs.TrySetResult();
        return tcs.Task;
    }

    private void Run()
    {
        var reader = _queue.Reader;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.TryRead(out var item))
                {
                    var wait = reader.WaitToReadAsync(_cts.Token).AsTask();
                    if (!wait.GetAwaiter().GetResult())
                        break;
                    continue;
                }

                switch (item.Kind)
                {
                    case Kind.Tick:
                        _engine.Tick();
                        break;
                    case Kind.HostGone:
                        _engine.OnHostGone();
                        break;
                    case Kind.Flush:
                        item.Flush!.TrySetResult();
                        break;
                    case Kind.Message:
                        if (item.Instance == 0 || item.Instance != Interlocked.Read(ref _activeInstance))
                        {
                            _log.Info($"丟棄已結束 Host 實例的事件 {item.Message}");
                            break;
                        }
                        _engine.Handle(item.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"注入執行緒例外：{ex}");
            throw; // 讓 HostGuard 崩潰並由排程工作重啟，而不是在未知狀態下繼續注入
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync().ConfigureAwait(false);
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
