using WinQuota.Core.Models;

namespace WinQuota.Core.Engine;

public static class QuotaEngine
{
    /// <summary>默认提前提醒阈值（秒）：30 / 15 / 5 / 1 分钟。</summary>
    public static readonly IReadOnlyList<int> DefaultReminderThresholdsSeconds = [1800, 900, 300, 60];

    public static long TotalQuotaSeconds(long baseQuotaSeconds, long bonusSeconds) => baseQuotaSeconds + bonusSeconds;

    public static long RemainingSeconds(long totalQuotaSeconds, long usedSeconds) =>
        Math.Max(0, totalQuotaSeconds - usedSeconds);

    /// <summary>
    /// 返回本次统计周期内刚刚越过的提醒阈值：
    /// 剩余时间从 remainingBefore 降到 remainingAfter，
    /// 阈值 t 被越过当且仅当 remainingAfter &lt;= t &lt; remainingBefore。
    /// </summary>
    public static IEnumerable<int> ThresholdsCrossed(long remainingBefore, long remainingAfter, IEnumerable<int>? thresholdsSeconds = null)
    {
        var thresholds = thresholdsSeconds ?? DefaultReminderThresholdsSeconds;
        return thresholds.Where(t => remainingAfter <= t && t < remainingBefore);
    }
}
