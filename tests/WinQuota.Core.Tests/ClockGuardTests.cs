using WinQuota.Core.Engine;

namespace WinQuota.Core.Tests;

public class ClockGuardTests
{
    [Fact]
    public void EffectiveDate_AdvancesNormally()
    {
        var max = new DateOnly(2026, 8, 15);
        Assert.Equal(new DateOnly(2026, 8, 16), ClockGuard.EffectiveDate(new DateOnly(2026, 8, 16), max));
        Assert.Equal(max, ClockGuard.EffectiveDate(max, max));
    }

    [Fact]
    public void EffectiveDate_ClampsRecentRollback()
    {
        var max = new DateOnly(2026, 8, 15);
        // 回拨 1 天（典型“改时间到昨天重置额度”）：按原日期继续
        Assert.Equal(max, ClockGuard.EffectiveDate(max.AddDays(-1), max));
        // 回拨 7 天以内同样钳制
        Assert.Equal(max, ClockGuard.EffectiveDate(max.AddDays(-7), max));
    }

    [Fact]
    public void EffectiveDate_AcceptsLargeBackwardJump_AsClockRepair()
    {
        var max = new DateOnly(2026, 8, 15);
        // 回拨超过 7 天：视为错误的时钟被纠正，接受新日期
        Assert.Equal(new DateOnly(2026, 8, 1), ClockGuard.EffectiveDate(new DateOnly(2026, 8, 1), max));
    }

    [Fact]
    public void IsBackwardAdjustment_DetectsManualRollbackOnly()
    {
        var five = TimeSpan.FromSeconds(5);
        Assert.False(ClockGuard.IsBackwardAdjustment(five, five));        // 正常
        Assert.False(ClockGuard.IsBackwardAdjustment(five + TimeSpan.FromSeconds(1), five)); // NTP 微调（前进）
        Assert.False(ClockGuard.IsBackwardAdjustment(five, five + TimeSpan.FromSeconds(1))); // NTP 微调（回退 1s）
        Assert.True(ClockGuard.IsBackwardAdjustment(TimeSpan.Zero, TimeSpan.FromHours(1)));  // 回拨 1 小时
        Assert.True(ClockGuard.IsBackwardAdjustment(five - TimeSpan.FromMinutes(10), five)); // 回拨 10 分钟
    }
}
