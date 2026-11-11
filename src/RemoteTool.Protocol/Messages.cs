using MessagePack;

namespace RemoteTool.Protocol;

/// <summary>
/// 一種訊息的固定 schema。每個實作對應唯一的 <see cref="MessageType"/>。
/// </summary>
public interface IMessage
{
    static abstract MessageType Type { get; }
}

/// <summary>驗證失敗原因。</summary>
public enum AuthError
{
    None = 0,
    /// <summary>帳號或密碼錯誤。</summary>
    BadCredentials = 1,
    /// <summary>此來源失敗次數過多，暫時鎖定。</summary>
    TooManyAttempts = 2,
    /// <summary>被控端已有其他控制端連線中。</summary>
    Busy = 3,
    /// <summary>子連線 token 無效、已用過或工作階段已結束。</summary>
    InvalidToken = 4,
}

/// <summary>
/// 每條連線的第一個封包，雙方各送一次。
/// <see cref="Channel"/> 宣告這條連線的角色；版本不同即中止。
/// </summary>
[MessagePackObject]
public sealed record Hello(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] ChannelKind Channel) : IMessage
{
    public static MessageType Type => MessageType.Hello;
}

/// <summary>控制端登入（只在 Control 連線、TLS 建立且指紋核對通過後送出）。</summary>
[MessagePackObject]
public sealed record LoginRequest(
    [property: Key(0)] string Username,
    [property: Key(1)] string Password) : IMessage
{
    public static MessageType Type => MessageType.LoginRequest;
}

[MessagePackObject]
public sealed record LoginResult(
    [property: Key(0)] bool Success,
    [property: Key(1)] AuthError Error) : IMessage
{
    public static MessageType Type => MessageType.LoginResult;
}

/// <summary>
/// 被控端發給控制端的一次性子連線 token。
/// 登入成功後各發一個給 Video 與 File；每用掉一個就再補發一個，供該通道重連。
/// </summary>
[MessagePackObject]
public sealed record SessionTokenGrant(
    [property: Key(0)] ChannelKind Channel,
    [property: Key(1)] byte[] Token) : IMessage
{
    public static MessageType Type => MessageType.SessionTokenGrant;
}

/// <summary>Video / File 子連線出示 token。</summary>
[MessagePackObject]
public sealed record SubConnectAuth(
    [property: Key(0)] byte[] Token) : IMessage
{
    public static MessageType Type => MessageType.SubConnectAuth;
}

[MessagePackObject]
public sealed record SubConnectResult(
    [property: Key(0)] bool Success,
    [property: Key(1)] AuthError Error) : IMessage
{
    public static MessageType Type => MessageType.SubConnectResult;
}

/// <summary>Control 心跳。控制端送出，被控端以相同序號回覆。</summary>
[MessagePackObject]
public sealed record Heartbeat(
    [property: Key(0)] long Sequence) : IMessage
{
    public static MessageType Type => MessageType.Heartbeat;
}

/// <summary>協定錯誤通知，送出後通常緊接著關閉連線。</summary>
[MessagePackObject]
public sealed record ErrorMessage(
    [property: Key(0)] string Message) : IMessage
{
    public static MessageType Type => MessageType.Error;
}

/// <summary>控制端要求被控端下一次送出完整畫面（首次連線、解碼錯誤或使用者重新整理）。</summary>
[MessagePackObject]
public sealed record KeyframeRequest(
    [property: Key(0)] long LastFrameId) : IMessage
{
    public static MessageType Type => MessageType.KeyframeRequest;
}

/// <summary>畫面的一個矩形區塊，<see cref="Data"/> 為 BGRA 逐列排列後以 Deflate 壓縮。</summary>
[MessagePackObject]
public sealed record VideoTile(
    [property: Key(0)] int X,
    [property: Key(1)] int Y,
    [property: Key(2)] int Width,
    [property: Key(3)] int Height,
    [property: Key(4)] byte[] Data);

/// <summary>
/// A1 PoC 影像格式（見 DECISIONS D-027）：一張畫面的變動區塊，可能分成多個封包，
/// 最後一個封包 <see cref="EndOfFrame"/> 為 true，控制端此時才更新顯示。
/// </summary>
[MessagePackObject]
public sealed record VideoTileUpdate(
    [property: Key(0)] long FrameId,
    [property: Key(1)] int FrameWidth,
    [property: Key(2)] int FrameHeight,
    [property: Key(3)] bool EndOfFrame,
    [property: Key(4)] VideoTile[] Tiles) : IMessage
{
    public static MessageType Type => MessageType.VideoTileUpdate;
}
