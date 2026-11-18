using System.Buffers;
using System.Security.Cryptography;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// Finds files with identical content. Read-only: files are opened for reading with full sharing.
/// Stage 1 groups by size (free), stage 2 hashes the first and last 64 KB, stage 3 hashes full content
/// only for files that still collide.
/// </summary>
public sealed class DuplicateFileFinder(ILogger<DuplicateFileFinder>? logger = null) : IDuplicateFinder
{
    private const int PartialBlock = 64 * 1024;
    private readonly ILogger _logger = logger ?? NullLogger<DuplicateFileFinder>.Instance;

    public Task<IReadOnlyList<DuplicateGroup>> FindAsync(ScanTree tree, long minSizeBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return Task.Run<IReadOnlyList<DuplicateGroup>>(() => Find(tree, Math.Max(1, minSizeBytes), progress, cancellationToken), cancellationToken);
    }

    private List<DuplicateGroup> Find(ScanTree tree, long minSize, IProgress<double>? progress, CancellationToken ct)
    {
        // Stage 1: group by exact size. Hard-link duplicates share storage, so they are not duplicates.
        var bySize = new Dictionary<long, List<int>>();
        for (int i = 1; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i) || (tree.GetFlags(i) & (NodeFlags.HardLinkDuplicate | NodeFlags.ReparsePoint)) != 0)
            {
                continue;
            }

            long size = tree.GetSize(i);
            if (size < minSize)
            {
                continue;
            }

            if (!bySize.TryGetValue(size, out List<int>? list))
            {
                bySize[size] = list = [];
            }

            list.Add(i);
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int totalFiles = candidates.Sum(kv => kv.Value.Count);
        int processed = 0;
        var groups = new List<DuplicateGroup>();

        foreach (var (size, nodes) in candidates.OrderByDescending(kv => kv.Key))
        {
            ct.ThrowIfCancellationRequested();

            // Stage 2: partial hash.
            var partial = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int node in nodes)
            {
                string? hash = TryHash(tree.GetFullPath(node), size, partialOnly: true, ct);
                processed++;
                if (hash is null)
                {
                    continue;
                }

                if (!partial.TryGetValue(hash, out List<int>? list))
                {
                    partial[hash] = list = [];
                }

                list.Add(node);
            }

            progress?.Report(totalFiles == 0 ? 1 : (double)processed / totalFiles);

            // Stage 3: full hash only when the partial hash didn't already cover the whole file.
            foreach (List<int> same in partial.Values.Where(l => l.Count > 1))
            {
                if (size <= 2 * PartialBlock)
                {
                    groups.Add(new DuplicateGroup(size, TryHash(tree.GetFullPath(same[0]), size, true, ct) ?? string.Empty, same));
                    continue;
                }

                var full = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                foreach (int node in same)
                {
                    string? hash = TryHash(tree.GetFullPath(node), size, partialOnly: false, ct);
                    if (hash is null)
                    {
                        continue;
                    }

                    if (!full.TryGetValue(hash, out List<int>? list))
                    {
                        full[hash] = list = [];
                    }

                    list.Add(node);
                }

                groups.AddRange(full.Where(kv => kv.Value.Count > 1).Select(kv => new DuplicateGroup(size, kv.Key, kv.Value)));
            }
        }

        progress?.Report(1);
        groups.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        return groups;
    }

    private string? TryHash(string path, long expectedSize, bool partialOnly, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
            {
                return null; // changed since the scan
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(PartialBlock);
            try
            {
                if (partialOnly)
                {
                    HashRange(stream, hasher, buffer, 0, Math.Min(PartialBlock, expectedSize), ct);
                    if (expectedSize > PartialBlock)
                    {
                        long tailStart = Math.Max(PartialBlock, expectedSize - PartialBlock);
                        HashRange(stream, hasher, buffer, tailStart, expectedSize - tailStart, ct);
                    }
                }
                else
                {
                    HashRange(stream, hasher, buffer, 0, expectedSize, ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Skipping {Path} for duplicate detection: {Message}", path, ex.Message);
            return null;
        }
    }

    private static void HashRange(FileStream stream, IncrementalHash hasher, byte[] buffer, long offset, long length, CancellationToken ct)
    {
        stream.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }
}
