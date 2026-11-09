<#
.SYNOPSIS
  Spike 0 實測用：記錄被控端上 Shift / Ctrl / Alt 與滑鼠按鈕的「按下／放開」時間戳（GUARD-01、GUARD-02、GUARD-05 證據）。

.DESCRIPTION
  每 20 ms 以 GetAsyncKeyState 讀取鍵盤狀態，狀態改變時印出並寫入記錄檔。
  遠端注入（HostGuard 的 SendInput）與本機實體按鍵都會反映在這裡。
  注意：被控端鎖定或出現 UAC 安全桌面時，本程式不在輸入桌面上，讀到的值一律為「放開」，那段期間的記錄不可當證據。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\KeyStateMonitor.ps1
#>
param(
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA ("RemoteTool\logs\keystate-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date)))
)

Add-Type -Namespace RemoteToolTest -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
'@

$keys = [ordered]@{
    'LShift'  = 0xA0; 'RShift' = 0xA1
    'LCtrl'   = 0xA2; 'RCtrl'  = 0xA3
    'LAlt'    = 0xA4; 'RAlt'   = 0xA5
    'LButton' = 0x01; 'RButton' = 0x02; 'MButton' = 0x04
}
$state = @{}
foreach ($k in $keys.Keys) { $state[$k] = $false }

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null
$header = "# KeyStateMonitor 開始 {0:yyyy-MM-dd HH:mm:ss.fff zzz}，電腦 {1}，使用者 {2}" -f (Get-Date), $env:COMPUTERNAME, $env:USERNAME
Write-Host $header
Write-Host "# 記錄檔：$LogPath（Ctrl+C 結束）"
Add-Content -Path $LogPath -Value $header -Encoding UTF8

while ($true) {
    foreach ($k in $keys.Keys) {
        $down = ([RemoteToolTest.Native]::GetAsyncKeyState($keys[$k]) -band 0x8000) -ne 0
        if ($down -ne $state[$k]) {
            $state[$k] = $down
            $line = "{0:yyyy-MM-dd HH:mm:ss.fff} {1,-8} {2}" -f (Get-Date), $k, $(if ($down) { 'DOWN' } else { 'UP' })
            Write-Host $line
            Add-Content -Path $LogPath -Value $line -Encoding UTF8
        }
    }
    Start-Sleep -Milliseconds 20
}
