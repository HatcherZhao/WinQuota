namespace WinQuota.Core.Engine;

/// <summary>
/// 系统时间防回拨保护（第四阶段防绕过的一部分）。
/// 用户把系统时间往回调不会产生新的“今天”，避免回拨到昨天重置额度。
/// </summary>
public static class ClockGuard
{
    /// <summary>
    /// 计算有效的额度日期：
    /// - today &gt;= 已观测的最大日期：正常（前进），使用 today；
    /// - 回拨 7 天以内：视为可疑回拨，继续按最大日期计（额度不重置）；
    /// - 回拨超过 7 天：更像是错误的时钟被纠正（如 CMOS 电池问题导致的未来日期），接受 today。
    /// </summary>
    public static DateOnly EffectiveDate(DateOnly today, DateOnly maxObservedDate)
    {
        if (today >= maxObservedDate)
        {
            return today;
        }

        return maxObservedDate.DayNumber - today.DayNumber <= 7 ? maxObservedDate : today;
    }

    /// <summary>
    /// 判断墙钟相对单调时钟是否发生了显著回拨：
    /// 两个时间源各自计算出的时间差之差（skew = 墙钟增量 - 单调增量），
    /// skew 小于 -120 秒即认为时间被手动回拨（NTP 微调不会达到该量级）。
    /// </summary>
    public static bool IsBackwardAdjustment(TimeSpan wallClockDelta, TimeSpan monotonicDelta) =>
        (wallClockDelta - monotonicDelta).TotalSeconds < -120;
}
