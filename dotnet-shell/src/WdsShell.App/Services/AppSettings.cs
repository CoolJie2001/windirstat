using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WdsShell.Core.Engine;

namespace WdsShell.App.Services;

/// <summary>
/// 全应用共享的设置单例：属性即设置项，改动通过 PropertyChanged 立刻被视图应用，
/// 落盘由设置窗口关闭和应用退出两个时机触发。
///
/// 键名尽量对齐上游（HKCU\Software\WinDirStat\WinDirStat\Options，见 windirstat/Options.h），
/// 便于对照；带 "shell 自有" 注释的项上游没有对应物。
/// 存储格式是 key=value 文本而非反射式 JSON —— 本项目开了 PublishAot，
/// 反射序列化器在裁剪后拿不到属性，会静默丢设置。
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    public static AppSettings Current { get; } = Load();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskScope", "settings.ini");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WdsShell", "settings.ini");

    /// <summary>对应上游 Options\DarkMode（默认 DM_USE_WINDOWS）。</summary>
    [ObservableProperty] private string _darkMode = "UseWindows";

    /// <summary>shell 自有：窗口背景材质。上游是 Win32 原生标题栏，没有这一层。</summary>
    [ObservableProperty] private string _backdrop = "Mica";

    // ---- 目录树列可见性：默认值取自上游 FileTreeColumnVisibility {1,1,1,1,1,0,1,0,1,0,0}
    //      按 ITEMCOLUMNS 顺序展开即 名称/占比/百分比/物理/逻辑/文件 显示，项目/文件夹/时间/属性/所有者 隐藏。
    //      名称与占比是上游的强制列，不进设置。----
    [ObservableProperty] private bool _showPercentage = true;
    [ObservableProperty] private bool _showSizePhysical = true;
    [ObservableProperty] private bool _showSizeLogical = true;
    [ObservableProperty] private bool _showFiles = true;
    [ObservableProperty] private bool _showFolders;

    /// <summary>shell 自有：上游用分割条比例（MainSplitterPos，默认 0.5/0.75），这里直接给像素宽。</summary>
    [ObservableProperty] private double _leftPaneWidth = 620;

    /// <summary>脚手架钳制项，上游无对应：区块图递归深度与每层子块上限。</summary>
    [ObservableProperty] private int _treemapMaxDepth = 3;
    [ObservableProperty] private int _treemapChildrenCap = 24;

    // ---- 扫描排除项：名称与默认值对齐上游 Options.h:174-182、286 ----
    [ObservableProperty] private bool _showFreeSpace = true;
    [ObservableProperty] private bool _excludeHiddenFile;
    [ObservableProperty] private bool _excludeProtectedFile;
    [ObservableProperty] private bool _excludeLinkedDirectories = true;
    [ObservableProperty] private int _scanningThreads = 4;

    public ScanOptions ToScanOptions() => new()
    {
        ExcludeHiddenFile = ExcludeHiddenFile,
        ExcludeProtectedFile = ExcludeProtectedFile,
        ExcludeLinkedDirectories = ExcludeLinkedDirectories,
        WorkerThreads = ScanningThreads,
        ShowFreeSpace = ShowFreeSpace,
    };

    public void ResetToDefaults()
    {
        var d = new AppSettings();
        DarkMode = d.DarkMode;
        Backdrop = d.Backdrop;
        ShowPercentage = d.ShowPercentage;
        ShowSizePhysical = d.ShowSizePhysical;
        ShowSizeLogical = d.ShowSizeLogical;
        ShowFiles = d.ShowFiles;
        ShowFolders = d.ShowFolders;
        LeftPaneWidth = d.LeftPaneWidth;
        TreemapMaxDepth = d.TreemapMaxDepth;
        TreemapChildrenCap = d.TreemapChildrenCap;
        ShowFreeSpace = d.ShowFreeSpace;
        ExcludeHiddenFile = d.ExcludeHiddenFile;
        ExcludeProtectedFile = d.ExcludeProtectedFile;
        ExcludeLinkedDirectories = d.ExcludeLinkedDirectories;
        ScanningThreads = d.ScanningThreads;
    }

    private static AppSettings Load()
    {
        var s = new AppSettings();
        string[] lines;
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return s; // 首次运行或文件不可读：用默认值
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            s.Apply(key, value);
        }
        return s;
    }

    private void Apply(string key, string value)
    {
        switch (key)
        {
            case nameof(DarkMode):
                if (value is "UseWindows" or "Light" or "Dark") DarkMode = value;
                break;
            case nameof(Backdrop):
                if (value is "Mica" or "Acrylic" or "None") Backdrop = value;
                break;
            case nameof(ShowPercentage): ShowPercentage = ParseBool(value); break;
            case nameof(ShowSizePhysical): ShowSizePhysical = ParseBool(value); break;
            case nameof(ShowSizeLogical): ShowSizeLogical = ParseBool(value); break;
            case nameof(ShowFiles): ShowFiles = ParseBool(value); break;
            case nameof(ShowFolders): ShowFolders = ParseBool(value); break;
            case nameof(LeftPaneWidth): LeftPaneWidth = ParseDouble(value, 400, 1600); break;
            case nameof(TreemapMaxDepth): TreemapMaxDepth = ParseInt(value, 1, 6); break;
            case nameof(TreemapChildrenCap): TreemapChildrenCap = ParseInt(value, 4, 64); break;
            case nameof(ShowFreeSpace): ShowFreeSpace = ParseBool(value); break;
            case nameof(ExcludeHiddenFile): ExcludeHiddenFile = ParseBool(value); break;
            case nameof(ExcludeProtectedFile): ExcludeProtectedFile = ParseBool(value); break;
            case nameof(ExcludeLinkedDirectories): ExcludeLinkedDirectories = ParseBool(value); break;
            case nameof(ScanningThreads): ScanningThreads = ParseInt(value, 1, 16); break;
        }
    }

    private static bool ParseBool(string value) =>
        value is "1" or "true" or "True" or "yes";

    private static int ParseInt(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : min;

    private static double ParseDouble(string value, double min, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : min;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, string.Join(Environment.NewLine, Lines()));
        }
        catch
        {
            // 设置写盘失败不该影响正在跑的扫描，静默即可
        }
    }

    private IEnumerable<string> Lines()
    {
        yield return "; DiskScope settings. Key names follow upstream HKCU\\Software\\WinDirStat\\WinDirStat\\Options";
        yield return "; This file is plain ASCII on purpose so any ini tool can read it.";
        yield return $"{nameof(DarkMode)}={DarkMode}";
        yield return $"{nameof(Backdrop)}={Backdrop}";
        yield return $"{nameof(ShowPercentage)}={B(ShowPercentage)}";
        yield return $"{nameof(ShowSizePhysical)}={B(ShowSizePhysical)}";
        yield return $"{nameof(ShowSizeLogical)}={B(ShowSizeLogical)}";
        yield return $"{nameof(ShowFiles)}={B(ShowFiles)}";
        yield return $"{nameof(ShowFolders)}={B(ShowFolders)}";
        yield return $"{nameof(LeftPaneWidth)}={LeftPaneWidth.ToString(CultureInfo.InvariantCulture)}";
        yield return $"{nameof(TreemapMaxDepth)}={TreemapMaxDepth}";
        yield return $"{nameof(TreemapChildrenCap)}={TreemapChildrenCap}";
        yield return $"{nameof(ShowFreeSpace)}={B(ShowFreeSpace)}";
        yield return $"{nameof(ExcludeHiddenFile)}={B(ExcludeHiddenFile)}";
        yield return $"{nameof(ExcludeProtectedFile)}={B(ExcludeProtectedFile)}";
        yield return $"{nameof(ExcludeLinkedDirectories)}={B(ExcludeLinkedDirectories)}";
        yield return $"{nameof(ScanningThreads)}={ScanningThreads}";
    }

    private static string B(bool value) => value ? "1" : "0";
}
