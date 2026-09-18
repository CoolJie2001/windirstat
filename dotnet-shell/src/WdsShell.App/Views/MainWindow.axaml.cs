using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WdsShell.App.ViewModels;

namespace WdsShell.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
            Treemap.Navigated += node => vm.NavigateInto(node);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnFileListDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && FileList.SelectedItem is NodeRow row)
            vm.NavigateInto(row.Node);
    }
}
