using WinQuota.Core.Engine;
using WinQuota.Core.Models;

namespace WinQuota.Core.Tests;

public class QuotaEngineTests
{
    [Fact]
    public void QuotaFor_ReturnsPerWeekdayLimit()
    {
        var rule = new LimitRule
        {
            MondayLimitSeconds = 7200,
            TuesdayLimitSeconds = 7200,
            WednesdayLimitSeconds = 7200,
            ThursdayLimitSeconds = 7200,
            FridayLimitSeconds = 7200,
            SaturdayLimitSeconds = 14400,
            SundayLimitSeconds = 14400,
        };

        // 2026-08-14 是周五，2026-08-15 是周六
        Assert.Equal(7200, rule.QuotaFor(new DateOnly(2026, 8, 14)));
        Assert.Equal(14400, rule.QuotaFor(new DateOnly(2026, 8, 15)));
        Assert.Equal(7200, rule.QuotaFor(new DateOnly(2026, 8, 10))); // 周一
    }

    [Fact]
    public void RemainingSeconds_ClampsToZero_WhenOverused()
    {
        Assert.Equal(600, QuotaEngine.RemainingSeconds(7200, 6600));
        Assert.Equal(0, QuotaEngine.RemainingSeconds(7200, 7200));
        Assert.Equal(0, QuotaEngine.RemainingSeconds(7200, 9000));
    }

    [Fact]
    public void TotalQuotaSeconds_AddsBonus()
    {
        Assert.Equal(10800, QuotaEngine.TotalQuotaSeconds(7200, 3600));
    }

    [Theory]
    [InlineData(1801, 1800, new[] { 1800 })]       // 恰好降到 30 分钟阈值
    [InlineData(1800, 1799, new int[] { })]        // 仍在阈值之上
    [InlineData(1000, 500, new[] { 900 })]         // 一次跨过 15 分钟阈值
    [InlineData(2000, 50, new[] { 1800, 900, 300, 60 })] // 一次跨过所有阈值
    [InlineData(61, 60, new[] { 60 })]          // 降到 1 分钟阈值
    [InlineData(60, 0, new int[] { })]          // 阈值已触发过，耗尽时不再重复（耗尽另有通知）
    [InlineData(0, 0, new int[] { })]           // 本来就已耗尽（阻止再启动场景）不重复提醒
    public void ThresholdsCrossed_DetectsOnlyNewlyCrossed(long before, long after, int[] expected)
    {
        var crossed = QuotaEngine.ThresholdsCrossed(before, after).ToArray();
        Assert.Equal(expected, crossed);
    }
}

public class AppMatcherTests
{
    private static ApplicationRule App(string processName, string? exePath = null) =>
        new() { ProcessName = processName, ExePath = exePath, Enabled = true };

    [Fact]
    public void Matches_ByProcessName_CaseInsensitive()
    {
        Assert.True(AppMatcher.Matches("FoxWQ.EXE", null, App("foxwq.exe")));
    }

    [Fact]
    public void Matches_ByFullPath_WhenPathConfigured()
    {
        var rule = App("foxwq.exe", @"C:\Program Files\FoxWQ\foxwq.exe");
        Assert.True(AppMatcher.Matches("foxwq.exe", @"c:\program files\foxwq\foxwq.exe", rule));
        // 路径不同（用户复制 exe 到别处）则不命中
        Assert.False(AppMatcher.Matches("foxwq.exe", @"D:\games\foxwq.exe", rule));
    }

    [Fact]
    public void Matches_IgnoresDisabledAppRule()
    {
        var rule = new ApplicationRule { ProcessName = "foxwq.exe", Enabled = false };
        Assert.False(AppMatcher.Matches("foxwq.exe", null, rule));
    }

    [Fact]
    public void Matches_RequiresExactName_NotSubstring()
    {
        Assert.False(AppMatcher.Matches("foxwqclient.exe", null, App("foxwq.exe")));
    }
}
