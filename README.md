# remote_tool

Windows 11 點對點遠端操控工具（自用）。控制端直接以 IP 連到被控端，不經任何中央協調、雲端或中繼伺服器。

設計依據：規劃書 **V2.2.2 基線**（Spike 0 階段）。Spike 0 各 Gate 的實作與驗證狀態見 [docs/spike0-status.md](docs/spike0-status.md)。

## 架構

被控端由兩個**使用者權限**程式組成，不使用 Windows 服務或 SYSTEM：

| 專案 | 平台 | 職責 |
| --- | --- | --- |
| `src/RemoteTool.Protocol` | 跨平台 | 分幀 `[Length][Type][RequestId][Sequence]`、MessagePack 訊息與檢查、SHA-256(SPKI) 指紋、子連線 HMAC 證明 |
| `src/RemoteTool.Host.Core` | 跨平台 | TLS 1.3 伺服器、Argon2id 帳號、漸進延遲節流、一次性 Session Token、桌面可控制判定（暫停／恢復／輸入世代） |
| `src/RemoteTool.HostGuard.Core` | 跨平台 | IPC 白名單、注入引擎與按鍵狀態表、輸入心跳逾時、重啟政策、監看 |
| `src/RemoteTool.Controller.Core` | 跨平台 | 主機公鑰釘選、登入、Video／File 子連線與退避重連、恢復回覆 |
| `src/RemoteTool.Host` | Windows | Host.exe：DPAPI、Named Pipe 用戶端、WTS 工作階段事件、主控台介面 |
| `src/RemoteTool.HostGuard` | Windows | HostGuard.exe：唯一呼叫 SendInput、輸入桌面檢查、Named Pipe（ACL + PID 檢查）、啟動與重啟 Host.exe |
| `src/RemoteTool.Controller` | Windows | Controller.exe：Spike 0 主控台版（WPF 畫面區於 G0-04） |
| `tests/RemoteTool.Tests` | 跨平台 | 單元測試與 loopback TLS 端對端測試 |

三條 TLS 1.3 連線共用埠 52100：`Control`（登入、鍵鼠、心跳、暫停／恢復）、`Video`（畫面）、`File`（檔案）。

## 建置

需要 .NET 10 SDK。

```
dotnet build RemoteTool.sln -c Release
dotnet test  RemoteTool.sln -c Release
```

核心程式庫與測試可在任何 OS 建置與執行；`Host`、`HostGuard`、`Controller` 為 `net10.0-windows`，在 Linux 上可編譯（`EnableWindowsTargeting`）但只能在 Windows 執行。

## 試用（Spike 0，雙機）

被控端（Windows 11，與 `Host.exe` 同目錄）：

```
Host.exe --add-account alex      # 建立帳號（密碼至少 10 字元）
Host.exe --fingerprint           # 記下主機指紋
HostGuard.exe                    # 啟動 HostGuard，由它啟動 Host.exe 並監聽 52100
```

防火牆需放行 TCP 52100（正式版由 MSI 安裝程式建立規則與「登入時執行」排程工作，M4）。

控制端：

```
Controller.exe 192.168.1.20 --user alex
```

首次連線會顯示指紋，須與被控端逐字核對後輸入 `yes`。
