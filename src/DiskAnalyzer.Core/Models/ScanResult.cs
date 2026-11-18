namespace DiskAnalyzer.Core.Models;

/// <summary>Where the displayed data came from. Always shown to the user.</summary>
public enum ScanSource
{
    /// <summary>Produced by a scan in this session.</summary>
    Live,

    /// <summary>Latest stored scan of a volume, loaded to avoid rescanning. Possibly stale.</summary>
    Cached,

    /// <summary>An older stored scan opened from Scan History.</summary>
    Historical,
}

public enum ScanStatus
{
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

/// <summary>Outcome of a scan (live or loaded from history).</summary>
public sealed class ScanResult
{
    public required ScanTree Tree { get; init; }

    public required string RootPath { get; init; }

    public DriveInfoModel? Drive { get; init; }

    public ScanSource Source { get; init; }

    public ScanStatus Status { get; init; }

    public DateTime StartedUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public string ProviderName { get; init; } = string.Empty;

    public long ErrorCount { get; init; }

    public IReadOnlyList<ScanError> ErrorSamples { get; init; } = [];

    /// <summary>Files smaller than this were not stored (history/cache only); 0 = complete.</summary>
    public long StoredFileThresholdBytes { get; init; }

    /// <summary>History row id once persisted.</summary>
    public long? HistoryId { get; set; }

    /// <summary>Why the MFT provider was not used, when it was requested.</summary>
    public string? ProviderNote { get; init; }

    public bool IsPartial => Status == ScanStatus.Cancelled || StoredFileThresholdBytes > 0;

    public long TotalBytes => Tree.GetSize(ScanTree.RootIndex);

    public long TotalAllocatedBytes => Tree.GetAllocated(ScanTree.RootIndex);

    public int TotalFiles => Tree.GetFileCount(ScanTree.RootIndex);

    public int TotalFolders => Tree.GetDirectoryCount(ScanTree.RootIndex);

    /// <summary>Capacity used for "% of drive" figures; 0 when unknown.</summary>
    public long DriveCapacityBytes => Drive?.TotalBytes ?? 0;

    public string SourceDescription => Source switch
    {
        ScanSource.Live => Status == ScanStatus.Cancelled ? "Live scan (cancelled — partial results)" : "Live scan",
        ScanSource.Cached => $"Cached result from {StartedUtc.ToLocalTime():g} — may be out of date",
        ScanSource.Historical => $"Historical scan from {StartedUtc.ToLocalTime():g}",
        _ => string.Empty,
    };
}
