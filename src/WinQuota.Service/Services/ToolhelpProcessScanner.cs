using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinQuota.Service.Services;

/// <summary>
/// 基于 Toolhelp32 快照的进程扫描器（纯 Win32 P/Invoke，无 COM 依赖）。
/// 之前使用 WMI（System.Management）在长期运行的服务中出现静默挂起，故替换为本实现。
/// </summary>
public sealed class ToolhelpProcessScanner : IProcessScanner
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly ILogger<ToolhelpProcessScanner> _logger;
    private readonly object _versionCacheGate = new();
    private Dictionary<(int Pid, string Name), (DateTime SeenUtc, Core.Engine.ProcessVersionInfo Info)> _versionCache = new();
    private Dictionary<(int Pid, string Name), (DateTime SeenUtc, Core.Engine.SignatureInfo Info)> _signatureCache = new();

    private static readonly TimeSpan VersionCacheTtl = TimeSpan.FromMinutes(2);
    private const int VersionCacheMaxEntries = 4096;
    private const int FreshSignatureVerifiesPerWindow = 30;
    private (DateTime WindowStartUtc, int Count) _signatureBudget = (DateTime.MinValue, 0);

    public ToolhelpProcessScanner(ILogger<ToolhelpProcessScanner> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        var list = new List<ProcessSnapshot>(300);
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            _logger.LogError("进程快照创建失败：{Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return list;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>(),
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return list;
            }

            do
            {
                if (!string.IsNullOrEmpty(entry.szExeFile))
                {
                    list.Add(new ProcessSnapshot((int)entry.th32ProcessID, entry.szExeFile, null));
                }
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return list;
    }

    public string? TryGetExecutablePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            // 进程已退出、跨位数（WoW64）或受保护进程无法读取模块，返回 null 即可
            return null;
        }
    }

    public Core.Engine.ProcessVersionInfo GetVersionInfo(int pid, string processName)
    {
        var key = (pid, processName);
        lock (_versionCacheGate)
        {
            if (_versionCache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.SeenUtc < VersionCacheTtl)
            {
                return entry.Info;
            }
        }

        string? path = null;
        string? productName = null;
        string? companyName = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                productName = string.IsNullOrWhiteSpace(version.ProductName) ? null : version.ProductName;
                companyName = string.IsNullOrWhiteSpace(version.CompanyName) ? null : version.CompanyName;
            }
        }
        catch
        {
            // 解析失败按空信息处理
        }

        var info = new Core.Engine.ProcessVersionInfo(productName, companyName, path);
        lock (_versionCacheGate)
        {
            if (_versionCache.Count >= VersionCacheMaxEntries)
            {
                _versionCache.Clear();
            }

            _versionCache[key] = (DateTime.UtcNow, info);
        }

        return info;
    }

    public Core.Engine.SignatureInfo GetSignatureInfo(int pid, string processName)
    {
        var key = (pid, processName);
        lock (_versionCacheGate)
        {
            if (_signatureCache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.SeenUtc < VersionCacheTtl)
            {
                return entry.Info;
            }
        }

        // WinVerifyTrust 较昂贵（每进程数毫秒到数十毫秒），限制每个时间窗口内的新验证次数；
        // 预算耗尽时本轮返回“不可信”且不缓存，下轮窗口再验证，避免大进程表拖慢扫描周期。
        var now = DateTime.UtcNow;
        lock (_versionCacheGate)
        {
            if (now - _signatureBudget.WindowStartUtc > TimeSpan.FromSeconds(5))
            {
                _signatureBudget = (now, 0);
            }

            if (_signatureBudget.Count >= FreshSignatureVerifiesPerWindow)
            {
                return default;
            }

            _signatureBudget = (_signatureBudget.WindowStartUtc, _signatureBudget.Count + 1);
        }

        var path = TryGetExecutablePath(pid);
        var signature = string.IsNullOrEmpty(path)
            ? default(Core.Engine.SignatureInfo)
            : FileSignatureReader.Read(path);

        lock (_versionCacheGate)
        {
            if (_signatureCache.Count >= VersionCacheMaxEntries)
            {
                _signatureCache.Clear();
            }

            _signatureCache[key] = (DateTime.UtcNow, signature);
        }

        return signature;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}
