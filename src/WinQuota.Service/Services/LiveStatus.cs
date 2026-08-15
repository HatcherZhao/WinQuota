using System.Collections.Concurrent;

namespace WinQuota.Service.Services;

/// <summary>
/// 监控循环的实时状态快照，供管理界面轮询展示：
/// 每条规则当前匹配到的进程、未落盘的计时增量（保证界面秒级新鲜）、整机使用状态。
/// 由 QuotaWorker 每个扫描周期整体替换。
/// </summary>
public sealed class LiveStatus
{
    public record RunningProcess(int Pid, string ProcessName);

    private volatile ComputerUsageState _computerState = ComputerUsageState.NoUserSession;
    private long _lastUpdateUtcTicks;
    private ConcurrentDictionary<long, IReadOnlyList<RunningProcess>> _matchedByRule = new();
    private ConcurrentDictionary<long, long> _pendingByRule = new();
    private ConcurrentDictionary<long, string> _iconPathByRule = new();

    public ComputerUsageState ComputerState => _computerState;
    public DateTime LastUpdateUtc => new(Interlocked.Read(ref _lastUpdateUtcTicks), DateTimeKind.Utc);

    public void Update(
        ComputerUsageState computerState,
        IReadOnlyDictionary<long, IReadOnlyList<RunningProcess>> matchedByRule,
        IReadOnlyDictionary<long, long> pendingByRule)
    {
        _computerState = computerState;
        _matchedByRule = new ConcurrentDictionary<long, IReadOnlyList<RunningProcess>>(matchedByRule);
        _pendingByRule = new ConcurrentDictionary<long, long>(pendingByRule);
        Interlocked.Exchange(ref _lastUpdateUtcTicks, DateTime.UtcNow.Ticks);
    }

    public IReadOnlyList<RunningProcess> GetRunningProcesses(long ruleId) =>
        _matchedByRule.TryGetValue(ruleId, out var list) ? list : [];

    public long GetPendingSeconds(long ruleId) =>
        _pendingByRule.TryGetValue(ruleId, out var pending) ? pending : 0;

    /// <summary>记录规则命中进程的 exe 路径（取第一个），供管理界面展示应用图标；进程退出后保留最近值。</summary>
    public void SetRuleIconPath(long ruleId, string path) => _iconPathByRule[ruleId] = path;

    public string? GetIconPath(long ruleId) =>
        _iconPathByRule.TryGetValue(ruleId, out var path) ? path : null;

    public void RemoveRuleIcon(long ruleId) => _iconPathByRule.TryRemove(ruleId, out _);

    // —— 会话空闲上报（托盘程序运行在用户会话内，GetLastInputInfo 在会话内才有效）——
    // 部分 Windows 环境的 WTS SessionFlags 语义反转/被远控驱动干扰，
    // 会话内实测的空闲时间是更可靠的“正在使用”判据。

    private long _sessionIdleReceivedUtcTicks;
    private long _sessionIdleSeconds = long.MinValue;

    public void ReportSessionIdle(double idleSeconds)
    {
        if (idleSeconds < 0)
        {
            return;
        }

        Interlocked.Exchange(ref _sessionIdleSeconds, (long)Math.Round(idleSeconds));
        Interlocked.Exchange(ref _sessionIdleReceivedUtcTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>获取新鲜的会话空闲秒数（超过 maxAge 视为过期，返回 null）。</summary>
    public double? TryGetSessionIdle(TimeSpan maxAge)
    {
        var received = Interlocked.Read(ref _sessionIdleReceivedUtcTicks);
        if (received == 0 || DateTime.UtcNow - new DateTime(received, DateTimeKind.Utc) > maxAge)
        {
            return null;
        }

        var idle = Interlocked.Read(ref _sessionIdleSeconds);
        return idle == long.MinValue ? null : idle;
    }
}
