using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Scanner.Providers.Mft;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

public sealed record NavItem(PageKind Kind, string Label, string Glyph);

/// <summary>Shell: navigation, global search, scan progress, result banner and export.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<PageKind, PageViewModel> _pages;
    private readonly ScanSessionService _session;
    private readonly ScanLauncher _launcher;
    private readonly ShellService _shell;
    private readonly ExportCoordinator _export;
    private readonly DispatcherTimer _progressTimer;
    private bool _suppressNavSync;

    public MainViewModel(
        IEnumerable<PageViewModel> pages,
        ScanSessionService session,
        ScanLauncher launcher,
        NavigationService navigation,
        ShellService shell,
        ExportCoordinator export)
    {
        _pages = pages.ToDictionary(p => p.Kind);
        _session = session;
        _launcher = launcher;
        _shell = shell;
        _export = export;
        export.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ExportStatus));

        NavItems =
        [
            new(PageKind.Overview, "Overview", ""),
            new(PageKind.Drives, "Drives", ""),
            new(PageKind.Explorer, "Folder Explorer", ""),
            new(PageKind.Heatmap, "Storage Heatmap", ""),
            new(PageKind.LargestFiles, "Largest Files", ""),
            new(PageKind.FileTypes, "File Types", ""),
            new(PageKind.History, "Scan History", ""),
            new(PageKind.Settings, "Settings", ""),
        ];

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        session.Orchestrator.StateChanged += (_, state) => Application.Current.Dispatcher.BeginInvoke(() => OnScanStateChanged(state));
        session.ResultChanged += (_, _) => UpdateResultBanner();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanSessionService.BackgroundStatus))
            {
                OnPropertyChanged(nameof(BackgroundStatus));
            }
        };
        navigation.NavigationRequested += (_, request) => Navigate(request.Page, request.Parameter);

        IsElevated = MftScanner.IsProcessElevated;
        Navigate(PageKind.Overview, null);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public bool IsElevated { get; }

    public string WindowTitle => IsElevated ? "Disk Analyzer (Administrator)" : "Disk Analyzer";

    public string? BackgroundStatus => _session.BackgroundStatus;

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; private set; }

    [ObservableProperty]
    public partial NavItem? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial string? GlobalSearchText { get; set; }

    // ---------------------------------------------------------------- scan progress

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string? ProgressHeadline { get; private set; }

    [ObservableProperty]
    public partial string? ProgressPath { get; private set; }

    [ObservableProperty]
    public partial string? ProgressDetails { get; private set; }

    // ---------------------------------------------------------------- result banner

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    public partial string? ResultSourceText { get; private set; }

    [ObservableProperty]
    public partial string? ResultSummary { get; private set; }

    [ObservableProperty]
    public partial bool ResultIsStale { get; private set; }

    [ObservableProperty]
    public partial string? ResultWarning { get; private set; }

    public string? ExportStatus => _export.Status;

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (!_suppressNavSync && value is not null)
        {
            Navigate(value.Kind, null);
        }
    }

    partial void OnGlobalSearchTextChanged(string? value)
    {
        if (_pages.TryGetValue(PageKind.Search, out PageViewModel? page) && page is SearchViewModel search)
        {
            search.QueryText = value;
            if (!string.IsNullOrWhiteSpace(value) && CurrentPage != search)
            {
                Navigate(PageKind.Search, null);
            }
        }
    }

    public void Navigate(PageKind kind, object? parameter)
    {
        if (!_pages.TryGetValue(kind, out PageViewModel? page))
        {
            return;
        }

        CurrentPage = page;
        _suppressNavSync = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.Kind == kind);
        _suppressNavSync = false;
        page.OnNavigatedTo(parameter);
    }

    [RelayCommand]
    private void PauseOrResume()
    {
        if (_session.Orchestrator.State == ScanState.Paused)
        {
            _session.Orchestrator.Resume();
        }
        else
        {
            _session.Orchestrator.Pause();
        }
    }

    [RelayCommand]
    private void CancelScan() => _session.Orchestrator.Cancel();

    [RelayCommand]
    private Task Rescan() => _launcher.RescanCurrentAsync();

    [RelayCommand]
    private async Task Refresh()
    {
        if (_session.Current is not null && !_session.Orchestrator.IsBusy && _session.Current.Source == ScanSource.Live)
        {
            await _launcher.RescanCurrentAsync().ConfigureAwait(true);
        }
        else if (_pages[PageKind.Drives] is DrivesViewModel drives)
        {
            await drives.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenSettings() => Navigate(PageKind.Settings, null);

    [RelayCommand]
    private void RestartElevated()
    {
        if (!_shell.RestartAsAdministrator())
        {
            _shell.ShowInfo("Disk Analyzer is still running without administrator rights. Standard scanning will be used.");
        }
    }

    /// <summary>Parameter: "{csv|xlsx|json}:{all|folders|topfiles|topfolders}".</summary>
    [RelayCommand]
    private async Task Export(string? spec)
    {
        ScanResult? result = _session.Current;
        if (result is null)
        {
            return;
        }

        string[] parts = (spec ?? "csv:all").Split(':');
        ExportFormat preferred = parts[0] switch { "xlsx" => ExportFormat.Excel, "json" => ExportFormat.Json, _ => ExportFormat.Csv };
        ExportScope scope = parts.Length > 1 ? parts[1] switch
        {
            "folders" => ExportScope.FoldersOnly,
            "topfiles" => ExportScope.TopFiles,
            "topfolders" => ExportScope.TopFolders,
            _ => ExportScope.AllItems,
        } : ExportScope.AllItems;
        await _export.ExportAsync(result, preferred, scope, 100).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- progress polling

    private void OnScanStateChanged(ScanState state)
    {
        bool busy = _session.Orchestrator.IsBusy;
        IsScanning = busy;
        IsPaused = state == ScanState.Paused;
        if (busy)
        {
            _progressTimer.Start();
            UpdateProgress();
        }
        else
        {
            _progressTimer.Stop();
            UpdateProgress();
        }
    }

    private void UpdateProgress()
    {
        ScanProgress p = _session.Orchestrator.GetProgress();
        IsProgressIndeterminate = p.Fraction is null || p.State is ScanState.Preparing or ScanState.Aggregating;
        ProgressPercent = (p.Fraction ?? 0) * 100;
        string state = p.State switch
        {
            ScanState.Preparing => "Preparing scan",
            ScanState.Paused => "Paused",
            ScanState.Aggregating => "Calculating folder sizes",
            ScanState.Scanning => string.IsNullOrEmpty(p.Phase) ? "Scanning" : p.Phase,
            _ => p.State.ToString(),
        };
        ProgressHeadline = p.Fraction is { } f && p.State == ScanState.Scanning ? $"{state} · {f:P0}" : state;
        ProgressPath = p.CurrentPath;
        string eta = p.EstimatedRemaining is { } r ? $" · about {SizeFormatter.FormatDuration(r)} left" : string.Empty;
        string errors = p.ErrorCount > 0 ? $" · {p.ErrorCount:N0} inaccessible" : string.Empty;
        ProgressDetails = $"{p.FilesScanned:N0} files · {p.FoldersScanned:N0} folders · {SizeFormatter.Format(p.BytesDiscovered)} found · {SizeFormatter.FormatDuration(p.Elapsed)}{eta}{errors}"
            + (p.ProviderName is null ? string.Empty : $" · {p.ProviderName}");
    }

    private void UpdateResultBanner()
    {
        ScanResult? r = _session.Current;
        HasResult = r is not null;
        if (r is null)
        {
            return;
        }

        ResultIsStale = r.Source != ScanSource.Live;
        ResultSourceText = r.Source switch
        {
            ScanSource.Live => r.Status == ScanStatus.Cancelled ? "Live scan · cancelled (partial)" : "Live scan",
            ScanSource.Cached => "Cached result",
            _ => "Historical scan",
        };
        string age = r.Source == ScanSource.Live ? $"completed {r.StartedUtc.ToLocalTime():t}" : $"from {r.StartedUtc.ToLocalTime():g}";
        ResultSummary = $"{r.RootPath} · {SizeFormatter.Format(r.TotalBytes)} in {r.TotalFiles:N0} files · {age} · {SizeFormatter.FormatDuration(r.Duration)} · {r.ProviderName}";

        var warnings = new List<string>();
        if (r.Source != ScanSource.Live)
        {
            warnings.Add("Stored results may be out of date; drive usage shown elsewhere is always live.");
        }

        if (r.StoredFileThresholdBytes > 0)
        {
            warnings.Add($"Files under {SizeFormatter.Format(r.StoredFileThresholdBytes)} were not stored; folder totals are complete.");
        }

        if (r.Status == ScanStatus.Cancelled)
        {
            warnings.Add("The scan was cancelled, so totals are incomplete.");
        }

        if (r.ErrorCount > 0)
        {
            warnings.Add($"{r.ErrorCount:N0} folders could not be read (access denied or in use).");
        }

        if (r.ProviderNote is not null && r.Source == ScanSource.Live)
        {
            warnings.Add(r.ProviderNote);
        }

        ResultWarning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
    }
}
