using Avalonia.Media;
using WdsShell.Core.Models;

namespace WdsShell.App.ViewModels;

/// <summary>文件树列表的一行（当前目录的直接子项快照）。</summary>
public sealed class NodeRow
{
    public required DiskNode Node { get; init; }
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string PercentText { get; init; }
    public required string MetaText { get; init; }
    public required IBrush Brush { get; init; }
    public required bool IsDirectory { get; init; }
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
