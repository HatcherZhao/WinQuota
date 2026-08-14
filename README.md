# WinQuota — Windows 防沉迷时间配额管理

Windows 10 / 11 本地防沉迷工具：限制**指定应用（游戏）**的每日使用时长，额度耗尽后自动关闭并当天禁止再次启动，次日自动恢复。完整背景与规划见《Windows 防沉迷管理软件实施计划.md》。

## 架构

```text
WinQuota.Service（.NET 10 WebApplication + Worker Service，本项目核心）
├── ToolhelpProcessScanner   Toolhelp32 快照进程扫描（纯 Win32，无 COM）
├── RuleMatcher              进程名匹配 + 按需完整路径 / 产品 / 数字签名匹配
├── FileSignatureReader      WinVerifyTrust 数字签名校验 + 签名者 CN 提取
├── QuotaWorker              监控主循环：扫描 → 匹配 → 计时 → 提醒 → 终止/锁定
├── ProcessTerminator        进程树终止（Kill(entireProcessTree)）
├── WtsSession               WTS 会话工具：枚举会话 / InfoEx 查询 / 用户会话内启动进程
├── WtsComputerUsageMonitor  整机使用状态：活动 / 锁屏 / 空闲 / 无会话
├── UserSessionWorkstationLocker  用户会话内执行锁屏（rundll32 LockWorkStation）
├── UserSessionNotifier      用户会话 Toast 通知（msg.exe 回退）
├── JobObjectManager         按规则的 Job Object：命中进程入 Job，子进程自动继承，耗尽时整树终止
├── LiveStatus               实时状态快照（供管理界面轮询）
├── Api/WinQuotaApi          本机 JSON API（127.0.0.1，PIN 鉴权，Host 校验）
└── CommandLine              规则 / 用量 / 奖励 / PIN 管理命令行

WinQuota.Web（Vue 3 + TypeScript + Vite + Arco Design）
└── 四页面：今日状态 / 限制规则 / 添加应用（含正在运行程序选择器 + exe 图标）/ 设置
    构建产物作为 wwwroot 由服务进程直接托管

WinQuota.Tray（WinForms 桌面程序：托盘 + WebView2 主窗口）
└── 原生窗口内嵌管理界面 / 今日状态气泡 / 锁定电脑 / 开机自启开关 / PIN 保护的退出

WinQuota.Core（类库）
├── Models                   LimitRule / ApplicationRule / DailyUsage
├── Data                     QuotaDatabase：SQLite 持久化（WAL）
│                            IntegrityGuard：全量 HMAC 签名 + 单调序号（防直改/回滚）
└── Engine                   QuotaEngine（额度/剩余/提醒阈值）、AppMatcher（匹配规则）
                             ClockGuard（时间防回拨）、UsageGuard（用量单调保护）
```

关键设计（对应计划书）：

- **应用组**：一条规则可绑定多个进程名（如 `foxwq.exe` + `foxwqclient.exe`），共享同一份每日额度
- **整机限制**：仅当“存在活动会话 + 未锁屏 + 距最后键鼠输入未超过空闲阈值（默认 300 秒）”时计入整机时间；锁屏 / 注销 / 空闲 / 系统睡眠均不计入（睡眠通过“单周期计入上限 = 2×扫描间隔”兜底，唤醒后不会把睡眠时间记为使用）
- **按星期差异化额度**：工作日与周末可设不同分钟数，内部一律以秒存储
- **惰性跨天重置**：每次读取按当天日期取 `daily_usage` 行，日期变化自然产生新记录，不依赖定时任务，多天关机也不出错
- **周期性统计**：每 5 秒扫描（固定节拍计时），每 30 秒落盘；耗尽/退出/关机时立即落盘
- **当天禁止再启动**：额度耗尽即终止进程；再次检测到目标进程会在下一扫描周期（≤5 秒）内再次终止
- **整机耗尽锁定**：整机额度耗尽先 Toast 提示再锁定工作站；可通过 `ComputerExhaustedAction=NotifyOnly` 改为仅提醒
- **提前提醒**：剩余 30 / 15 / 5 / 1 分钟 Toast 通知；耗尽通知有 60 秒节流
- **临时奖励**：管理员可为当天追加额度（如 +15 分钟），次日自动失效
- **管理员 PIN**：SHA256 + 盐哈希存储（供后续 GUI 验证使用）
- **系统时间防回拨**：回拨 7 天以内额度继续按原日期累计（防“改时间重置额度”），超 7 天视为时钟纠正；显著回拨会 Toast 提醒
- **产品级识别**：规则可配置 ProductName / Publisher，用户重命名或复制 exe 后仍能命中（与进程名/路径匹配取并集）
- **数字签名校验**：规则可配置签名者（证书 CN，需 WinVerifyTrust 校验通过），改名、复制、换目录的 exe 副本均能命中；签名验证按需触发并带缓存与每窗口预算，不影响扫描周期
- **Job Object 进程树管控**：命中进程纳入规则 Job，其派生子进程（含改名子进程）自动入 Job；额度耗尽时一次终止整棵树
- **服务自恢复**：安装脚本配置 SCM 故障重启策略（60 秒内最多 3 次），异常退出/被强杀后自动拉起
- **数据库防篡改**：三层防护——① 全量 HMAC 签名（规则/用量/设置，任何直改如改小 used_seconds、调高额度、删 PIN 都会被检出）；② 单调递增序号镜像到密钥文件（winquota.db.key），把数据库回滚成旧副本同样检出；③ 运行中内存单调保护（读回值变小即自动恢复差值并告警）。检测到篡改后服务冻结数据库读写、继续按最近合法规则执行限制并每小时告警
- **服务与数据 ACL 加固**：服务 DACL 仅允许 SYSTEM/管理员启停配置（`sc stop` 对受限用户失效）；数据目录通过 SID ACL 限制为 SYSTEM/管理员（数据库与密钥不可读不可改）。对本地管理员级攻击者只能提高门槛，无法根绝

## 构建与测试

要求 .NET 10 SDK 与 Node.js（构建管理界面）。

```bash
# 前端（修改 src/WinQuota.Web 后需要重新构建，产物自动进入服务 wwwroot）
cd src/WinQuota.Web && npm install && npm run build && cd ../..

dotnet build WinQuota.slnx
dotnet test WinQuota.slnx
```

## 管理界面

服务启动后直接访问 `http://127.0.0.1:58390/`（端口可用 `WinQuota__ApiPort` 修改）：

- **今日状态**：每 5 秒刷新的剩余时间、运行中进程、整机状态、最近 7/30 天使用统计图
- **限制规则**：启用/禁用、修改额度、临时奖励（+15/30/60 分钟）、删除
- **添加应用**：从正在运行的程序中选择（含产品名/路径），或手动输入进程名；可一键读取并记录数字签名者（改名/复制 exe 仍能识别）；也可创建整机规则
- **设置**：管理员 PIN 设置/修改

API 仅监听回环地址并校验 Host 头；查询类接口免鉴权，规则修改等敏感操作需要 PIN（请求头 `X-WinQuota-Pin`）。未设置 PIN 时敏感操作放行（首次使用），建议装好后立即设置。

## 桌面程序（托盘 + 主窗口）

`WinQuota.Tray.exe` 是原生桌面程序（WebView2 内嵌管理界面，无需打开浏览器）：

- 启动即显示主窗口；双击托盘图标或右键菜单“管理界面”再次打开
- 关闭窗口只是隐藏到托盘，真正退出需要管理员 PIN（未设置 PIN 时直接确认）
- 鼠标悬停显示第一条规则的剩余时间，“今日状态”弹出全部规则
- 右键菜单：管理界面 / 今日状态 / 锁定电脑 / 开机自启 / 退出
- 再次运行 exe 会唤醒已运行的实例显示窗口（单实例）
- 目标机器缺少 WebView2 运行时时自动回退系统浏览器；`--minimized` 参数启动时仅驻留托盘
- 退出桌面程序不影响后台服务的限制

## 打包安装程序

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

安装包包含：后台服务（含网页管理界面）、托盘程序、开始菜单快捷方式、卸载器；
安装时可选“托盘开机自启”，服务自动注册并配置故障自恢复。

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

- 数据库默认位于 `%ProgramData%\WinQuota\winquota.db`（可用 `WINQUOTA_DB` 环境变量或 `--db` 覆盖），完整性密钥位于同目录 `winquota.db.key`（勿删改；数据目录已被 ACL 限制为 SYSTEM/管理员）
- 日志位于 `%ProgramData%\WinQuota\logs\`（滚动保留 14 天）
- 直接运行 exe 即为控制台模式（便于调试），命令行参数则为 CLI 管理模式
- 可通过环境变量覆盖配置（`WinQuota__` 前缀），如 `WinQuota__IdleThresholdSeconds=600`、`WinQuota__ComputerExhaustedAction=NotifyOnly`

> 注意：以管理员身份在控制台直接运行时，锁屏状态下无法获取用户会话令牌（错误 1314，WTSQueryUserToken 需要 SE_TCB 特权），此时 Toast / 锁定动作会降级为日志。安装为 LocalSystem 服务则无此限制，推荐以服务方式部署。

## CLI 用法

```text
winquota rules add --name 野狐围棋 --process foxwq.exe --process foxwqclient.exe --minutes 120 --weekend-minutes 240
winquota rules add-computer --name 电脑使用 --minutes 120 --weekend-minutes 240
winquota rules list
winquota rules enable --id 1 | rules disable --id 1
winquota rules remove --id 1
winquota usage [--date yyyy-MM-dd]
winquota bonus --id 1 --minutes 15      # 当天临时奖励（应用与整机规则通用）
winquota pin set | pin verify --value <PIN> | pin has
winquota debug scan                     # 诊断：扫描进程并显示各规则匹配结果
winquota debug session                  # 诊断：当前会话状态（锁屏/空闲判定原始数据）
winquota debug lock                     # 诊断：立即锁定当前会话
winquota debug integrity                # 诊断：数据库完整性校验（直改/回滚/密钥状态）
winquota debug signature <exe路径>      # 诊断：验证 exe 数字签名并显示签名者 CN
```

`rules add` 可选 `--path <exe完整路径>`：配置后按路径精确匹配（防止改名/复制绕过），未配置时按进程名匹配（不区分大小写）；`--signer <签名者CN>`：配置后任何由该签名者有效签名的 exe 都会命中（最强识别方式，先用 `debug signature <exe路径>` 查询实际签名者）。

## 当前状态（第一~四阶段完成，v0.6.0）

第一阶段（应用限制）已实测验证：进程识别 → 1:1 精确计时 → 额度耗尽自动终止 → 再启动 5 秒内阻止 → 临时奖励即时生效 → Toast 通知 → 每日自动恢复（单测覆盖）。

第二阶段（整机限制）已完成：活动/锁屏/空闲/无会话状态检测（WTSSessionInfoEx）、锁屏与空闲不计入、睡眠唤醒计时上限保护、耗尽先提示后锁定（可配仅提醒）、按星期额度与临时奖励复用。锁屏门控已在真实锁屏会话上实测（不计时）；空闲检测依赖系统的 LastInputTime 数据（部分系统锁屏期间不填充，自动降级为不判空闲）。

第三阶段（使用体验）已完成：服务直接托管 Vue 3 管理界面（四页面 + 进程选择器 + exe 图标 + 7/30 天统计图 + PIN 门控）、本机 JSON API、ProductName 自动读取、托盘程序（状态/管理界面/锁定/自启/PIN 退出）、CLI `usage --days` 与 `debug lock`。API 已 curl 全流程冒烟，界面与托盘均已实测。

第四阶段（防绕过）已完成：系统时间回拨检测与额度日期钳制、产品级识别（ProductName/Publisher）、数字签名校验（WinVerifyTrust + 签名者 CN 匹配，实测 dotnet.exe 等内嵌签名可命中）、Job Object 进程树管控、服务自恢复与 SCM/数据目录双 ACL 加固、数据库防篡改三层防护（HMAC 全量签名 / 单调序号防回滚 / 运行时用量单调保护，直改、回滚、删 PIN、删基线行均有单测或端到端实测覆盖）。

已知边界（与设计一致）：对拥有本地管理员权限或可物理接触机器的攻击者，防篡改只能提高门槛、无法根绝——请确保被限制用户使用标准账户；Windows 系统自带程序多为目录签名（catalog）而非内嵌签名，`debug signature` 会显示"无效或未签名"，属正常现象，游戏与第三方软件通常为内嵌签名。
