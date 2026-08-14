using System.Collections.Concurrent;
using System.Drawing;

namespace WinQuota.Service.Services;

/// <summary>
/// 进程 exe 图标缓存：按路径提取图标并转为 PNG 字节，供管理界面进程选择器展示。
/// 失败（受保护文件 / 图标缺失）缓存为空值避免反复尝试。
/// </summary>
public static class IconCache
{
    private const int MaxEntries = 1000;
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static byte[]? GetPng(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        byte[]? png = null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                png = stream.ToArray();
            }
        }
        catch
        {
            png = null;
        }

        if (Cache.Count >= MaxEntries)
        {
            Cache.Clear();
        }

        Cache[path] = png;
        return png;
    }
}
