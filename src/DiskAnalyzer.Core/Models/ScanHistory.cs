namespace DiskAnalyzer.Core.Models;

/// <summary>One row of persisted scan history.</summary>
public sealed record ScanHistory
{
    public long ScanId { get; init; }

    public string DriveId { get; init; } = string.Empty;

    public string DriveName { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public DateTime ScannedUtc { get; init; }

    public long TotalBytes { get; init; }

    public long TotalAllocatedBytes { get; init; }

    public int FileCount { get; init; }

    public int FolderCount { get; init; }

    public TimeSpan Duration { get; init; }

    public ScanStatus Status { get; init; }

    public string ProviderName { get; init; } = string.Empty;

    public long ErrorCount { get; init; }

    public long DriveCapacityBytes { get; init; }

    public long DriveFreeBytes { get; init; }

    public long StoredFileThresholdBytes { get; init; }

    public int StoredNodeCount { get; init; }
}

public enum ChangeKind
{
    Added,
    Removed,
    Grown,
    Shrunk,
}

public sealed record ChangedItem(string Path, bool IsDirectory, ChangeKind Kind, long OldSizeBytes, long NewSizeBytes)
{
    public long DeltaBytes => NewSizeBytes - OldSizeBytes;
}

/// <summary>Differences between two scans of the same root.</summary>
public sealed record ScanComparison
{
    public required ScanHistory Older { get; init; }

    public required ScanHistory Newer { get; init; }

    public IReadOnlyList<ChangedItem> AddedFiles { get; init; } = [];

    public IReadOnlyList<ChangedItem> RemovedFiles { get; init; } = [];

    public IReadOnlyList<ChangedItem> GrownFolders { get; init; } = [];

    public IReadOnlyList<ChangedItem> ShrunkFolders { get; init; } = [];

    public IReadOnlyList<ChangedItem> AddedFolders { get; init; } = [];

    public IReadOnlyList<ChangedItem> RemovedFolders { get; init; } = [];

    public int TotalAddedFiles { get; init; }

    public int TotalRemovedFiles { get; init; }

    public long AddedFileBytes { get; init; }

    public long RemovedFileBytes { get; init; }

    /// <summary>Files below this size were not stored in one of the scans, so they are not compared.</summary>
    public long ComparedFileThresholdBytes { get; init; }

    public long TotalDeltaBytes => Newer.TotalBytes - Older.TotalBytes;
}
