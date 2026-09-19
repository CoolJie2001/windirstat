using System.Globalization;
using System.Linq;
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

    /// <summary>
    /// 全窗口唯一选中项，由 VM 双向绑定提供。选中只体现为高亮，绝不改变基准子树，
    /// 对应上游单击 extent 仅广播 MODEL_CHANGE_SELECTION_ACTION 的行为。
    /// </summary>
    public static readonly StyledProperty<DiskNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<TreemapControl, DiskNode?>(nameof(SelectedNode));

    /// <summary>底色由主题给（画刷走 DynamicResource，代码里不写死颜色），空窗期不再是死板的深灰板。</summary>
    public static readonly StyledProperty<IBrush?> BackdropBrushProperty =
        AvaloniaProperty.Register<TreemapControl, IBrush?>(nameof(BackdropBrush));

    /// <summary>
    /// 选中项若没有自己的瓦片（目录树里点了个未下钻的深层项），在区块图里高亮
    /// "包含它的最深已合成祖先" 的瓦片 —— 上游对未在图里的项正是这样落在祖先矩形上。
    /// </summary>
    public static readonly StyledProperty<DiskNode?> HighlightDescendantNodeProperty =
        AvaloniaProperty.Register<TreemapControl, DiskNode?>(nameof(HighlightDescendantNode));

    /// <summary>
    /// 指针停着的瓦片。上游 m_hoverItem 只用于覆写状态栏文字
    /// （CMainFrame::UpdatePaneText 的 GetHoverInfo 分支），不参与选中语义，故这里只单向广播出去。
    /// </summary>
    public static readonly StyledProperty<DiskNode?> HoveredNodeProperty =
        AvaloniaProperty.Register<TreemapControl, DiskNode?>(nameof(HoveredNode));

    /// <summary>
    /// 高亮与外发光的颜色，对应上游 COptions::TreeMapHighlightColor（Options.h:247，
    /// 上游默认白色 + 三重描边；这里默认白芯 + 天蓝光晕，浅色主题下由 DynamicResource 换成深蓝）。
    /// </summary>
    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<TreemapControl, Color>(nameof(HighlightColor),
            Color.FromRgb(0x63, 0xB9, 0xFF));

    static TreemapControl()
    {
        TilesProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.DropStaleHover());
        SelectedNodeProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateVisual());
        BackdropBrushProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateVisual());
        HighlightDescendantNodeProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateVisual());
        HoveredNodeProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateVisual());
        HighlightColorProperty.Changed.AddClassHandler<TreemapControl>((c, _) =>
        {
            c._glowPens = null; // 换色即作废缓存的描边
            c.InvalidateVisual();
        });
    }

    public IReadOnlyList<TreemapTile>? Tiles
    {
        get => GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public IBrush? BackdropBrush
    {
        get => GetValue(BackdropBrushProperty);
        set => SetValue(BackdropBrushProperty, value);
    }

    public DiskNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public DiskNode? HighlightDescendantNode
    {
        get => GetValue(HighlightDescendantNodeProperty);
        set => SetValue(HighlightDescendantNodeProperty, value);
    }

    public DiskNode? HoveredNode
    {
        get => GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    public Color HighlightColor
    {
        get => GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    /// <summary>双击瓦片时触发，载荷为要 zoom 到的目录（文件取其父目录，同 OnTreeMapZoomIn）。</summary>
    public event Action<DiskNode>? ZoomRequested;

    private static readonly IBrush SelectionTint = new SolidColorBrush(Avalonia.Media.Color.FromArgb(44, 0xFF, 0xFF, 0xFF));

    // 白芯 + 黑底环：深浅色块上都留得住一条硬边界，光晕负责"被点亮"，描边负责"到底是哪一块"。
    private static readonly Pen SelectionOuterPen = new(new SolidColorBrush(Avalonia.Media.Color.FromArgb(175, 0, 0, 0)), 3.6);
    private static readonly Pen SelectionCorePen = new(new SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)), 2.0);

    // 悬停比选中共用同一套白芯描边，只降一档不透明度：既看得清，又压不过选中态。
    private static readonly Pen HoverOuterPen = new(new SolidColorBrush(Avalonia.Media.Color.FromArgb(90, 0, 0, 0)), 2.6);
    private static readonly Pen HoverCorePen = new(new SolidColorBrush(Avalonia.Media.Color.FromArgb(185, 0xFF, 0xFF, 0xFF)), 1.4);

    /// <summary>
    /// 光晕逐圈加亮加粗（自外向内画）。上游画的是三重同色实心描边（RenderHighlightRectangle），
    /// 这里把厚度摊开成递减的透明度，得到"发光"而不是"三层框"。
    /// </summary>
    private static readonly (double Thickness, byte Alpha)[] GlowRings =
    [
        (12, 18), (9, 32), (6.5, 50), (4, 72), (2.5, 100),
    ];

    private Pen[]? _glowPens;
    private Color _glowPensFor;

    // 悬停提示：白色亮边 + 极淡白罩，浅深色瓦片上都可见；无瓦片时置 null 跳过绘制。
    private DiskNode? _hoverNode;

    private const double CornerRadius = 6;

    /// <summary>瓦片间隙与圆角：留缝让相邻色块不糊成一片，圆角造"拼图卡片"质感。</summary>
    private const double TileGap = 1.0;
    private const double TileCornerRadius = 2.5;

    /// <summary>换不透明度：Avalonia 的 Color 没有 WinUI 那套 WithAlpha，自己补一个。</summary>
    private static Color Alpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// 光晕描边按颜色缓存。Avalonia 的描边以路径为轴向两侧各占一半厚度，
    /// 所以画的时候矩形要外扩 thickness/2，整条描边落在瓦片之外，不污染色块本身。
    /// </summary>
    private Pen[] GlowPens()
    {
        var color = HighlightColor;
        if (_glowPens is null || !_glowPensFor.Equals(color))
        {
            var pens = new Pen[GlowRings.Length];
            for (var i = 0; i < pens.Length; i++)
                pens[i] = new Pen(new SolidColorBrush(Alpha(color, GlowRings[i].Alpha)), GlowRings[i].Thickness);
            _glowPens = pens;
            _glowPensFor = color;
        }
        return _glowPens;
    }

    /// <summary>重新布局后原命中瓦片可能已经没了（扫描期每 250ms 换一批），悬停态随之作废。</summary>
    private void DropStaleHover()
    {
        var tiles = Tiles;
        if (_hoverNode is not null && (tiles is null || tiles.All(t => !ReferenceEquals(t.Node, _hoverNode))))
        {
            _hoverNode = null;
            HoveredNode = null;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var tiles = Tiles;
        var full = new Rect(0, 0, w, h);
        // 外层卡片是圆角的，控件自己不裁就会把直角底色盖到卡片圆角之外。
        using (context.PushClip(new RoundedRect(full, CornerRadius, CornerRadius)))
        {
            if (BackdropBrush is { } backdrop) context.FillRectangle(backdrop, full);
            if (tiles is null || tiles.Count == 0) return;

            var typeface = new Typeface("Inter");
            var shadowPen = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(90, 0, 0, 0)), 1);

            foreach (var tile in tiles)
            {
                var rc = TileRect(tile, w, h);
                if (rc.Width < 1 || rc.Height < 1) continue;

                var radius = Math.Min(TileCornerRadius, Math.Min(rc.Width, rc.Height) / 2);
                context.DrawRectangle(tile.Brush, shadowPen, rc, radius, radius);

                // 顶缘一道半透明白高光、底缘一道半透明黑阴影：色块立刻有了受光面。
                if (rc.Width >= 4 && rc.Height >= 3)
                {
                    var hl = new SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 255, 255, 255));
                    var sh = new SolidColorBrush(Avalonia.Media.Color.FromArgb(45, 0, 0, 0));
                    context.FillRectangle(hl, new Rect(rc.X + radius, rc.Y, rc.Width - 2 * radius, 1));
                    context.FillRectangle(sh, new Rect(rc.X + radius, rc.Bottom - 1, rc.Width - 2 * radius, 1));
                }

                if (rc.Width < 46 || rc.Height < 16 || tile.Depth >= 2) continue;
                var luminance = Luminance(tile);
                var textBrush = luminance >= 0.6 ? Brushes.Black : Brushes.White;
                var text = MakeLabel(tile.Node.Name, typeface, 13 - tile.Depth * 2, textBrush, rc.Width - 6);
                // 先铺四向偏移的同色系描底层再填正文：任何底色上文字都有对比度。
                var haloBrush = new SolidColorBrush(
                    luminance >= 0.6 ? Avalonia.Media.Color.FromArgb(110, 255, 255, 255)
                                     : Avalonia.Media.Color.FromArgb(110, 0, 0, 0));
                var origin = rc.TopLeft + new Point(3, 1);
                foreach (var off in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
                    context.DrawText(MakeLabel(tile.Node.Name, typeface, 13 - tile.Depth * 2,
                        haloBrush, rc.Width - 6), origin + off);
                context.DrawText(text, origin);
            }

            // 悬停先画、选中后画：两者是同一块时选中态盖住悬停态，不做双重叠加。
            if (_hoverNode is { } hovered && !ReferenceEquals(hovered, HighlightDescendantNode ?? SelectedNode))
                foreach (var tile in tiles)
                    if (ReferenceEquals(tile.Node, hovered))
                        DrawHoverRing(context, TileRect(tile, w, h));

            var selected = HighlightDescendantNode ?? SelectedNode;
            if (selected is not null)
            {
                foreach (var tile in tiles)
                {
                    if (!ReferenceEquals(tile.Node, selected)) continue;
                    DrawSelectionHighlight(context, TileRect(tile, w, h), tile);
                }
            }

            // 选中项自身有瓦片时直接高亮；没有（点的是未下钻的深层项）则高亮包含它的
            // 最深已合成祖先瓦片，虚线框与发光实心区分"就是这块 / 在这块里面"。
            var target = HighlightDescendantNode ?? SelectedNode;
            if (target is not null && tiles.All(t => !ReferenceEquals(t.Node, target)))
            {
                var ancestors = new HashSet<DiskNode>();
                for (var p = target.Parent; p is not null; p = p.Parent) ancestors.Add(p);
                var deepest = tiles
                    .Where(t => ancestors.Contains(t.Node))
                    .OrderBy(t => t.W * t.H)
                    .FirstOrDefault();
                if (deepest is not null)
                {
                    var rc = TileRect(deepest, w, h);
                    var glowPens = GlowPens();
                    for (var i = 0; i < 3; i++)
                    {
                        var t = glowPens[i].Thickness;
                        context.DrawRectangle(null, glowPens[i], rc.Inflate(t / 2),
                            TileCornerRadius + t / 2, TileCornerRadius + t / 2);
                    }
                    var dashPen = new Pen(new SolidColorBrush(Alpha(HighlightColor, 230)), 2,
                        new DashStyle([2, 2], 0));
                    context.DrawRectangle(null, dashPen, rc.Inflate(1), TileCornerRadius + 1, TileCornerRadius + 1);
                }
            }
        }
    }

    /// <summary>归一化瓦片 → 像素矩形，每边收进半条间隙：缝隙均匀、相邻瓦片互不贴死。</summary>
    private static Rect TileRect(TreemapTile tile, double w, double h)
    {
        var g = Math.Min(TileGap / 2, 2);
        return new Rect(tile.X * w + g, tile.Y * h + g,
            Math.Max(0, tile.W * w - 2 * g), Math.Max(0, tile.H * h - 2 * g));
    }

    /// <summary>色块亮度（Rec.601），文字与罩色都按它决定用黑还是白。</summary>
    private static double Luminance(TreemapTile tile) =>
        tile.Brush is ISolidColorBrush b
            ? (0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B) / 255.0
            : 0.5;

    /// <summary>
    /// 选中态：块内罩一层（浅块压成高亮色、深块提亮）+ 外发光 + 黑底白芯硬边。
    /// 小于 7px 的块画不出环，照上游 RenderHighlightRectangle 的 else 分支整块填高亮色。
    /// </summary>
    private void DrawSelectionHighlight(DrawingContext context, Rect rc, TreemapTile tile)
    {
        if (rc.Width < 7 || rc.Height < 7)
        {
            context.DrawRectangle(new SolidColorBrush(HighlightColor), null, rc);
            return;
        }

        var tint = Luminance(tile) >= 0.6
            ? new SolidColorBrush(Alpha(HighlightColor, 62))
            : SelectionTint;
        context.DrawRectangle(tint, null, rc, TileCornerRadius, TileCornerRadius);

        foreach (var pen in GlowPens())
            context.DrawRectangle(null, pen, rc.Inflate(pen.Thickness / 2),
                TileCornerRadius + pen.Thickness / 2, TileCornerRadius + pen.Thickness / 2);

        context.DrawRectangle(null, SelectionOuterPen, rc.Inflate(1.2),
            TileCornerRadius + 1.2, TileCornerRadius + 1.2);
        context.DrawRectangle(null, SelectionCorePen, rc.Inflate(0.4),
            TileCornerRadius + 0.4, TileCornerRadius + 0.4);
    }

    /// <summary>悬停态：同一套描边降一档强度，不加发光，避免扫鼠标时满图冒光。</summary>
    private void DrawHoverRing(DrawingContext context, Rect rc)
    {
        if (rc.Width < 4 || rc.Height < 4)
        {
            context.DrawRectangle(new SolidColorBrush(Alpha(HighlightColor, 180)), null, rc);
            return;
        }
        context.DrawRectangle(null, HoverOuterPen, rc.Inflate(0.9), TileCornerRadius + 0.9, TileCornerRadius + 0.9);
        context.DrawRectangle(null, HoverCorePen, rc.Inflate(0.3), TileCornerRadius + 0.3, TileCornerRadius + 0.3);
    }

    /// <summary>瓦片标签的统一构造：两行上限 + 省略号。</summary>
    private static FormattedText MakeLabel(string text, Typeface typeface, double size, IBrush brush, double maxWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(0, maxWidth),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>命中最深（面积最小）的瓦片 —— 即视觉上真正看到的那一块，同上游 FindItemByPoint。</summary>
    private TreemapTile? HitTest(Point p)
    {
        var tiles = Tiles;
        if (tiles is null || tiles.Count == 0) return null;

        TreemapTile? hit = null;
        foreach (var tile in tiles)
        {
            var rc = new Rect(tile.X * Bounds.Width, tile.Y * Bounds.Height,
                tile.W * Bounds.Width, tile.H * Bounds.Height);
            if (!rc.Contains(p)) continue;
            if (hit is null || tile.W * tile.H < hit.W * tile.H)
                hit = tile;
        }
        return hit;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var node = HitTest(e.GetPosition(this))?.Node;
        if (ReferenceEquals(node, _hoverNode)) return;
        _hoverNode = node;
        HoveredNode = node; // 推给 VM：上游悬停时状态栏整条换成悬停项的完整路径
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverNode is null) return;
        _hoverNode = null;
        HoveredNode = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit is null) return;

        // 单击只改选中（经双向绑定回流到 VM，列表侧同步高亮），不动基准子树：
        // 同上游 CGraphView::OnLButtonDown 仅 NotifyOtherPanes(MODEL_CHANGE_SELECTION_ACTION)。
        SelectedNode = hit.Node;
        if (e.ClickCount < 2) return;

        // 双击才 zoom，且目标就是点到的这一块（文件则其父目录，同 OnTreeMapZoomIn）。
        // 是否已是当前基准由 VM 判断并忽略，这里不钳制层级。
        var target = hit.Node.Kind == NodeKind.Directory ? hit.Node : hit.Node.Parent;
        if (target is not null)
            ZoomRequested?.Invoke(target);
    }
}
