using System.Globalization;
using System.Text;
using System.Text.Json;
using DiskAnalyzer.Core.Algorithms;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.Infrastructure.Export;

/// <summary>One exported row. Built lazily per node so a million rows never sit in memory.</summary>
internal readonly record struct ExportRow(
    string Path,
    string Name,
    bool IsDirectory,
    string Extension,
    string Category,
    long SizeBytes,
    long AllocatedBytes,
    string ParentFolder,
    DateTime? LastModifiedUtc,
    int FileCount,
    int FolderCount,
    double PercentOfDrive,
    double PercentOfParent,
    double PercentOfScan);

/// <summary>Streams scan results to CSV, Excel (.xlsx) or JSON.</summary>
public sealed class ExportService(ILogger<ExportService> logger) : IExportService
{
    internal static readonly string[] Headers =
    [
        "Path", "Name", "Type", "Extension", "Category", "Size (bytes)", "Size on disk (bytes)", "Size",
        "Parent folder", "Last modified (UTC)", "Files", "Folders", "% of drive", "% of parent", "% of scan",
    ];

    public async Task<ExportSummary> ExportAsync(ScanResult result, ExportOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        string directory = Path.GetDirectoryName(Path.GetFullPath(options.FilePath))!;
        Directory.CreateDirectory(directory);

        IReadOnlyList<int> nodes = SelectNodes(result.Tree, options);
        string temp = options.FilePath + ".partial";
        ExportSummary summary;
        try
        {
            summary = await Task.Run(() => options.Format switch
            {
                ExportFormat.Csv => WriteCsv(temp, result, nodes, progress, cancellationToken),
                ExportFormat.Json => WriteJson(temp, result, options, nodes, progress, cancellationToken),
                ExportFormat.Excel => XlsxWriter.Write(temp, result, nodes, BuildRow, progress, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unknown format."),
            }, cancellationToken).ConfigureAwait(false);

            File.Move(temp, options.FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        logger.LogInformation("Exported {Rows:N0} rows ({Scope}) to {Path} as {Format}", summary.RowsWritten, options.Scope, options.FilePath, options.Format);
        return summary with { FilePath = options.FilePath };
    }

    private static IReadOnlyList<int> SelectNodes(ScanTree tree, ExportOptions options)
    {
        int top = Math.Clamp(options.TopCount, 1, 1_000_000);
        switch (options.Scope)
        {
            case ExportScope.TopFiles:
                return TreeAlgorithms.TopN(tree, ScanTree.RootIndex, top, i => !tree.IsDirectory(i) && (tree.GetFlags(i) & NodeFlags.HardLinkDuplicate) == 0, tree.GetSize);
            case ExportScope.TopFolders:
                return TreeAlgorithms.TopN(tree, ScanTree.RootIndex, top, i => i != ScanTree.RootIndex && tree.IsDirectory(i), tree.GetSize);
            case ExportScope.FoldersOnly:
            {
                var list = new List<int>();
                for (int i = 0; i < tree.Count; i++)
                {
                    if (tree.IsDirectory(i))
                    {
                        list.Add(i);
                    }
                }

                return list;
            }

            default:
                return new RangeList(tree.Count);
        }
    }

    internal static ExportRow BuildRow(ScanResult result, int index)
    {
        ScanTree tree = result.Tree;
        bool isDir = tree.IsDirectory(index);
        int parent = tree.GetParent(index);
        long size = tree.GetSize(index);
        string ext = isDir ? string.Empty : tree.GetExtension(index);
        return new ExportRow(
            tree.GetFullPath(index),
            index == ScanTree.RootIndex ? tree.RootPath : tree.GetName(index),
            isDir,
            ext,
            isDir ? "Folder" : FileCategoryClassifier.Classify(ext).ToString(),
            size,
            tree.GetAllocated(index),
            parent >= 0 ? tree.GetFullPath(parent) : string.Empty,
            tree.GetLastWriteUtc(index),
            isDir ? tree.GetFileCount(index) : 0,
            isDir ? tree.GetDirectoryCount(index) : 0,
            SizeFormatter.Percent(size, result.DriveCapacityBytes),
            parent >= 0 ? SizeFormatter.Percent(size, tree.GetSize(parent)) : 100,
            SizeFormatter.Percent(size, result.TotalBytes));
    }

    private static ExportSummary WriteCsv(string path, ScanResult result, IReadOnlyList<int> nodes, IProgress<double>? progress, CancellationToken ct)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        writer.WriteLine(string.Join(',', Headers.Select(Csv)));
        var sb = new StringBuilder(512);
        CultureInfo inv = CultureInfo.InvariantCulture;
        for (int n = 0; n < nodes.Count; n++)
        {
            if ((n & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)n / nodes.Count);
            }

            ExportRow row = BuildRow(result, nodes[n]);
            sb.Clear()
                .Append(Csv(row.Path)).Append(',')
                .Append(Csv(row.Name)).Append(',')
                .Append(row.IsDirectory ? "Folder" : "File").Append(',')
                .Append(Csv(row.Extension)).Append(',')
                .Append(row.Category).Append(',')
                .Append(row.SizeBytes.ToString(inv)).Append(',')
                .Append(row.AllocatedBytes.ToString(inv)).Append(',')
                .Append(Csv(SizeFormatter.Format(row.SizeBytes, inv))).Append(',')
                .Append(Csv(row.ParentFolder)).Append(',')
                .Append(row.LastModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? string.Empty).Append(',')
                .Append(row.IsDirectory ? row.FileCount.ToString(inv) : string.Empty).Append(',')
                .Append(row.IsDirectory ? row.FolderCount.ToString(inv) : string.Empty).Append(',')
                .Append(row.PercentOfDrive.ToString("0.####", inv)).Append(',')
                .Append(row.PercentOfParent.ToString("0.####", inv)).Append(',')
                .Append(row.PercentOfScan.ToString("0.####", inv));
            writer.WriteLine(sb);
        }

        progress?.Report(1);
        return new ExportSummary(path, nodes.Count, false, null);
    }

    internal static string Csv(string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static ExportSummary WriteJson(string path, ScanResult result, ExportOptions options, IReadOnlyList<int> nodes, IProgress<double>? progress, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        json.WriteStartObject();
        json.WriteStartObject("scan");
        json.WriteString("rootPath", result.RootPath);
        json.WriteString("source", result.Source.ToString());
        json.WriteString("status", result.Status.ToString());
        json.WriteString("scannedUtc", result.StartedUtc);
        json.WriteNumber("durationSeconds", Math.Round(result.Duration.TotalSeconds, 3));
        json.WriteString("provider", result.ProviderName);
        json.WriteNumber("totalBytes", result.TotalBytes);
        json.WriteNumber("totalAllocatedBytes", result.TotalAllocatedBytes);
        json.WriteNumber("fileCount", result.TotalFiles);
        json.WriteNumber("folderCount", result.TotalFolders);
        json.WriteNumber("driveCapacityBytes", result.DriveCapacityBytes);
        json.WriteNumber("storedFileThresholdBytes", result.StoredFileThresholdBytes);
        json.WriteString("exportScope", options.Scope.ToString());
        json.WriteEndObject();
        json.WriteStartArray("items");
        for (int n = 0; n < nodes.Count; n++)
        {
            if ((n & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)n / nodes.Count);
                json.Flush();
            }

            ExportRow row = BuildRow(result, nodes[n]);
            json.WriteStartObject();
            json.WriteString("path", row.Path);
            json.WriteString("name", row.Name);
            json.WriteString("type", row.IsDirectory ? "Folder" : "File");
            if (!row.IsDirectory)
            {
                json.WriteString("extension", row.Extension);
                json.WriteString("category", row.Category);
            }

            json.WriteNumber("sizeBytes", row.SizeBytes);
            json.WriteNumber("allocatedBytes", row.AllocatedBytes);
            json.WriteString("parentFolder", row.ParentFolder);
            if (row.LastModifiedUtc is { } modified)
            {
                json.WriteString("lastModifiedUtc", modified);
            }

            if (row.IsDirectory)
            {
                json.WriteNumber("fileCount", row.FileCount);
                json.WriteNumber("folderCount", row.FolderCount);
            }

            json.WriteNumber("percentOfDrive", Math.Round(row.PercentOfDrive, 4));
            json.WriteNumber("percentOfParent", Math.Round(row.PercentOfParent, 4));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        progress?.Report(1);
        return new ExportSummary(path, nodes.Count, false, null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    /// <summary>0..n-1 without allocating an array.</summary>
    private sealed class RangeList(int count) : IReadOnlyList<int>
    {
        public int this[int index] => index;

        public int Count => count;

        public IEnumerator<int> GetEnumerator() => Enumerable.Range(0, count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
