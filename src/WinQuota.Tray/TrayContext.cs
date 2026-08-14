using System.Drawing;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

namespace WinQuota.Tray;

/// <summary>
/// WinQuota 托盘程序（计划书第十七节）：
/// 展示今日剩余时间、打开管理界面、锁定电脑、开机自启开关；
/// 退出需要管理员 PIN，且退出托盘不会停止后台服务。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static string ApiBase =>
        $"http://127.0.0.1:{Environment.GetEnvironmentVariable("WINQUOTA__APIPORT") ?? "58390"}";

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private string _lastStatusSummary = "WinQuota：正在获取状态…";

    public TrayContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "WinQuota 防沉迷",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusBalloon();

        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        _ = RefreshStatusAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("今日状态") { Font = new Font(menu.Font, FontStyle.Bold) };
        statusItem.Click += (_, _) => ShowStatusBalloon();

        var openItem = new ToolStripMenuItem("打开管理界面");
        openItem.Click += (_, _) => OpenManagementUi();

        var lockItem = new ToolStripMenuItem("锁定电脑");
        lockItem.Click += (_, _) => LockWorkStation();

        var autoStartItem = new ToolStripMenuItem("开机自启（托盘）") { Checked = AutoStart.IsEnabled() };
        autoStartItem.Click += (_, _) =>
        {
            var enabled = !AutoStart.IsEnabled();
            AutoStart.SetEnabled(enabled);
            autoStartItem.Checked = enabled;
        };

        var exitItem = new ToolStripMenuItem("退出（需要管理员 PIN）");
        exitItem.Click += async (_, _) => await ExitWithPinAsync();

        menu.Items.Add(statusItem);
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(lockItem);
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{ApiBase}/api/status");
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<StatusPayload>();
            _lastStatusSummary = payload is { Rules.Count: > 0 }
                ? string.Join("\n", payload.Rules.Select(r =>
                    $"{r.Name}：剩余 {FormatRemaining(r.RemainingSeconds)}{(r.Running ? "（运行中）" : "")}"))
                : "WinQuota：暂无限制规则";

            // NotifyIcon.Text 上限 63 字符
            var first = payload!.Rules.FirstOrDefault();
            _notifyIcon.Text = first is null
                ? "WinQuota 防沉迷"
                : Truncate($"WinQuota：{first.Name} 剩余 {FormatRemaining(first.RemainingSeconds)}", 63);
        }
        catch
        {
            _lastStatusSummary = "WinQuota：无法连接后台服务（服务未运行？）";
            _notifyIcon.Text = "WinQuota：服务未运行";
        }
    }

    private void ShowStatusBalloon()
    {
        _notifyIcon.ShowBalloonTip(10_000, "WinQuota 今日状态", _lastStatusSummary, ToolTipIcon.Info);
    }

    private static void OpenManagementUi()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ApiBase + "/",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 无法打开浏览器时忽略
        }
    }

    private async Task ExitWithPinAsync()
    {
        try
        {
            using var settingsResponse = await _http.GetAsync($"{ApiBase}/api/settings");
            var settings = settingsResponse.IsSuccessStatusCode
                ? await settingsResponse.Content.ReadFromJsonAsync<SettingsPayload>()
                : null;

            if (settings?.PinConfigured == true)
            {
                using var dialog = new PinDialog();
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var verify = new { pin = dialog.Pin };
                using var verifyResponse = await _http.PostAsJsonAsync($"{ApiBase}/api/pin/verify", verify);
                var result = await verifyResponse.Content.ReadFromJsonAsync<VerifyResult>();
                if (result?.Ok != true)
                {
                    MessageBox.Show("PIN 错误，无法退出。", "WinQuota",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
        }
        catch
        {
            // 服务不可达时不阻止退出（例如已卸载服务）
        }

        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }

    private static string FormatRemaining(long seconds)
    {
        var minutes = (long)Math.Ceiling(Math.Max(0, seconds) / 60.0);
        return minutes >= 60 ? $"{minutes / 60}小时{minutes % 60}分" : $"{minutes}分钟";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    /// <summary>运行时绘制一个简单的盾形“W”图标，避免携带二进制资源。</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(28, 108, 219));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var foreground = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold);
            var size = graphics.MeasureString("W", font);
            graphics.DrawString("W", font, foreground, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    private sealed record StatusPayload(string Date, string ComputerState, List<RuleStatus> Rules);

    private sealed record RuleStatus(long Id, string Name, string Type, bool Enabled, long RemainingSeconds, bool Running);

    private sealed record SettingsPayload(bool PinConfigured);

    private sealed record VerifyResult(bool Ok);
}
