using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WdsShell.App.ViewModels;

namespace WdsShell.App.Controls;

/// <summary>扩展名占比色带（对应 WinDirStat 的 ExtensionView 条）。</summary>
public sealed class ExtensionBarControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<ExtensionSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<ExtensionBarControl, IReadOnlyList<ExtensionSegment>?>(nameof(Segments));

    static ExtensionBarControl()
    {
        SegmentsProperty.Changed.AddClassHandler<ExtensionBarControl>((c, _) => c.InvalidateVisual());
    }

    public IReadOnlyList<ExtensionSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x20, 0x20, 0x24)), new Rect(0, 0, w, h));
        var segments = Segments;
        if (segments is null || segments.Count == 0 || w <= 0) return;

        var total = segments.Sum(s => s.Bytes);
        if (total <= 0) return;

        var x = 0.0;
        var typeface = new Typeface("Inter");
        foreach (var seg in segments)
        {
            var width = seg.Bytes * w / total;
            var brush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                (byte)((seg.Color >> 16) & 0xFF), (byte)((seg.Color >> 8) & 0xFF), (byte)(seg.Color & 0xFF)));
            context.DrawRectangle(brush, null, new Rect(x, 0, width, h));
            if (width > 40)
            {
                var text = new FormattedText(seg.Extension, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 10, Brushes.White)
                {
                    MaxTextWidth = width - 4,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(x + 3, (h - text.Height) / 2));
            }
            x += width;
        }
    }
}
