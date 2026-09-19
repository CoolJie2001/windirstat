using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WdsShell.Core.Layout;
using WdsShell.Core.Models;

namespace WdsShell.App.ViewModels;

/// <summary>
/// 目录树的一行。同一个 DiskNode 全程复用同一个行实例，扫描增量只走属性通知，
/// 绝不重建集合 —— 这是展开态、选中态和悬停高亮在扫描期不丢的前提。
/// 子行首次被取用时才物化，对应上游"父项展开时 OnChildrenAdded 才插行、折叠时整块删除"。
/// </summary>
public sealed partial class TreeNodeRow : ObservableObject
{
    private readonly Func<DiskNode, TreeNodeRow?, TreeNodeRow> _rowFor;
    private readonly HashSet<DiskNode> _seen = [];
    private ObservableCollection<TreeNodeRow>? _children;
    private bool _hasChildren;

    public TreeNodeRow(DiskNode node, TreeNodeRow? parent,
        Func<DiskNode, TreeNodeRow?, TreeNodeRow> rowFor)
    {
        Node = node;
        Parent = parent;
        Indent = (parent?.Indent ?? -1) + 1; // 上游 VIEWSTATE.indent：根为 0，其子为 1……
        _rowFor = rowFor;
        Name = node.Name;
        // 上游占比条取深度循环色（FileTreeColors[indent % 8]），不是扩展名色
        BarColor = TreeBarPalette.ColorFor(Indent);
    }

    public DiskNode Node { get; }
    public int Indent { get; }
    public TreeNodeRow? Parent { get; }
    public string Name { get; }
    public uint BarColor { get; }

    /// <summary>
    /// 能否展开的判据与上游 HasChildren()（GetTreeListChildCount() &gt; 0）一致：只看子项数。
    /// 扫描期尚未走到的目录暂时没有箭头，等其子项落地、行重新 realize 时出现。
    /// </summary>
    public bool HasChildren => _hasChildren;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>被 TreeDataGrid 取到才物化子行；未展开过时返回空集合。</summary>
    public ObservableCollection<TreeNodeRow> Children
    {
        get
        {
            _children ??= [];
            AppendNewChildrenCore();
            return _children;
        }
    }

    /// <summary>子行是否已被取用过（未取用过则整棵子树都不占内存）。</summary>
    public bool IsMaterialized => _children is not null;

    /// <summary>
    /// 扫描期只把新出现的子项追加到末尾，不打乱用户正在看的行位置。
    /// 与 <see cref="Children"/> 同语义：即使这一行还没被表格取用过，也当场物化 ——
    /// 从区块图反查深层行时必须能逐层向下找到子行。
    /// </summary>
    public void AppendNewChildren()
    {
        _children ??= [];
        AppendNewChildrenCore();
    }

    private void AppendNewChildrenCore()
    {
        foreach (var c in Node.SnapshotChildren())
            if (_seen.Add(c)) _children!.Add(_rowFor(c, this));
    }

    public TreeNodeRow? FindChildRow(DiskNode child) =>
        _children?.FirstOrDefault(r => ReferenceEquals(r.Node, child));

    /// <summary>
    /// 折叠路径之外的兄弟分支，对应上游 ExpandPathToItem 中对 parent 与目标之间各行调 CollapseItem。
    /// </summary>
    public void CollapseSiblingsExcept(DiskNode keep)
    {
        if (_children is null) return;
        foreach (var r in _children)
            if (!ReferenceEquals(r.Node, keep) && r.IsExpanded) r.IsExpanded = false;
    }

    /// <summary>扫描稳定后一次性按物理尺寸降序排列（上游是 ~375ms 周期的 SortItems）。</summary>
    public void SortChildren()
    {
        if (_children is null || _children.Count < 2) return;
        var target = _children.OrderByDescending(static r => r.Node.PhysicalSize).ToList();
        RowReconcile.Reconcile(_children, target);
    }

    [ObservableProperty] private string _percentText = string.Empty;
    [ObservableProperty] private string _physicalText = string.Empty;
    [ObservableProperty] private string _logicalText = string.Empty;
    [ObservableProperty] private string _filesText = string.Empty;
    [ObservableProperty] private string _foldersText = string.Empty;
    [ObservableProperty] private double _subtreeFraction;
    [ObservableProperty] private double _absoluteFraction;

    /// <summary>
    /// 两个分母各管一头，与上游一致：子树条 = 占直接父级（GetFraction），
    /// 绝对条与 % 列 = 占扫描根（GetAbsoluteFraction，UseAbsolutePercentages 默认开）。
    /// </summary>
    public void Update(DiskNode root)
    {
        var parentSize = Math.Max(1L, Node.Parent?.PhysicalSize ?? Node.PhysicalSize);
        SubtreeFraction = (double)Node.PhysicalSize / parentSize;
        AbsoluteFraction = (double)Node.PhysicalSize / Math.Max(1L, root.PhysicalSize);
        PercentText = $"{AbsoluteFraction * 100:0.##}%";
        PhysicalText = SizeFormat.Format(Node.PhysicalSize);
        LogicalText = SizeFormat.Format(Node.LogicalSize);
        if (Node.Kind == NodeKind.File)
        {
            FilesText = string.Empty;
            FoldersText = string.Empty;
        }
        else
        {
            FilesText = Node.FileCount.ToString("N0");
            FoldersText = Node.DirCount.ToString("N0");
        }

        var hasChildren = Node.Kind == NodeKind.Directory && Node.ChildCount > 0;
        if (hasChildren == _hasChildren) return;
        _hasChildren = hasChildren;
        OnPropertyChanged(nameof(HasChildren));
    }
}

/// <summary>
/// 集合原地对齐：只用 Add/Remove 做最小改动，保留行实例。
/// 整表 Reset 会让 TreeDataGrid 重建容器，展开/选中/悬停状态随之丢失。
/// 不能用 Move 表达位移：TreeDataGrid 11.1 对 Move 的处理是已知坑，
/// 容器不移位导致新旧两行叠在同一格（文字重影）；拆成 Remove+Insert 才会被当成普通增删重排。
/// </summary>
internal static class RowReconcile
{
    public static void Reconcile<T>(ObservableCollection<T> current, IReadOnlyList<T> target)
        where T : class
    {
        var keep = new HashSet<T>(target);
        for (var i = current.Count - 1; i >= 0; i--)
            if (!keep.Contains(current[i])) current.RemoveAt(i);

        for (var i = 0; i < target.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], target[i])) continue;
            var moved = -1;
            for (var j = i + 1; j < current.Count; j++)
                if (ReferenceEquals(current[j], target[i])) { moved = j; break; }
            if (moved >= 0) current.RemoveAt(moved);
            current.Insert(i, target[i]);
        }

        while (current.Count > target.Count) current.RemoveAt(current.Count - 1);
    }
}

/// <summary>treemap 瓦片：归一化坐标（0..1），由 TreemapControl 缩放到像素。</summary>
public sealed class TreemapTile
{
    public required DiskNode Node { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public required uint ColorRgb { get; init; }
    public required int Depth { get; init; }

    public IBrush Brush => _brush ??= new SolidColorBrush(Avalonia.Media.Color.FromRgb(
        (byte)((ColorRgb >> 16) & 0xFF), (byte)((ColorRgb >> 8) & 0xFF), (byte)(ColorRgb & 0xFF)));

    private IBrush? _brush;
}

/// <summary>扩展名占比色带的一段。</summary>
public sealed class ExtensionSegment
{
    public required long Bytes { get; init; }
    public required string Extension { get; init; }
    public required uint Color { get; init; }
}
