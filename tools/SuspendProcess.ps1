<#
.SYNOPSIS
  Spike 0 實測用（GUARD-01「Host 無回應」）：延遲後暫停指定程序的所有執行緒一段時間，再恢復。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\SuspendProcess.ps1 -Name Host -DelaySeconds 20 -SuspendSeconds 15
#>
param(
    [string]$Name = 'Host',
    [int]$DelaySeconds = 20,
    [int]$SuspendSeconds = 15
)

Add-Type -Namespace RemoteToolTest -Name Proc -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
'@

$proc = Get-Process -Name $Name -ErrorAction Stop | Select-Object -First 1
Write-Host ("{0:HH:mm:ss.fff} {1} 秒後暫停 {2}（PID {3}）{4} 秒" -f (Get-Date), $DelaySeconds, $Name, $proc.Id, $SuspendSeconds)
Start-Sleep -Seconds $DelaySeconds

$handle = [RemoteToolTest.Proc]::OpenProcess(0x0800, $false, $proc.Id) # PROCESS_SUSPEND_RESUME
if ($handle -eq [IntPtr]::Zero) { throw "OpenProcess 失敗" }
try {
    [void][RemoteToolTest.Proc]::NtSuspendProcess($handle)
    Write-Host ("{0:HH:mm:ss.fff} 已暫停（模擬 Host 無回應）" -f (Get-Date))
    Start-Sleep -Seconds $SuspendSeconds
    [void][RemoteToolTest.Proc]::NtResumeProcess($handle)
    Write-Host ("{0:HH:mm:ss.fff} 已恢復執行" -f (Get-Date))
}
finally {
    [void][RemoteToolTest.Proc]::CloseHandle($handle)
}
