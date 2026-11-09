using RemoteTool.Protocol;

// 被控端（遠端電腦）進入點 — Spike 0 骨架。
//
// 職責（見規劃書 V2.2.2 第三節）：
//   - TLS 監聽、帳密驗證、三條連線與 Session Token
//   - 螢幕擷取與 H.264 編碼
//   - 接收並「驗證」遠端鍵鼠事件，再經本機 IPC 交給 HostGuard 注入
//     （Host.exe 本身不呼叫 SendInput）
//   - 剪貼簿、檔案傳輸、工作階段監控、系統列
//
// 以使用者權限執行，無 Windows 服務、無 SYSTEM。實際功能於 M1 起逐項實作。

Console.WriteLine($"RemoteTool Host (被控端) — 骨架");
Console.WriteLine($"支援通道: {string.Join(", ", Enum.GetNames<ChannelKind>())}");
Console.WriteLine("尚未實作：TLS 監聽、擷取、編碼、IPC。見規劃書 Spike 0 / M1。");
