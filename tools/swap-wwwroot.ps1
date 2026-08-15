$dst = 'D:\Program Files\WinQuota\wwwroot'
$src = 'D:\_workspace\projects\WinQuota\src\WinQuota.Web\dist'
# 静态文件热替换，无需重启服务（不影响正在运行的游戏计时）
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
Copy-Item -Recurse $src $dst
Get-ChildItem $dst\assets | Select-Object -ExpandProperty Name
