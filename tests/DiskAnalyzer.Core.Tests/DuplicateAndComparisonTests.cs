using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Tests;

public sealed class DuplicateAndComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "da-dup-" + Guid.NewGuid().ToString("N"));

    public DuplicateAndComparisonTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task DuplicateFinder_GroupsIdenticalContent_AndIgnoresSameSizeDifferentContent()
    {
        byte[] content = new byte[300_000];
        new Random(42).NextBytes(content);
        byte[] different = (byte[])content.Clone();
        different[150_000] ^= 0xFF; // same size, same head/tail, different middle

        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(_root, "sub", "b.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(_root, "c.bin"), different);
        await File.WriteAllBytesAsync(Path.Combine(_root, "small1.txt"), "hello"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(_root, "small2.txt"), "hello"u8.ToArray());

        var tree = new ScanTree(_root);
        int sub = tree.AddDirectory(ScanTree.RootIndex, "sub", 0, FileAttributes.Directory);
        tree.AddFile(ScanTree.RootIndex, "a.bin", content.Length, 0, 0, FileAttributes.Normal);
        tree.AddFile(sub, "b.bin", content.Length, 0, 0, FileAttributes.Normal);
        tree.AddFile(ScanTree.RootIndex, "c.bin", different.Length, 0, 0, FileAttributes.Normal);
        tree.AddFile(ScanTree.RootIndex, "small1.txt", 5, 0, 0, FileAttributes.Normal);
        tree.AddFile(ScanTree.RootIndex, "small2.txt", 5, 0, 0, FileAttributes.Normal);

        var groups = await new DuplicateFileFinder().FindAsync(tree, minSizeBytes: 1);

        Assert.Equal(2, groups.Count);
        DuplicateGroup big = groups[0];
        Assert.Equal(content.Length, big.SizeBytes);
        Assert.Equal(["a.bin", "b.bin"], big.Nodes.Select(tree.GetName).Order());
        Assert.Equal(content.Length, big.WastedBytes);

        var onlyLarge = await new DuplicateFileFinder().FindAsync(tree, minSizeBytes: 1000);
        Assert.Single(onlyLarge);
    }

    [Fact]
    public void Comparison_FindsAddedRemovedAndResizedItems()
    {
        ScanTree older = TreeBuilder.Build(@"D:\",
            (@"keep\same.dat", 100),
            (@"grow\a.dat", 100),
            (@"shrink\b.dat", 500),
            (@"gone\deleted.iso", 4_000));
        ScanTree newer = TreeBuilder.Build(@"D:\",
            (@"keep\same.dat", 100),
            (@"grow\a.dat", 100),
            (@"grow\new.mp4", 9_000),
            (@"shrink\b.dat", 200),
            (@"fresh\x.bin", 50));

        var info = new ScanHistory { ScanId = 1, RootPath = @"D:\", TotalBytes = older.GetSize(0) };
        var info2 = new ScanHistory { ScanId = 2, RootPath = @"D:\", TotalBytes = newer.GetSize(0) };
        ScanComparison diff = ScanComparer.Compare(info, older, info2, newer);

        Assert.Contains(diff.AddedFiles, i => i.Path == @"D:\grow\new.mp4" && i.NewSizeBytes == 9_000);
        Assert.Contains(diff.AddedFiles, i => i.Path == @"D:\fresh\x.bin");
        Assert.Contains(diff.RemovedFiles, i => i.Path == @"D:\gone\deleted.iso" && i.OldSizeBytes == 4_000);
        Assert.Contains(diff.GrownFolders, i => i.Path == @"D:\grow" && i.DeltaBytes == 9_000);
        Assert.Contains(diff.ShrunkFolders, i => i.Path == @"D:\shrink" && i.DeltaBytes == -300);
        Assert.Contains(diff.AddedFolders, i => i.Path == @"D:\fresh");
        Assert.Contains(diff.RemovedFolders, i => i.Path == @"D:\gone");
        Assert.DoesNotContain(diff.GrownFolders, i => i.Path == @"D:\keep");
        Assert.Equal(2, diff.TotalAddedFiles);
        Assert.Equal(1, diff.TotalRemovedFiles);
    }

    [Fact]
    public void Comparison_IgnoresFilesBelowStoredThreshold()
    {
        ScanTree older = TreeBuilder.Build(@"D:\", (@"a\tiny.txt", 10), (@"a\big.bin", 5_000));
        ScanTree newer = TreeBuilder.Build(@"D:\", (@"a\big.bin", 5_000));
        var o = new ScanHistory { StoredFileThresholdBytes = 0 };
        var n = new ScanHistory { StoredFileThresholdBytes = 1_000 };
        ScanComparison diff = ScanComparer.Compare(o, older, n, newer);
        Assert.Empty(diff.RemovedFiles);
        Assert.Equal(1_000, diff.ComparedFileThresholdBytes);
    }
}
