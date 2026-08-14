namespace WinQuota.Service;

public sealed class WinQuotaOptions
{
    public const string SectionName = "WinQuota";

    /// <summary>SQLite 数据库路径。空值时使用默认位置 %ProgramData%\WinQuota\winquota.db，或环境变量 WINQUOTA_DB。</summary>
    public string? DatabasePath { get; set; }

    /// <summary>进程扫描与计时周期（秒）。</summary>
    public int ScanIntervalSeconds { get; set; } = 5;

    /// <summary>用量落盘周期（秒）。</summary>
    public int FlushIntervalSeconds { get; set; } = 30;

    /// <summary>耗尽提示的最小重复间隔（秒），避免用户反复重启应用时刷屏。</summary>
    public int ExhaustedNotifyThrottleSeconds { get; set; } = 60;

    /// <summary>整机规则的空闲判定阈值（秒）：超过该时长无键鼠输入则暂停计整机时间。0 = 不启用空闲检测。</summary>
    public int IdleThresholdSeconds { get; set; } = 300;

    /// <summary>整机额度耗尽后的动作：Lock（锁定工作站，默认）或 NotifyOnly（仅提醒）。</summary>
    public string ComputerExhaustedAction { get; set; } = "Lock";

    /// <summary>管理界面 HTTP API 监听端口（仅绑定 127.0.0.1）。</summary>
    public int ApiPort { get; set; } = 58390;

    public bool LockOnComputerExhausted =>
        !"NotifyOnly".Equals(ComputerExhaustedAction, StringComparison.OrdinalIgnoreCase);

    public string ResolveDatabasePath()
    {
        var path = DatabasePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("WINQUOTA_DB");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WinQuota",
                "winquota.db");
        }

        return Path.GetFullPath(path);
    }
}
