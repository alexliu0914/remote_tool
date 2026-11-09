<#
.SYNOPSIS
  Spike 0 實測用：延遲後強制結束指定程序並印出時間戳（GUARD-01、GUARD-03、GUARD-05）。

.DESCRIPTION
  延遲執行可避免測試者在「遠端正按住 Shift / Ctrl / 滑鼠左鍵」時還要用實體鍵鼠操作工作管理員（那會干擾按鍵狀態）。
  -Repeat 大於 1 時，每次都先等程序重新出現（HostGuard 重啟 Host.exe）再結束它，用來測重啟政策。

.EXAMPLE
  # 20 秒後結束 Host.exe 一次
  powershell -ExecutionPolicy Bypass -File tools\DelayedKill.ps1 -Name Host -DelaySeconds 20
.EXAMPLE
  # 連續 6 次：每次 Host.exe 重新出現 2 秒後結束（GUARD-03）
  powershell -ExecutionPolicy Bypass -File tools\DelayedKill.ps1 -Name Host -DelaySeconds 2 -Repeat 6
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$DelaySeconds = 20,
    [int]$Repeat = 1,
    [int]$WaitForProcessSeconds = 60
)

for ($i = 1; $i -le $Repeat; $i++) {
    $deadline = (Get-Date).AddSeconds($WaitForProcessSeconds)
    $proc = $null
    while (-not $proc -and (Get-Date) -lt $deadline) {
        $proc = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $proc) {
        Write-Host ("{0:HH:mm:ss.fff} 第 {1} 次：{2} 沒有在 {3} 秒內出現，停止。" -f (Get-Date), $i, $Name, $WaitForProcessSeconds)
        break
    }
    Write-Host ("{0:HH:mm:ss.fff} 第 {1} 次：{2} PID {3}，{4} 秒後強制結束" -f (Get-Date), $i, $Name, $proc.Id, $DelaySeconds)
    Start-Sleep -Seconds $DelaySeconds
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Host ("{0:HH:mm:ss.fff} 第 {1} 次：已強制結束 PID {2}" -f (Get-Date), $i, $proc.Id)
    Start-Sleep -Milliseconds 300
}
