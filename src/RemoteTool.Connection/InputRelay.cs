using RemoteTool.Protocol;

namespace RemoteTool.Connection;

/// <summary>擷取中的螢幕在虛擬桌面上的位置（實際像素）與整個虛擬桌面範圍。</summary>
public readonly record struct ScreenGeometry(int OutputLeft, int OutputTop, int OutputWidth, int OutputHeight,
    int VirtualLeft, int VirtualTop, int VirtualWidth, int VirtualHeight);

/// <summary>接收已驗證的輸入（Windows 實作為連到 HostGuard 的 Named Pipe）。</summary>
public interface IInputSink
{
    void Send(GuardRecord record);
}

/// <summary>
/// 被控端的輸入轉送（B1，PLAN §3）：Host.exe 只「接收與驗證」遠端鍵鼠事件，再交給 HostGuard 注入，本身不呼叫 SendInput。
/// 驗證：輸入世代相符、欄位在範圍內、只接受鍵鼠白名單；座標由「遠端畫面正規化座標」換算為「虛擬桌面正規化座標」。
/// </summary>
public sealed class InputRelay
{
    private readonly IInputSink _sink;
    private readonly Func<ScreenGeometry?> _geometry;
    private readonly Action<string>? _log;
    private long _dropped;

    public InputRelay(IInputSink sink, Func<ScreenGeometry?> geometry, Action<string>? log = null)
    {
        _sink = sink;
        _geometry = geometry;
        _log = log;
    }

    /// <summary>目前的輸入世代（B1d 暫停 / 恢復時遞增）。</summary>
    public uint Generation { get; set; } = 1;

    /// <summary>因世代不符或格式不合而丟棄的事件數。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>處理一個 Control 封包；不是輸入訊息時回傳 false。</summary>
    public bool Handle(Frame frame)
    {
        switch (frame.Type)
        {
            case MessageType.KeyEvent:
            {
                var e = MessageCodec.Decode<KeyEvent>(frame);
                if (!Accept(e.Generation) || e.ScanCode is 0 or > 0x1FF) return Drop("按鍵");
                _sink.Send(GuardRecord.Key(e.Generation, e.ScanCode, e.Extended, e.Down));
                return true;
            }
            case MessageType.MouseMoveEvent:
            {
                var e = MessageCodec.Decode<MouseMoveEvent>(frame);
                if (!Accept(e.Generation) || !Map(e.X, e.Y, out int x, out int y)) return Drop("滑鼠移動");
                _sink.Send(GuardRecord.Move(e.Generation, x, y));
                return true;
            }
            case MessageType.MouseButtonEvent:
            {
                var e = MessageCodec.Decode<MouseButtonEvent>(frame);
                if (!Accept(e.Generation) || !Enum.IsDefined(e.Button) || !Map(e.X, e.Y, out int x, out int y)) return Drop("滑鼠按鍵");
                _sink.Send(GuardRecord.Button(e.Generation, e.Button, e.Down, x, y));
                return true;
            }
            case MessageType.MouseWheelEvent:
            {
                var e = MessageCodec.Decode<MouseWheelEvent>(frame);
                if (!Accept(e.Generation) || e.Delta is < -32767 or > 32767 || !Map(e.X, e.Y, out int x, out int y)) return Drop("滾輪");
                _sink.Send(GuardRecord.Wheel(e.Generation, (short)e.Delta, e.Horizontal, x, y));
                return true;
            }
            case MessageType.ReleaseAllInput:
            {
                MessageCodec.Decode<ReleaseAllInput>(frame);
                ReleaseAll(); // 放開不受世代限制：寧可多放開
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>放開所有遠端按住的鍵與按鈕（工作階段結束、控制端要求、斷線時）。</summary>
    public void ReleaseAll() => _sink.Send(GuardRecord.ReleaseAll(Generation));

    private bool Accept(uint generation) => generation == Generation;

    private bool Drop(string kind)
    {
        if (Interlocked.Increment(ref _dropped) <= 5) _log?.Invoke($"丟棄不合法或舊世代的{kind}事件");
        return true;
    }

    /// <summary>遠端畫面正規化座標（0～65535）→ 虛擬桌面正規化座標（SendInput ABSOLUTE | VIRTUALDESK）。</summary>
    public bool Map(int nx, int ny, out int vx, out int vy)
    {
        vx = vy = 0;
        if (nx is < 0 or > 65535 || ny is < 0 or > 65535) return false;
        if (_geometry() is not { } g || g.OutputWidth <= 0 || g.VirtualWidth <= 1 || g.VirtualHeight <= 1) return false;
        double px = g.OutputLeft + nx / 65535.0 * (g.OutputWidth - 1);
        double py = g.OutputTop + ny / 65535.0 * (g.OutputHeight - 1);
        vx = (int)Math.Round((px - g.VirtualLeft) * 65535.0 / (g.VirtualWidth - 1));
        vy = (int)Math.Round((py - g.VirtualTop) * 65535.0 / (g.VirtualHeight - 1));
        vx = Math.Clamp(vx, 0, 65535);
        vy = Math.Clamp(vy, 0, 65535);
        return true;
    }
}
