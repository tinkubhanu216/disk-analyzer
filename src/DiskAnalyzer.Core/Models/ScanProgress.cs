namespace DiskAnalyzer.Core.Models;

public enum ScanState
{
    Idle,
    Preparing,
    Scanning,
    Paused,
    Aggregating,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Immutable progress snapshot handed to the UI.</summary>
public sealed record ScanProgress
{
    public ScanState State { get; init; }

    public string Phase { get; init; } = string.Empty;

    public string? CurrentPath { get; init; }

    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesDiscovered { get; init; }

    public long ErrorCount { get; init; }

    /// <summary>0..1, or null when no meaningful fraction is known.</summary>
    public double? Fraction { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Only set when a reasonable estimate is available.</summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    public string? ProviderName { get; init; }

    public double ItemsPerSecond => Elapsed.TotalSeconds > 0.2 ? (FilesScanned + FoldersScanned) / Elapsed.TotalSeconds : 0;

    public static ScanProgress Idle { get; } = new() { State = ScanState.Idle };
}

/// <summary>A filesystem error recorded during a scan.</summary>
public sealed record ScanError(string Path, string Message);
