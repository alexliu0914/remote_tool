<#
.SYNOPSIS
  Spike 0 實測用（GUARD-04）：嘗試以「不是 HostGuard 啟動的程序」連上 HostGuard 的 IPC 管道。

.DESCRIPTION
  在時限內反覆嘗試連線。連上後送出一則白名單外的訊息，並檢查 HostGuard 是否關閉管道。
  判定以 hostguard.log 為準：應出現「拒絕 IPC 連線」或「存取被拒」；若 HostGuard 記錄顯示接受了這個連線，判 FAIL。

  管道名稱：在被控端 PowerShell 執行
    Get-CimInstance Win32_Process -Filter "Name='Host.exe'" | Select-Object ProcessId, CommandLine
  取 --guard-pipe 後面的值。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\PipeProbe.ps1 -PipeName RemoteTool.HostGuard.1.0123abcd... -Seconds 30
#>
param(
    [Parameter(Mandatory = $true)][string]$PipeName,
    [int]$Seconds = 30
)

Write-Host ("PipeProbe PID {0}，使用者 {1}，目標 \\.\pipe\{2}" -f $PID, $env:USERNAME, $PipeName)
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::Out)
    try {
        $client.Connect(200)
        Write-Host ("{0:HH:mm:ss.fff} 已連上管道，送出白名單外訊息…" -f (Get-Date))
        # [BodyLength=0][Type=0x77][Generation=0]
        $bytes = [byte[]](0, 0, 0x77, 0, 0, 0, 0)
        try {
            $client.Write($bytes, 0, $bytes.Length); $client.Flush()
            Start-Sleep -Milliseconds 1500
            $client.Write($bytes, 0, $bytes.Length); $client.Flush()
            Write-Host "管道仍可寫入。請檢查 hostguard.log：若沒有「拒絕 IPC 連線」，判 FAIL。"
        }
        catch {
            Write-Host ("{0:HH:mm:ss.fff} 管道已被關閉：{1}" -f (Get-Date), $_.Exception.Message)
        }
        return
    }
    catch [System.TimeoutException] {
        # 管道忙碌（Host.exe 正連線中）或尚未建立，繼續嘗試
    }
    catch {
        Write-Host ("{0:HH:mm:ss.fff} 無法連線：{1}：{2}" -f (Get-Date), $_.Exception.GetType().Name, $_.Exception.Message)
        return
    }
    finally {
        $client.Dispose()
    }
}
Write-Host "時限內未能連上（管道一直忙碌或不存在）。"
