using WinQuota.Core.Data;

namespace WinQuota.Core.Tests;

public class QuotaDatabaseTests
{
    private readonly QuotaDatabase _database;

    public QuotaDatabaseTests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winquota-test-{Guid.NewGuid():N}.db");
        _database = new QuotaDatabase(path);
    }

    [Fact]
    public void AddComputerRule_PersistsWithTypeComputerAndNoApps()
    {
        var ruleId = _database.AddComputerRule("电脑使用", [10800, 10800, 10800, 10800, 10800, 18000, 18000]);

        var entry = Assert.Single(_database.GetRules());
        Assert.Equal(ruleId, entry.Rule.Id);
        Assert.Equal(Models.RuleType.COMPUTER, entry.Rule.Type);
        Assert.True(entry.Rule.Enabled);
        Assert.Empty(entry.Apps);
        Assert.Equal(10800, entry.Rule.QuotaFor(new DateOnly(2026, 8, 14))); // 周五
        Assert.Equal(18000, entry.Rule.QuotaFor(new DateOnly(2026, 8, 15))); // 周六
    }

    [Fact]
    public void ComputerRule_UsageAccumulatesAndRollsOverLikeAppRules()
    {
        var ruleId = _database.AddComputerRule("电脑使用", [3600, 3600, 3600, 3600, 3600, 3600, 3600]);
        var day1 = new DateOnly(2026, 8, 15);

        _database.AddUsedSeconds(ruleId, day1, 500);
        Assert.Equal(500, _database.GetOrCreateUsage(ruleId, day1).UsedSeconds);
        Assert.Equal(0, _database.GetOrCreateUsage(ruleId, day1.AddDays(1)).UsedSeconds);
    }

    [Fact]
    public void AddApplicationRule_PersistsRuleAndProcesses()
    {
        var ruleId = _database.AddApplicationRule(
            "野狐围棋",
            [7200, 7200, 7200, 7200, 7200, 14400, 14400],
            ["foxwq.exe", "foxwqclient.exe"]);

        var rules = _database.GetRules();
        var entry = Assert.Single(rules);
        Assert.Equal(ruleId, entry.Rule.Id);
        Assert.Equal("野狐围棋", entry.Rule.Name);
        Assert.True(entry.Rule.Enabled);
        Assert.Equal(2, entry.Apps.Count);
        Assert.Contains(entry.Apps, a => a.ProcessName == "foxwq.exe");
        Assert.Contains(entry.Apps, a => a.ProcessName == "foxwqclient.exe");
    }

    [Fact]
    public void GetRules_RespectsEnabledFilter()
    {
        var id1 = _database.AddApplicationRule("a", [60, 60, 60, 60, 60, 60, 60], ["a.exe"]);
        var id2 = _database.AddApplicationRule("b", [60, 60, 60, 60, 60, 60, 60], ["b.exe"]);
        _database.SetRuleEnabled(id2, false);

        Assert.Equal(2, _database.GetRules().Count);
        var enabled = _database.GetRules(enabledFilter: true);
        Assert.Single(enabled);
        Assert.Equal(id1, enabled[0].Rule.Id);
    }

    [Fact]
    public void RemoveRule_DeletesUsageAndApps()
    {
        var id = _database.AddApplicationRule("a", [60, 60, 60, 60, 60, 60, 60], ["a.exe"]);
        _database.AddUsedSeconds(id, new DateOnly(2026, 8, 15), 120);

        Assert.True(_database.RemoveRule(id));
        Assert.Empty(_database.GetRules());
        Assert.Empty(_database.GetUsageForDate(new DateOnly(2026, 8, 15)));
        Assert.False(_database.RemoveRule(id));
    }

    [Fact]
    public void Usage_RollsOverLazilyByDate()
    {
        var id = _database.AddApplicationRule("a", [3600, 3600, 3600, 3600, 3600, 3600, 3600], ["a.exe"]);
        var day1 = new DateOnly(2026, 8, 15);

        _database.AddUsedSeconds(id, day1, 3000);
        Assert.Equal(3000, _database.GetOrCreateUsage(id, day1).UsedSeconds);

        // 第二天读取：新的一行从零开始，与昨天的用量无关
        var day2 = day1.AddDays(1);
        var nextDayUsage = _database.GetOrCreateUsage(id, day2);
        Assert.Equal(0, nextDayUsage.UsedSeconds);

        // 多天关机后再开机，依然正确
        var day5 = day1.AddDays(4);
        Assert.Equal(0, _database.GetOrCreateUsage(id, day5).UsedSeconds);
        // 昨天的数据没有被破坏
        Assert.Equal(3000, _database.GetOrCreateUsage(id, day1).UsedSeconds);
    }

    [Fact]
    public void AddUsedSeconds_AccumulatesIncrementally()
    {
        var id = _database.AddApplicationRule("a", [3600, 3600, 3600, 3600, 3600, 3600, 3600], ["a.exe"]);
        var today = new DateOnly(2026, 8, 15);

        _database.AddUsedSeconds(id, today, 5);
        _database.AddUsedSeconds(id, today, 5);
        _database.AddUsedSeconds(id, today, 0); // 非正数不写入

        Assert.Equal(10, _database.GetOrCreateUsage(id, today).UsedSeconds);
    }

    [Fact]
    public void AddBonusSeconds_AffectsOnlyThatDay()
    {
        var id = _database.AddApplicationRule("a", [3600, 3600, 3600, 3600, 3600, 3600, 3600], ["a.exe"]);
        var day1 = new DateOnly(2026, 8, 15);
        var day2 = day1.AddDays(1);

        var bonus = _database.AddBonusSeconds(id, day1, 900);
        Assert.Equal(900, bonus);
        Assert.Equal(900, _database.GetOrCreateUsage(id, day1).BonusSeconds);
        Assert.Equal(0, _database.GetOrCreateUsage(id, day2).BonusSeconds);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        Assert.Null(_database.GetSetting("k"));
        _database.SetSetting("k", "v1");
        Assert.Equal("v1", _database.GetSetting("k"));
        _database.SetSetting("k", "v2");
        Assert.Equal("v2", _database.GetSetting("k"));
    }
}
