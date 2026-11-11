# Protocol V1 規格

> 依據：規劃書 [plan/PLAN_V2.2.2.md](plan/PLAN_V2.2.2.md) 第七節；核准：2026-11-11（PLAN_GAPS G-08、G-07，DECISIONS D-038、D-039）。
> 實作：`src/RemoteTool.Protocol`（`FrameCodec`、`FrameStream`、`ProtocolRules`、`MessageCodec`、`Messages`）。
> 一致性測試：`tests/RemoteTool.Protocol.Tests`、`tests/RemoteTool.Connection.Tests`。

## 1. 傳輸

- 三條 TCP 連線，皆為 **TLS 1.3**，共用埠 **52100**：Control、Video、File。
- 被控端使用自簽憑證；控制端只比對主機指紋 SHA-256(SubjectPublicKeyInfo)，不檢查憑證鏈、名稱、到期日。
- **所有連線共用同一套分幀**（第 2 節）。連線角色由該連線的 Hello 決定，封包本身不帶通道欄位。

## 2. 分幀（Framing）

所有整數皆為 **big-endian（網路位元組順序）**，無號整數。

```
位移  大小  欄位         型別     說明
 0     4    Length       uint32   Length 欄位「之後」的位元組數 = 10 + Payload 長度
 4     2    MessageType  uint16   訊息類型（第 4 節）
 6     4    RequestId    uint32   0 = 非請求 / 回應；請求由送出方配發非 0 值，回應填入相同值
10     4    Sequence     uint32   每條連線、每個方向從 1 開始，每個封包 +1
14     N    Payload      bytes    MessagePack（第 5 節）
```

| 項目 | 值 |
| --- | --- |
| 表頭（Length 之後）大小 | 10 位元組 |
| Payload 上限 | 1,048,576 位元組（1 MiB） |
| Length 合法範圍 | 10 ≤ Length ≤ 1,048,586 |
| 完整封包上限 | 1,048,590 位元組 |

**接收順序（必須遵守）**

1. 讀 4 位元組 Length。
2. **先驗證 Length 在合法範圍內**，不合法即關閉連線（不讀、不配置後續內容）。
3. 依 Length 配置緩衝區並讀滿。
4. 檢查 Sequence 是否等於預期值（第 3 節）。
5. 檢查 MessageType 是否為 V1 定義的類型，且允許在此連線角色與方向出現（第 4 節）。
6. 以安全設定反序列化 Payload（第 5 節），並做訊息層的語意檢查（長度、範圍）。

大於 1 MiB 的資料（例如大畫面、檔案）由上層切塊，每塊一個封包。

## 3. Sequence 與 RequestId

- **Sequence**：每條連線、每個方向各自獨立，第一個封包（Hello）為 1，之後每個封包 +1。接收端收到的值不等於預期值（跳號、重複、0）即為協定錯誤。達到 2³²−1 後須重新建立連線，不得回繞。
- **RequestId**：用於請求 / 回應配對。目前使用：

| 請求 | 回應 |
| --- | --- |
| LoginRequest | LoginResult |
| Heartbeat（控制端送） | Heartbeat（被控端回） |
| SessionTokenRequest | SessionTokenGrant |

  不屬於任何請求的訊息（例如登入成功後主動發出的 SessionTokenGrant、影像、錯誤）RequestId 為 0。

## 4. 訊息類型

`MessageType` 為 uint16。下表以外的值為**未知類型**。方向：C→H 控制端送被控端，H→C 反之。

| 值 | 類型 | 連線角色 | 方向 | 狀態 |
| --- | --- | --- | --- | --- |
| 1 | Hello | 全部 | 雙向 | 已實作 |
| 2 | LoginRequest | Control | C→H | 已實作 |
| 3 | LoginResult | Control | H→C | 已實作 |
| 4 | SessionTokenGrant | Control | H→C | 已實作 |
| 5 | SubConnectAuth | Video、File | C→H | 已實作 |
| 6 | Heartbeat | Control | 雙向 | 已實作 |
| 7 | SubConnectResult | Video、File | H→C | 已實作 |
| 8 | SessionTokenRequest | Control | C→H | 已實作 |
| 9 | SubConnectChallenge | Video、File | H→C | 已實作 |
| 10 | SessionStateChanged | Control | H→C | 規劃中 |
| 11 | PauseControl | Control | H→C | 規劃中 |
| 12 | ResumeControl | Control | 雙向 | 規劃中 |
| 20–25 | KeyEvent、MouseMoveEvent、MouseButtonEvent、MouseWheelEvent、InputHeartbeat、ReleaseAllInput | Control | C→H | 規劃中（B1） |
| 40 | VideoFrame | Video | H→C | 規劃中（A2） |
| 41 | CursorShape | Video | H→C | 規劃中（A3） |
| 42 | KeyframeRequest | Video | C→H | 已實作 |
| 43 | VideoTileUpdate | Video | H→C | 已實作（A1 PoC） |
| 50 | ClipboardText | Control | 雙向 | 規劃中 |
| 60–65 | FileListRequest、FileListReply、FileChunk、FileChunkAck、FileTransferCancel、FileTransferResult | File | 雙向 | 規劃中 |
| 200 | Error | 全部 | 雙向 | 已實作 |

**Hello 之前**：每條連線的第一個封包必須是 Hello；在雙方完成 Hello 前，只允許 Hello 與 Error。

**行為規則**

| 情況 | 處理 |
| --- | --- |
| 未知類型 | 回 `Error(UnknownMessageType)` 後關閉連線 |
| 已知類型但不允許在此角色 / 方向出現 | 回 `Error(ProtocolViolation)` 後關閉連線 |
| Sequence 錯誤 | 回 `Error(ProtocolViolation)` 後關閉連線 |
| Length 超出範圍 | 回 `Error(ProtocolViolation)`（盡力）後關閉連線 |
| Payload 反序列化失敗、含多餘位元組、語意檢查不過 | 回 `Error(ProtocolViolation)` 後關閉連線 |
| 已定義、但本版尚未實作的類型（表中「規劃中」） | 接收端忽略（實作後依規格處理） |
| 收到對方的 Error | 記錄後關閉連線 |

錯誤通知為「盡力」：送出 Error 失敗不影響關閉。

## 5. Payload（MessagePack）

- 每種訊息為固定 schema 的 sealed record，以整數鍵（陣列格式）序列化；**訊息中沒有 object / Typeless 欄位，只反序列化成指定型別，不接受任意型別**。
- 安全設定：`MessagePackSecurity.UntrustedData`（防雜湊碰撞攻擊），物件最大深度 **16**。
- 宣告長度大於剩餘資料的陣列 / 字串直接拒絕（不預先配置）。
- 反序列化後必須剛好用完 Payload，有多餘位元組即拒絕。
- 訊息層語意上限（反序列化後檢查）：

| 欄位 | 上限 |
| --- | --- |
| LoginRequest.Username | 256 字元 |
| LoginRequest.Password | 1024 字元 |
| Hello.Capabilities | 32 項，每項 64 字元 |
| SessionTokenGrant.Token、SubConnectChallenge.Nonce、SubConnectAuth.Mac | 恰好 32 位元組 |
| VideoTileUpdate 畫面寬高 | 1～8192 |
| VideoTile 寬高 | 1～256，且須在畫面內；解壓後大小須剛好等於寬×高×4 |

主要訊息欄位（鍵 0 起）：

| 訊息 | 欄位 |
| --- | --- |
| Hello | ProtocolVersion(int)、Role(byte：1 Control、2 Video、3 File)、Capabilities(string[]) |
| Error | Code(int：0 Unspecified、1 VersionMismatch、2 UnknownMessageType、3 ProtocolViolation)、Message(string) |
| LoginRequest | Username、Password |
| LoginResult | Success、Error(AuthError)、RetryAfterSeconds |
| SessionTokenGrant | Role、Token(32B)、ExpiresInSeconds |
| SessionTokenRequest | Role |
| SubConnectChallenge | Nonce(32B) |
| SubConnectAuth | Mac(32B) |
| SubConnectResult | Success、Error(AuthError) |
| Heartbeat | Timestamp(long，送出方自用) |
| KeyframeRequest | LastFrameId |
| VideoTileUpdate | FrameId、FrameWidth、FrameHeight、EndOfFrame、Tiles[]{X、Y、Width、Height、Data} |

AuthError：0 None、1 BadCredentials、2 TooManyAttempts、3 Busy、4 InvalidToken。

## 6. 版本協商

- 雙方第一個封包都是 Hello，帶 `ProtocolVersion` 與 `Capabilities`。
- **Hello 的分幀與欄位 0、1 在所有未來版本保持不變**，讓不同版本至少能辨識彼此版本。
- 被控端收到不同版本：回 `Error(VersionMismatch, "協定版本不相容…請更新兩端程式")` 後關閉。
- 控制端收到不同版本的 Hello 回覆：中止並顯示「協定版本不相容，請更新兩端程式」。
- Capabilities 目前兩端皆送空陣列；未來用來宣告選用功能（例如 `h264`），未知項目忽略。

## 7. 連線流程

### 7.1 Control

```
C → H  Hello(1, Control, [])                     Seq 1
H → C  Hello(1, Control, [])                     Seq 1
C → H  LoginRequest(帳號, 密碼)        RequestId=r Seq 2
H → C  LoginResult(...)               RequestId=r Seq 2
H → C  SessionTokenGrant(Video, token, 30)        Seq 3
H → C  SessionTokenGrant(File,  token, 30)        Seq 4
C → H  Heartbeat                      RequestId=h  每 2 秒
H → C  Heartbeat                      RequestId=h
```

雙方 6 秒未收到心跳（回覆）即結束工作階段。Control 結束 → 撤銷工作階段：token 全部作廢、關閉 Video 與 File。

### 7.2 Video / File 子連線（PLAN §7 Session Token 規格）

```
C → H  Hello(1, Video, [])
H → C  Hello(1, Video, [])
H → C  SubConnectChallenge(nonce：32 位元組亂數)
C → H  SubConnectAuth(mac)
H → C  SubConnectResult(Success, Error)
```

- `mac = HMAC-SHA256(key = token, data = nonce ‖ ASCII("RemoteTool-SubConnect-v1") ‖ 角色位元組)`。
- **token 只經由 Control 傳送一次，子連線上只出現 MAC**；nonce 每次不同，錄下的 MAC 無法重放。
- token：CSPRNG 256 位元；Video、File 各一枚、不可跨角色；**發出後 30 秒內**須使用；一次性；只存在記憶體並存放於工作階段物件中（綁定該帳號、該 Control 連線與被控端的 Windows 工作階段）；Control 斷線或重新登入時全部作廢。
- 子連線重連：先在 Control 上送 `SessionTokenRequest(角色)`，取得新的 `SessionTokenGrant`（同角色舊 token 立即作廢），再依上列流程連線。
- 驗證失敗（無 token、過期、MAC 不符、跨角色）一律回 `SubConnectResult(false, InvalidToken)`，並記錄來源 IP。
- **TLS exporter（RFC 9266）**：.NET 10 未提供公開 API（`SslStream` 只有 `tls-server-end-point` 與 TLS 1.3 不適用的 `tls-unique`），因此子連線授權未綁定到該條 TLS 交握；以 nonce + HMAC、30 秒期限、一次性、且子連線須通過相同主機指紋核對作為緩解（D-039）。

## 8. 安全限制摘要

| 項目 | 值 | 位置 |
| --- | --- | --- |
| 握手到登入完成時限 | 15 秒 | `HostServerOptions.HandshakeTimeout` |
| 未驗證連線上限 | 總共 32、每來源 4 | `HostServerOptions` |
| 同時密碼比對 | 2 | `HostServerOptions.MaxConcurrentPasswordChecks` |
| 登入節流 | 每來源 1、2、4…60 秒漸進延遲；全域 10 次突發、每秒 2 次 | `LoginThrottle` |
| 同時工作階段 | 1 | `HostServer` |
