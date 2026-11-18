using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Tests;

public class ScanTreeTests
{
    [Theory]
    [InlineData(@"C:\", @"C:\a\b\file.txt")]
    [InlineData(@"D:\Projects", @"D:\Projects\a\b\file.txt")]
    public void FullPath_IsRebuiltFromParents(string root, string expected)
    {
        ScanTree tree = TreeBuilder.Build(root, (@"a\b\file.txt", 1));
        int file = tree.FindByPath(expected);
        Assert.NotEqual(ScanTree.None, file);
        Assert.Equal(expected, tree.GetFullPath(file));
        Assert.Equal(Path.GetDirectoryName(expected), tree.GetParentPath(file));
    }

    [Fact]
    public void FindByPath_IsCaseInsensitive_AndRejectsSiblingPrefix()
    {
        ScanTree tree = TreeBuilder.Build(@"D:\Data", (@"Music\song.mp3", 1));
        Assert.NotEqual(ScanTree.None, tree.FindByPath(@"d:\data\MUSIC\Song.MP3"));
        Assert.Equal(ScanTree.RootIndex, tree.FindByPath(@"D:\Data\"));
        Assert.Equal(ScanTree.None, tree.FindByPath(@"D:\DataX\Music"));
        Assert.Equal(ScanTree.None, tree.FindByPath(@"D:\Data\Missing"));
    }

    [Fact]
    public void Extensions_AreInternedCaseInsensitively()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\", ("a.MP4", 1), ("b.mp4", 1), ("c.Mp4", 1), ("noext", 1), ("archive.tar.gz", 1));
        int a = tree.FindByPath(@"C:\a.MP4");
        int b = tree.FindByPath(@"C:\b.mp4");
        Assert.Equal(tree.GetExtensionId(a), tree.GetExtensionId(b));
        Assert.Equal(".mp4", tree.GetExtension(a));
        Assert.Equal(string.Empty, tree.GetExtension(tree.FindByPath(@"C:\noext")));
        Assert.Equal(".gz", tree.GetExtension(tree.FindByPath(@"C:\archive.tar.gz")));
    }

    [Fact]
    public void ParentIndex_IsAlwaysLowerThanChild()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\", (@"a\b\c\d", 1), (@"a\e", 2), (@"f\g", 3));
        for (int i = 1; i < tree.Count; i++)
        {
            Assert.True(tree.GetParent(i) < i);
        }
    }

    [Fact]
    public void Ancestry_AndDepth()
    {
        ScanTree tree = TreeBuilder.Build(@"C:\", (@"a\b\c.txt", 1));
        int file = tree.FindByPath(@"C:\a\b\c.txt");
        int a = tree.FindByPath(@"C:\a");
        Assert.Equal(3, tree.GetDepth(file));
        Assert.True(tree.IsSelfOrAncestor(a, file));
        Assert.False(tree.IsSelfOrAncestor(file, a));
        Assert.Equal(4, tree.GetAncestry(file).Count);
    }
}
