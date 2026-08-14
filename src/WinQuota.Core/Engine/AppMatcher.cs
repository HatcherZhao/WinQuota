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
}
