namespace DiskAnalyzer.Core.Models;

/// <summary>Materialized view of a file node, created only for rows that are displayed or exported.</summary>
public sealed record FileEntry
{
    public required int NodeIndex { get; init; }

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required string ParentDirectory { get; init; }

    public long SizeBytes { get; init; }

    public long AllocatedBytes { get; init; }

    public DateTime? LastWriteUtc { get; init; }

    public string Extension { get; init; } = string.Empty;

    public FileCategory Category { get; init; }

    public FileAttributes Attributes { get; init; }

    public NodeFlags Flags { get; init; }

    public static FileEntry FromTree(ScanTree tree, int index) => new()
    {
        NodeIndex = index,
        Name = tree.GetName(index),
        FullPath = tree.GetFullPath(index),
        ParentDirectory = tree.GetParentPath(index),
        SizeBytes = tree.GetSize(index),
        AllocatedBytes = tree.GetAllocated(index),
        LastWriteUtc = tree.GetLastWriteUtc(index),
        Extension = tree.GetExtension(index),
        Category = FileCategoryClassifier.Classify(tree.GetExtension(index)),
        Attributes = tree.GetAttributes(index),
        Flags = tree.GetFlags(index),
    };
}

/// <summary>Materialized view of a folder node.</summary>
public sealed record FolderEntry
{
    public required int NodeIndex { get; init; }

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required string ParentDirectory { get; init; }

    public long SizeBytes { get; init; }

    public long AllocatedBytes { get; init; }

    public int FileCount { get; init; }

    public int FolderCount { get; init; }

    public DateTime? LastWriteUtc { get; init; }

    public NodeFlags Flags { get; init; }

    public static FolderEntry FromTree(ScanTree tree, int index) => new()
    {
        NodeIndex = index,
        Name = tree.GetName(index),
        FullPath = tree.GetFullPath(index),
        ParentDirectory = tree.GetParentPath(index),
        SizeBytes = tree.GetSize(index),
        AllocatedBytes = tree.GetAllocated(index),
        FileCount = tree.GetFileCount(index),
        FolderCount = tree.GetDirectoryCount(index),
        LastWriteUtc = tree.GetLastWriteUtc(index),
        Flags = tree.GetFlags(index),
    };
}
