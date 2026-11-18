namespace DiskAnalyzer.Core.Models;

/// <summary>Options that control a single scan.</summary>
public sealed record ScanConfiguration
{
    /// <summary>Folder or drive root to scan.</summary>
    public required string RootPath { get; init; }

    /// <summary>Drive that contains <see cref="RootPath"/>, when known.</summary>
    public DriveInfoModel? Drive { get; init; }

    /// <summary>Descend into directory junctions/symlinks. Off by default; a loop guard is applied when on.</summary>
    public bool FollowReparsePoints { get; init; }

    public bool IncludeHidden { get; init; } = true;

    public bool IncludeSystem { get; init; } = true;

    /// <summary>Count each hard-linked file once (standard provider: opens each file for attributes). The MFT provider always deduplicates.</summary>
    public bool DeduplicateHardLinks { get; init; }

    /// <summary>Use the NTFS MFT reader for whole-volume scans when the process is elevated.</summary>
    public bool UseMftWhenAvailable { get; init; } = true;

    /// <summary>Worker count; 0 = choose automatically from CPU count and drive type.</summary>
    public int MaxConcurrency { get; init; }

    /// <summary>True when the scan root is the root of a volume.</summary>
    public bool IsDriveRoot =>
        Drive is not null && string.Equals(Path.TrimEndingDirectorySeparator(RootPath), Path.TrimEndingDirectorySeparator(Drive.RootPath), StringComparison.OrdinalIgnoreCase);

    public int ResolveConcurrency()
    {
        if (MaxConcurrency > 0)
        {
            return Math.Clamp(MaxConcurrency, 1, 64);
        }

        int cpu = Environment.ProcessorCount;
        return Drive?.Kind switch
        {
            DriveKind.Network => Math.Clamp(cpu, 4, 8),
            DriveKind.Removable or DriveKind.CdRom => 2,
            _ when Drive?.Media == StorageMediaKind.Hdd => 4,
            _ => Math.Clamp(cpu, 4, 16),
        };
    }
}
