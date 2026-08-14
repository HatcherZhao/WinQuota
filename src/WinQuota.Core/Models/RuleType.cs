namespace WinQuota.Core.Models;

public enum RuleType
{
    /// <summary>整机使用时长限制（第二阶段实现）。</summary>
    COMPUTER = 0,

    /// <summary>指定应用使用时长限制。</summary>
    APPLICATION = 1,
}
