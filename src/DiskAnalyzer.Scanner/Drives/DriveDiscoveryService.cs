using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Scanner.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Scanner.Drives;

/// <summary>
/// Enumerates drives. Each drive is queried on its own task with a timeout, because a
/// disconnected network drive can block Win32 volume calls for a long time.
/// </summary>
public sealed class DriveDiscoveryService(ILogger<DriveDiscoveryService> logger) : IDriveDiscoveryService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyList<DriveInfoModel>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Drive enumeration failed");
            return [];
        }

        var tasks = drives.Select(d => QueryWithTimeoutAsync(d, cancellationToken)).ToArray();
        DriveInfoModel[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<DriveInfoModel?> GetDriveForPathAsync(string path, CancellationToken cancellationToken = default)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        try
        {
            return await QueryWithTimeoutAsync(new DriveInfo(root), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null; // UNC share without a drive letter
        }
    }

    private async Task<DriveInfoModel> QueryWithTimeoutAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        DriveKind kind = MapKind(drive.DriveType);
        try
        {
            return await Task.Run(() => Query(drive, kind), cancellationToken).WaitAsync(QueryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Drive {Drive} did not respond within {Timeout}", drive.Name, QueryTimeout);
            return new DriveInfoModel { RootPath = drive.Name, Kind = kind, IsReady = false, VolumeLabel = "Not responding" };
        }
    }

    private DriveInfoModel Query(DriveInfo drive, DriveKind kind)
    {
        try
        {
            if (!drive.IsReady)
            {
                return new DriveInfoModel { RootPath = drive.Name, Kind = kind, IsReady = false };
            }

            uint serial = 0;
            int clusterSize = 4096;
            unsafe
            {
                if (NativeMethods.GetVolumeInformation(drive.Name, null, 0, out serial, out _, out _, null, 0) == false)
                {
                    serial = 0;
                }
            }

            if (NativeMethods.GetDiskFreeSpace(drive.Name, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            {
                clusterSize = (int)(sectorsPerCluster * bytesPerSector);
            }

            (StorageMediaKind media, string? bus) = kind is DriveKind.Fixed or DriveKind.Removable ? QueryStorage(drive.Name) : (StorageMediaKind.Unknown, null);

            return new DriveInfoModel
            {
                RootPath = drive.Name,
                VolumeLabel = SafeGet(() => drive.VolumeLabel) ?? string.Empty,
                Kind = kind,
                FileSystem = SafeGet(() => drive.DriveFormat) ?? string.Empty,
                IsReady = true,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.TotalFreeSpace,
                VolumeSerialNumber = serial,
                ClusterSize = clusterSize,
                Media = media,
                BusType = bus,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not query drive {Drive}", drive.Name);
            return new DriveInfoModel { RootPath = drive.Name, Kind = kind, IsReady = false };
        }
    }

    /// <summary>Queries seek penalty (SSD vs HDD) and bus type. Opens the volume with no access rights, so no elevation is needed.</summary>
    private static unsafe (StorageMediaKind Media, string? Bus) QueryStorage(string root)
    {
        string device = @"\\.\" + root.TrimEnd('\\');
        using SafeFileHandle handle = NativeMethods.CreateFile(device, 0, FileShare.ReadWrite, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return (StorageMediaKind.Unknown, null);
        }

        StorageMediaKind media = StorageMediaKind.Unknown;
        var seekQuery = new NativeMethods.StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
        NativeMethods.DeviceSeekPenaltyDescriptor seek;
        if (NativeMethods.DeviceIoControl(handle, NativeMethods.IoctlStorageQueryProperty, &seekQuery, sizeof(NativeMethods.StoragePropertyQuery), &seek, sizeof(NativeMethods.DeviceSeekPenaltyDescriptor), out _, IntPtr.Zero))
        {
            media = seek.IncursSeekPenalty != 0 ? StorageMediaKind.Hdd : StorageMediaKind.Ssd;
        }

        string? bus = null;
        var deviceQuery = new NativeMethods.StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
        byte* buffer = stackalloc byte[1024];
        if (NativeMethods.DeviceIoControl(handle, NativeMethods.IoctlStorageQueryProperty, &deviceQuery, sizeof(NativeMethods.StoragePropertyQuery), buffer, 1024, out int returned, IntPtr.Zero)
            && returned >= sizeof(NativeMethods.StorageDeviceDescriptor))
        {
            var descriptor = *(NativeMethods.StorageDeviceDescriptor*)buffer;
            bus = descriptor.BusType switch
            {
                1 => "Scsi",
                3 => "Ata",
                4 => "1394",
                7 => "Usb",
                8 => "Raid",
                9 => "iScsi",
                10 => "Sas",
                11 => "Sata",
                12 => "Sd",
                13 => "Mmc",
                14 or 15 => "Virtual",
                16 => "StorageSpaces",
                17 => "NVMe",
                _ => null,
            };
            if (bus is "NVMe" && media == StorageMediaKind.Unknown)
            {
                media = StorageMediaKind.Ssd;
            }
        }

        return (media, bus);
    }

    private static string? SafeGet(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DriveKind MapKind(DriveType type) => type switch
    {
        DriveType.Fixed => DriveKind.Fixed,
        DriveType.Removable => DriveKind.Removable,
        DriveType.Network => DriveKind.Network,
        DriveType.CDRom => DriveKind.CdRom,
        DriveType.Ram => DriveKind.Ram,
        _ => DriveKind.Unknown,
    };
}
