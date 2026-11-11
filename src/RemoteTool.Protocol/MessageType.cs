namespace RemoteTool.Protocol;

/// <summary>
/// Protocol V1 訊息類型（線路上為 uint16 big-endian）。
/// 不在此列舉中的值為「未知類型」：接收端回 Error(UnknownMessageType) 後關閉連線。
/// 每種類型允許出現的連線角色與方向見 <see cref="ProtocolRules"/>。
/// </summary>
public enum MessageType : ushort
{
    // --- 每條連線共用 ---
    Hello = 1,              // 第一個封包，雙方各送一次：ProtocolVersion、Role、Capabilities

    // --- Control：登入與工作階段 ---
    LoginRequest = 2,
    LoginResult = 3,
    SessionTokenGrant = 4,  // Video / File 的一次性 token（30 秒內使用）
    Heartbeat = 6,          // 請求帶 RequestId，回應填相同 RequestId
    SessionTokenRequest = 8,// 子連線重連前向 Control 申請新 token

    // --- Video / File：子連線驗證 ---
    SubConnectAuth = 5,     // HMAC-SHA256(token, nonce ‖ 標籤 ‖ 角色)
    SubConnectResult = 7,
    SubConnectChallenge = 9,// 被控端送出的一次性 nonce

    // --- Control：工作階段狀態（規劃中） ---
    SessionStateChanged = 10, // 鎖定 / 解鎖 / 登出 / 切換使用者 / UAC
    PauseControl = 11,
    ResumeControl = 12,       // 攜帶新的輸入世代編號

    // --- Control：輸入（規劃中） ---
    KeyEvent = 20,
    MouseMoveEvent = 21,
    MouseButtonEvent = 22,
    MouseWheelEvent = 23,
    InputHeartbeat = 24,     // 有鍵按住時定期送，逾時則釋放
    ReleaseAllInput = 25,

    // --- Video ---
    VideoFrame = 40,         // H.264（A2）
    CursorShape = 41,        // A3
    KeyframeRequest = 42,    // 要求 IDR / 整張重送
    VideoTileUpdate = 43,    // A1 PoC：變動區塊（Deflate）

    // --- Control：剪貼簿（規劃中） ---
    ClipboardText = 50,

    // --- File（規劃中） ---
    FileListRequest = 60,
    FileListReply = 61,
    FileChunk = 62,          // 256 KB 區塊 + 區塊雜湊
    FileChunkAck = 63,
    FileTransferCancel = 64,
    FileTransferResult = 65, // 全檔 SHA-256 驗證結果

    // --- 共用 ---
    Error = 200,
}
