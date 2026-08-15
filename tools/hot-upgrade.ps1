$inst = 'D:\Program Files\WinQuota'
$pub = 'D:\_workspace\projects\WinQuota\publish\service'

& sc.exe stop WinQuota | Out-Null
Start-Sleep 2
& taskkill.exe /IM WinQuota.Service.exe /F 2>$null | Out-Null
Start-Sleep 1

Copy-Item "$pub\WinQuota.Service.exe" "$inst\WinQuota.Service.exe" -Force
if (Test-Path "$inst\wwwroot") { Remove-Item -Recurse -Force "$inst\wwwroot" }
Copy-Item -Recurse "$pub\wwwroot" "$inst\wwwroot"

Start-Service WinQuota
Start-Sleep 6
& sc.exe query WinQuota | Select-String 'STATE'
