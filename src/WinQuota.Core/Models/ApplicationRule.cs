namespace WinQuota.Core.Models;

public class ApplicationRule
{
    public long Id { get; set; }

    public long RuleId { get; set; }

    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>进程名，含 .exe 扩展名，比较时不区分大小写。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>可选的完整路径匹配，配置后按路径精确匹配（不区分大小写），否则按进程名匹配。</summary>
    public string? ExePath { get; set; }

    public string? ProductName { get; set; }

    public string? Publisher { get; set; }

    public bool Enabled { get; set; } = true;
}
