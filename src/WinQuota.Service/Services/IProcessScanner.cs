namespace WinQuota.Service.Services;

public interface IProcessScanner
{
    /// <summary>枚举当前系统中的进程（PID 与进程名）。</summary>
    IReadOnlyList<ProcessSnapshot> Snapshot();

    /// <summary>按需解析进程的完整路径。仅当规则配置了路径匹配时才调用，避免每轮全量解析。</summary>
    string? TryGetExecutablePath(int pid);

    /// <summary>
    /// 按需解析进程 exe 的版本信息（ProductName / CompanyName），用于产品级匹配。
    /// 带缓存：同一 (pid, 进程名) 在 TTL 内只解析一次，控制每轮扫描的额外开销。
    /// </summary>
    Core.Engine.ProcessVersionInfo GetVersionInfo(int pid, string processName);

    /// <summary>
    /// 按需验证进程 exe 的数字签名并提取签名者 CN，用于签名者匹配（第四阶段防绕过）。
    /// WinVerifyTrust 较昂贵（每进程几十毫秒），仅在规则配置了 Signer 时对候选进程调用，且带缓存。
    /// </summary>
    Core.Engine.SignatureInfo GetSignatureInfo(int pid, string processName);
}
