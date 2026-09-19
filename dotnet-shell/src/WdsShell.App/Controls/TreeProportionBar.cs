using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using WdsShell.Core.Layout;

namespace WdsShell.App.Controls;

/// <summary>
/// 目录树"占比"列单元格：轨道 + 子树条（占直接父级比）+ 内嵌绝对条（占扫描根比）。
/// 几何与混色逐条对应上游 CItem::DrawSubItem(COL_SIZE_PROPORTION)（Item.Extended.cpp:100-175），
/// 包括 MFC 侧的 Deflate(2,4) 内缩与"每深一级右移 SizeProportionIndent"。
/// </summary>
public sealed class TreeProportionBar : Control
{
    public static readonly StyledProperty<double> SubtreeFractionProperty =
        AvaloniaProperty.Register<TreeProportionBar, double>(nameof(SubtreeFraction));

    public static readonly StyledProperty<double> AbsoluteFractionProperty =
        AvaloniaProperty.Register<TreeProportionBar, double>(nameof(AbsoluteFraction));

    public static readonly StyledProperty<int> IndentProperty =
        AvaloniaProperty.Register<TreeProportionBar, int>(nameof(Indent));

    /// <summary>0xRRGGBB，按行缩进取自 <see cref="TreeBarPalette"/>，不是扩展名色。</summary>
    public static readonly StyledProperty<uint> BaseColorProperty =
        AvaloniaProperty.Register<TreeProportionBar, uint>(nameof(BaseColor), 0x40408C);

    static TreeProportionBar()
    {
        SubtreeFractionProperty.Changed.AddClassHandler<TreeProportionBar>(static (c, _) => c.InvalidateVisual());
        AbsoluteFractionProperty.Changed.AddClassHandler<TreeProportionBar>(static (c, _) => c.InvalidateVisual());
        IndentProperty.Changed.AddClassHandler<TreeProportionBar>(static (c, _) => c.InvalidateVisual());
        BaseColorProperty.Changed.AddClassHandler<TreeProportionBar>(static (c, _) => c.InvalidateVisual());
    }

    public double SubtreeFraction
    {
        get => GetValue(SubtreeFractionProperty);
        set => SetValue(SubtreeFractionProperty, value);
    }

    public double AbsoluteFraction
    {
        get => GetValue(AbsoluteFractionProperty);
        set => SetValue(AbsoluteFractionProperty, value);
    }

    public int Indent
    {
        get => GetValue(IndentProperty);
        set => SetValue(IndentProperty, value);
    }

    public uint BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    /// <summary>上游 blendColor：逐通道线性插值。</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        byte Ch(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Ch(from.R, to.R), Ch(from.G, to.G), Ch(from.B, to.B));
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // rc.Deflate(2, 4) 后 rc.left += indent * SizeProportionIndent
        var left = 2 + Indent * TreeBarPalette.SizeProportionIndent;
        var track = new Rect(left, 4, Math.Max(0, w - 2 - left), Math.Max(0, h - 8));
        if (track.Width <= 1 || track.Height <= 1) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var color = Rgb(BaseColor);
        var neutralBack = dark ? Color.FromRgb(40, 40, 40) : Color.FromRgb(225, 225, 225);
        var trackFill = dark ? Blend(neutralBack, Colors.White, 0.10) : Blend(neutralBack, Colors.Black, 0.06);
        var trackBorder = dark ? Blend(trackFill, Colors.White, 0.18) : Blend(trackFill, Colors.Black, 0.18);
        var subtreeFill = Blend(trackFill, color, dark ? 0.68 : 0.48);
        var subtreeGlow = Blend(subtreeFill, Colors.White, dark ? 0.18 : 0.30);
        var absoluteFill = dark ? Blend(color, Colors.White, 0.12) : Blend(color, Colors.Black, 0.10);
        var absoluteGlow = Blend(absoluteFill, Colors.White, dark ? 0.16 : 0.26);
        var absoluteEdge = Blend(absoluteFill, Colors.Black, dark ? 0.18 : 0.12);

        context.DrawRectangle(new SolidColorBrush(trackFill),
            new Pen(new SolidColorBrush(trackBorder), 1), track, 1.5, 1.5);

        // rc.Deflate(1, 1)
        var inner = new Rect(track.X + 1, track.Y + 1, track.Width - 2, track.Height - 2);
        if (inner.Width <= 0 || inner.Height <= 0) return;

        double X(double fraction) => inner.X + Math.Round(inner.Width * Math.Clamp(fraction, 0, 1));

        var subtreeRight = X(SubtreeFraction);
        if (subtreeRight > inner.X)
        {
            var rcSubtree = new Rect(inner.X, inner.Y, subtreeRight - inner.X, inner.Height);
            context.DrawRectangle(new SolidColorBrush(subtreeFill), null, rcSubtree, 1.5, 1.5);
            if (rcSubtree.Height >= 3 && rcSubtree.Width >= 2)
                context.FillRectangle(new SolidColorBrush(subtreeGlow),
                    new Rect(rcSubtree.X + 1, rcSubtree.Y, rcSubtree.Width - 2, 1));
            if (subtreeRight < inner.Right)
                context.FillRectangle(new SolidColorBrush(trackBorder),
                    new Rect(subtreeRight, inner.Y, 1, inner.Height));
        }

        // rcAbsolute.Deflate(0, 2)：绝对条在子树条内上下各内缩 2px
        var absoluteRight = X(Math.Min(SubtreeFraction, AbsoluteFraction));
        if (absoluteRight > inner.X && inner.Height > 4)
        {
            var rcAbsolute = new Rect(inner.X, inner.Y + 2, absoluteRight - inner.X, inner.Height - 4);
            context.DrawRectangle(new SolidColorBrush(absoluteFill), null, rcAbsolute, 1.5, 1.5);
            if (rcAbsolute.Height >= 3 && rcAbsolute.Width >= 2)
                context.FillRectangle(new SolidColorBrush(absoluteGlow),
                    new Rect(rcAbsolute.X + 1, rcAbsolute.Y, rcAbsolute.Width - 2, 1));
            if (rcAbsolute.Height >= 2)
                context.FillRectangle(new SolidColorBrush(absoluteEdge),
                    new Rect(rcAbsolute.Right - 1, rcAbsolute.Y + 1, 1, rcAbsolute.Height - 2));
        }
    }
}
