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

/// <summary>控制端要求被控端下一次送出完整畫面。</summary>
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
