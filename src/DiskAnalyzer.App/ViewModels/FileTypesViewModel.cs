using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Controls;
using DiskAnalyzer.App.Converters;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Visualization.Rendering;

namespace DiskAnalyzer.App.ViewModels;

public sealed record ExtensionRow(ExtensionStatistic Stat, double Ratio, double ShareOfTotal)
{
    public string Name => Stat.DisplayName;

    public string Category => Stat.Category.ToString();

    public string SizeText => SizeFormatter.Format(Stat.TotalBytes);

    public string FilesText => $"{Stat.FileCount:N0}";

    public string ShareText => SizeFormatter.FormatPercent(ShareOfTotal * 100);

    public string AverageText => Stat.FileCount > 0 ? SizeFormatter.Format(Stat.TotalBytes / Stat.FileCount) : string.Empty;

    public System.Windows.Media.Brush Brush { get; } = new System.Windows.Media.SolidColorBrush(TreemapColorScheme.ToMediaColor(TreemapColorScheme.GetExtensionColor(Stat.Extension)));
}

public sealed partial class FileTypesViewModel : PageViewModel
{
    private readonly IStorageAnalysisService _analysis;
    private readonly NavigationService _navigation;
    private IReadOnlyList<ExtensionRow> _allRows = [];

    public FileTypesViewModel(ScanSessionService session, IStorageAnalysisService analysis, NavigationService navigation)
        : base(session)
    {
        _analysis = analysis;
        _navigation = navigation;
    }

    public override string Title => "File Types";

    public override PageKind Kind => PageKind.FileTypes;

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartSegment>? CategorySegments { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ExtensionRow> TopExtensions { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ExtensionRow> Extensions { get; private set; } = [];

    [ObservableProperty]
    public partial string? ExtensionFilter { get; set; }

    [ObservableProperty]
    public partial string? TotalText { get; private set; }

    [ObservableProperty]
    public partial ExtensionRow? SelectedExtension { get; set; }

    partial void OnExtensionFilterChanged(string? value) => ApplyFilter();

    protected override void OnResultChanged()
    {
        Categories.Clear();
        ScanResult? r = Session.Current;
        if (r is null)
        {
            _allRows = [];
            TopExtensions = [];
            ApplyFilter();
            return;
        }

        IReadOnlyList<ExtensionStatistic> stats = _analysis.GetExtensionStatistics(r.Tree);
        long total = Math.Max(1, stats.Sum(s => s.TotalBytes));
        long max = Math.Max(1, stats.Count > 0 ? stats[0].TotalBytes : 1);
        _allRows = stats.Select(s => new ExtensionRow(s, (double)s.TotalBytes / max, (double)s.TotalBytes / total)).ToList();
        TopExtensions = _allRows.Take(10).ToList();
        ApplyFilter();

        foreach (CategoryStatistic c in _analysis.GetCategoryStatistics(r.Tree))
        {
            Categories.Add(new CategoryRow(c.Category, c.TotalBytes, c.FileCount, (double)c.TotalBytes / total));
        }

        CategorySegments = Categories.Select(c => new ChartSegment(c.Name, c.Bytes, CategoryBrushConverter.GetBrush(c.Category))).ToList();
        TotalText = $"{SizeFormatter.Format(total)} in {stats.Sum(s => s.FileCount):N0} files · {stats.Count:N0} extensions";
    }

    private void ApplyFilter()
    {
        string? filter = ExtensionFilter?.Trim().TrimStart('*');
        Extensions = string.IsNullOrEmpty(filter)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    [RelayCommand]
    private void SearchExtension(ExtensionRow? row)
    {
        if (row is not null)
        {
            _navigation.Navigate(PageKind.Search, new SearchPreset(null, string.IsNullOrEmpty(row.Stat.Extension) ? null : row.Stat.Extension, null));
        }
    }

    [RelayCommand]
    private void SearchCategory(CategoryRow? row)
    {
        if (row is not null)
        {
            _navigation.Navigate(PageKind.Search, new SearchPreset(null, null, row.Category));
        }
    }
}
