using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinQuota.Service.Services;

public interface IJobObjectManager
{
    /// <summary>把进程加入规则对应的 Job Object。之后由该进程派生的子进程自动进入同一 Job。</summary>
    void AssignToRule(long ruleId, int pid);

    /// <summary>终止规则 Job 中的全部进程（含游戏拉起的、进程名未匹配的子进程）。</summary>
    void TerminateRule(long ruleId);
}

/// <summary>
/// 每条应用规则一个命名 Job Object（Local\WinQuota\Rule&lt;id&gt;）：
/// 命中的进程被分配进 Job，其后续子进程自动继承成员资格；
/// 额度耗尽时 TerminateJobObject 一次终止整棵进程树——即使子进程换了名字。
/// 不设置 KILL_ON_JOB_CLOSE：服务自身重启不应影响正在运行的游戏。
/// </summary>
public sealed class JobObjectManager : IJobObjectManager, IDisposable
{
    private readonly ILogger<JobObjectManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<long, IntPtr> _jobs = [];

    public JobObjectManager(ILogger<JobObjectManager> logger)
    {
        _logger = logger;
    }

    public void AssignToRule(long ruleId, int pid)
    {
        try
        {
            var job = GetOrCreateJob(ruleId);
            if (job == IntPtr.Zero)
            {
                return;
            }

            var process = NativeMethods.OpenProcess(NativeMethods.ProcessSetQuota, false, pid);
            if (process == IntPtr.Zero)
            {
                return; // 受保护进程（如反作弊）打不开句柄，留待进程名匹配兜底
            }

            try
            {
                if (!NativeMethods.AssignProcessToJobObject(job, process))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogDebug("进程 {Pid} 加入 Job 失败（Win32 {Error}）", pid, error);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AssignToRule({RuleId}, {Pid}) 异常", ruleId, pid);
        }
    }

    public void TerminateRule(long ruleId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(ruleId, out var job))
            {
                return;
            }

            if (NativeMethods.TerminateJobObject(job, 1))
            {
                _logger.LogInformation("已终止规则 {RuleId} 的 Job Object（含派生子进程）", ruleId);
            }
            else
            {
                _logger.LogWarning("TerminateJobObject(规则 {RuleId}) 失败：{Error}",
                    ruleId, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
    }

    private IntPtr GetOrCreateJob(long ruleId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(ruleId, out var existing))
            {
                return existing;
            }

            // 注意：内核对象命名空间不会自动创建中间目录，必须使用扁平名称。
            var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, $"Local\\WinQuota_Rule{ruleId}");
            if (job == IntPtr.Zero)
            {
                _logger.LogWarning("CreateJobObject(规则 {RuleId}) 失败：{Error}",
                    ruleId, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return IntPtr.Zero;
            }

            _jobs[ruleId] = job;
            return job;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values)
            {
                NativeMethods.CloseHandle(job);
            }

            _jobs.Clear();
        }
    }

    private static class NativeMethods
    {
        public const uint ProcessSetQuota = 0x0100;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
