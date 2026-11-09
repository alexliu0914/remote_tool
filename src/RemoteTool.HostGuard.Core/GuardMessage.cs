namespace RemoteTool.HostGuard.Core;

/// <summary>
/// Host.exe → HostGuard 本機 IPC 訊息白名單（第三節「HostGuard 規格」）。
/// 不在此列舉中的類型一律丟棄並記錄。刻意不提供執行命令、讀檔、剪貼簿等功能。
/// </summary>
public enum GuardMessageType : byte
{
    Key = 1,
    MouseMove = 2,
    MouseButton = 3,
    MouseWheel = 4,
    UnicodeText = 5,
    InputHeartbeat = 6,
    Pause = 7,          // Generation = 暫停後的新世代
    Resume = 8,         // Generation = 恢復後接受的世代
    ReleaseAll = 9,
}

public enum GuardMouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>
/// 一則 IPC 訊息。依 <see cref="Type"/> 只使用相關欄位。
/// </summary>
public readonly record struct GuardMessage(
    GuardMessageType Type,
    uint Generation,
    ushort ScanCode = 0,
    bool Extended = false,
    bool Down = false,
    ushort X = 0,
    ushort Y = 0,
    GuardMouseButton Button = 0,
    short WheelDelta = 0,
    bool Horizontal = false,
    string? Text = null)
{
    public static GuardMessage Key(uint generation, ushort scanCode, bool extended, bool down)
        => new(GuardMessageType.Key, generation, ScanCode: scanCode, Extended: extended, Down: down);

    public static GuardMessage MouseMove(uint generation, ushort x, ushort y)
        => new(GuardMessageType.MouseMove, generation, X: x, Y: y);

    public static GuardMessage MouseButtonEvent(uint generation, GuardMouseButton button, bool down)
        => new(GuardMessageType.MouseButton, generation, Button: button, Down: down);

    public static GuardMessage Wheel(uint generation, short delta, bool horizontal)
        => new(GuardMessageType.MouseWheel, generation, WheelDelta: delta, Horizontal: horizontal);

    public static GuardMessage Unicode(uint generation, string text)
        => new(GuardMessageType.UnicodeText, generation, Text: text);

    public static GuardMessage Heartbeat(uint generation) => new(GuardMessageType.InputHeartbeat, generation);
    public static GuardMessage Pause(uint newGeneration) => new(GuardMessageType.Pause, newGeneration);
    public static GuardMessage Resume(uint generation) => new(GuardMessageType.Resume, generation);
    public static GuardMessage ReleaseAll(uint generation) => new(GuardMessageType.ReleaseAll, generation);

    public override string ToString() => Type == GuardMessageType.UnicodeText
        ? $"{Type} gen={Generation} len={Text?.Length ?? 0}"   // 不把文字內容寫進記錄
        : $"{Type} gen={Generation}";
}
