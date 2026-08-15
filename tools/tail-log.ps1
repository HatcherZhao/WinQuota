$f = Get-ChildItem 'C:\ProgramData\WinQuota\logs\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ('log: ' + $f.Name)
Get-Content $f.FullName | Select-String 'FOXWQ|宽限|剩余|耗尽|终止|通知' | Select-Object -Last 25 | ForEach-Object { $_.Line }
