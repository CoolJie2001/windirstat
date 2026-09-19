using WdsShell.Core.Engine;

namespace WdsShell.Core.Cleanup;

public static class DefaultCleanupRules
{
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Temporary and editor leftovers.
        ".tmp", ".temp", ".~tmp", ".swp", ".swo", ".swn",
        // Backup and abandoned copies.
        ".bak", ".bkp", ".backup", ".old", ".orig",
        // Rebuildable logs, caches and crash dumps. These are candidates only;
        // they are deliberately not selected automatically.
        ".cache", ".log", ".log1", ".log2", ".dmp", ".chk", ".err",
        // Incomplete browser/download artifacts.
        ".crdownload", ".part", ".partial", ".download",
    };

    public static IReadOnlyList<CleanupRule> Create() =>
    [
        new CleanupRule
        {
            Id = "zero-byte-file",
            Category = "0 字节文件",
            Description = "当前扫描中发现的空文件；可能是占位文件，请确认后再处理",
            DefaultSelected = false,
            IsMatch = static file => file.LogicalSize == 0,
        },
        new CleanupRule
        {
            Id = "junk-extension",
            Category = "典型垃圾扩展名",
            Description = "扩展名属于常见临时、备份、缓存、日志或未完成下载文件",
            DefaultSelected = false,
            IsMatch = static file => JunkExtensions.Contains(Path.GetExtension(file.Name)),
        },
    ];
}
