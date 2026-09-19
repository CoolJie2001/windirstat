namespace WdsShell.Core.Cleanup;

public static class DefaultCleanupRules
{
    public static IReadOnlyList<CleanupRule> Create()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempRoot = Normalize(Path.Combine(localAppData, "Temp"));

        var rules = new List<CleanupRule>();
        if (Directory.Exists(tempRoot))
        {
            rules.Add(new CleanupRule
            {
                Id = "user-temp",
                Category = "临时文件",
                Description = "当前用户临时目录中长期未修改的普通文件",
                MinimumAgeDays = 30,
                DefaultSelected = false,
                IsPathAllowed = path => IsUnder(path, tempRoot),
            });
        }

        AddChromiumRules(rules, localAppData, "Google", "Chrome", "Chrome / Edge 浏览器缓存");
        AddChromiumRules(rules, localAppData, "Microsoft", "Edge", "Chrome / Edge 浏览器缓存");

        var firefoxRoot = Normalize(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"));
        if (Directory.Exists(firefoxRoot))
        {
            rules.Add(new CleanupRule
            {
                Id = "firefox-cache",
                Category = "Firefox 缓存",
                Description = "Firefox profile 下可重建的 cache2 文件",
                MinimumAgeDays = 14,
                DefaultSelected = true,
                IsPathAllowed = path => IsProfileChild(path, firefoxRoot, ["cache2"]),
            });
        }

        return rules;
    }

    private static void AddChromiumRules(
        ICollection<CleanupRule> rules,
        string localAppData,
        string vendor,
        string product,
        string category)
    {
        var profilesRoot = Normalize(Path.Combine(localAppData, vendor, product, "User Data"));
        if (!Directory.Exists(profilesRoot)) return;

        var leaves = new[]
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            Path.Combine("Network", "Cache"),
            Path.Combine("Service Worker", "CacheStorage"),
        };

        rules.Add(new CleanupRule
        {
            Id = $"{product.ToLowerInvariant()}-cache",
            Category = category,
            Description = "浏览器 profile 下可重新生成的缓存文件",
            MinimumAgeDays = 14,
            DefaultSelected = true,
            IsPathAllowed = path => IsProfileChild(path, profilesRoot, leaves),
        });
    }

    private static bool IsProfileChild(string path, string profilesRoot, IReadOnlyList<string> leaves)
    {
        var relative = RelativePath(path, profilesRoot);
        if (relative is null) return false;

        var separator = Path.DirectorySeparatorChar;
        var parts = relative.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var leaf = string.Join(separator, parts.Skip(1).TakeWhile((_, index) => index < 3));
        return leaves.Any(candidate =>
            leaf.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith(candidate + separator, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, string root)
    {
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RelativePath(string path, string root)
    {
        if (!IsUnder(path, root)) return null;
        return Path.GetRelativePath(root, path);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
