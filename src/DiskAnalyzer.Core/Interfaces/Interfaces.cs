using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Threading;

namespace DiskAnalyzer.Core.Interfaces;

/// <summary>Discovers the drives available on this machine.</summary>
public interface IDriveDiscoveryService
{
    Task<IReadOnlyList<DriveInfoModel>> GetDrivesAsync(CancellationToken cancellationToken = default);

    Task<DriveInfoModel?> GetDriveForPathAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Result of checking whether a scanner can handle a configuration.</summary>
public readonly record struct ScannerAvailability(bool IsAvailable, string Reason)
{
    public static ScannerAvailability Available { get; } = new(true, string.Empty);

    public static ScannerAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>A scanning strategy (standard enumeration, NTFS MFT…). Independent of any UI.</summary>
public interface IFileSystemScanner
{
    string Name { get; }

    /// <summary>Higher priority providers are tried first.</summary>
    int Priority { get; }

    ScannerAvailability CheckAvailability(ScanConfiguration configuration);

    /// <summary>
    /// Scans and returns a non-aggregated tree. On cancellation the scanner stops promptly and
    /// returns what it has so far, with the root flagged <see cref="NodeFlags.Partial"/>, instead of throwing.
    /// </summary>
    Task<ScanTree> ScanAsync(ScanConfiguration configuration, ScanProgressTracker progress, AsyncPauseGate pauseGate, CancellationToken cancellationToken);
}

/// <summary>Runs scans: picks the provider, handles pause/resume/cancel and produces a <see cref="ScanResult"/>.</summary>
public interface IScanOrchestrator
{
    ScanState State { get; }

    bool IsBusy { get; }

    event EventHandler<ScanState>? StateChanged;

    ScanProgress GetProgress();

    /// <summary>Runs a scan. Cancellation (via <see cref="Cancel"/> or the token) returns a partial result instead of throwing.</summary>
    Task<ScanResult> ScanAsync(ScanConfiguration configuration, CancellationToken cancellationToken = default);

    void Pause();

    void Resume();

    void Cancel();
}

/// <summary>Computes folder totals from leaf sizes.</summary>
public interface IFolderAggregationService
{
    void Aggregate(ScanTree tree, CancellationToken cancellationToken = default);
}

/// <summary>Read-only analysis over a scanned tree.</summary>
public interface IStorageAnalysisService
{
    StorageStatistics GetStatistics(ScanTree tree);

    IReadOnlyList<int> GetLargestFiles(ScanTree tree, int count, int scopeNode = ScanTree.RootIndex);

    IReadOnlyList<int> GetLargestFolders(ScanTree tree, int count, int scopeNode = ScanTree.RootIndex);

    IReadOnlyList<ExtensionStatistic> GetExtensionStatistics(ScanTree tree, int scopeNode = ScanTree.RootIndex);

    IReadOnlyList<CategoryStatistic> GetCategoryStatistics(ScanTree tree, int scopeNode = ScanTree.RootIndex);

    Task<SearchResult> SearchAsync(ScanTree tree, SearchQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Squarified treemap layout.</summary>
public interface ITreemapLayoutEngine
{
    TreemapLayout Layout(ScanTree tree, int rootIndex, TreemapRect bounds, TreemapLayoutOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Renders a layout into a 32-bit BGRA pixel buffer.</summary>
public interface ITreemapRenderer
{
    void Render(TreemapLayout layout, int[] pixels, int width, int height, Func<TreemapNode, uint> colorSelector, CancellationToken cancellationToken = default);
}

/// <summary>Persists scans (history) and loads them back (cache/historical results).</summary>
public interface IScanCacheService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a scan. Files smaller than <paramref name="fileThresholdBytes"/> are not stored; folders always are.</summary>
    Task<long> SaveAsync(ScanResult result, long fileThresholdBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanHistory>> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task<ScanHistory?> GetLatestForRootAsync(string driveId, string rootPath, CancellationToken cancellationToken = default);

    Task<ScanResult> LoadAsync(long scanId, ScanSource source, CancellationToken cancellationToken = default);

    Task DeleteAsync(long scanId, CancellationToken cancellationToken = default);

    Task<ScanComparison> CompareAsync(long olderScanId, long newerScanId, CancellationToken cancellationToken = default);
}

public enum ExportFormat
{
    Csv,
    Excel,
    Json,
}

public enum ExportScope
{
    /// <summary>Every file and folder.</summary>
    AllItems,

    /// <summary>Folders only.</summary>
    FoldersOnly,

    /// <summary>The N largest files.</summary>
    TopFiles,

    /// <summary>The N largest folders.</summary>
    TopFolders,
}

public sealed record ExportOptions(string FilePath, ExportFormat Format, ExportScope Scope, int TopCount = 100);

public sealed record ExportSummary(string FilePath, long RowsWritten, bool Truncated, string? Note);

public interface IExportService
{
    Task<ExportSummary> ExportAsync(ScanResult result, ExportOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Finds files with identical content (read-only hashing).</summary>
public interface IDuplicateFinder
{
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(ScanTree tree, long minSizeBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
