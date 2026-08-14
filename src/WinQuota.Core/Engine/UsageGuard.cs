namespace WinQuota.Core.Engine;

/// <summary>
/// 用量防篡改判定（第四阶段防绕过）：
/// 服务持续运行期间记住每条规则当天见过的最大已用 / 奖励秒数，
/// 数据库读回值变小即说明有人绕过 API 直接改库（如把 used_seconds 改小重开额度）。
/// 已用秒数当天只会增长、奖励只经管理员 API 增长，因此单调性破坏即可判定篡改。
/// </summary>
public static class UsageGuard
{
    /// <summary>容忍 1 秒内的取整误差。</summary>
    public static bool IsTampered(long observedUsed, long rememberedMaxUsed, long observedBonus, long rememberedMaxBonus) =>
        observedUsed < rememberedMaxUsed - 1 || observedBonus < rememberedMaxBonus;
}
