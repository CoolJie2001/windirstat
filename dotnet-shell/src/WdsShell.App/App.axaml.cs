using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WdsShell.App.Services;
using WdsShell.App.Views;

namespace WdsShell.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppSettings.Current.PropertyChanged += OnSettingsChanged;
            ContextMenuRegistration.Apply(AppSettings.Current.EnableContextMenu);
            var scanFolder = CommandLineOptions.GetScanFolder(desktop.Args);
            desktop.MainWindow = new MainWindow(scanFolder);
            // 设置窗口关闭时会存一次；从任务栏直接结束进程时靠这里兜底。
            desktop.Exit += (_, _) =>
            {
                AppSettings.Current.PropertyChanged -= OnSettingsChanged;
                AppSettings.Current.Save();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.EnableContextMenu)) return;

        ContextMenuRegistration.Apply(AppSettings.Current.EnableContextMenu);
        AppSettings.Current.Save();
    }
}
