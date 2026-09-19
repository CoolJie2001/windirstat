using System.Security;
using Microsoft.Win32;

namespace WdsShell.App.Services;

/// <summary>
/// Registers the per-user Explorer verb used by the folder context menu.
/// HKCU is intentional: enabling the feature must not require elevation.
/// </summary>
internal static class ContextMenuRegistration
{
    private const string MenuKeyPath = @"Software\Classes\Directory\shell\DiskScope";
    private const string MenuText = "使用 DiskScope 分析空间";

    public static bool Apply(bool enabled)
    {
        return enabled ? RegisterCurrentPublishedExe() : TryUnregister();
    }

    public static bool RegisterCurrentPublishedExe()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "DiskScope",
                StringComparison.OrdinalIgnoreCase)) return false;

        return TryRegister(processPath);
    }

    public static bool TryRegister(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath)) return false;

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath)) return false;

            using var menu = Registry.CurrentUser.CreateSubKey(MenuKeyPath);
            if (menu is null) return false;

            menu.SetValue(null, MenuText, RegistryValueKind.String);
            menu.SetValue("MUIVerb", MenuText, RegistryValueKind.String);
            menu.SetValue("Icon", fullPath, RegistryValueKind.String);

            using var command = menu.CreateSubKey("command");
            if (command is null) return false;

            command.SetValue(null, $"\"{fullPath}\" --scan-folder \"%1\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static bool TryUnregister()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
