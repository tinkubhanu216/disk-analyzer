using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Scanner.Interop;

/// <summary>Win32 declarations. Every handle opened here is read-only (attributes or data read access).</summary>
internal static partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint OpenExisting = 3;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const uint InvalidFileSize = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize, void* outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, int fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathSize, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;

        public readonly ulong FileIndex => ((ulong)FileIndexHigh << 32) | FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StorageDeviceDescriptor
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        public byte RemovableMedia;
        public byte CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
    }

    /// <summary>Opens a file or directory for attribute reads only, without following reparse points.</summary>
    internal static SafeFileHandle OpenForAttributes(string path, bool followReparsePoint = false) =>
        CreateFile(path, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | (followReparsePoint ? 0 : FileFlagOpenReparsePoint), IntPtr.Zero);

    /// <summary>Physical size of compressed, sparse or cloud-placeholder files; null on failure.</summary>
    internal static long? TryGetCompressedFileSize(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    internal static unsafe string? TryGetFinalPath(SafeFileHandle handle)
    {
        const int capacity = 32768;
        char[] buffer = new char[capacity];
        fixed (char* p = buffer)
        {
            uint length = GetFinalPathNameByHandle(handle, p, capacity, 0);
            if (length == 0 || length >= capacity)
            {
                return null;
            }

            string path = new(p, 0, (int)length);
            return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
                : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
                : path;
        }
    }
}
