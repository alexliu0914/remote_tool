using System.Buffers.Binary;

namespace RemoteTool.Protocol;

/// <summary>Host → HostGuard 本機 IPC 的訊息類型（白名單，PLAN §3「HostGuard 規格」）。</summary>
public enum GuardRecordType : byte
{
    Key = 1,
    MouseMove = 2,
    MouseButton = 3,
    MouseWheel = 4,
    ReleaseAll = 5,
    InputHeartbeat = 6, // B1b
    Pause = 7,          // B1d
    Resume = 8,         // B1d：攜帶新的輸入世代
}

/// <summary>
/// Host 與 HostGuard 之間的固定長度記錄（16 位元組，little-endian），格式簡單、解析不配置記憶體：
/// <code>
/// 0  Type      u8   GuardRecordType（不在白名單即丟棄並記錄）
/// 1  Flags     u8   bit0 按下、bit1 延伸鍵、bit2 水平滾輪
/// 2  Value16   u16  掃描碼 / 滑鼠按鍵 / 滾輪量（int16）
/// 4  X         i32  虛擬桌面正規化座標 0～65535
/// 8  Y         i32
/// 12 Generation u32 輸入世代
/// </code>
/// HostGuard 不提供執行命令、讀檔、剪貼簿等任何其他功能。
/// </summary>
public readonly record struct GuardRecord(GuardRecordType Type, byte Flags, ushort Value16, int X, int Y, uint Generation)
{
    public const int Size = 16;

    /// <summary>
    /// HostGuard 注入時放在 SendInput 的 dwExtraInfo。控制端畫面區看到帶此標記的輸入就不轉送，
    /// 避免單機測試時「注入 → 控制端收到 → 再轉送」形成迴圈。
    /// </summary>
    public const uint InjectedMarker = 0x52544F4C; // "RTOL"
    public const byte FlagDown = 1, FlagExtended = 2, FlagHorizontal = 4;

    public bool Down => (Flags & FlagDown) != 0;
    public bool Extended => (Flags & FlagExtended) != 0;
    public bool Horizontal => (Flags & FlagHorizontal) != 0;

    public static GuardRecord Key(uint gen, ushort scan, bool extended, bool down)
        => new(GuardRecordType.Key, (byte)((down ? FlagDown : 0) | (extended ? FlagExtended : 0)), scan, 0, 0, gen);
    public static GuardRecord Move(uint gen, int x, int y) => new(GuardRecordType.MouseMove, 0, 0, x, y, gen);
    public static GuardRecord Button(uint gen, MouseButton b, bool down, int x, int y)
        => new(GuardRecordType.MouseButton, down ? FlagDown : (byte)0, (ushort)b, x, y, gen);
    public static GuardRecord Wheel(uint gen, short delta, bool horizontal, int x, int y)
        => new(GuardRecordType.MouseWheel, horizontal ? FlagHorizontal : (byte)0, unchecked((ushort)delta), x, y, gen);
    public static GuardRecord ReleaseAll(uint gen) => new(GuardRecordType.ReleaseAll, 0, 0, 0, 0, gen);

    public void WriteTo(Span<byte> dest)
    {
        dest[0] = (byte)Type;
        dest[1] = Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], Value16);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..], X);
        BinaryPrimitives.WriteInt32LittleEndian(dest[8..], Y);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..], Generation);
    }

    /// <summary>解析一筆記錄；類型不在白名單或欄位超出範圍時回傳 false。</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out GuardRecord record)
    {
        record = default;
        if (src.Length < Size) return false;
        var type = (GuardRecordType)src[0];
        if (!Enum.IsDefined(type)) return false;
        var r = new GuardRecord(type, src[1], BinaryPrimitives.ReadUInt16LittleEndian(src[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(src[4..]), BinaryPrimitives.ReadInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[12..]));
        if ((r.Flags & ~(FlagDown | FlagExtended | FlagHorizontal)) != 0) return false;
        if (r.X is < 0 or > 65535 || r.Y is < 0 or > 65535) return false;
        if (type == GuardRecordType.Key && r.Value16 is 0 or > 0x1FF) return false;
        if (type == GuardRecordType.MouseButton && !Enum.IsDefined((MouseButton)r.Value16)) return false;
        record = r;
        return true;
    }
}
