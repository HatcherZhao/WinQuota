using Microsoft.Extensions.Options;

namespace WinQuota.Service.Services;

public enum ComputerUsageState
{
    /// <summary>用户会话活动且未锁屏、未空闲，计入整机使用时间。</summary>
    Active,

    /// <summary>没有处于活动状态的登录会话（已注销 / 无用户登录）。</summary>
    NoUserSession,

    /// <summary>会话已锁屏。</summary>
    Locked,

    /// <summary>超过空闲阈值没有任何输入。</summary>
    Idle,
}

public interface IComputerUsageMonitor
{
    ComputerUsageState GetState();
}

/// <summary>
/// 整机使用状态检测：
/// 仅当“存在活动会话 + 未锁屏 + 距最后一次键鼠输入未超过空闲阈值”时视为正在使用。
/// 会话锁屏状态与最后输入时间均来自 WTSSessionInfoEx（按会话查询，服务可用）。
/// </summary>
public sealed class WtsComputerUsageMonitor : IComputerUsageMonitor
{
    // 托盘上报的空闲数据超过该时长视为过期，回退 WTS 判定
    private static readonly TimeSpan TrayIdleFreshness = TimeSpan.FromSeconds(90);

    private readonly WinQuotaOptions _options;
    private readonly LiveStatus _liveStatus;

    public WtsComputerUsageMonitor(IOptions<WinQuotaOptions> options, LiveStatus liveStatus)
    {
        _options = options.Value;
        _liveStatus = liveStatus;
    }

    public ComputerUsageState GetState()
    {
        var sessionId = WtsSession.FindActiveSessionId();
        if (sessionId < 0)
        {
            return ComputerUsageState.NoUserSession;
        }

        var queryOk = WtsSession.QueryInfoEx(sessionId, out var info);
        var flagSaysLocked = queryOk && info.SessionFlags == 1;

        // 优先采用托盘在用户会话内实测的空闲时间（GetLastInputInfo 只有会话内进程才读得准）：
        // 部分 Windows 环境的 WTS SessionFlags 语义反转或被远控驱动干扰，
        // 有新鲜输入即视为正在使用，不受异常锁屏标志影响。
        if (_liveStatus.TryGetSessionIdle(TrayIdleFreshness) is { } trayIdleSeconds)
        {
            if (trayIdleSeconds <= _options.IdleThresholdSeconds)
            {
                return ComputerUsageState.Active;
            }

            return flagSaysLocked ? ComputerUsageState.Locked : ComputerUsageState.Idle;
        }

        if (!queryOk)
        {
            // 查询失败时按“正在使用”保守计数，避免因 API 异常导致限制失效。
            return ComputerUsageState.Active;
        }

        if (info.SessionFlags == 1)
        {
            return ComputerUsageState.Locked;
        }

        if (_options.IdleThresholdSeconds > 0 && info.LastInputTime > 0 && info.CurrentTime > info.LastInputTime)
        {
            var idleSeconds = (info.CurrentTime - info.LastInputTime) / 10_000_000.0;
            if (idleSeconds > _options.IdleThresholdSeconds)
            {
                return ComputerUsageState.Idle;
            }
        }

        return ComputerUsageState.Active;
    }
}
