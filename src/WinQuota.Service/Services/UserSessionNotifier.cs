using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinQuota.Service.Services;

/// <summary>
/// 用户会话通知器：
/// 1. 优先在活动会话中启动 PowerShell 弹出 Toast；
/// 2. 失败时回退 msg.exe 会话消息框；
/// 3. 全部失败时仅写日志。
/// </summary>
public sealed class UserSessionNotifier : INotifier
{
    private static readonly string PowerShellPath =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly ILogger<UserSessionNotifier> _logger;

    public UserSessionNotifier(ILogger<UserSessionNotifier> logger)
    {
        _logger = logger;
    }

    public void Notify(string title, string message)
    {
        _logger.LogInformation("通知：{Title} - {Message}", title, message);
        try
        {
            if (TryShowToast(title, message))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toast 通知失败，回退 msg.exe");
        }

        TryShowMessageDialog(title, message);
    }

    private bool TryShowToast(string title, string message)
    {
        var sessionId = WtsSession.FindActiveSessionId();
        if (sessionId < 0)
        {
            return false;
        }

        var script = BuildToastScript(title, message);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var commandLine = $"\"{PowerShellPath}\" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";
        return WtsSession.LaunchInSession(sessionId, commandLine);
    }

    private static string BuildToastScript(string title, string message)
    {
        // PowerShell 单引号字符串中，单引号以两个单引号转义。
        var psTitle = title.Replace("'", "''");
        var psMessage = message.Replace("'", "''");
        return $"""
            $ErrorActionPreference = 'SilentlyContinue'
            [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
            $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
            $nodes = $template.GetElementsByTagName('text')
            [void]$nodes.Item(0).AppendChild($template.CreateTextNode('{psTitle}'))
            [void]$nodes.Item(1).AppendChild($template.CreateTextNode('{psMessage}'))
            $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.Explorer').Show($toast)
            Start-Sleep -Seconds 2
            """;
    }

    private void TryShowMessageDialog(string title, string message)
    {
        try
        {
            var sessionId = WtsSession.FindActiveSessionId();
            if (sessionId < 0)
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msg.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(sessionId.ToString());
            startInfo.ArgumentList.Add("/time:20");
            startInfo.ArgumentList.Add($"[{title}] {message}");
            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msg.exe 回退通知失败（Home 版 Windows 可能没有 msg.exe）");
        }
    }
}
