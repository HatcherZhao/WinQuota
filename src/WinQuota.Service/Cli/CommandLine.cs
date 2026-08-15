using System.Globalization;
using WinQuota.Core.Data;
using WinQuota.Core.Engine;
using WinQuota.Core.Models;

namespace WinQuota.Service.Cli;

/// <summary>
/// 轻量管理命令行（第一阶段的“简单管理界面”），GUI 客户端开发完成后由其取代。
/// 用法示例：
///   winquota rules add --name 野狐围棋 --process foxwq.exe --process foxwqclient.exe --minutes 120 --weekend-minutes 240
///   winquota rules list
///   winquota rules disable --id 1
///   winquota usage
///   winquota bonus --id 1 --minutes 15
///   winquota pin set
/// </summary>
public static class CommandLine
{
    private static readonly string[] Verbs = ["rules", "usage", "bonus", "extend", "pin", "debug"];

    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && Verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static int Run(IReadOnlyList<string> args)
    {
        try
        {
            var databasePath = ResolveDatabasePath(args);
            var database = new QuotaDatabase(databasePath);
            return args[0].ToLowerInvariant() switch
            {
                "rules" => RunRules(args.Skip(1).ToList(), database),
                "usage" => RunUsage(args.Skip(1).ToList(), database),
                "bonus" => RunBonus(args.Skip(1).ToList(), database),
                "extend" => RunExtend(args.Skip(1).ToList(), database),
                "pin" => RunPin(args.Skip(1).ToList(), database),
                "debug" => RunDebug(args.Skip(1).ToList(), database),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int RunRules(IReadOnlyList<string> args, QuotaDatabase database)
    {
        if (args.Count == 0)
        {
            return Usage();
        }

        var options = ParseOptions(args.Skip(1));
        switch (args[0].ToLowerInvariant())
        {
            case "list":
                return ListRules(database);
            case "add":
                return AddRule(options, database);
            case "add-computer":
                return AddComputerRule(options, database);
            case "remove":
            {
                if (!TryGetLong(options, "id", out var id))
                {
                    Console.Error.WriteLine("需要 --id <编号>");
                    return 1;
                }

                Console.WriteLine(database.RemoveRule(id) ? $"已删除规则 #{id}" : $"规则 #{id} 不存在");
                return 0;
            }
            case "enable":
            case "disable":
            {
                if (!TryGetLong(options, "id", out var id))
                {
                    Console.Error.WriteLine("需要 --id <编号>");
                    return 1;
                }

                Console.WriteLine(database.SetRuleEnabled(id, args[0].Equals("enable", StringComparison.OrdinalIgnoreCase))
                    ? $"已{(args[0].ToLowerInvariant() == "enable" ? "启用" : "禁用")}规则 #{id}"
                    : $"规则 #{id} 不存在");
                return 0;
            }
            default:
                return Usage();
        }
    }

    private static int ListRules(QuotaDatabase database)
    {
        var rules = database.GetRules();
        if (rules.Count == 0)
        {
            Console.WriteLine("（暂无规则）");
            return 0;
        }

        foreach (var (rule, apps) in rules)
        {
            var typeText = rule.Type == RuleType.COMPUTER ? "整机限制" : "应用限制";
            Console.WriteLine($"#{rule.Id} [{(rule.Enabled ? "启用" : "禁用")}] {rule.Name}（{typeText}）");
            Console.WriteLine($"    周一~周五 {FormatDuration(rule.MondayLimitSeconds)}，周末 {FormatDuration(rule.SaturdayLimitSeconds)}");
            foreach (var app in apps)
            {
                var match = string.IsNullOrEmpty(app.ExePath) ? app.ProcessName : $"{app.ExePath}";
                Console.WriteLine($"    进程：{match}");
                if (!string.IsNullOrWhiteSpace(app.ProductName))
                {
                    Console.WriteLine($"    产品：{app.ProductName}");
                }

                if (!string.IsNullOrWhiteSpace(app.Signer))
                {
                    Console.WriteLine($"    签名者：{app.Signer}");
                }
            }
        }

        return 0;
    }

    private static int AddRule(Dictionary<string, List<string>> options, QuotaDatabase database)
    {
        if (!options.TryGetValue("name", out var names) || names.Count == 0)
        {
            Console.Error.WriteLine("需要 --name <应用名称>");
            return 1;
        }

        if (!options.TryGetValue("process", out var processes) || processes.Count == 0)
        {
            Console.Error.WriteLine("需要至少一个 --process <进程名>");
            return 1;
        }

        if (!TryGetLong(options, "minutes", out var weekdayMinutes) || weekdayMinutes <= 0)
        {
            Console.Error.WriteLine("需要 --minutes <工作日分钟数>");
            return 1;
        }

        var weekendMinutes = TryGetLong(options, "weekend-minutes", out var wm) && wm > 0 ? wm : weekdayMinutes;
        options.TryGetValue("path", out var paths);
        options.TryGetValue("product", out var products);
        options.TryGetValue("signer", out var signers);

        var weekdayLimits = new[]
        {
            weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L,
            weekendMinutes * 60L, weekendMinutes * 60L,
        };

        var ruleId = database.AddApplicationRule(
            names[0],
            weekdayLimits,
            processes,
            paths?.FirstOrDefault(),
            products?.FirstOrDefault(),
            null,
            signers?.FirstOrDefault(),
            options.TryGetValue("remind", out var reminds) ? reminds.FirstOrDefault() : null,
            (int)(TryGetLong(options, "allow-extend", out var ae) ? ae : 0),
            (int)(TryGetLong(options, "extend-minutes", out var em) && em > 0 ? em : 20));
        Console.WriteLine($"已创建规则 #{ruleId}：{names[0]}，工作日 {weekdayMinutes} 分钟 / 周末 {weekendMinutes} 分钟");
        Console.WriteLine("（若后台服务已在运行，新规则将在下一个扫描周期生效）");
        return 0;
    }

    private static int AddComputerRule(Dictionary<string, List<string>> options, QuotaDatabase database)
    {
        if (!options.TryGetValue("name", out var names) || names.Count == 0)
        {
            Console.Error.WriteLine("需要 --name <规则名称>");
            return 1;
        }

        if (!TryGetLong(options, "minutes", out var weekdayMinutes) || weekdayMinutes <= 0)
        {
            Console.Error.WriteLine("需要 --minutes <工作日分钟数>");
            return 1;
        }

        var weekendMinutes = TryGetLong(options, "weekend-minutes", out var wm) && wm > 0 ? wm : weekdayMinutes;
        var weekdayLimits = new[]
        {
            weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L, weekdayMinutes * 60L,
            weekendMinutes * 60L, weekendMinutes * 60L,
        };

        var ruleId = database.AddComputerRule(
            names[0],
            weekdayLimits,
            options.TryGetValue("remind", out var reminds) ? reminds.FirstOrDefault() : null,
            (int)(TryGetLong(options, "allow-extend", out var ae) ? ae : 0),
            (int)(TryGetLong(options, "extend-minutes", out var em) && em > 0 ? em : 20));
        Console.WriteLine($"已创建整机规则 #{ruleId}：{names[0]}，工作日 {weekdayMinutes} 分钟 / 周末 {weekendMinutes} 分钟");
        Console.WriteLine("（锁屏与空闲时间不计入，耗尽后自动锁定电脑）");
        return 0;
    }

    private static int RunExtend(IReadOnlyList<string> args, QuotaDatabase database)
    {
        var options = ParseOptions(args);
        if (!TryGetLong(options, "id", out var ruleId))
        {
            Console.Error.WriteLine("需要 --id <规则编号>");
            return 1;
        }

        var (granted, used, max, seconds) = database.ExtendUsage(ruleId, DateOnly.FromDateTime(DateTime.Now));
        if (!granted)
        {
            Console.Error.WriteLine(max <= 0 ? "该规则不允许延期" : $"今日延期次数已用完（{used}/{max}）");
            return 1;
        }

        Console.WriteLine($"已延期 {seconds / 60} 分钟（今日已用 {used}/{max} 次）");
        return 0;
    }

    private static int DebugLock()
    {
        var sessionId = Services.WtsSession.FindActiveSessionId();
        Console.WriteLine($"活动会话：{sessionId}");
        if (sessionId < 0)
        {
            return 1;
        }

        try
        {
            var ok = Services.WtsSession.LaunchInSession(sessionId, "rundll32.exe user32.dll,LockWorkStation");
            Console.WriteLine(ok ? "已发起工作站锁定命令。" : "锁定失败。");
            return ok ? 0 : 1;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.WriteLine($"锁定失败：Win32 错误 {ex.NativeErrorCode}（{ex.Message}）");
            return 1;
        }
    }

    private static int RunUsage(IReadOnlyList<string> args, QuotaDatabase database)
    {
        var options = ParseOptions(args);

        if (options.TryGetValue("days", out var dayValues) && int.TryParse(dayValues.FirstOrDefault(), out var days) && days > 0)
        {
            return ShowRecentUsage(Math.Min(days, 90), database);
        }

        DateOnly date;
        if (options.TryGetValue("date", out var dateValues) && DateOnly.TryParseExact(dateValues[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
        }
        else
        {
            date = DateOnly.FromDateTime(DateTime.Now);
        }

        var rules = database.GetRules().ToDictionary(entry => entry.Rule.Id, entry => entry.Rule);
        var usages = database.GetUsageForDate(date);
        Console.WriteLine($"【{date:yyyy-MM-dd} 使用情况】");
        if (usages.Count == 0)
        {
            Console.WriteLine("（当天暂无使用记录）");
            return 0;
        }

        foreach (var usage in usages)
        {
            var name = rules.TryGetValue(usage.RuleId, out var rule) ? rule.Name : $"规则 #{usage.RuleId}";
            var quota = rules.TryGetValue(usage.RuleId, out var r) ? r.QuotaFor(date) : 0;
            var total = QuotaEngine.TotalQuotaSeconds(quota, usage.BonusSeconds);
            var remaining = QuotaEngine.RemainingSeconds(total, usage.UsedSeconds);
            var bonusText = usage.BonusSeconds > 0 ? $"（含临时奖励 {FormatDuration(usage.BonusSeconds)}）" : "";
            Console.WriteLine($"  {name}：已用 {FormatDuration(usage.UsedSeconds)} / {FormatDuration(total)}{bonusText}，剩余 {FormatDuration(remaining)}");
        }

        return 0;
    }

    private static int ShowRecentUsage(int days, QuotaDatabase database)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-(days - 1));
        var rules = database.GetRules().ToDictionary(e => e.Rule.Id, e => e.Rule.Name);
        var usage = database.GetRecentUsage(from, today)
            .GroupBy(u => u.RuleId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(u => u.UsageDate, u => u.UsedSeconds));

        Console.WriteLine($"【最近 {days} 天使用情况】");
        foreach (var (ruleId, name) in rules)
        {
            var total = 0L;
            var parts = new List<string>();
            for (var i = 0; i < days; i++)
            {
                var date = from.AddDays(i);
                var used = usage.TryGetValue(ruleId, out var byDate) && byDate.TryGetValue(date, out var seconds) ? seconds : 0;
                total += used;
                parts.Add($"{date:MM-dd} {FormatDuration(used)}");
            }

            Console.WriteLine($"  {name}（合计 {FormatDuration(total)}）");
            Console.WriteLine($"    {string.Join("，", parts)}");
        }

        return 0;
    }

    private static int RunBonus(IReadOnlyList<string> args, QuotaDatabase database)
    {
        var options = ParseOptions(args);
        if (!TryGetLong(options, "id", out var ruleId))
        {
            Console.Error.WriteLine("需要 --id <规则编号>");
            return 1;
        }

        if (!TryGetLong(options, "minutes", out var minutes) || minutes <= 0)
        {
            Console.Error.WriteLine("需要 --minutes <奖励分钟数>");
            return 1;
        }

        var totalBonus = database.AddBonusSeconds(ruleId, DateOnly.FromDateTime(DateTime.Now), minutes * 60);
        Console.WriteLine($"已为规则 #{ruleId} 今日增加 {minutes} 分钟，当日累计奖励 {FormatDuration(totalBonus)}");
        return 0;
    }

    private static int RunPin(IReadOnlyList<string> args, QuotaDatabase database)
    {
        var options = ParseOptions(args);
        var command = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        switch (command)
        {
            case "set":
            {
                var pin = ReadPin("请输入管理员 PIN：", "请再次输入：");
                PinHasher.SetPin(database, pin);
                Console.WriteLine("管理员 PIN 已设置。");
                return 0;
            }
            case "verify":
            {
                if (!PinHasher.HasPin(database))
                {
                    Console.WriteLine("尚未设置 PIN。");
                    return 1;
                }

                if (!options.TryGetValue("value", out var values))
                {
                    Console.Error.WriteLine("需要 --value <PIN>（或使用交互输入）");
                    return 1;
                }

                Console.WriteLine(PinHasher.VerifyPin(database, values[0]) ? "验证通过" : "PIN 错误");
                return PinHasher.VerifyPin(database, values[0]) ? 0 : 2;
            }
            case "has":
                Console.WriteLine(PinHasher.HasPin(database) ? "已设置 PIN" : "尚未设置 PIN");
                return 0;
            default:
                return Usage();
        }
    }

    private static string ReadPin(string prompt, string confirmPrompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var first = Console.ReadLine();
            if (string.IsNullOrEmpty(first))
            {
                Console.WriteLine("PIN 不能为空。");
                continue;
            }

            Console.Write(confirmPrompt);
            var second = Console.ReadLine();
            if (first == second)
            {
                return first;
            }

            Console.WriteLine("两次输入不一致，请重试。");
        }
    }

    private static int RunDebug(IReadOnlyList<string> args, QuotaDatabase database)
    {
        var command = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        switch (command)
        {
            case "scan":
                return DebugScan(database);
            case "session":
                return DebugSession();
            case "lock":
                return DebugLock();
            case "integrity":
                return DebugIntegrity(database);
            case "signature":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("需要 exe 完整路径：winquota debug signature C:\\path\\to.exe");
                    return 1;
                }

                var signature = Services.FileSignatureReader.Read(args[1]);
                Console.WriteLine(signature.Trusted
                    ? $"签名有效，签名者：{signature.SignerCn}"
                    : "签名无效或未签名（不可用于签名者匹配）");
                return signature.Trusted ? 0 : 2;
            }
            default:
                Console.WriteLine("""
                    用法：
                      winquota debug scan      —— 扫描进程并显示每条规则的匹配结果
                      winquota debug session   —— 显示当前会话状态（锁屏 / 空闲判定原始数据）
                      winquota debug lock      —— 立即锁定当前会话（实测锁定通道）
                      winquota debug integrity —— 校验数据库完整性（检测直改 / 回滚 / 密钥状态）
                      winquota debug signature <exe路径> —— 验证 exe 数字签名并显示签名者
                    """);
                return 0;
        }
    }

    private static int DebugIntegrity(QuotaDatabase database)
    {
        var status = database.VerifyIntegrity();
        var text = status switch
        {
            WinQuota.Core.Data.IntegrityStatus.Ok => "通过：数据未被篡改",
            WinQuota.Core.Data.IntegrityStatus.Tampered => "失败：数据被直改或数据库文件被回滚（服务将冻结数据库读写并告警）",
            WinQuota.Core.Data.IntegrityStatus.NoBaseline => "基线缺失（签名行被删除或完整性防护未启用）",
            WinQuota.Core.Data.IntegrityStatus.KeyMissing => "密钥文件缺失（winquota.db.key 不在数据库同目录）",
            _ => status.ToString(),
        };
        Console.WriteLine($"数据库：{database.DatabasePath}");
        Console.WriteLine($"完整性：{text}");
        Console.WriteLine($"密钥文件：{(database.HasIntegrityKey ? "存在" : "缺失")}");
        if (status != WinQuota.Core.Data.IntegrityStatus.Ok)
        {
            Console.WriteLine("若确认为环境变化（如重装/迁移）所致，可在服务停止后删除密钥文件并重启服务以重建基线。");
            return 2;
        }

        return 0;
    }

    private static int DebugScan(QuotaDatabase database)
    {
        var scanner = new Services.ToolhelpProcessScanner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.ToolhelpProcessScanner>.Instance);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshots = scanner.Snapshot();
        Console.WriteLine($"扫描到 {snapshots.Count} 个进程（耗时 {stopwatch.ElapsedMilliseconds}ms）");

        foreach (var (rule, apps) in database.GetRules(enabledFilter: true))
        {
            var matched = Services.RuleMatcher.MatchProcesses(snapshots, scanner, apps);
            Console.WriteLine($"规则 #{rule.Id} {rule.Name}：匹配 {matched.Count} 个进程");
            foreach (var m in matched)
            {
                Console.WriteLine($"    PID {m.Pid} {m.ProcessName}");
            }
        }

        return 0;
    }

    private static int DebugSession()
    {
        var sessionId = Services.WtsSession.FindActiveSessionId();
        Console.WriteLine($"活动会话 ID：{sessionId}");
        if (sessionId < 0)
        {
            return 0;
        }

        if (!Services.WtsSession.QueryInfoEx(sessionId, out var info))
        {
            Console.WriteLine("WTSSessionInfoEx 查询失败");
            return 1;
        }

        var lastInput = DateTime.FromFileTimeUtc(info.LastInputTime);
        var current = DateTime.FromFileTimeUtc(info.CurrentTime);
        var idleSeconds = info.CurrentTime > info.LastInputTime
            ? (info.CurrentTime - info.LastInputTime) / 10_000_000.0
            : -1;
        Console.WriteLine($"会话 {info.SessionId}，用户 {info.DomainName}\\{info.UserName}");
        Console.WriteLine($"SessionFlags（0=未锁 1=已锁）：{info.SessionFlags}");
        Console.WriteLine($"LastInputTime：{lastInput:yyyy-MM-dd HH:mm:ss}（原始 {info.LastInputTime}）");
        Console.WriteLine($"CurrentTime：{current:yyyy-MM-dd HH:mm:ss}（原始 {info.CurrentTime}）");
        Console.WriteLine($"空闲时长：{idleSeconds:F0} 秒");
        return 0;
    }

    private static string ResolveDatabasePath(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals("--db", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        var path = Environment.GetEnvironmentVariable("WINQUOTA_DB");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WinQuota",
                "winquota.db");
        }

        return Path.GetFullPath(path);
    }

    private static Dictionary<string, List<string>> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                current = arg[2..];
                result[current] = [];
            }
            else if (current is not null)
            {
                result[current].Add(arg);
            }
        }

        return result;
    }

    private static bool TryGetLong(Dictionary<string, List<string>> options, string key, out long value)
    {
        value = 0;
        return options.TryGetValue(key, out var values) &&
               values.Count > 0 &&
               long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatDuration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var parts = new List<string>();
        if (ts.Hours > 0)
        {
            parts.Add($"{ts.Hours}小时");
        }

        if (ts.Minutes > 0)
        {
            parts.Add($"{ts.Minutes}分");
        }

        if (ts.Seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{ts.Seconds}秒");
        }

        return string.Concat(parts);
    }

    private static int Usage()
    {
        Console.WriteLine("""
            WinQuota 命令行管理

            用法：
              winquota rules add --name <名称> --process <进程名> [--process <进程名>...] [--path <exe完整路径>]
                                 [--product <产品名>] [--minutes <工作日分钟>] [--weekend-minutes <周末分钟>]
                                 （--product 按 exe 内嵌 ProductName 匹配，重命名/复制 exe 后依然命中）
              winquota rules add-computer --name <名称> --minutes <工作日分钟> [--weekend-minutes <周末分钟>]
              winquota rules list
              winquota rules enable --id <编号> | rules disable --id <编号>
              winquota rules remove --id <编号>
              winquota usage [--date yyyy-MM-dd]
              winquota bonus --id <编号> --minutes <分钟>
              winquota pin set | pin verify --value <PIN> | pin has
              winquota debug scan | debug session

            全局选项：--db <数据库路径>（默认取 WINQUOTA_DB 环境变量或 %ProgramData%\WinQuota\winquota.db）
            """);
        return 0;
    }
}
