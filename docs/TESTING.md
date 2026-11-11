# 測試流程

> 驗收條件見 [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md)，狀態與紀錄見 [TEST_STATUS.md](TEST_STATUS.md)。
> 測完請把結果（每項通過 / 失敗與現象）回報，記入 TEST_STATUS.md 的測試紀錄。

## 目前進度

| 階段 | 內容 | 狀態 |
| --- | --- | --- |
| M1 第一步 | TLS 1.3、主機指紋、帳密登入、三條連線、一次性 token、心跳 | 單機已驗證（G0-01 PARTIAL_PASS） |
| 規劃書對齊 | .NET 10、Argon2id、漸進延遲節流、Protocol V1、token nonce + HMAC、6 秒心跳 | **待單機回歸（4.3）** |
| **A1** | 被控端 DXGI 擷取主螢幕 → 區塊差異 + Deflate → 控制端 WPF 視窗顯示 | **待實機測試** |

A1 起控制端改為 **WPF 視窗程式**（登入視窗 + 遠端畫面視窗），取代原本的黑色主控台。
因此第四節 4.4 的連線測試要用新介面**重新跑一次**（回歸測試）。

A1 是 PoC（D-026、D-027）：畫面上限 10 FPS、寬 1920，**不代表正式效能**。尚無鍵鼠、游標、多螢幕。

以下指令都在 **PowerShell** 中、`remote_tool` 資料夾執行。

## 一、前置需求

| 項目 | 安裝 | 確認 |
| --- | --- | --- |
| .NET 10 SDK | `winget install Microsoft.DotNet.SDK.10` | `dotnet --version` 顯示 `10.0.x` |
| Git | `winget install Git.Git` | `git --version` |

裝完任何一項都要**關掉 PowerShell 重開**。

## 二、取得 / 更新程式碼

第一次：

```powershell
git clone -b claude/sweet-gauss-x289io https://github.com/alexliu0914/remote_tool.git
```

已經 clone 過：

```powershell
git checkout claude/sweet-gauss-x289io
```

```powershell
git pull
```

**每次更新後都要重新建置**，否則執行的還是舊程式：

```powershell
dotnet build RemoteTool.sln -c Release
```

## 三、自動測試

```powershell
dotnet test RemoteTool.sln -c Release
```

預期三行，全部 `Failed: 0`：

| 測試專案 | 數量 | 內容 |
| --- | --- | --- |
| RemoteTool.Protocol.Tests | 50 | Protocol V1：欄位寬度與位元組順序、長度上限、序號、未知 / 不允許的類型、惡意 MessagePack |
| RemoteTool.Connection.Tests | 55 | 127.0.0.1 真實 TLS 1.3：Argon2id、節流、指紋、探測、Busy、token（HMAC、過期、重放、重新申請）、心跳、協定錯誤 |
| RemoteTool.Video.Tests | 22 | 區塊編解碼逐像素一致、縮放、解碼防護（越界、壓縮炸彈）、經 TLS 的影像串流、FPS 上限 |

## 四、單機實測（一台電腦）

### 4.1 準備

設定捷徑變數（**每開一個新 PowerShell 視窗都要重設**）：

```powershell
$H = ".\src\RemoteTool.Host\bin\Release\net10.0-windows\Host.exe"
```

```powershell
$C = ".\src\RemoteTool.Controller\bin\Release\net10.0-windows\Controller.exe"
```

被控端帳密。（若要測 R-15「舊帳密檔」，請先做 R-15 再設定。）**規劃書對齊後必須重新設定**：密碼改用 Argon2id、最短 10 字元，舊的帳密檔無法使用
（Host 會顯示「帳密檔為舊格式…請重新執行 Host.exe --setup」）：

```powershell
& $H --setup
```

完成時會顯示雜湊參數與本機耗時（例如 `記憶體 19456 KiB、次數 2、平行度 1；本機耗時 120 ms`），請把耗時一併回報。

### 4.2 啟動

**PowerShell 視窗 A**（被控端，保持開著）：

```powershell
& $H
```

畫面會顯示帳號、埠、畫面上限、本機 IP 與主機指紋。

**開控制端**（會出現「RemoteTool 控制端」登入視窗）：

```powershell
& $C
```

輸入 IP `127.0.0.1`、埠 `52100`、帳號、密碼，按「連線」。

> **單機注意**：控制端顯示的是自己的螢幕，畫面裡會出現「畫面中的畫面」無限重複，這是正常的。
> 也因此畫面一直在變，FPS 會維持在上限附近。

### 4.3 回歸：連線測試（WPF 版，對應 G0-01；R-01～R-16）

每項做完按「中斷連線」或關閉畫面視窗，回到登入視窗再做下一項。

| # | 操作 | 預期結果 |
| --- | --- | --- |
| R-01 | 第一次連線 | 跳出「第一次連到 127.0.0.1:52100」視窗，顯示指紋；與視窗 A 逐字核對相同後按「指紋相同，信任並連線」→ 開啟遠端畫面視窗 |
| R-02 | 再連一次 | **不再詢問指紋**，直接開啟畫面視窗 |
| R-03 | 輸入錯誤密碼 | 登入視窗紅字「登入失敗：帳號或密碼錯誤。」 |
| R-04 | 輸入錯誤帳號 | 同上（不透露是帳號還是密碼錯） |
| R-05 | 保持一個連線，再開一個 `& $C` 連線 | 第二個顯示「被控端已有其他控制端連線中。」 |
| R-06 | 連線中，在視窗 A 按 Ctrl+C | 畫面視窗自動關閉，登入視窗顯示「連線結束：…」 |
| R-07 | 視窗 A 重新 `& $H` 後再連 | 成功，不詢問指紋 |
| R-08 | ① 用錯誤密碼**連續快速**按「連線」4～5 次（每次重新輸入錯誤密碼後立刻按）② 出現等待提示後，**立刻**改用正確密碼登入 ③ 等提示的秒數過去，再用正確密碼登入 | ① 前幾次為「帳號或密碼錯誤」，之後出現「登入失敗：嘗試太頻繁，請 N 秒後再試。」（N 依失敗次數為 1、2、4、8…，最多 60）② 正確密碼也先被拒，同樣顯示等待秒數 ③ 登入成功（**不會被鎖定**）。視窗 A 會顯示「登入被拒（TooManyAttempts，N 秒後可再試）」<br>說明：第 1 次失敗後只需等 1 秒，人工重新輸入通常已超過 1 秒，所以只錯一次看不到提示 |
| R-09 | 先按「移除此主機的指紋記錄」→ 是，再連線，指紋視窗按「取消」 | 「未信任此主機，已中止連線（帳密未送出）」；視窗 A **沒有**登入紀錄 |
| R-10 | 關閉 Host，刪除 `%LOCALAPPDATA%\RemoteTool\host-cert.pfx`，重啟 Host 後再連 | 紅字警告視窗「主機指紋與上次不同」，列出上次與這次的指紋；不會連線 |
| R-11 | 接續 R-10，按「移除此主機的指紋記錄」→ 是，再連線 | 重新詢問指紋（新指紋），信任後連線成功 |
| R-12 | 關閉 Host，直接連線 | 「連線失敗」並提示檢查 Host、IP、防火牆 |
| R-13 | 第一次連線的指紋視窗，等 30 秒以上才按「信任並連線」 | 仍能連線成功（不受被控端 15 秒握手時限影響） |
| R-15 | 不重新設定，直接用舊的帳密檔啟動 Host（只在升級後第一次做；若已重設可略過） | Host 顯示「帳密檔為舊格式或不支援的演算法，請重新執行 Host.exe --setup」並結束 |
| R-16 | `& $H --setup` 時輸入 9 個字的密碼 | 顯示「密碼長度須為 10 ~ 1024 字」，不儲存 |
| R-14 | ① 用控制端連線一次（成功或密碼錯誤都可以）② 按登入視窗右上角 ✕ 關閉整個控制端 ③ 重新執行 `& $C` | 登入視窗的 IP、埠、帳號已自動填好，游標停在密碼欄；**密碼欄是空的**（不記密碼）。設定存在 `%LOCALAPPDATA%\RemoteTool\controller-settings.json` |

### 4.4 A1：遠端畫面測試（對應 G0-02 的畫面部分）

| # | 操作 | 預期結果 |
| --- | --- | --- |
| V-01 | 連線成功 | 畫面視窗出現被控端主螢幕的畫面，不是黑畫面 |
| V-02 | 在被控端移動視窗、打字、開關選單 | 控制端約 0.1～0.5 秒內跟著更新，畫面內容正確、顏色正常 |
| V-03 | 看畫面視窗下方狀態列 | 顯示解析度（例如 `1920×1080`）、FPS（不超過 10）、kbps、心跳毫秒 |
| V-04 | 勾選 / 取消「符合視窗大小」 | 勾選：整個畫面縮放到視窗內；取消：原尺寸並出現捲軸 |
| V-05 | 按「重新整理畫面」 | 畫面重繪，不中斷連線 |
| V-06 | 縮放畫面視窗大小、最大化 | 畫面跟著縮放，比例不變形 |
| V-07 | 變更被控端解析度（設定 → 系統 → 顯示器 → 顯示器解析度），之後改回 | 畫面短暫停住後恢復，狀態列解析度跟著變；連線不中斷 |
| V-08 | 被控端開一個需要系統管理員權限的程式，跳出 UAC 視窗時按「否」 | UAC 期間畫面停住（視窗 A 顯示「畫面暫時無法擷取」），關閉 UAC 後恢復（「畫面擷取恢復」）；連線不中斷 |
| V-09 | 按 Win+L 鎖定，再登入解鎖 | 解鎖後畫面恢復、連線仍在（單機時鎖定期間無法看控制端，解鎖後確認即可） |
| V-10 | 關閉 Host，改用 `& $H --fps 5` 啟動後連線 | 狀態列 FPS 不超過 5 |
| V-11 | 有 2K / 4K 螢幕時：連線後看狀態列 | 寬度超過 1920 的螢幕會縮小（例如 3840×2160 → `1920×1080`） |
| V-12 | 連線 5 分鐘、持續移動視窗，開工作管理員看 Host 與 Controller 的記憶體 | 記憶體穩定不持續增加（參考：1080p 時各約 300 MB 以下） |
| V-13 | 畫面視窗按「中斷連線」 | 回到登入視窗顯示「已中斷連線。」；視窗 A 顯示工作階段結束 |

> **A1 的畫面視窗只能「看」，不能操作**：鍵盤與滑鼠要到 B1（HostGuard）才實作，在畫面視窗裡點擊或打字不會有任何反應，這是預期行為。
> 所有操作都直接在**真正的桌面**上做（畫面視窗以外的地方），再回頭看畫面視窗有沒有跟著變。
>
> **單機可以測 A1 畫面**：被控端擷取的是這台電腦的螢幕，控制端視窗顯示的也是這台電腦的螢幕，
> 所以會看到「畫面中的畫面」不斷重複（像兩面鏡子對照），這就是正常結果。
> 判斷方法：在畫面視窗**外面**做動作（例如移動桌面上的另一個視窗、開開始功能表），看畫面視窗裡是否同步出現。
> 建議把畫面視窗縮小到半個螢幕，另一半放要操作的視窗，比較容易看。
>
> 單機與雙機的差別：
>
> | 項目 | 單機 | 說明 |
> | --- | --- | --- |
> | V-01～V-07、V-10、V-12、V-13 | 可以 | 照表操作 |
> | V-08 UAC | 可以，看視窗 A 文字 | UAC 出現時整個螢幕變暗，看不到控制端；關閉 UAC 後檢查視窗 A 有「畫面暫時無法擷取」→「畫面擷取恢復」，且控制端仍連線、畫面恢復 |
> | V-09 鎖定 | 可以，解鎖後確認 | 鎖定期間看不到；解鎖後確認視窗 A 的訊息與控制端仍連線 |
> | V-11 2K / 4K | 需要寬度大於 1920 的螢幕 | 沒有就記為「未測（無設備）」 |
> | 從另一台觀察鎖定、UAC 期間的畫面 | 需要雙機 | 第五節 5.6 |

> 若 V-01 黑畫面或視窗 A 出現「無法啟動 DXGI 擷取」「找不到接著螢幕的顯示卡」，請把視窗 A 的訊息完整回報。

> **控制端診斷記錄**：連線失敗時，完整錯誤會寫入 `%LOCALAPPDATA%\RemoteTool\controller.log`（不含密碼）。
> 回報問題時可用 `Get-Content $env:LOCALAPPDATA\RemoteTool\controller.log -Tail 40` 顯示最後 40 行並貼上。

## 五、雙機實測（兩台實體 Windows 11）

**G0-01 只有雙機實測通過才可標 PASS**（D-025）。

### 5.1 角色

| 電腦 | 稱呼 | 執行 |
| --- | --- | --- |
| A：你操作的那台 | 控制端 | `Controller.exe` |
| B：被操控的那台 | 被控端 | `Host.exe`（日後由 `HostGuard.exe` 啟動；目前直接執行） |

### 5.2 在 A 上打包

```powershell
dotnet publish src/RemoteTool.Host -c Release -r win-x64 --self-contained -o publish\Host
```

```powershell
dotnet publish src/RemoteTool.Controller -c Release -r win-x64 --self-contained -o publish\Controller
```

把整個 `publish\Host` 資料夾複製到 B，例如 `C:\RemoteTool\Host\`。B 不需安裝 .NET。

### 5.3 B：開放防火牆（系統管理員 PowerShell，只需一次）

```powershell
New-NetFirewallRule -DisplayName "RemoteTool 52100" -Direction Inbound -Protocol TCP -LocalPort 52100 -Action Allow -Profile Private
```

B 的網路設定檔需為「私人」。第一次執行 Host.exe 若跳出防火牆提示，選「允許」。測完可移除：

```powershell
Remove-NetFirewallRule -DisplayName "RemoteTool 52100"
```

### 5.4 B：設定並啟動

```powershell
cd C:\RemoteTool\Host
```

```powershell
.\Host.exe --setup
```

```powershell
.\Host.exe
```

記下「本機 IP」（例如 `192.168.1.20`）與主機指紋。

### 5.5 A：確認網路並連線

```powershell
Test-NetConnection 192.168.1.20 -Port 52100
```

`TcpTestSucceeded : True` 代表網路與防火牆正常。接著執行 `publish\Controller\Controller.exe`，IP 填 B 的 IP。

### 5.6 雙機測試項目

1. 第四節 4.3 的 R-01～R-16（把 `127.0.0.1` 換成 B 的 IP）
2. 第四節 4.4 的 V-01～V-13（V-09 可在鎖定期間直接從 A 觀察：畫面停住但連線不斷）
3. 額外：

| # | 操作 | 預期結果 |
| --- | --- | --- |
| D-01 | 連線中拔掉 B 的網路線或關閉 Wi-Fi | A 約 6 秒內畫面視窗關閉，顯示「連線結束：…心跳逾時」 |
| D-02 | 接續 D-01，恢復 B 的網路後再連 | 成功（不會顯示「已有其他控制端」） |
| D-03 | B 靜止不動 30 秒 | 狀態列 FPS 降為 0、kbps 接近 0 |
| D-04 | 在 B 播放影片或快速拖動視窗 | 記下狀態列 FPS 與 kbps（供 A2 比較，A1 不設通過門檻） |

### 5.7 常見問題

| 狀況 | 檢查 |
| --- | --- |
| 連線失敗 | B 的 Host 是否在執行；`Test-NetConnection` 是否成功；B 的 IP 是否變動 |
| SmartScreen 擋下程式 | 自建、未簽章的程式會被提示，點「其他資訊」→「仍要執行」 |
| 「埠 52100 已被占用」 | 已經開了一個 Host；或改埠（Host 用 `--port 52101`，控制端埠欄填 52101） |
| 指紋與上次不同 | 先確認 B 是否刪過資料夾或重灌。**原因不明時不要移除指紋記錄** |

## 六、判定標準

| 項目 | 通過條件 |
| --- | --- |
| 建置 | 0 個錯誤 |
| 自動測試 | 50 + 55 + 22 個全部通過 |
| 單機回歸 | 4.3 的 R-01～R-16 全部符合預期 |
| A1 單機 | 4.4 的 V-01～V-13 全部符合預期 |
| 雙機 | 5.6 全部符合預期（G0-01 才可標 PASS） |

## 七、資料位置與重置

`%LOCALAPPDATA%\RemoteTool`（只有目前 Windows 使用者能存取）：

| 檔案 | 用途 |
| --- | --- |
| `host-cert.pfx` | 被控端憑證與私鑰。刪除後會產生新的，控制端會看到指紋變更警告 |
| `host-credentials.json` | 被控端帳密（只有雜湊）。重設請執行 `Host.exe --setup` |
| `known-hosts.json` | 控制端信任過的主機指紋 |
| `controller-settings.json` | 控制端上次輸入的 IP、埠、帳號（不含密碼） |
| `controller.log` | 控制端診斷記錄（連線失敗的完整錯誤，不含密碼） |

```powershell
explorer $env:LOCALAPPDATA\RemoteTool
```

用另一個資料夾測試（只對目前 PowerShell 視窗有效）：

```powershell
$env:REMOTETOOL_HOME = "C:\RemoteToolTest"
```

## 八、尚未能測的項目

H.264（A2）、鍵鼠（B1）、游標 / 多螢幕 / 高 DPI（A3）、工作階段與重新連線、剪貼簿、檔案拖拉，
以及 GUARD-01～05。

## 附錄：舊版紀錄

M1 第一步的主控台版測試步驟（紀錄 T-003 依據）見 commit `c4b55e0` 的本文件：

```powershell
git show c4b55e0:docs/TESTING.md
```
