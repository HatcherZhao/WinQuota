# 架构与设计

WinQuota 由四个项目组成：后台服务（核心）、Web 管理界面、桌面托盘程序与基础类库。

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
├── UserSessionNotifier      用户会话 Toast 通知（msg.exe 回退，关键通知置顶弹窗）
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

## 关键设计

- **应用组**：一条规则可绑定多个进程名（如 `foxwq.exe` + `foxwqclient.exe`），共享同一份每日额度
- **整机限制**：仅当“存在活动会话 + 正在使用”时计入整机时间；锁屏 / 注销 / 空闲 / 系统睡眠均不计入（睡眠通过“单周期计入上限 = 2×扫描间隔”兜底）。“正在使用”优先采用托盘程序在用户会话内实测的键鼠空闲时间（GetLastInputInfo）——部分 Windows 环境的 WTS 锁屏标志语义反转或被远控驱动干扰，会话内读数不受影响；托盘未运行时回退 WTS 判定。诊断工具：`tools/probe-desktop.ps1`（查看真实输入桌面与空闲）
- **按星期差异化额度**：工作日与周末可设不同分钟数，内部一律以秒存储
- **惰性跨天重置**：每次读取按当天日期取 `daily_usage` 行，日期变化自然产生新记录，不依赖定时任务，多天关机也不出错
- **周期性统计**：每 5 秒扫描（固定节拍计时），每 30 秒落盘；耗尽/退出/关机时立即落盘
- **当天禁止再启动**：额度耗尽即终止进程；再次检测到目标进程会在下一扫描周期（≤5 秒）内再次终止
- **整机耗尽锁定**：整机额度耗尽先 Toast 提示再锁定工作站；可通过 `ComputerExhaustedAction=NotifyOnly` 改为仅提醒
- **提前提醒**：剩余时间每越过一个配置阈值（分钟）弹一次 Toast 通知，阈值按规则配置（默认 `30,15,5,1`）；耗尽通知有 60 秒节流
- **临时奖励**：管理员可为当天追加额度（如 +15 分钟），次日自动失效
- **自助延期（v0.8.0+）**：规则可配置“额度耗尽后允许延期”（最多次数 + 每次分钟数）。耗尽后先进入宽限期（默认 300 秒，`ExtensionGraceSeconds` 可配），期间不杀进程 / 不锁屏，等使用者在管理界面自行延期（免管理员 PIN，次数由服务端在事务内强制）；宽限期内每 30 秒以关键通道提醒一次，超时未延期才执行限制。宽限通知走 msg.exe 置顶弹窗，全屏游戏压制 Toast 时仍可见。延期 / 奖励增加总额度后，提醒阈值重新武装（延期得到的时间同样有 10/5/1 分钟提醒）
- **管理员 PIN**：SHA256 + 盐哈希存储（供管理界面 / 托盘验证使用）
- **系统时间防回拨**：回拨 7 天以内额度继续按原日期累计（防“改时间重置额度”），超 7 天视为时钟纠正；显著回拨会 Toast 提醒
- **产品级识别**：规则可配置 ProductName / Publisher，用户重命名或复制 exe 后仍能命中（与进程名/路径匹配取并集）
- **数字签名校验**：规则可配置签名者（证书 CN，需 WinVerifyTrust 校验通过），改名、复制、换目录的 exe 副本均能命中；签名验证按需触发并带缓存与每窗口预算，不影响扫描周期
- **Job Object 进程树管控**：命中进程纳入规则 Job，其派生子进程（含改名子进程）自动入 Job；额度耗尽时一次终止整棵树
- **服务自恢复**：安装脚本配置 SCM 故障重启策略（60 秒内最多 3 次），异常退出/被强杀后自动拉起
- **数据库防篡改**：三层防护——① 全量 HMAC 签名（规则/用量/设置，任何直改如改小 used_seconds、调高额度、删 PIN 都会被检出）；② 单调递增序号镜像到密钥文件（winquota.db.key），把数据库回滚成旧副本同样检出；③ 运行中内存单调保护（读回值变小即自动恢复差值并告警）。检测到篡改后服务冻结数据库读写、继续按最近合法规则执行限制并每小时告警
- **服务与数据 ACL 加固**：服务 DACL 仅允许 SYSTEM/管理员启停配置（`sc stop` 对受限用户失效）；数据目录通过 SID ACL 限制为 SYSTEM/管理员（数据库与密钥不可读不可改）。对本地管理员级攻击者只能提高门槛，无法根绝

## 开发状态（v0.8.1）

**第一阶段（应用限制）** 已实测验证：进程识别 → 1:1 精确计时 → 额度耗尽自动终止 → 再启动 5 秒内阻止 → 临时奖励即时生效 → Toast 通知 → 每日自动恢复（单测覆盖）。

**第二阶段（整机限制）** 已完成：活动/锁屏/空闲/无会话状态检测（WTSSessionInfoEx）、锁屏与空闲不计入、睡眠唤醒计时上限保护、耗尽先提示后锁定（可配仅提醒）、按星期额度与临时奖励复用。锁屏门控已在真实锁屏会话上实测（不计时）；空闲检测依赖系统的 LastInputTime 数据（部分系统锁屏期间不填充，自动降级为不判空闲）。

**第三阶段（使用体验）** 已完成：服务直接托管 Vue 3 管理界面（四页面 + 进程选择器 + exe 图标 + 7/30 天统计图 + PIN 门控）、本机 JSON API、ProductName 自动读取、托盘程序（状态/管理界面/锁定/自启/PIN 退出）、CLI `usage --days` 与 `debug lock`。API 已 curl 全流程冒烟，界面与托盘均已实测。

**第四阶段（防绕过）** 已完成：系统时间回拨检测与额度日期钳制、产品级识别（ProductName/Publisher）、数字签名校验（WinVerifyTrust + 签名者 CN 匹配，实测 dotnet.exe 等内嵌签名可命中）、Job Object 进程树管控、服务自恢复与 SCM/数据目录双 ACL 加固、数据库防篡改三层防护（HMAC 全量签名 / 单调序号防回滚 / 运行时用量单调保护，直改、回滚、删 PIN、删基线行均有单测或端到端实测覆盖）。

**v0.8 系列**：提醒阈值按规则可配置（分钟 CSV）；额度耗尽后允许使用者自助延期（宽限期内不杀进程/不锁屏，次数与时长由服务端强制）；宽限通知改走 msg.exe 置顶弹窗穿透全屏游戏；延期/奖励后提醒阈值重新武装；首页进度条百分比保留两位小数（改用 Arco `#text` 插槽实现，`format` prop 在 Arco 2.58 中不存在）。

## 已知边界

对拥有本地管理员权限或可物理接触机器的攻击者，防篡改只能提高门槛、无法根绝——请确保被限制用户使用标准账户；Windows 系统自带程序多为目录签名（catalog）而非内嵌签名，`debug signature` 会显示“无效或未签名”，属正常现象，游戏与第三方软件通常为内嵌签名。

原始需求背景与分阶段规划见 [implementation-plan.md](implementation-plan.md)。
