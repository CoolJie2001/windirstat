using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WdsShell.App.ViewModels;
using WdsShell.Core.Models;

namespace WdsShell.App.Controls;

/// <summary>
/// Squarified treemap 自绘控件。VM 只推归一化瓦片，控件负责缩放/绘制/命中，
/// 渲染与布局解耦 —— 与 C++ core 的 TreeMap/TreeMapView 分工同构。
/// </summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapTile>?> TilesProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<TreemapTile>?>(nameof(Tiles));

    static TreemapControl()
    {
        TilesProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateVisual());
    }

    public IReadOnlyList<TreemapTile>? Tiles
    {
        get => GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    /// <summary>点击目录瓦片时触发（MainWindow 转给 VM 导航）。</summary>
    public event Action<DiskNode>? Navigated;

    private static readonly Pen GridPen = new(new SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 0, 0, 0)), 1);

    public override void Render(DrawingContext context)
    {
        var tiles = Tiles;
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x20, 0x20, 0x24)), new Rect(0, 0, w, h));
        if (tiles is null || tiles.Count == 0 || w <= 0 || h <= 0) return;

        var typeface = new Typeface("Inter");

        foreach (var tile in tiles)
        {
            var rc = new Rect(tile.X * w, tile.Y * h, tile.W * w, tile.H * h);
            if (rc.Width < 1 || rc.Height < 1) continue;
            context.DrawRectangle(tile.Brush, GridPen, rc);

            if (rc.Width < 46 || rc.Height < 16 || tile.Depth >= 2) continue;
            var text = new FormattedText(
                tile.Node.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                13 - tile.Depth * 2,
                Brushes.Black)
            {
                MaxTextWidth = Math.Max(0, rc.Width - 6),
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(text, rc.TopLeft + new Point(3, 1));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var tiles = Tiles;
        if (tiles is null || tiles.Count == 0) return;

        var p = e.GetPosition(this);
        TreemapTile? hit = null;
        foreach (var tile in tiles)
        {
            var rc = new Rect(tile.X * Bounds.Width, tile.Y * Bounds.Height,
                tile.W * Bounds.Width, tile.H * Bounds.Height);
            if (!rc.Contains(p)) continue;
            // 取命中的最小（最深）瓦片
            if (hit is null || tile.W * tile.H < hit.W * hit.H)
                hit = tile;
        }
        if (hit is not null)
            Navigated?.Invoke(hit.Node);
    }
}
