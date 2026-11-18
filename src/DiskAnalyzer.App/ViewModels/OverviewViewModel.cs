using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Controls;
using DiskAnalyzer.App.Converters;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Algorithms;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

public sealed record StatTile(string Label, string Value, string? Detail, string Glyph);

public sealed record CategoryRow(FileCategory Category, long Bytes, long Files, double Ratio)
{
    public string Name => Category.ToString();

    public string SizeText => SizeFormatter.Format(Bytes);

    public string PercentText => SizeFormatter.FormatPercent(Ratio * 100);

    public string FilesText => $"{Files:N0} files";

    public System.Windows.Media.Brush Brush => CategoryBrushConverter.GetBrush(Category);
}

public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly IStorageAnalysisService _analysis;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ScanLauncher _launcher;

    public OverviewViewModel(ScanSessionService session, DrivesViewModel drives, IStorageAnalysisService analysis, NavigationService navigation, ShellService shell, ScanLauncher launcher, ThemeService theme)
        : base(session)
    {
        Drives = drives;
        _analysis = analysis;
        _navigation = navigation;
        _shell = shell;
        _launcher = launcher;
        IsDarkTheme = theme.IsDark;
        theme.ThemeChanged += (_, _) => IsDarkTheme = theme.IsDark;
    }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; private set; }

    /// <summary>Mini treemap click: open the heatmap at the clicked item.</summary>
    public void OnTreemapClick(int treeIndex) => _navigation.Navigate(PageKind.Heatmap, treeIndex);

    public override string Title => "Overview";

    public override PageKind Kind => PageKind.Overview;

    public DrivesViewModel Drives { get; }

    public ObservableCollection<StatTile> Tiles { get; } = [];

    public ObservableCollection<NodeItemViewModel> TopFolders { get; } = [];

    public ObservableCollection<NodeItemViewModel> LargestFiles { get; } = [];

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartSegment>? CategorySegments { get; private set; }

    [ObservableProperty]
    public partial ScanTree? Tree { get; private set; }

    [ObservableProperty]
    public partial long DriveCapacity { get; private set; }

    [ObservableProperty]
    public partial string? UsageComparison { get; private set; }

    [ObservableProperty]
    public partial string? ScannedTotalText { get; private set; }

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedFile { get; set; }

    public string DifferenceExplanation =>
        "Folder totals rarely match the used space Windows reports for a drive. The difference comes from NTFS metadata " +
        "(the Master File Table, journal and security descriptors), System Restore shadow copies, reserved space, " +
        "folders that could not be read without administrator rights, hard links (Windows counts their data once, " +
        "Explorer-style totals count each name), compression and sparse files (logical size vs. size on disk), " +
        "and cloud placeholders that occupy little or no local space.";

    partial void OnSelectedFolderChanged(NodeItemViewModel? value)
    {
        if (value is not null)
        {
            _navigation.Navigate(PageKind.Explorer, value.Index);
            SelectedFolder = null;
        }
    }

    partial void OnSelectedFileChanged(NodeItemViewModel? value)
    {
        if (value is not null)
        {
            _navigation.Navigate(PageKind.Explorer, value.Index);
            SelectedFile = null;
        }
    }

    protected override void OnResultChanged()
    {
        ScanResult? result = Session.Current;
        StorageStatistics? stats = Session.Statistics;
        Tiles.Clear();
        TopFolders.Clear();
        LargestFiles.Clear();
        Categories.Clear();
        Tree = result?.Tree;
        DriveCapacity = result?.DriveCapacityBytes ?? 0;
        if (result is null || stats is null)
        {
            return;
        }

        ScanTree tree = result.Tree;
        long capacity = result.DriveCapacityBytes;
        Tiles.Add(new StatTile("Total scanned size", SizeFormatter.Format(stats.TotalBytes), $"{SizeFormatter.Format(stats.TotalAllocatedBytes)} on disk", ""));
        Tiles.Add(new StatTile("Files", SizeFormatter.FormatCount(stats.FileCount), stats.HardLinkDuplicateCount > 0 ? $"{stats.HardLinkDuplicateCount:N0} hard links counted once" : $"{stats.ExtensionCount:N0} file types", ""));
        Tiles.Add(new StatTile("Folders", SizeFormatter.FormatCount(stats.FolderCount), stats.InaccessibleFolderCount > 0 ? $"{stats.InaccessibleFolderCount:N0} inaccessible" : "all readable", ""));
        if (stats.LargestFolderIndex >= 0)
        {
            Tiles.Add(new StatTile("Largest folder", SizeFormatter.Format(tree.GetSize(stats.LargestFolderIndex)), tree.GetFullPath(stats.LargestFolderIndex), ""));
        }

        if (stats.LargestFileIndex >= 0)
        {
            Tiles.Add(new StatTile("Largest file", SizeFormatter.Format(tree.GetSize(stats.LargestFileIndex)), tree.GetFullPath(stats.LargestFileIndex), ""));
        }

        foreach (int child in TreeAlgorithms.GetChildrenBySize(tree, ScanTree.RootIndex).Where(tree.IsDirectory).Take(12))
        {
            TopFolders.Add(new NodeItemViewModel(tree, child, capacity));
        }

        foreach (int file in _analysis.GetLargestFiles(tree, 8))
        {
            LargestFiles.Add(new NodeItemViewModel(tree, file, capacity));
        }

        IReadOnlyList<CategoryStatistic> categories = _analysis.GetCategoryStatistics(tree);
        long total = Math.Max(1, categories.Sum(c => c.TotalBytes));
        foreach (CategoryStatistic c in categories)
        {
            Categories.Add(new CategoryRow(c.Category, c.TotalBytes, c.FileCount, (double)c.TotalBytes / total));
        }

        CategorySegments = Categories.Select(c => new ChartSegment(c.Name, c.Bytes, c.Brush)).ToList();
        ScannedTotalText = SizeFormatter.Format(stats.TotalBytes);

        if (result.Drive is { TotalBytes: > 0 } drive && result.IsDriveRootScan())
        {
            long used = drive.UsedBytes;
            long diff = used - stats.TotalAllocatedBytes;
            UsageComparison = $"Windows reports {SizeFormatter.Format(used)} used on {drive.Letter} (at scan time). " +
                $"Scanned files total {SizeFormatter.Format(stats.TotalBytes)} ({SizeFormatter.Format(stats.TotalAllocatedBytes)} on disk). " +
                (diff >= 0 ? $"{SizeFormatter.Format(diff)} is not attributed to scanned files." : $"Scanned size on disk exceeds used space by {SizeFormatter.Format(-diff)} (hard links or compression counted at full size).");
        }
        else
        {
            UsageComparison = result.Drive is { TotalBytes: > 0 } d
                ? $"This folder uses {SizeFormatter.FormatPercent(SizeFormatter.Percent(stats.TotalBytes, d.TotalBytes))} of {d.Letter} ({SizeFormatter.Format(d.TotalBytes)})."
                : null;
        }
    }

    [RelayCommand]
    private void Visualize() => _navigation.Navigate(PageKind.Heatmap, ScanTree.RootIndex);

    [RelayCommand]
    private void OpenExplorer() => _navigation.Navigate(PageKind.Explorer, ScanTree.RootIndex);

    [RelayCommand]
    private void ShowInWindowsExplorer(NodeItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.ShowInExplorer(item.FullPath);
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

    [RelayCommand]
    private void GoToDrives() => _navigation.Navigate(PageKind.Drives);
}

internal static class ScanResultExtensions
{
    public static bool IsDriveRootScan(this ScanResult result) =>
        result.Drive is not null && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(result.RootPath), System.IO.Path.TrimEndingDirectorySeparator(result.Drive.RootPath), StringComparison.OrdinalIgnoreCase);
}
