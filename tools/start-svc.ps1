& taskkill.exe /IM WinQuota.Service.exe /F 2>$null
Start-Sleep 2
try { Start-Service WinQuota -ErrorAction Stop; Write-Host 'START-OK' } catch { Write-Host ('START-FAIL: ' + $_.Exception.Message) }
Start-Sleep 5
& sc.exe query WinQuota | Select-String 'STATE'
# 配置故障自恢复（之前被 360 拦截未成功）
& sc.exe failure WinQuota reset= 86400 actions= restart/60000/restart/60000/restart/60000
& sc.exe qfailure WinQuota
