namespace DiskAnalyzer.Core.Models;

/// <summary>Headline numbers for a scan result.</summary>
public sealed record StorageStatistics
{
    public long TotalBytes { get; init; }

    public long TotalAllocatedBytes { get; init; }

    public int FileCount { get; init; }

    public int FolderCount { get; init; }

    public int InaccessibleFolderCount { get; init; }

    public int ReparsePointCount { get; init; }

    public int HardLinkDuplicateCount { get; init; }

    public int LargestFileIndex { get; init; } = ScanTree.None;

    public int LargestFolderIndex { get; init; } = ScanTree.None;

    public int ExtensionCount { get; init; }
}

public sealed record ExtensionStatistic(string Extension, FileCategory Category, long TotalBytes, long FileCount)
{
    public string DisplayName => string.IsNullOrEmpty(Extension) ? "(no extension)" : Extension;
}

public sealed record CategoryStatistic(FileCategory Category, long TotalBytes, long FileCount);

/// <summary>Criteria for searching indexed scan results.</summary>
public sealed record SearchQuery
{
    /// <summary>Substring to match against names (case-insensitive). Supports <c>*</c>/<c>?</c> wildcards.</summary>
    public string? Text { get; init; }

    /// <summary>Restrict to these extensions (with leading dot).</summary>
    public IReadOnlyCollection<string>? Extensions { get; init; }

    public FileCategory? Category { get; init; }

    public long MinSizeBytes { get; init; }

    public bool IncludeFiles { get; init; } = true;

    public bool IncludeFolders { get; init; } = true;

    /// <summary>Only search under this node (default: whole tree).</summary>
    public int ScopeNode { get; init; } = ScanTree.RootIndex;

    public int MaxResults { get; init; } = 5000;
}

public sealed record SearchResult(IReadOnlyList<int> Nodes, int TotalMatches, TimeSpan Elapsed)
{
    public bool IsTruncated => TotalMatches > Nodes.Count;
}

public sealed record DuplicateGroup(long SizeBytes, string Hash, IReadOnlyList<int> Nodes)
{
    /// <summary>Bytes that could be reclaimed by keeping one copy.</summary>
    public long WastedBytes => SizeBytes * (Nodes.Count - 1);
}
