namespace RemoteTool.Protocol;

/// <summary>
/// 封包類型。每種訊息在上層對應一個固定 schema（MessagePack，見 <see cref="Messages"/>）。
/// 解碼時未知類型即視為格式錯誤並斷線。
/// </summary>
public enum MessageType : byte
{
    // --- 握手與工作階段 (Control；Hello / HelloAck / SubConnect* 也用於 Video、File) ---
    Hello = 1,              // 角色 + ProtocolVersion + Capabilities
    HelloAck = 2,           // 版本結果；子連線附 nonce
    LoginRequest = 3,
    LoginResult = 4,
    SessionTokenRequest = 5,
    SessionTokenGrant = 6,  // 子連線用的一次性 token
    SubConnectAuth = 7,     // HMAC-SHA256(Token, nonce + 角色)
    SubConnectResult = 8,
    Heartbeat = 9,
    HeartbeatAck = 10,
    Disconnect = 11,        // 正常中止並附原因（登出、切換使用者、本機中斷…）

    // --- 工作階段狀態 (Control) ---
    PauseControl = 20,      // 進入暫停；攜帶新的輸入世代編號與原因
    ResumeControl = 21,     // 開始恢復；控制端看到該世代的 IDR 後回覆 ResumeAck
    ResumeAck = 22,

    // --- 輸入 (Control) ---
    KeyEvent = 30,
    MouseMoveEvent = 31,
    MouseButtonEvent = 32,
    MouseWheelEvent = 33,
    UnicodeTextEvent = 34,  // 「貼上 Unicode 文字」
    InputHeartbeat = 35,    // 有鍵按住時定期送，逾時則釋放
    ReleaseAllInput = 36,

    // --- 畫面 (Video) ---
    VideoFrame = 40,
    CursorShape = 41,
    KeyframeRequest = 42,   // 要求 IDR

    // --- 剪貼簿 (Control) ---
    ClipboardText = 50,

    // --- 檔案 (File)；M3 實作 ---
    FileListRequest = 60,
    FileListReply = 61,
    FileChunk = 62,
    FileChunkAck = 63,
    FileTransferCancel = 64,
    FileTransferResult = 65,

    // --- 共用 ---
    Error = 200,
}
