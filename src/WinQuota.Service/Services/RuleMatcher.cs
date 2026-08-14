using WinQuota.Core.Engine;
using WinQuota.Core.Models;

namespace WinQuota.Service.Services;

/// <summary>
/// 把扫描到的进程与应用规则做匹配。三种条件取并集（任一命中即算匹配）：
/// 1. 完整路径精确匹配（配置 ExePath 时，防不同目录同名程序误伤）；
/// 2. 进程名匹配（廉价字符串比较）；
/// 3. 产品级匹配（ProductName / Publisher，防用户重命名或复制 exe 绕过，第四阶段）。
/// 路径与产品信息仅在对应条件配置时才按需解析。
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

                if (!string.IsNullOrWhiteSpace(app.ExePath))
                {
                    if (string.Equals(snapshot.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        var path = scanner.TryGetExecutablePath(snapshot.Pid);
                        if (AppMatcher.Matches(snapshot.ProcessName, path, app))
                        {
                            matched.Add(snapshot);
                            break;
                        }
                    }
                }
                else if (AppMatcher.Matches(snapshot.ProcessName, null, app))
                {
                    matched.Add(snapshot);
                    break;
                }

                // 产品级匹配（仅在规则配置了 ProductName / Publisher 时才解析版本信息）
                if ((!string.IsNullOrWhiteSpace(app.ProductName) || !string.IsNullOrWhiteSpace(app.Publisher)) &&
                    AppMatcher.MatchesByProduct(scanner.GetVersionInfo(snapshot.Pid, snapshot.ProcessName), app))
                {
                    matched.Add(snapshot);
                    break;
                }
            }
        }

        return matched;
    }
}
