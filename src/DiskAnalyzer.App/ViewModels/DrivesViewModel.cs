using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Infrastructure.Settings;
using DiskAnalyzer.Scanner.Providers.Mft;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>One drive card. Capacity figures are always live from Windows, never from history.</summary>
public sealed partial class DriveCardViewModel(DriveInfoModel drive, DrivesViewModel owner) : ObservableObject
{
    public DriveInfoModel Drive { get; } = drive;

    public string Title => Drive.DisplayName;

    public string Letter => Drive.Letter;

    public string Subtitle => Drive.IsReady
        ? $"{Drive.KindDescription} · {Drive.FileSystem}{(Drive.BusType is { } bus && bus != "Virtual" ? " · " + bus : string.Empty)}"
        : $"{Drive.KindDescription} · not ready";

    public bool IsReady => Drive.IsReady;

    public double UsedRatio => Drive.UtilizationPercent / 100;

    public string UsedText => SizeFormatter.Format(Drive.UsedBytes);

    public string FreeText => SizeFormatter.Format(Drive.FreeBytes);

    public string TotalText => SizeFormatter.Format(Drive.TotalBytes);

    public string UtilizationText => Drive.IsReady ? $"{Drive.UtilizationPercent:0.00}% used" : string.Empty;

    public string UsageLine => Drive.IsReady ? $"{UsedText} used of {TotalText} · {FreeText} free" : "Insert media or reconnect the drive to scan it.";

    public bool IsNearlyFull => Drive.IsReady && Drive.UtilizationPercent >= 90;

    public string Glyph => Drive.Kind switch
    {
        DriveKind.Network => "",
        DriveKind.Removable => "",
        DriveKind.CdRom => "",
        _ when Drive.IsExternal => "",
        _ => "",
    };

    public bool CanUseMft => Drive.IsNtfs && Drive.Kind is DriveKind.Fixed or DriveKind.Removable;

    [ObservableProperty]
    public partial ScanHistory? CachedScan { get; set; }

    public string? CachedText => CachedScan is null ? null : $"Last scan {CachedScan.ScannedUtc.ToLocalTime():g} · {SizeFormatter.Format(CachedScan.TotalBytes)}";

    partial void OnCachedScanChanged(ScanHistory? value) => OnPropertyChanged(nameof(CachedText));

    [RelayCommand]
    private Task Scan() => owner.ScanDriveAsync(this);

    [RelayCommand]
    private Task OpenCached() => owner.OpenCachedAsync(this);
}

public sealed partial class DrivesViewModel : PageViewModel
{
    private readonly IDriveDiscoveryService _discovery;
    private readonly IScanCacheService _cache;
    private readonly ISettingsService _settings;
    private readonly ScanLauncher _launcher;
    private readonly ShellService _shell;
    private readonly ILogger<DrivesViewModel> _logger;

    public DrivesViewModel(ScanSessionService session, IDriveDiscoveryService discovery, IScanCacheService cache, ISettingsService settings, ScanLauncher launcher, ShellService shell, ILogger<DrivesViewModel> logger)
        : base(session)
    {
        _discovery = discovery;
        _cache = cache;
        _settings = settings;
        _launcher = launcher;
        _shell = shell;
        _logger = logger;
        session.HistoryChanged += async (_, _) => await LoadCachedInfoAsync().ConfigureAwait(true);
        _ = RefreshAsync();
    }

    public override string Title => "Drives";

    public override PageKind Kind => PageKind.Drives;

    public ObservableCollection<DriveCardViewModel> Drives { get; } = [];

    public bool IsElevated => MftScanner.IsProcessElevated;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    public string ScanModeText => IsElevated
        ? (_settings.Current.UseMftWhenAvailable
            ? "Running as administrator: whole NTFS volumes are scanned by reading the Master File Table (fastest)."
            : "Running as administrator, but MFT scanning is turned off in Settings.")
        : "Using standard folder enumeration. Restart as administrator to read the NTFS Master File Table directly, which is much faster for whole drives.";

    [RelayCommand]
    private async Task Refresh()
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<DriveInfoModel> drives = await _discovery.GetDrivesAsync().ConfigureAwait(true);
            Drives.Clear();
            foreach (DriveInfoModel d in drives)
            {
                Drives.Add(new DriveCardViewModel(d, this));
            }

            var ready = drives.Where(d => d.IsReady).ToList();
            long total = ready.Sum(d => d.TotalBytes), used = ready.Sum(d => d.UsedBytes);
            Summary = $"{drives.Count} drives · {SizeFormatter.Format(used)} used of {SizeFormatter.Format(total)} across {ready.Count} ready drives";
            await LoadCachedInfoAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drive discovery failed");
            Summary = "Drives could not be listed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    private async Task LoadCachedInfoAsync()
    {
        if (!_settings.Current.OfferCachedResults)
        {
            foreach (DriveCardViewModel card in Drives)
            {
                card.CachedScan = null;
            }

            return;
        }

        foreach (DriveCardViewModel card in Drives.Where(c => c.IsReady))
        {
            try
            {
                card.CachedScan = await _cache.GetLatestForRootAsync(card.Drive.DriveId, card.Drive.RootPath).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read cached scan for {Drive}", card.Drive.RootPath);
            }
        }
    }

    internal Task ScanDriveAsync(DriveCardViewModel card) => _launcher.StartAsync(card.Drive.RootPath, card.Drive);

    internal async Task OpenCachedAsync(DriveCardViewModel card)
    {
        if (card.CachedScan is null)
        {
            return;
        }

        try
        {
            await Session.OpenStoredAsync(card.CachedScan.ScanId, ScanSource.Cached).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open cached scan");
            _shell.ShowError("The cached scan could not be opened: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ScanFolder()
    {
        string? folder = _shell.PickFolder();
        if (folder is not null)
        {
            await _launcher.StartAsync(folder).ConfigureAwait(true);
        }
    }

    public override void OnNavigatedTo(object? parameter) => OnPropertyChanged(nameof(ScanModeText));
}
