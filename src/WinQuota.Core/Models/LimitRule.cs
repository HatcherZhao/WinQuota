namespace WinQuota.Core.Models;

public class LimitRule
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public RuleType Type { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>周一额度（秒）。</summary>
    public long MondayLimitSeconds { get; set; }

    public long TuesdayLimitSeconds { get; set; }

    public long WednesdayLimitSeconds { get; set; }

    public long ThursdayLimitSeconds { get; set; }

    public long FridayLimitSeconds { get; set; }

    public long SaturdayLimitSeconds { get; set; }

    public long SundayLimitSeconds { get; set; }

    public long QuotaFor(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => MondayLimitSeconds,
        DayOfWeek.Tuesday => TuesdayLimitSeconds,
        DayOfWeek.Wednesday => WednesdayLimitSeconds,
        DayOfWeek.Thursday => ThursdayLimitSeconds,
        DayOfWeek.Friday => FridayLimitSeconds,
        DayOfWeek.Saturday => SaturdayLimitSeconds,
        DayOfWeek.Sunday => SundayLimitSeconds,
        _ => 0,
    };
}
