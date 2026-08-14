# WinQuota 服务安装脚本（需管理员 PowerShell 运行）
# 用法：
#   1. 先发布：dotnet publish src/WinQuota.Service -c Release -r win-x64 --self-contained false -o C:\WinQuota
#   2. 再执行：powershell -ExecutionPolicy Bypass -File tools\install-service.ps1 -InstallDir C:\WinQuota
param(
    [string]$InstallDir = "C:\WinQuota"
)

$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行本脚本"
}

$binPath = Join-Path $InstallDir "WinQuota.Service.exe"
if (-not (Test-Path $binPath)) {
    throw "找不到 $binPath，请先执行 dotnet publish"
}

if (Get-Service WinQuota -ErrorAction SilentlyContinue) {
    Write-Host "服务已存在，先卸载旧服务..." -ForegroundColor Yellow
    & sc.exe stop WinQuota | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete WinQuota | Out-Null
    Start-Sleep -Seconds 2
}

& sc.exe create WinQuota binPath= "`"$binPath`"" start= auto obj= LocalSystem DisplayName= "WinQuota 防沉迷服务" | Out-Null
& sc.exe description WinQuota "WinQuota 防沉迷：进程监控、每日额度、耗尽限制。删除或停止本服务将导致限制失效。" | Out-Null

# 故障自恢复：异常退出后自动重启（60s 内最多 3 次），即使被强制结束也会被 SCM 拉起
& sc.exe failure WinQuota reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# 服务 ACL 加固：DACL 保护（P），仅 SYSTEM 与管理员有完全控制，
# 受限用户无法 sc stop / sc config / sc delete 本服务
& sc.exe sdset WinQuota "D:P(A;;GA;;;SY)(A;;GA;;;BA)" | Out-Null

# 数据目录 ACL 加固：仅 SYSTEM 与管理员可访问（按 SID 指定，不受系统语言影响）。
# 数据库与完整性密钥（winquota.db.key）不给受限用户任何权限，防直改数据 / 伪造签名。
# 注意：仅加固默认数据目录；若用 WINQUOTA_DB 指定了其它位置，请自行设置相应 ACL。
$dataDir = Join-Path $env:ProgramData "WinQuota"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
& icacls.exe $dataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" /grant:r "*S-1-5-32-544:(OI)(CI)F" | Out-Null

& sc.exe start WinQuota | Out-Null
Write-Host "WinQuota 服务已安装并启动。" -ForegroundColor Green
Write-Host "管理界面：http://127.0.0.1:58390/"
Write-Host "托盘程序如需开机自启，运行一次 WinQuota.Tray.exe 后在托盘菜单中开启。"
