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

    /// <summary>提前提醒阈值（分钟，逗号分隔，从大到小）；空或非法时用默认 30,15,5,1。</summary>
    public string ReminderMinutes { get; set; } = "30,15,5,1";

    /// <summary>额度耗尽后允许用户自助延期的最多次数（0 = 不允许）。</summary>
    public int MaxExtensions { get; set; }

    /// <summary>每次延期的分钟数。</summary>
    public int ExtensionMinutes { get; set; } = 20;

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

    public int[] ReminderThresholdsSeconds()
    {
        var parsed = (ReminderMinutes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var minutes) && minutes > 0 ? minutes * 60 : 0)
            .Where(seconds => seconds > 0)
            .Distinct()
            .OrderByDescending(seconds => seconds)
            .ToArray();
        return parsed.Length > 0 ? parsed : [1800, 900, 300, 60];
    }
}
