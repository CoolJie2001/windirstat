namespace WdsShell.App;

internal static class CommandLineOptions
{
    public static string? GetScanFolder(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--scan-folder", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
