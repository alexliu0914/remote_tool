namespace RemoteTool.Protocol;

/// <summary>
/// 封包類型。每種訊息在上層對應一個固定 schema（MessagePack）。
/// 反序列化時拒絕未知類型、超長或格式錯誤的資料。
/// </summary>
public enum MessageType : byte
{
    // --- 握手與工作階段 (Control) ---
    Hello = 1,              // ProtocolVersion + Capabilities
    LoginRequest = 2,
    LoginResult = 3,
    SessionTokenGrant = 4,  // 子連線用的一次性 token
    SubConnectAuth = 5,     // Video / File 子連線出示 token
    Heartbeat = 6,

    // --- 工作階段狀態 (Control) ---
    SessionStateChanged = 10, // 鎖定 / 解鎖 / 登出 / 切換使用者 / UAC
    PauseControl = 11,
    ResumeControl = 12,       // 攜帶新的輸入世代編號

    // --- 輸入 (Control) ---
    KeyEvent = 20,
    MouseMoveEvent = 21,
    MouseButtonEvent = 22,
    MouseWheelEvent = 23,
    InputHeartbeat = 24,     // 有鍵按住時定期送，逾時則釋放
    ReleaseAllInput = 25,

    // --- 畫面 (Video) ---
    VideoFrame = 40,
    CursorShape = 41,
    KeyframeRequest = 42,    // 要求 IDR

    // --- 剪貼簿 (Control) ---
    ClipboardText = 50,

    // --- 檔案 (File) ---
    FileListRequest = 60,
    FileListReply = 61,
    FileChunk = 62,          // 256 KB 區塊 + 區塊雜湊
    FileChunkAck = 63,
    FileTransferCancel = 64,
    FileTransferResult = 65, // 全檔 SHA-256 驗證結果

    // --- 共用 ---
    Error = 200,
}
