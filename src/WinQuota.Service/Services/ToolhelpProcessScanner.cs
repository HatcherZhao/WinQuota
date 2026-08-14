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
