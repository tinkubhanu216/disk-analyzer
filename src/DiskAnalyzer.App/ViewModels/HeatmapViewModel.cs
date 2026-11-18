using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Infrastructure.Settings;
using DiskAnalyzer.Visualization.Rendering;

namespace DiskAnalyzer.App.ViewModels;

public sealed record ColorModeOption(TreemapColorMode Value, string Label);

public sealed record SizeThresholdOption(long Bytes, string Label);

public sealed record LegendItem(string Label, System.Windows.Media.Brush Brush);

/// <summary>Interactive treemap ("Storage Heatmap") state: zoom root, display options, selection.</summary>
public sealed partial class HeatmapViewModel : PageViewModel
{
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public HeatmapViewModel(ScanSessionService session, NavigationService navigation, ShellService shell, ISettingsService settings, ThemeService theme)
        : base(session)
    {
        _navigation = navigation;
        _shell = shell;
        _settings = settings;
        _theme = theme;
        ColorModes =
        [
            new(TreemapColorMode.FileType, "File type"),
            new(TreemapColorMode.Age, "Age (last modified)"),
            new(TreemapColorMode.Size, "Size heat"),
        ];
        Thresholds =
        [
            new(0, "Show everything"),
            new(1L << 20, "Hide < 1 MB"),
            new(10L << 20, "Hide < 10 MB"),
            new(100L << 20, "Hide < 100 MB"),
            new(1L << 30, "Hide < 1 GB"),
        ];
        AppSettings s = settings.Current;
        SelectedColorMode = ColorModes.FirstOrDefault(m => m.Value.ToString() == s.TreemapColorMode) ?? ColorModes[0];
        SelectedThreshold = Thresholds[0];
        ShowLabels = s.TreemapShowLabels;
        IsDark = theme.IsDark;
        theme.ThemeChanged += (_, _) => IsDark = theme.IsDark;
        BuildLegend();
    }

    public override string Title => "Storage Heatmap";

    public override PageKind Kind => PageKind.Heatmap;

    public IReadOnlyList<ColorModeOption> ColorModes { get; }

    public IReadOnlyList<SizeThresholdOption> Thresholds { get; }

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<LegendItem> Legend { get; } = [];

    [ObservableProperty]
    public partial ScanTree? Tree { get; private set; }

    [ObservableProperty]
    public partial int RootNode { get; private set; }

    [ObservableProperty]
    public partial long DriveCapacity { get; private set; }

    [ObservableProperty]
    public partial ColorModeOption SelectedColorMode { get; set; }

    [ObservableProperty]
    public partial SizeThresholdOption SelectedThreshold { get; set; }

    [ObservableProperty]
    public partial bool ShowLabels { get; set; }

    [ObservableProperty]
    public partial bool HighlightLargestFiles { get; set; }

    [ObservableProperty]
    public partial bool HighlightLargestFolders { get; set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial bool IsDark { get; private set; }

    [ObservableProperty]
    public partial int SelectedNode { get; set; } = ScanTree.None;

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedItem { get; private set; }

    [ObservableProperty]
    public partial NodeItemViewModel? RootItem { get; private set; }

    [ObservableProperty]
    public partial string? RenderStats { get; set; }

    public bool CanZoomOut => RootNode > 0;

    /// <summary>Tiles smaller than this (pixels per side) are merged into "smaller items" (Settings).</summary>
    public double MinTilePixels => _settings.Current.TreemapMinTilePixels;

    partial void OnSelectedColorModeChanged(ColorModeOption value)
    {
        BuildLegend();
        AppSettings s = _settings.Current.Clone();
        s.TreemapColorMode = value.Value.ToString();
        _settings.Save(s);
    }

    partial void OnShowLabelsChanged(bool value)
    {
        AppSettings s = _settings.Current.Clone();
        s.TreemapShowLabels = value;
        _settings.Save(s);
    }

    partial void OnSelectedNodeChanged(int value)
    {
        ScanResult? r = Session.Current;
        SelectedItem = r is not null && value >= 0 && value < r.Tree.Count ? new NodeItemViewModel(r.Tree, value, r.DriveCapacityBytes) : null;
    }

    protected override void OnResultChanged()
    {
        ScanResult? r = Session.Current;
        Tree = r?.Tree;
        DriveCapacity = r?.DriveCapacityBytes ?? 0;
        SelectedNode = ScanTree.None;
        ZoomTo(ScanTree.RootIndex);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        if (parameter is int node && Session.Current?.Tree is { } tree && node >= 0 && node < tree.Count)
        {
            ZoomTo(tree.IsDirectory(node) ? node : tree.GetParent(node));
            if (!tree.IsDirectory(node))
            {
                SelectedNode = node;
            }
        }
    }

    public void ZoomTo(int node)
    {
        ScanResult? r = Session.Current;
        if (r is null || node < 0 || node >= r.Tree.Count || !r.Tree.IsDirectory(node))
        {
            Breadcrumbs.Clear();
            RootItem = null;
            return;
        }

        RootNode = node;
        RootItem = new NodeItemViewModel(r.Tree, node, r.DriveCapacityBytes);
        Breadcrumbs.Clear();
        foreach (int n in r.Tree.GetAncestry(node))
        {
            Breadcrumbs.Add(new BreadcrumbItem(n, n == ScanTree.RootIndex ? r.Tree.RootPath.TrimEnd('\\') : r.Tree.GetName(n)));
        }

        OnPropertyChanged(nameof(CanZoomOut));
    }

    /// <summary>Tile click: folders (and "smaller items" tiles) zoom in; files are selected.</summary>
    public void OnTileClicked(int treeIndex, bool isFolderLike)
    {
        if (Session.Current is null)
        {
            return;
        }

        if (isFolderLike)
        {
            ZoomTo(treeIndex);
            SelectedNode = treeIndex;
        }
        else
        {
            SelectedNode = treeIndex;
        }
    }

    /// <summary>Mouse wheel: in = one level towards the hovered tile, out = parent.</summary>
    public void OnZoomRequested(bool zoomIn, int hoveredIndex)
    {
        ScanTree? tree = Session.Current?.Tree;
        if (tree is null)
        {
            return;
        }

        if (!zoomIn)
        {
            ZoomOut();
            return;
        }

        if (hoveredIndex < 0 || hoveredIndex == RootNode)
        {
            return;
        }

        // Find the child of the current root on the path to the hovered node.
        int node = hoveredIndex;
        while (node >= 0 && tree.GetParent(node) != RootNode)
        {
            node = tree.GetParent(node);
        }

        if (node >= 0 && tree.IsDirectory(node))
        {
            ZoomTo(node);
        }
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (Session.Current is { } r && RootNode > 0)
        {
            int previous = RootNode;
            ZoomTo(r.Tree.GetParent(RootNode));
            SelectedNode = previous;
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ScanTree? tree = Session.Current?.Tree;
        if (tree is null || SelectedNode < 0)
        {
            return;
        }

        int target = tree.IsDirectory(SelectedNode) ? SelectedNode : tree.GetParent(SelectedNode);
        if (target != RootNode && tree.IsSelfOrAncestor(RootNode, target))
        {
            ZoomTo(target);
        }
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomTo(ScanTree.RootIndex);
        SelectedNode = ScanTree.None;
    }

    [RelayCommand]
    private void NavigateBreadcrumb(BreadcrumbItem? crumb)
    {
        if (crumb is not null)
        {
            ZoomTo(crumb.Index);
        }
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        if (SelectedItem is { } item)
        {
            _shell.ShowInExplorer(item.FullPath);
        }
    }

    [RelayCommand]
    private void ShowInFolderExplorer()
    {
        int node = SelectedNode >= 0 ? SelectedNode : RootNode;
        _navigation.Navigate(PageKind.Explorer, node);
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedItem is { } item)
        {
            _shell.CopyToClipboard(item.FullPath);
        }
    }

    private void BuildLegend()
    {
        Legend.Clear();
        static System.Windows.Media.SolidColorBrush B(uint rgb) => new(TreemapColorScheme.ToMediaColor(rgb));
        switch (SelectedColorMode?.Value ?? TreemapColorMode.FileType)
        {
            case TreemapColorMode.Age:
                Legend.Add(new LegendItem("Today", B(TreemapColorScheme.Heat(1))));
                Legend.Add(new LegendItem("~1 month", B(TreemapColorScheme.Heat(TreemapColorScheme.AgeHeat(DateTime.UtcNow.AddDays(-30).Ticks, DateTime.UtcNow)))));
                Legend.Add(new LegendItem("~1 year", B(TreemapColorScheme.Heat(TreemapColorScheme.AgeHeat(DateTime.UtcNow.AddYears(-1).Ticks, DateTime.UtcNow)))));
                Legend.Add(new LegendItem("5+ years", B(TreemapColorScheme.Heat(0))));
                break;
            case TreemapColorMode.Size:
                Legend.Add(new LegendItem("Largest", B(TreemapColorScheme.Heat(1))));
                Legend.Add(new LegendItem("Large", B(TreemapColorScheme.Heat(0.75))));
                Legend.Add(new LegendItem("Medium", B(TreemapColorScheme.Heat(0.5))));
                Legend.Add(new LegendItem("Small", B(TreemapColorScheme.Heat(0.15))));
                break;
            default:
                foreach (FileCategory c in FileCategoryClassifier.All)
                {
                    Legend.Add(new LegendItem(c.ToString(), B(TreemapColorScheme.GetCategoryColor(c))));
                }

                Legend.Add(new LegendItem("Folder (not subdivided)", B(TreemapColorScheme.FolderTileColor)));
                Legend.Add(new LegendItem("Smaller items", B(TreemapColorScheme.AggregateColor)));
                break;
        }
    }
}
