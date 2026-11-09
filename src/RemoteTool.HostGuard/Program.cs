// HostGuard 進入點 — Spike 0 骨架。
//
// 職責（見規劃書 V2.2.2 第三節「HostGuard 規格」）：
//   - 唯一呼叫 SendInput 的程式；注入與按鍵狀態表在同一執行緒更新，
//     消除「先記錄再注入」的競態
//   - 經本機 Named Pipe 接收 Host.exe 已授權的鍵鼠事件（白名單、
//     限同使用者與工作階段、只收自己啟動的 Host.exe）
//   - 監看 Host.exe 的 process handle，異常結束後釋放仍按住的鍵，
//     再依重啟政策（指數退避、10 分鐘內最多 5 次）重啟
//   - 鎖定 / 登出 / 切換使用者 / UAC 安全桌面不可用時不注入
//
// 以使用者權限執行，不連網、不讀剪貼簿、不處理畫面或檔案、無 SYSTEM。
// 實際功能於 Spike 0 (GUARD-01~05) 起逐項實作。

Console.WriteLine("RemoteTool HostGuard — 骨架");
Console.WriteLine("尚未實作：本機 IPC、SendInput、按鍵狀態表、監看與重啟。見規劃書 GUARD-01~05。");
