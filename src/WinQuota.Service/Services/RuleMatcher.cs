using WinQuota.Core.Engine;
using WinQuota.Core.Models;

namespace WinQuota.Service.Services;

/// <summary>
/// 把扫描到的进程与应用规则做匹配。
/// 按进程名匹配是廉价的字符串比较；完整路径仅在规则配置了 ExePath 且进程名命中时按需解析。
/// </summary>
public static class RuleMatcher
{
    public static List<ProcessSnapshot> MatchProcesses(
        IReadOnlyList<ProcessSnapshot> snapshots,
        IProcessScanner scanner,
        IReadOnlyList<ApplicationRule> apps)
    {
        var matched = new List<ProcessSnapshot>();
        foreach (var snapshot in snapshots)
        {
            foreach (var app in apps)
            {
                if (!app.Enabled)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(app.ExePath))
                {
                    if (AppMatcher.Matches(snapshot.ProcessName, null, app))
                    {
                        matched.Add(snapshot);
                        break;
                    }
                }
                else if (string.Equals(snapshot.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    var path = scanner.TryGetExecutablePath(snapshot.Pid);
                    if (AppMatcher.Matches(snapshot.ProcessName, path, app))
                    {
                        matched.Add(snapshot);
                        break;
                    }
                }
            }
        }

        return matched;
    }
}
