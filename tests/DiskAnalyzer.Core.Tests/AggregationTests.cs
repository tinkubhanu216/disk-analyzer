using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Tests;

public class AggregationTests
{
    [Fact]
    public void FolderSize_IsSumOfNestedFiles()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\",
            (@"a\one.txt", 100),
            (@"a\b\two.bin", 250),
            (@"a\b\c\three.mp4", 1_000),
            (@"d\four.zip", 7));

        int a = tree.FindByPath(@"C:\a");
        int b = tree.FindByPath(@"C:\a\b");
        int c = tree.FindByPath(@"C:\a\b\c");
        int d = tree.FindByPath(@"C:\d");

        Assert.Equal(1_350, tree.GetSize(a));
        Assert.Equal(1_250, tree.GetSize(b));
        Assert.Equal(1_000, tree.GetSize(c));
        Assert.Equal(7, tree.GetSize(d));
        Assert.Equal(1_357, tree.GetSize(ScanTree.RootIndex));
    }

    [Fact]
    public void Counts_IncludeAllDescendants()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\data",
            (@"x\1", 1), (@"x\2", 1), (@"x\y\3", 1), (@"x\y\z\4", 1), (@"5", 1));

        int x = tree.FindByPath(@"C:\data\x");
        Assert.Equal(4, tree.GetFileCount(x));
        Assert.Equal(2, tree.GetDirectoryCount(x));
        Assert.Equal(5, tree.GetFileCount(ScanTree.RootIndex));
        Assert.Equal(3, tree.GetDirectoryCount(ScanTree.RootIndex));
    }

    [Fact]
    public void Aggregation_IsIdempotent()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\", (@"a\f", 10), (@"a\b\g", 20));
        var service = new FolderAggregationService();
        service.Aggregate(tree);
        service.Aggregate(tree);
        Assert.Equal(30, tree.GetSize(ScanTree.RootIndex));
        Assert.Equal(2, tree.GetFileCount(ScanTree.RootIndex));
    }

    [Fact]
    public void EmptyDirectories_HaveZeroSize()
    {
        var tree = new ScanTree(@"C:\");
        int empty = tree.AddDirectory(ScanTree.RootIndex, "empty", 0, FileAttributes.Directory);
        int nested = tree.AddDirectory(empty, "nested", 0, FileAttributes.Directory);
        new FolderAggregationService().Aggregate(tree);

        Assert.Equal(0, tree.GetSize(empty));
        Assert.Equal(0, tree.GetSize(nested));
        Assert.Equal(1, tree.GetDirectoryCount(empty));
        Assert.Equal(0, tree.GetFileCount(ScanTree.RootIndex));
        Assert.Equal(2, tree.GetDirectoryCount(ScanTree.RootIndex));
    }

    [Fact]
    public void HardLinkDuplicates_AreCountedOnce()
    {
        var tree = new ScanTree(@"C:\");
        int a = tree.AddDirectory(ScanTree.RootIndex, "a", 0, FileAttributes.Directory);
        int b = tree.AddDirectory(ScanTree.RootIndex, "b", 0, FileAttributes.Directory);
        tree.AddFile(a, "file.dll", 1_000, 4096, 0, FileAttributes.Normal);
        tree.AddFile(b, "file.dll", 1_000, 4096, 0, FileAttributes.Normal, NodeFlags.HardLinkDuplicate);
        new FolderAggregationService().Aggregate(tree);

        Assert.Equal(1_000, tree.GetSize(ScanTree.RootIndex));
        Assert.Equal(4096, tree.GetAllocated(ScanTree.RootIndex));
        Assert.Equal(0, tree.GetSize(b));
        Assert.Equal(2, tree.GetFileCount(ScanTree.RootIndex));
    }

    [Fact]
    public void LargeTree_SpanningManyChunks_AggregatesCorrectly()
    {
        var tree = new ScanTree(@"C:\");
        int folders = 300;
        int filesPerFolder = 1_000; // 300k nodes, > 4 chunks of 64K
        long expected = 0;
        for (int f = 0; f < folders; f++)
        {
            int dir = tree.AddDirectory(ScanTree.RootIndex, "d" + f, 0, FileAttributes.Directory);
            for (int i = 0; i < filesPerFolder; i++)
            {
                tree.AddFile(dir, $"f{i}.dat", i + 1, 0, 0, FileAttributes.Normal);
                expected += i + 1;
            }
        }

        new FolderAggregationService().Aggregate(tree);
        Assert.Equal(expected, tree.GetSize(ScanTree.RootIndex));
        Assert.Equal(folders * filesPerFolder, tree.GetFileCount(ScanTree.RootIndex));
        Assert.Equal(folders + (folders * filesPerFolder) + 1, tree.Count);
    }

    [Fact]
    public void AddNode_RejectsInvalidParent()
    {
        var tree = new ScanTree(@"C:\");
        int file = tree.AddFile(ScanTree.RootIndex, "x.txt", 1, 1, 0, FileAttributes.Normal);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.AddFile(99, "y", 1, 1, 0, FileAttributes.Normal));
        Assert.Throws<ArgumentException>(() => tree.AddFile(file, "z", 1, 1, 0, FileAttributes.Normal));
    }
}
