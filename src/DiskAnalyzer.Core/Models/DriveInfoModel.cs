namespace DiskAnalyzer.Core.Models;

public enum DriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
    CdRom,
    Ram,
}

public enum StorageMediaKind
{
    Unknown,
    Ssd,
    Hdd,
}

/// <summary>Snapshot of a drive as reported by Windows at discovery time.</summary>
public sealed record DriveInfoModel
{
    /// <summary>Root path, e.g. <c>C:\</c>.</summary>
    public required string RootPath { get; init; }

    public string Letter => RootPath.Length >= 2 ? RootPath[..2] : RootPath;

    public string VolumeLabel { get; init; } = string.Empty;

    public DriveKind Kind { get; init; }

    public StorageMediaKind Media { get; init; }

    /// <summary>Bus type reported by the storage driver (USB, NVMe, SATA…), when available.</summary>
    public string? BusType { get; init; }

    public string FileSystem { get; init; } = string.Empty;

    public bool IsReady { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>Utilization in percent (0–100).</summary>
    public double UtilizationPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;

    public uint VolumeSerialNumber { get; init; }

    public int ClusterSize { get; init; }

    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable identifier used to match scan history to a volume.</summary>
    public string DriveId => $"{Letter.TrimEnd(':')}:{VolumeSerialNumber:X8}";

    public bool IsExternal => Kind == DriveKind.Removable || string.Equals(BusType, "Usb", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel) ? $"Local Disk ({Letter})" : $"{VolumeLabel} ({Letter})";

    public string KindDescription => Kind switch
    {
        DriveKind.Fixed when IsExternal => "External drive",
        DriveKind.Fixed => Media switch
        {
            StorageMediaKind.Ssd => "SSD",
            StorageMediaKind.Hdd => "Hard disk",
            _ => "Local disk",
        },
        DriveKind.Removable => "Removable drive",
        DriveKind.Network => "Network drive",
        DriveKind.CdRom => "Optical drive",
        DriveKind.Ram => "RAM disk",
        _ => "Drive",
    };
}
