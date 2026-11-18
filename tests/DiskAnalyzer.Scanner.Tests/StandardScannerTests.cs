using System.Security.Principal;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Core.Threading;
using DiskAnalyzer.Scanner.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskAnalyzer.Scanner.Tests;

public class StandardScannerTests
{
    private static async Task<(ScanTree Tree, ScanProgressTracker Progress)> ScanAsync(string root, ScanConfiguration? config = null, CancellationToken ct = default)
    {
        var scanner = new StandardFileSystemScanner(NullLogger<StandardFileSystemScanner>.Instance);
        var progress = new ScanProgressTracker();
        ScanTree tree = await scanner.ScanAsync(config ?? new ScanConfiguration { RootPath = root }, progress, new AsyncPauseGate(), ct);
        new FolderAggregationService().Aggregate(tree, CancellationToken.None);
        return (tree, progress);
    }

    [Fact]
    public async Task Scan_ComputesSizesAndCounts()
    {
        using var temp = new TempTree();
        temp.File(@"a\one.bin", 1_000);
        temp.File(@"a\b\two.bin", 2_000);
        temp.File(@"a\b\c\three.bin", 3_000);
        temp.File(@"top.txt", 10);
        temp.Dir(@"empty\nested");

        var (tree, progress) = await ScanAsync(temp.Root);

        Assert.Equal(6_010, tree.GetSize(ScanTree.RootIndex));
        Assert.Equal(4, tree.GetFileCount(ScanTree.RootIndex));
        Assert.Equal(5, tree.GetDirectoryCount(ScanTree.RootIndex)); // a, b, c, empty, nested
        Assert.Equal(6_000, tree.GetSize(tree.FindByPath(Path.Combine(temp.Root, "a"))));
        Assert.Equal(0, tree.GetSize(tree.FindByPath(Path.Combine(temp.Root, "empty"))));
        Assert.Equal(4, progress.FilesScanned);
        Assert.Equal(6_010, progress.BytesDiscovered);
        Assert.Equal(0, progress.ErrorCount);
        Assert.Equal(Path.Combine(temp.Root, @"a\b\c\three.bin"), tree.GetFullPath(tree.FindByPath(Path.Combine(temp.Root, @"a\b\c\three.bin"))));
    }

    [Fact]
    public async Task Scan_EmptyDirectory()
    {
        using var temp = new TempTree();
        var (tree, _) = await ScanAsync(temp.Root);
        Assert.Equal(1, tree.Count);
        Assert.Equal(0, tree.GetSize(ScanTree.RootIndex));
    }

    [Fact]
    public async Task Scan_VeryLargeDirectory_IsBatchedCorrectly()
    {
        using var temp = new TempTree();
        const int count = 10_000; // > 2 batches of 4096
        for (int i = 0; i < count; i++)
        {
            temp.File($@"big\f{i}.dat", i % 7);
        }

        var (tree, _) = await ScanAsync(temp.Root);
        int big = tree.FindByPath(Path.Combine(temp.Root, "big"));
        Assert.Equal(count, tree.GetFileCount(big));
        Assert.Equal(count, tree.GetChildCount(big));
        long expected = Enumerable.Range(0, count).Sum(i => (long)(i % 7));
        Assert.Equal(expected, tree.GetSize(big));
    }

    [Fact]
    public async Task Scan_DeepStructure_BeyondMaxPath()
    {
        using var temp = new TempTree();
        string relative = string.Join('\\', Enumerable.Range(0, 60).Select(i => $"level{i:D2}")); // ~480 chars + root
        temp.File(Path.Combine(relative, "deep.txt"), 123);

        var (tree, progress) = await ScanAsync(temp.Root);
        Assert.Equal(123, tree.GetSize(ScanTree.RootIndex));
        Assert.Equal(60, tree.GetDirectoryCount(ScanTree.RootIndex));
        Assert.Equal(0, progress.ErrorCount);
        Assert.True(Path.Combine(temp.Root, relative).Length > 260);
    }

    [Fact]
    public async Task Scan_DoesNotFollowJunctions_ByDefault()
    {
        using var temp = new TempTree();
        temp.File(@"target\payload.bin", 50_000);
        temp.Junction("link", Path.Combine(temp.Root, "target"));

        var (tree, _) = await ScanAsync(temp.Root);
        int link = tree.FindByPath(Path.Combine(temp.Root, "link"));
        Assert.NotEqual(ScanTree.None, link);
        Assert.True((tree.GetFlags(link) & NodeFlags.ReparsePoint) != 0);
        Assert.Equal(0, tree.GetChildCount(link));
        Assert.Equal(50_000, tree.GetSize(ScanTree.RootIndex)); // not double counted
    }

    [Fact]
    public async Task Scan_JunctionLoop_IsGuarded_WhenFollowing()
    {
        using var temp = new TempTree();
        temp.File(@"inner\file.bin", 1_000);
        temp.Junction(@"inner\loop", temp.Root); // points back to an ancestor

        var (tree, _) = await ScanAsync(temp.Root, new ScanConfiguration { RootPath = temp.Root, FollowReparsePoints = true });
        Assert.Equal(1_000, tree.GetSize(ScanTree.RootIndex));
    }

    [Fact]
    public async Task Scan_HardLinks_CountedOnce_WhenDeduplicating()
    {
        using var temp = new TempTree();
        string original = temp.File(@"a\data.bin", 80_000);
        temp.Dir("b");
        temp.HardLink(@"b\alias.bin", original);

        var (plain, _) = await ScanAsync(temp.Root);
        Assert.Equal(160_000, plain.GetSize(ScanTree.RootIndex)); // Explorer-style: each name counted

        var (deduped, _) = await ScanAsync(temp.Root, new ScanConfiguration { RootPath = temp.Root, DeduplicateHardLinks = true });
        Assert.Equal(80_000, deduped.GetSize(ScanTree.RootIndex));
        Assert.Equal(2, deduped.GetFileCount(ScanTree.RootIndex));
    }

    [Fact]
    public async Task Scan_AccessDenied_IsRecorded_AndScanContinues()
    {
        using var temp = new TempTree();
        temp.File(@"open\visible.bin", 500);
        string locked = temp.Dir("locked");
        temp.File(@"locked\secret.bin", 9_999);
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal(0, TempTree.RunIcacls($"\"{locked}\" /deny *{sid}:(RD)"));
        try
        {
            var (tree, progress) = await ScanAsync(temp.Root);
            int lockedNode = tree.FindByPath(locked);
            Assert.True((tree.GetFlags(lockedNode) & NodeFlags.Inaccessible) != 0);
            Assert.Equal(500, tree.GetSize(ScanTree.RootIndex));
            Assert.True(progress.ErrorCount >= 1);
            Assert.Contains(progress.ErrorSamples, e => e.Path == locked);
        }
        finally
        {
            TempTree.RunIcacls($"\"{locked}\" /remove:d *{sid}");
        }
    }

    [Fact]
    public async Task Scan_HiddenFiles_CanBeExcluded()
    {
        using var temp = new TempTree();
        temp.File("shown.txt", 10);
        string hidden = temp.File("hidden.txt", 20);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var (all, _) = await ScanAsync(temp.Root);
        Assert.Equal(30, all.GetSize(ScanTree.RootIndex));
        var (visible, _) = await ScanAsync(temp.Root, new ScanConfiguration { RootPath = temp.Root, IncludeHidden = false });
        Assert.Equal(10, visible.GetSize(ScanTree.RootIndex));
    }

    [Fact]
    public async Task Scan_Cancellation_ReturnsPartialTree_WithoutThrowing()
    {
        using var temp = new TempTree();
        for (int d = 0; d < 50; d++)
        {
            for (int f = 0; f < 50; f++)
            {
                temp.File($@"d{d}\f{f}.bin", 1);
            }
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (tree, _) = await ScanAsync(temp.Root, null, cts.Token);
        Assert.True((tree.GetFlags(ScanTree.RootIndex) & NodeFlags.Partial) != 0);
        Assert.True(tree.GetFileCount(ScanTree.RootIndex) < 2_500);
    }

    [Fact]
    public async Task Scan_MissingRoot_Throws()
    {
        var scanner = new StandardFileSystemScanner(NullLogger<StandardFileSystemScanner>.Instance);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            scanner.ScanAsync(new ScanConfiguration { RootPath = @"Z:\definitely\missing\" + Guid.NewGuid() }, new ScanProgressTracker(), new AsyncPauseGate(), default));
    }

    [Fact]
    public async Task Scan_ReadOnly_DoesNotModifyFiles()
    {
        using var temp = new TempTree();
        string file = temp.File(@"x\keep.bin", 4_096);
        DateTime before = File.GetLastWriteTimeUtc(file);
        await ScanAsync(temp.Root);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.Equal(4_096, new FileInfo(file).Length);
    }
}
