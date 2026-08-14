namespace WinQuota.Service.Services;

public interface IProcessScanner
{
    /// <summary>枚举当前系统中的进程（PID 与进程名）。</summary>
    IReadOnlyList<ProcessSnapshot> Snapshot();

    /// <summary>按需解析进程的完整路径。仅当规则配置了路径匹配时才调用，避免每轮全量解析。</summary>
    string? TryGetExecutablePath(int pid);
}
