using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Visualization.Rendering;

public enum TreemapColorMode
{
    /// <summary>Color by file category, with a stable shade per extension.</summary>
    FileType,

    /// <summary>Heat by last-modified time: recent is hot, old is cold.</summary>
    Age,

    /// <summary>Heat by size relative to the largest item in view.</summary>
    Size,
}

/// <summary>Colors (0x00RRGGBB) for treemap tiles and the legend.</summary>
public static class TreemapColorScheme
{
    private static readonly uint[] CategoryColors =
    [
        0x3B82F6, // Documents
        0x22C55E, // Images
        0xEF4444, // Videos
        0xA855F7, // Audio
        0xF97316, // Applications
        0xEAB308, // Archives
        0x14B8A6, // Development
        0x64748B, // System
        0x9CA3AF, // Other
    ];

    public const uint FolderTileColor = 0x8A8F98;
    public const uint AggregateColor = 0x5B616B;

    public static uint GetCategoryColor(FileCategory category) => CategoryColors[(int)category];

    public static uint FolderFrameColor(bool dark, int depth)
    {
        // Slightly lighter frames for deeper folders so nesting stays readable.
        int step = Math.Min(depth, 8) * 6;
        return dark ? Gray(0x2C + step) : Gray(0xC8 - step);
    }

    /// <summary>Category color shifted by a deterministic per-extension amount so extensions are distinguishable.</summary>
    public static uint GetExtensionColor(string extension)
    {
        uint baseColor = GetCategoryColor(FileCategoryClassifier.Classify(extension));
        if (string.IsNullOrEmpty(extension))
        {
            return baseColor;
        }

        uint hash = 2166136261;
        foreach (char c in extension)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
        }

        RgbToHsl(baseColor, out double h, out double s, out double l);
        h = (h + (((int)(hash % 25) - 12) / 360.0) + 1) % 1;
        l = Math.Clamp(l + ((((int)((hash >> 8) % 21)) - 10) / 100.0), 0.25, 0.75);
        return HslToRgb(h, s, l);
    }

    /// <summary>Blue (0) → green → yellow → red (1).</summary>
    public static uint Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double h = (1 - t) * 240.0 / 360.0;
        return HslToRgb(h, 0.75, 0.5);
    }

    /// <summary>Heat for file age: modified today → 1, older than ~5 years → 0 (log scale).</summary>
    public static double AgeHeat(long lastWriteUtcTicks, DateTime nowUtc)
    {
        if (lastWriteUtcTicks <= 0)
        {
            return 0;
        }

        double days = Math.Max(0, (nowUtc.Ticks - lastWriteUtcTicks) / (double)TimeSpan.TicksPerDay);
        return 1 - Math.Clamp(Math.Log10(days + 1) / Math.Log10(1826), 0, 1);
    }

    public static System.Windows.Media.Color ToMediaColor(uint rgb) =>
        System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static uint Gray(int v)
    {
        uint c = (uint)Math.Clamp(v, 0, 255);
        return (c << 16) | (c << 8) | c;
    }

    private static void RgbToHsl(uint rgb, out double h, out double s, out double l)
    {
        double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min)
        {
            h = s = 0;
            return;
        }

        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        h /= 6;
    }

    private static uint HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
            double p = (2 * l) - q;
            r = HueToRgb(p, q, h + (1 / 3.0));
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - (1 / 3.0));
        }

        return ((uint)Math.Round(r * 255) << 16) | ((uint)Math.Round(g * 255) << 8) | (uint)Math.Round(b * 255);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t < 1 / 6.0 ? p + ((q - p) * 6 * t)
            : t < 1 / 2.0 ? q
            : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6)
            : p;
    }
}
