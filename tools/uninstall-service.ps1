# WinQuota 服务卸载脚本（需管理员 PowerShell 运行）
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行本脚本"
}

& sc.exe stop WinQuota | Out-Null
Start-Sleep -Seconds 2
& sc.exe delete WinQuota | Out-Null
Write-Host "WinQuota 服务已卸载（数据库与日志保留在 %ProgramData%\WinQuota）。"
