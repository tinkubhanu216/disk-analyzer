using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Tests;

/// <summary>Small helper to build trees from path/size pairs.</summary>
internal static class TreeBuilder
{
    public static ScanTree Build(string root, params (string Path, long Size)[] files)
    {
        var tree = new ScanTree(root);
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = ScanTree.RootIndex };
        foreach (var (path, size) in files)
        {
            string[] parts = path.Split('\\');
            string current = string.Empty;
            int parent = ScanTree.RootIndex;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = current.Length == 0 ? parts[i] : current + "\\" + parts[i];
                if (!folders.TryGetValue(current, out int dir))
                {
                    dir = tree.AddDirectory(parent, parts[i], new DateTime(2024, 1, 1).Ticks, FileAttributes.Directory);
                    folders[current] = dir;
                }

                parent = dir;
            }

            if (parts[^1].EndsWith('\\') || size < 0)
            {
                string dirPath = current.Length == 0 ? parts[^1] : current + "\\" + parts[^1];
                if (!folders.ContainsKey(dirPath))
                {
                    folders[dirPath] = tree.AddDirectory(parent, parts[^1], 0, FileAttributes.Directory);
                }

                continue;
            }

            tree.AddFile(parent, parts[^1], size, (size + 4095) / 4096 * 4096, new DateTime(2025, 6, 1).Ticks, FileAttributes.Normal);
        }

        new FolderAggregationService().Aggregate(tree);
        return tree;
    }
}
