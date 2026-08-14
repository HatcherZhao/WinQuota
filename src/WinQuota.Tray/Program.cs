namespace WinQuota.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "WinQuota.Tray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return; // 已有托盘实例在运行
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
    }
}
