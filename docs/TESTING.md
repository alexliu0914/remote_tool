# 測試流程

目前為 **Spike 0 骨架**：可測的是共用協定庫 `RemoteTool.Protocol`（封包分幀）的單元測試，
以及三個元件（Controller / Host / HostGuard）能否建置並啟動。TLS、擷取、編碼、輸入注入、
檔案傳輸尚未實作，因此還沒有端對端（兩台電腦互連）的測試。

## 一、前置需求

- .NET SDK 8.0 以上（`dotnet --version` 應顯示 `8.0.x` 或更新）
- Git

安裝 .NET 8 SDK：

| 平台 | 指令 |
| --- | --- |
| Windows 11 | `winget install Microsoft.DotNet.SDK.8` |
| Ubuntu 24.04 | `sudo apt-get install -y dotnet-sdk-8.0` |

## 二、取得程式碼

```bash
git clone https://github.com/alexliu0914/remote_tool.git
cd remote_tool
```

## 三、Windows 11 上的完整測試（與 CI 相同）

```bash
dotnet restore RemoteTool.sln
dotnet build   RemoteTool.sln -c Release --no-restore
dotnet test    RemoteTool.sln -c Release --no-build --verbosity normal
```

預期：`Build succeeded. 0 Error(s)`，測試 `Passed: 7, Failed: 0`。

啟動三個元件（目前只會印出骨架訊息）：

```bash
src\RemoteTool.Controller\bin\Release\net8.0-windows\Controller.exe
src\RemoteTool.Host\bin\Release\net8.0-windows\Host.exe
src\RemoteTool.HostGuard\bin\Release\net8.0-windows\HostGuard.exe
```

## 四、Linux 上的測試

協定庫與測試為 `net8.0`，可直接在 Linux 測：

```bash
dotnet test tests/RemoteTool.Protocol.Tests/RemoteTool.Protocol.Tests.csproj -c Release
```

三個應用程式為 `net8.0-windows`，在 Linux 上需加 `EnableWindowsTargeting` 才能建置：

```bash
dotnet build RemoteTool.sln -c Release -p:EnableWindowsTargeting=true
```

目前的骨架只用到 Console，因此可在 Linux 以 `dotnet` 執行確認進入點正常
（日後加入 WPF / Win32 / D3D11 後就只能在 Windows 上執行）：

```bash
dotnet src/RemoteTool.Controller/bin/Release/net8.0-windows/Controller.dll
dotnet src/RemoteTool.Host/bin/Release/net8.0-windows/Host.dll
dotnet src/RemoteTool.HostGuard/bin/Release/net8.0-windows/HostGuard.dll
```

## 五、單元測試涵蓋項目（`FrameCodecTests`）

線路格式：`[Length 4B big-endian][Channel 1B][Type 1B][Payload N]`，Payload 上限 1 MB。

| 測試 | 驗證內容 |
| --- | --- |
| `Encode_Then_Decode_RoundTrips` | 編碼後解碼，通道、類型、內容一致 |
| `Encode_EmptyPayload_RoundTrips` | 空內容（如心跳）可正確往返 |
| `TryDecode_IncompleteLengthPrefix_ReturnsFalse` | 長度欄位未收齊時回傳 false、不吃資料 |
| `TryDecode_IncompleteBody_ReturnsFalse` | 內容未收齊時回傳 false、不吃資料 |
| `TryDecode_TwoFramesInBuffer_ConsumesFirstOnly` | 緩衝區有兩個封包時只解第一個，可接續解第二個 |
| `Encode_PayloadOverLimit_Throws` | 超過 1 MB 的內容拒絕編碼 |
| `TryDecode_LengthOverLimit_Throws` | 偽造超大長度欄位時拋出 `InvalidDataException` |

只跑單一測試：

```bash
dotnet test tests/RemoteTool.Protocol.Tests -c Release --filter "FullyQualifiedName~RoundTrips"
```

## 六、判定標準

| 項目 | 通過條件 |
| --- | --- |
| 建置 | 0 Error |
| 單元測試 | 7 / 7 Passed |
| 元件啟動 | 三個程式皆印出「骨架」訊息並正常結束（exit code 0） |

## 七、兩台 Windows 11 的實機測試

> **目前骨架階段只需一台。** 三個程式尚不連網、不互相溝通，兩台都跑結果相同。
> 以下步驟供連線功能（M1 起）完成後使用。

### 7.1 角色分配

| 電腦 | 稱呼 | 執行的程式 | 說明 |
| --- | --- | --- | --- |
| A：你操作的那台 | 控制端 | `Controller.exe` | 輸入被控端 IP、帳號、密碼，看遠端畫面並操作 |
| B：被操控的那台 | 被控端 | `HostGuard.exe` + `Host.exe` | 以一般使用者權限執行，等待連線；依設計由 HostGuard 啟動並監看 Host |

### 7.2 在一台電腦上建置並打包

只需在一台裝有 .NET SDK 的電腦（例如 A）上執行。`--self-contained` 會把 .NET 執行環境一起打包，
另一台**不需安裝 .NET**。

```bash
dotnet test RemoteTool.sln -c Release
```

```bash
dotnet publish src/RemoteTool.Controller -c Release -r win-x64 --self-contained -o publish\Controller
```

```bash
dotnet publish src/RemoteTool.Host -c Release -r win-x64 --self-contained -o publish\Host
```

```bash
dotnet publish src/RemoteTool.HostGuard -c Release -r win-x64 --self-contained -o publish\HostGuard
```

產出：

| 資料夾 | 放到哪台 |
| --- | --- |
| `publish\Controller\` | A（控制端） |
| `publish\Host\`、`publish\HostGuard\` | B（被控端） |

把整個資料夾（不是只有 .exe）以隨身碟、網路芳鄰等方式複製到對應電腦，例如 B 的 `C:\RemoteTool\`。

### 7.3 網路與防火牆

1. 查 B 的 IP：在 B 執行 `ipconfig`，記下「IPv4 位址」（例如 `192.168.1.20`）。
2. 確認 A 連得到 B：在 A 執行 `ping 192.168.1.20`。
   （Windows 預設會擋 ping；不通不一定代表網路不通，可接著測第 4 步。）
3. 在 B 開放 TCP 52100 輸入連線（以系統管理員身分開 PowerShell，只需設定一次）：

   ```powershell
   New-NetFirewallRule -DisplayName "RemoteTool 52100" -Direction Inbound -Protocol TCP -LocalPort 52100 -Action Allow -Profile Private
   ```

   兩台應在同一個區域網路，且網路設定檔為「私人」。若 B 的網路是「公用」，請改為私人，
   或把 `-Profile Private` 改成 `-Profile Any`（安全性較低）。
4. B 啟動 Host 後，在 A 確認埠可連：

   ```powershell
   Test-NetConnection 192.168.1.20 -Port 52100
   ```

   `TcpTestSucceeded : True` 即表示網路與防火牆正常。

移除防火牆規則（測試結束後）：

```powershell
Remove-NetFirewallRule -DisplayName "RemoteTool 52100"
```

### 7.4 執行順序

1. **B（被控端）**：以一般使用者登入桌面（不支援登入前連線），執行 `C:\RemoteTool\HostGuard\HostGuard.exe`，
   確認 Host 已啟動。
2. **A（控制端）**：執行 `Controller.exe`，輸入 B 的 IP、被控端設定的帳號密碼。
3. **首次連線**：核對 Controller 顯示的主機指紋 SHA-256(SPKI) 與 B 上顯示的是否一致，一致才接受。
4. 依規劃書驗收項目逐項測試：畫面、鍵鼠、中英文輸入、雙向剪貼簿、雙向檔案與資料夾拖拉。

### 7.5 常見問題

| 狀況 | 檢查 |
| --- | --- |
| 連不上 | B 的 Host 是否在執行；`Test-NetConnection` 是否成功；IP 是否變動 |
| SmartScreen 擋下程式 | 自建、未簽章的程式會被提示，點「其他資訊」→「仍要執行」 |
| 指紋與上次不同 | B 重新產生了憑證，或連到了別台電腦；確認原因前不要接受 |
| 遠端鎖定 / UAC 視窗時無法操作 | 符合設計：安全桌面與鎖定狀態下不注入輸入 |

## 八、尚未能測的項目

實機驗收項目 G0-01～05 與 GUARD-01～05（擷取、編碼、輸入注入、HostGuard 監看與重啟等）
需等對應功能於 Spike 0 / M1 實作後，於兩台 Windows 11 實機上依規劃書進行。
