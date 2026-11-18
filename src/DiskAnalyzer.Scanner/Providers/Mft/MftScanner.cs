using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Threading;
using DiskAnalyzer.Scanner.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Scanner.Providers.Mft;

/// <summary>
/// Optional provider that reads the NTFS Master File Table directly (like WizTree).
/// Requires an elevated process; never requests elevation itself. The volume is opened
/// with GENERIC_READ only — nothing is ever written.
/// <para>
/// Correctness: hard links (several $FILE_NAME attributes in one record) are counted once;
/// DOS 8.3 aliases are ignored; reparse points cannot cause double counting or loops because
/// the MFT is flat and records are linked only by their real parent reference.
/// </para>
/// </summary>
public sealed class MftScanner(ILogger<MftScanner> logger) : IFileSystemScanner
{
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const int RootRecord = 5;

    public string Name => "NTFS MFT reader";

    public int Priority => 100;

    public static bool IsProcessElevated => Environment.IsPrivilegedProcess;

    public ScannerAvailability CheckAvailability(ScanConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.UseMftWhenAvailable)
        {
            return ScannerAvailability.Unavailable("MFT scanning is turned off in Settings.");
        }

        if (configuration.Drive is null || !configuration.IsDriveRoot)
        {
            return ScannerAvailability.Unavailable("The MFT reader scans whole volumes only.");
        }

        if (configuration.Drive.Kind is not (DriveKind.Fixed or DriveKind.Removable))
        {
            return ScannerAvailability.Unavailable("The MFT reader needs a local volume.");
        }

        if (!configuration.Drive.IsNtfs)
        {
            return ScannerAvailability.Unavailable($"{configuration.Drive.Letter} is {configuration.Drive.FileSystem}, not NTFS.");
        }

        if (configuration.FollowReparsePoints)
        {
            return ScannerAvailability.Unavailable("Following junctions/symlinks is only supported by standard enumeration.");
        }

        if (!IsProcessElevated)
        {
            return ScannerAvailability.Unavailable("Reading the MFT requires running as administrator.");
        }

        return ScannerAvailability.Available;
    }

    public Task<ScanTree> ScanAsync(ScanConfiguration configuration, ScanProgressTracker progress, AsyncPauseGate pauseGate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Task.Factory.StartNew(() => Scan(configuration, progress, pauseGate, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private ScanTree Scan(ScanConfiguration config, ScanProgressTracker progress, AsyncPauseGate pause, CancellationToken ct)
    {
        string root = config.Drive!.RootPath;
        string device = @"\\.\" + root.TrimEnd('\\');
        logger.LogInformation("MFT scan of {Device}", device);

        using SafeFileHandle handle = NativeMethods.CreateFile(device, NativeMethods.GenericRead, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open volume {device} (Win32 error {error}).");
        }

        using var volume = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        progress.Phase = "Reading MFT";
        progress.CurrentPath = $"{root} (Master File Table)";

        byte[] bootBuffer = new byte[4096];
        ReadAt(volume, 0, bootBuffer);
        NtfsBootSector boot = NtfsBootSector.Parse(bootBuffer);

        // Record 0 describes the MFT itself: its $DATA run list gives the MFT's (possibly fragmented) location.
        byte[] record0 = new byte[AlignUp(boot.RecordSize, 4096)];
        ReadAt(volume, boot.MftLcn * boot.BytesPerCluster, record0);
        Span<byte> rec0 = record0.AsSpan(0, boot.RecordSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(rec0) != NtfsRecord.SignatureFile || !NtfsRecord.ApplyFixup(rec0))
        {
            throw new InvalidDataException("MFT record 0 is invalid.");
        }

        (List<DataRun> runs, long mftDataSize) = ReadMftDataRuns(rec0);
        long totalRecordsLong = mftDataSize / boot.RecordSize;
        if (totalRecordsLong is <= RootRecord or > int.MaxValue / 2)
        {
            throw new InvalidDataException($"Unexpected MFT record count {totalRecordsLong}.");
        }

        int totalRecords = (int)totalRecordsLong;
        progress.SetWorkTotal(totalRecords);
        var data = new MftData(totalRecords);

        ReadAllRecords(volume, boot, runs, data, progress, pause, ct);
        if (ct.IsCancellationRequested)
        {
            var partial = new ScanTree(root);
            partial.AddFlags(ScanTree.RootIndex, NodeFlags.Partial);
            return partial;
        }

        progress.Phase = "Building folder tree";
        return BuildTree(root, data, config, progress, ct);
    }

    private static (List<DataRun> Runs, long DataSize) ReadMftDataRuns(ReadOnlySpan<byte> record)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == NtfsRecord.AttributeEnd)
            {
                break;
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length <= 0 || offset + length > record.Length)
            {
                break;
            }

            if (type == NtfsRecord.Data && record[offset + 8] != 0 && record[offset + 9] == 0)
            {
                int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(offset + 32)..]);
                long dataSize = BinaryPrimitives.ReadInt64LittleEndian(record[(offset + 48)..]);
                return (NtfsRecord.DecodeRuns(record.Slice(offset + runOffset, length - runOffset)), dataSize);
            }

            offset += length;
        }

        throw new NotSupportedException("The MFT's $DATA attribute is stored in an attribute list (extremely fragmented MFT).");
    }

    /// <summary>Streams the MFT in large chunks, following its run list, and parses every record.</summary>
    private void ReadAllRecords(FileStream volume, NtfsBootSector boot, List<DataRun> runs, MftData data, ScanProgressTracker progress, AsyncPauseGate pause, CancellationToken ct)
    {
        int recordSize = boot.RecordSize;
        int chunk = Math.Max(recordSize, ChunkBytes / recordSize * recordSize);
        byte[] buffer = new byte[chunk + boot.BytesPerCluster + 4096];
        long totalBytes = (long)data.Count * recordSize;

        for (long virtualOffset = 0; virtualOffset < totalBytes; virtualOffset += chunk)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            pause.WaitIfPaused(ct);
            int length = (int)Math.Min(chunk, totalBytes - virtualOffset);
            ReadVirtual(volume, boot, runs, virtualOffset, buffer, length);

            int firstRecord = (int)(virtualOffset / recordSize);
            int records = length / recordSize;
            long files = 0, folders = 0, bytes = 0;
            for (int r = 0; r < records; r++)
            {
                Span<byte> record = buffer.AsSpan(r * recordSize, recordSize);
                ParseRecord(record, firstRecord + r, boot.BytesPerCluster, data, ref files, ref folders, ref bytes);
            }

            progress.AddWorkDone(records);
            progress.AddFiles(files, bytes);
            progress.AddFolders(folders);
        }

        logger.LogInformation("Parsed {Records:N0} MFT records", data.Count);
    }

    /// <summary>Reads <paramref name="length"/> bytes of the MFT stream starting at a virtual (stream) offset.</summary>
    private static void ReadVirtual(FileStream volume, NtfsBootSector boot, List<DataRun> runs, long virtualOffset, byte[] buffer, int length)
    {
        long clusterSize = boot.BytesPerCluster;
        long runStart = 0;
        int written = 0;
        foreach (DataRun run in runs)
        {
            long runBytes = run.ClusterCount * clusterSize;
            long runEnd = runStart + runBytes;
            long want = virtualOffset + written;
            if (want < runEnd && written < length)
            {
                long within = want - runStart;
                int take = (int)Math.Min(length - written, runEnd - want);
                if (run.IsSparse)
                {
                    Array.Clear(buffer, written, take);
                }
                else
                {
                    // Volume reads must be sector-aligned in size; the run is whole clusters, so rounding up stays inside it.
                    int readLength = (int)Math.Min(AlignUp(take, boot.BytesPerSector), runBytes - within);
                    ReadAt(volume, (run.Lcn * clusterSize) + within, buffer.AsSpan(written, readLength));
                }

                written += take;
                if (written >= length)
                {
                    return;
                }
            }

            runStart = runEnd;
        }

        if (written < length)
        {
            Array.Clear(buffer, written, length - written);
        }
    }

    private static void ParseRecord(Span<byte> record, int recordNumber, long clusterSize, MftData data, ref long files, ref long folders, ref long bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != NtfsRecord.SignatureFile)
        {
            return;
        }

        ushort headerFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((headerFlags & NtfsRecord.FlagInUse) == 0 || !NtfsRecord.ApplyFixup(record))
        {
            return;
        }

        long baseReference = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]) & 0x0000FFFFFFFFFFFF;
        bool isBase = baseReference == 0;
        int target = isBase ? recordNumber : (int)baseReference;
        if ((uint)target >= (uint)data.Count)
        {
            return;
        }

        if (isBase)
        {
            data.Sequence[recordNumber] = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
            data.State[recordNumber] |= MftData.InUse;
            if ((headerFlags & NtfsRecord.FlagDirectory) != 0)
            {
                data.State[recordNumber] |= MftData.IsDirectory;
                folders++;
            }
            else
            {
                files++;
            }
        }

        int bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset + 16 <= bytesInUse)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == NtfsRecord.AttributeEnd)
            {
                break;
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > bytesInUse)
            {
                break;
            }

            ReadOnlySpan<byte> attribute = record.Slice(offset, length);
            bool nonResident = attribute[8] != 0;
            int nameLength = attribute[9];

            switch (type)
            {
                case NtfsRecord.StandardInformation when !nonResident:
                {
                    ReadOnlySpan<byte> value = ResidentValue(attribute);
                    if (value.Length >= 36)
                    {
                        data.Modified[target] = NtfsRecord.FileTimeToTicks(BinaryPrimitives.ReadInt64LittleEndian(value[8..]));
                        data.Attributes[target] = (int)BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                    }

                    break;
                }

                case NtfsRecord.FileName when !nonResident:
                {
                    ReadOnlySpan<byte> value = ResidentValue(attribute);
                    if (value.Length < 66)
                    {
                        break;
                    }

                    byte nameSpace = value[65];
                    if (nameSpace == 2)
                    {
                        break; // DOS 8.3 alias of a name we already record
                    }

                    int nameChars = value[64];
                    if (66 + (nameChars * 2) > value.Length)
                    {
                        break;
                    }

                    long parentRef = BinaryPrimitives.ReadInt64LittleEndian(value);
                    int parent = (int)(parentRef & 0x0000FFFFFFFFFFFF);
                    ushort parentSeq = (ushort)((ulong)parentRef >> 48);
                    string name = Encoding.Unicode.GetString(value.Slice(66, nameChars * 2));
                    data.AddName(target, parent, parentSeq, name);
                    break;
                }

                case NtfsRecord.Data:
                {
                    long allocated = 0; // resident data lives inside the MFT record
                    long? size = null;
                    if (nonResident)
                    {
                        if (attribute.Length < 64)
                        {
                            break;
                        }

                        // Every extent contributes the clusters its runs really occupy (sparse runs excluded).
                        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
                        long clusters = runOffset < attribute.Length ? NtfsRecord.CountAllocatedClusters(attribute[runOffset..]) : -1;
                        allocated = clusters > 0 ? clusters * clusterSize : 0;

                        // Only the first extent (VCN 0) carries the stream's logical size.
                        if (BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]) == 0)
                        {
                            size = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                        }
                    }
                    else
                    {
                        size = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                    }

                    if (nameLength == 0 && size is { } logical)
                    {
                        data.Size[target] = logical;
                        bytes += logical;
                    }

                    // Named streams (ADS, $BadClus:$Bad, $UsnJrnl:$J…) occupy disk space but are not part of the logical file size.
                    data.Allocated[target] += Math.Max(0, allocated);
                    break;
                }

                case NtfsRecord.ReparsePoint:
                    data.State[target] |= MftData.HasReparsePoint;
                    break;
            }

            offset += length;
        }
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
    {
        int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        return valueOffset + valueLength <= attribute.Length ? attribute.Slice(valueOffset, valueLength) : [];
    }

    /// <summary>Builds the <see cref="ScanTree"/> breadth-first from the root so parents always precede children.</summary>
    private ScanTree BuildTree(string rootPath, MftData data, ScanConfiguration config, ScanProgressTracker progress, CancellationToken ct)
    {
        int n = data.Count;
        var links = data.CollectLinks();

        // Validate parents and group links by parent (CSR layout).
        int[] childStart = new int[n + 1];
        var orphanLinks = new List<int>();
        bool[] valid = new bool[links.Count];
        for (int l = 0; l < links.Count; l++)
        {
            (int child, int parent, ushort parentSeq, _) = links[l];
            if (child == RootRecord)
            {
                continue;
            }

            bool parentOk = (uint)parent < (uint)n
                && (data.State[parent] & (MftData.InUse | MftData.IsDirectory)) == (MftData.InUse | MftData.IsDirectory)
                && (parentSeq == 0 || data.Sequence[parent] == parentSeq);
            if (parentOk)
            {
                valid[l] = true;
                childStart[parent + 1]++;
            }
            else
            {
                orphanLinks.Add(l);
            }
        }

        for (int i = 0; i < n; i++)
        {
            childStart[i + 1] += childStart[i];
        }

        int[] childLinks = new int[childStart[n]];
        int[] fill = (int[])childStart.Clone();
        for (int l = 0; l < links.Count; l++)
        {
            if (valid[l])
            {
                childLinks[fill[links[l].Parent]++] = l;
            }
        }

        var tree = new ScanTree(rootPath, data.Modified[RootRecord], (FileAttributes)data.Attributes[RootRecord] | FileAttributes.Directory);
        bool[] counted = new bool[n];
        bool[] expanded = new bool[n];
        var queue = new Queue<(int Record, int Node)>();
        queue.Enqueue((RootRecord, ScanTree.RootIndex));
        expanded[RootRecord] = true;

        void AddLink(int linkIndex, int parentNode)
        {
            (int child, _, _, string name) = links[linkIndex];
            var attributes = (FileAttributes)data.Attributes[child];
            if ((!config.IncludeHidden && (attributes & FileAttributes.Hidden) != 0) || (!config.IncludeSystem && (attributes & FileAttributes.System) != 0))
            {
                return;
            }

            NodeFlags flags = (data.State[child] & MftData.HasReparsePoint) != 0 || (attributes & FileAttributes.ReparsePoint) != 0
                ? NodeFlags.ReparsePoint
                : NodeFlags.None;

            if ((data.State[child] & MftData.IsDirectory) != 0)
            {
                int node = tree.AddDirectory(parentNode, name, data.Modified[child], attributes | FileAttributes.Directory, flags);
                if (!expanded[child])
                {
                    expanded[child] = true;
                    queue.Enqueue((child, node));
                }
            }
            else
            {
                if (counted[child])
                {
                    flags |= NodeFlags.HardLinkDuplicate;
                }

                counted[child] = true;
                tree.AddFile(parentNode, name, data.Size[child], data.Allocated[child], data.Modified[child], attributes, flags);
            }
        }

        void Drain()
        {
            int processed = 0;
            while (queue.Count > 0)
            {
                if ((++processed & 0x3FFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                (int record, int node) = queue.Dequeue();
                for (int k = childStart[record]; k < childStart[record + 1]; k++)
                {
                    AddLink(childLinks[k], node);
                }
            }
        }

        Drain();

        // Records whose parent no longer exists (rare: pending deletes, corruption) still use space.
        var unreachable = orphanLinks.Where(l => links[l].Child != RootRecord && !counted[links[l].Child] && !expanded[links[l].Child]).ToList();
        if (unreachable.Count > 0)
        {
            int orphanFolder = tree.AddDirectory(ScanTree.RootIndex, "[Unlinked MFT records]", 0, FileAttributes.Directory, NodeFlags.Synthetic);
            foreach (int l in unreachable)
            {
                AddLink(l, orphanFolder);
            }

            Drain();
            logger.LogWarning("{Count} MFT records had no valid parent and were grouped under a synthetic folder", unreachable.Count);
        }

        progress.Phase = "Finishing";
        return tree;
    }

    private static void ReadAt(FileStream volume, long offset, Span<byte> buffer)
    {
        volume.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int read = volume.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of volume at offset {offset + total}.");
            }

            total += read;
        }
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>Per-record columns collected while streaming the MFT.</summary>
    private sealed class MftData(int count)
    {
        public const byte InUse = 1;
        public const byte IsDirectory = 2;
        public const byte HasReparsePoint = 4;

        public int Count { get; } = count;

        public ushort[] Sequence { get; } = new ushort[count];

        public byte[] State { get; } = new byte[count];

        public long[] Size { get; } = new long[count];

        public long[] Allocated { get; } = new long[count];

        public long[] Modified { get; } = new long[count];

        public int[] Attributes { get; } = new int[count];

        private readonly int[] _firstParent = new int[count];
        private readonly ushort[] _firstParentSeq = new ushort[count];
        private readonly string?[] _firstName = new string?[count];
        private readonly List<(int Child, int Parent, ushort ParentSeq, string Name)> _extraLinks = [];

        public void AddName(int record, int parent, ushort parentSeq, string name)
        {
            if (_firstName[record] is null)
            {
                _firstName[record] = name;
                _firstParent[record] = parent;
                _firstParentSeq[record] = parentSeq;
            }
            else if (!(_firstParent[record] == parent && string.Equals(_firstName[record], name, StringComparison.Ordinal)))
            {
                _extraLinks.Add((record, parent, parentSeq, name)); // hard link
            }
        }

        public List<(int Child, int Parent, ushort ParentSeq, string Name)> CollectLinks()
        {
            var links = new List<(int, int, ushort, string)>(Count + _extraLinks.Count);
            for (int r = 0; r < Count; r++)
            {
                if ((State[r] & InUse) != 0 && _firstName[r] is { } name)
                {
                    links.Add((r, _firstParent[r], _firstParentSeq[r], name));
                }
            }

            foreach (var extra in _extraLinks)
            {
                if ((State[extra.Child] & InUse) != 0)
                {
                    links.Add(extra);
                }
            }

            return links;
        }
    }
}
