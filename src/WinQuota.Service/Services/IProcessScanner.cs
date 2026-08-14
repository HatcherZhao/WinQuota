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
}
