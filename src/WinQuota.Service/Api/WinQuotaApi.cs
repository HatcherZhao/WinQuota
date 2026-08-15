using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WinQuota.Core.Data;
using WinQuota.Core.Engine;
using WinQuota.Core.Models;
using WinQuota.Service.Cli;
using WinQuota.Service.Services;

namespace WinQuota.Service.Api;

public static class WinQuotaApi
{
    public static void MapWinQuotaApi(this WebApplication app)
    {
        // 仅允许本机回环地址访问（API 绑定 127.0.0.1，这里再校验 Host 头防 DNS 重绑定）。
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.MapGet("/api/status", (QuotaDatabase db, LiveStatus live, HttpRequest request) =>
        {
            // 托盘程序在用户会话内实测的空闲时间（GetLastInputInfo），优先于不可靠的 WTS 锁屏标志
            if (double.TryParse(
                    request.Headers["X-WinQuota-IdleSeconds"].FirstOrDefault(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var idleSeconds))
            {
                live.ReportSessionIdle(idleSeconds);
            }

            var today = DateOnly.FromDateTime(DateTime.Now);
            var rules = db.GetRules();
            var list = rules.Select(entry =>
            {
                var usage = db.GetOrCreateUsage(entry.Rule.Id, today);
                var quota = entry.Rule.QuotaFor(today);
                var used = usage.UsedSeconds + live.GetPendingSeconds(entry.Rule.Id);
                var running = live.GetRunningProcesses(entry.Rule.Id);
                return new
                {
                    id = entry.Rule.Id,
                    name = entry.Rule.Name,
                    type = entry.Rule.Type == RuleType.COMPUTER ? "computer" : "application",
                    enabled = entry.Rule.Enabled,
                    quotaSeconds = quota,
                    bonusSeconds = usage.BonusSeconds,
                    usedSeconds = used,
                    remainingSeconds = QuotaEngine.RemainingSeconds(QuotaEngine.TotalQuotaSeconds(quota, usage.BonusSeconds), used),
                    running = entry.Rule.Enabled && running.Count > 0,
                    processes = running.Select(p => new { pid = p.Pid, name = p.ProcessName }),
                    iconPath = entry.Rule.Type == RuleType.COMPUTER ? null : live.GetIconPath(entry.Rule.Id),
                    extensionsMax = entry.Rule.MaxExtensions,
                    extensionsUsed = usage.ExtensionsUsed,
                    extensionMinutes = entry.Rule.ExtensionMinutes,
                };
            });

            return Results.Json(new
            {
                date = today.ToString("yyyy-MM-dd"),
                computerState = live.ComputerState.ToString().ToLowerInvariant(),
                liveUpdateUtc = live.LastUpdateUtc,
                rules = list,
            });
        });

        app.MapGet("/api/rules", (QuotaDatabase db) =>
        {
            var list = db.GetRules().Select(entry => new
            {
                id = entry.Rule.Id,
                name = entry.Rule.Name,
                type = entry.Rule.Type == RuleType.COMPUTER ? "computer" : "application",
                enabled = entry.Rule.Enabled,
                reminderMinutes = entry.Rule.ReminderMinutes,
                maxExtensions = entry.Rule.MaxExtensions,
                extensionMinutes = entry.Rule.ExtensionMinutes,
                weekdayQuotaSeconds = new[]
                {
                    entry.Rule.MondayLimitSeconds, entry.Rule.TuesdayLimitSeconds, entry.Rule.WednesdayLimitSeconds,
                    entry.Rule.ThursdayLimitSeconds, entry.Rule.FridayLimitSeconds, entry.Rule.SaturdayLimitSeconds,
                    entry.Rule.SundayLimitSeconds,
                },
                apps = entry.Apps.Select(a => new
                {
                    id = a.Id,
                    applicationName = a.ApplicationName,
                    processName = a.ProcessName,
                    exePath = a.ExePath,
                    productName = a.ProductName,
                    publisher = a.Publisher,
                    signer = a.Signer,
                }),
            });
            return Results.Json(new { rules = list });
        });

        app.MapPost("/api/rules/app", (AddAppRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            if (string.IsNullOrWhiteSpace(body.Name) || body.ProcessNames is not { Count: > 0 } || body.Minutes <= 0)
            {
                return Results.BadRequest(new { error = "name、processNames、minutes 必填且合法" });
            }

            var ruleId = db.AddApplicationRule(
                body.Name.Trim(),
                BuildWeekdayLimits(body.Minutes, body.WeekendMinutes),
                body.ProcessNames.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray(),
                string.IsNullOrWhiteSpace(body.ExePath) ? null : body.ExePath.Trim(),
                string.IsNullOrWhiteSpace(body.ProductName) ? null : body.ProductName.Trim(),
                null,
                string.IsNullOrWhiteSpace(body.Signer) ? null : body.Signer.Trim(),
                body.ReminderMinutes,
                body.MaxExtensions ?? 0,
                body.ExtensionMinutes ?? 20);
            return Results.Json(new { ruleId });
        });

        app.MapPost("/api/rules/computer", (AddComputerRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            if (string.IsNullOrWhiteSpace(body.Name) || body.Minutes <= 0)
            {
                return Results.BadRequest(new { error = "name、minutes 必填且合法" });
            }

            var ruleId = db.AddComputerRule(
                body.Name.Trim(),
                BuildWeekdayLimits(body.Minutes, body.WeekendMinutes),
                body.ReminderMinutes,
                body.MaxExtensions ?? 0,
                body.ExtensionMinutes ?? 20);
            return Results.Json(new { ruleId });
        });

        app.MapPost("/api/rules/update", (UpdateRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            var ok = db.UpdateRuleQuotas(body.Id, BuildWeekdayLimits(body.Minutes, body.WeekendMinutes));
            return ok ? Results.Ok() : Results.BadRequest(new { error = "规则不存在" });
        });

        app.MapPost("/api/rules/edit", (EditRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            var hasName = !string.IsNullOrWhiteSpace(body.Name);
            var hasProcesses = body.ProcessNames is { Count: > 0 };
            var hasNewSettings = !string.IsNullOrWhiteSpace(body.ReminderMinutes) ||
                                 body.MaxExtensions is not null ||
                                 body.ExtensionMinutes is not null;
            if (!hasName && !hasProcesses && !hasNewSettings)
            {
                return Results.BadRequest(new { error = "至少提供 name、processNames 或提醒/延期配置" });
            }

            var processes = hasProcesses
                ? body.ProcessNames!.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray()
                : null;
            var ok = db.UpdateRuleDetails(body.Id, body.Name, processes, body.ExePath, body.ProductName, body.Publisher, body.Signer,
                body.ReminderMinutes, body.MaxExtensions, body.ExtensionMinutes);
            return ok ? Results.Ok() : Results.BadRequest(new { error = "规则不存在" });
        });

        // 用户自助延期：无需 PIN（次数与分钟数由服务端按规则配置强制）
        app.MapPost("/api/extend", (ExtendRequest body, QuotaDatabase db) =>
        {
            var (granted, used, max, seconds) = db.ExtendUsage(body.RuleId, DateOnly.FromDateTime(DateTime.Now));
            return granted
                ? Results.Json(new { granted = true, extensionsUsed = used, maxExtensions = max, grantedSeconds = seconds })
                : Results.Json(new { granted = false, extensionsUsed = used, maxExtensions = max, error = max <= 0 ? "该规则不允许延期" : "今日延期次数已用完" }, statusCode: 400);
        });

        app.MapPost("/api/rules/enable", (EnableRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            return db.SetRuleEnabled(body.Id, body.Enabled) ? Results.Ok() : Results.BadRequest(new { error = "规则不存在" });
        });

        app.MapPost("/api/rules/delete", (DeleteRuleRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            return db.RemoveRule(body.Id) ? Results.Ok() : Results.BadRequest(new { error = "规则不存在" });
        });

        app.MapPost("/api/bonus", (BonusRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            if (!PinAuthorized(request, db))
            {
                return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
            }

            if (body.Minutes <= 0)
            {
                return Results.BadRequest(new { error = "minutes 必须为正数" });
            }

            var bonus = db.AddBonusSeconds(body.Id, DateOnly.FromDateTime(DateTime.Now), body.Minutes * 60);
            return Results.Json(new { bonusSeconds = bonus });
        });

        app.MapGet("/api/usage", (int days, QuotaDatabase db) =>
        {
            days = Math.Clamp(days == 0 ? 7 : days, 1, 90);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var from = today.AddDays(-(days - 1));
            var rules = db.GetRules().ToDictionary(e => e.Rule.Id, e => e.Rule);
            var usage = db.GetRecentUsage(from, today)
                .GroupBy(u => u.RuleId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(u => u.UsageDate, u => u.UsedSeconds));

            var dates = Enumerable.Range(0, days).Select(from.AddDays).ToList();
            var list = rules.Values.Select(rule => new
            {
                ruleId = rule.Id,
                name = rule.Name,
                type = rule.Type == RuleType.COMPUTER ? "computer" : "application",
                days = dates.Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    usedSeconds = usage.TryGetValue(rule.Id, out var byDate) && byDate.TryGetValue(d, out var used) ? used : 0,
                }),
            });

            return Results.Json(new { days, rules = list });
        });

        app.MapGet("/api/processes", (IProcessScanner scanner) =>
        {
            var result = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in scanner.Snapshot())
            {
                var path = scanner.TryGetExecutablePath(snapshot.Pid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var key = $"{snapshot.ProcessName}|{path}";
                if (result.ContainsKey(key))
                {
                    continue;
                }

                string? productName = null;
                long workingSetBytes;
                try
                {
                    using var process = Process.GetProcessById(snapshot.Pid);
                    workingSetBytes = process.WorkingSet64;
                }
                catch
                {
                    workingSetBytes = 0;
                }

                try
                {
                    productName = FileVersionInfo.GetVersionInfo(path).ProductName;
                }
                catch
                {
                    // 受保护进程读不到版本信息，忽略
                }

                result[key] = new ProcessInfo(snapshot.Pid, snapshot.ProcessName, path, productName, workingSetBytes);
            }

            return Results.Json(new
            {
                // 按内存占用倒序：管理员通常要限制的就是占内存最多的游戏
                processes = result.Values.OrderByDescending(p => p.WorkingSetBytes)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            });
        });

        app.MapGet("/api/icon", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest();
            }

            var png = Services.IconCache.GetPng(path);
            return png is null ? Results.NotFound() : Results.File(png, "image/png");
        });

        // 按需验证某个 exe 的数字签名并返回签名者 CN（WinVerifyTrust 较昂贵，进程选择器点击时调用）
        app.MapGet("/api/signature", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path))
            {
                return Results.BadRequest(new { error = "需要合法的 exe 路径" });
            }

            var signature = FileSignatureReader.Read(path);
            return Results.Json(new
            {
                trusted = signature.Trusted,
                signerCn = signature.SignerCn,
            });
        });

        app.MapPost("/api/pin/verify", (VerifyPinRequest body, QuotaDatabase db) =>
            Results.Json(new { ok = !PinHasher.HasPin(db) || PinHasher.VerifyPin(db, body.Pin ?? string.Empty) }));

        app.MapPost("/api/pin", (PinRequest body, QuotaDatabase db, HttpRequest request) =>
        {
            // 已设置 PIN 时，修改需要先验证旧 PIN。
            if (PinHasher.HasPin(db))
            {
                var current = request.Headers["X-WinQuota-Pin"].FirstOrDefault();
                if (string.IsNullOrEmpty(current) || !PinHasher.VerifyPin(db, current))
                {
                    return Results.Json(new { error = "PIN required or incorrect" }, statusCode: 403);
                }
            }

            if (string.IsNullOrWhiteSpace(body.NewPin) || body.NewPin.Trim().Length < 4)
            {
                return Results.BadRequest(new { error = "PIN 至少 4 位" });
            }

            PinHasher.SetPin(db, body.NewPin.Trim());
            return Results.Ok();
        });

        app.MapGet("/api/settings", (QuotaDatabase db) => Results.Json(new
        {
            pinConfigured = PinHasher.HasPin(db),
        }));
    }

    /// <summary>敏感操作鉴权：未设置 PIN 时放行（首次使用），设置后必须携带正确 PIN。</summary>
    private static bool PinAuthorized(HttpRequest request, QuotaDatabase db)
    {
        if (!PinHasher.HasPin(db))
        {
            return true;
        }

        var pin = request.Headers["X-WinQuota-Pin"].FirstOrDefault();
        return !string.IsNullOrEmpty(pin) && PinHasher.VerifyPin(db, pin);
    }

    private static long[] BuildWeekdayLimits(long minutes, long? weekendMinutes)
    {
        var weekday = Math.Max(1, minutes) * 60;
        var weekend = (weekendMinutes is > 0 ? weekendMinutes.Value : Math.Max(1, minutes)) * 60;
        return [weekday, weekday, weekday, weekday, weekday, weekend, weekend];
    }

    public sealed record AddAppRuleRequest(string Name, List<string>? ProcessNames, string? ExePath, string? ProductName, string? Signer, long Minutes, long? WeekendMinutes, string? ReminderMinutes, int? MaxExtensions, int? ExtensionMinutes);

    public sealed record ProcessInfo(int Pid, string Name, string Path, string? ProductName, long WorkingSetBytes);

    public sealed record AddComputerRuleRequest(string Name, long Minutes, long? WeekendMinutes, string? ReminderMinutes, int? MaxExtensions, int? ExtensionMinutes);

    public sealed record UpdateRuleRequest(long Id, long Minutes, long? WeekendMinutes);

    public sealed record EditRuleRequest(long Id, string? Name, List<string>? ProcessNames, string? ExePath, string? ProductName, string? Publisher, string? Signer, string? ReminderMinutes, int? MaxExtensions, int? ExtensionMinutes);

    public sealed record ExtendRequest(long RuleId);

    public sealed record EnableRuleRequest(long Id, bool Enabled);

    public sealed record DeleteRuleRequest(long Id);

    public sealed record BonusRequest(long Id, long Minutes);

    public sealed record PinRequest(string? NewPin);

    public sealed record VerifyPinRequest(string? Pin);
}
