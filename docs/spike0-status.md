# Spike 0 狀態

基線：規劃書 V2.2.2（2026-11-09）。依規劃書規定，驗證結果只能標 **PASS / FAIL / 未測**；PASS 須附測試步驟、測試機型號與 Windows 版本、記錄檔及螢幕錄影。
**以下所有項目目前都是「未測」**：尚未在兩台實體 Windows 11 上實測。自動化測試（loopback、跨平台）只驗證邏輯，不能作為 Gate 的 PASS 證據。

「實作」欄說明程式碼現況；「自動化測試」欄說明已有哪些不依賴 Windows 的測試。

## Gate

| Gate | 實作 | 自動化測試 | 驗證結果 |
| --- | --- | --- | --- |
| G0-01 直接 IP、TLS 1.3、SHA-256(SPKI) 指紋、自訂帳密、三條連線、一次性 Token；Video／File 獨立重連、Control 中斷撤銷 | 已實作（Protocol、Host.Core、Controller.Core；Host.exe／Controller.exe 主控台版） | 有：首次指紋核對、未確認不登入、金鑰變更拒絕、錯誤密碼與節流、不可達與驗證失敗分開提示、第二位控制者被拒、Token 重放／跨角色／過期、Video 中斷暫停並重連恢復、File 獨立重連、Control 中斷撤銷、超大封包 | 未測 |
| G0-02 DXGI → H.264 → 控制端顯示、鍵鼠可操作 | **未實作**。Host.exe 目前用 `PlaceholderVideoSource`（空白關鍵幀）只為驗證恢復程序；鍵鼠注入（SendInput）已實作 | 無（需 Windows GPU） | 未測 |
| G0-03 ① 登入時自動啟動 ② 安全暫停與恢復 ③ 登出中止 ④ A → B → A ⑤ 按鍵釋放與 GUARD-01～05 | ① 排程工作屬 M4 安裝程式，目前手動啟動 HostGuard.exe；②～⑤ 邏輯已實作（ControlGate、HostSession、InjectionEngine、WindowsDesktopStateSource） | 有：鎖定暫停／解鎖恢復且世代遞增、舊世代輸入丟棄、UAC 類（輸入桌面非 Default）暫停、登出中止、切換使用者讓出埠並重新監聽且不繼承 Token、HostGuard 故障停止控制、跨工作階段世代遞增、鎖定與斷線時以真實注入引擎釋放按鍵 | 未測 |
| G0-04 HwndHost 畫面區鍵鼠與拖入 | 未實作 | 無 | 未測 |
| G0-05 遠端面板拖到本地 Explorer、完整性、暫存備援 | 未實作 | 無 | 未測 |
| G0-06 管理員視窗（UIAccess 候選，不阻擋） | 未研究 | 無 | 未測 |
| G0-07 UAC 辨識（不阻擋） | 未研究；目前一律顯示通用訊息「遠端桌面暫時無法操作」 | 有：未可靠辨識時使用通用訊息 | 未測 |

## HostGuard

| ID | 實作 | 自動化測試 | 驗證結果 |
| --- | --- | --- | --- |
| GUARD-01 Host 崩潰／無回應後 3 秒內放開 | Host 結束 → 丟棄該實例佇列並釋放；無回應 → 有鍵按住且 1 秒無輸入心跳即釋放 | 有：心跳逾時釋放、Host 結束釋放並丟棄佇列中的事件 | 未測 |
| GUARD-02 不影響本機使用者 | 只放開狀態表中由遠端按下的鍵；遠端「放開」未由遠端按下的鍵會被丟棄 | 有 | 未測（IPC 延遲 p95 < 5 ms 需實測） |
| GUARD-03 重啟政策 | 結束碼 0 不重啟；異常 1、2、4、8、16 秒退避；10 分鐘內第 6 次放棄並寫事件記錄 | 有 | 未測 |
| GUARD-04 本機 IPC 安全 | Pipe ACL 只允許目前使用者並拒絕網路；FirstPipeInstance；名稱含隨機值；檢查連線端 PID 為自己啟動的 Host.exe 且同一工作階段；Host 端反向檢查伺服器 PID；白名單外訊息丟棄並記錄 | 有：白名單與欄位檢查（PID／ACL 需 Windows） | 未測 |
| GUARD-05 不可控制狀態與 HostGuard 故障 | HostGuard 注入前自行檢查（主控台、未鎖定、輸入桌面為 Default）；HostGuard 結束時 Host.exe 停止遠端控制並以結束碼 2 退出 | 有：桌面不可控制時丟棄並釋放 | 未測 |

## 其他待確認

- .NET 能否取得 TLS exporter（RFC 9266）以綁定子連線授權：**未確認**，目前未綁定（子連線以 HMAC(Token, nonce ‖ 角色) 證明）。
- 軟體 H.264 編碼器的授權與專利責任：未確認（G0-02 尚未實作）。
- `WTSSessionInfoEx` 鎖定旗標、UAC 安全桌面時 `OpenInputDesktop` 的行為、`SessionSwitch` 事件時序：待 G0-03 實機確認。

## 雙機手動測試步驟（建議）

前置：兩台 Windows 11，記錄型號、Windows 版本（`winver`）、網路條件；被控端以 `Host.exe --add-account` 建立帳號並放行 TCP 52100。記錄檔位於 `%LOCALAPPDATA%\RemoteTool\logs\`（`host.log`、`hostguard.log`）。

1. **G0-01**：被控端執行 `HostGuard.exe`。控制端 `Controller.exe <IP> --user <帳號>`，核對指紋（與 `Host.exe --fingerprint` 相同）→ 出現「[可控制]」。`status` 應顯示 Video、File 皆已連線。`drop video`、`drop file` 各自重連，Control 不中斷。關閉控制端 → 被控端記錄「工作階段結束」。
2. **錯誤密碼與節流**：連續輸錯密碼，第二次起應顯示「請於 N 秒後再試」，等待時間 1、2、4… 秒遞增。
3. **G0-03 鎖定**：連線中於被控端按 Win+L → 控制端顯示「遠端已鎖定，等待本機解鎖」；在被控端本機解鎖 → 約 1 秒後顯示「[可控制]」且世代加 1。
4. **G0-03 UAC**：被控端開啟需要提升權限的程式 → 控制端顯示「遠端桌面暫時無法操作」；處理後自動恢復。
5. **G0-03 A → B → A**：連線中於被控端切換到使用者 B → 控制端顯示「遠端已切換使用者」；B 的 HostGuard 可監聽 52100；切回 A 後須重新登入。
6. **GUARD-01**：控制端 `hold 2A`（左 Shift）後，分別以工作管理員結束 Host.exe、讓 Host 未處理例外、讓 Host 無回應；於被控端以記事本觀察 Shift 是否在 3 秒內放開，並錄影。
7. **GUARD-05**：連線中結束 HostGuard.exe → 控制端應顯示連線結束（GuardUnavailable），Host.exe 結束；記錄是否有殘留按鍵。
