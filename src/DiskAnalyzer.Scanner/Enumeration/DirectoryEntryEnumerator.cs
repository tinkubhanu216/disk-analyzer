using System.IO.Enumeration;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Scanner.Enumeration;

/// <summary>One directory entry, captured without allocating FileInfo/DirectoryInfo.</summary>
internal struct EntryData
{
    public string Name;
    public long Length;
    public long Allocated;
    public long LastWriteUtcTicks;
    public FileAttributes Attributes;
    public NodeFlags Flags;

    public readonly bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

/// <summary>
/// Streams the immediate children of a single directory using the low-level
/// <see cref="FileSystemEnumerator{TResult}"/> (FindFirstFileEx/NtQueryDirectoryFile with a large buffer).
/// Errors are recorded instead of thrown so one inaccessible folder never aborts the scan.
/// </summary>
internal sealed class DirectoryEntryEnumerator : FileSystemEnumerator<EntryData>
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,
    };

    private readonly bool _includeHidden;
    private readonly bool _includeSystem;

    public DirectoryEntryEnumerator(string directory, bool includeHidden, bool includeSystem)
        : base(directory, Options)
    {
        _includeHidden = includeHidden;
        _includeSystem = includeSystem;
    }

    /// <summary>Win32 error code of the last failure (0 = none).</summary>
    public int LastError { get; private set; }

    /// <summary>True when the directory itself could not be opened.</summary>
    public bool FailedToOpen { get; private set; }

    private bool _yieldedAny;

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        FileAttributes attributes = entry.Attributes;
        if (!_includeHidden && (attributes & FileAttributes.Hidden) != 0)
        {
            return false;
        }

        return _includeSystem || (attributes & FileAttributes.System) == 0;
    }

    protected override EntryData TransformEntry(ref FileSystemEntry entry)
    {
        _yieldedAny = true;
        FileAttributes attributes = entry.Attributes;
        NodeFlags flags = entry.IsDirectory ? NodeFlags.Directory : NodeFlags.None;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            flags |= NodeFlags.ReparsePoint;
        }

        return new EntryData
        {
            Name = entry.FileName.ToString(),
            Length = entry.IsDirectory ? 0 : entry.Length,
            LastWriteUtcTicks = entry.LastWriteTimeUtc.UtcTicks,
            Attributes = attributes,
            Flags = flags,
        };
    }

    protected override bool ContinueOnError(int error)
    {
        LastError = error;
        if (!_yieldedAny)
        {
            FailedToOpen = true;
        }

        return true;
    }
}
