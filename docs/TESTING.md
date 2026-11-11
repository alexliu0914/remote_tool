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

## 七、尚未能測的項目

實機驗收項目 G0-01～05 與 GUARD-01～05（擷取、編碼、輸入注入、HostGuard 監看與重啟等）
需等對應功能於 Spike 0 / M1 實作後，於兩台 Windows 11 實機上依規劃書進行。
