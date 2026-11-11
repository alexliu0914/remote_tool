# 設計

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
| `src/RemoteTool.HostGuard` | net8.0-windows | 輸入注入與 Host 監看 | 規劃中（B1） |

跨平台專案可在 Linux / CI 用真實 TLS 測試（D-023）。

## 2. 連線（已實作）

### 2.1 線路格式（D-009、D-010）

`[Length 4B BE][Channel 1B][Type 1B][Payload ≤ 1 MB]`，Payload 為 MessagePack（不受信任資料模式；類型不符、格式錯誤、多餘位元組皆拒絕）。

### 2.2 建立工作階段

```
控制端                                              被控端
  │── TCP + TLS 1.3 (Control) ─────────────────────▶│
  │   [核對 SPKI 指紋：不符→中止；陌生→問使用者]       │   ← 帳密尚未送出（D-016）
  │── Hello(v1, Control) ──────────────────────────▶│
  │◀──────────────────────────── Hello(v1, Control) ─│   版本不符→Error+關閉（D-022）
  │── LoginRequest(帳號, 密碼) ────────────────────▶│   鎖定檢查、PBKDF2 比對（D-018）
  │◀──────────────────────────── LoginResult ────────│   失敗 / Busy（D-020）→關閉
  │◀──── SessionTokenGrant(Video) / (File) ──────────│   一次性 32B token（D-019）
  │── TCP + TLS 1.3 (Video)，指紋須與 Control 相同 ─▶│
  │── Hello(v1, Video) → SubConnectAuth(token) ────▶│
  │◀─────────────────────────── SubConnectResult ────│
  │◀──── SessionTokenGrant(Video)（補發，供重連）─────│
  │   （File 同上）                                  │
  │── Heartbeat(n) 每 2 秒 ────────────────────────▶│   10 秒未收到→結束（D-021）
  │◀──────────────────────────────── Heartbeat(n) ───│
```

Control 結束 → 撤銷工作階段：作廢 token、關閉 Video / File（D-008）。

首次連線：上圖的指紋確認在一條探測連線上進行，使用者確認後關閉，再以釘選的指紋重新連線並送帳密（D-032）。

### 2.3 子通道擴充點

- 被控端：`HostServerOptions.SubChannelHandler` 在子連線驗證成功後被呼叫，取得可寫入的 `FrameStream` 與對方送來封包的佇列；連線結束時取消。
- 控制端：`ControllerConnectOptions.VideoFrameReceived` 在收到 Video 封包時被呼叫。

### 2.4 本機資料（D-017）

`%LOCALAPPDATA%\RemoteTool`（可用環境變數 `REMOTETOOL_HOME` 覆寫）：`host-cert.pfx`、`host-credentials.json`（僅雜湊）、`known-hosts.json`。

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

### 3.2 A2 正式路徑（規劃中）

DXGI → H.264 硬體編碼（Media Foundation，軟體備援）→ `VideoFrame`（類型 40）→ 控制端硬體解碼 → D3D11 → HwndHost 顯示（D-013）。

## 4. 輸入（規劃中，B1）

Controller 擷取鍵鼠 → Control 連線 → Host 驗證（白名單、工作階段狀態、輸入世代）→ Named Pipe → HostGuard 注入並更新按鍵狀態表（D-005、D-007、D-015）。Host 異常結束時 HostGuard 釋放仍按住的鍵（D-006）。

## 5. 工作階段狀態（規劃中）

鎖定 / 解鎖 / 登出 / 切換使用者 / UAC 安全桌面以 `SessionStateChanged`、`PauseControl`、`ResumeControl` 通知控制端；暫停期間不注入，恢復時以新的輸入世代丟棄舊事件（G0-03）。

## 6. 檔案傳輸（規劃中）

File 連線，256 KB 區塊 + 區塊雜湊，完成後全檔 SHA-256；限速；支援取消與中斷（D-014、G0-04、G0-05）。
