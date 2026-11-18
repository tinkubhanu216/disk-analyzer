using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.Services;

/// <summary>
/// Holds the result currently shown in the app (live, cached or historical) and runs scans.
/// All pages read from here; switching results raises <see cref="ResultChanged"/> once.
/// </summary>
public sealed partial class ScanSessionService : ObservableObject
{
    private readonly IScanCacheService _cache;
    private readonly IStorageAnalysisService _analysis;
    private readonly ISettingsService _settings;
    private readonly IDriveDiscoveryService _drives;
    private readonly ILogger<ScanSessionService> _logger;

    public ScanSessionService(IScanOrchestrator orchestrator, IScanCacheService cache, IStorageAnalysisService analysis, ISettingsService settings, IDriveDiscoveryService drives, ILogger<ScanSessionService> logger)
    {
        Orchestrator = orchestrator;
        _cache = cache;
        _analysis = analysis;
        _settings = settings;
        _drives = drives;
        _logger = logger;
    }

    public IScanOrchestrator Orchestrator { get; }

    [ObservableProperty]
    public partial ScanResult? Current { get; private set; }

    [ObservableProperty]
    public partial StorageStatistics? Statistics { get; private set; }

    [ObservableProperty]
    public partial string? BackgroundStatus { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Current"/> changes.</summary>
    public event EventHandler? ResultChanged;

    /// <summary>Raised after scan history was written or deleted.</summary>
    public event EventHandler? HistoryChanged;

    public ScanConfiguration CreateConfiguration(string rootPath, DriveInfoModel? drive)
    {
        AppSettings s = _settings.Current;
        return new ScanConfiguration
        {
            RootPath = rootPath,
            Drive = drive,
            IncludeHidden = s.IncludeHidden,
            IncludeSystem = s.IncludeSystem,
            FollowReparsePoints = s.FollowReparsePoints,
            DeduplicateHardLinks = s.DeduplicateHardLinks,
            UseMftWhenAvailable = s.UseMftWhenAvailable,
            MaxConcurrency = s.MaxConcurrency,
        };
    }

    /// <summary>Scans a drive or folder. Returns null if a scan was already running.</summary>
    public async Task<ScanResult?> ScanAsync(string rootPath, DriveInfoModel? drive = null)
    {
        if (Orchestrator.IsBusy)
        {
            return null;
        }

        drive ??= await _drives.GetDriveForPathAsync(rootPath).ConfigureAwait(true);
        ScanResult result = await Orchestrator.ScanAsync(CreateConfiguration(rootPath, drive)).ConfigureAwait(true);
        await SetResultAsync(result).ConfigureAwait(true);

        if (_settings.Current.SaveScanHistory && result.Status is ScanStatus.Completed or ScanStatus.CompletedWithErrors)
        {
            _ = SaveHistoryAsync(result);
        }

        return result;
    }

    public async Task OpenStoredAsync(long scanId, ScanSource source)
    {
        BackgroundStatus = "Loading stored scan…";
        try
        {
            ScanResult result = await _cache.LoadAsync(scanId, source).ConfigureAwait(true);
            await SetResultAsync(result).ConfigureAwait(true);
        }
        finally
        {
            BackgroundStatus = null;
        }
    }

    public async Task SetResultAsync(ScanResult result)
    {
        StorageStatistics stats = await Task.Run(() => _analysis.GetStatistics(result.Tree)).ConfigureAwait(true);
        Statistics = stats;
        Current = result;
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyHistoryChanged() => HistoryChanged?.Invoke(this, EventArgs.Empty);

    private async Task SaveHistoryAsync(ScanResult result)
    {
        BackgroundStatus = "Saving scan to history…";
        try
        {
            await Task.Run(() => _cache.SaveAsync(result, _settings.Current.HistoryFileThresholdBytes)).ConfigureAwait(true);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving scan history failed");
        }
        finally
        {
            BackgroundStatus = null;
        }
    }
}

/// <summary>Pages of the main window.</summary>
public enum PageKind
{
    Overview,
    Drives,
    Explorer,
    Heatmap,
    LargestFiles,
    FileTypes,
    History,
    Settings,
    Search,
}

/// <summary>Decouples pages from the shell: any page can ask to show another page with a parameter.</summary>
public sealed class NavigationService
{
    public event EventHandler<(PageKind Page, object? Parameter)>? NavigationRequested;

    public void Navigate(PageKind page, object? parameter = null) =>
        Application.Current.Dispatcher.Invoke(() => NavigationRequested?.Invoke(this, (page, parameter)));
}
