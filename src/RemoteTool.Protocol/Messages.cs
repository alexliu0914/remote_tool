using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace RemoteTool.Protocol;

/// <summary>一種訊息的固定 schema。每個實作對應唯一的 <see cref="MessageType"/>。</summary>
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
    /// <summary>此來源需等待（漸進延遲）或全域節流中，稍後再試。</summary>
    TooManyAttempts = 2,
    /// <summary>被控端已有其他控制端連線中。</summary>
    Busy = 3,
    /// <summary>子連線 token 無效、已用過、已過期或工作階段已結束。</summary>
    InvalidToken = 4,
}

/// <summary>Error 訊息的錯誤碼。</summary>
public enum ErrorCode
{
    Unspecified = 0,
    /// <summary>協定版本不相容，請更新兩端程式。</summary>
    VersionMismatch = 1,
    /// <summary>收到未定義的訊息類型。</summary>
    UnknownMessageType = 2,
    /// <summary>違反協定：類型不允許、序號錯誤、內容格式錯誤或超出限制。</summary>
    ProtocolViolation = 3,
}

/// <summary>
/// 每條連線的第一個封包，雙方各送一次（Sequence = 1）。
/// <see cref="Role"/> 宣告這條連線的角色（Control / Video / File）；版本不同即中止。
/// Hello 的線路格式在所有未來版本都保持不變，讓舊版也能辨識版本不符。
/// </summary>
[MessagePackObject]
public sealed record Hello(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] ChannelKind Role,
    [property: Key(2)] string[] Capabilities) : IMessage
{
    public static MessageType Type => MessageType.Hello;
}

/// <summary>協定錯誤通知，送出後關閉連線。</summary>
[MessagePackObject]
public sealed record ErrorMessage(
    [property: Key(0)] ErrorCode Code,
    [property: Key(1)] string Message) : IMessage
{
    public static MessageType Type => MessageType.Error;
}

/// <summary>控制端登入（只在 Control 連線、TLS 建立且指紋核對通過後送出）。</summary>
[MessagePackObject]
public sealed record LoginRequest(
    [property: Key(0)] string Username,
    [property: Key(1)] string Password) : IMessage
{
    public static MessageType Type => MessageType.LoginRequest;
}

/// <summary>登入結果。<see cref="RetryAfterSeconds"/> 在 TooManyAttempts 時為建議等待秒數。</summary>
[MessagePackObject]
public sealed record LoginResult(
    [property: Key(0)] bool Success,
    [property: Key(1)] AuthError Error,
    [property: Key(2)] int RetryAfterSeconds) : IMessage
{
    public static MessageType Type => MessageType.LoginResult;
}

/// <summary>
/// 被控端發給控制端的一次性子連線 token（只經由 Control 傳送，子連線上不出現）。
/// 登入成功時 Video、File 各發一枚；之後重連需以 <see cref="SessionTokenRequest"/> 申請。
/// </summary>
[MessagePackObject]
public sealed record SessionTokenGrant(
    [property: Key(0)] ChannelKind Role,
    [property: Key(1)] byte[] Token,
    [property: Key(2)] int ExpiresInSeconds) : IMessage
{
    public static MessageType Type => MessageType.SessionTokenGrant;
}

/// <summary>控制端申請新的子連線 token（子連線重連前）。回應為帶相同 RequestId 的 SessionTokenGrant。</summary>
[MessagePackObject]
public sealed record SessionTokenRequest(
    [property: Key(0)] ChannelKind Role) : IMessage
{
    public static MessageType Type => MessageType.SessionTokenRequest;
}

/// <summary>被控端在子連線 Hello 之後送出的一次性挑戰值。</summary>
[MessagePackObject]
public sealed record SubConnectChallenge(
    [property: Key(0)] byte[] Nonce) : IMessage
{
    public static MessageType Type => MessageType.SubConnectChallenge;
}

/// <summary>子連線證明：<see cref="SubConnectProof.Compute"/>，不直接送出 token。</summary>
[MessagePackObject]
public sealed record SubConnectAuth(
    [property: Key(0)] byte[] Mac) : IMessage
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

/// <summary>Control 心跳。控制端以 RequestId 送出，被控端以相同 RequestId 回覆。</summary>
[MessagePackObject]
public sealed record Heartbeat(
    [property: Key(0)] long Timestamp) : IMessage
{
    public static MessageType Type => MessageType.Heartbeat;
}

/// <summary>滑鼠按鍵。</summary>
public enum MouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>
/// 鍵盤事件（B1，PLAN §5.2：以掃描碼注入，使用遠端輸入法）。<see cref="Generation"/> 為輸入世代，不符即丟棄。
/// </summary>
[MessagePackObject]
public sealed record KeyEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] ushort ScanCode,
    [property: Key(2)] bool Extended,
    [property: Key(3)] bool Down) : IMessage
{
    public static MessageType Type => MessageType.KeyEvent;
}

/// <summary>滑鼠移動。<see cref="X"/>、<see cref="Y"/> 為遠端畫面上的位置，正規化為 0～65535。</summary>
[MessagePackObject]
public sealed record MouseMoveEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] int X,
    [property: Key(2)] int Y) : IMessage
{
    public static MessageType Type => MessageType.MouseMoveEvent;
}

/// <summary>滑鼠按鍵（附位置，正規化 0～65535）。</summary>
[MessagePackObject]
public sealed record MouseButtonEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] MouseButton Button,
    [property: Key(2)] bool Down,
    [property: Key(3)] int X,
    [property: Key(4)] int Y) : IMessage
{
    public static MessageType Type => MessageType.MouseButtonEvent;
}

/// <summary>滑鼠滾輪（附位置，正規化 0～65535）。<see cref="Delta"/> 以 120 為一格。</summary>
[MessagePackObject]
public sealed record MouseWheelEvent(
    [property: Key(0)] uint Generation,
    [property: Key(1)] int Delta,
    [property: Key(2)] bool Horizontal,
    [property: Key(3)] int X,
    [property: Key(4)] int Y) : IMessage
{
    public static MessageType Type => MessageType.MouseWheelEvent;
}

/// <summary>控制端要求放開所有遠端按住的鍵與按鈕（例如畫面區失去焦點）。</summary>
[MessagePackObject]
public sealed record ReleaseAllInput(
    [property: Key(0)] uint Generation) : IMessage
{
    public static MessageType Type => MessageType.ReleaseAllInput;
}

/// <summary>控制端要求被控端下一次送出完整畫面。</summary>
[MessagePackObject]
public sealed record KeyframeRequest(
    [property: Key(0)] long LastFrameId) : IMessage
{
    public static MessageType Type => MessageType.KeyframeRequest;
}

/// <summary>
/// H.264 影像（A2，PLAN §5.1）：一張編碼後的畫面（Annex B 位元串流）。大畫面分塊傳送：
/// 同一張畫面的各塊 <see cref="FrameId"/> 相同，<see cref="ChunkIndex"/> 由 0 遞增到 <see cref="ChunkCount"/> − 1。
/// <see cref="Data"/> 以 ReadOnlyMemory 表示，送出端可直接切片不複製；線路格式仍為 MessagePack bin。
/// 接收端解出的 Data 可能指向重複使用的讀取緩衝區，保留前須複製。
/// </summary>
[MessagePackObject]
public sealed record VideoFrame(
    [property: Key(0)] long FrameId,
    [property: Key(1)] int Width,
    [property: Key(2)] int Height,
    [property: Key(3)] bool IsKeyframe,
    [property: Key(4)] int ChunkIndex,
    [property: Key(5)] int ChunkCount,
    [property: Key(6)] ReadOnlyMemory<byte> Data,
    [property: Key(7)] string Codec) : IMessage
{
    public static MessageType Type => MessageType.VideoFrame;
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
/// A1 PoC 影像格式（D-027）：一張畫面的變動區塊，可能分成多個封包，
/// 最後一個封包 <see cref="EndOfFrame"/> 為 true，控制端此時才更新顯示。
/// <see cref="IsKeyframe"/> 為 true 表示這張畫面送出全部區塊（首張、重新整理、恢復或大小改變）。
/// </summary>
[MessagePackObject]
public sealed record VideoTileUpdate(
    [property: Key(0)] long FrameId,
    [property: Key(1)] int FrameWidth,
    [property: Key(2)] int FrameHeight,
    [property: Key(3)] bool EndOfFrame,
    [property: Key(4)] VideoTile[] Tiles,
    [property: Key(5)] bool IsKeyframe) : IMessage
{
    public static MessageType Type => MessageType.VideoTileUpdate;
}

/// <summary>
/// 子連線證明（PLAN §7）：MAC = HMAC-SHA256(key = token, data = nonce ‖ "RemoteTool-SubConnect-v1" ‖ 角色位元組)。
/// token 本身只在 Control 上傳送一次，子連線上只出現 MAC。
/// </summary>
public static class SubConnectProof
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("RemoteTool-SubConnect-v1");

    public static byte[] Compute(ReadOnlySpan<byte> token, ReadOnlySpan<byte> nonce, ChannelKind role)
    {
        var data = new byte[nonce.Length + Label.Length + 1];
        nonce.CopyTo(data);
        Label.CopyTo(data.AsSpan(nonce.Length));
        data[^1] = (byte)role;
        return HMACSHA256.HashData(token, data);
    }
}
