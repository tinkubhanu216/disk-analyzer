using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// Computes cumulative folder sizes and descendant counts.
/// Relies on the <see cref="ScanTree"/> invariant that a parent's index is lower than its children's,
/// so a single reverse pass visits every child before its parent: O(n), no recursion, no allocation.
/// </summary>
public sealed class FolderAggregationService : IFolderAggregationService
{
    public void Aggregate(ScanTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        int count = tree.Count;

        // Reset folder aggregates so aggregation is idempotent.
        for (int i = 0; i < count; i++)
        {
            if (tree.IsDirectory(i))
            {
                tree.SetSize(i, 0, 0);
                tree.SetCounts(i, 0, 0);
            }
        }

        for (int i = count - 1; i > 0; i--)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int parent = tree.GetParent(i);
            long parentSize = tree.GetSize(parent);
            long parentAllocated = tree.GetAllocated(parent);
            int parentFiles = tree.GetFileCount(parent);
            int parentDirs = tree.GetDirectoryCount(parent);

            if (tree.IsDirectory(i))
            {
                tree.SetSize(parent, parentSize + tree.GetSize(i), parentAllocated + tree.GetAllocated(i));
                tree.SetCounts(parent, parentFiles + tree.GetFileCount(i), parentDirs + tree.GetDirectoryCount(i) + 1);
            }
            else
            {
                tree.SetSize(parent, parentSize + tree.GetCountedSize(i), parentAllocated + tree.GetCountedAllocated(i));
                tree.SetCounts(parent, parentFiles + 1, parentDirs);
            }
        }

        tree.IsAggregated = true;
    }
}
