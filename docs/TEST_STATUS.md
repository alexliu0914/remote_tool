# 驗收追蹤

> 狀態定義與規則見 [ACCEPTANCE_CRITERIA.md](ACCEPTANCE_CRITERIA.md)。
> **第 3 節測試紀錄只能新增，不得刪除或改寫**（包含失敗紀錄）。

## 1. Gate 狀態

| Gate | 狀態 | 已驗證 | 尚缺 |
| --- | --- | --- | --- |
| G0-01 | **PARTIAL_PASS** | 單機（127.0.0.1）Windows 11：TLS 1.3、首次指紋核對、指紋變更中止、帳密登入、失敗鎖定、三條連線、Busy、斷線偵測（紀錄 T-002、T-003） | **兩台實體 Win11 直接 IP 連線**（TESTING.md 第五節）。未完成前不得標 PASS |
| G0-02 | IN_PROGRESS | — | A1 畫面路徑開發中；H.264（A2）、鍵鼠（B1）未開始 |
| G0-03 | NOT_STARTED | — | 全部 |
| G0-04 | NOT_STARTED | — | 全部（依 D-028，於 A2 導入 HwndHost 後驗證） |
| G0-05 | NOT_STARTED | — | 全部 |
| G0-06 | NOT_STARTED | — | 研究項目，不阻擋 MVP |
| G0-07 | NOT_STARTED | — | 研究項目，不阻擋 MVP |

## 2. HostGuard 狀態

| ID | 狀態 | 備註 |
| --- | --- | --- |
| GUARD-01 | NOT_STARTED | B1 |
| GUARD-02 | NOT_STARTED | B1 |
| GUARD-03 | NOT_STARTED | B1 |
| GUARD-04 | NOT_STARTED | B1 |
| GUARD-05 | NOT_STARTED | B1 + 工作階段階段 |

## 3. 測試紀錄（只增不刪）

| # | 日期 | Commit | 環境 | 執行者 | 範圍 | 結果 | 證據 / 備註 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T-001 | 2026-11-11 | 44c45a6 | 雲端 Linux（Ubuntu 24.04, .NET 8.0.134） | Claude | 骨架：協定單元測試 7 項、建置、三程式啟動 | PASS | 7/7；三程式印出骨架訊息 |
| T-002 | 2026-11-11 | c4b55e0 | 雲端 Linux | Claude | 自動測試：Protocol 20 項、Connection 23 項（127.0.0.1 真實 TLS 1.3） | PASS | 43/43，連續 3 次；另以主控台程式實跑：首次信任、已知主機、錯誤密碼、Busy、Host 關閉、憑證更換警告、`--forget` 皆符合預期 |
| T-003 | 2026-11-11 | c4b55e0 | 使用者 Windows 11 實機，單機（127.0.0.1） | 使用者 | TESTING.md 4.4 單機實測第 1–12 項 | PASS | 使用者回報 12 項全部通過。過程中曾因執行舊版建置（未切分支、未重新建置）看到骨架訊息，更新後正常，非程式缺陷 |

## 4. 尚待人工執行

| 項目 | 對應 | 需要 | 步驟 |
| --- | --- | --- | --- |
| 雙機連線驗證 | G0-01 | 兩台實體 Windows 11、同一區域網路 | [TESTING.md](TESTING.md) 第五節（4.4 的 12 項 + 5.5 的 2 項） |
