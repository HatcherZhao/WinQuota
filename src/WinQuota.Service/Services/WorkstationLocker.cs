using Microsoft.Extensions.Logging;

namespace WinQuota.Service.Services;

public interface IWorkstationLocker
{
    /// <summary>锁定当前活动会话的工作站（Windows 锁屏）。</summary>
    bool LockWorkstation();
}

/// <summary>
/// 从会话 0 的服务无法直接调用 LockWorkStation（它只作用于调用者所在会话），
/// 标准做法是用用户会话令牌在用户会话中启动 rundll32 执行锁屏。
/// </summary>
public sealed class UserSessionWorkstationLocker : IWorkstationLocker
{
    private const string LockCommand = "rundll32.exe user32.dll,LockWorkStation";

    private readonly ILogger<UserSessionWorkstationLocker> _logger;

    public UserSessionWorkstationLocker(ILogger<UserSessionWorkstationLocker> logger)
    {
        _logger = logger;
    }

    public bool LockWorkstation()
    {
        try
        {
            var sessionId = WtsSession.FindActiveSessionId();
            if (sessionId < 0)
            {
                return false;
            }

            var locked = WtsSession.LaunchInSession(sessionId, LockCommand);
            if (locked)
            {
                _logger.LogInformation("已发起工作站锁定（会话 {SessionId}）", sessionId);
            }

            return locked;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作站锁定失败");
            return false;
        }
    }
}
