using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using WdsShell.App.Controls;
using WdsShell.App.Services;
using WdsShell.App.ViewModels;
using WdsShell.Core.Models;

namespace WdsShell.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly string? _initialScanPath;

    // Source 与列只能在代码里建：TreeDataGrid 11.1 没有 ItemsSource，列也不支持 XAML 声明。
    // 列可见性是设置项，改动时整体重建 source，故选中模型不是 readonly。
    private TreeSelectionModelBase<TreeNodeRow> _rowSelection;
    private HierarchicalTreeDataGridSource<TreeNodeRow>? _fileTreeSource;

    // 选中态在"树 → VM"与"VM → 树"两个方向之间来回传播，用这个开关掐掉回环。
    private bool _syncingSelection;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialScanPath)
    {
        _initialScanPath = initialScanPath;
        // 必须用源生成器产出的 InitializeComponent：它除加载 XAML 外还会给 x:Name 字段赋值。
        // 若自行改写一个无参同名重载，XAML 仍能正常渲染，但 Treemap 会一直是 null。
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        Treemap.ZoomRequested += _vm.ZoomTo;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.TreeRevealRequested += OnTreeRevealRequested;
        FileTree.DoubleTapped += OnFileTreeDoubleTapped;

        _rowSelection = RebuildFileTree();
        ApplyTheme();
        ApplyBackdrop();
        ApplyPaneWidth();
        // 构造时窗口还没建 HWND，DWM 那次设材质要在显示之后再补一次才落得上。
        Opened += async (_, _) =>
        {
            ApplyBackdrop();
            if (_initialScanPath is { } path)
                await _vm.ScanFolderAsync(path);
        };
        AppSettings.Current.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            AppSettings.Current.PropertyChanged -= OnSettingsChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.TreeRevealRequested -= OnTreeRevealRequested;
            _vm.Dispose();
        };
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DarkMode):
                ApplyTheme();
                break;
            case nameof(AppSettings.Backdrop):
                ApplyBackdrop();
                break;
            case nameof(AppSettings.LeftPaneWidth):
                ApplyPaneWidth();
                break;
            // 名称与占比是上游的强制列，只切换可选列才需要重建，动一次代价是一次整表重排。
            case nameof(AppSettings.ShowPercentage):
            case nameof(AppSettings.ShowSizePhysical):
            case nameof(AppSettings.ShowSizeLogical):
            case nameof(AppSettings.ShowFiles):
            case nameof(AppSettings.ShowFolders):
                _rowSelection = RebuildFileTree();
                break;
        }
    }

    /// <summary>对应上游 Options\DarkMode：默认跟随系统，也可强制浅色/深色。</summary>
    private void ApplyTheme() =>
        Application.Current!.RequestedThemeVariant = AppSettings.Current.DarkMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

    /// <summary>
    /// 材质。口径全部收在 <see cref="SystemBackdrop.ApplyTo"/>：Avalonia 的 hint 只算旧系统兜底，
    /// Win11 上真材质由 DWMWA_SYSTEMBACKDROP_TYPE 决定，设置窗口走的是同一个方法。
    /// </summary>
    private void ApplyBackdrop() =>
        SystemBackdrop.ApplyTo(this, AppSettings.Current.Backdrop);

    private void ApplyPaneWidth() =>
        MainGrid.ColumnDefinitions[0].Width = new GridLength(AppSettings.Current.LeftPaneWidth);

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow().ShowDialog(this);
        AppSettings.Current.Save();
    }

    /// <summary>
    /// 列顺序与宽度照抄上游 CFileTreeView::InitializeColumns 的 96dpi 基准值
    /// （名称 250 / 占比 105+30 / 其余各 90）；Last Change / Attributes / Owner
    /// 三列要等托管引擎补采集后再加（见 README ⏳）。
    /// 名称列不给 star：11.1 版列对象没有 MinWidth，star 会被固定列挤成 0，
    /// 不如照上游一样全部定宽、面板不够宽就出横向滚动条。
    /// </summary>
    private TreeSelectionModelBase<TreeNodeRow> RebuildFileTree()
    {
        var s = AppSettings.Current;
        var source = new HierarchicalTreeDataGridSource<TreeNodeRow>(_vm.TreeRoot);

        source.Columns.Add(new HierarchicalExpanderColumn<TreeNodeRow>(
            new TextColumn<TreeNodeRow, string>("名称", r => r.Name, new GridLength(250)),
            childSelector: r => r.Children,
            hasChildrenSelector: r => r.HasChildren,
            isExpandedSelector: r => r.IsExpanded));

        // 占比列 = 上游 DrawSubItem(COL_SIZE_PROPORTION) 的自绘单元格
        source.Columns.Add(new TemplateColumn<TreeNodeRow>("占比", ProportionCell, null,
            new GridLength(135), null));

        if (s.ShowPercentage)
            source.Columns.Add(new TextColumn<TreeNodeRow, string>("百分比", r => r.PercentText,
                new GridLength(90), RightAligned));
        if (s.ShowSizePhysical)
            source.Columns.Add(new TextColumn<TreeNodeRow, string>("大小（物理）", r => r.PhysicalText,
                new GridLength(90), RightAligned));
        if (s.ShowSizeLogical)
            source.Columns.Add(new TextColumn<TreeNodeRow, string>("大小（逻辑）", r => r.LogicalText,
                new GridLength(90), RightAligned));
        if (s.ShowFiles)
            source.Columns.Add(new TextColumn<TreeNodeRow, string>("文件", r => r.FilesText,
                new GridLength(90), RightAligned));
        if (s.ShowFolders)
            source.Columns.Add(new TextColumn<TreeNodeRow, string>("文件夹", r => r.FoldersText,
                new GridLength(90), RightAligned));

        FileTree.Source = source;
        _fileTreeSource = source;

        // 上游列表支持 Ctrl/Shift 多选；当前共享选中态只有一个槽位，先按单选接。
        var selection = (TreeSelectionModelBase<TreeNodeRow>)source.RowSelection!;
        selection.SingleSelect = true;
        selection.SelectionChanged += OnTreeSelectionChanged;

        // 重建换掉了整个选中模型，按 VM 里的唯一选中项把它补回树上。
        if (_vm.SelectedNode is { } node) SelectInTree(selection, node);
        return selection;
    }

    /// <summary>
    /// VM 的唯一选中项 → 树侧：先展开目标所在路径，再选中那一行。
    /// 11.1.1 的表格不会把模型行的 IsExpanded 变化同步进行索引（实测设完 true 行数仍不变），
    /// 所以自顶向下逐级调公开的 <see cref="HierarchicalTreeDataGridSource{T}.Expand"/>，
    /// 否则深层行根本不在索引里，Select 会静默落空。
    /// </summary>
    private void SelectInTree(TreeSelectionModelBase<TreeNodeRow> selection, DiskNode node)
    {
        if (_fileTreeSource is not { } source) return;
        if (_vm.Reveal(node) is not { } row) return;

        var path = _vm.ModelPathOf(row);
        for (var i = 1; i < path.Count; i++)
            source.Expand(path.Slice(0, i)); // 只展开祖先，选中项自身的展开态保持原样

        if (selection.IsSelected(path))
        {
            BringIntoView(path);
            return;
        }

        _syncingSelection = true;
        selection.Select(path);
        _syncingSelection = false;

        BringIntoView(path);
    }

    /// <summary>
    /// 把刚选中的行滚进可见区，对应上游 CFileTreeView 在 ExpandPathToItem 之后的 EnsureVisible。
    /// 11.1.1 没有公开的 ScrollIntoView，只能按"扁平行号 × 平均行高"自己算偏移；
    /// 且要排到下一个 UI 工作项，等表格把展开后的 extent 更新完。
    /// </summary>
    private void BringIntoView(Avalonia.Controls.IndexPath path)
    {
        if (_fileTreeSource is not { } source) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (source.Rows is not HierarchicalRows<TreeNodeRow> rows || FileTree.Scroll is not { } scroll) return;
            var flat = rows.ModelIndexToRowIndex(path);
            if (flat < 0) return;

            var offset = scroll.Offset;
            var rowHeight = scroll.Extent.Height / Math.Max(1, rows.Count);
            var top = flat * rowHeight;
            if (top >= offset.Y && top + rowHeight <= offset.Y + scroll.Viewport.Height) return;
            scroll.Offset = new Vector(offset.X, Math.Clamp(top, 0,
                Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        });
    }

    private static TextColumnOptions<TreeNodeRow> RightAligned =>
        new() { TextAlignment = TextAlignment.Right };

    /// <summary>占比列单元格：自绘控件的四个入参绑到行对象，单元格回收换 DataContext 时自动跟到新行。</summary>
    // 反射绑定与本仓库 XAML 侧的 {Binding} 同一机制（AvaloniaUseCompiledBindingsByDefault=false）；
    // 真要 PublishAot 时全项目一起换成编译绑定，不在这里单点特例。
#pragma warning disable IL2026
    private static readonly IDataTemplate ProportionCell = new FuncDataTemplate<TreeNodeRow>((_, _) =>
    {
        var bar = new TreeProportionBar { MinHeight = 24 };
        bar.Bind(TreeProportionBar.SubtreeFractionProperty, new Binding(nameof(TreeNodeRow.SubtreeFraction)));
        bar.Bind(TreeProportionBar.AbsoluteFractionProperty, new Binding(nameof(TreeNodeRow.AbsoluteFraction)));
        bar.Bind(TreeProportionBar.IndentProperty, new Binding(nameof(TreeNodeRow.Indent)));
        bar.Bind(TreeProportionBar.BaseColorProperty, new Binding(nameof(TreeNodeRow.BarColor)));
        return bar;
    }, supportsRecycling: true);
#pragma warning restore IL2026

    /// <summary>树里点选 → 写入唯一的 SelectedNode（区块图随即高亮同一项）。</summary>
    private void OnTreeSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<TreeNodeRow> e)
    {
        if (sender is not TreeSelectionModelBase<TreeNodeRow> selection
            || selection.SelectedItem is not { } row) return;
        if (ReferenceEquals(row.Node, _vm.SelectedNode))
        {
            _vm.LocateTreeSelection(row.Node);
            return;
        }

        _syncingSelection = true;
        _vm.SelectedNode = row.Node;
        _syncingSelection = false;
        _vm.LocateTreeSelection(row.Node);
    }

    /// <summary>
    /// 区块图单击选中 → 树侧按上游 EmulateInteractiveSelection 的路子展开定位。
    /// 展开与选中由 <see cref="SelectInTree"/> 负责。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedNode) || _syncingSelection) return;
        if (_vm.SelectedNode is { } node) SelectInTree(_rowSelection, node);
    }

    private void OnTreeRevealRequested(DiskNode node) => SelectInTree(_rowSelection, node);


    /// <summary>
    /// 对应上游 CTreeListControl::OnItemDoubleClick：目录切换展开状态。
    /// 上游对文件是"用外壳打开"，这里暂不引入启动外部程序的副作用。
    /// </summary>
    private void OnFileTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_rowSelection.SelectedItem is { } row && row.Node.Kind == NodeKind.Directory)
            row.IsExpanded = !row.IsExpanded;
    }
}
