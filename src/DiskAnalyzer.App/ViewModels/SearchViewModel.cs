using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

public sealed record SearchPreset(string? Text, string? Extension, FileCategory? Category);

public enum SearchItemKind
{
    All,
    Files,
    Folders,
}

public sealed record ItemKindOption(SearchItemKind Value, string Label);

/// <summary>Searches the in-memory scan index (no filesystem access), debounced and cancellable.</summary>
public sealed partial class SearchViewModel : PageViewModel
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private readonly IStorageAnalysisService _analysis;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ILogger<SearchViewModel> _logger;
    private CancellationTokenSource? _cts;

    public SearchViewModel(ScanSessionService session, IStorageAnalysisService analysis, NavigationService navigation, ShellService shell, ILogger<SearchViewModel> logger)
        : base(session)
    {
        _analysis = analysis;
        _navigation = navigation;
        _shell = shell;
        _logger = logger;
        KindOptions = [new(SearchItemKind.All, "Files and folders"), new(SearchItemKind.Files, "Files only"), new(SearchItemKind.Folders, "Folders only")];
        CategoryOptions = [new(null, "All types"), .. FileCategoryClassifier.All.Select(c => new CategoryOption(c, c.ToString()))];
        SelectedKind = KindOptions[0];
        SelectedCategory = CategoryOptions[0];
    }

    public override string Title => "Search";

    public override PageKind Kind => PageKind.Search;

    public IReadOnlyList<ItemKindOption> KindOptions { get; }

    public IReadOnlyList<CategoryOption> CategoryOptions { get; }

    [ObservableProperty]
    public partial string? QueryText { get; set; }

    [ObservableProperty]
    public partial string? ExtensionText { get; set; }

    [ObservableProperty]
    public partial string? MinSizeText { get; set; }

    [ObservableProperty]
    public partial ItemKindOption SelectedKind { get; set; }

    [ObservableProperty]
    public partial CategoryOption SelectedCategory { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<NodeItemViewModel> Results { get; private set; } = [];

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedResult { get; set; }

    partial void OnQueryTextChanged(string? value) => Schedule();

    partial void OnExtensionTextChanged(string? value) => Schedule();

    partial void OnMinSizeTextChanged(string? value) => Schedule();

    partial void OnSelectedKindChanged(ItemKindOption value) => Schedule();

    partial void OnSelectedCategoryChanged(CategoryOption value) => Schedule();

    protected override void OnResultChanged() => Schedule();

    public override void OnNavigatedTo(object? parameter)
    {
        if (parameter is SearchPreset preset)
        {
            QueryText = preset.Text;
            ExtensionText = preset.Extension;
            SelectedCategory = CategoryOptions.FirstOrDefault(c => c.Value == preset.Category) ?? CategoryOptions[0];
            SelectedKind = preset.Extension is not null || preset.Category is not null ? KindOptions[1] : KindOptions[0];
        }

        Schedule();
    }

    private void Schedule() => _ = RunAsync();

    private async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        ScanResult? result = Session.Current;
        if (result is null)
        {
            Results = [];
            Summary = "Scan a drive or folder to search its contents.";
            return;
        }

        bool hasCriteria = !string.IsNullOrWhiteSpace(QueryText) || !string.IsNullOrWhiteSpace(ExtensionText) || !string.IsNullOrWhiteSpace(MinSizeText) || SelectedCategory.Value is not null;
        if (!hasCriteria)
        {
            Results = [];
            Summary = "Type a name, wildcard (*.iso) or pick a filter. Search uses the scan index — nothing is rescanned.";
            return;
        }

        long minSize = 0;
        if (!string.IsNullOrWhiteSpace(MinSizeText) && !SizeFormatter.TryParse(MinSizeText, out minSize))
        {
            Summary = "Enter a minimum size such as 100 MB or 2 GB.";
            return;
        }

        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(true);
            IsSearching = true;
            var query = new SearchQuery
            {
                Text = QueryText,
                Extensions = string.IsNullOrWhiteSpace(ExtensionText)
                    ? null
                    : ExtensionText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim().TrimStart('*')).ToList(),
                Category = SelectedCategory.Value,
                MinSizeBytes = minSize,
                IncludeFiles = SelectedKind.Value != SearchItemKind.Folders,
                IncludeFolders = SelectedKind.Value != SearchItemKind.Files,
                MaxResults = 5000,
            };
            SearchResult found = await _analysis.SearchAsync(result.Tree, query, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Results = found.Nodes.Select(n => new NodeItemViewModel(result.Tree, n, result.DriveCapacityBytes)).ToList();
            long bytes = Results.Where(r => !r.IsDirectory).Sum(r => r.SizeBytes);
            Summary = found.TotalMatches == 0
                ? "No matches."
                : $"{found.TotalMatches:N0} matches in {found.Elapsed.TotalMilliseconds:0} ms"
                  + (found.IsTruncated ? $" · showing the {found.Nodes.Count:N0} largest" : string.Empty)
                  + (bytes > 0 ? $" · listed files total {SizeFormatter.Format(bytes)}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // superseded by newer input
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            Summary = "Search failed: " + ex.Message;
        }
        finally
        {
            if (_cts == cts)
            {
                IsSearching = false;
            }
        }
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
    private void ClearFilters()
    {
        ExtensionText = null;
        MinSizeText = null;
        SelectedKind = KindOptions[0];
        SelectedCategory = CategoryOptions[0];
    }
}
