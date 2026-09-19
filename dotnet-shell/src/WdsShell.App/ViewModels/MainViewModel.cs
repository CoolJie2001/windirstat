using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WdsShell.App.Services;
using WdsShell.Core.Cleanup;
using WdsShell.Core.Engine;
using WdsShell.Core.Layout;
using WdsShell.Core.Models;
using WdsShell.Interop;

namespace WdsShell.App.ViewModels;

/// <summary>
/// 主视图模型。UI ↔ 引擎之间只经由 IDiskScanEngine + DiskNode 快照交互，
/// 所有刷新走 250ms 节流定时器 —— 这是扫描期不卡 UI 的关键约束，
/// 未来接 wdscore.dll 的高频事件流时同样依赖它。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const double TreemapSubdivideArea = 0.012; // 面积占比超过该值才下钻一层

    /// <summary>每 tick 遍历可见行的时间预算：用户把大目录全展开后，刷新不许挤占输入。</summary>
    private const int RowUpdateBudgetMs = 12;

    /// <summary>设置单例：区块图钳制参数与扫描排除项都由它驱动，改完即时生效。</summary>
    public AppSettings Settings { get; } = AppSettings.Current;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _rowBudget = new();
    private IDiskScanEngine? _engine;
    private CleanupAnalyzer? _cleanupAnalyzer;
    private DiskNode? _zoomNode; // null = 扫描根。只决定区块图以哪棵子树为基准，不改目录树的根。
    private DateTime _lastSnapshotAt = DateTime.UtcNow;
    private long _lastSig;
    private bool _force = true; // 强制刷新一次（导航、扫描状态变化后置位）
    private bool _wasScanning;

    // 行对象按 DiskNode 全局复用：区块图给出一个 DiskNode 时，要能在树里找回同一个行实例，
    // 否则 TreeDataGrid 的选中/展开状态对不上。
    private readonly Dictionary<DiskNode, TreeNodeRow> _rowByNode = new();
    private DiskNode? _rootNode;
    private TreeNodeRow? _rootRow;

    public ObservableCollection<DriveInfoView> Drives { get; } = [];

    /// <summary>
    /// 目录树顶层恒为一个元素 = 扫描根本身，对应上游 CTreeListControl::SetRootItem
    /// （VIEWSTATE.indent 的注释即"根为 0，其子为 1"）。缩放只换区块图基准，绝不换树根。
    /// </summary>
    public ObservableCollection<TreeNodeRow> TreeRoot { get; } = [];
    public ObservableCollection<CleanupItemRow> CleanupItems { get; } = [];

    [ObservableProperty] private DriveInfoView? _selectedDrive;
    [ObservableProperty] private string _breadcrumb = "选择驱动器开始扫描";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _idleHint = "就绪 · 单击区块或树行即可在此查看完整路径";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _cleanupSummary = "扫描时自动识别可清理文件";
    [ObservableProperty] private bool _isCleanupBusy;
    [ObservableProperty] private IReadOnlyList<TreemapTile> _treemapTiles = [];
    [ObservableProperty] private IReadOnlyList<ExtensionSegment> _extensionSegments = [];

    // 全窗口只有一份选中项：区块图与目录树读写同一个 SelectedNode，任一侧改动都同步过去。
    // 树侧的"哪一行被选中"由视图自己持有（TreeDataGrid 的 selection model），不进 VM。
    [ObservableProperty] private DiskNode? _selectedNode;

    /// <summary>
    /// 区块图上指针停着的瓦片。对应上游 CGraphView::m_hoverItem：只覆写状态栏文字，
    /// 绝不改选中态、也不进目录树（UpdatePaneText 里 hover 分支优先于 selection 分支）。
    /// </summary>
    [ObservableProperty] private DiskNode? _hoveredNode;

    partial void OnIsScanningChanged(bool value) => IdleHint = value
        ? "正在扫描，路径随目录逐层落地"
        : "就绪 · 单击区块或树行即可在此查看完整路径";

    partial void OnSelectedNodeChanged(DiskNode? value) => RaiseSelectionText();
    partial void OnHoveredNodeChanged(DiskNode? value) => RaiseSelectionText();

    /// <summary>状态栏与浮层当前"报"的那一项：悬停优先，其次选中（同上游 UpdatePaneText）。</summary>
    private DiskNode? ReportedNode => HoveredNode ?? SelectedNode;

    public bool HasReportedNode => ReportedNode is not null;

    /// <summary>完整路径。根节点 Name 即绝对路径，故 &lt;Free Space&gt; 这类伪节点也拿得到 C:\<Free Space>。</summary>
    public string SelectionPathText => ReportedNode?.GetPath() ?? string.Empty;

    public string SelectionNameText => ReportedNode?.Name ?? string.Empty;

    /// <summary>状态栏大小格：整串由这里给，无选中/悬停时留空，省得 XAML 的 StringFormat 印出半个前缀。</summary>
    public string SelectionSizeText => ReportedNode is { } n ? $"物理大小: Σ {SizeFormat.Format(n.PhysicalSize)}" : string.Empty;

    /// <summary>浮层标题行：名称 · 大小 ·（目录再带子树统计），与状态栏同一份数据源，不会互相矛盾。</summary>
    public string CaptionTitleText => ReportedNode switch
    {
        null => string.Empty,
        var n when n.Kind == NodeKind.File => $"{n.Name} · {SizeFormat.Format(n.PhysicalSize)}",
        var n => $"{n.Name} · {SizeFormat.Format(n.PhysicalSize)} · {n.FileCount:N0} 文件 / {n.DirCount:N0} 目录",
    };

    /// <summary>
    /// 浮层路径行：状态栏那一格放不下时才用这个中段省略版本。
    /// 头 16 / 尾 52 个字符：驱动器号与真正的目标名都在，被省略的是中间的目录链。
    /// </summary>
    public string CaptionPathText => ElideMiddle(SelectionPathText);

    private static string ElideMiddle(string path, int head = 16, int tail = 52)
    {
        var sep = System.IO.Path.DirectorySeparatorChar; // 本文件同时 using 了 Avalonia.Controls，那里也有个 Path
        if (path.Length <= head + tail + 1) return path;
        var cut = path.IndexOf(sep, head);
        // 前段切在分隔符上，别留半截目录名
        var prefix = cut > 0 ? path[..(cut + 1)] : path[..head];
        // 后段按"整段"倒着取：宁可超一点预算，也不把目标名切成 …E_DIRECTORY\x01.dat 这种残样
        var start = Math.Max(prefix.Length, path.Length - tail);
        var boundary = path.IndexOf(sep, start);
        if (boundary < 0) boundary = path.LastIndexOf(sep);
        return boundary < 0 ? path : $"{prefix}…\\{path[(boundary + 1)..]}";
    }

    private DiskNode? _raisedNode;
    private string _raisedSize = string.Empty;

    /// <summary>
    /// 按值比对后再广播：扫描期每 250ms 也要走一次这里，路径与大小都没变时不许惊动绑定，
    /// 否则状态栏与浮层每 tick 重排一次，悬停效果会被刷掉。
    /// </summary>
    private void RaiseSelectionText()
    {
        var node = ReportedNode;
        var size = node is { } n ? SizeFormat.Format(n.PhysicalSize) : string.Empty;
        var changed = !ReferenceEquals(node, _raisedNode) || size != _raisedSize;
        if (!changed) return;
        _raisedNode = node;
        _raisedSize = size;

        foreach (var name in new[]
                 {
                     nameof(HasReportedNode), nameof(SelectionPathText), nameof(SelectionNameText),
                     nameof(SelectionSizeText), nameof(CaptionTitleText), nameof(CaptionPathText),
                 })
            OnPropertyChanged(name);
    }

    public MainViewModel()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            Drives.Add(new DriveInfoView(drive));
        SelectedDrive = Drives.FirstOrDefault(d => d.Info.Name.StartsWith("C:", StringComparison.Ordinal))
                        ?? Drives.FirstOrDefault();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();

        // 区块图的两个钳制参数改动后必须强制重画一次：空闲期快照签名不变，否则改了看不出效果。
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TreemapMaxDepth)
                                or nameof(AppSettings.TreemapChildrenCap))
            {
                _force = true;
                RefreshSnapshot();
            }
        };
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedDrive is null || IsScanning) return;
        var path = SelectedDrive.Info.RootDirectory.FullName;

        // 引擎选择：native core（wdscore.dll，NTFS MFT 秒扫）就绪后自动接管；当前回退托管引擎。
        // 扫描排除项每次开扫时从设置快照一次，扫描中途改设置不影响正在跑的这趟。
        _engine?.Dispose();
        _cleanupAnalyzer?.Dispose();
        _cleanupAnalyzer = new CleanupAnalyzer(DefaultCleanupRules.Create());
        CleanupItems.Clear();
        CleanupSummary = "正在等待扫描结果…";
        var options = Settings.ToScanOptions();
        _engine = NativeWdsEngine.IsAvailable(out _)
            ? new NativeWdsEngine()
            : new ManagedWalkEngine(options);
        _engine.FileDiscovered += _cleanupAnalyzer.Accept;
        _engine.StateChanged += OnEngineStateChanged;
        _zoomNode = null;
        _rootNode = null;
        _rootRow = null;
        _rowByNode.Clear();
        TreeRoot.Clear();
        SelectedNode = null;
        HoveredNode = null;
        _force = true;

        try
        {
            IsScanning = true;
            StatusText = $"正在扫描 {path} …";
            await _engine.StartScanAsync(path);
        }
        catch (Exception ex)
        {
            IsScanning = false;
            StatusText = $"启动扫描失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopScan() => _engine?.StopScan();

    /// <summary>
    /// zoom in 到"树里当前选中的项"，对应上游 CWinDirStatModel::OnTreeMapZoomIn：
    /// 文件取其父目录，目录取自身，根项等价于不缩放。绝不反向改动选中项。
    /// </summary>
    [RelayCommand]
    private void ZoomInSelected()
    {
        var node = SelectedNode;
        if (node is null || _rootNode is null) return;
        if (ReferenceEquals(node, _rootNode)) return;
        ZoomTo(node.Kind == NodeKind.File ? node.Parent : node);
    }

    /// <summary>zoom out 一级。已在扫描根时不动作，同上游 ID_TREEMAP_ZOOMOUT 要求 IsZoomed。</summary>
    [RelayCommand]
    private void ZoomOut()
    {
        if (_zoomNode is null) return;
        var parent = _zoomNode.Parent;
        _zoomNode = parent is null || ReferenceEquals(parent, _rootNode) ? null : parent;
        _force = true;
        RefreshSnapshot();
    }

    /// <summary>
    /// 切换区块图的基准子树（treemap 双击、工具栏缩放都走这里）。
    /// 只改 zoom 根，绝不改动选中项 —— 选中与缩放是两套状态，同上游。
    /// 目标已是当前基准时不动作，同 CTreeMapView::DrillDown 的前置判断。
    /// </summary>
    public void ZoomTo(DiskNode? node)
    {
        if (node is null || node.Kind != NodeKind.Directory) return;
        if (ReferenceEquals(node, _zoomNode ?? _rootNode)) return;
        _zoomNode = node;
        _force = true;
        RefreshSnapshot();
    }

    /// <summary>
    /// 目录树选中项变化时，让区块图至少能定位到该项所在的布局范围。
    /// 已经有独立瓦片的节点只更新选中高亮；被区块图的深度/数量裁剪，或不在当前
    /// 缩放分支中的目录，则把该目录作为新的区块图基准。这样不会破坏用户对当前
    /// 可见布局的浏览，但深层树项也不会只在右侧留下一个无关的外层高亮。
    /// </summary>
    public void LocateTreeSelection(DiskNode? node)
    {
        if (node is null || _rootNode is null) return;
        if (TreemapTiles.Any(tile => ReferenceEquals(tile.Node, node))) return;

        var target = node.Kind == NodeKind.Directory ? node : node.Parent;
        if (target is null || ReferenceEquals(target, _zoomNode ?? _rootNode))
        {
            _force = true;
            RefreshSnapshot();
            return;
        }

        ZoomTo(target);
    }

    private void OnEngineStateChanged(object? sender, ScanState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            // StopScan can post a final state for the previous engine while a
            // new scan is already being prepared. Never close the new analyzer
            // because of that stale notification.
            if (!ReferenceEquals(sender, _engine)) return;
            if (state is ScanState.Completed or ScanState.Stopped or ScanState.Failed)
                _cleanupAnalyzer?.Complete();
            IsScanning = state == ScanState.Running;
            StatusText = state switch
            {
                ScanState.Completed => "扫描完成",
                ScanState.Stopped => "扫描已停止",
                ScanState.Failed => "扫描失败",
                _ => StatusText,
            };
            _force = true;
            RefreshSnapshot();
        });

    private long LastBytes;
    private double SpeedBytesPerSec;

    private void RefreshSnapshot()
    {
        DrainCleanupCandidates();
        var root = _engine?.Root;
        if (root is null) return;

        var scope = _zoomNode ?? root;

        // 空闲且数据自上次快照无变化时直接跳过：杜绝每 250ms 无谓重绘导致悬停高亮动画被重置。
        var sig = root.PhysicalSize * 31 + root.FileCount * 17 + root.DirCount
                  + ((long)scope.ChildCount << 40) ^ RuntimeHelpers.GetHashCode(scope);
        if (!_force && !IsScanning && sig == _lastSig) return;
        _force = false;
        _lastSig = sig;

        // 下行速率估算（快照差分）
        var now = DateTime.UtcNow;
        var dt = (now - _lastSnapshotAt).TotalSeconds;
        if (dt > 0.2)
        {
            var delta = root.PhysicalSize - LastBytes;
            SpeedBytesPerSec = delta / dt;
            LastBytes = root.PhysicalSize;
            _lastSnapshotAt = now;
        }

        // ---- 目录树：根恒为扫描根，缩放不动树根 ----
        RefreshTree(root);
        if (_wasScanning && !IsScanning && _rootRow is not null) SortMaterialized(_rootRow);
        _wasScanning = IsScanning;

        // ---- treemap ----
        var tiles = new List<TreemapTile>(256);
        BuildTiles(scope, 0, 0, 1, 1, 0, tiles);
        TreemapTiles = tiles;

        // ---- 扩展名色带 ----
        if (_engine is not null)
        {
            var stats = _engine.ExtensionStats
                .OrderByDescending(kvp => kvp.Value.Bytes)
                .Take(14)
                .Select(kvp => new ExtensionSegment
                {
                    Bytes = kvp.Value.Bytes,
                    Extension = kvp.Key,
                    Color = ExtensionPalette.GetColor(kvp.Key),
                })
                .ToList();
            ExtensionSegments = stats;
        }

        Breadcrumb = BuildBreadcrumb(scope);
        var free = root.SnapshotChildren().FirstOrDefault(c => c.Kind == NodeKind.FreeSpace);
        StatusText = $"{(IsScanning ? "扫描中" : "空闲")} · {_engine!.State} · " +
                     $"文件 {root.FileCount:N0} · 目录 {root.DirCount:N0} · " +
                     $"已用 {SizeFormat.Format(root.PhysicalSize - (free?.PhysicalSize ?? 0))} · " +
                     $"{SizeFormat.Format((long)Math.Max(0, SpeedBytesPerSec))}/s";
        // 路径不变，但选中/悬停项的尺寸在扫描期每 tick 都在长：大小格与浮层得跟着重取。
        RaiseSelectionText();
    }

    private void DrainCleanupCandidates()
    {
        if (_cleanupAnalyzer is null) return;

        foreach (var candidate in _cleanupAnalyzer.Drain())
            CleanupItems.Add(new CleanupItemRow(candidate));

        var selected = CleanupItems.Where(static item => item.IsSelected).ToArray();
        var size = selected.Sum(static item => item.Candidate.Size);
        CleanupSummary = CleanupItems.Count == 0
            ? (IsScanning ? "扫描中：暂未发现可清理文件" : "没有发现符合默认规则的文件")
            : $"发现 {CleanupItems.Count:N0} 个文件 · 已勾选 {selected.Length:N0} 个 · {SizeFormat.Format(size)}";
    }

    [RelayCommand]
    private void SelectAllCleanup()
    {
        foreach (var item in CleanupItems) item.IsSelected = true;
        DrainCleanupCandidates();
    }

    [RelayCommand]
    private void ClearCleanupSelection()
    {
        foreach (var item in CleanupItems) item.IsSelected = false;
        DrainCleanupCandidates();
    }

    [RelayCommand]
    private async Task MoveSelectedToRecycleBinAsync()
    {
        if (IsCleanupBusy) return;

        var selected = CleanupItems.Where(static item => item.IsSelected).ToArray();
        if (selected.Length == 0) return;

        IsCleanupBusy = true;
        try
        {
            var results = await Task.Run(() => selected.Select(item =>
            {
                try
                {
                    var info = new FileInfo(item.Candidate.FullPath);
                    if (!info.Exists || info.Length != item.Candidate.Size ||
                        info.LastWriteTimeUtc != item.Candidate.LastWriteTimeUtc)
                        return new RecycleResult(item.Candidate.FullPath, false, "文件在预览后发生变化");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new RecycleResult(item.Candidate.FullPath, false, ex.Message);
                }

                return RecycleBin.Move(item.Candidate.FullPath);
            }).ToArray());

            var succeeded = results.Where(static result => result.Succeeded)
                .Select(static result => result.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = CleanupItems.Count - 1; i >= 0; i--)
                if (succeeded.Contains(CleanupItems[i].Candidate.FullPath))
                    CleanupItems.RemoveAt(i);

            var failed = results.Length - succeeded.Count;
            StatusText = failed == 0
                ? $"已将 {succeeded.Count:N0} 个文件移入回收站，请重新扫描刷新目录树"
                : $"已移入回收站 {succeeded.Count:N0} 个，{failed:N0} 个未处理";
            DrainCleanupCandidates();
        }
        finally
        {
            IsCleanupBusy = false;
        }
    }

    /// <summary>
    /// 建立/维护目录树行：根行 = 扫描根且默认展开（同上游 SetRootItem 紧跟 ExpandItem(0)），
    /// 之后只在"祖先全部展开"的可见范围内自顶向下刷新，超出时间预算就留到下一个 tick。
    /// </summary>
    private void RefreshTree(DiskNode root)
    {
        _rootNode = root;
        if (!ReferenceEquals(_rootRow?.Node, root))
        {
            _rowByNode.Clear();
            TreeRoot.Clear();
            _rootRow = CreateRow(root, null);
            _rootRow.IsExpanded = true;
            TreeRoot.Add(_rootRow);
        }

        _rowBudget.Restart();
        UpdateVisibleRows(_rootRow, root);
        _rowBudget.Stop();
    }

    private TreeNodeRow CreateRow(DiskNode node, TreeNodeRow? parent)
    {
        if (_rowByNode.TryGetValue(node, out var cached)) return cached;
        var row = new TreeNodeRow(node, parent, CreateRow);
        _rowByNode[node] = row;
        row.Update(_rootNode ?? node); // 新建行立刻取一次现值：空闲期展开分支时不该先看到空行
        return row;
    }

    private void UpdateVisibleRows(TreeNodeRow row, DiskNode root)
    {
        row.Update(root);
        if (!row.IsExpanded || _rowBudget.ElapsedMilliseconds >= RowUpdateBudgetMs) return;
        foreach (var child in row.Children) UpdateVisibleRows(child, root);
    }

    /// <summary>扫描收尾时给所有已物化的层级排一次序（上游是周期性 SortItems）。</summary>
    private static void SortMaterialized(TreeNodeRow row)
    {
        if (!row.IsMaterialized) return;
        foreach (var child in row.Children) SortMaterialized(child);
        row.SortChildren();
    }

    /// <summary>
    /// 对应上游 CTreeListControl::ExpandPathToItem：逐级展开目标所在路径、折叠路径之外的兄弟分支，
    /// 返回目录树里对应的行。子项尚未被扫描到时，停在能到达的最深处。
    /// </summary>
    public TreeNodeRow? Reveal(DiskNode node)
    {
        if (_rootRow is null) return null;

        var path = new List<DiskNode>();
        for (var p = node; p is not null && !ReferenceEquals(p, _rootRow.Node); p = p.Parent)
            path.Add(p);
        if (path.Count == 0) return _rootRow;
        path.Reverse(); // 自根向下

        var row = _rootRow;
        row.IsExpanded = true;
        for (var i = 0; i < path.Count; i++)
        {
            row.CollapseSiblingsExcept(path[i]);
            row.AppendNewChildren();
            var child = row.FindChildRow(path[i]);
            if (child is null) return row;
            if (i < path.Count - 1) child.IsExpanded = true; // 只展开祖先，选中项自身保持原状
            row = child;
        }
        return row;
    }

    /// <summary>行 → 层级索引路径。TreeDataGrid 的程序化选中按 IndexPath 定位，且它是懒加载的，索引本身不物化任何行。</summary>
    public IndexPath ModelPathOf(TreeNodeRow row)
    {
        var indexes = new List<int>();
        var current = row;
        while (current.Parent is { } parent)
        {
            indexes.Add(parent.Children.IndexOf(current));
            current = parent;
        }
        indexes.Add(TreeRoot.IndexOf(current)); // 顶层位置（当前恒为 0）
        indexes.Reverse();
        return new IndexPath(indexes);
    }

    private static string BuildBreadcrumb(DiskNode node)
    {
        var parts = new List<string>();
        for (DiskNode? p = node; p is not null; p = p.Parent) parts.Add(p.Name);
        parts.Reverse();
        return string.Join(" › ", parts);
    }

    /// <summary>
    /// 深度与每层子块上限是脚手架钳制（阶段三目标是去掉它们、改成纯几何剪枝），
    /// 现已提到设置面板，用户可在"卡"与"糙"之间自己取舍。
    /// </summary>
    private void BuildTiles(DiskNode node, double x, double y, double w, double h, int depth,
        List<TreemapTile> output)
    {
        var maxDepth = Settings.TreemapMaxDepth;
        if (depth >= maxDepth || output.Count > 4000 || w <= 0 || h <= 0) return;

        var kids = node.SnapshotChildren()
            .Where(k => k.PhysicalSize > 0)
            .Take(Settings.TreemapChildrenCap)
            .ToArray();
        if (kids.Length == 0) return;

        var values = kids.Select(k => (double)k.PhysicalSize).ToArray();
        foreach (var tile in TreeMapLayout.Squarify(values, x, y, w, h))
        {
            var kid = kids[tile.Index];
            uint rgb = kid.Kind switch
            {
                NodeKind.File => ExtensionPalette.GetColor(kid.Extension),
                NodeKind.FreeSpace => 0x3F3F46,
                _ => ExtensionPalette.GetDirectoryTint(kid, depth + 1),
            };
            output.Add(new TreemapTile
            {
                Node = kid,
                X = tile.X, Y = tile.Y, W = tile.Width, H = tile.Height,
                ColorRgb = rgb,
                Depth = depth,
            });

            if (kid.Kind == NodeKind.Directory && depth + 1 < maxDepth &&
                tile.Width * tile.Height > TreemapSubdivideArea &&
                tile.Width > 0.06 && tile.Height > 0.06)
            {
                BuildTiles(kid, tile.X, tile.Y, tile.Width, tile.Height, depth + 1, output);
            }
        }
    }
}

public sealed class DriveInfoView(DriveInfo info)
{
    public DriveInfo Info { get; } = info;
    public override string ToString()
    {
        var label = string.IsNullOrEmpty(Info.VolumeLabel) ? "本地磁盘" : Info.VolumeLabel;
        return $"{label} ({Info.Name.TrimEnd('\\')})";
    }
}
