using System.ComponentModel;
using Avalonia.Controls;
using WdsShell.App.Services;

namespace WdsShell.App.Views;

/// <summary>下拉项：存的是与上游对齐的键名，显示的是中文。</summary>
public sealed record Choice(string Key, string Label)
{
    public override string ToString() => Label;
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Current;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = _settings;

        Fill(ThemeBox, _settings.DarkMode, v => _settings.DarkMode = v,
            ("UseWindows", "跟随系统"), ("Light", "浅色"), ("Dark", "深色"));
        Fill(BackdropBox, _settings.Backdrop, v => _settings.Backdrop = v,
            ("Mica", "Mica（云母）"), ("Acrylic", "Acrylic（亚克力）"), ("None", "无（纯色底）"));

        // 材质是逐窗口的属性，光靠绑定到设置单例不会自己装到这个对话框上：
        // 不在这里走一遍，设置窗口就会一直停在 XAML 里写死的那条 Mica hint 上，跟主窗不一致。
        ApplyMaterial();
        // 构造期还没有 HWND，DWM 那次补设要等窗口显示之后。
        Opened += (_, _) => ApplyMaterial();

        // "恢复默认"把两个下拉也带回默认项，其余控件靠绑定自己跟。
        _settings.PropertyChanged += OnSettingsChanged;

        // 设置单例是静态的，订阅不摘就把每次打开的对话框永久留在内存里。
        Closing += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            _settings.Save();
        };
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DarkMode):
                Select(ThemeBox, _settings.DarkMode);
                break;
            case nameof(AppSettings.Backdrop):
                Select(BackdropBox, _settings.Backdrop);
                ApplyMaterial();
                break;
        }
    }

    private void ApplyMaterial() => SystemBackdrop.ApplyTo(this, _settings.Backdrop);

    private void OnReset(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _settings.ResetToDefaults();

    private static void Fill(ComboBox box, string current, Action<string> apply,
        params (string Key, string Label)[] items)
    {
        foreach (var (key, label) in items) box.Items.Add(new Choice(key, label));
        Select(box, current);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is Choice c) apply(c.Key);
        };
    }

    private static void Select(ComboBox box, string key)
    {
        if (box.SelectedItem is Choice current && current.Key == key) return;
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is Choice c && c.Key == key)
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }
}
