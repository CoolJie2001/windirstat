using System.Collections.Concurrent;

namespace WdsShell.Core.Layout;

/// <summary>
/// 扩展名 → 颜色分配（对应上游 cushion color 注册表的角色）。
/// 用黄金角散布色相保证任意数量扩展名的颜色彼此远离；目录取"最大子文件"的颜色链。
/// 输出为 0xRRGGBB，UI 层自行转换，避免 Core 绑定任何图形类型。
/// </summary>
public static class ExtensionPalette
{
    private static readonly ConcurrentDictionary<string, uint> s_colors = new(StringComparer.OrdinalIgnoreCase);
    private static int _assigned;

    public static uint GetColor(string extension)
    {
        if (string.IsNullOrEmpty(extension)) extension = "(none)";
        return s_colors.GetOrAdd(extension, static ext =>
        {
            var i = Interlocked.Increment(ref _assigned);
            return HsvToRgb((i * 137.508) % 360, 0.62, 0.85);
        });
    }

    /// <summary>目录颜色 = 其最大文件后代的颜色（浅化以区分层级），无文件时返回中性灰。</summary>
    public static uint GetDirectoryTint(Models.DiskNode node, int depth)
    {
        var leaf = FindDominantFile(node, 0);
        if (leaf is null) return 0x808080;
        var c = GetColor(leaf.Extension);
        // 每深一层向白色靠拢一点，形成层级感
        var t = Math.Min(depth, 4) * 0.12;
        var r = (int)(((c >> 16) & 0xFF) * (1 - t) + 255 * t);
        var g = (int)(((c >> 8) & 0xFF) * (1 - t) + 255 * t);
        var b = (int)((c & 0xFF) * (1 - t) + 255 * t);
        return ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static Models.DiskNode? FindDominantFile(Models.DiskNode node, int depth)
    {
        if (depth > 3) return node.Kind == Models.NodeKind.File ? node : null;
        foreach (var child in node.SnapshotChildren())
        {
            if (child.Kind == Models.NodeKind.File) return child;
            if (child.IsDirectory)
            {
                var found = FindDominantFile(child, depth + 1);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static uint HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((uint)((r + m) * 255) << 16) | ((uint)((g + m) * 255) << 8) | (uint)((b + m) * 255);
    }
}

/// <summary>字节数 → 人类可读（B/KB/MB/GB/TB）。</summary>
public static class SizeFormat
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {units[u]}" : $"{v:0.##} {units[u]}";
    }
}
