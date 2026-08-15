$bin = 'D:\Program Files\WinQuota\WinQuota.Service.exe'
if (-not (Get-Service WinQuota -ErrorAction SilentlyContinue)) {
    New-Service -Name WinQuota -DisplayName 'WinQuota 防沉迷服务' -BinaryPathName $bin -StartupType Automatic -Description 'WinQuota 防沉迷：进程监控、每日额度、时间限制。停止或删除本服务将导致限制失效。'
}
& sc.exe description WinQuota 'WinQuota 防沉迷：进程监控、每日额度、时间限制。停止或删除本服务将导致限制失效。'
& sc.exe failure WinQuota reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service WinQuota
Get-Service WinQuota | Select-Object Status, Name | Format-Table -HideTableHeaders
