using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

public enum ExplorerSort
{
    SizeDescending,
    SizeAscending,
    Name,
    FileCount,
    LastModified,
}

public sealed record SortOption(ExplorerSort Value, string Label);

public sealed record CategoryOption(FileCategory? Value, string Label);

public sealed record BreadcrumbItem(int Index, string Name);

/// <summary>Lazily expanded folder node in the explorer tree.</summary>
public sealed partial class FolderTreeItemViewModel : ObservableObject
{
    private static readonly FolderTreeItemViewModel Placeholder = new();
    private readonly ExplorerViewModel? _owner;
    private bool _loaded;

    private FolderTreeItemViewModel()
    {
        Name = string.Empty;
        Tree = null!;
    }

    public FolderTreeItemViewModel(ScanTree tree, int index, ExplorerViewModel owner)
    {
        Tree = tree;
        Index = index;
        _owner = owner;
        Name = index == ScanTree.RootIndex ? tree.RootPath : tree.GetName(index);
        long parentSize = tree.GetParent(index) is >= 0 and var p ? tree.GetSize(p) : tree.GetSize(index);
        SizeText = SizeFormatter.Format(tree.GetSize(index));
        Ratio = parentSize > 0 ? (double)tree.GetSize(index) / parentSize : 1;
        IsInaccessible = (tree.GetFlags(index) & NodeFlags.Inaccessible) != 0;
        if (HasSubfolders())
        {
            Children.Add(Placeholder);
        }
    }

    public ScanTree Tree { get; }

    public int Index { get; }

    public string Name { get; }

    public string SizeText { get; } = string.Empty;

    public double Ratio { get; }

    public bool IsInaccessible { get; }

    public ObservableCollection<FolderTreeItemViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureLoaded();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _owner?.OnTreeSelection(this);
        }
    }

    public void EnsureLoaded()
    {
        if (_loaded || _owner is null)
        {
            return;
        }

        _loaded = true;
        Children.Clear();
        var dirs = new List<int>();
        foreach (int c in Tree.GetChildren(Index))
        {
            if (Tree.IsDirectory(c))
            {
                dirs.Add(c);
            }
        }

        dirs.Sort((a, b) => Tree.GetSize(b).CompareTo(Tree.GetSize(a)));
        foreach (int d in dirs)
        {
            Children.Add(new FolderTreeItemViewModel(Tree, d, _owner));
        }
    }

    private bool HasSubfolders()
    {
        foreach (int c in Tree.GetChildren(Index))
        {
            if (Tree.IsDirectory(c))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed partial class ExplorerViewModel : PageViewModel
{
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ScanLauncher _launcher;
    private readonly Stack<int> _back = new();
    private readonly Stack<int> _forward = new();
    private bool _syncingTree;

    public ExplorerViewModel(ScanSessionService session, NavigationService navigation, ShellService shell, ScanLauncher launcher)
        : base(session)
    {
        _navigation = navigation;
        _shell = shell;
        _launcher = launcher;
        SortOptions =
        [
            new(ExplorerSort.SizeDescending, "Largest to smallest"),
            new(ExplorerSort.SizeAscending, "Smallest to largest"),
            new(ExplorerSort.Name, "Alphabetical"),
            new(ExplorerSort.FileCount, "File count"),
            new(ExplorerSort.LastModified, "Last modified"),
        ];
        CategoryOptions = [new(null, "All types"), .. FileCategoryClassifier.All.Select(c => new CategoryOption(c, c.ToString()))];
        SelectedSort = SortOptions[0];
        SelectedCategory = CategoryOptions[0];
    }

    public override string Title => "Folder Explorer";

    public override PageKind Kind => PageKind.Explorer;

    public IReadOnlyList<SortOption> SortOptions { get; }

    public IReadOnlyList<CategoryOption> CategoryOptions { get; }

    public ObservableCollection<FolderTreeItemViewModel> RootItems { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NodeItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial int CurrentFolder { get; private set; } = ScanTree.None;

    [ObservableProperty]
    public partial NodeItemViewModel? CurrentFolderItem { get; private set; }

    [ObservableProperty]
    public partial NodeItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; }

    [ObservableProperty]
    public partial CategoryOption SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string? NameFilter { get; set; }

    [ObservableProperty]
    public partial string? ExtensionFilter { get; set; }

    [ObservableProperty]
    public partial string? MinSizeFilter { get; set; }

    [ObservableProperty]
    public partial bool ShowFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowFolders { get; set; } = true;

    [ObservableProperty]
    public partial string? ItemsSummary { get; private set; }

    [ObservableProperty]
    public partial string? FilterError { get; private set; }

    /// <summary>Item shown in the details panel: the selection, or the current folder.</summary>
    public NodeItemViewModel? DetailsItem => SelectedItem ?? CurrentFolderItem;

    partial void OnSelectedItemChanged(NodeItemViewModel? value) => OnPropertyChanged(nameof(DetailsItem));

    partial void OnCurrentFolderItemChanged(NodeItemViewModel? value) => OnPropertyChanged(nameof(DetailsItem));

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(NameFilter) || !string.IsNullOrWhiteSpace(ExtensionFilter) || !string.IsNullOrWhiteSpace(MinSizeFilter) || SelectedCategory.Value is not null || !ShowFiles || !ShowFolders;

    partial void OnSelectedSortChanged(SortOption value) => ReloadItems();

    partial void OnSelectedCategoryChanged(CategoryOption value) => ReloadItems();

    partial void OnNameFilterChanged(string? value) => ReloadItems();

    partial void OnExtensionFilterChanged(string? value) => ReloadItems();

    partial void OnMinSizeFilterChanged(string? value) => ReloadItems();

    partial void OnShowFilesChanged(bool value) => ReloadItems();

    partial void OnShowFoldersChanged(bool value) => ReloadItems();

    protected override void OnResultChanged()
    {
        _back.Clear();
        _forward.Clear();
        RootItems.Clear();
        ScanTree? tree = Session.Current?.Tree;
        if (tree is null)
        {
            Items = [];
            return;
        }

        var root = new FolderTreeItemViewModel(tree, ScanTree.RootIndex, this);
        RootItems.Add(root);
        root.IsExpanded = true;
        CurrentFolder = ScanTree.None;
        NavigateTo(ScanTree.RootIndex, recordHistory: false);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        if (parameter is int node && Session.Current?.Tree is { } tree && node >= 0 && node < tree.Count)
        {
            if (tree.IsDirectory(node))
            {
                NavigateTo(node, recordHistory: true);
            }
            else
            {
                NavigateTo(tree.GetParent(node), recordHistory: true);
                SelectedItem = Items.FirstOrDefault(i => i.Index == node);
            }
        }
    }

    internal void OnTreeSelection(FolderTreeItemViewModel item)
    {
        if (!_syncingTree)
        {
            NavigateTo(item.Index, recordHistory: true);
        }
    }

    public void NavigateTo(int folder, bool recordHistory)
    {
        ScanResult? result = Session.Current;
        if (result is null || folder < 0 || folder >= result.Tree.Count || !result.Tree.IsDirectory(folder))
        {
            return;
        }

        if (recordHistory && CurrentFolder >= 0 && CurrentFolder != folder)
        {
            _back.Push(CurrentFolder);
            _forward.Clear();
        }

        CurrentFolder = folder;
        CurrentFolderItem = new NodeItemViewModel(result.Tree, folder, result.DriveCapacityBytes);
        Breadcrumbs.Clear();
        foreach (int node in result.Tree.GetAncestry(folder))
        {
            Breadcrumbs.Add(new BreadcrumbItem(node, node == ScanTree.RootIndex ? result.Tree.RootPath.TrimEnd('\\') : result.Tree.GetName(node)));
        }

        ReloadItems();
        SyncTree(folder);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void ReloadItems()
    {
        OnPropertyChanged(nameof(IsFiltered));
        ScanResult? result = Session.Current;
        if (result is null || CurrentFolder < 0)
        {
            Items = [];
            return;
        }

        ScanTree tree = result.Tree;
        FilterError = null;
        long minSize = 0;
        if (!string.IsNullOrWhiteSpace(MinSizeFilter) && !SizeFormatter.TryParse(MinSizeFilter, out minSize))
        {
            FilterError = "Enter a size such as 500 MB, 1.5 GB or 200 KB.";
        }

        HashSet<string>? extensions = string.IsNullOrWhiteSpace(ExtensionFilter)
            ? null
            : ExtensionFilter.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().TrimStart('*'))
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        NameMatcher matcher = NameMatcher.Create(NameFilter);
        FileCategory? category = SelectedCategory?.Value;
        bool fileFilters = extensions is not null || category is not null;

        var list = new List<NodeItemViewModel>();
        long capacity = result.DriveCapacityBytes;
        foreach (int c in tree.GetChildren(CurrentFolder))
        {
            bool isDir = tree.IsDirectory(c);
            if (isDir ? !ShowFolders || fileFilters : !ShowFiles)
            {
                continue;
            }

            if (tree.GetSize(c) < minSize || !matcher.IsMatch(tree.GetName(c)))
            {
                continue;
            }

            if (!isDir)
            {
                string ext = tree.GetExtension(c);
                if ((extensions is not null && !extensions.Contains(ext)) || (category is not null && FileCategoryClassifier.Classify(ext) != category))
                {
                    continue;
                }
            }

            list.Add(new NodeItemViewModel(tree, c, capacity));
        }

        Comparison<NodeItemViewModel> comparison = (SelectedSort?.Value ?? ExplorerSort.SizeDescending) switch
        {
            ExplorerSort.SizeAscending => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            ExplorerSort.Name => (a, b) => FolderFirst(a, b) ?? StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
            ExplorerSort.FileCount => (a, b) => b.FileCount != a.FileCount ? b.FileCount.CompareTo(a.FileCount) : b.SizeBytes.CompareTo(a.SizeBytes),
            ExplorerSort.LastModified => (a, b) => b.LastModifiedTicks.CompareTo(a.LastModifiedTicks),
            _ => (a, b) => b.SizeBytes.CompareTo(a.SizeBytes),
        };
        list.Sort(comparison);
        Items = list;

        int total = tree.GetChildCount(CurrentFolder);
        long shown = list.Sum(i => i.IsDirectory || (i.Flags & NodeFlags.HardLinkDuplicate) == 0 ? i.SizeBytes : 0);
        ItemsSummary = list.Count == total
            ? $"{total:N0} items"
            : $"{list.Count:N0} of {total:N0} items match · {SizeFormatter.Format(shown)}";
    }

    private static int? FolderFirst(NodeItemViewModel a, NodeItemViewModel b) =>
        a.IsDirectory == b.IsDirectory ? null : a.IsDirectory ? -1 : 1;

    /// <summary>Expands the tree down to <paramref name="folder"/> and selects it.</summary>
    private void SyncTree(int folder)
    {
        if (RootItems.Count == 0 || Session.Current is null)
        {
            return;
        }

        _syncingTree = true;
        try
        {
            FolderTreeItemViewModel current = RootItems[0];
            foreach (int node in Session.Current.Tree.GetAncestry(folder).Skip(1))
            {
                current.EnsureLoaded();
                current.IsExpanded = true;
                FolderTreeItemViewModel? next = current.Children.FirstOrDefault(c => c.Index == node);
                if (next is null)
                {
                    break;
                }

                current = next;
            }

            current.IsSelected = true;
        }
        finally
        {
            _syncingTree = false;
        }
    }

    [RelayCommand]
    private void Open(NodeItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsDirectory)
        {
            NavigateTo(item.Index, recordHistory: true);
        }
        else
        {
            SelectedItem = item;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count > 0)
        {
            _forward.Push(CurrentFolder);
            NavigateTo(_back.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private void GoForward()
    {
        if (_forward.Count > 0)
        {
            _back.Push(CurrentFolder);
            NavigateTo(_forward.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private void GoUp()
    {
        if (Session.Current is { } r && CurrentFolder > 0)
        {
            NavigateTo(r.Tree.GetParent(CurrentFolder), recordHistory: true);
        }
    }

    [RelayCommand]
    private void NavigateBreadcrumb(BreadcrumbItem? crumb)
    {
        if (crumb is not null)
        {
            NavigateTo(crumb.Index, recordHistory: true);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        NameFilter = null;
        ExtensionFilter = null;
        MinSizeFilter = null;
        SelectedCategory = CategoryOptions[0];
        ShowFiles = true;
        ShowFolders = true;
    }

    [RelayCommand]
    private void Visualize(NodeItemViewModel? item)
    {
        int target = item is { IsDirectory: true } ? item.Index : CurrentFolder;
        _navigation.Navigate(PageKind.Heatmap, target);
    }

    [RelayCommand]
    private void ShowInWindowsExplorer(NodeItemViewModel? item) => _shell.ShowInExplorer((item ?? CurrentFolderItem)?.FullPath ?? string.Empty);

    [RelayCommand]
    private void CopyPath(NodeItemViewModel? item)
    {
        if ((item ?? CurrentFolderItem) is { } i)
        {
            _shell.CopyToClipboard(i.FullPath);
        }
    }

    [RelayCommand]
    private async Task ScanThisFolder(NodeItemViewModel? item)
    {
        NodeItemViewModel? target = item is { IsDirectory: true } ? item : CurrentFolderItem;
        if (target is not null)
        {
            await _launcher.StartAsync(target.FullPath).ConfigureAwait(true);
        }
    }
}
