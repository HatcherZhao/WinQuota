# 安装与部署

## 构建

要求 .NET 10 SDK 与 Node.js（构建管理界面）。

```bash
# 前端（修改 src/WinQuota.Web 后需要重新构建，产物自动进入服务 wwwroot）
cd src/WinQuota.Web && npm install && npm run build && cd ../..

dotnet build WinQuota.slnx
dotnet test WinQuota.slnx
```

## 打包安装程序（推荐分发方式）

```bash
# 1. 前端
cd src/WinQuota.Web && npm install && npm run build && cd ../..
# 2. 发布自包含单文件（目标机器无需安装 .NET）
dotnet publish src/WinQuota.Service -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/service
dotnet publish src/WinQuota.Tray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/tray
# 3. NSIS 安装包（注意：installer.nsi 需 UTF-8 BOM 编码）
cd tools && makensis installer.nsi
# 产物：dist/WinQuota-Setup-<版本>.exe
```

安装包包含：后台服务（含网页管理界面）、托盘程序、开始菜单快捷方式、卸载器；安装时可选“托盘开机自启”，服务自动注册并配置故障自恢复。升级安装为原位更新服务，减少安全软件触发面。

## 部署为 Windows 服务

推荐使用安装脚本（自动配置故障自恢复策略）：

```bash
dotnet publish src/WinQuota.Service -c Release -r win-x64 --self-contained false -o C:\WinQuota
# 管理员 PowerShell：
powershell -ExecutionPolicy Bypass -File tools\install-service.ps1 -InstallDir C:\WinQuota
# 卸载：
powershell -ExecutionPolicy Bypass -File tools\uninstall-service.ps1
```

手动方式（不带自恢复配置）：

```bash
sc create WinQuota binPath= "C:\WinQuota\WinQuota.Service.exe" start= auto obj= LocalSystem
sc start WinQuota
```

## 数据与日志

- 数据库默认位于 `%ProgramData%\WinQuota\winquota.db`（可用 `WINQUOTA_DB` 环境变量或 `--db` 覆盖），完整性密钥位于同目录 `winquota.db.key`（勿删改；数据目录已被 ACL 限制为 SYSTEM/管理员）
- 日志位于 `%ProgramData%\WinQuota\logs\`（滚动保留 14 天）
- 直接运行 exe 即为控制台模式（便于调试），命令行参数则为 CLI 管理模式（见 [cli.md](cli.md)）

## 配置项

通过环境变量覆盖（`WinQuota__` 前缀）或 appsettings.json：

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `WinQuota__ApiPort` | 58390 | 管理界面 HTTP 端口（仅绑定 127.0.0.1） |
| `WinQuota__ScanIntervalSeconds` | 5 | 进程扫描与计时周期（秒） |
| `WinQuota__FlushIntervalSeconds` | 30 | 用量落盘周期（秒） |
| `WinQuota__IdleThresholdSeconds` | 300 | 整机规则空闲判定阈值（秒），0 = 不启用 |
| `WinQuota__ComputerExhaustedAction` | Lock | 整机耗尽动作：Lock 锁定工作站 / NotifyOnly 仅提醒 |
| `WinQuota__ExtensionGraceSeconds` | 300 | 耗尽但有剩余延期次数时的宽限时长（秒），下限 30 |
| `WinQuota__ExhaustedNotifyThrottleSeconds` | 60 | 耗尽提示最小重复间隔（秒） |

例如：`WinQuota__IdleThresholdSeconds=600`、`WinQuota__ComputerExhaustedAction=NotifyOnly`。

> 注意：以管理员身份在控制台直接运行时，锁屏状态下无法获取用户会话令牌（错误 1314，WTSQueryUserToken 需要 SE_TCB 特权），此时 Toast / 锁定动作会降级为日志。安装为 LocalSystem 服务则无此限制，推荐以服务方式部署。
