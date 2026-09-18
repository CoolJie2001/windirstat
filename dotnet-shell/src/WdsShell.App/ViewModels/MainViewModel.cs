using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private const int MaxRows = 400;          // 列表快照行数上限（虚拟列表本身不限）
    private const int TreemapChildrenCap = 24; // 每层参与布局的子节点数上限
    private const int TreemapMaxDepth = 3;
    private const double TreemapSubdivideArea = 0.012; // 面积占比超过该值才下钻一层

    private readonly DispatcherTimer _timer;
    private IDiskScanEngine? _engine;
    private DiskNode? _currentNode; // null = 扫描根
    private DateTime _lastSnapshotAt = DateTime.UtcNow;

    // 列表行按 DiskNode 复用，配合原地属性通知做增量 reconcile，
    // 绝不再 Rows.Clear()（整表 Reset 是悬停/选中框“上下跳动”的根因）。
    private readonly Dictionary<DiskNode, NodeRow> _rowByNode = new();
    private long _lastSig;
    private bool _force = true; // 强制刷新一次（导航、扫描状态变化后置位）

    public ObservableCollection<DriveInfoView> Drives { get; } = [];
    public ObservableCollection<NodeRow> Rows { get; } = [];

    [ObservableProperty] private DriveInfoView? _selectedDrive;
    [ObservableProperty] private string _breadcrumb = "选择驱动器开始扫描";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private IReadOnlyList<TreemapTile> _treemapTiles = [];
    [ObservableProperty] private IReadOnlyList<ExtensionSegment> _extensionSegments = [];

    public MainViewModel()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            Drives.Add(new DriveInfoView(drive));
        SelectedDrive = Drives.FirstOrDefault(d => d.Info.Name.StartsWith("C:", StringComparison.Ordinal))
                        ?? Drives.FirstOrDefault();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedDrive is null || IsScanning) return;
        var path = SelectedDrive.Info.RootDirectory.FullName;

        // 引擎选择：native core（wdscore.dll，NTFS MFT 秒扫）就绪后自动接管；当前回退托管引擎
        _engine?.Dispose();
        _engine = NativeWdsEngine.IsAvailable(out _)
            ? new NativeWdsEngine()
            : new ManagedWalkEngine();
        _engine.StateChanged += OnEngineStateChanged;
        _currentNode = null;
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

    [RelayCommand]
    private void NavigateUp()
    {
        if (_currentNode is null) return; // 已在扫描根
        var parent = _currentNode.Parent;
        _currentNode = parent is null || ReferenceEquals(parent, _engine?.Root) ? null : parent;
        _force = true;
        RefreshSnapshot();
    }

    /// <summary>双击列表项 / 点击 treemap 目录块进入。</summary>
    public void NavigateInto(DiskNode? node)
    {
        if (node is null || node.Kind != NodeKind.Directory) return;
        _currentNode = node;
        _force = true;
        RefreshSnapshot();
    }

    private void OnEngineStateChanged(object? sender, ScanState state) =>
        Dispatcher.UIThread.Post(() =>
        {
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
        var root = _engine?.Root;
        if (root is null) return;

        var scope = _currentNode ?? root;

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

        // ---- 列表行（按 DiskNode 复用行对象、原地更新字段，绝不整表 Clear） ----
        var children = scope.SnapshotChildren();
        var scopeSize = Math.Max(1, scope.PhysicalSize);
        var target = new List<NodeRow>(Math.Min(children.Length, MaxRows));
        foreach (var child in children.Take(MaxRows))
        {
            if (!_rowByNode.TryGetValue(child, out var row))
            {
                row = new NodeRow { Node = child, IsDirectory = child.IsDirectory };
                _rowByNode[child] = row;
            }

            uint rgb = child.Kind switch
            {
                NodeKind.File => ExtensionPalette.GetColor(child.Extension),
                NodeKind.FreeSpace => 0x3F3F46,
                _ => ExtensionPalette.GetDirectoryTint(child, 0),
            };
            row.Name = child.Name;
            row.SizeText = SizeFormat.Format(child.PhysicalSize);
            row.PercentText = $"{child.PhysicalSize * 100.0 / scopeSize:0.##}%";
            row.MetaText = child.IsDirectory ? $"{child.FileCount:N0} 文件" : string.Empty;
            if (rgb != row.LastRgb)
            {
                row.LastRgb = rgb;
                row.Brush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                    (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
            }
            target.Add(row);
        }
        ReconcileRows(target);

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
    }

    /// <summary>
    /// 将 <see cref="Rows"/> 就地调整到与 <paramref name="target"/> 相同的成员与顺序，
    /// 仅用 Add/Remove/Move 做最小改动——保留行容器实例，避免整表 Reset 引发悬停/选中框闪烁。
    /// 当扫描结束后集合稳定时，本方法不产生任何变更事件。
    /// </summary>
    private void ReconcileRows(IReadOnlyList<NodeRow> target)
    {
        var keep = new HashSet<NodeRow>(target);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            var stale = Rows[i];
            if (keep.Contains(stale)) continue;
            _rowByNode.Remove(stale.Node);
            Rows.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i])) continue;
            var moved = -1;
            for (var j = i + 1; j < Rows.Count; j++)
                if (ReferenceEquals(Rows[j], target[i])) { moved = j; break; }
            if (moved >= 0) Rows.Move(moved, i);
            else Rows.Insert(i, target[i]);
        }

        while (Rows.Count > target.Count)
        {
            var extra = Rows[^1];
            Rows.RemoveAt(Rows.Count - 1);
            _rowByNode.Remove(extra.Node);
        }
    }

    private static string BuildBreadcrumb(DiskNode node)
    {
        var parts = new List<string>();
        for (DiskNode? p = node; p is not null; p = p.Parent) parts.Add(p.Name);
        parts.Reverse();
        return string.Join(" › ", parts);
    }

    private static void BuildTiles(DiskNode node, double x, double y, double w, double h, int depth,
        List<TreemapTile> output)
    {
        if (depth >= TreemapMaxDepth || output.Count > 4000 || w <= 0 || h <= 0) return;

        var kids = node.SnapshotChildren()
            .Where(k => k.PhysicalSize > 0)
            .Take(TreemapChildrenCap)
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

            if (kid.Kind == NodeKind.Directory && depth + 1 < TreemapMaxDepth &&
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
