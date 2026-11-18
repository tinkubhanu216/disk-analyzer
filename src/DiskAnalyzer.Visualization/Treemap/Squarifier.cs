using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Visualization.Treemap;

/// <summary>
/// Squarified treemap subdivision (Bruls, Huizing &amp; van Wijk, 2000).
/// Lays out values (sorted descending) into a rectangle so that each output rectangle's area is
/// proportional to its value and aspect ratios stay close to 1.
/// </summary>
public static class Squarifier
{
    public static void Squarify(ReadOnlySpan<double> values, TreemapRect bounds, Span<TreemapRect> output)
    {
        if (output.Length < values.Length)
        {
            throw new ArgumentException("Output span is too small.", nameof(output));
        }

        double total = 0;
        foreach (double v in values)
        {
            total += Math.Max(0, v);
        }

        if (total <= 0 || bounds.IsEmpty)
        {
            for (int i = 0; i < values.Length; i++)
            {
                output[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
            }

            return;
        }

        double scale = bounds.Area / total;
        TreemapRect remaining = bounds;
        int start = 0;
        int n = values.Length;

        while (start < n)
        {
            double side = Math.Min(remaining.Width, remaining.Height);
            if (side <= 0)
            {
                for (int i = start; i < n; i++)
                {
                    output[i] = new TreemapRect(remaining.X, remaining.Y, 0, 0);
                }

                return;
            }

            // Grow the row while the worst aspect ratio improves.
            double rowMax = Math.Max(0, values[start]) * scale;
            double rowSum = 0;
            double worst = double.PositiveInfinity;
            int end = start;
            while (end < n)
            {
                double area = Math.Max(0, values[end]) * scale;
                double candidateSum = rowSum + area;
                double candidateWorst = Worst(rowMax, area, candidateSum, side);
                if (end > start && candidateWorst > worst)
                {
                    break;
                }

                rowSum = candidateSum;
                worst = candidateWorst;
                end++;
            }

            bool isLastRow = end == n;
            if (remaining.Width >= remaining.Height)
            {
                // Column on the left side.
                double columnWidth = isLastRow ? remaining.Width : rowSum / remaining.Height;
                double y = remaining.Y;
                for (int i = start; i < end; i++)
                {
                    double h = i == end - 1 ? remaining.Bottom - y : Math.Max(0, values[i]) * scale / columnWidth;
                    output[i] = new TreemapRect(remaining.X, y, columnWidth, Math.Max(0, h));
                    y += h;
                }

                remaining = new TreemapRect(remaining.X + columnWidth, remaining.Y, Math.Max(0, remaining.Width - columnWidth), remaining.Height);
            }
            else
            {
                // Row along the top.
                double rowHeight = isLastRow ? remaining.Height : rowSum / remaining.Width;
                double x = remaining.X;
                for (int i = start; i < end; i++)
                {
                    double w = i == end - 1 ? remaining.Right - x : Math.Max(0, values[i]) * scale / rowHeight;
                    output[i] = new TreemapRect(x, remaining.Y, Math.Max(0, w), rowHeight);
                    x += w;
                }

                remaining = new TreemapRect(remaining.X, remaining.Y + rowHeight, remaining.Width, Math.Max(0, remaining.Height - rowHeight));
            }

            start = end;
        }
    }

    /// <summary>Worst aspect ratio of a row with the given largest/smallest areas, total area and side length.</summary>
    private static double Worst(double maxArea, double minArea, double sum, double side)
    {
        if (minArea <= 0 || sum <= 0)
        {
            return double.PositiveInfinity;
        }

        double s2 = sum * sum;
        double w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }
}
