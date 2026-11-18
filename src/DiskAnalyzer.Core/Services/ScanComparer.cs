using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>Compares two scans of the same root by path.</summary>
public static class ScanComparer
{
    public const int MaxListedItems = 2000;

    public static ScanComparison Compare(ScanHistory olderInfo, ScanTree older, ScanHistory newerInfo, ScanTree newer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        // Only files present in both stores can be compared fairly.
        long threshold = Math.Max(olderInfo.StoredFileThresholdBytes, newerInfo.StoredFileThresholdBytes);

        var oldIndex = new Dictionary<string, (long Size, bool IsDir)>(older.Count, StringComparer.OrdinalIgnoreCase);
        var paths = BuildRelativePaths(older, cancellationToken);
        for (int i = 1; i < older.Count; i++)
        {
            bool isDir = older.IsDirectory(i);
            if (!isDir && older.GetSize(i) < threshold)
            {
                continue;
            }

            oldIndex[paths[i]] = (older.GetSize(i), isDir);
        }

        var addedFiles = new List<ChangedItem>();
        var addedFolders = new List<ChangedItem>();
        var grown = new List<ChangedItem>();
        var shrunk = new List<ChangedItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newPaths = BuildRelativePaths(newer, cancellationToken);
        string newRoot = newer.RootPath.TrimEnd('\\');

        for (int i = 1; i < newer.Count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool isDir = newer.IsDirectory(i);
            long size = newer.GetSize(i);
            if (!isDir && size < threshold)
            {
                continue;
            }

            string rel = newPaths[i];
            string display = newRoot + "\\" + rel;
            if (oldIndex.TryGetValue(rel, out var old) && old.IsDir == isDir)
            {
                seen.Add(rel);
                if (isDir && size != old.Size)
                {
                    (size > old.Size ? grown : shrunk).Add(new ChangedItem(display, true, size > old.Size ? ChangeKind.Grown : ChangeKind.Shrunk, old.Size, size));
                }
            }
            else
            {
                (isDir ? addedFolders : addedFiles).Add(new ChangedItem(display, isDir, ChangeKind.Added, 0, size));
            }
        }

        var removedFiles = new List<ChangedItem>();
        var removedFolders = new List<ChangedItem>();
        string oldRoot = older.RootPath.TrimEnd('\\');
        foreach (var (rel, old) in oldIndex)
        {
            if (!seen.Contains(rel))
            {
                (old.IsDir ? removedFolders : removedFiles).Add(new ChangedItem(oldRoot + "\\" + rel, old.IsDir, ChangeKind.Removed, old.Size, 0));
            }
        }

        return new ScanComparison
        {
            Older = olderInfo,
            Newer = newerInfo,
            AddedFiles = Top(addedFiles, x => x.NewSizeBytes),
            RemovedFiles = Top(removedFiles, x => x.OldSizeBytes),
            GrownFolders = Top(grown, x => x.DeltaBytes),
            ShrunkFolders = Top(shrunk, x => -x.DeltaBytes),
            AddedFolders = Top(addedFolders, x => x.NewSizeBytes),
            RemovedFolders = Top(removedFolders, x => x.OldSizeBytes),
            TotalAddedFiles = addedFiles.Count,
            TotalRemovedFiles = removedFiles.Count,
            AddedFileBytes = addedFiles.Sum(x => x.NewSizeBytes),
            RemovedFileBytes = removedFiles.Sum(x => x.OldSizeBytes),
            ComparedFileThresholdBytes = threshold,
        };
    }

    private static List<ChangedItem> Top(List<ChangedItem> items, Func<ChangedItem, long> key) =>
        items.OrderByDescending(key).Take(MaxListedItems).ToList();

    /// <summary>Relative path per node, built top-down (parents precede children).</summary>
    private static string[] BuildRelativePaths(ScanTree tree, CancellationToken cancellationToken)
    {
        var paths = new string[tree.Count];
        paths[0] = string.Empty;
        for (int i = 1; i < tree.Count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string parent = paths[tree.GetParent(i)];
            paths[i] = parent.Length == 0 ? tree.GetName(i) : parent + "\\" + tree.GetName(i);
        }

        return paths;
    }
}
