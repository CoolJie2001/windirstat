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
        if (w <= 0 || h <= 0) return;
        var full = new Rect(0, 0, w, h);

        // 圆角胶囊外形 + 随主题的内凹底色，替代原来写死的深灰直角条。
        const double radius = 4;
        using (context.PushClip(new RoundedRect(full, radius, radius)))
        {
            context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromArgb(28, 0, 0, 0)),
                full);

            var segments = Segments;
            var total = segments?.Sum(s => s.Bytes) ?? 0;
            if (segments is not null && total > 0)
            {
                var x = 0.0;
                var typeface = new Typeface("Inter");
                foreach (var seg in segments)
                {
                    var width = seg.Bytes * w / total;
                    var brush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(
                        (byte)((seg.Color >> 16) & 0xFF), (byte)((seg.Color >> 8) & 0xFF), (byte)(seg.Color & 0xFF)));
                    var rc = new Rect(x, 0, width, h);
                    if (rc.Width > 0.5) context.FillRectangle(brush, rc);

                    // 顶部 1px 高光 + 段间 1px 暗缝：色带立刻有了立体感与分段感。
                    if (rc.Width >= 3)
                    {
                        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 255, 255, 255)),
                            new Rect(rc.X, 0, rc.Width, 1));
                        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 0, 0, 0)),
                            new Rect(rc.Right - 1, 1, 1, h - 2));
                    }
                    if (width > 40)
                    {
                        // 白字铺四向偏移的黑描底层再填正文，浅色段（如浅黄、浅绿）上不再看不清。
                        var halo = new SolidColorBrush(Avalonia.Media.Color.FromArgb(120, 0, 0, 0));
                        var origin = new Point(x + 3, (h - 14) / 2);
                        foreach (var off in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
                        {
                            var shadow = new FormattedText(seg.Extension, CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, typeface, 10, halo)
                            {
                                MaxTextWidth = width - 4,
                                Trimming = TextTrimming.CharacterEllipsis,
                            };
                            context.DrawText(shadow, origin + off);
                        }
                        var text = new FormattedText(seg.Extension, CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, typeface, 10, Brushes.White)
                        {
                            MaxTextWidth = width - 4,
                            Trimming = TextTrimming.CharacterEllipsis,
                        };
                        context.DrawText(text, origin);
                    }
                    x += width;
                }
            }
        }

        // 描边画在裁剪外沿，任何分段组合下外框都是完整的一圈。
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(45, 0, 0, 0)), 1),
            new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);
    }
}
