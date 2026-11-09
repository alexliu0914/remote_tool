using MessagePack;

namespace RemoteTool.Protocol.Messages;

// 每種訊息一個固定 schema。反序列化後一律呼叫 Validate()，超長或不合法即拋 InvalidDataException。
// 鍵以整數編號；新增欄位只能附加在最後，不可重排。

/// <summary>可自我檢查的訊息。</summary>
public interface IMessage
{
    /// <summary>檢查欄位範圍；不合法時拋 <see cref="InvalidDataException"/>。</summary>
    void Validate();
}

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    public static void Length(string? value, int max, string field, bool allowEmpty = true)
    {
        That(value is not null, $"{field} 不可為 null");
        That(allowEmpty || value!.Length > 0, $"{field} 不可為空");
        That(value!.Length <= max, $"{field} 超過 {max} 字元");
    }

    public static void Bytes(byte[]? value, int exact, string field)
        => That(value is not null && value.Length == exact, $"{field} 長度必須為 {exact}");

    public static void Defined<TEnum>(TEnum value, string field) where TEnum : struct, Enum
        => That(Enum.IsDefined(value), $"{field} 值不合法");
}

// ---------------- 握手 ----------------

[MessagePackObject]
public sealed record Hello(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] ChannelRole Role,
    [property: Key(2)] string[] Capabilities) : IMessage
{
    public void Validate()
    {
        Check.Defined(Role, nameof(Role));
        Check.That(Capabilities is not null && Capabilities.Length <= 32, "Capabilities 過多");
        foreach (var c in Capabilities!)
            Check.Length(c, 64, "Capability");
    }
}

public enum HelloStatus : byte
{
    Ok = 0,
    VersionMismatch = 1,
    Busy = 2,          // 已有一名控制者
    TooManyConnections = 3,
}

[MessagePackObject]
public sealed record HelloAck(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] HelloStatus Status,
    [property: Key(2)] byte[]? Nonce) : IMessage
{
    public void Validate()
    {
        Check.Defined(Status, nameof(Status));
        if (Nonce is not null)
            Check.Bytes(Nonce, ProtocolConstants.NonceSize, nameof(Nonce));
    }
}

// ---------------- 登入 ----------------

[MessagePackObject]
public sealed record LoginRequest(
    [property: Key(0)] string Username,
    [property: Key(1)] string Password) : IMessage
{
    public void Validate()
    {
        Check.Length(Username, ProtocolConstants.MaxUsernameLength, nameof(Username), allowEmpty: false);
        Check.Length(Password, ProtocolConstants.MaxPasswordLength, nameof(Password), allowEmpty: false);
    }

    // 避免密碼經由 record 的 ToString 意外寫入記錄。
    public override string ToString() => $"LoginRequest {{ Username = {Username} }}";
}

public enum LoginStatus : byte
{
    Ok = 0,
    InvalidCredentials = 1,
    Throttled = 2,       // 依 RetryAfterMs 稍後再試
    Busy = 3,
    NotAllowed = 4,      // 例如本機拒絕「允許」
}

[MessagePackObject]
public sealed record LoginResult(
    [property: Key(0)] LoginStatus Status,
    [property: Key(1)] int RetryAfterMs,
    [property: Key(2)] uint InputGeneration) : IMessage
{
    public void Validate()
    {
        Check.Defined(Status, nameof(Status));
        Check.That(RetryAfterMs >= 0, "RetryAfterMs 不可為負");
    }
}

[MessagePackObject]
public sealed record SessionTokenRequest(
    [property: Key(0)] ChannelRole Role) : IMessage
{
    public void Validate()
    {
        Check.Defined(Role, nameof(Role));
        Check.That(Role != ChannelRole.Control, "不可為 Control 申請 Token");
    }
}

[MessagePackObject]
public sealed record SessionTokenGrant(
    [property: Key(0)] ChannelRole Role,
    [property: Key(1)] byte[] Token,
    [property: Key(2)] int ExpiresInMs) : IMessage
{
    public void Validate()
    {
        Check.Defined(Role, nameof(Role));
        Check.Bytes(Token, ProtocolConstants.SessionTokenSize, nameof(Token));
        Check.That(ExpiresInMs > 0, "ExpiresInMs 必須為正");
    }

    public override string ToString() => $"SessionTokenGrant {{ Role = {Role} }}";
}

[MessagePackObject]
public sealed record SubConnectAuth(
    [property: Key(0)] ChannelRole Role,
    [property: Key(1)] byte[] Proof) : IMessage
{
    public void Validate()
    {
        Check.Defined(Role, nameof(Role));
        Check.That(Role != ChannelRole.Control, "子連線不可為 Control");
        Check.Bytes(Proof, SubConnectProof.Size, nameof(Proof));
    }
}

[MessagePackObject]
public sealed record SubConnectResult(
    [property: Key(0)] bool Success) : IMessage
{
    public void Validate() { }
}

[MessagePackObject]
public sealed record Heartbeat(
    [property: Key(0)] long TimestampMs) : IMessage
{
    public void Validate() { }
}

[MessagePackObject]
public sealed record HeartbeatAck(
    [property: Key(0)] long TimestampMs) : IMessage
{
    public void Validate() { }
}

public enum DisconnectReason : byte
{
    Normal = 0,
    LocalUserDisconnected = 1, // 被控端本機按「中斷」
    UserLoggedOff = 2,
    UserSwitched = 3,
    HostShuttingDown = 4,
    GuardUnavailable = 5,      // HostGuard IPC 中斷，停止遠端控制
    HeartbeatTimeout = 6,
    ProtocolError = 7,
    Replaced = 8,              // 子連線被同角色的新連線取代
}

[MessagePackObject]
public sealed record Disconnect(
    [property: Key(0)] DisconnectReason Reason) : IMessage
{
    public void Validate() => Check.Defined(Reason, nameof(Reason));
}

// ---------------- 工作階段狀態 ----------------

public enum PauseReason : byte
{
    NotStarted = 0,         // 登入後尚未完成第一次恢復（等 Video 與 IDR）
    Locked = 1,             // 「遠端已鎖定，等待本機解鎖」
    DesktopUnavailable = 2, // 通用：「遠端桌面暫時無法操作」
    ElevationPrompt = 3,    // 只有 G0-07 能可靠辨識 UAC 時才使用
    VideoLost = 4,          // 「畫面重新連線中」
    CaptureFailed = 5,
}

[MessagePackObject]
public sealed record PauseControl(
    [property: Key(0)] uint Generation,
    [property: Key(1)] PauseReason Reason) : IMessage
{
    public void Validate() => Check.Defined(Reason, nameof(Reason));
}

[MessagePackObject]
public sealed record ResumeControl(
    [property: Key(0)] uint Generation) : IMessage
{
    public void Validate() { }
}

[MessagePackObject]
public sealed record ResumeAck(
    [property: Key(0)] uint Generation) : IMessage
{
    public void Validate() { }
}

// ---------------- 輸入 ----------------

[MessagePackObject]
public sealed record KeyEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] ushort ScanCode,
    [property: Key(2)] bool Extended,
    [property: Key(3)] bool Down) : IMessage
{
    public void Validate() => Check.That(ScanCode is > 0 and <= 0xFF, "ScanCode 超出範圍");
}

[MessagePackObject]
public sealed record MouseMoveEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] ushort X,   // 0–65535，對應整個虛擬桌面
    [property: Key(2)] ushort Y) : IMessage
{
    public void Validate() { }
}

public enum MouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

[MessagePackObject]
public sealed record MouseButtonEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] MouseButton Button,
    [property: Key(2)] bool Down) : IMessage
{
    public void Validate() => Check.Defined(Button, nameof(Button));
}

[MessagePackObject]
public sealed record MouseWheelEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] short Delta,
    [property: Key(2)] bool Horizontal) : IMessage
{
    public void Validate() { }
}

[MessagePackObject]
public sealed record UnicodeTextEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] string Text) : IMessage
{
    public void Validate() => Check.Length(Text, ProtocolConstants.MaxUnicodeTextLength, nameof(Text), allowEmpty: false);

    public override string ToString() => $"UnicodeTextEvent {{ Generation = {Generation}, Length = {Text.Length} }}";
}

[MessagePackObject]
public sealed record InputHeartbeat(
    [property: Key(0)] uint Generation) : IMessage
{
    public void Validate() { }
}

[MessagePackObject]
public sealed record ReleaseAllInput(
    [property: Key(0)] uint Generation) : IMessage
{
    public void Validate() { }
}

// ---------------- 畫面 ----------------

[MessagePackObject]
public sealed record VideoFrame(
    [property: Key(0)] uint Generation,   // 恢復用：控制端看到此世代的 IDR 後才回覆 ResumeAck
    [property: Key(1)] bool IsKeyframe,
    [property: Key(2)] long TimestampUs,
    [property: Key(3)] byte[] Data) : IMessage
{
    public void Validate() => Check.That(Data is not null, "Data 不可為 null");
}

[MessagePackObject]
public sealed record KeyframeRequest(
    [property: Key(0)] uint Generation) : IMessage
{
    public void Validate() { }
}

// ---------------- 共用 ----------------

[MessagePackObject]
public sealed record ErrorMessage(
    [property: Key(0)] int Code,
    [property: Key(1)] string Message) : IMessage
{
    public void Validate() => Check.Length(Message, 512, nameof(Message));
}
