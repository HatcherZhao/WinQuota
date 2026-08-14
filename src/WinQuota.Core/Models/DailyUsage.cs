namespace WinQuota.Core.Models;

public class DailyUsage
{
    public long Id { get; set; }

    public long RuleId { get; set; }

    public DateOnly UsageDate { get; set; }

    public long UsedSeconds { get; set; }

    /// <summary>管理员临时奖励的秒数，仅影响当天。</summary>
    public long BonusSeconds { get; set; }
}
