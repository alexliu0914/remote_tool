namespace RemoteTool.HostGuard.Core;

/// <summary>
/// HostGuard 的注入引擎與按鍵狀態表（第三節「注入與狀態」）。
/// <para>
/// 所有方法必須在同一個執行緒呼叫（由 <see cref="GuardLoop"/> 保證），
/// 注入與狀態表在同一處更新，Host.exe 崩潰不會讓狀態失準。
/// </para>
/// <list type="number">
/// <item>收到鍵鼠事件先檢查世代編號、暫停狀態與桌面可控制，不符就丟棄。</item>
/// <item>SendInput 回傳成功後才把按下的鍵加入狀態表；放開成功後移除。</item>
/// <item>只釋放自己按下且仍在狀態表中的鍵與按鈕。</item>
/// </list>
/// </summary>
public sealed class InjectionEngine
{
    private readonly IInputInjector _injector;
    private readonly IDesktopProbe _probe;
    private readonly IGuardLog _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _heartbeatTimeout;

    private readonly HashSet<(ushort Scan, bool Extended)> _keysDown = [];
    private readonly HashSet<GuardMouseButton> _buttonsDown = [];
    private uint _generation;
    private bool _paused = true; // Host 送出第一個 Resume 前不注入
    private long _lastLivenessTimestamp;

    public InjectionEngine(IInputInjector injector, IDesktopProbe probe, IGuardLog? log = null,
        TimeProvider? time = null, TimeSpan? heartbeatTimeout = null)
    {
        _injector = injector;
        _probe = probe;
        _log = log ?? NullGuardLog.Instance;
        _time = time ?? TimeProvider.System;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(1);
        _lastLivenessTimestamp = _time.GetTimestamp();
    }

    public uint Generation => _generation;
    public bool IsPaused => _paused;
    public bool HasHeldInput => _keysDown.Count > 0 || _buttonsDown.Count > 0;
    public IReadOnlyCollection<(ushort Scan, bool Extended)> KeysDown => _keysDown;
    public IReadOnlyCollection<GuardMouseButton> ButtonsDown => _buttonsDown;

    /// <summary>丟棄的事件數（GUARD-04、05 證據用）。</summary>
    public long DroppedCount { get; private set; }

    /// <summary>處理一則來自 Host.exe 的訊息。</summary>
    public void Handle(in GuardMessage m)
    {
        switch (m.Type)
        {
            case GuardMessageType.Pause:
                if (m.Generation > _generation)
                    _generation = m.Generation;
                _paused = true;
                ReleaseAll("Host 通知暫停");
                return;

            case GuardMessageType.Resume:
                if (m.Generation < _generation)
                {
                    Drop(m, "Resume 世代過舊");
                    return;
                }
                if (m.Generation > _generation)
                    ReleaseAll("世代前進");
                _generation = m.Generation;
                _paused = false;
                _lastLivenessTimestamp = _time.GetTimestamp();
                _log.Info($"恢復注入，世代 {_generation}");
                return;

            case GuardMessageType.ReleaseAll:
                ReleaseAll("Host 要求全部釋放");
                return;
        }

        // 以下為鍵鼠事件與輸入心跳
        if (m.Generation != _generation)
        {
            Drop(m, $"世代不符（目前 {_generation}）");
            return;
        }
        if (_paused)
        {
            Drop(m, "暫停中");
            return;
        }
        if (!_probe.IsControllable())
        {
            Drop(m, "桌面不可控制");
            ReleaseAll("桌面不可控制");
            return;
        }

        _lastLivenessTimestamp = _time.GetTimestamp();

        switch (m.Type)
        {
            case GuardMessageType.InputHeartbeat:
                return;

            case GuardMessageType.Key:
            {
                var key = (m.ScanCode, m.Extended);
                if (m.Down)
                {
                    if (_injector.Key(m.ScanCode, m.Extended, down: true))
                        _keysDown.Add(key);
                }
                else if (_keysDown.Contains(key))
                {
                    // 只放開自己按下的鍵，避免放開本機使用者按住的鍵
                    if (_injector.Key(m.ScanCode, m.Extended, down: false))
                        _keysDown.Remove(key);
                }
                else
                {
                    Drop(m, "放開未由遠端按下的鍵");
                }
                return;
            }

            case GuardMessageType.MouseButton:
                if (m.Down)
                {
                    if (_injector.MouseButton(m.Button, down: true))
                        _buttonsDown.Add(m.Button);
                }
                else if (_buttonsDown.Contains(m.Button))
                {
                    if (_injector.MouseButton(m.Button, down: false))
                        _buttonsDown.Remove(m.Button);
                }
                else
                {
                    Drop(m, "放開未由遠端按下的按鈕");
                }
                return;

            case GuardMessageType.MouseMove:
                _injector.MouseMove(m.X, m.Y);
                return;

            case GuardMessageType.MouseWheel:
                _injector.MouseWheel(m.WheelDelta, m.Horizontal);
                return;

            case GuardMessageType.UnicodeText:
                _injector.UnicodeText(m.Text!);
                return;
        }

        Drop(m, "未知類型");
    }

    /// <summary>
    /// 定期呼叫（例如每 100 ms）：
    /// 有按鍵按住且超過逾時沒收到輸入心跳就釋放；桌面不可控制也釋放。
    /// </summary>
    public void Tick()
    {
        if (!HasHeldInput)
            return;

        if (_time.GetElapsedTime(_lastLivenessTimestamp) > _heartbeatTimeout)
        {
            ReleaseAll("輸入心跳逾時（Host 無回應或控制端停止）");
            return;
        }
        if (!_probe.IsControllable())
            ReleaseAll("桌面不可控制");
    }

    /// <summary>
    /// 新的 Host.exe 實例開始：世代從 0 重新計算（新 Host 不沿用任何舊授權與世代），保持暫停直到其第一個 Resume。
    /// </summary>
    public void ResetForNewHost()
    {
        ReleaseAll("新的 Host 實例");
        _paused = true;
        _generation = 0;
    }

    /// <summary>Host.exe 已結束或 IPC 中斷：暫停並釋放所有遠端按下的鍵。</summary>
    public void OnHostGone()
    {
        _paused = true;
        ReleaseAll("Host.exe 結束或 IPC 中斷");
    }

    /// <summary>
    /// 放開狀態表中所有鍵與按鈕。放開失敗的保留在表中，下次 Tick / ReleaseAll 再試。
    /// </summary>
    public void ReleaseAll(string reason)
    {
        if (!HasHeldInput)
            return;

        int released = 0;
        foreach (var key in _keysDown.ToArray())
        {
            if (_injector.Key(key.Scan, key.Extended, down: false))
            {
                _keysDown.Remove(key);
                released++;
            }
        }
        foreach (var button in _buttonsDown.ToArray())
        {
            if (_injector.MouseButton(button, down: false))
            {
                _buttonsDown.Remove(button);
                released++;
            }
        }

        if (HasHeldInput)
            _log.Warn($"釋放未完成（{reason}）：已放開 {released}，剩餘 {_keysDown.Count + _buttonsDown.Count}");
        else
            _log.Info($"已釋放 {released} 個遠端按鍵／按鈕（{reason}）");
    }

    private void Drop(in GuardMessage m, string reason)
    {
        DroppedCount++;
        _log.Info($"丟棄 {m}：{reason}");
    }
}
