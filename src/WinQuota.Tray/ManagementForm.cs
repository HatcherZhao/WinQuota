using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WinQuota.Tray;

/// <summary>
/// 桌面主窗口：WebView2 内嵌管理界面，作为原生应用窗口展示
/// （关闭只是隐藏到托盘，真正退出走托盘菜单并需要 PIN）。
/// </summary>
internal sealed class ManagementForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private bool _initialized;
    private bool _allowClose;

    public ManagementForm()
    {
        Text = "WinQuota 防沉迷管理";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1220, 800);
        MinimumSize = new Size(860, 600);
        ShowInTaskbar = true;
        Icon = AppIcon.Create();

        // 用户点 X 只是隐藏到托盘；真正的退出走托盘菜单（PIN 保护）
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // WebView2 的用户数据目录必须可写：Program Files 下不行，放到 %LocalAppData%。
        _webView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinQuota", "WebView2"),
        };

        Controls.Add(_webView);
        Load += async (_, _) => await InitializeWebViewAsync();
    }

    /// <summary>程序退出时的真实关闭（绕过“隐藏到托盘”）。</summary>
    public void ReallyClose()
    {
        _allowClose = true;
        Close();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Navigate(TrayContext.ApiBasePublic + "/");
            _initialized = true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // 目标机器没有 WebView2 运行时：回退系统默认浏览器
            TrayContext.OpenUrlInBrowser(TrayContext.ApiBasePublic + "/");
            Close();
        }
    }
}
