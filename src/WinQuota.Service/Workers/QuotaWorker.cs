using System.Diagnostics;
using Microsoft.Extensions.Options;
using WinQuota.Core.Data;
using WinQuota.Core.Engine;
using WinQuota.Core.Models;
using WinQuota.Service.Services;

namespace WinQuota.Service.Workers;

/// <summary>
/// 防沉迷监控主循环：
/// - 应用规则：周期扫描进程 → 匹配 → 计时 → 跨阈值提醒 → 耗尽终止进程（当天再启动立即再终止）；
/// - 整机规则：仅当“活动会话 + 未锁屏 + 未空闲”时计时，耗尽后锁定工作站。
/// </summary>
public sealed class QuotaWorker : BackgroundService
{
    private readonly QuotaDatabase _database;
    private readonly IProcessScanner _scanner;
    private readonly IProcessTerminator _terminator;
    private readonly INotifier _notifier;
    private readonly IComputerUsageMonitor _computerUsageMonitor;
    private readonly IWorkstationLocker _workstationLocker;
    private readonly IJobObjectManager _jobObjectManager;
    private readonly LiveStatus _liveStatus;
    private readonly ILogger<QuotaWorker> _logger;
    private readonly WinQuotaOptions _options;

    private readonly Dictionary<long, RuleRuntime> _runtimes = [];
    private ComputerUsageState? _lastComputerState;

    // 系统时间防回拨状态
    private DateOnly _maxObservedDate = DateOnly.MinValue;
    private DateTime _lastWallClockUtc = DateTime.MinValue;
    private long _lastMonotonicTicks;
    private DateTime _lastTamperNotifyUtc = DateTime.MinValue;

    // 数据库完整性防篡改状态（第四阶段）
    private IReadOnlyList<(LimitRule Rule, IReadOnlyList<ApplicationRule> Apps)>? _lastGoodRules;
    private bool _integrityFrozen;
    private DateTime _lastIntegrityAlertUtc = DateTime.MinValue;
    private DateTime _lastUsageTamperNotifyUtc = DateTime.MinValue;
    private bool _keyLossHandled;

    public QuotaWorker(
        QuotaDatabase database,
        IProcessScanner scanner,
        IProcessTerminator terminator,
        INotifier notifier,
        IComputerUsageMonitor computerUsageMonitor,
        IWorkstationLocker workstationLocker,
        IJobObjectManager jobObjectManager,
        LiveStatus liveStatus,
        IOptions<WinQuotaOptions> options,
        ILogger<QuotaWorker> logger)
    {
        _database = database;
        _scanner = scanner;
        _terminator = terminator;
        _notifier = notifier;
        _computerUsageMonitor = computerUsageMonitor;
        _workstationLocker = workstationLocker;
        _jobObjectManager = jobObjectManager;
        _liveStatus = liveStatus;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WinQuota 监控启动，扫描周期 {Scan}s，落盘周期 {Flush}s，数据库 {Db}",
            _options.ScanIntervalSeconds, _options.FlushIntervalSeconds, _database.DatabasePath);

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.ScanIntervalSeconds));

        // 固定节拍循环：按相邻两次 tick 的真实时间差计时，避免周期内工作耗时造成的漏计。
        // 单周期计入上限为 2 倍扫描间隔：系统睡眠/休眠唤醒后，间隔会被放大，不能把睡眠时间记为使用时间。
        var lastTickTimestamp = Stopwatch.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            var tickStart = Stopwatch.GetTimestamp();
            var rawElapsed = (int)Math.Round((tickStart - lastTickTimestamp) / (double)Stopwatch.Frequency, MidpointRounding.AwayFromZero);
            var elapsedSeconds = Math.Clamp(rawElapsed, 1, (int)interval.TotalSeconds * 2);
            lastTickTimestamp = tickStart;

            try
            {
                Tick(elapsedSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "监控周期执行失败，继续下一轮");
            }

            var sleep = interval - TimeSpan.FromMilliseconds((Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency);
            if (sleep > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(sleep, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        FlushAll();
        _logger.LogInformation("WinQuota 监控停止");
    }

    private void Tick(int elapsedSeconds)
    {
        var now = DateTime.Now;
        var today = ResolveEffectiveDate(now);

        // 完整性校验在一切读取之前：被篡改的库不允许影响本轮的规则与用量。
        MaintainIntegrityState();
        var rules = LoadRulesForEnforcement();

        var snapshots = _scanner.Snapshot();

        // 整机使用状态只在存在整机规则时查询（WTS 调用），并记录状态变化便于排查。
        var computerState = _computerUsageMonitor.GetState();
        if (computerState != _lastComputerState)
        {
            _logger.LogInformation("整机使用状态变化：{Old} → {New}", _lastComputerState?.ToString() ?? "未知", computerState);
            _lastComputerState = computerState;
        }

        var liveMatched = new Dictionary<long, IReadOnlyList<LiveStatus.RunningProcess>>();
        foreach (var (rule, apps) in rules)
        {
            var isComputerRule = rule.Type == RuleType.COMPUTER;
            var matched = isComputerRule
                ? []
                : RuleMatcher.MatchProcesses(snapshots, _scanner, apps);
            var inUse = isComputerRule
                ? computerState == ComputerUsageState.Active
                : matched.Count > 0;
            if (!inUse)
            {
                continue;
            }

            if (!isComputerRule && matched.Count > 0)
            {
                // 进程纳入规则 Job Object：之后其派生的子进程自动进入，
                // 耗尽时可以一次终止整棵树（含换了名字的子进程）。
                foreach (var process in matched)
                {
                    _jobObjectManager.AssignToRule(rule.Id, process.Pid);
                }

                liveMatched[rule.Id] = matched
                    .Select(m => new LiveStatus.RunningProcess(m.Pid, m.ProcessName))
                    .ToList();
            }

            var runtime = GetRuntime(rule.Id, today);

            // 每轮从数据库读取最新用量与奖励，保证 CLI / 管理端的修改即时生效；
            // 读取同时做单调保护：已用被改小 / 奖励被删时自动恢复并告警。
            var usage = ReadUsageWithGuard(rule.Id, today, runtime);
            var totalQuota = QuotaEngine.TotalQuotaSeconds(rule.QuotaFor(today), usage.BonusSeconds);
            var usedBefore = usage.UsedSeconds + runtime.PendingDeltaSeconds;
            var usedAfter = usedBefore + elapsedSeconds;
            var remainingBefore = QuotaEngine.RemainingSeconds(totalQuota, usedBefore);
            var remainingAfter = QuotaEngine.RemainingSeconds(totalQuota, usedAfter);

            runtime.PendingDeltaSeconds += elapsedSeconds;

            if (remainingAfter <= 0)
            {
                Flush(runtime);
                var throttled = runtime.LastExhaustedNotifyUtc is { } last &&
                                now - last < TimeSpan.FromSeconds(Math.Max(0, _options.ExhaustedNotifyThrottleSeconds));
                if (!throttled)
                {
                    runtime.LastExhaustedNotifyUtc = now;
                    var actionText = isComputerRule
                        ? (_options.LockOnComputerExhausted ? "电脑即将锁定。" : "（仅提醒模式）")
                        : "明天将自动恢复额度。";
                    _notifier.Notify("WinQuota 防沉迷", $"{rule.Name} 今日使用时间已达到限制，{actionText}");
                }

                if (isComputerRule)
                {
                    _logger.LogWarning("整机规则 {Rule} 今日额度已耗尽（已用 {Used}s / {Total}s），状态 {State}",
                        rule.Name, usedAfter, totalQuota, computerState);
                    if (_options.LockOnComputerExhausted)
                    {
                        _workstationLocker.LockWorkstation();
                    }
                }
                else
                {
                    _logger.LogWarning("规则 {Rule} 今日额度已耗尽（已用 {Used}s / {Total}s），终止 {Count} 个进程",
                        rule.Name, usedAfter, totalQuota, matched.Count);
                    // 先终止 Job Object（覆盖换名子进程），再按匹配列表兜底终止。
                    _jobObjectManager.TerminateRule(rule.Id);
                    _terminator.Terminate(matched);
                }

                continue;
            }

            foreach (var threshold in QuotaEngine.ThresholdsCrossed(remainingBefore, remainingAfter))
            {
                if (runtime.FiredReminders.Add(threshold))
                {
                    _notifier.Notify("WinQuota 防沉迷", $"{rule.Name} 今日剩余 {threshold / 60} 分钟。");
                }
            }

            // 接近耗尽时逐秒级落盘，平时按周期落盘。
            if (remainingAfter <= 60 || now - runtime.LastFlushUtc >= TimeSpan.FromSeconds(Math.Max(1, _options.FlushIntervalSeconds)))
            {
                Flush(runtime);
            }
        }

            _liveStatus.Update(
                computerState,
                liveMatched,
                rules.ToDictionary(e => e.Rule.Id, e => GetRuntime(e.Rule.Id, today).PendingDeltaSeconds));
    }

    /// <summary>
    /// 计算本轮使用的有效日期，并监测系统时间回拨：
    /// 回拨时额度继续按已观测的最大日期累计（防“改时间到昨天重置额度”），
    /// 同时以 Toast 提醒（每小时最多一次）。
    /// </summary>
    private DateOnly ResolveEffectiveDate(DateTime now)
    {
        var nowUtc = now.ToUniversalTime();
        var monotonic = Stopwatch.GetTimestamp();
        if (_lastMonotonicTicks > 0)
        {
            var wallDelta = nowUtc - _lastWallClockUtc;
            var monoDelta = TimeSpan.FromSeconds((monotonic - _lastMonotonicTicks) / (double)Stopwatch.Frequency);
            if (ClockGuard.IsBackwardAdjustment(wallDelta, monoDelta) &&
                DateTime.UtcNow - _lastTamperNotifyUtc > TimeSpan.FromHours(1))
            {
                _lastTamperNotifyUtc = DateTime.UtcNow;
                _logger.LogWarning("检测到系统时间被回拨（墙钟 {Wall:F0}s / 单调 {Mono:F0}s），额度日期继续按 {Date} 计",
                    wallDelta.TotalSeconds, monoDelta.TotalSeconds, _maxObservedDate);
                _notifier.Notify("WinQuota 防沉迷", "检测到系统时间被回拨，今日额度不会因此重置。");
            }
        }

        _lastWallClockUtc = nowUtc;
        _lastMonotonicTicks = monotonic;

        var today = DateOnly.FromDateTime(now);
        var effective = ClockGuard.EffectiveDate(today, _maxObservedDate);
        if (effective != _maxObservedDate && today < _maxObservedDate && _maxObservedDate.DayNumber - today.DayNumber <= 7)
        {
            _logger.LogWarning("系统日期回拨 {Days} 天（{Today} < {Max}），额度按 {Effective} 继续",
                _maxObservedDate.DayNumber - today.DayNumber, today, _maxObservedDate, effective);
        }

        if (effective > _maxObservedDate)
        {
            _maxObservedDate = effective;
        }

        return effective;
    }

    /// <summary>
    /// 每轮开始时校验数据库完整性（第四阶段防绕过）：
    /// - 校验通过：刷新“最近合法规则”缓存，正常执行；
    /// - 数据被直改 / 数据库文件被回滚 / 基线行被删：进入冻结状态——继续按最近合法规则
    ///   与内存用量执行限制（终止/锁定照常），但不再读写数据库，并每小时 Toast 告警；
    /// - 密钥文件丢失（如重装/迁移导致）：首次自动重建基线并告警，其后不再静默重建。
    /// </summary>
    private void MaintainIntegrityState()
    {
        var status = _database.VerifyIntegrity();
        if (status == IntegrityStatus.Ok)
        {
            if (_integrityFrozen)
            {
                _logger.LogWarning("数据库完整性校验恢复正常");
            }

            _integrityFrozen = false;
            return;
        }

        var keyLoss = status == IntegrityStatus.KeyMissing || (status == IntegrityStatus.NoBaseline && !_database.HasIntegrityKey);
        if (keyLoss && !_keyLossHandled)
        {
            // 密钥与库一起丢失更可能是重装/迁移：重建基线（当前数据被签名）并留下记录
            _keyLossHandled = true;
            _logger.LogWarning("完整性密钥文件缺失（状态 {Status}），已重建基线；若非重装导致请检查数据", status);
            _database.ReinitializeIntegrity();
            return;
        }

        _integrityFrozen = true;
        _logger.LogError("数据库完整性校验失败（状态 {Status}），冻结数据库读写，继续按最近合法规则执行限制", status);
        if (DateTime.UtcNow - _lastIntegrityAlertUtc > TimeSpan.FromHours(1))
        {
            _lastIntegrityAlertUtc = DateTime.UtcNow;
            _notifier.Notify("WinQuota 防沉迷", "检测到限制数据被异常修改，限制仍按此前规则继续执行，请管理员检查。");
        }
    }

    /// <summary>完整性正常时从数据库读取并刷新缓存；冻结期间沿用最近一次合法的规则列表。</summary>
    private IReadOnlyList<(LimitRule Rule, IReadOnlyList<ApplicationRule> Apps)> LoadRulesForEnforcement()
    {
        if (!_integrityFrozen)
        {
            _lastGoodRules = _database.GetRules(enabledFilter: true);
            return _lastGoodRules;
        }

        if (_lastGoodRules is not null)
        {
            return _lastGoodRules;
        }

        // 服务启动时库已处于被篡改状态：没有更好的来源，读取现状并保持告警
        _logger.LogWarning("启动时数据库完整性异常，暂按当前读取的规则执行");
        return _database.GetRules(enabledFilter: true);
    }

    /// <summary>
    /// 读取当天用量并做单调保护：已用秒数当天只增不减，奖励只经管理员操作增长；
    /// 数据库读回值小于内存记住的最大值说明有人直改数据库，自动把差值写回并告警。
    /// 冻结期间不读数据库，完全按内存值继续计时。
    /// </summary>
    private DailyUsage ReadUsageWithGuard(long ruleId, DateOnly today, RuleRuntime runtime)
    {
        if (_integrityFrozen)
        {
            return new DailyUsage { RuleId = ruleId, UsageDate = today, UsedSeconds = runtime.LastReadDbUsed, BonusSeconds = runtime.KnownBonus };
        }

        var usage = _database.GetOrCreateUsage(ruleId, today);
        if (UsageGuard.IsTampered(usage.UsedSeconds, runtime.KnownDbUsed, usage.BonusSeconds, runtime.KnownBonus))
        {
            var missingUsed = runtime.KnownDbUsed - usage.UsedSeconds;
            var missingBonus = runtime.KnownBonus - usage.BonusSeconds;
            _logger.LogError("规则 {RuleId} 用量被直改（数据库 {DbUsed}s/{DbBonus}s < 记忆 {KnownUsed}s/{KnownBonus}s），自动恢复",
                ruleId, usage.UsedSeconds, usage.BonusSeconds, runtime.KnownDbUsed, runtime.KnownBonus);
            if (missingUsed > 1)
            {
                _database.AddUsedSeconds(ruleId, today, missingUsed);
            }

            if (missingBonus > 0)
            {
                _database.AddBonusSeconds(ruleId, today, missingBonus);
            }

            if (DateTime.UtcNow - _lastUsageTamperNotifyUtc > TimeSpan.FromHours(1))
            {
                _lastUsageTamperNotifyUtc = DateTime.UtcNow;
                _notifier.Notify("WinQuota 防沉迷", "检测到今日使用数据被异常修改，已自动恢复。");
            }

            usage = _database.GetOrCreateUsage(ruleId, today);
        }

        runtime.LastReadDbUsed = usage.UsedSeconds;
        runtime.KnownDbUsed = Math.Max(runtime.KnownDbUsed, usage.UsedSeconds);
        runtime.KnownBonus = Math.Max(runtime.KnownBonus, usage.BonusSeconds);
        return usage;
    }

    private RuleRuntime GetRuntime(long ruleId, DateOnly today)
    {
        if (_runtimes.TryGetValue(ruleId, out var runtime) && runtime.Date == today)
        {
            return runtime;
        }

        // 跨天：先把属于昨天的未落盘增量写回，再创建当天运行时（提醒状态随日期自然重置）。
        if (runtime is { PendingDeltaSeconds: > 0 })
        {
            Flush(runtime);
        }

        runtime = new RuleRuntime { RuleId = ruleId, Date = today };
        _runtimes[ruleId] = runtime;
        return runtime;
    }

    private void Flush(RuleRuntime runtime)
    {
        // 完整性冻结期间不写数据库：写入会立即重签被篡改的数据，掩盖篡改痕迹
        if (_integrityFrozen)
        {
            return;
        }

        if (runtime.PendingDeltaSeconds > 0)
        {
            _database.AddUsedSeconds(runtime.RuleId, runtime.Date, runtime.PendingDeltaSeconds);
            runtime.LastReadDbUsed += runtime.PendingDeltaSeconds;
            runtime.KnownDbUsed = Math.Max(runtime.KnownDbUsed, runtime.LastReadDbUsed);
            runtime.PendingDeltaSeconds = 0;
        }

        runtime.LastFlushUtc = DateTime.Now;
    }

    private void FlushAll()
    {
        foreach (var runtime in _runtimes.Values)
        {
            try
            {
                Flush(runtime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止前落盘失败，规则 {RuleId}", runtime.RuleId);
            }
        }
    }

    private sealed class RuleRuntime
    {
        public long RuleId { get; init; }
        public DateOnly Date { get; init; }
        public long PendingDeltaSeconds { get; set; }
        public HashSet<int> FiredReminders { get; } = [];
        public DateTime LastFlushUtc { get; set; } = DateTime.MinValue;
        public DateTime? LastExhaustedNotifyUtc { get; set; }

        // 用量单调保护（第四阶段防绕过）：当天见过的数据库侧最大已用 / 奖励，以及最近一次读回的已用值
        public long KnownDbUsed { get; set; }
        public long KnownBonus { get; set; }
        public long LastReadDbUsed { get; set; }
    }
}
