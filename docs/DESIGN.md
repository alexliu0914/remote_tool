# 設計

> 規劃書原文見 [plan/PLAN_V2.2.2.md](plan/PLAN_V2.2.2.md)（最高依據）；目前實作與原文的差異見 [PLAN_GAPS.md](PLAN_GAPS.md)。
> 決議的理由與來源見 [DECISIONS.md](DECISIONS.md)（以 D-xxx 標註）。需求見 [REQUIREMENTS.md](REQUIREMENTS.md)。
> 本文件描述「目前設計」，每節標註實作狀態：**已實作** / **A1 實作中** / **規劃中**。

## 1. 元件（D-004）

```
 控制端（你的電腦）                         被控端（遠端電腦，同一使用者、一般權限）
┌─────────────────────┐   3 條 TLS 1.3   ┌──────────────────────┐  Named Pipe  ┌──────────────┐
│ Controller.exe (WPF)│ ═══ TCP 52100 ══ │ Host.exe             │ ───────────▶ │ HostGuard.exe│
│ 顯示畫面、擷取鍵鼠   │  Control/Video/  │ TLS、登入、擷取、編碼 │  已驗證輸入   │ 唯一 SendInput│
│ 剪貼簿、檔案拖拉     │  File            │ 剪貼簿、檔案、監控    │ ◀─ 監看/重啟 ─│ 不連網        │
└─────────────────────┘                  └──────────────────────┘              └──────────────┘
```

| 專案 | 目標框架 | 職責 | 狀態 |
| --- | --- | --- | --- |
| `src/RemoteTool.Protocol` | net8.0 | 封包分幀、訊息 schema、`FrameStream` | 已實作 |
| `src/RemoteTool.Connection` | net8.0 | TLS 1.3、指紋、帳密、token、三條連線、心跳 | 已實作 |
| `src/RemoteTool.Video` | net8.0 | A1 區塊差異編解碼、縮放、影像串流迴圈（不含擷取） | 已實作（待實機驗證） |
| `src/RemoteTool.Host` | net8.0-windows | 被控端主程式；DXGI 擷取 | 已實作（DXGI 待實機驗證） |
| `src/RemoteTool.Controller` | net8.0-windows（WPF） | 控制端：登入視窗、指紋對話框、遠端畫面視窗 | 已實作（WPF 待實機驗證） |
| `src/RemoteTool.Media` | net10.0-windows | Media Foundation H.264 編碼 / 解碼 | A2a 已實作（待實機驗證） |
| `src/RemoteTool.HostGuard` | net10.0-windows | 輸入注入與 Host 監看 | 規劃中（B1） |

跨平台專案可在 Linux / CI 用真實 TLS 測試（D-023）。

## 2. 連線（已實作）

### 2.1 線路格式（Protocol V1，D-038）

`[Length u32][MessageType u16][RequestId u32][Sequence u32]` + MessagePack，全部 big-endian，Payload ≤ 1 MiB。
完整規格（欄位、上限、訊息表、錯誤處理、版本協商）見 [PROTOCOL_V1.md](PROTOCOL_V1.md)。

### 2.2 建立工作階段

```
控制端                                              被控端
  │── TCP + TLS 1.3 (Control) ─────────────────────▶│
  │   [首次：只取指紋並關閉 → 使用者核對（D-035）]     │   ← 帳密尚未送出
  │   [已知：指紋不符即中止]                          │
  │── Hello(V1, Control, caps) ────────────────────▶│
  │◀──────────────────────── Hello(V1, Control) ─────│   版本不符→Error(VersionMismatch)+關閉
  │── LoginRequest(帳號, 密碼)  [RequestId r] ─────▶│   節流（D-041）、Argon2id（D-037）
  │◀──────────────────────── LoginResult [r] ────────│   失敗 / Busy / TooManyAttempts→關閉
  │◀──── SessionTokenGrant(Video) / (File)，30 秒 ────│   token 只走 Control（D-039）
  │── TCP + TLS 1.3 (Video)，指紋須與 Control 相同 ─▶│
  │── Hello(V1, Video) ────────────────────────────▶│
  │◀──────────── Hello + SubConnectChallenge(nonce) ─│
  │── SubConnectAuth(HMAC(token, nonce‖標籤‖角色)) ─▶│   一次性、限角色、30 秒
  │◀─────────────────────────── SubConnectResult ────│
  │   （File 同上；重連前以 SessionTokenRequest 申請） │
  │── Heartbeat [RequestId h] 每 2 秒 ─────────────▶│   6 秒未收到→結束
  │◀───────────────────────────── Heartbeat [h] ─────│
```

Control 結束 → 撤銷工作階段：作廢 token、關閉 Video / File（D-008）。

### 2.3 子通道擴充點

- 被控端：`HostServerOptions.SubChannelHandler` 在子連線驗證成功後被呼叫，取得可寫入的 `FrameStream` 與對方送來封包的佇列；連線結束時取消。
- 控制端：`ControllerConnectOptions.VideoFrameReceived` 在收到 Video 封包時被呼叫。

### 2.4 本機資料（D-017、D-046）

`%LOCALAPPDATA%\RemoteTool`（可用環境變數 `REMOTETOOL_HOME` 覆寫）：`host-cert.pfx`、`host-credentials.json`（多組帳號，僅 Argon2id 雜湊）、`known-hosts.json`（已信任公鑰與位址提示）——三者以 DPAPI（目前使用者）加密；`controller-settings.json`、`controller.log` 不含秘密。

### 2.5 主機信任（D-045）

以公鑰識別主機：已信任公鑰在任何位址都信任；同位址出現未信任的新金鑰即拒絕；陌生位址＋陌生金鑰則請使用者核對。

### 2.6 子連線重連（D-047）

Video / File 中斷時 Control 保持；控制端以 SessionTokenRequest 取得新 token 重連（退避 1、2、4…秒，30 秒為限，之後可手動重試）。Control 中斷才結束工作階段。

## 3. 遠端畫面

### 3.1 A1 PoC（已實作，待實機驗證；D-026～D-030、D-033）

```
被控端                                                         控制端
DXGI Desktop Duplication（主螢幕）
  → 複製到 staging texture → BGRA byte[]
  → 寬 > 1920 時整數倍縮小（方框平均）
  → 64×64 區塊與上一張比對，只取變動區塊
  → 每塊 Deflate，打包成 VideoTileUpdate（每包 ≤ 512 KB）
  → Video 連線送出 ─────────────────────────────────────▶ 解壓、檢查邊界與大小
                                                            → 寫入畫布（BGRA）
                                                            → EndOfFrame 時更新 WriteableBitmap
◀──────────────────────────────────── KeyframeRequest ──── 首次連線或需要時要求整張重送
```

- **FPS 上限** 預設 10；**佇列** 為零：一張送完才擷取下一張，網路慢時自動降低實際 FPS，記憶體只保留目前與上一張。
- 無變化時（DXGI 逾時）不送資料。
- DXGI `ACCESS_LOST`（解析度變更、桌面切換、UAC）時重建 duplication；重建失敗則每秒重試，期間不送畫面。
- 解碼端防護：寬高上限 8192、區塊必須在畫面內、解壓後大小必須剛好等於區塊大小（防壓縮炸彈）。
- 控制端：背景執行緒解到 `FrameCanvas`，每張完成時累積變動範圍；UI 執行緒只把變動範圍寫入 `WriteableBitmap`（Bgr32，忽略 DXGI 不可靠的 alpha）。UI 跟不上時多張合併成一次更新，不排隊。
- 已知限制：高 DPI 下「原尺寸」並非逐像素（點陣圖以 96 DPI 顯示），A3 處理。

### 3.2 A2 路徑（A2a 已實作，待實機驗證；D-049～D-051）

```
被控端                                                         控制端
DXGI → BGRA → (縮放) → 裁為偶數寬高 → BGRA→NV12（CPU）
  → Media Foundation H.264 編碼（硬體非同步 MFT 優先 → 軟體 MFT，D-052）
  → Annex B → VideoFrame 分塊（≤512 KB）──────────────────▶ 組裝（檢查順序、塊數、大小）
                                                            → MF H.264 解碼（A2a：軟體）→ NV12→BGRA（CPU，裁掉對齊列）
                                                            → FrameCanvas → WriteableBitmap（A2b 改 HwndHost + D3D11）
```

- 協商：控制端 Video Hello 宣告 `video.h264` / `video.tiles`；被控端擇一，編碼器故障時改送區塊差異（控制端兩者都能處理）。
- 關鍵幀：第一張、解析度改變、重新整理、擷取恢復、子連線重連時強制關鍵幀（ICodecAPI ForceKeyFrame）；GOP 2 秒；關鍵幀前若無 SPS/PPS 則補上序列標頭。
- 專案：`src/RemoteTool.Media`（net10.0-windows，Vortice.MediaFoundation），`MfH264HardwareEncoder`（非同步、事件執行緒）/ `MfH264Encoder`（同步軟體）/ `ResilientH264Encoder`（硬體失敗改軟體）/ `MfH264Decoder`；`ProcessOutput` 以 COM vtable 呼叫以明確掌握 sample 參考計數。

## 4. 輸入（規劃中，B1）

Controller 擷取鍵鼠 → Control 連線 → Host 驗證（白名單、工作階段狀態、輸入世代）→ Named Pipe → HostGuard 注入並更新按鍵狀態表（D-005、D-007、D-015）。Host 異常結束時 HostGuard 釋放仍按住的鍵（D-006）。

## 5. 工作階段狀態（規劃中；原文 §3）

**桌面可控制**＝三項同時成立：工作階段未鎖定且在主控台上；輸入桌面為 Default（OpenInputDesktop 可開啟）；DXGI 擷取正常。任一項不成立即暫停。

暫停：① 停止注入並釋放所有遠端按鍵 ② 輸入世代 +1，舊世代事件丟棄 ③ 停止送畫面與剪貼簿 ④ 控制端最後一張畫面變暗並蓋上「已暫停，非即時畫面」。

恢復：① 三項判定連續成立 1 秒以上 ② 先送 IDR，控制端顯示新畫面後回覆新世代並清空未送出的輸入 ③ Host 收到回覆後才接受新世代輸入、恢復剪貼簿。

| Windows 狀態 | 行為 | 控制端訊息 |
| --- | --- | --- |
| 鎖定 | 暫停，連線保持 | 「遠端已鎖定，等待本機解鎖」 |
| 本機解鎖 | 依恢復程序自動恢復 | — |
| 未鎖定但桌面不可控制 | 暫停，連線保持 | 「遠端桌面暫時無法操作」（G0-07 可靠辨識 UAC 時才顯示專用訊息） |
| 登出、關機 | 通知後中止連線 | 「遠端使用者已登出」 |
| 切換使用者 | 中止連線、撤銷授權、讓出 52100；新使用者的 Host 不繼承授權 | 「遠端已切換使用者」 |

以 WTSRegisterSessionNotification 接收事件；HostGuard 也自行檢查鎖定與輸入桌面，不只依賴 Host 通知。

## 6. 檔案傳輸（規劃中）

File 連線，256 KB 區塊 + 區塊雜湊，完成後全檔 SHA-256；限速；支援取消與中斷（D-014、G0-04、G0-05）。

## 7. 部署與自動啟動（規劃中；原文 §3、§4）

WiX MSI（安裝時需管理員一次）：安裝到 Program Files、建立入站防火牆規則（預設只允許私有網路）、建立「登入時執行」的排程工作。每次登入由排程工作以使用者權限啟動 HostGuard，再由 HostGuard 啟動 Host。Host 無主視窗、有系統列圖示（帳號設定、指紋、被操控提示、中斷）。

目前 Spike 原型：Host 為主控台程式、手動啟動，HostGuard 尚未實作。
