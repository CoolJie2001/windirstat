using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WdsShell.Core.Models;

namespace WdsShell.App.ViewModels;

/// <summary>
/// 文件树列表的一行（当前目录的直接子项快照）。
/// 显示字段采用 <see cref="ObservableObject"/> 原地通知更新，而非每次快照重建行对象——
/// 后者会让 ListBox 反复销毁/创建容器，从而在鼠标悬停时不断重置高亮/选中动画（表现为“上下跳动”）。
/// </summary>
public sealed partial class NodeRow : ObservableObject
{
    // 身份与类型固定：同一 DiskNode 复用同一行容器
    public required DiskNode Node { get; init; }
    public required bool IsDirectory { get; init; }

    // 颜色缓存：仅当扩展名配色变化时才替换 Brush 实例，避免无谓的属性通知与重绘。
    public uint LastRgb { get; set; } = uint.MaxValue;

    // 随扫描增量变化的显示字段
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _percentText = string.Empty;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private IBrush _brush = Brushes.Transparent;
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
