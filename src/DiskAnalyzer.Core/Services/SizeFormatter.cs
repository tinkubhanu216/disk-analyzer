using System.Globalization;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// Converts byte counts to human-readable text. Uses binary multiples (1 KB = 1024 bytes), matching Windows Explorer.
/// Only the UI and exports call this; all calculations stay in bytes.
/// </summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes) => Format(bytes, CultureInfo.CurrentCulture);

    public static string Format(long bytes, IFormatProvider culture)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes, culture);
        }

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : string.Create(culture, $"{bytes} bytes");
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, culture) + " " + Units[unit];
    }

    public static string FormatSigned(long bytes) => bytes > 0 ? "+" + Format(bytes) : Format(bytes);

    /// <summary>Percentage of <paramref name="part"/> in <paramref name="whole"/> (0–100), 0 when the whole is 0.</summary>
    public static double Percent(long part, long whole) => whole > 0 ? part * 100.0 / whole : 0;

    public static string FormatPercent(double percent) =>
        percent switch
        {
            0 => "0%",
            < 0.01 => "<0.01%",
            < 10 => percent.ToString("0.00", CultureInfo.CurrentCulture) + "%",
            _ => percent.ToString("0.0", CultureInfo.CurrentCulture) + "%",
        };

    public static string FormatCount(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    public static string FormatDuration(TimeSpan span) =>
        span.TotalSeconds < 1 ? $"{span.TotalMilliseconds:0} ms"
        : span.TotalMinutes < 1 ? $"{span.TotalSeconds:0.0} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
        : $"{(int)span.TotalHours} h {span.Minutes} min";

    /// <summary>Parses text such as "500 MB", "1.5GB", "200k" or "1024" into bytes.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().ToUpperInvariant().Replace("BYTES", string.Empty, StringComparison.Ordinal).Replace("B", string.Empty, StringComparison.Ordinal).Trim();
        long multiplier = 1;
        if (s.Length > 0 && char.IsLetter(s[^1]))
        {
            multiplier = s[^1] switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                'T' => 1L << 40,
                _ => 0,
            };
            s = s[..^1].Trim();
        }

        if (multiplier == 0 || !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value < 0 || value * multiplier > long.MaxValue)
        {
            return false;
        }

        bytes = (long)(value * multiplier);
        return true;
    }
}
