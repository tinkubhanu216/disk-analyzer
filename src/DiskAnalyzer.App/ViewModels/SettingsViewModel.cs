using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Infrastructure;
using DiskAnalyzer.Infrastructure.Database;
using DiskAnalyzer.Infrastructure.Settings;
using DiskAnalyzer.Scanner.Providers.Mft;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.ViewModels;

public sealed record ThemeOption(ThemePreference Value, string Label);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly ShellService _shell;
    private readonly IScanCacheService _cache;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(ScanSessionService session, ISettingsService settings, ThemeService theme, ShellService shell, IScanCacheService cache, ILogger<SettingsViewModel> logger)
        : base(session)
    {
        _settings = settings;
        _theme = theme;
        _shell = shell;
        _cache = cache;
        _logger = logger;
        ThemeOptions = [new(ThemePreference.System, "Use Windows setting"), new(ThemePreference.Light, "Light"), new(ThemePreference.Dark, "Dark")];
        ThresholdOptions =
        [
            new(0, "Store every file (largest database)"),
            new(64L << 10, "Files of 64 KB or more"),
            new(1L << 20, "Files of 1 MB or more (recommended)"),
            new(10L << 20, "Files of 10 MB or more"),
            new(100L << 20, "Files of 100 MB or more"),
        ];
        Load(settings.Current);
    }

    public override string Title => "Settings";

    public override PageKind Kind => PageKind.Settings;

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public IReadOnlyList<SizeThresholdOption> ThresholdOptions { get; }

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [0, 1, 2, 4, 8, 12, 16, 24, 32];

    [ObservableProperty] public partial ThemeOption SelectedTheme { get; set; }

    [ObservableProperty] public partial bool IncludeHidden { get; set; }

    [ObservableProperty] public partial bool IncludeSystem { get; set; }

    [ObservableProperty] public partial bool FollowReparsePoints { get; set; }

    [ObservableProperty] public partial bool DeduplicateHardLinks { get; set; }

    [ObservableProperty] public partial bool UseMftWhenAvailable { get; set; }

    [ObservableProperty] public partial int MaxConcurrency { get; set; }

    [ObservableProperty] public partial bool SaveScanHistory { get; set; }

    [ObservableProperty] public partial SizeThresholdOption SelectedThreshold { get; set; }

    [ObservableProperty] public partial int MaxScansPerRoot { get; set; }

    [ObservableProperty] public partial bool OfferCachedResults { get; set; }

    [ObservableProperty] public partial double TreemapMinTilePixels { get; set; }

    public bool IsElevated => MftScanner.IsProcessElevated;

    public string VersionText => $"Version {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";

    /// <summary>Copyright line from the assembly metadata (set in Directory.Build.props).</summary>
    public string CopyrightText => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public string ElevationText => IsElevated
        ? "Running as administrator. NTFS volumes can be scanned through the Master File Table."
        : "Running with standard rights. The app never needs administrator rights; MFT scanning is used only when you start it as administrator.";

    public string DataFolder => AppPaths.DataDirectory;

    public string ConcurrencyHint => $"0 = automatic (this PC has {Environment.ProcessorCount} logical processors; fewer workers are used for hard disks, USB and network drives).";

    private void Load(AppSettings s)
    {
        _loading = true;
        SelectedTheme = ThemeOptions.First(t => t.Value == s.Theme);
        IncludeHidden = s.IncludeHidden;
        IncludeSystem = s.IncludeSystem;
        FollowReparsePoints = s.FollowReparsePoints;
        DeduplicateHardLinks = s.DeduplicateHardLinks;
        UseMftWhenAvailable = s.UseMftWhenAvailable;
        MaxConcurrency = s.MaxConcurrency;
        SaveScanHistory = s.SaveScanHistory;
        SelectedThreshold = ThresholdOptions.FirstOrDefault(t => t.Bytes == s.HistoryFileThresholdBytes) ?? new SizeThresholdOption(s.HistoryFileThresholdBytes, $"Files of {SizeFormatter.Format(s.HistoryFileThresholdBytes)} or more");
        MaxScansPerRoot = s.MaxScansPerRoot;
        OfferCachedResults = s.OfferCachedResults;
        TreemapMinTilePixels = s.TreemapMinTilePixels;
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(HasResult) or null)
        {
            return;
        }

        AppSettings s = _settings.Current.Clone();
        s.Theme = SelectedTheme.Value;
        s.IncludeHidden = IncludeHidden;
        s.IncludeSystem = IncludeSystem;
        s.FollowReparsePoints = FollowReparsePoints;
        s.DeduplicateHardLinks = DeduplicateHardLinks;
        s.UseMftWhenAvailable = UseMftWhenAvailable;
        s.MaxConcurrency = MaxConcurrency;
        s.SaveScanHistory = SaveScanHistory;
        s.HistoryFileThresholdBytes = SelectedThreshold.Bytes;
        s.MaxScansPerRoot = Math.Clamp(MaxScansPerRoot, 1, 100);
        s.OfferCachedResults = OfferCachedResults;
        s.TreemapMinTilePixels = Math.Clamp(TreemapMinTilePixels, 1, 20);
        _settings.Save(s);

        if (e.PropertyName == nameof(SelectedTheme))
        {
            _theme.Apply(s.Theme);
        }

        if (_cache is SqliteScanCacheService sqlite)
        {
            sqlite.MaxScansPerRoot = s.MaxScansPerRoot;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(AppPaths.LogDirectory);

    [RelayCommand]
    private void RestartElevated()
    {
        if (!_shell.RestartAsAdministrator())
        {
            _shell.ShowInfo("Disk Analyzer is still running with standard rights.");
        }
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!_shell.Confirm("Delete all stored scans from history?\n\nOnly the app's own history database is affected; no files on your drives are touched."))
        {
            return;
        }

        try
        {
            foreach (var scan in await _cache.GetHistoryAsync().ConfigureAwait(true))
            {
                await _cache.DeleteAsync(scan.ScanId).ConfigureAwait(true);
            }

            Session.NotifyHistoryChanged();
            _shell.ShowInfo("Scan history cleared.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing history failed");
            _shell.ShowError("History could not be cleared: " + ex.Message);
        }
    }
}
