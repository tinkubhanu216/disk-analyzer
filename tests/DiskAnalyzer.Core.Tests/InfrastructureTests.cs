using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Infrastructure.Database;
using DiskAnalyzer.Infrastructure.Export;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskAnalyzer.Core.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "da-infra-" + Guid.NewGuid().ToString("N"));

    public InfrastructureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ScanResult SampleResult(DateTime? when = null, params (string, long)[] extra)
    {
        (string, long)[] files = [(@"Docs\a, b.pdf", 2_000_000), (@"Docs\tiny.txt", 10), (@"Media\v.mp4", 50_000_000), (@"Media\deep\x.jpg", 3_000_000), .. extra];
        ScanTree tree = TreeBuilder.Build(@"E:\", files);
        return new ScanResult
        {
            Tree = tree,
            RootPath = @"E:\",
            Drive = new DriveInfoModel { RootPath = @"E:\", TotalBytes = 1L << 30, FreeBytes = 1L << 29, VolumeSerialNumber = 0xABCD1234, FileSystem = "NTFS", IsReady = true },
            Source = ScanSource.Live,
            Status = ScanStatus.Completed,
            StartedUtc = when ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromSeconds(3),
            ProviderName = "test",
        };
    }

    [Fact]
    public async Task History_RoundTrip_PreservesFolderTotals_AndLabelsSource()
    {
        var cache = new SqliteScanCacheService(Path.Combine(_dir, "h.db"), NullLogger<SqliteScanCacheService>.Instance);
        ScanResult live = SampleResult();
        long id = await cache.SaveAsync(live, fileThresholdBytes: 1_000_000);

        IReadOnlyList<ScanHistory> history = await cache.GetHistoryAsync();
        ScanHistory row = Assert.Single(history);
        Assert.Equal(live.TotalBytes, row.TotalBytes);
        Assert.Equal(4, row.FileCount);
        Assert.Equal("E:ABCD1234", row.DriveId);

        ScanResult cached = await cache.LoadAsync(id, ScanSource.Cached);
        Assert.Equal(ScanSource.Cached, cached.Source);
        Assert.Equal(live.TotalBytes, cached.TotalBytes); // totals stay exact
        Assert.Equal(4, cached.TotalFiles);
        Assert.Equal(ScanTree.None, cached.Tree.FindByPath(@"E:\Docs\tiny.txt")); // below threshold, not stored
        Assert.NotEqual(ScanTree.None, cached.Tree.FindByPath(@"E:\Media\deep\x.jpg"));
        Assert.Equal(live.Tree.GetSize(live.Tree.FindByPath(@"E:\Docs")), cached.Tree.GetSize(cached.Tree.FindByPath(@"E:\Docs")));
        Assert.True(cached.IsPartial);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.LoadAsync(id, ScanSource.Live));
    }

    [Fact]
    public async Task History_Compare_And_Prune()
    {
        var cache = new SqliteScanCacheService(Path.Combine(_dir, "h2.db"), NullLogger<SqliteScanCacheService>.Instance) { MaxScansPerRoot = 2 };
        long first = await cache.SaveAsync(SampleResult(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), 0);
        long second = await cache.SaveAsync(SampleResult(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), (@"Media\new.mkv", 7_000_000)), 0);

        ScanComparison diff = await cache.CompareAsync(first, second);
        Assert.Contains(diff.AddedFiles, i => i.Path.EndsWith("new.mkv", StringComparison.Ordinal));
        Assert.Contains(diff.GrownFolders, i => i.Path == @"E:\Media" && i.DeltaBytes == 7_000_000);

        await cache.SaveAsync(SampleResult(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)), 0);
        IReadOnlyList<ScanHistory> history = await cache.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.DoesNotContain(history, h => h.ScanId == first);

        ScanHistory? latest = await cache.GetLatestForRootAsync("E:ABCD1234", @"E:\");
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), latest!.ScannedUtc);
        Assert.Null(await cache.GetLatestForRootAsync("E:00000000", @"E:\")); // different volume → no stale match

        await cache.DeleteAsync(second);
        Assert.Single(await cache.GetHistoryAsync());
    }

    [Fact]
    public async Task Export_Csv_QuotesAndIncludesAllRows()
    {
        ScanResult result = SampleResult();
        string path = Path.Combine(_dir, "out.csv");
        ExportSummary summary = await new ExportService(NullLogger<ExportService>.Instance).ExportAsync(result, new ExportOptions(path, ExportFormat.Csv, ExportScope.AllItems));

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(result.Tree.Count + 1, lines.Length);
        Assert.Equal(result.Tree.Count, summary.RowsWritten);
        Assert.StartsWith("Path,Name,Type", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("\"E:\\Docs\\a, b.pdf\",\"a, b.pdf\",File,.pdf,Documents,2000000,", StringComparison.Ordinal));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task Export_Json_TopFiles()
    {
        ScanResult result = SampleResult();
        string path = Path.Combine(_dir, "top.json");
        await new ExportService(NullLogger<ExportService>.Instance).ExportAsync(result, new ExportOptions(path, ExportFormat.Json, ExportScope.TopFiles, 2));

        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        JsonElement items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("v.mp4", items[0].GetProperty("name").GetString());
        Assert.Equal(50_000_000, items[0].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(@"E:\", doc.RootElement.GetProperty("scan").GetProperty("rootPath").GetString());
    }

    [Fact]
    public async Task Export_Xlsx_IsWellFormedSpreadsheet()
    {
        ScanResult result = SampleResult();
        string path = Path.Combine(_dir, "out.xlsx");
        await new ExportService(NullLogger<ExportService>.Instance).ExportAsync(result, new ExportOptions(path, ExportFormat.Excel, ExportScope.TopFolders, 10));

        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (string part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/styles.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" })
        {
            ZipArchiveEntry entry = zip.GetEntry(part) ?? throw new Xunit.Sdk.XunitException($"Missing {part}");
            using Stream s = entry.Open();
            XDocument.Load(s); // throws if not well-formed
        }

        using Stream sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XDocument xml = XDocument.Load(sheet);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        int rows = xml.Descendants(ns + "row").Count();
        Assert.Equal(1 + 3, rows); // header + Docs, Media, Media\deep (the scan root is excluded)
    }
}
