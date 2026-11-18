using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

public sealed class DuplicateGroupViewModel(DuplicateGroup group, IReadOnlyList<NodeItemViewModel> items)
{
    public DuplicateGroup Group { get; } = group;

    public IReadOnlyList<NodeItemViewModel> Items { get; } = items;

    public string Header => $"{Items.Count} copies of {Items[0].Name} · {SizeFormatter.Format(Group.SizeBytes)} each · {SizeFormatter.Format(Group.WastedBytes)} reclaimable";
}

public sealed partial class LargestFilesViewModel : PageViewModel
{
    private readonly IStorageAnalysisService _analysis;
    private readonly IDuplicateFinder _duplicates;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ExportCoordinator _export;
    private readonly ILogger<LargestFilesViewModel> _logger;
    private CancellationTokenSource? _duplicateCts;

    public LargestFilesViewModel(ScanSessionService session, IStorageAnalysisService analysis, IDuplicateFinder duplicates, NavigationService navigation, ShellService shell, ExportCoordinator export, ILogger<LargestFilesViewModel> logger)
        : base(session)
    {
        _analysis = analysis;
        _duplicates = duplicates;
        _navigation = navigation;
        _shell = shell;
        _export = export;
        _logger = logger;
    }

    public override string Title => "Largest Files";

    public override PageKind Kind => PageKind.LargestFiles;

    public IReadOnlyList<int> CountOptions { get; } = [20, 50, 100, 500, 1000];

    [ObservableProperty]
    public partial int SelectedCount { get; set; } = 20;

    [ObservableProperty]
    public partial IReadOnlyList<NodeItemViewModel> Files { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NodeItemViewModel> Folders { get; private set; } = [];

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial string? DuplicateMinSize { get; set; } = "10 MB";

    [ObservableProperty]
    public partial bool IsFindingDuplicates { get; private set; }

    [ObservableProperty]
    public partial double DuplicateProgress { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<DuplicateGroupViewModel> DuplicateGroups { get; private set; } = [];

    [ObservableProperty]
    public partial string? DuplicateSummary { get; private set; }

    partial void OnSelectedCountChanged(int value) => Reload();

    protected override void OnResultChanged()
    {
        CancelDuplicates();
        DuplicateGroups = [];
        DuplicateSummary = null;
        Reload();
    }

    private void Reload()
    {
        ScanResult? r = Session.Current;
        if (r is null)
        {
            Files = [];
            Folders = [];
            return;
        }

        Files = _analysis.GetLargestFiles(r.Tree, SelectedCount).Select(i => new NodeItemViewModel(r.Tree, i, r.DriveCapacityBytes)).ToList();
        Folders = _analysis.GetLargestFolders(r.Tree, SelectedCount).Select(i => new NodeItemViewModel(r.Tree, i, r.DriveCapacityBytes)).ToList();
    }

    [RelayCommand]
    private void ShowLocation(NodeItemViewModel? item)
    {
        if (item is not null)
        {
            _navigation.Navigate(PageKind.Explorer, item.Index);
        }
    }

    [RelayCommand]
    private void ShowInHeatmap(NodeItemViewModel? item)
    {
        if (item is not null)
        {
            _navigation.Navigate(PageKind.Heatmap, item.Index);
        }
    }

    [RelayCommand]
    private void ShowInWindowsExplorer(NodeItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.ShowInExplorer(item.FullPath);
        }
    }

    [RelayCommand]
    private void CopyPath(NodeItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.CopyToClipboard(item.FullPath);
        }
    }

    [RelayCommand]
    private Task ExportTopFiles(string? format) => ExportAsync(ExportScope.TopFiles, format);

    [RelayCommand]
    private Task ExportTopFolders(string? format) => ExportAsync(ExportScope.TopFolders, format);

    private Task ExportAsync(ExportScope scope, string? format)
    {
        ScanResult? r = Session.Current;
        if (r is null)
        {
            return Task.CompletedTask;
        }

        ExportFormat preferred = format switch { "xlsx" => ExportFormat.Excel, "json" => ExportFormat.Json, _ => ExportFormat.Csv };
        return _export.ExportAsync(r, preferred, scope, SelectedCount);
    }

    [RelayCommand]
    private async Task FindDuplicates()
    {
        ScanResult? r = Session.Current;
        if (r is null || IsFindingDuplicates)
        {
            return;
        }

        if (!SizeFormatter.TryParse(DuplicateMinSize, out long minSize))
        {
            DuplicateSummary = "Enter a minimum size such as 10 MB.";
            return;
        }

        if (r.Source != ScanSource.Live)
        {
            DuplicateSummary = "Duplicate detection reads file contents, so it needs a live scan. Rescan the drive first.";
            return;
        }

        _duplicateCts = new CancellationTokenSource();
        IsFindingDuplicates = true;
        DuplicateProgress = 0;
        DuplicateSummary = "Comparing files with identical sizes…";
        try
        {
            var progress = new Progress<double>(p => DuplicateProgress = p * 100);
            IReadOnlyList<DuplicateGroup> groups = await _duplicates.FindAsync(r.Tree, Math.Max(1, minSize), progress, _duplicateCts.Token).ConfigureAwait(true);
            DuplicateGroups = groups.Take(500).Select(g => new DuplicateGroupViewModel(g, g.Nodes.Select(n => new NodeItemViewModel(r.Tree, n, r.DriveCapacityBytes)).ToList())).ToList();
            long wasted = groups.Sum(g => g.WastedBytes);
            DuplicateSummary = groups.Count == 0
                ? $"No duplicate files of {SizeFormatter.Format(minSize)} or more were found."
                : $"{groups.Count:N0} sets of identical files · {SizeFormatter.Format(wasted)} could be reclaimed by keeping one copy of each. Nothing is deleted by this tool."
                  + (groups.Count > 500 ? " Showing the 500 largest sets." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            DuplicateSummary = "Duplicate search cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duplicate search failed");
            DuplicateSummary = "Duplicate search failed: " + ex.Message;
        }
        finally
        {
            IsFindingDuplicates = false;
            _duplicateCts?.Dispose();
            _duplicateCts = null;
        }
    }

    [RelayCommand]
    private void CancelDuplicates() => _duplicateCts?.Cancel();
}
