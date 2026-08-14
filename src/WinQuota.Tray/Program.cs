namespace WinQuota.Tray;

internal static class Program
{
    internal const string ShowWindowEventName = @"Local\WinQuota_ShowWindow";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "WinQuota.Tray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：唤醒它显示主窗口后退出
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowWindowEventName);
                show.Set();
            }
            catch
            {
                // 实例恰好正在退出
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
    }
}
