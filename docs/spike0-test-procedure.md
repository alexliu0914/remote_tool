# Spike 0 雙機實測步驟

適用版本：分支 `ccr-c1bf9aa6-do7hjc`（規劃書 V2.2.2 基線）。本文件可單獨使用，照順序做即可。

## 0. 先讀

**判定規則（規劃書第八節）**
- 每項結果只能填 **PASS、FAIL、未測**。
- PASS 必須附上：測試步驟（本文件的編號即可）、兩台測試機的型號與 Windows 版本、記錄檔、螢幕錄影。
- FAIL 必須保留失敗證據和重現步驟。
- 沒做到、做不了的項目一律填「未測」，不可填 PASS。

**目前做不了的項目（直接填「未測」）**

| 項目 | 原因 |
| --- | --- |
| G0-02 畫面（DXGI → H.264 → 顯示） | 尚未實作。控制端目前只看得到「已收畫面」的計數，沒有影像。鍵鼠注入可以測（T03）。 |
| G0-04、G0-05 | 尚未實作 |
| G0-06 | 尚未研究 |
| GUARD-02 的「IPC 延遲 p95 < 5 ms」 | 還沒有量測工具 |
| 剪貼簿相關（暫停時停止剪貼簿同步） | 剪貼簿是 M2 的功能 |

**名詞**
- **被控端**：被操控的 Windows 11，執行 `HostGuard.exe` 和 `Host.exe`。
- **控制端**：操作的 Windows 11，執行 `Controller.exe`。
- **終端機**：建議用 Windows 終端機（Windows Terminal）。舊式主控台視窗輸入中文可能出問題。

---

## 1. 準備

### 1.1 設備與環境記錄

測試前先填好，之後每份證據都要附上這張表。

| 欄位 | 被控端 | 控制端 |
| --- | --- | --- |
| 機型（廠牌型號） | | |
| CPU / GPU | | |
| Windows 版本（執行 `winver`，例：23H2 組建 22631.xxxx） | | |
| Windows 帳號類型（系統管理員／標準使用者） | | |
| IP 位址 | | |
| 網路（同區網、有線或 Wi-Fi） | | |
| UAC 設定（控制台 → 使用者帳戶 → 變更使用者帳戶控制設定，記下滑桿位置） | | — |

T14、T19 另外需要被控端有**第二個 Windows 帳號 B**（標準使用者即可），並設定好密碼。

### 1.2 建置

在任一台裝有 **.NET 10 SDK** 的 Windows 電腦上，於 PowerShell 執行：

```powershell
git clone https://github.com/alexliu0914/remote_tool.git
cd remote_tool
git checkout ccr-c1bf9aa6-do7hjc

# Debug 建置：Host.exe 才有測試按鍵（Q = 正常結束、X = 未處理例外）
dotnet publish src\RemoteTool.HostGuard  -c Debug -r win-x64 --self-contained -o out\host
dotnet publish src\RemoteTool.Host       -c Debug -r win-x64 --self-contained -o out\host
dotnet publish src\RemoteTool.Controller -c Debug -r win-x64 --self-contained -o out\controller
```

- `out\host` 整個資料夾複製到被控端的 `C:\RemoteTool\bin`。`HostGuard.exe` 和 `Host.exe` 必須放在同一個資料夾。
- `out\controller` 整個資料夾複製到控制端的 `C:\RemoteTool\controller`。
- `tools` 資料夾複製到被控端的 `C:\RemoteTool\tools`。

這是自包含版本，兩台電腦都不用另外安裝 .NET。執行檔沒有程式碼簽章，SmartScreen 若跳出警告，請選「其他資訊 → 仍要執行」，並在結果中記錄有出現警告。

### 1.3 被控端設定（用帳號 A 登入）

1. **確認網路設定檔是「私人」**（PowerShell）：
   ```powershell
   Get-NetConnectionProfile
   ```
   `NetworkCategory` 應為 `Private`。如果是 `Public`，到「設定 → 網路和網際網路 → 內容」改成私人網路。
2. **建立防火牆規則**（系統管理員 PowerShell）：
   ```powershell
   New-NetFirewallRule -DisplayName "RemoteTool Host (Spike 0)" -Direction Inbound `
     -Program "C:\RemoteTool\bin\Host.exe" -Protocol TCP -LocalPort 52100 -Action Allow -Profile Private
   ```
3. **（選做，GUARD-03 需要）註冊事件記錄來源**（系統管理員 PowerShell）：
   ```powershell
   [System.Diagnostics.EventLog]::CreateEventSource('RemoteTool', 'Application')
   ```
4. **建立帳號**（一般 PowerShell，用帳號 A）：
   ```powershell
   cd C:\RemoteTool\bin
   .\Host.exe --add-account tester
   ```
   輸入兩次密碼（至少 10 個字元），應顯示 `已儲存帳號 tester。`
5. **記下主機指紋**：
   ```powershell
   .\Host.exe --fingerprint
   ```
   會印出 16 組、每組 4 個 16 進位字元，例如 `AB12 CD34 …`。拍照或抄下來，T01 要用。

### 1.4 控制端設定

不需要額外設定。主機信任記錄存在 `%LOCALAPPDATA%\RemoteTool\known_hosts.json`。

### 1.5 證據收集

- 每項測試開一個資料夾，例如 `C:\RemoteTool\evidence\T16a\`。
- **記錄檔**：被控端的 `%LOCALAPPDATA%\RemoteTool\logs\` 下有 `host.log`、`hostguard.log`、`keystate-*.log`。每項測試做完就複製一份。
- **控制端輸出**：把終端機內容全選複製，存成 `controller.txt`。
- **錄影**：兩台都要錄。可用 Windows 11 剪取工具的錄影功能（Win+Shift+R，或開剪取工具選「錄製」），也可以用手機同時拍兩台螢幕。錄影時讓**系統時鐘**入鏡，才能和記錄檔的時間對照。
- 每份證據附上 1.1 的環境表。

### 1.6 標準啟動（大部分測試都從這裡開始）

被控端（帳號 A）：

1. 開一個 Windows 終端機分頁 **「Guard」**：
   ```powershell
   cd C:\RemoteTool\bin
   .\HostGuard.exe
   ```
   HostGuard 和 Host 的訊息都印在這個分頁。應看到：
   - `HostGuard 啟動（PID …，工作階段 …）`
   - `已啟動 Host.exe（PID …，實例 1）`
   - `Host.exe 已連上 IPC（實例 1）`
   - `開始監聽 [::]:52100`
   - `主機指紋 SHA-256(SPKI)：…`
   - `[Debug 測試按鍵] Q = 正常結束，X = 未處理例外`
2. 開第二個分頁 **「Monitor」**，啟動按鍵狀態記錄：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\KeyStateMonitor.ps1
   ```
3. 開啟**記事本**，點一下讓它成為前景視窗（遠端打的字會出現在這裡）。

控制端：

4. 開終端機：
   ```powershell
   cd C:\RemoteTool\controller
   .\Controller.exe <被控端 IP> --user tester
   ```
   輸入密碼。等到出現 `[可控制] 世代 N`。也可以輸入 `status`，應看到 `可控制：True`。

**重要**：Guard 分頁按 Ctrl+C 會讓 HostGuard 和 Host **一起**結束。只有在要結束整個測試時才按。

**控制端指令速查**

| 指令 | 作用 |
| --- | --- |
| `status` | 顯示是否可控制、世代、Video／File 是否連線、已收畫面數 |
| `key 1E` | 按一下 A 鍵（掃描碼 16 進位；加 ` e` 代表 extended 鍵） |
| `hold 2A` / `release 2A` | 按住／放開左 Shift |
| `hold 1D` / `release 1D` | 按住／放開左 Ctrl |
| `text 你好` | 以「貼上 Unicode 文字」模式送出 |
| `move 32768 32768` | 移動滑鼠，座標 0–65535 對應整個虛擬桌面（32768 32768 是中央） |
| `click left` / `mdown left` / `mup left` | 按一下／按住／放開左鍵（也可用 right、middle） |
| `wheel -120` | 滾輪向下一格 |
| `drop video` / `drop file` | 強制中斷子連線（已中斷時則手動重連） |
| `quit` | 中斷連線 |

常用掃描碼：A = `1E`，Enter = `1C`，左 Shift = `2A`，左 Ctrl = `1D`，左 Alt = `38`。

---

## 2. 測試案例

建議照編號順序做。T18、T20 會讓 HostGuard 結束，放在最後。

### T01 首次連線與指紋核對（G0-01、SEC-01）

1. 確認控制端的 `%LOCALAPPDATA%\RemoteTool\known_hosts.json` **不存在**。若存在就刪除。
2. 被控端照 1.6 第 1 步啟動 HostGuard。
3. 控制端執行 `.\Controller.exe <IP> --user tester`，輸入密碼。
4. 控制端顯示 `第一次連線到 <IP>。主機指紋 SHA-256(SPKI)：`。逐字和 1.3 第 5 步抄下的指紋比對。
5. **第一次輸入 `no`**。
   - 預期：控制端顯示 `未確認主機指紋，未登入。`；被控端記錄**沒有** `登入成功`。
6. 再執行一次 Controller，這次輸入 `yes`。
   - 預期（控制端）：顯示 `已連線。…`，約 1–2 秒內出現 `[可控制] 世代 N`。
   - 預期（被控端 Guard 分頁）：依序出現 `登入成功，帳號 tester，來源 <控制端 IP>`、`【正在被遠端操控】…`、`Video 子連線已建立`、`File 子連線已建立`、`開始恢復，世代 N`、`已恢復遠端控制，世代 N`，HostGuard 印出 `恢復注入，世代 N`。
7. 控制端輸入 `status`。
   - 預期：`可控制：True … Video：True，File：True，已收畫面 <數字>`。隔幾秒再輸入一次，畫面數應增加（約每秒 2 張，是替代用的空白畫面）。
8. （選做，用來佐證 TLS 1.3）在控制端用 Wireshark 擷取 `tcp.port == 52100`，ClientHello／ServerHello 應顯示 TLS 1.3。

**證據**：錄影（兩台）、指紋照片、controller.txt、host.log、hostguard.log。

### T02 已信任主機不再詢問（G0-01）

1. 控制端輸入 `quit`，再重新執行 Controller 並登入。
   - 預期：**不會**再顯示指紋確認，直接出現 `已連線` 和 `[可控制]`。

### T03 鍵盤、文字與滑鼠注入（G0-02 的鍵鼠部分）

前提：被控端記事本是前景視窗。

1. `key 1E` → 記事本出現 `a`。
2. `key 1C` → 換行。
3. `text 你好，Spike 0 測試 𠀀` → 記事本出現相同文字（含最後的增補字元）。
4. `move 32768 32768` → 被控端滑鼠游標移到虛擬桌面中央（多螢幕時是所有螢幕合起來的中央）。
5. `click left` → 在游標位置點一下。
6. `wheel -120` → 在可捲動的視窗上捲動一格。

**判定**：全部正確為 PASS。另外記錄中文輸入法開啟時，`key` 指令的行為（掃描碼會交給被控端的輸入法處理）。
**注意**：G0-02 本身（畫面）仍是未測。這一項只記在「鍵鼠可操作」這部分的證據。

### T04 錯誤密碼與漸進延遲（SEC-03 的一部分）

1. 控制端連續執行 Controller，每次都輸入**錯誤密碼**，共 6 次。
   - 預期：一開始顯示 `帳號或密碼錯誤。`。之後（依輸入速度而定）會出現 `嘗試次數過多，請於 N 秒後再試。`，N 逐次變大（1、2、4、8、16… 秒，上限 60）。
   - 預期（被控端）：每次失敗都有 `登入失敗，來源 <IP>`，**不含帳號名稱或密碼**。
2. 等到超過顯示的秒數，再用**正確密碼**登入 → 成功。

**證據**：controller.txt、host.log（確認記錄裡沒有帳號或密碼）。

### T05 不可達與被控端未執行（NET-02）

1. 控制端連到一個不存在的區網位址，例如 `.\Controller.exe 192.168.1.250 --user tester`。
   - 預期：約 10 秒內顯示 `連不到 192.168.1.250:52100。…`，**不是**「帳號或密碼錯誤」。
2. 被控端在 Guard 分頁按 Ctrl+C 結束。控制端連到被控端的真實 IP。
   - 預期：同樣顯示 `連不到 …`。
3. 被控端重新啟動 HostGuard。

### T06 同一時間只允許一名控制者

1. 控制端 A 已連線並可控制。
2. 從第二台電腦，或同一台控制端的另一個終端機，再執行一次 Controller 並登入。
   - 預期：第二個顯示 `遠端已有其他控制者連線。`；第一個不受影響（輸入 `status` 仍可控制）。

### T07 主機金鑰變更（SEC-02）

1. 被控端 Guard 分頁按 Ctrl+C 結束。
2. 被控端把 `%LOCALAPPDATA%\RemoteTool\host.dat` **改名**為 `host.dat.bak`。
3. 執行 `.\Host.exe --add-account tester` 重新建立帳號，再執行 `.\Host.exe --fingerprint`。應得到**不同的**指紋。
4. 啟動 HostGuard。控制端連線。
   - 預期：顯示 `<IP> 的主機金鑰已變更，連線已拒絕。新指紋：…`，**不會**出現指紋確認，被控端記錄**沒有** `登入成功`（帳密根本沒有送出）。
5. 還原：被控端結束 HostGuard，刪除新的 `host.dat`，把 `host.dat.bak` 改回 `host.dat`，重新啟動 HostGuard。控制端再連線，應可直接登入（不詢問指紋）。

### T08 Video、File 各自重連（G0-01、NET-03）

1. 已連線並可控制。輸入 `drop video`。
   - 預期（控制端）：依序出現 `[Video 中斷，重新連線中…]`、`[暫停，非即時畫面] 畫面重新連線中（世代 N+1）`、`[Video 已連線]`、`[可控制] 世代 N+1`。整個過程中連線**沒有**結束。
   - 預期（被控端）：`暫停遠端控制：VideoLost，世代 N+1` → `Video 子連線已建立` → `已恢復遠端控制，世代 N+1`。
2. 輸入 `drop file`。
   - 預期：`[File 中斷，重新連線中…]` → `[File 已連線]`，**沒有**暫停訊息，`status` 一直是可控制、世代不變。
3. 輸入 `key 1E`，確認仍可輸入。

### T09 斷線與輸入逾時時釋放按鍵（G0-03 ⑤）

**A. 拔網路**

1. 已連線並可控制，Monitor 分頁正在記錄。
2. 控制端輸入 `hold 2A`。Monitor 應顯示 `LShift DOWN`。
3. **拔掉控制端的網路線**（或關閉控制端的 Wi-Fi），同時看時鐘。
   - 預期：Monitor 約 1 秒內顯示 `LShift UP`（輸入心跳逾時）；HostGuard 印出 `已釋放 1 個遠端按鍵／按鈕（輸入心跳逾時…）`。
   - 預期：約 6 秒後被控端出現 `Control 心跳逾時` 和 `工作階段結束 … 原因 HeartbeatTimeout`；控制端出現 `[連線已結束：HeartbeatTimeout]`。
4. 接回網路，重新執行 Controller，必須重新輸入密碼才能連線。

**B. 控制端停止回應（輸入逾時）**

1. 已連線並可控制。在**控制端**的另一個 PowerShell 視窗先準備好以下指令，但先不要按 Enter：
   ```powershell
   powershell -ExecutionPolicy Bypass -File <tools 路徑>\SuspendProcess.ps1 -Name Controller -DelaySeconds 10 -SuspendSeconds 10
   ```
   （tools 資料夾也要複製一份到控制端。）
2. 按 Enter 執行，10 秒內在 Controller 輸入 `hold 2A`。
   - 預期：Controller 被暫停後約 1 秒，被控端 Monitor 顯示 `LShift UP`。

**證據**：keystate log（看 DOWN／UP 的時間差）、hostguard.log、錄影（時鐘入鏡）。

### T10 鎖定與解鎖（G0-03 ②）

1. 已連線並可控制，Monitor 正在記錄。控制端輸入 `hold 2A`，確認 `LShift DOWN`。
2. 在**被控端本機**按 Win+L 鎖定。
   - 預期（控制端）：`[暫停，非即時畫面] 遠端已鎖定，等待本機解鎖（世代 N+1）`。
   - 預期（被控端）：host.log 有 `暫停遠端控制：Locked，世代 N+1`；hostguard.log 有 `已釋放 1 個遠端按鍵／按鈕（Host 通知暫停）`。
   - 注意：鎖定期間 Monitor 不在輸入桌面上，讀到的一律是「放開」，這段 Monitor 記錄不能當證據，**以 hostguard.log 為準**。
3. 鎖定期間，控制端輸入 `key 1E`。
   - 預期：`目前不可控制，未送出`。
4. 等 10 秒，在被控端本機解鎖。
   - 預期：解鎖約 1 秒後，控制端出現 `[可控制] 世代 N+1`；被控端有 `開始恢復`、`已恢復遠端控制，世代 N+1`。
5. 控制端輸入 `key 1E` → 記事本出現 `a`。

### T11 UAC 提示（G0-03 ②、G0-07）

1. 已連線並可控制。
2. 在**被控端本機**的 PowerShell 執行 `Start-Process notepad -Verb RunAs`，會跳出 UAC 提示。
   - 預期（控制端）：`[暫停，非即時畫面] 遠端桌面暫時無法操作（世代 N+1）`。
   - 預期（被控端）：`暫停遠端控制：DesktopUnavailable`。
   - G0-07：顯示的應該是**通用訊息**，不是「遠端出現系統管理員提示」（目前沒有可靠的 UAC 辨識方式）。
3. UAC 提示期間，控制端輸入 `key 1E` → `目前不可控制，未送出`。
4. 在被控端本機按「否」關閉提示。
   - 預期：約 1 秒後恢復 `[可控制] 世代 N+1`。

**注意**：如果 UAC 滑桿設在「不要將桌面變暗」，UAC 不會切到安全桌面，這時**不會暫停**。請在結果中記錄 UAC 設定。

### T12 登出（G0-03 ③）

1. 已連線並可控制。
2. 在被控端本機登出帳號 A（開始 → 帳號頭像 → 登出）。
   - 預期（控制端）：`[連線已結束：UserLoggedOff]`。
   - 若顯示的是 `Normal` 或 `HeartbeatTimeout`，表示程式被結束時來不及通知控制端。請記錄實際顯示的內容，這一項判 FAIL（不符合「通知控制端後中止」）。
3. 帳號 A 重新登入，照 1.6 重新啟動。

### T13 登入時自動啟動（G0-03 ①）

正式版由 MSI 安裝程式建立排程工作（M4）。這裡用手動建立的排程工作模擬，驗證機制。

1. 被控端用**系統管理員** PowerShell 建立排程工作（帳號 A）：
   ```powershell
   $user = "$env:USERDOMAIN\$env:USERNAME"
   $a = New-ScheduledTaskAction -Execute 'C:\RemoteTool\bin\HostGuard.exe' -WorkingDirectory 'C:\RemoteTool\bin'
   $t = New-ScheduledTaskTrigger -AtLogOn -User $user
   $p = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
   $s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
   Register-ScheduledTask -TaskName 'RemoteTool HostGuard (Spike 0)' -Action $a -Trigger $t -Principal $p -Settings $s
   ```
   （若這時系統管理員 PowerShell 是用另一個帳號開的，`$user` 請改成帳號 A。）
2. 先結束目前的 HostGuard（Ctrl+C），然後登出再登入帳號 A。
   - 預期：登入後自動出現 HostGuard 的主控台視窗，內容同 1.6 第 1 步。
3. 開工作管理員 → 詳細資料 → 在欄位標題按右鍵 →「選取欄位」→ 勾選「已提高權限」。
   - 預期：`HostGuard.exe` 和 `Host.exe` 都存在，使用者名稱是帳號 A，「已提高權限」為**否**。
4. 控制端連線 → 可控制。

測完**先不要刪**這個排程工作，T20 要觀察它的重啟行為。

### T14 Windows 使用者 A → B → A（G0-03 ④）

前提：被控端有帳號 B。先登入一次帳號 B，把 `C:\RemoteTool\bin` 的權限確認為 B 可讀取與執行，並在 B 的 PowerShell 執行 `C:\RemoteTool\bin\Host.exe --add-account testerb`。之後切回帳號 A，照 1.6 啟動並讓控制端連線。

1. 在被控端「開始 → 帳號頭像 → 切換使用者」，切到帳號 B（不要登出 A）。
   - 預期（控制端）：`[連線已結束：UserSwitched]`。
   - 預期（A 的 host.log，回到 A 後查看）：`工作階段事件：ConsoleDisconnect`、`工作階段結束 … 原因 UserSwitched`、`已停止監聽並讓出埠`。
2. 在帳號 B 的工作階段執行 `C:\RemoteTool\bin\HostGuard.exe`。
   - 預期：B 的視窗出現 `開始監聽 [::]:52100`（表示 A 已讓出埠）。
3. 控制端用**帳號 A 的信任記錄**連線（帳號用 `testerb`）。
   - 預期：顯示 `主機金鑰已變更，連線已拒絕`。原因是每個 Windows 使用者有自己的主機金鑰，這也證明 B 的 Host 沒有繼承 A 的任何授權。
   - **請記錄成「發現」**：同一台電腦的不同 Windows 使用者會有不同的指紋，控制端會判定為金鑰變更。這需要另外決定設計方向，不影響本項的判定。
4. 在 B 的 HostGuard 視窗按 Ctrl+C 結束，然後切回帳號 A。
   - 預期（A）：`工作階段事件：ConsoleConnect`、`開始監聽 [::]:52100`（若埠暫時被占用，會先出現 `埠 52100 被占用，稍後重試`，之後成功）。
5. 控制端用 `tester` 連線 → 不詢問指紋，**必須重新輸入密碼**才能登入。

**判定**：第 1、2、4、5 步都符合預期就判 PASS。第 3 步只要「被拒絕」就符合。

### T15 GUARD-02 不影響本機使用者

1. 已連線並可控制，Monitor 正在記錄，記事本在前景。
2. **本機實體**按住左 Ctrl 不放（Monitor 顯示 `LCtrl DOWN`）。這時在控制端輸入 `release 1D`。
   - 預期：Monitor 的 LCtrl **仍是 DOWN**；hostguard.log 有 `丟棄 Key gen=N：放開未由遠端按下的鍵`。放開實體 Ctrl。
3. 在被控端另開一個 PowerShell 執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\DelayedKill.ps1 -Name Host -DelaySeconds 20
   ```
   然後立刻：
   - 控制端輸入 `hold 2A`（遠端按住左 Shift）。
   - 第 15 秒左右，**本機實體**按住右 Shift 不放，一直按到 Host 被結束後 5 秒。
   - 預期：Host 被結束後 3 秒內 Monitor 顯示 `LShift UP`，但 `RShift` 保持 DOWN，直到你放開實體鍵。
4. Host 重啟期間（hostguard.log 顯示 `秒後重啟` 到 `已啟動 Host.exe` 之間），用本機實體鍵盤在記事本打字、移動實體滑鼠。
   - 預期：本機鍵鼠完全正常。

**已知限制**：如果本機剛好也按著**同一個鍵**，釋放遠端按鍵時這個鍵也會被視為放開（Windows 無法區分同一鍵的兩個來源）。這不算 FAIL。
**IPC 延遲 p95 < 5 ms**：填「未測」。

### T16 GUARD-01 Host.exe 崩潰或無回應時釋放按鍵

三種方式各做一次。每次都先照 1.6 讓控制端可控制，並確認 Monitor 正在記錄。
**每次都要遠端按住：左 Shift、左 Ctrl、滑鼠左鍵**（控制端依序輸入 `hold 2A`、`hold 1D`、`mdown left`），並確認 Monitor 顯示 `LShift DOWN`、`LCtrl DOWN`、`LButton DOWN`。
遠端按住期間**不要碰被控端的實體滑鼠**，否則會干擾左鍵的狀態。

**T16a 強制結束**

1. 被控端 Monitor 以外的 PowerShell 執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\DelayedKill.ps1 -Name Host -DelaySeconds 30
   ```
2. 30 秒內在控制端完成三個 hold，然後等待。
   - 預期：DelayedKill 印出 `已強制結束 PID …` 之後 **3 秒內**，Monitor 顯示三個 `UP`。
   - 預期（hostguard.log）：`IPC 中斷`、`已釋放 3 個遠端按鍵／按鈕（…）`、`Host.exe 異常結束（結束碼 0x…），1 秒後重啟`、`已啟動 Host.exe（PID 新的，實例 N+1）`。
   - 預期（控制端）：`[連線已結束：…]`。重新執行 Controller 時**必須重新輸入密碼**。

**T16b 未處理例外**

1. 先用實體滑鼠點一下 Guard 分頁讓它成為前景（**要在 hold 之前點**）。
2. 控制端完成三個 hold。
3. 用實體鍵盤在 Guard 分頁按 **X**（此時遠端還按著 Ctrl、Shift，終端機收到的會是 Ctrl+Shift+X，仍會觸發）。
   - 預期：Guard 分頁出現 `[測試] 觸發未處理例外` 和例外訊息；3 秒內 Monitor 三個 `UP`；hostguard.log 出現 `Host.exe 異常結束（結束碼 0xE0434352）…重啟`。
   - 若終端機攔截了 Ctrl+Shift+X 導致沒有觸發，請記錄下來，並改成只按住 Shift 加滑鼠左鍵重做（在結果中註明）。

**T16c Host 無回應**

1. 被控端執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\SuspendProcess.ps1 -Name Host -DelaySeconds 30 -SuspendSeconds 15
   ```
2. 30 秒內在控制端完成三個 hold。
   - 預期：`已暫停（模擬 Host 無回應）` 之後約 1 秒（3 秒內），Monitor 三個 `UP`；hostguard.log 出現 `已釋放 3 個遠端按鍵／按鈕（輸入心跳逾時…）`。
   - Host 沒有結束，所以**不會**重啟；約 6 秒後控制端出現 `[連線已結束：HeartbeatTimeout]`。

**證據（每種方式各一份）**：keystate log（崩潰前後各鍵狀態與時間戳）、hostguard.log、host.log、DelayedKill／SuspendProcess 的輸出、錄影（時鐘入鏡）。

### T17 GUARD-03 重啟政策：正常結束不重啟

1. 照 1.6 啟動（控制端可以不連線）。
2. 用實體滑鼠點 Guard 分頁，按 **Q**。
   - 預期：`[測試] 正常結束`、`Host 正常結束`、`Host.exe 正常結束，HostGuard 一併結束`；HostGuard 也結束，`Get-Process Host, HostGuard` 找不到這兩個程序。
3. 「登出時不重啟」：T12 登出後重新登入，在啟動 HostGuard 之前確認沒有殘留的 Host.exe。

### T18 GUARD-03 重啟政策：10 分鐘內第 6 次不再重啟

1. 照 1.6 啟動 HostGuard（控制端不需要連線）。
2. 執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\DelayedKill.ps1 -Name Host -DelaySeconds 2 -Repeat 6
   ```
   全程約 1 分鐘。
   - 預期（hostguard.log）：前 5 次分別是 `…，1 秒後重啟（10 分鐘內第 1 次）`、`2 秒…第 2 次`、`4 秒…第 3 次`、`8 秒…第 4 次`、`16 秒…第 5 次`。第 6 次是 `Host.exe 在 10 分鐘內崩潰 6 次，不再重啟`，之後 HostGuard 結束。
   - 用 hostguard.log 的時間戳確認每次「異常結束」到下一次「已啟動 Host.exe」的間隔約為 1、2、4、8、16 秒。
   - DelayedKill 第 6 次之後應顯示「沒有在 60 秒內出現」。
3. 若做了 1.3 第 3 步：開「事件檢視器 → Windows 記錄 → 應用程式」，應有來源 `RemoteTool`、層級「錯誤」的事件，內容是 `Host.exe 在 10 分鐘內異常結束 6 次…`。沒有註冊來源的話，這部分填「未測」。

### T19 GUARD-04 本機 IPC 安全

1. 照 1.6 啟動 HostGuard。用帳號 A 的 PowerShell 取得管道名稱：
   ```powershell
   Get-CimInstance Win32_Process -Filter "Name='Host.exe'" | Select-Object ProcessId, CommandLine
   ```
   記下 `--guard-pipe` 後面的值（形如 `RemoteTool.HostGuard.1.0123…`）。

**A. 其他使用者不能連**

2. 執行 `runas /user:<電腦名>\<帳號B> powershell`，輸入 B 的密碼。在開出的 B 的 PowerShell 執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\PipeProbe.ps1 -PipeName <管道名稱> -Seconds 10
   ```
   - 預期：`無法連線：UnauthorizedAccessException：…`（存取被拒），或 `時限內未能連上`。
   - **FAIL 的條件**：出現 `已連上管道`，且 hostguard.log 沒有對應的拒絕記錄。

**B. 同一使用者、但不是 HostGuard 啟動的程序不能用**

3. 在帳號 A 的 PowerShell 執行（`-Seconds 40`）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\PipeProbe.ps1 -PipeName <管道名稱> -Seconds 40
   ```
4. 在另一個 PowerShell **立刻**執行 `Stop-Process -Name Host -Force`（Host 重啟期間管道會空出來）。
   - 預期：PipeProbe 印出 `已連上管道…`，接著 `管道已被關閉`；hostguard.log 出現 `拒絕 IPC 連線：PID <PipeProbe 的 PID> 不是 HostGuard 啟動的 Host.exe（…）`。之後新的 Host 正常連上：`Host.exe 已連上 IPC（實例 N+1）`。
   - 如果 PipeProbe 顯示 `時限內未能連上`，表示新的 Host 先搶到管道，請重做。

**C. 白名單外訊息、舊世代事件被丟棄**

5. 實機上無法從外部偽造合法的 Host 連線，這一部分目前只有自動化測試涵蓋（`GuardCodecTests`、`InjectionEngineTests`），實機填「未測」。
6. **其他工作階段**的情境（同一使用者在不同工作階段）在一般家用環境很難做出來，填「未測」並註明原因。

### T20 GUARD-05 HostGuard 故障

**A. 不可控制時不注入**：以 T10、T11 的 hostguard.log 為證據。暫停期間若收到事件，記錄中應出現 `丟棄 …：暫停中`、`世代不符` 或 `桌面不可控制`，且沒有對應的注入。

**B. 結束 HostGuard**

1. 照 1.6 讓控制端可控制。控制端輸入 `hold 2A`，Monitor 顯示 `LShift DOWN`。
2. 被控端執行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\RemoteTool\tools\DelayedKill.ps1 -Name HostGuard -DelaySeconds 10
   ```
   - 預期（控制端）：`[連線已結束：GuardUnavailable]`。
   - 預期（host.log）：`HostGuard IPC 中斷：立即停止遠端控制`、`HostGuard IPC 中斷，停止遠端控制並結束`；Host.exe 也結束。
   - **已知限制**：HostGuard 被結束後，按鍵狀態表也跟著消失，LShift **可能會一直是 DOWN**。請記錄 Monitor 的實際結果，然後按一下實體左 Shift 清除。這一點是規劃書明列的限制，不會因此判 FAIL；判定看的是「Host.exe 立即停止遠端控制並提示」，以及「有記錄是否殘留按鍵」。
3. 若 T13 的排程工作還在：觀察工作排程器是否重新啟動 HostGuard，以及間隔多久（工作排程器 → 工作排程器程式庫 → `RemoteTool HostGuard (Spike 0)` → 歷程記錄）。把觀察結果記下來，規劃書這一項的間隔標為「待驗證」。

---

## 3. 結果記錄表

| 測試 | 對應項目 | 結果（PASS／FAIL／未測） | 證據資料夾 | 備註 |
| --- | --- | --- | --- | --- |
| T01 | G0-01、SEC-01 | | | |
| T02 | G0-01 | | | |
| T03 | G0-02（鍵鼠部分） | | | G0-02 畫面部分：未測 |
| T04 | SEC-03（節流） | | | |
| T05 | NET-02 | | | |
| T06 | 單一控制者 | | | |
| T07 | SEC-02 | | | |
| T08 | G0-01、NET-03 | | | |
| T09A / T09B | G0-03 ⑤ | | | |
| T10 | G0-03 ②、VID-02（Win+L） | | | |
| T11 | G0-03 ②、G0-07 | | | UAC 設定： |
| T12 | G0-03 ③ | | | |
| T13 | G0-03 ① | | | 以手動排程工作模擬 |
| T14 | G0-03 ④ | | | 發現：每位使用者指紋不同 |
| T15 | GUARD-02 | | | IPC 延遲：未測 |
| T16a / b / c | GUARD-01 | | | |
| T17 | GUARD-03（正常結束） | | | |
| T18 | GUARD-03（重啟上限） | | | 事件記錄： |
| T19A / B | GUARD-04 | | | C：未測 |
| T20A / B | GUARD-05 | | | 殘留按鍵： |
| — | G0-04、G0-05、G0-06 | 未測 | — | 尚未實作或研究 |

**Gate 判定**：G0-01 要 T01、T02、T08 全部 PASS（T04～T07 是同一 Gate 的安全項目，建議一起 PASS）。G0-03 要 T09～T14、T15～T20 全部 PASS。任何一項 FAIL 或未測，該 Gate 就不能判 PASS。

## 4. 收尾

被控端（系統管理員 PowerShell）：

```powershell
Unregister-ScheduledTask -TaskName 'RemoteTool HostGuard (Spike 0)' -Confirm:$false
Remove-NetFirewallRule -DisplayName "RemoteTool Host (Spike 0)"
# 若註冊過事件來源：
[System.Diagnostics.EventLog]::DeleteEventSource('RemoteTool')
```

測試資料（**先把證據複製出來再刪**）：被控端每個測試帳號的 `%LOCALAPPDATA%\RemoteTool\`，控制端的 `%LOCALAPPDATA%\RemoteTool\known_hosts.json`。

## 5. 遇到問題時

| 現象 | 先檢查 |
| --- | --- |
| 控制端一直顯示「連不到」 | 被控端 Guard 分頁有沒有 `開始監聽`；防火牆規則；網路設定檔是否為「私人」；在被控端執行 `Get-NetTCPConnection -LocalPort 52100 -State Listen` 確認有程序在監聽 |
| 登入顯示「遠端目前不允許控制」 | HostGuard 沒有在執行，或 Host 不是由 HostGuard 啟動的 |
| 連上了但一直沒有 `[可控制]` | 被控端是否鎖定或在 UAC 畫面；host.log 有沒有 `暫停遠端控制：…` |
| `text` 送出的中文變成亂碼或問號 | 控制端改用 Windows 終端機重試；記錄控制端的終端機種類 |
| HostGuard 一啟動就結束 | hostguard.log：`同一工作階段已有 HostGuard 執行中`（先結束舊的）或 `找不到 …\Host.exe`（兩個 exe 不在同一資料夾） |

和預期不同的地方，請保留記錄檔與錄影並寫下重現步驟。這些就是 FAIL 的證據，也是下一輪修正的依據。
