using RemoteTool.Protocol;

// 控制端（你的電腦）進入點 — Spike 0 骨架。
//
// 職責（見規劃書 V2.2.2 第三節）：
//   - 輸入 IP / 帳號 / 密碼，建立 Control 連線並完成 TLS 1.3 握手
//   - 首次連線核對主機指紋 SHA-256(SubjectPublicKeyInfo)
//   - 登入後以 Session Token 開 Video / File 連線
//   - WPF 介面；畫面區用 HwndHost 嵌 D3D11，鍵鼠與拖放由該 Win32 視窗處理
//   - H.264 解碼顯示、擷取鍵鼠、文字剪貼簿、雙向檔案拖拉
//
// 實際功能於 M1 起逐項實作。

Console.WriteLine("RemoteTool Controller (控制端) — 骨架");
Console.WriteLine($"支援通道: {string.Join(", ", Enum.GetNames<ChannelKind>())}");
Console.WriteLine("尚未實作：TLS 連線、指紋核對、WPF 介面、解碼顯示。見規劃書 Spike 0 / M1。");
