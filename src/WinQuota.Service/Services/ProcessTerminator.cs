using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinQuota.Service.Services;

public interface IProcessTerminator
{
    /// <summary>终止指定进程（含子进程树）。返回实际终止的数量。</summary>
    int Terminate(IEnumerable<ProcessSnapshot> processes);
}

public sealed class ProcessTerminator : IProcessTerminator
{
    private readonly ILogger<ProcessTerminator> _logger;

    public ProcessTerminator(ILogger<ProcessTerminator> logger)
    {
        _logger = logger;
    }

    public int Terminate(IEnumerable<ProcessSnapshot> processes)
    {
        var killed = 0;
        foreach (var snapshot in processes)
        {
            try
            {
                using var process = Process.GetProcessById(snapshot.Pid);
                // 终止前再次核对进程名，防止 PID 复用误杀。
                if (!string.Equals(process.ProcessName + ".exe", snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                killed++;
                _logger.LogInformation("已终止进程 {Process} (PID {Pid})", snapshot.ProcessName, snapshot.Pid);
            }
            catch (ArgumentException)
            {
                // 进程已退出
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "终止进程 {Process} (PID {Pid}) 失败", snapshot.ProcessName, snapshot.Pid);
            }
        }

        return killed;
    }
}
