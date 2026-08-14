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
    private readonly WinQuotaOptions _options;

    public WtsComputerUsageMonitor(IOptions<WinQuotaOptions> options)
    {
        _options = options.Value;
    }

    public ComputerUsageState GetState()
    {
        var sessionId = WtsSession.FindActiveSessionId();
        if (sessionId < 0)
        {
            return ComputerUsageState.NoUserSession;
        }

        if (!WtsSession.QueryInfoEx(sessionId, out var info))
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
