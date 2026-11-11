# 測試流程

## 目前進度

**M1 第一步：連線與登入** 已完成，可以測：

- 被控端（Host）以 TLS 1.3 監聽 TCP 52100，第一次啟動自動產生自簽憑證
- 控制端（Controller）以 IP 連線，**第一次連線核對主機指紋**，指紋變了就中止
- 帳號密碼登入（被控端只存雜湊）；同一來源 5 分鐘內失敗 5 次即暫時鎖定
- 登入後以一次性 token 建立 Video、File 兩條子連線（共三條連線）
- 心跳：控制端每 2 秒送一次；10 秒沒心跳即判定斷線
- 同時只允許一個控制端

**尚未實作**：遠端畫面、鍵鼠、剪貼簿、檔案傳輸、WPF 視窗介面。目前兩個程式都是主控台（黑色視窗）版本。

以下指令都在 **PowerShell** 中執行。

## 一、前置需求

| 項目 | 安裝 | 確認 |
| --- | --- | --- |
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` | `dotnet --version` 顯示 `8.0.x` 以上 |
| Git | `winget install Git.Git` | `git --version` |

裝完任何一項都要**關掉 PowerShell 重開**。

## 二、取得程式碼

新程式在 `claude/sweet-gauss-x289io` 分支，要用 `-b` 指定：

```powershell
git clone -b claude/sweet-gauss-x289io https://github.com/alexliu0914/remote_tool.git
```

```powershell
cd remote_tool
```

已經 clone 過的話，改用更新：

```powershell
git fetch origin
```

```powershell
git checkout claude/sweet-gauss-x289io
```

```powershell
git pull
```

## 三、自動測試

```powershell
dotnet build RemoteTool.sln -c Release
```

```powershell
dotnet test RemoteTool.sln -c Release
```

預期兩行結果：

```
Passed!  - Failed: 0, Passed: 20 ... RemoteTool.Protocol.Tests.dll
Passed!  - Failed: 0, Passed: 23 ... RemoteTool.Connection.Tests.dll
```

Connection 測試會在本機 127.0.0.1 實際建立 TLS 1.3 連線，涵蓋：登入成功與三條連線、帳密錯誤、
失敗鎖定、拒絕陌生指紋時帳密不送出、指紋變更即中止、第二個控制端被拒、斷線後可重新登入、
token 只能用一次且不能跨通道、心跳逾時、協定版本不符。

## 四、單機實測（一台電腦，開兩個 PowerShell 視窗）

先在 `remote_tool` 資料夾設定兩個捷徑變數。**每開一個新視窗都要重新設定一次**：

```powershell
$H = ".\src\RemoteTool.Host\bin\Release\net8.0-windows\Host.exe"
```

```powershell
$C = ".\src\RemoteTool.Controller\bin\Release\net8.0-windows\Controller.exe"
```

### 4.1 設定被控端帳密（只需一次）

```powershell
& $H --setup
```

輸入帳號，密碼至少 8 字並輸入兩次。看到「已儲存」即完成。

### 4.2 啟動被控端（視窗 A）

```powershell
& $H
```

畫面會顯示帳號、埠、本機 IP 與 **主機指紋**（4 行十六進位）。讓這個視窗保持開著。

### 4.3 連線（視窗 B）

```powershell
& $C 127.0.0.1
```

依序輸入帳號、密碼。第一次會顯示指紋並問是否信任：**和視窗 A 的指紋逐字比對**，相同才輸入 `y`。

成功時視窗 B 顯示「已連線並登入。Control / Video / File 三條連線皆已建立。」，之後每 5 秒顯示一行
「連線中，心跳來回 N ms」。視窗 A 會出現「登入成功」與兩行「子連線已建立」。按 Enter 中斷。

### 4.4 測試項目清單

每項做完都在視窗 B 按 Enter 中斷（若仍連線中），再做下一項。

| # | 操作 | 預期結果 |
| --- | --- | --- |
| 1 | 照 4.3 連線，指紋輸入 `y` | 連線成功，每 5 秒顯示心跳 |
| 2 | 再連一次 | **不再詢問指紋**，直接連線成功 |
| 3 | 輸入錯誤密碼 | 「登入失敗：帳號或密碼錯誤。」 |
| 4 | 輸入錯誤帳號 | 同上（不會透露是帳號還是密碼錯） |
| 5 | 保持一個視窗 B 連線中，另開視窗 C 再連 | 視窗 C 顯示「被控端已有其他控制端連線中。」 |
| 6 | 連線中，在視窗 A 按 Ctrl+C 關閉被控端 | 視窗 B 數秒內顯示「連線結束：被控端已結束連線」 |
| 7 | 視窗 A 重新 `& $H` 啟動後再連 | 成功（指紋沒變，不會詢問） |
| 8 | 連續輸錯密碼 5 次，第 6 次輸入正確密碼 | 第 6 次顯示「失敗次數過多…暫時被鎖定」；5 分鐘後恢復，或重啟 Host 立即恢復 |
| 9 | 第一次連線時，指紋問題回答 `n` | 「未信任此主機，已中止連線（帳密未送出）」；視窗 A 不會出現登入紀錄 |
| 10 | 模擬憑證被換掉：關閉 Host，刪除 `%LOCALAPPDATA%\RemoteTool\host-cert.pfx`，重新啟動 Host 後再連 | 紅字「警告：主機指紋與上次不同！已中止連線，帳密未送出。」並列出新舊指紋 |
| 11 | 接續第 10 項，執行 `& $C --forget 127.0.0.1` 後再連 | 重新詢問指紋（新指紋），輸入 `y` 後連線成功 |
| 12 | 不開 Host，直接連 | 「連線失敗」並提示檢查 Host、IP、防火牆 |

> 第 9 項：若之前已信任過 127.0.0.1，要先執行 `& $C --forget 127.0.0.1` 才會再被詢問。

## 五、兩台 Windows 11 實測

### 5.1 角色

| 電腦 | 稱呼 | 執行 |
| --- | --- | --- |
| A：你操作的那台 | 控制端 | `Controller.exe` |
| B：被操控的那台 | 被控端 | `Host.exe`（日後改由 `HostGuard.exe` 啟動並監看；目前直接執行 Host） |

### 5.2 在 A 上打包

`--self-contained` 會把 .NET 一起打包，B **不需安裝 .NET**：

```powershell
dotnet publish src/RemoteTool.Host -c Release -r win-x64 --self-contained -o publish\Host
```

```powershell
dotnet publish src/RemoteTool.Controller -c Release -r win-x64 --self-contained -o publish\Controller
```

把整個 `publish\Host` 資料夾（不是只有 .exe）複製到 B，例如 `C:\RemoteTool\Host\`。

### 5.3 B：開放防火牆（系統管理員 PowerShell，只需一次）

```powershell
New-NetFirewallRule -DisplayName "RemoteTool 52100" -Direction Inbound -Protocol TCP -LocalPort 52100 -Action Allow -Profile Private
```

兩台要在同一個區域網路，且 B 的網路設定檔為「私人」（設定 → 網路和網際網路 → 內容）。
第一次執行 Host.exe 時若跳出 Windows 防火牆提示，也選「允許」（私人網路）。

測試結束後可移除：

```powershell
Remove-NetFirewallRule -DisplayName "RemoteTool 52100"
```

### 5.4 B：設定帳密並啟動

```powershell
cd C:\RemoteTool\Host
```

```powershell
.\Host.exe --setup
```

```powershell
.\Host.exe
```

記下畫面上的「本機 IP」（例如 `192.168.1.20`）與主機指紋。

### 5.5 A：確認網路並連線

```powershell
Test-NetConnection 192.168.1.20 -Port 52100
```

`TcpTestSucceeded : True` 代表網路和防火牆正常。接著：

```powershell
.\publish\Controller\Controller.exe 192.168.1.20
```

核對指紋與 B 畫面相同後輸入 `y`。再依第四節 4.4 的清單測一遍（把 `127.0.0.1` 換成 B 的 IP）。
額外加測：

| # | 操作 | 預期結果 |
| --- | --- | --- |
| 13 | 連線中拔掉 B 的網路線或關閉 Wi-Fi | A 約 10 秒內顯示「連線結束：…心跳逾時」 |
| 14 | 接續第 13 項，恢復 B 的網路後再連 | 成功（B 的工作階段也已因心跳逾時結束，不會顯示「已有其他控制端」） |

### 5.6 常見問題

| 狀況 | 檢查 |
| --- | --- |
| 連線失敗 | B 的 Host 是否在執行；`Test-NetConnection` 是否成功；B 的 IP 是否變動 |
| SmartScreen 擋下程式 | 自建、未簽章的程式會被提示，點「其他資訊」→「仍要執行」 |
| 「埠 52100 已被占用」 | 已經開了一個 Host；或用 `--port 52101` 改埠（兩邊都要加） |
| 指紋與上次不同 | 先確認 B 是否刪過資料夾或重灌。**原因不明時不要 `--forget`** |

## 六、判定標準

| 項目 | 通過條件 |
| --- | --- |
| 建置 | 0 個錯誤 |
| 自動測試 | 20 + 23 個全部通過 |
| 單機實測 | 4.4 的 12 項全部符合預期 |
| 兩台實測 | 4.4 的 12 項 + 5.5 的 2 項全部符合預期 |

## 七、資料位置與重置

兩個程式的資料都放在 `%LOCALAPPDATA%\RemoteTool`（只有目前 Windows 使用者能存取）：

| 檔案 | 用途 |
| --- | --- |
| `host-cert.pfx` | 被控端憑證與私鑰。刪除後會產生新的，控制端會看到指紋變更警告 |
| `host-credentials.json` | 被控端帳密（只有雜湊，沒有明文密碼）。重設請執行 `Host.exe --setup` |
| `known-hosts.json` | 控制端信任過的主機指紋 |

開啟資料夾：

```powershell
explorer $env:LOCALAPPDATA\RemoteTool
```

想用另一個資料夾測試（不影響原本設定），先設定環境變數，只對目前視窗有效：

```powershell
$env:REMOTETOOL_HOME = "C:\RemoteToolTest"
```

## 八、尚未能測的項目

遠端畫面、鍵鼠、中英文輸入、剪貼簿、檔案拖拉，以及規劃書的 G0-01～05、GUARD-01～05
（擷取、編碼、輸入注入、HostGuard 監看與重啟），需等對應功能實作後再測。
