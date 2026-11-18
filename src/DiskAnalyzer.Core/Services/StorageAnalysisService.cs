using System.Collections.Concurrent;
using System.Diagnostics;
using DiskAnalyzer.Core.Algorithms;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>Analysis and search directly over <see cref="ScanTree"/> columns. Nothing is rescanned.</summary>
public sealed class StorageAnalysisService : IStorageAnalysisService
{
    public StorageStatistics GetStatistics(ScanTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        int inaccessible = 0, reparse = 0, hardLinks = 0;
        int largestFile = ScanTree.None, largestFolder = ScanTree.None;
        long largestFileSize = -1, largestFolderSize = -1;
        int count = tree.Count;
        for (int i = 1; i < count; i++)
        {
            NodeFlags flags = tree.GetFlags(i);
            if ((flags & NodeFlags.Inaccessible) != 0)
            {
                inaccessible++;
            }

            if ((flags & NodeFlags.ReparsePoint) != 0)
            {
                reparse++;
            }

            if ((flags & NodeFlags.HardLinkDuplicate) != 0)
            {
                hardLinks++;
            }

            long size = tree.GetCountedSize(i);
            if ((flags & NodeFlags.Directory) != 0)
            {
                if (size > largestFolderSize)
                {
                    largestFolderSize = size;
                    largestFolder = i;
                }
            }
            else if (size > largestFileSize)
            {
                largestFileSize = size;
                largestFile = i;
            }
        }

        return new StorageStatistics
        {
            TotalBytes = tree.GetSize(ScanTree.RootIndex),
            TotalAllocatedBytes = tree.GetAllocated(ScanTree.RootIndex),
            FileCount = tree.GetFileCount(ScanTree.RootIndex),
            FolderCount = tree.GetDirectoryCount(ScanTree.RootIndex),
            InaccessibleFolderCount = inaccessible,
            ReparsePointCount = reparse,
            HardLinkDuplicateCount = hardLinks,
            LargestFileIndex = largestFile,
            LargestFolderIndex = largestFolder,
            ExtensionCount = tree.Extensions.Count - 1,
        };
    }

    public IReadOnlyList<int> GetLargestFiles(ScanTree tree, int count, int scopeNode = ScanTree.RootIndex) =>
        TreeAlgorithms.TopN(tree, scopeNode, count, i => !tree.IsDirectory(i) && (tree.GetFlags(i) & NodeFlags.HardLinkDuplicate) == 0, tree.GetSize);

    public IReadOnlyList<int> GetLargestFolders(ScanTree tree, int count, int scopeNode = ScanTree.RootIndex) =>
        TreeAlgorithms.TopN(tree, scopeNode, count, i => i != scopeNode && tree.IsDirectory(i), tree.GetSize);

    public IReadOnlyList<ExtensionStatistic> GetExtensionStatistics(ScanTree tree, int scopeNode = ScanTree.RootIndex)
    {
        ArgumentNullException.ThrowIfNull(tree);
        int extensionCount = tree.Extensions.Count;
        long[] bytes = new long[extensionCount];
        long[] files = new long[extensionCount];
        TreeAlgorithms.VisitSubtree(tree, scopeNode, i =>
        {
            if (!tree.IsDirectory(i))
            {
                int ext = tree.GetExtensionId(i);
                bytes[ext] += tree.GetCountedSize(i);
                files[ext]++;
            }
        });

        var result = new List<ExtensionStatistic>();
        for (int e = 0; e < extensionCount; e++)
        {
            if (files[e] > 0)
            {
                string ext = tree.Extensions[e];
                result.Add(new ExtensionStatistic(ext, FileCategoryClassifier.Classify(ext), bytes[e], files[e]));
            }
        }

        result.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return result;
    }

    public IReadOnlyList<CategoryStatistic> GetCategoryStatistics(ScanTree tree, int scopeNode = ScanTree.RootIndex)
    {
        var bytes = new long[FileCategoryClassifier.All.Count];
        var files = new long[FileCategoryClassifier.All.Count];
        foreach (ExtensionStatistic stat in GetExtensionStatistics(tree, scopeNode))
        {
            bytes[(int)stat.Category] += stat.TotalBytes;
            files[(int)stat.Category] += stat.FileCount;
        }

        return FileCategoryClassifier.All
            .Select(c => new CategoryStatistic(c, bytes[(int)c], files[(int)c]))
            .Where(c => c.FileCount > 0)
            .OrderByDescending(c => c.TotalBytes)
            .ToList();
    }

    public Task<SearchResult> SearchAsync(ScanTree tree, SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Search(tree, query, cancellationToken), cancellationToken);
    }

    internal static SearchResult Search(ScanTree tree, SearchQuery query, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        NameMatcher matcher = NameMatcher.Create(query.Text);

        // Pre-compute which extension ids pass the extension/category filter.
        bool[]? allowedExtensions = null;
        if (query.Extensions is { Count: > 0 } || query.Category is not null)
        {
            var wanted = query.Extensions is { Count: > 0 }
                ? new HashSet<string>(query.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase)
                : null;
            allowedExtensions = new bool[tree.Extensions.Count];
            for (int e = 0; e < allowedExtensions.Length; e++)
            {
                string ext = tree.Extensions[e];
                allowedExtensions[e] = (wanted is null || wanted.Contains(ext))
                    && (query.Category is null || FileCategoryClassifier.Classify(ext) == query.Category);
            }
        }

        bool fileOnlyFilter = allowedExtensions is not null;
        int count = tree.Count;
        var buckets = new ConcurrentBag<List<int>>();
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(1, count, Math.Max(4096, count / (Environment.ProcessorCount * 4))), options, range =>
        {
            var local = new List<int>();
            for (int i = range.Item1; i < range.Item2; i++)
            {
                bool isDir = tree.IsDirectory(i);
                if (isDir ? !query.IncludeFolders || fileOnlyFilter : !query.IncludeFiles)
                {
                    continue;
                }

                if (tree.GetSize(i) < query.MinSizeBytes)
                {
                    continue;
                }

                if (allowedExtensions is not null && !allowedExtensions[tree.GetExtensionId(i)])
                {
                    continue;
                }

                if (!matcher.IsMatch(tree.GetName(i)))
                {
                    continue;
                }

                if (query.ScopeNode != ScanTree.RootIndex && !tree.IsSelfOrAncestor(query.ScopeNode, i))
                {
                    continue;
                }

                local.Add(i);
            }

            if (local.Count > 0)
            {
                buckets.Add(local);
            }
        });

        var all = new List<int>(buckets.Sum(b => b.Count));
        foreach (List<int> bucket in buckets)
        {
            all.AddRange(bucket);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int total = all.Count;
        all.Sort((a, b) => tree.GetSize(b).CompareTo(tree.GetSize(a)));
        if (all.Count > query.MaxResults)
        {
            all.RemoveRange(query.MaxResults, all.Count - query.MaxResults);
        }

        return new SearchResult(all, total, stopwatch.Elapsed);
    }
}

/// <summary>Case-insensitive substring or <c>*</c>/<c>?</c> wildcard matcher.</summary>
public sealed class NameMatcher
{
    private readonly string? _text;
    private readonly bool _wildcard;

    private NameMatcher(string? text, bool wildcard)
    {
        _text = text;
        _wildcard = wildcard;
    }

    public static NameMatcher Create(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new NameMatcher(null, false);
        }

        return new NameMatcher(text, text.Contains('*') || text.Contains('?'));
    }

    public bool IsMatch(string name)
    {
        if (_text is null)
        {
            return true;
        }

        return _wildcard
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(_text, name, ignoreCase: true)
            : name.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }
}
