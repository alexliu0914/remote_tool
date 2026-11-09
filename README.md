# remote_tool

Windows 11 點對點遠端操控工具（自用）。控制端直接以 IP 連到被控端，不經任何中央協調、雲端或中繼伺服器。

設計依據：規劃書 **V2.2.2 基線**（Spike 0 階段）。

## 範圍

- **平台**：Windows 11 x64 ↔ Windows 11 x64
- **連線**：直接 IP、TLS 1.3、被控端自訂帳密、首次連線核對主機指紋 SHA-256(SPKI)
- **功能**：遠端畫面、鍵鼠、中英文輸入、雙向文字剪貼簿、雙向檔案與資料夾傳輸（工具內拖拉）
- **不做**：雲端中繼、手機 / Mac、登入前連線、遠端操作 UAC、提升權限

## 架構

被控端由兩個**使用者權限**程式組成，不使用 Windows 服務或 SYSTEM：

| 元件 | 專案 | 職責 |
| --- | --- | --- |
| 控制端 | `src/RemoteTool.Controller` | 連線、WPF 介面、解碼顯示、擷取鍵鼠、剪貼簿、檔案拖拉 |
| 被控端主程式 | `src/RemoteTool.Host` | TLS、帳密、三條連線、擷取、編碼、剪貼簿、檔案、工作階段監控 |
| 被控端守護 | `src/RemoteTool.HostGuard` | 唯一注入輸入者（SendInput）、按鍵狀態、監看與重啟 Host |
| 共用協定 | `src/RemoteTool.Protocol` | 封包分幀、通道與訊息類型（跨平台、可測） |

三條 TLS 連線共用埠 52100：`Control`（鍵鼠、剪貼簿、心跳）、`Video`（H.264）、`File`（限速檔案傳輸）。

## 建置

需要 .NET SDK 8.0 以上。

```
dotnet restore RemoteTool.sln
dotnet build   RemoteTool.sln -c Release
dotnet test    RemoteTool.sln -c Release
```

協定庫與其測試為 `net8.0`（跨平台），三個應用程式為 `net8.0-windows`，因此完整建置需在 Windows 上進行。CI 在 `windows-latest` 執行建置與測試。

## 現況

Spike 0 骨架：方案結構、共用協定分幀與測試、三個元件進入點。擷取、編碼、輸入注入、檔案傳輸等平台專屬功能依規劃書 Spike 0 / M1 逐項實作，並以 G0-01～05 與 GUARD-01～05 實機驗收後才標記完成。
