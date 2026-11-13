# 架構決議紀錄

> 已核准的架構決議。**新決議只能新增，不刪除舊決議**；推翻舊決議時新增一筆並在舊決議標註「已被 D-xxx 取代」。
>
> **規劃書原文 [plan/PLAN_V2.2.2.md](plan/PLAN_V2.2.2.md) 為最高依據（D-034）。** 本表與原文衝突時以原文為準，衝突項目見 [PLAN_GAPS.md](PLAN_GAPS.md)。
>
> 來源代號：**PLAN** = 規劃書 V2.2.2 基線（D-001～D-015 撰寫時原文尚未入庫，取自 README 與程式註解；已於 2026-11-11 對照原文標註）；
> **USER** = 使用者於對話中核准；**DEV** = 開發中提出、尚待使用者確認（確認後改為 USER）。

| ID | 日期 | 決議 | 來源 | 狀態 |
| --- | --- | --- | --- | --- |
| D-001 | 2026-11-09 | 點對點直接 IP 連線，不使用雲端、中繼或協調伺服器 | PLAN | 有效 |
| D-002 | 2026-11-09 | 平台僅 Windows 11 x64 ↔ Windows 11 x64，.NET 8 | PLAN | 已被 D-036 取代（.NET 10） |
| D-003 | 2026-11-09 | 被控端全部以一般使用者權限執行；不使用 Windows 服務、不使用 SYSTEM | PLAN | 有效 |
| D-004 | 2026-11-09 | 三個元件：Controller（控制端）、Host（被控端主程式）、HostGuard（被控端守護），另有跨平台共用庫 | PLAN | 有效 |
| D-005 | 2026-11-09 | HostGuard 是**唯一**呼叫 SendInput 的程式；Host 驗證遠端輸入後經本機 Named Pipe 交給 HostGuard。HostGuard 不連網、不讀剪貼簿、不處理畫面或檔案。注入與按鍵狀態表在同一執行緒更新 | PLAN | 有效 |
| D-006 | 2026-11-09 | HostGuard 監看 Host 的 process handle，異常結束後先釋放仍按住的鍵，再依重啟政策（指數退避、10 分鐘內最多 5 次）重啟 | PLAN | 有效 |
| D-007 | 2026-11-09 | Named Pipe IPC 白名單：限同使用者與工作階段，只接受 HostGuard 自己啟動的 Host.exe | PLAN | 有效 |
| D-008 | 2026-11-09 | 三條 TLS 連線共用 TCP 52100：Control（握手、登入、心跳、鍵鼠、剪貼簿、螢幕清單、品質調整）、Video（H.264 與游標）、File（限速檔案傳輸）。Control 中斷撤銷整個工作階段；Video / File 中斷各自獨立重連 | PLAN | 有效 |
| D-009 | 2026-11-09 | 封包格式 `[Length 4B big-endian][Channel 1B][Type 1B][Payload]`，Payload 上限 1 MB，超過即拒絕 | PLAN | 已被 D-038 取代（Protocol V1 分幀） |
| D-010 | 2026-11-09 | 訊息內容以 MessagePack 序列化，每種訊息固定 schema；拒絕未知類型、超長或格式錯誤資料 | PLAN | 有效；安全設定與 Capabilities 見 D-038 |
| D-011 | 2026-11-09 | TLS 1.3；被控端自簽憑證；控制端首次連線核對 SHA-256(SubjectPublicKeyInfo) 指紋 | PLAN | 有效 |
| D-012 | 2026-11-09 | 登入後以 Session Token 開 Video / File 子連線，token 為一次性 | PLAN | 有效 |
| D-013 | 2026-11-09 | Controller 使用 WPF；畫面區以 HwndHost 嵌入 D3D11，鍵鼠與拖放由該 Win32 視窗處理（G0-04 驗證，失敗則驗證替代方案） | PLAN | 有效 |
| D-014 | 2026-11-09 | 檔案傳輸以 256 KB 區塊加區塊雜湊，完成後全檔 SHA-256 驗證 | PLAN | 有效 |
| D-015 | 2026-11-09 | 輸入有按鍵按住時定期送 InputHeartbeat，逾時則釋放；ResumeControl 攜帶新的輸入世代編號以丟棄舊事件 | PLAN | 有效 |
| D-016 | 2026-11-11 | 指紋核對在 TLS 握手當下進行，**送出帳密之前**完成；陌生主機需使用者確認，指紋不符即中止且不自動覆寫記錄 | DEV→USER | 有效；信任改以公鑰識別（D-045） |
| D-017 | 2026-11-11 | 憑證為 ECDSA P-256 自簽、有效 20 年，存於 `%LOCALAPPDATA%\RemoteTool\host-cert.pfx`；控制端不檢查憑證鏈、名稱與到期日，只比對指紋 | DEV→USER | 有效；私鑰檔改以 DPAPI 保護（D-046） |
| D-018 | 2026-11-11 | 帳密以 PBKDF2-SHA256（310,000 次、16 位元組鹽）雜湊保存；同來源 IP 5 分鐘內失敗 5 次暫時鎖定；每次失敗延遲 500 ms；帳號錯誤與密碼錯誤回應相同 | DEV→USER | 已被 D-037（Argon2id）與 D-041（漸進延遲）取代 |
| D-019 | 2026-11-11 | 一次性 token 為 32 位元組亂數，綁定通道（Video token 不能開 File）；每用掉一個即經 Control 補發一個供重連；Control 結束時全部作廢 | DEV→USER | 已被 D-039 取代（30 秒、重新申請、nonce + HMAC） |
| D-020 | 2026-11-11 | 同時只允許一個控制端工作階段；第二個登入回應 Busy | DEV→USER | 有效 |
| D-021 | 2026-11-11 | Control 心跳：控制端每 2 秒送出，雙方 10 秒未收到即結束工作階段 | DEV→USER | 已被 D-038 取代（6 秒） |
| D-022 | 2026-11-11 | 每條連線第一個封包為 Hello（協定版本 + 通道），版本不同即回 Error 並關閉 | DEV→USER | 有效；V1 加入 Capabilities 與 Error 錯誤碼（D-038） |
| D-023 | 2026-11-11 | 連線層（`RemoteTool.Connection`）與協定庫保持跨平台（net8.0），以便在 CI 與 Linux 上用真實 TLS 做整合測試；Windows 專屬功能放在應用程式專案 | DEV→USER | 有效 |
| D-024 | 2026-11-11 | 開發順序：A1 DXGI 擷取 + 基本影像傳輸 + WPF 顯示 → A2 H.264 硬體編解碼（含軟體備援）→ B1 HostGuard IPC、注入、狀態追蹤與異常復原 → A3 多螢幕、游標、高 DPI、解析度切換 → 工作階段 / 鎖定 / UAC 安全暫停與重連 → 雙向檔案拖拉 PoC → Spike 0 完整驗收 | USER | 有效 |
| D-025 | 2026-11-11 | 雙機驗證可延後、不阻擋開發，但必須在 Spike 0 最終驗收前完成；未執行的雙機測試不得以單機結果代替 | USER | 有效 |
| D-026 | 2026-11-11 | A1 可用未壓縮或簡單壓縮畫面證明整條路徑，但須限制解析度、FPS 與佇列大小；僅作 PoC，不作正式效能基線 | USER | 有效 |
| D-027 | 2026-11-11 | A1 影像 PoC 格式：畫面切成 64×64 區塊，只送與上一張不同的區塊，每塊以 Deflate 壓縮（`VideoTileUpdate`，類型 43）。預設上限：寬 1920（超過以整數倍縮小）、10 FPS；**不設佇列**，傳送未完成時不擷取下一張（永遠送最新畫面）。A2 改為 H.264 後此格式保留作為除錯備援或移除 | DEV | 待確認 |
| D-028 | 2026-11-11 | A1 控制端顯示用 WPF `WriteableBitmap`；HwndHost + D3D11 於 A2（搭配 H.264 解碼）導入，G0-04 以該版本驗證 | DEV | 已被 D-054 取代（WriteableBitmap 保留為備援） |
| D-029 | 2026-11-11 | A1 只擷取主螢幕，不含游標；多螢幕與游標於 A3 | DEV | 待確認 |
| D-030 | 2026-11-11 | DXGI / D3D11 以 Vortice.Windows（Vortice.Direct3D11、Vortice.DXGI）封裝呼叫，不自行撰寫 COM interop | DEV | 待確認 |
| D-031 | 2026-11-11 | A1 起 Controller 改為 WPF 視窗程式（登入視窗 + 遠端畫面視窗），取代 M1 第一步的主控台版本；`--forget` 改為登入視窗上的按鈕 | DEV | 待確認 |
| D-032 | 2026-11-11 | 首次連線的指紋確認在一條「探測連線」上進行；使用者確認後關閉它，以已釘選的指紋重新連線再送帳密。確認期間暫停控制端逾時，避免使用者核對太久觸發被控端 15 秒握手時限 | DEV | 已被 D-035 取代 |
| D-033 | 2026-11-11 | DXGI 擷取時列舉顯示卡，選擇接著主螢幕的那張建立 D3D11 裝置（雙顯卡筆電的預設顯示卡不一定接著螢幕） | DEV | 待確認 |
| D-034 | 2026-11-11 | 規劃書 V2.2.2 原文入庫於 `docs/plan/PLAN_V2.2.2.md`，為最高依據；其他文件與原文不一致時以原文為準，差異記於 `docs/PLAN_GAPS.md` | USER | 有效 |
| D-035 | 2026-11-11 | 首次連線改由介面分兩步：`ProbeFingerprintAsync` 只做 TLS 握手取得指紋並關閉連線 → 使用者核對（此時沒有任何進行中的連線或計時器）→ 記住指紋後才呼叫 `ConnectAsync` 登入。原因：D-032 作法在 Windows 上核對超過 30 秒仍失敗（T-005） | DEV | 待確認 |
| D-036 | 2026-11-11 | 整個方案升級為 .NET 10 LTS（net10.0 / net10.0-windows），`global.json` 指定 SDK 10.0.100 以上；以 `X509CertificateLoader` 取代過時的憑證建構子 | USER | 有效（PLAN_GAPS G-09） |
| D-037 | 2026-11-11 | 密碼以 Argon2id 雜湊，套件 Konscious.Security.Cryptography.Argon2 1.3.1。評估：MIT 授權、2024-06-19 發佈、在 net10.0 使用其 net8.0 版、相依 Blake2 1.1.1（net8+ 無相依）與 System.Memory 4.5.4、NuGet 無已知弱點、輸出符合 RFC 9106 Argon2id 測試向量。每組密碼獨立 16 位元組隨機鹽；參數 19456 KiB / 2 次 / 平行度 1（雲端實測驗證約 130 ms）；載入與驗證時檢查參數範圍（8 MiB～1 GiB、次數 1～16、平行度 1～16）；最短 10 字元；明文密碼與衍生值用後清零，不記錄 | USER | 有效（G-02） |
| D-038 | 2026-11-11 | Protocol V1：所有連線共用分幀 `[Length u32][MessageType u16][RequestId u32][Sequence u32]` + MessagePack，big-endian；Length = 10 + Payload，Payload ≤ 1 MiB，先驗證 Length 再配置；Sequence 每方向從 1 逐一遞增；未知類型回 Error(UnknownMessageType) 後關閉；類型依角色與方向限制；MessagePack 使用 UntrustedData、深度 16、只反序列化成固定型別；Hello 帶 Capabilities，版本不符回 Error(VersionMismatch)；心跳 2 秒 / 6 秒逾時。完整規格見 PROTOCOL_V1.md | USER | 有效（G-08、G-05） |
| D-039 | 2026-11-11 | 子連線授權：token 只經 Control 傳送；子連線上被控端送 32 位元組 nonce，控制端回 HMAC-SHA256(token, nonce ‖ 標籤 ‖ 角色)；token 30 秒內有效、一次性、限角色，存於工作階段物件（綁定帳號、Control 連線、Windows 工作階段）；重連以 SessionTokenRequest 重新申請。**TLS exporter（RFC 9266）調查結果：.NET 10 無公開 API，無法綁定 TLS 交握**，以 nonce + HMAC、30 秒、一次性與相同主機指紋核對緩解 | DEV（依 PLAN §7） | 有效（G-07） |
| D-040 | 2026-11-11 | GUARD 驗收規格：以規劃書第十節為主要基準，合併對話版；保留規劃書 GUARD-01～05 原意，對話版以 CONV-GUARD-01～05 保留原 ID 並對應到子案例 01-a、01-b、03-a、02-a、05-a～05-d | USER | 有效（G-18） |
| D-041 | 2026-11-11 | 登入節流改依規劃書：每來源漸進延遲 1、2、4…秒（上限 60 秒），等待期間的嘗試直接拒絕且不延長；不採鎖定；全域 token bucket（突發 10、每秒 2）；同時 Argon2 比對 2；未驗證連線上限總共 32、每來源 4；15 分鐘無失敗即忘記該來源 | DEV（依 PLAN §6） | 有效（G-01） |
| D-042 | 2026-11-11 | Argon2id 參數依實測維持 19456 KiB / 2 次 / 平行度 1：使用者 Windows 11 實機建立雜湊 253 ms（T-011）、雲端 134 ms。理由：此組合已是常見建議的最低強度，不宜再降；登入等待約 0.25 秒對使用者無感；若提高，每次比對的記憶體與時間會放大登入洪水攻擊的資源消耗（目前同時比對上限 2，約 38 MiB）。日後若改變硬體或威脅評估再調整，調整需新增決議 | DEV（依實測） | 有效 |
| D-043 | 2026-11-12 | 減少大型暫存陣列（V-12）：`FrameStream` 每條連線保留一個送出緩衝區，MessagePack 直接序列化到其中再回填表頭；控制端 Video 連線開啟 `ReuseReadBuffer`（Payload 只在回呼期間有效）；重送完整畫面不再複製整張畫面。雲端 30 秒整張變動測試：gen2 GC 559 → 10 次，工作集由擺盪改為持平；受控堆積兩版皆無洩漏 | DEV | 有效（Windows 實機 V-12 持平，T-020） |
| D-044 | 2026-11-12 | `VideoTileUpdate` 新增 `IsKeyframe`（鍵 5），控制端以此確認「重新整理畫面」完成並在狀態列顯示；工具列「符合視窗大小」核取方塊改為會顯示目前模式的按鈕（V-04、V-05） | DEV | 待確認 |
| D-045 | 2026-11-12 | 主機信任以**公鑰（SPKI 指紋）**識別（PLAN §6、G-06）：已信任的公鑰不論位址都直接信任；另記「位址 → 上次指紋」作提示，同位址出現**未信任**的新金鑰判定為變更並拒絕；「移除指紋記錄」移除該公鑰及所有指向它的位址；舊格式自動轉換 | DEV（依 PLAN §6） | 有效 |
| D-046 | 2026-11-12 | `host-cert.pfx`、`host-credentials.json`、`known-hosts.json` 以 DPAPI（CurrentUser，額外熵為檔名）保護，檔頭 `RTSEC1:DPAPI`；舊明文檔載入後自動改存（金鑰不變、指紋不變）；寫入先寫暫存檔再取代。非 Windows（CI / 開發）使用不加密的 PLAIN 並於檔頭標示。套件 System.Security.Cryptography.ProtectedData 10.0.15 | DEV（依 PLAN §4、§6，G-03） | 有效 |
| D-047 | 2026-11-12 | 子連線獨立重連（PLAN §7、NET-03）：Video / File 中斷時 Control 保持，控制端以 SessionTokenRequest 取得新 token 後重連，退避 1、2、4…秒（上限 16 秒），30 秒內未成功顯示失敗，可手動重試；Video 重連期間畫面變暗標示「非即時」；任一子連線中斷不再結束工作階段。Host 測試專用開關 `--test-drop-video` / `--test-drop-file` 供實機驗證 | DEV（依 PLAN §7） | 有效 |
| D-048 | 2026-11-12 | 多組帳號（G-04）：帳密檔 `{ Version, Accounts[] }`；帳號區分大小寫、不可重複；不存在的帳號仍以第一組參數完整計算雜湊（時間一致）；成功登入在 Host 記錄帳號（稽核），失敗不記錄輸入的帳號；舊單一帳號檔自動轉換 | DEV（依 PLAN §6） | 有效 |
| D-049 | 2026-11-12 | A2 分兩步：A2a 先以 Media Foundation **同步軟體 MFT**（Windows 內建 Microsoft H.264 編碼器 / 解碼器）打通 DXGI → H.264 → 顯示，色彩轉換 BGRA↔NV12（BT.709 有限範圍）在 CPU 進行，顯示仍為 WriteableBitmap；A2a-2 加入硬體編碼（非同步 MFT、事件驅動）並在啟動時優先探測；A2b 導入 HwndHost + D3D11 與硬體解碼。理由：硬體非同步 MFT 複雜度高且無法在雲端執行，先在實機確認基本管線 | DEV | 待確認 |
| D-050 | 2026-11-12 | 影像格式協商：控制端在 Video 連線 Hello 宣告 `video.h264`（解碼器可用時）與 `video.tiles`；被控端在控制端支援且編碼器可用時送 H.264（VideoFrame，分塊 ≤ 512 KB、每張 ≤ 64 塊、≤ 16 MiB），否則送區塊差異。編碼器在串流中故障時，該連線改用區塊差異並送完整畫面；控制端兩種格式都能處理、寫入同一畫布。H.264 預設 30 FPS、6000 kbps、CBR、低延遲、無 B 幀、GOP 2 秒；`--codec`、`--bitrate`、`--fps` 可調 | DEV | 待確認 |
| D-051 | 2026-11-12 | PLAN §5.1「畫面落後時清空待送佇列並要求 IDR」：因採零佇列（一張送完才擷取下一張）且每張編碼結果都完整送出，參考鏈不會因丟幀損壞，此情境不會發生；落後時以降低擷取頻率處理。解碼失敗時控制端每秒最多要求一次關鍵幀（PLAN_GAPS G-10） | DEV | 待確認 |
| D-052 | 2026-11-12 | 硬體 H.264 編碼（A2a-2）：列舉硬體非同步 MFT，解鎖（MF_TRANSFORM_ASYNC_UNLOCK）後以專用執行緒接收 NeedInput / HaveOutput 事件，輸入為系統記憶體 NV12（暫不使用 D3D11 裝置管理員）。`ResilientH264Encoder`：Auto 模式硬體優先，硬體在建立或串流中失敗時改用軟體（不退回區塊差異）；Host 啟動時實際試編一張畫面探測。`--codec auto|h264|h264-hw|h264-sw|tiles` | DEV（依 PLAN §4） | 有效（Intel Quick Sync 實機通過，T-030、T-032） |
| D-053 | 2026-11-12 | 編碼輸出記憶體（HW-06）：`EncodedFrame` 改為借自 ArrayPool 的緩衝區 + 長度，`VideoStreamer` 送出後歸還；`VideoFrame.Data` 改為 ReadOnlyMemory 以切片分塊不複製（MessagePack 線路格式仍為 bin，Protocol V1 不變）；接收端組裝時複製。`--memlog` 區分 GC 已提交與原生記憶體 | DEV | 有效（HW-06 原生記憶體持平，T-032） |
| D-054 | 2026-11-12 | A2b 畫面區：`RemoteScreenHost`（HwndHost）建立 Win32 子視窗（視窗類別以 DefWindowProc 註冊，訊息由 HwndHost.WndProc 處理），D3D11 裝置 + DXGI flip-discard 交換鏈，Direct2D 縮放繪製（符合視窗：高品質三次插值；原尺寸：最近點並以 WPF 捲軸捲動），裝置遺失自動重建。重連覆蓋訊息以 DirectWrite 畫在 D3D 畫面內、重試按鈕移到工具列（WPF 無法疊在 HwndHost 上）。滑鼠 / 鍵盤由子視窗 WndProc 接收（WM_GETDLGCODE 要求所有按鍵），拖放以 OLE `RegisterDragDrop` + `IDropTarget` 讀取 CF_HDROP；座標依繪製位置換算為遠端座標。A2b 只顯示收到的輸入作為 G0-04 驗證，轉送於 B1、檔案傳輸於 M3。D3D11 無法建立時改用 WPF WriteableBitmap。硬體解碼（DXVA）於 A2b-2 | DEV（依 PLAN §4、D-013） | 待實機確認（G0-04） |

