using System.Net;
using RemoteTool.HostGuard.Core;
using RemoteTool.Protocol;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Host.Core;

/// <summary>
/// 一名控制者的工作階段：Control 連線 + 可獨立重連的 Video、File 連線（第七節）。
/// <list type="bullet">
/// <item>Control 中斷：撤銷整個工作階段（關閉 Video、File，Token 失效，釋放按鍵）。</item>
/// <item>Video 中斷：暫停（釋放按鍵並遞增世代），重連後從 IDR 開始並依恢復程序接受輸入。</item>
/// <item>File 中斷：只影響檔案傳輸。</item>
/// </list>
/// </summary>
public sealed class HostSession
{
    private readonly HostServer _server;
    private readonly HostServerOptions _options;
    private readonly SessionTokenService _tokens;
    private readonly IGuardChannel _guard;
    private readonly IDesktopStateSource _desktop;
    private readonly IVideoSource _video;
    private readonly IHostLog _log;
    private readonly TimeProvider _time;
    private readonly ControlGate _gate;

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<DisconnectReason> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FramedConnection? _control;
    private FramedConnection? _videoConn;
    private FramedConnection? _fileConn;
    private long _controlLastHeard;
    private long _videoLastHeard;
    private long _fileLastHeard;
    private bool _awaitingKeyframe;
    private int _ended;
    private long _droppedInput;

    internal HostSession(HostServer server, string account, IPAddress remoteAddress, long controlConnectionId,
        HostServerOptions options, SessionTokenService tokens, IGuardChannel guard, IDesktopStateSource desktop,
        IVideoSource video, IHostLog log, TimeProvider time)
    {
        _server = server;
        Account = account;
        RemoteAddress = remoteAddress;
        ControlConnectionId = controlConnectionId;
        _options = options;
        _tokens = tokens;
        _guard = guard;
        _desktop = desktop;
        _video = video;
        _log = log;
        _time = time;
        _gate = new ControlGate(time, options.ResumeStableDuration);
    }

    public string Account { get; }
    public IPAddress RemoteAddress { get; }
    public long ControlConnectionId { get; }
    public bool IsEnded => Volatile.Read(ref _ended) != 0;

    /// <summary>結束時完成，結果為結束原因。</summary>
    public Task<DisconnectReason> Completion => _completion.Task;

    public uint Generation { get { lock (_sync) return _gate.Generation; } }
    public GateState State { get { lock (_sync) return _gate.State; } }
    public bool HasVideo { get { lock (_sync) return _videoConn is not null; } }
    public bool HasFile { get { lock (_sync) return _fileConn is not null; } }

    /// <summary>因世代不符或暫停而丟棄的鍵鼠事件數。</summary>
    public long DroppedInputCount => Interlocked.Read(ref _droppedInput);

    // ---------------- Control ----------------

    internal async Task RunControlAsync(FramedConnection control)
    {
        lock (_sync)
        {
            _control = control;
            _controlLastHeard = _time.GetTimestamp();
        }
        // 登入後 HostGuard 先進入暫停（新世代），確保不會沿用任何舊狀態
        await _guard.SendAsync(GuardMessage.Pause(Generation)).ConfigureAwait(false);

        var evaluate = EvaluateLoopAsync(_cts.Token);
        var pump = VideoPumpAsync(_cts.Token);
        var reason = DisconnectReason.Normal;
        try
        {
            while (true)
            {
                var frame = await control.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Volatile.Write(ref _controlLastHeard, _time.GetTimestamp());
                if (!await HandleControlFrameAsync(control, frame.Value).ConfigureAwait(false))
                    break;
            }
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"Control 協定錯誤：{ex.Message}");
            reason = DisconnectReason.ProtocolError;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await EndAsync(reason).ConfigureAwait(false);
        await Task.WhenAll(Swallow(evaluate), Swallow(pump)).ConfigureAwait(false);
    }

    private async Task<bool> HandleControlFrameAsync(FramedConnection control, Frame frame)
    {
        switch (frame.Type)
        {
            case MessageType.Heartbeat:
            {
                var hb = MessageSerializer.Read<Heartbeat>(frame);
                await control.SendAsync(new HeartbeatAck(hb.TimestampMs), frame.RequestId, _cts.Token).ConfigureAwait(false);
                return true;
            }
            case MessageType.SessionTokenRequest:
            {
                var req = MessageSerializer.Read<SessionTokenRequest>(frame);
                var token = _tokens.Issue(new TokenBinding(Account, _options.WindowsSessionId, ControlConnectionId, req.Role));
                await control.SendAsync(new SessionTokenGrant(req.Role, token, (int)_tokens.Lifetime.TotalMilliseconds),
                    frame.RequestId, _cts.Token).ConfigureAwait(false);
                return true;
            }
            case MessageType.ResumeAck:
            {
                var ack = MessageSerializer.Read<ResumeAck>(frame);
                bool resumed;
                lock (_sync)
                    resumed = _gate.OnResumeAck(ack.Generation);
                if (resumed)
                {
                    await _guard.SendAsync(GuardMessage.Resume(ack.Generation)).ConfigureAwait(false);
                    _log.Info($"已恢復遠端控制，世代 {ack.Generation}");
                }
                return true;
            }
            case MessageType.KeyEvent:
            {
                var e = MessageSerializer.Read<KeyEvent>(frame);
                await ForwardInputAsync(e.Generation, GuardMessage.Key(e.Generation, e.ScanCode, e.Extended, e.Down)).ConfigureAwait(false);
                return true;
            }
            case MessageType.MouseMoveEvent:
            {
                var e = MessageSerializer.Read<MouseMoveEvent>(frame);
                await ForwardInputAsync(e.Generation, GuardMessage.MouseMove(e.Generation, e.X, e.Y)).ConfigureAwait(false);
                return true;
            }
            case MessageType.MouseButtonEvent:
            {
                var e = MessageSerializer.Read<MouseButtonEvent>(frame);
                await ForwardInputAsync(e.Generation,
                    GuardMessage.MouseButtonEvent(e.Generation, (GuardMouseButton)e.Button, e.Down)).ConfigureAwait(false);
                return true;
            }
            case MessageType.MouseWheelEvent:
            {
                var e = MessageSerializer.Read<MouseWheelEvent>(frame);
                await ForwardInputAsync(e.Generation, GuardMessage.Wheel(e.Generation, e.Delta, e.Horizontal)).ConfigureAwait(false);
                return true;
            }
            case MessageType.UnicodeTextEvent:
            {
                var e = MessageSerializer.Read<UnicodeTextEvent>(frame);
                await ForwardInputAsync(e.Generation, GuardMessage.Unicode(e.Generation, e.Text)).ConfigureAwait(false);
                return true;
            }
            case MessageType.InputHeartbeat:
            {
                var e = MessageSerializer.Read<InputHeartbeat>(frame);
                await ForwardInputAsync(e.Generation, GuardMessage.Heartbeat(e.Generation)).ConfigureAwait(false);
                return true;
            }
            case MessageType.ReleaseAllInput:
            {
                var e = MessageSerializer.Read<ReleaseAllInput>(frame);
                await _guard.SendAsync(GuardMessage.ReleaseAll(e.Generation)).ConfigureAwait(false);
                return true;
            }
            case MessageType.Disconnect:
                MessageSerializer.Read<Disconnect>(frame);
                return false;
            default:
                throw new InvalidDataException($"Control 連線不接受 {frame.Type}");
        }
    }

    private async Task ForwardInputAsync(uint generation, GuardMessage message)
    {
        bool accept;
        lock (_sync)
            accept = _gate.AcceptsInput(generation);
        if (!accept)
        {
            Interlocked.Increment(ref _droppedInput);
            return;
        }
        await _guard.SendAsync(message).ConfigureAwait(false);
    }

    // ---------------- Video ----------------

    internal async Task RunVideoAsync(FramedConnection conn)
    {
        FramedConnection? old;
        lock (_sync)
        {
            if (IsEnded)
                return;
            old = _videoConn;
            _videoConn = conn;
            _videoLastHeard = _time.GetTimestamp();
        }
        if (old is not null)
            await CloseReplacedAsync(old).ConfigureAwait(false);
        await EvaluateNowAsync().ConfigureAwait(false);

        try
        {
            while (true)
            {
                var frame = await conn.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Volatile.Write(ref _videoLastHeard, _time.GetTimestamp());
                switch (frame.Value.Type)
                {
                    case MessageType.Heartbeat:
                        var hb = MessageSerializer.Read<Heartbeat>(frame.Value);
                        await conn.SendAsync(new HeartbeatAck(hb.TimestampMs), frame.Value.RequestId, _cts.Token).ConfigureAwait(false);
                        break;
                    case MessageType.KeyframeRequest:
                        MessageSerializer.Read<KeyframeRequest>(frame.Value);
                        _video.RequestKeyframe();
                        break;
                    default:
                        throw new InvalidDataException($"Video 連線不接受 {frame.Value.Type}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            _log.Info($"Video 連線結束：{ex.Message}");
        }
        finally
        {
            bool wasCurrent;
            lock (_sync)
            {
                wasCurrent = ReferenceEquals(_videoConn, conn);
                if (wasCurrent)
                    _videoConn = null;
            }
            if (wasCurrent && !IsEnded)
                await EvaluateNowAsync().ConfigureAwait(false); // → 暫停（VideoLost）
        }
    }

    /// <summary>單一讀取者持續取畫面，送到目前的 Video 連線；暫停時丟棄，恢復時從 IDR 開始。</summary>
    private async Task VideoPumpAsync(CancellationToken ct)
    {
        await foreach (var f in _video.ReadFramesAsync(ct).ConfigureAwait(false))
        {
            FramedConnection? target;
            uint generation;
            lock (_sync)
            {
                target = _videoConn;
                if (target is null || _gate.State == GateState.Paused)
                    continue;
                if (_awaitingKeyframe)
                {
                    if (!f.IsKeyframe)
                        continue;
                    _awaitingKeyframe = false;
                }
                generation = _gate.Generation;
            }
            try
            {
                await target.SendAsync(new VideoFrame(generation, f.IsKeyframe, f.TimestampUs, f.Data), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Video 讀取迴圈會處理斷線
            }
        }
    }

    // ---------------- File（M3 實作傳輸引擎） ----------------

    internal async Task RunFileAsync(FramedConnection conn)
    {
        FramedConnection? old;
        lock (_sync)
        {
            if (IsEnded)
                return;
            old = _fileConn;
            _fileConn = conn;
            _fileLastHeard = _time.GetTimestamp();
        }
        if (old is not null)
            await CloseReplacedAsync(old).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var frame = await conn.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Volatile.Write(ref _fileLastHeard, _time.GetTimestamp());
                if (frame.Value.Type == MessageType.Heartbeat)
                {
                    var hb = MessageSerializer.Read<Heartbeat>(frame.Value);
                    await conn.SendAsync(new HeartbeatAck(hb.TimestampMs), frame.Value.RequestId, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                await conn.SendAsync(new ErrorMessage(501, "檔案傳輸尚未實作（M3）"), frame.Value.RequestId, _cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            _log.Info($"File 連線結束：{ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_fileConn, conn))
                    _fileConn = null;
            }
        }
    }

    private static async Task CloseReplacedAsync(FramedConnection old)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await old.SendAsync(new Disconnect(DisconnectReason.Replaced), cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        await old.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------- 可控制判定與心跳 ----------------

    private async Task EvaluateLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.EvaluateInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (CheckHeartbeats())
                return;
            await EvaluateNowAsync().ConfigureAwait(false);
        }
    }

    /// <summary>檢查三條連線的心跳；Control 逾時回傳 true（工作階段已結束）。</summary>
    private bool CheckHeartbeats()
    {
        var timeout = _options.HeartbeatTimeout;
        if (_time.GetElapsedTime(Volatile.Read(ref _controlLastHeard)) > timeout)
        {
            _log.Warn("Control 心跳逾時");
            _ = EndAsync(DisconnectReason.HeartbeatTimeout);
            return true;
        }

        FramedConnection? staleVideo = null, staleFile = null;
        lock (_sync)
        {
            if (_videoConn is not null && _time.GetElapsedTime(Volatile.Read(ref _videoLastHeard)) > timeout)
                staleVideo = _videoConn;
            if (_fileConn is not null && _time.GetElapsedTime(Volatile.Read(ref _fileLastHeard)) > timeout)
                staleFile = _fileConn;
        }
        // 關閉後由各自的讀取迴圈清理；Video 會觸發暫停
        if (staleVideo is not null)
            _ = staleVideo.DisposeAsync().AsTask();
        if (staleFile is not null)
            _ = staleFile.DisposeAsync().AsTask();
        return false;
    }

    internal async Task EvaluateNowAsync()
    {
        if (IsEnded)
            return;
        await _evalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsEnded)
                return;
            var snapshot = _desktop.GetSnapshot();
            GateAction action;
            FramedConnection? control;
            lock (_sync)
            {
                action = _gate.Evaluate(new GateConditions(snapshot, _video.IsCaptureHealthy, _videoConn is not null));
                if (action.Kind == GateActionKind.BeginResume)
                    _awaitingKeyframe = true;
                control = _control;
            }

            switch (action.Kind)
            {
                case GateActionKind.Pause:
                    // 先讓 HostGuard 停止注入並釋放，再通知控制端
                    await _guard.SendAsync(GuardMessage.Pause(action.Generation)).ConfigureAwait(false);
                    _log.Info($"暫停遠端控制：{action.Reason}，世代 {action.Generation}");
                    if (control is not null)
                        await TrySendAsync(control, new PauseControl(action.Generation, action.Reason)).ConfigureAwait(false);
                    break;
                case GateActionKind.BeginResume:
                    _log.Info($"開始恢復，世代 {action.Generation}");
                    if (control is not null)
                        await TrySendAsync(control, new ResumeControl(action.Generation)).ConfigureAwait(false);
                    _video.RequestKeyframe();
                    break;
            }
        }
        finally
        {
            _evalLock.Release();
        }
    }

    // ---------------- 結束 ----------------

    /// <summary>結束工作階段：撤銷 Token、釋放按鍵、通知控制端並關閉三條連線。可重複呼叫。</summary>
    public async Task EndAsync(DisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        _tokens.RevokeAll(ControlConnectionId);

        FramedConnection? control, video, file;
        uint nextGeneration;
        lock (_sync)
        {
            control = _control;
            video = _videoConn;
            file = _fileConn;
            _videoConn = _fileConn = null;
            nextGeneration = _gate.Generation + 1;
        }

        // 被控端負責釋放：讓 HostGuard 暫停（丟棄舊世代）並放開所有遠端按下的鍵
        await _guard.SendAsync(GuardMessage.Pause(nextGeneration)).ConfigureAwait(false);

        if (control is not null && reason != DisconnectReason.Normal)
            await TrySendAsync(control, new Disconnect(reason)).ConfigureAwait(false);

        _cts.Cancel();
        foreach (var c in new[] { video, file, control })
        {
            if (c is not null)
                await c.DisposeAsync().ConfigureAwait(false);
        }

        _server.OnSessionEnded(this, reason);
        _completion.TrySetResult(reason);
    }

    private static async Task TrySendAsync<T>(FramedConnection conn, T message) where T : notnull
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await conn.SendAsync(message, cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
