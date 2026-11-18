using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Tests;

public class AnalysisTests
{
    private readonly StorageAnalysisService _service = new();

    private static ScanTree Sample() => TreeBuilder.Build(@"C:\",
        (@"Videos\movie.mkv", 5_000_000),
        (@"Videos\clip.mp4", 2_000_000),
        (@"Docs\report.pdf", 300_000),
        (@"Docs\notes.TXT", 1_000),
        (@"Docs\old\scan.pdf", 700_000),
        (@"Code\app.cs", 5_000),
        (@"Code\bin\app.exe", 900_000),
        (@"photo.JPG", 1_500_000),
        (@"README", 50));

    [Fact]
    public void Percentages_AreRelativeToWhole()
    {
        Assert.Equal(25, SizeFormatter.Percent(256, 1024), 6);
        Assert.Equal(0, SizeFormatter.Percent(10, 0));
        Assert.Equal(74.21875, SizeFormatter.Percent(380L << 30, 512L << 30), 6);
    }

    [Fact]
    public void LargestFiles_AreSortedDescending()
    {
        ScanTree tree = Sample();
        IReadOnlyList<int> top = _service.GetLargestFiles(tree, 3);
        Assert.Equal(["movie.mkv", "clip.mp4", "photo.JPG"], top.Select(tree.GetName));
    }

    [Fact]
    public void LargestFolders_ExcludeScope()
    {
        ScanTree tree = Sample();
        IReadOnlyList<int> top = _service.GetLargestFolders(tree, 10);
        Assert.DoesNotContain(ScanTree.RootIndex, top);
        Assert.Equal("Videos", tree.GetName(top[0]));
        Assert.Equal(7_000_000, tree.GetSize(top[0]));
    }

    [Fact]
    public void LargestFiles_RespectsScope()
    {
        ScanTree tree = Sample();
        int docs = tree.FindByPath(@"C:\Docs");
        IReadOnlyList<int> top = _service.GetLargestFiles(tree, 10, docs);
        Assert.Equal(["scan.pdf", "report.pdf", "notes.TXT"], top.Select(tree.GetName));
    }

    [Fact]
    public void ExtensionGrouping_SumsBytesAndCounts()
    {
        ScanTree tree = Sample();
        var stats = _service.GetExtensionStatistics(tree);
        ExtensionStatistic pdf = stats.Single(s => s.Extension == ".pdf");
        Assert.Equal(1_000_000, pdf.TotalBytes);
        Assert.Equal(2, pdf.FileCount);
        Assert.Equal(FileCategory.Documents, pdf.Category);
        Assert.Equal(".mkv", stats[0].Extension);
        Assert.Contains(stats, s => s.Extension == string.Empty && s.FileCount == 1);
        Assert.Equal(tree.GetSize(ScanTree.RootIndex), stats.Sum(s => s.TotalBytes));
    }

    [Fact]
    public void CategoryGrouping_CoversAllBytes()
    {
        ScanTree tree = Sample();
        var categories = _service.GetCategoryStatistics(tree);
        Assert.Equal(7_000_000, categories.Single(c => c.Category == FileCategory.Videos).TotalBytes);
        Assert.Equal(1_001_000, categories.Single(c => c.Category == FileCategory.Documents).TotalBytes);
        Assert.Equal(tree.GetSize(ScanTree.RootIndex), categories.Sum(c => c.TotalBytes));
    }

    [Fact]
    public void Statistics_FindLargestItems()
    {
        ScanTree tree = Sample();
        StorageStatistics stats = _service.GetStatistics(tree);
        Assert.Equal("movie.mkv", tree.GetName(stats.LargestFileIndex));
        Assert.Equal("Videos", tree.GetName(stats.LargestFolderIndex));
        Assert.Equal(9, stats.FileCount);
        Assert.Equal(5, stats.FolderCount);
    }

    [Theory]
    [InlineData("pdf", null, 2)]
    [InlineData("*.pdf", null, 2)]
    [InlineData("APP", null, 2)]
    [InlineData("doc", null, 1)]
    [InlineData("a?p.*", null, 2)]
    public void Search_MatchesNames(string text, long? minSize, int expected)
    {
        ScanTree tree = Sample();
        SearchResult result = StorageAnalysisService.Search(tree, new SearchQuery { Text = text, MinSizeBytes = minSize ?? 0 }, CancellationToken.None);
        Assert.Equal(expected, result.TotalMatches);
    }

    [Fact]
    public void Search_FiltersByExtensionCategoryAndSize()
    {
        ScanTree tree = Sample();
        Assert.Equal(2, StorageAnalysisService.Search(tree, new SearchQuery { Extensions = [".pdf"] }, default).TotalMatches);
        Assert.Equal(2, StorageAnalysisService.Search(tree, new SearchQuery { Category = FileCategory.Videos }, default).TotalMatches);
        SearchResult big = StorageAnalysisService.Search(tree, new SearchQuery { MinSizeBytes = 1_000_000, IncludeFolders = false }, default);
        Assert.Equal(["movie.mkv", "clip.mp4", "photo.JPG"], big.Nodes.Select(tree.GetName));
        SearchResult folders = StorageAnalysisService.Search(tree, new SearchQuery { Text = "o", IncludeFiles = false }, default);
        Assert.All(folders.Nodes, n => Assert.True(tree.IsDirectory(n)));
    }

    [Fact]
    public void Search_RespectsScopeAndLimit()
    {
        ScanTree tree = Sample();
        int docs = tree.FindByPath(@"C:\Docs");
        SearchResult scoped = StorageAnalysisService.Search(tree, new SearchQuery { Text = "pdf", ScopeNode = docs }, default);
        Assert.Equal(2, scoped.TotalMatches);
        SearchResult limited = StorageAnalysisService.Search(tree, new SearchQuery { Text = "", IncludeFolders = false, MaxResults = 3 }, default);
        Assert.Equal(3, limited.Nodes.Count);
        Assert.Equal(9, limited.TotalMatches);
        Assert.True(limited.IsTruncated);
    }

    [Fact]
    public async Task Search_IsCancellable()
    {
        ScanTree tree = Sample();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SearchAsync(tree, new SearchQuery { Text = "x" }, cts.Token));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(512L * 1024 * 1024 * 1024, "512 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TB")]
    public void SizeFormatter_UsesBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("500 MB", 500L * 1024 * 1024)]
    [InlineData("1.5GB", 1536L * 1024 * 1024)]
    [InlineData("200k", 200L * 1024)]
    [InlineData("1024", 1024L)]
    public void SizeFormatter_Parses(string text, long expected)
    {
        Assert.True(SizeFormatter.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(".MP4", FileCategory.Videos)]
    [InlineData("jpg", FileCategory.Images)]
    [InlineData(".docx", FileCategory.Documents)]
    [InlineData(".7z", FileCategory.Archives)]
    [InlineData(".cs", FileCategory.Development)]
    [InlineData(".dll", FileCategory.System)]
    [InlineData(".exe", FileCategory.Applications)]
    [InlineData(".flac", FileCategory.Audio)]
    [InlineData(".unknownext", FileCategory.Other)]
    [InlineData("", FileCategory.Other)]
    public void Classifier_MapsExtensions(string ext, FileCategory expected) =>
        Assert.Equal(expected, FileCategoryClassifier.Classify(ext));
}
