using System.Drawing;

namespace WinQuota.Tray;

/// <summary>运行时绘制应用图标（蓝色圆底 + 白色 W），托盘与主窗口共用。</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(28, 108, 219));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var foreground = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold);
            var size = graphics.MeasureString("W", font);
            graphics.DrawString("W", font, foreground, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
