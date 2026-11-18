using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

public sealed partial class HistoryRowViewModel(ScanHistory scan) : ObservableObject
{
    public ScanHistory Scan { get; } = scan;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public string DateText => Scan.ScannedUtc.ToLocalTime().ToString("g");

    public string SizeText => SizeFormatter.Format(Scan.TotalBytes);

    public string FilesText => $"{Scan.FileCount:N0}";

    public string FoldersText => $"{Scan.FolderCount:N0}";

    public string DurationText => SizeFormatter.FormatDuration(Scan.Duration);

    public string StatusText => Scan.Status switch
    {
        ScanStatus.CompletedWithErrors => $"Completed · {Scan.ErrorCount:N0} unreadable",
        _ => Scan.Status.ToString(),
    };

    public string StoredText => Scan.StoredFileThresholdBytes > 0 ? $"files ≥ {SizeFormatter.Format(Scan.StoredFileThresholdBytes)}" : "all files";
}

public sealed class ChangeRowViewModel(ChangedItem item)
{
    public ChangedItem Item { get; } = item;

    public string Path => Item.Path;

    public string OldText => Item.Kind == ChangeKind.Added ? "—" : SizeFormatter.Format(Item.OldSizeBytes);

    public string NewText => Item.Kind == ChangeKind.Removed ? "—" : SizeFormatter.Format(Item.NewSizeBytes);

    public string DeltaText => SizeFormatter.FormatSigned(Item.DeltaBytes);

    public bool IsIncrease => Item.DeltaBytes > 0;
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IScanCacheService _cache;
    private readonly ShellService _shell;
    private readonly NavigationService _navigation;
    private readonly ILogger<HistoryViewModel> _logger;

    public HistoryViewModel(ScanSessionService session, IScanCacheService cache, ShellService shell, NavigationService navigation, ILogger<HistoryViewModel> logger)
        : base(session)
    {
        _cache = cache;
        _shell = shell;
        _navigation = navigation;
        _logger = logger;
        session.HistoryChanged += async (_, _) => await RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    public override string Title => "Scan History";

    public override PageKind Kind => PageKind.History;

    public ObservableCollection<HistoryRowViewModel> Scans { get; } = [];

    [ObservableProperty]
    public partial HistoryRowViewModel? SelectedScan { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial ScanComparison? Comparison { get; private set; }

    [ObservableProperty]
    public partial string? ComparisonTitle { get; private set; }

    [ObservableProperty]
    public partial string? ComparisonSummary { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> AddedFiles { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> RemovedFiles { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> GrownFolders { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> ShrunkFolders { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> AddedFolders { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChangeRowViewModel> RemovedFolders { get; private set; } = [];

    public override void OnNavigatedTo(object? parameter) => _ = RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task Refresh()
    {
        try
        {
            IReadOnlyList<ScanHistory> rows = await _cache.GetHistoryAsync().ConfigureAwait(true);
            Scans.Clear();
            foreach (ScanHistory row in rows)
            {
                Scans.Add(new HistoryRowViewModel(row));
            }

            Status = rows.Count == 0
                ? "No scans stored yet. Completed scans are saved automatically (see Settings)."
                : $"{rows.Count} stored scans. Tick two scans of the same location and choose Compare.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read scan history");
            Status = "Scan history could not be read: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task Open(HistoryRowViewModel? row)
    {
        row ??= SelectedScan;
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Session.OpenStoredAsync(row.Scan.ScanId, ScanSource.Historical).ConfigureAwait(true);
            _navigation.Navigate(PageKind.Overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open scan {Id}", row.Scan.ScanId);
            _shell.ShowError("The stored scan could not be opened: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Delete(HistoryRowViewModel? row)
    {
        row ??= SelectedScan;
        if (row is null || !_shell.Confirm($"Delete the stored scan of {row.Scan.RootPath} from {row.DateText}?\n\nThis removes it from history only; no files on disk are touched."))
        {
            return;
        }

        try
        {
            await _cache.DeleteAsync(row.Scan.ScanId).ConfigureAwait(true);
            Scans.Remove(row);
            Session.NotifyHistoryChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed");
            _shell.ShowError("The scan could not be deleted: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task Compare()
    {
        var picked = Scans.Where(s => s.IsChecked).ToList();
        if (picked.Count != 2)
        {
            _shell.ShowInfo("Tick exactly two scans to compare.");
            return;
        }

        if (!string.Equals(picked[0].Scan.RootPath, picked[1].Scan.RootPath, StringComparison.OrdinalIgnoreCase)
            && !_shell.Confirm("These scans are of different locations, so most items will appear as added or removed. Compare anyway?"))
        {
            return;
        }

        IsBusy = true;
        Status = "Comparing scans…";
        try
        {
            ScanComparison c = await _cache.CompareAsync(picked[0].Scan.ScanId, picked[1].Scan.ScanId).ConfigureAwait(true);
            Comparison = c;
            ComparisonTitle = $"{c.Older.RootPath}: {c.Older.ScannedUtc.ToLocalTime():g} → {c.Newer.ScannedUtc.ToLocalTime():g}";
            ComparisonSummary = $"Total changed by {SizeFormatter.FormatSigned(c.TotalDeltaBytes)} · {c.TotalAddedFiles:N0} files added ({SizeFormatter.Format(c.AddedFileBytes)}) · "
                + $"{c.TotalRemovedFiles:N0} files removed ({SizeFormatter.Format(c.RemovedFileBytes)})"
                + (c.ComparedFileThresholdBytes > 0 ? $" · files under {SizeFormatter.Format(c.ComparedFileThresholdBytes)} were not stored and are not compared" : string.Empty);
            AddedFiles = c.AddedFiles.Select(i => new ChangeRowViewModel(i)).ToList();
            RemovedFiles = c.RemovedFiles.Select(i => new ChangeRowViewModel(i)).ToList();
            GrownFolders = c.GrownFolders.Select(i => new ChangeRowViewModel(i)).ToList();
            ShrunkFolders = c.ShrunkFolders.Select(i => new ChangeRowViewModel(i)).ToList();
            AddedFolders = c.AddedFolders.Select(i => new ChangeRowViewModel(i)).ToList();
            RemovedFolders = c.RemovedFolders.Select(i => new ChangeRowViewModel(i)).ToList();
            Status = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comparison failed");
            Status = "Comparison failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseComparison() => Comparison = null;
}
