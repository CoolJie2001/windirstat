namespace WdsShell.Core.Layout;

public readonly record struct LayoutTile(int Index, double X, double Y, double Width, double Height);

/// <summary>
/// Squarified treemap 布局（Bruls et al.）。
/// 与上游 Controls/TreeMapLayout.cpp 同一算法族；此处独立实现，
/// 输出归一化坐标由调用方缩放到像素，纯函数、无状态、可单测。
/// </summary>
public static class TreeMapLayout
{
    public static List<LayoutTile> Squarify(IReadOnlyList<double> values, double x, double y, double w, double h)
    {
        var result = new List<LayoutTile>(values.Count);
        var order = new List<int>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] > 0) order.Add(i);
        }
        order.Sort((a, b) => values[b].CompareTo(values[a]));
        if (order.Count == 0 || w <= 0 || h <= 0)
            return result;

        var total = order.Sum(i => values[i]);
        var rect = new RectD(x, y, w, h);
        var queue = new Queue<int>(order);
        var current = new List<int>();
        var scale = (rect.W * rect.H) / total;

        while (queue.Count > 0)
        {
            current.Clear();
            var rowValue = 0d;
            var worst = double.MaxValue;

            // 贪心：只要加入当前行能改善（或不恶化）最坏长宽比，就继续加
            while (queue.Count > 0)
            {
                var nextValue = values[queue.Peek()] * scale;
                var testWorst = WorstRatio(current, rowValue, nextValue, rect);
                if (testWorst > worst) break;
                worst = testWorst;
                current.Add(queue.Dequeue());
                rowValue += nextValue;
            }
            if (current.Count == 0 && queue.Count > 0)
            {
                current.Add(queue.Dequeue());
                rowValue += values[current[0]] * scale;
            }

            rect = LayoutRow(current, rowValue, rect, result, values, scale);
        }
        return result;
    }

    private record struct RectD(double X, double Y, double W, double H);

    private static double WorstRatio(List<int> row, double rowValue, double nextValue, RectD rect)
    {
        var shorter = Math.Min(rect.W, rect.H);
        var sum = rowValue + nextValue;
        if (sum <= 0 || shorter <= 0) return double.MaxValue;
        var s2 = shorter * shorter;
        var rmax2 = Math.Max(rowValue, nextValue) > 0
            ? Math.Max(s2 * rowValue / (sum * sum), sum * sum / (s2 * Math.Max(1e-9, rowValue)))
            : double.MaxValue;
        var rmin2 = Math.Min(s2 * rowValue / (sum * sum), sum * sum / (s2 * Math.Max(1e-9, nextValue)));
        return Math.Max(rmax2, rmin2);
    }

    private static RectD LayoutRow(List<int> row, double rowValue, RectD rect, List<LayoutTile> result,
        IReadOnlyList<double> values, double scale)
    {
        if (row.Count == 0) return rect;
        var horizontal = rect.W >= rect.H;
        var thickness = horizontal ? rowValue / rect.W : rowValue / rect.H; // 行带宽度
        var offset = 0d;
        foreach (var i in row)
        {
            var extent = values[i] * scale / Math.Max(thickness, 1e-9);
            if (horizontal)
            {
                result.Add(new LayoutTile(i, rect.X + offset, rect.Y, extent, thickness));
                offset += extent;
            }
            else
            {
                result.Add(new LayoutTile(i, rect.X, rect.Y + offset, thickness, extent));
                offset += extent;
            }
        }
        return horizontal
            ? new RectD(rect.X, rect.Y + thickness, rect.W, Math.Max(0, rect.H - thickness))
            : new RectD(rect.X + thickness, rect.Y, Math.Max(0, rect.W - thickness), rect.H);
    }
}
