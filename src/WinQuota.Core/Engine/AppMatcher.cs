using WinQuota.Core.Models;

namespace WinQuota.Core.Engine;

public static class AppMatcher
{
    /// <summary>
    /// 判断一个运行中的进程是否命中应用规则：
    /// 配置了 ExePath 时按完整路径精确匹配，否则按进程名匹配；均不区分大小写。
    /// </summary>
    public static bool Matches(string processName, string? executablePath, ApplicationRule appRule)
    {
        if (!appRule.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(appRule.ExePath))
        {
            return string.Equals(executablePath, appRule.ExePath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(processName, appRule.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 产品级匹配（第四阶段防绕过）：用户重命名或复制 exe 后进程名失效，
    /// 但 exe 内嵌的 ProductName / CompanyName（Publisher）不变。
    /// 规则配置了至少一项产品条件且全部满足（配置了的项）时命中。
    /// </summary>
    public static bool MatchesByProduct(ProcessVersionInfo info, ApplicationRule appRule)
    {
        if (!appRule.Enabled)
        {
            return false;
        }

        var hasProduct = !string.IsNullOrWhiteSpace(appRule.ProductName);
        var hasPublisher = !string.IsNullOrWhiteSpace(appRule.Publisher);
        if (!hasProduct && !hasPublisher)
        {
            return false; // 未配置产品条件，不参与产品匹配
        }

        if (hasProduct && !string.Equals(info.ProductName, appRule.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (hasPublisher && !string.Equals(info.CompanyName, appRule.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(info.ProductName) || !string.IsNullOrWhiteSpace(info.CompanyName);
    }
}
