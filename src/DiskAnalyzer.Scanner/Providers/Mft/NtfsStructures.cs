using System.Buffers.Binary;

namespace DiskAnalyzer.Scanner.Providers.Mft;

/// <summary>Fields of the NTFS boot sector needed to locate the MFT.</summary>
internal readonly record struct NtfsBootSector(int BytesPerSector, int BytesPerCluster, long MftLcn, int RecordSize)
{
    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || !sector.Slice(3, 8).SequenceEqual("NTFS    "u8))
        {
            throw new InvalidDataException("Not an NTFS boot sector.");
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        int rawSectorsPerCluster = sector[0x0D];
        int sectorsPerCluster = rawSectorsPerCluster <= 0x80 ? rawSectorsPerCluster : 1 << (256 - rawSectorsPerCluster);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        sbyte clustersPerRecord = unchecked((sbyte)sector[0x40]);
        int bytesPerCluster = bytesPerSector * sectorsPerCluster;
        int recordSize = clustersPerRecord > 0 ? clustersPerRecord * bytesPerCluster : 1 << -clustersPerRecord;

        if (bytesPerSector is < 256 or > 4096 || bytesPerCluster <= 0 || recordSize is < 256 or > 65536 || mftLcn <= 0)
        {
            throw new InvalidDataException("Unexpected NTFS geometry.");
        }

        return new NtfsBootSector(bytesPerSector, bytesPerCluster, mftLcn, recordSize);
    }
}

/// <summary>A contiguous run of clusters of a non-resident attribute.</summary>
internal readonly record struct DataRun(long Lcn, long ClusterCount, bool IsSparse);

internal static class NtfsRecord
{
    public const uint SignatureFile = 0x454C4946; // "FILE"
    public const uint AttributeEnd = 0xFFFFFFFF;
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint Data = 0x80;
    public const uint ReparsePoint = 0xC0;

    public const ushort FlagInUse = 0x0001;
    public const ushort FlagDirectory = 0x0002;

    /// <summary>Converts a FILETIME (100 ns since 1601) into <see cref="DateTime"/> ticks.</summary>
    public static long FileTimeToTicks(long fileTime) =>
        fileTime > 0 && fileTime < DateTime.MaxValue.Ticks - 504911232000000000L ? fileTime + 504911232000000000L : 0;

    /// <summary>
    /// Applies the update sequence array ("fixup") that NTFS uses to detect torn writes.
    /// Returns false if the record is corrupt.
    /// </summary>
    public static bool ApplyFixup(Span<byte> record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
        if (usaCount < 2 || usaOffset + (usaCount * 2) > record.Length)
        {
            return false;
        }

        int stride = record.Length / (usaCount - 1);
        if (stride < 256)
        {
            return false;
        }

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int position = (i * stride) - 2;
            if (position + 2 > record.Length)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(record[position..]) != usn)
            {
                return false;
            }

            record[position] = record[usaOffset + (i * 2)];
            record[position + 1] = record[usaOffset + (i * 2) + 1];
        }

        return true;
    }

    /// <summary>
    /// Clusters actually allocated by a run list: sparse runs (no LCN) are excluded. This is exact for sparse,
    /// compressed and fragmented streams and does not depend on attribute flags (e.g. $BadClus:$Bad spans the
    /// whole volume but is almost entirely sparse). Returns -1 for a malformed run list.
    /// </summary>
    public static long CountAllocatedClusters(ReadOnlySpan<byte> runs)
    {
        long clusters = 0;
        int pos = 0;
        while (pos < runs.Length)
        {
            byte header = runs[pos++];
            if (header == 0)
            {
                return clusters;
            }

            int lengthSize = header & 0x0F;
            int offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > runs.Length)
            {
                return -1;
            }

            long length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length |= (long)runs[pos + i] << (8 * i);
            }

            if (offsetSize != 0)
            {
                clusters += length;
            }

            pos += lengthSize + offsetSize;
        }

        return clusters;
    }

    /// <summary>Decodes an NTFS mapping-pairs (run list) array.</summary>
    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<DataRun>();
        long lcn = 0;
        int pos = 0;
        while (pos < runs.Length)
        {
            byte header = runs[pos++];
            if (header == 0)
            {
                break;
            }

            int lengthSize = header & 0x0F;
            int offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > runs.Length)
            {
                throw new InvalidDataException("Corrupt run list.");
            }

            long length = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                length |= (long)runs[pos + i] << (8 * i);
            }

            pos += lengthSize;
            if (offsetSize == 0)
            {
                result.Add(new DataRun(0, length, IsSparse: true));
                continue;
            }

            long offset = 0;
            for (int i = 0; i < offsetSize; i++)
            {
                offset |= (long)runs[pos + i] << (8 * i);
            }

            // Sign-extend.
            if ((runs[pos + offsetSize - 1] & 0x80) != 0)
            {
                offset |= -1L << (8 * offsetSize);
            }

            pos += offsetSize;
            lcn += offset;
            result.Add(new DataRun(lcn, length, IsSparse: false));
        }

        return result;
    }
}
