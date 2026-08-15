using WinQuota.Core.Data;
using WinQuota.Core.Engine;
using WinQuota.Core.Models;

namespace WinQuota.Core.Tests;

public class ReminderAndExtensionTests
{
    [Fact]
    public void ReminderThresholds_ParsesCsvMinutes_DescAndDedup()
    {
        var rule = new LimitRule { ReminderMinutes = "10, 30,10, 5" };
        Assert.Equal(new[] { 1800, 600, 300 }, rule.ReminderThresholdsSeconds());
    }

    [Fact]
    public void ReminderThresholds_FallsBackToDefault_WhenInvalid()
    {
        Assert.Equal(new[] { 1800, 900, 300, 60 }, new LimitRule { ReminderMinutes = "" }.ReminderThresholdsSeconds());
        Assert.Equal(new[] { 1800, 900, 300, 60 }, new LimitRule { ReminderMinutes = "abc,0,-5" }.ReminderThresholdsSeconds());
    }

    [Fact]
    public void ThresholdsCrossed_UsesCustomThresholds()
    {
        var thresholds = new[] { 600, 60 }; // 10 分钟 / 1 分钟
        Assert.Equal(new[] { 600 }, QuotaEngine.ThresholdsCrossed(700, 500, thresholds));
        Assert.Equal(new int[] { }, QuotaEngine.ThresholdsCrossed(700, 650, thresholds));
        // 默认阈值不再适用于自定义配置
        Assert.Equal(new int[] { }, QuotaEngine.ThresholdsCrossed(1805, 1790, thresholds));
    }
}

public class ExtensionTests : IDisposable
{
    private readonly QuotaDatabase _database;

    public ExtensionTests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winquota-ext-{Guid.NewGuid():N}.db");
        _database = new QuotaDatabase(path);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_database.DatabasePath); } catch { }
        try { File.Delete(_database.DatabasePath + "-wal"); } catch { }
        try { File.Delete(_database.DatabasePath + "-shm"); } catch { }
        try { File.Delete(_database.DatabasePath + ".key"); } catch { }
    }

    [Fact]
    public void ExtendUsage_EnforcesCountLimit_AndAccumulatesBonus()
    {
        var id = _database.AddComputerRule("电脑", [3600, 3600, 3600, 3600, 3600, 3600, 3600],
            reminderMinutes: null, maxExtensions: 2, extensionMinutes: 20);
        var day = new DateOnly(2026, 8, 16);

        var r1 = _database.ExtendUsage(id, day);
        var r2 = _database.ExtendUsage(id, day);
        var r3 = _database.ExtendUsage(id, day); // 超过 2 次

        Assert.True(r1.Granted);
        Assert.True(r2.Granted);
        Assert.False(r3.Granted);
        Assert.Equal(2, r3.ExtensionsUsed);

        var usage = _database.GetOrCreateUsage(id, day);
        Assert.Equal(2400, usage.BonusSeconds);   // 2 × 20 分钟
        Assert.Equal(2, usage.ExtensionsUsed);

        // 完整性校验在延期写入后依然通过（签名已刷新）
        Assert.Equal(Core.Data.IntegrityStatus.Ok, _database.VerifyIntegrity());
    }

    [Fact]
    public void ExtendUsage_RefusedWhenNotAllowed()
    {
        var id = _database.AddComputerRule("电脑", [3600, 3600, 3600, 3600, 3600, 3600, 3600]);
        var (granted, _, max, _) = _database.ExtendUsage(id, new DateOnly(2026, 8, 16));
        Assert.False(granted);
        Assert.Equal(0, max);
    }

    [Fact]
    public void ExtendUsage_ResetsNextDay()
    {
        var id = _database.AddComputerRule("电脑", [3600, 3600, 3600, 3600, 3600, 3600, 3600],
            reminderMinutes: null, maxExtensions: 1, extensionMinutes: 15);
        var day1 = new DateOnly(2026, 8, 16);

        Assert.True(_database.ExtendUsage(id, day1).Granted);
        Assert.False(_database.ExtendUsage(id, day1).Granted); // 当天用完

        // 第二天次数与奖励自然重置（惰性跨天）
        var day2 = day1.AddDays(1);
        Assert.True(_database.ExtendUsage(id, day2).Granted);
        Assert.Equal(0, _database.GetOrCreateUsage(id, day2).BonusSeconds - 900); // 只含当天 15 分钟
        Assert.Equal(900, _database.GetOrCreateUsage(id, day2).BonusSeconds);
    }

    [Fact]
    public void UpdateRuleDetails_UpdatesReminderAndExtensionSettings()
    {
        var id = _database.AddComputerRule("电脑", [3600, 3600, 3600, 3600, 3600, 3600, 3600]);
        Assert.True(_database.UpdateRuleDetails(id, null, null, reminderMinutes: "45,10", maxExtensions: 3, extensionMinutes: 25));

        var rule = Assert.Single(_database.GetRules()).Rule;
        Assert.Equal("45,10", rule.ReminderMinutes);
        Assert.Equal(3, rule.MaxExtensions);
        Assert.Equal(25, rule.ExtensionMinutes);
    }
}
