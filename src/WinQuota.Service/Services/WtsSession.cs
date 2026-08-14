using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinQuota.Service.Services;

/// <summary>
/// Windows 终端服务（WTS）会话工具：
/// 枚举会话、查询会话扩展信息（锁屏状态 / 最后输入时间）、在用户会话中启动进程。
/// 服务运行在会话 0，任何面向桌面用户的操作都必须通过这些 API 定位并进入用户会话。
/// 注意：不要使用 WTSGetActiveConsoleSessionId —— 部分 Windows 版本的 wtsapi32.dll 不按名称导出它。
/// </summary>
internal static class WtsSession
{
    public const int WtsActive = 0;
    private const int WtsSessionInfoEx = 25;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>查找处于活动状态（WTSActive）的会话 ID，找不到返回 -1。</summary>
    public static int FindActiveSessionId()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionsPtr, out var count))
        {
            return -1;
        }

        try
        {
            var structSize = Marshal.SizeOf<NativeMethods.WtsSessionInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoW>(sessionsPtr + i * structSize);
                if (info.State == WtsActive)
                {
                    return info.SessionId;
                }
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionsPtr);
        }

        return -1;
    }

    public readonly record struct SessionInfoEx(
        int SessionId,
        int SessionState,
        int SessionFlags,
        long LogonTime,
        long LastInputTime,
        long CurrentTime,
        string? UserName,
        string? DomainName);

    /// <summary>
    /// 查询会话扩展信息（WTSSessionInfoEx）。
    /// SessionFlags：0 = 未锁屏，1 = 已锁屏（其余值视为未锁，保守处理）。
    /// LastInputTime / CurrentTime 为 FILETIME（1601 以来的 100ns 单位），差值即空闲时长。
    /// </summary>
    public static bool QueryInfoEx(int sessionId, out SessionInfoEx info)
    {
        info = default;
        if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsSessionInfoEx, out var bufferPtr, out _))
        {
            return false;
        }

        try
        {
            var ex = Marshal.PtrToStructure<NativeMethods.WtsInfoExW>(bufferPtr);
            if (ex.Level != 1)
            {
                return false;
            }

            info = new SessionInfoEx(
                ex.Data.SessionId,
                ex.Data.SessionState,
                ex.Data.SessionFlags,
                ex.Data.LogonTime,
                ex.Data.LastInputTime,
                ex.Data.CurrentTime,
                ex.Data.UserName,
                ex.Data.DomainName);
            return true;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(bufferPtr);
        }
    }

    /// <summary>用用户会话的令牌在该会话中启动进程（服务向桌面展示 UI / 执行动作的唯一通道）。</summary>
    public static bool LaunchInSession(int sessionId, string commandLine)
    {
        if (sessionId < 0)
        {
            return false;
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSQueryUserToken(会话 {sessionId}) 失败");
        }

        using (token)
        {
            var startup = new NativeMethods.StartupInfoW { cb = Marshal.SizeOf<NativeMethods.StartupInfoW>() };
            if (!NativeMethods.CreateProcessAsUserW(
                    token, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero,
                    Environment.SystemDirectory, ref startup, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUserW 失败");
            }

            return true;
        }
    }

    private static class NativeMethods
    {
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSEnumerateSessionsW(
            IntPtr hServer,
            int reserved,
            int version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQuerySessionInformationW(
            IntPtr hServer,
            int sessionId,
            int infoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(int sessionId, out SafeTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessAsUserW(
            SafeTokenHandle hToken,
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref StartupInfoW lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WtsSessionInfoW
        {
            public int SessionId;
            public string WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WtsInfoExW
        {
            public int Level;
            public WtsInfoExLevel1W Data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WtsInfoExLevel1W
        {
            public int SessionId;
            public int SessionState;
            public int SessionFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
            public string WinStationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
            public string UserName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
            public string DomainName;
            public long LogonTime;
            public long ConnectTime;
            public long DisconnectTime;
            public long LastInputTime;
            public long CurrentTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfoW
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        public sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            [System.Security.SecurityCritical]
            public SafeTokenHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
    }
}
