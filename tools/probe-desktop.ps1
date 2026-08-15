$code = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll")]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static string Probe()
    {
        var sb = new StringBuilder(256);
        // 打开当前接收输入的桌面：Default = 未锁屏，Winlogon = 锁屏/安全桌面
        IntPtr h = OpenInputDesktop(0, false, 0x00FF); // DESKTOP_READOBJECTS 等通用权限
        if (h == IntPtr.Zero)
        {
            sb.Append("OpenInputDesktop failed: ").Append(Marshal.GetLastWin32Error());
        }
        else
        {
            var name = new StringBuilder(128);
            uint needed2;
            if (GetUserObjectInformation(h, 2 /* UOI_NAME */, name, 128, out needed2))
            {
                sb.Append("InputDesktop=").Append(name.ToString());
            }
            else
            {
                sb.Append("GetUserObjectInformation failed");
            }
            CloseDesktop(h);
        }

        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref lii))
        {
            uint idle = (uint)Environment.TickCount - lii.dwTime;
            sb.Append(" | IdleSeconds=").Append(idle / 1000);
        }
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $code -Language CSharp
[DesktopProbe]::Probe()
