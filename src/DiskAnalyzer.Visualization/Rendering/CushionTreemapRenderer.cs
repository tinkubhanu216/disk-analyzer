using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Visualization.Rendering;

/// <summary>
/// Renders a treemap into a 32-bit pixel buffer (0x00RRGGBB, i.e. WPF <c>Bgr32</c>) using cushion shading
/// (van Wijk &amp; van de Wetering). Folder frames are filled first; leaf tiles never overlap, so they are
/// shaded in parallel without locking. One bitmap replaces what would otherwise be thousands of WPF elements.
/// </summary>
public sealed class CushionTreemapRenderer : ITreemapRenderer
{
    // Light direction (-1, -1, 10), normalized.
    private const double LightX = -0.09759;
    private const double LightY = -0.09759;
    private const double LightZ = 0.99043;
    private const double Ambient = 0.35;
    private const double Diffuse = 0.80;

    public uint BackgroundColor { get; set; } = 0x202020;

    public void Render(TreemapLayout layout, int[] pixels, int width, int height, Func<TreemapNode, uint> colorSelector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(colorSelector);
        if (pixels.Length < width * height)
        {
            throw new ArgumentException("Pixel buffer is smaller than width × height.", nameof(pixels));
        }

        Array.Fill(pixels, (int)BackgroundColor, 0, width * height);

        var leaves = new List<TreemapNode>(layout.Nodes.Count);
        foreach (TreemapNode node in layout.Nodes)
        {
            if (node.Kind == TreemapNodeKind.Folder)
            {
                if (node != layout.Root)
                {
                    FillFlat(pixels, width, height, node.Bounds, colorSelector(node));
                }
            }
            else
            {
                leaves.Add(node);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, leaves.Count, Math.Max(64, leaves.Count / (Environment.ProcessorCount * 8) + 1)), options, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                TreemapNode leaf = leaves[i];
                RenderCushion(pixels, width, height, leaf, colorSelector(leaf));
            }
        });
    }

    private static (int X0, int Y0, int X1, int Y1) Snap(TreemapRect r, int width, int height) =>
        (Math.Clamp((int)Math.Round(r.X), 0, width),
         Math.Clamp((int)Math.Round(r.Y), 0, height),
         Math.Clamp((int)Math.Round(r.Right), 0, width),
         Math.Clamp((int)Math.Round(r.Bottom), 0, height));

    private static void FillFlat(int[] pixels, int width, int height, TreemapRect rect, uint color)
    {
        var (x0, y0, x1, y1) = Snap(rect, width, height);
        for (int y = y0; y < y1; y++)
        {
            pixels.AsSpan((y * width) + x0, x1 - x0).Fill((int)color);
        }
    }

    private static void RenderCushion(int[] pixels, int width, int height, TreemapNode node, uint color)
    {
        var (x0, y0, x1, y1) = Snap(node.Bounds, width, height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        double r = (color >> 16) & 0xFF;
        double g = (color >> 8) & 0xFF;
        double b = color & 0xFF;
        double s1 = node.S1, s2 = node.S2, s3 = node.S3, s4 = node.S4;

        // Tiny tiles: flat color with a dark edge reads better than shading 2 pixels.
        if (x1 - x0 < 3 || y1 - y0 < 3)
        {
            FillFlat(pixels, width, height, node.Bounds, Scale(r, g, b, 0.85));
            return;
        }

        for (int y = y0; y < y1; y++)
        {
            double ny = -((2 * s3 * (y + 0.5)) + s4);
            Span<int> row = pixels.AsSpan(y * width, width);
            for (int x = x0; x < x1; x++)
            {
                double nx = -((2 * s1 * (x + 0.5)) + s2);
                double cos = ((nx * LightX) + (ny * LightY) + LightZ) / Math.Sqrt((nx * nx) + (ny * ny) + 1.0);
                double intensity = Ambient + (Diffuse * Math.Max(0, cos));
                row[x] = (int)Scale(r, g, b, intensity);
            }
        }
    }

    private static uint Scale(double r, double g, double b, double factor)
    {
        uint rr = (uint)Math.Min(255, r * factor);
        uint gg = (uint)Math.Min(255, g * factor);
        uint bb = (uint)Math.Min(255, b * factor);
        return (rr << 16) | (gg << 8) | bb;
    }
}
