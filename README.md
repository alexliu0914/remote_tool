# remote_tool

Windows 11 點對點遠端操控工具（自用）。控制端直接以 IP 連到被控端，不經任何中央協調、雲端或中繼伺服器。

設計依據：規劃書 **V2.2.2 基線**（Spike 0 階段），原文見 [docs/plan/PLAN_V2.2.2.md](docs/plan/PLAN_V2.2.2.md)。

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
| 共用協定 | `src/RemoteTool.Protocol` | 封包分幀、通道與訊息類型、MessagePack 訊息（跨平台、可測） |
| 連線層 | `src/RemoteTool.Connection` | TLS 1.3、主機指紋、帳密、Session Token、三條連線與心跳（跨平台、可測） |

三條 TLS 連線共用埠 52100：`Control`（鍵鼠、剪貼簿、心跳）、`Video`（H.264）、`File`（限速檔案傳輸）。

## 建置

需要 .NET SDK 10（`global.json` 指定 10.0.100 以上）。

```
dotnet restore RemoteTool.sln
dotnet build   RemoteTool.sln -c Release
dotnet test    RemoteTool.sln -c Release
```

協定、連線、影像庫與其測試為 `net10.0`（跨平台），三個應用程式為 `net10.0-windows`，因此完整建置需在 Windows 上進行。CI 在 `windows-latest` 執行建置與測試。

## 現況

**M1 第一步：連線與登入** 已完成（主控台版本）：

- 被控端 TLS 1.3 監聽、自簽 ECDSA 憑證，指紋為 SHA-256(SPKI)
- 控制端首次連線核對指紋、指紋變更即中止；核對一律在送出帳密之前
- 帳密以 PBKDF2-SHA256 雜湊保存；同來源 5 分鐘內失敗 5 次暫時鎖定
- 登入後以一次性 Session Token 建立 Video / File 子連線；Control 中斷即撤銷整個工作階段
- 心跳 2 秒一次、10 秒逾時；同時只允許一個控制端

擷取、編碼、輸入注入（HostGuard）、剪貼簿、檔案傳輸與 WPF 介面依規劃書後續逐項實作，
並以 G0-01～05 與 GUARD-01～05 實機驗收後才標記完成。

## 文件

| 文件 | 內容 |
| --- | --- |
| [docs/plan/PLAN_V2.2.2.md](docs/plan/PLAN_V2.2.2.md) | **規劃書原文（最高依據）** |
| [docs/PLAN_GAPS.md](docs/PLAN_GAPS.md) | 規劃書與目前文件、程式的差異與處理狀態 |
| [docs/PROTOCOL_V1.md](docs/PROTOCOL_V1.md) | Protocol V1 正式規格（分幀、訊息、錯誤處理、版本協商、子連線授權） |
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | MVP 範圍、功能與非功能需求、不做的項目 |
| [docs/DESIGN.md](docs/DESIGN.md) | 架構、連線流程、畫面 / 輸入 / 檔案設計與實作狀態 |
| [docs/DECISIONS.md](docs/DECISIONS.md) | 已核准的架構決議（只增不刪） |
| [docs/ACCEPTANCE_CRITERIA.md](docs/ACCEPTANCE_CRITERIA.md) | G0、GUARD 驗收條件與狀態定義 |
| [docs/TEST_STATUS.md](docs/TEST_STATUS.md) | 驗收追蹤與測試紀錄（只增不刪） |
| [docs/TESTING.md](docs/TESTING.md) | 測試步驟 |
