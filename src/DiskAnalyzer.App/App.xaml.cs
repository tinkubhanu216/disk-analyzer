using System.Windows;
using System.Windows.Threading;
using DiskAnalyzer.App.Services;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.App.Views;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Infrastructure;
using DiskAnalyzer.Infrastructure.Database;
using DiskAnalyzer.Infrastructure.Export;
using DiskAnalyzer.Infrastructure.Logging;
using DiskAnalyzer.Infrastructure.Settings;
using DiskAnalyzer.Scanner;
using DiskAnalyzer.Scanner.Drives;
using DiskAnalyzer.Scanner.Providers;
using DiskAnalyzer.Scanner.Providers.Mft;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();
        ILoggerFactory loggerFactory = LoggingSetup.CreateLoggerFactory(AppPaths.LogDirectory, verbose: e.Args.Contains("--verbose"));
        _logger = loggerFactory.CreateLogger<App>();
        _logger.LogInformation("Disk Analyzer starting (elevated: {Elevated}, OS: {OS})", MftScanner.IsProcessElevated, Environment.OSVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _services = ConfigureServices(loggerFactory);

        ISettingsService settings = _services.GetRequiredService<ISettingsService>();
        _services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);
        if (_services.GetRequiredService<IScanCacheService>() is SqliteScanCacheService sqlite)
        {
            sqlite.MaxScansPerRoot = settings.Current.MaxScansPerRoot;
        }

        try
        {
            await _services.GetRequiredService<IScanCacheService>().InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History database unavailable; continuing without history");
        }

        var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
        await HandleCommandLineAsync(e.Args).ConfigureAwait(true);
    }

    /// <summary>
    /// Command line: <c>DiskAnalyzer.exe [--scan &lt;path&gt;] [--page overview|explorer|heatmap|largest|types|history|settings]</c>.
    /// </summary>
    private async Task HandleCommandLineAsync(string[] args)
    {
        string? scan = null, page = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--scan" && i + 1 < args.Length)
            {
                scan = args[++i];
            }
            else if (args[i] == "--page" && i + 1 < args.Length)
            {
                page = args[++i];
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && System.IO.Directory.Exists(args[i]))
            {
                scan = args[i];
            }
        }

        if (scan is not null)
        {
            await _services!.GetRequiredService<ScanLauncher>().StartAsync(System.IO.Path.GetFullPath(scan)).ConfigureAwait(true);
        }

        PageKind? kind = page?.ToLowerInvariant() switch
        {
            "overview" => PageKind.Overview,
            "drives" => PageKind.Drives,
            "explorer" => PageKind.Explorer,
            "heatmap" => PageKind.Heatmap,
            "largest" => PageKind.LargestFiles,
            "types" => PageKind.FileTypes,
            "history" => PageKind.History,
            "settings" => PageKind.Settings,
            _ => null,
        };
        if (kind is { } k)
        {
            _services!.GetRequiredService<NavigationService>().Navigate(k);
        }
    }

    private static ServiceProvider ConfigureServices(ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Core
        services.AddSingleton<IFolderAggregationService, FolderAggregationService>();
        services.AddSingleton<IStorageAnalysisService, StorageAnalysisService>();
        services.AddSingleton<IDuplicateFinder, DuplicateFileFinder>();

        // Scanner (providers are tried by priority: MFT first when available, then standard enumeration)
        services.AddSingleton<IDriveDiscoveryService, DriveDiscoveryService>();
        services.AddSingleton<IFileSystemScanner, MftScanner>();
        services.AddSingleton<IFileSystemScanner, StandardFileSystemScanner>();
        services.AddSingleton<IScanOrchestrator, ScanOrchestrator>();

        // Infrastructure
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IScanCacheService, SqliteScanCacheService>();
        services.AddSingleton<IExportService, ExportService>();

        // App services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ScanSessionService>();
        services.AddSingleton<ScanLauncher>();
        services.AddSingleton<ExportCoordinator>();

        // View models
        services.AddSingleton<DrivesViewModel>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<DrivesViewModel>());
        services.AddSingleton<PageViewModel, OverviewViewModel>();
        services.AddSingleton<PageViewModel, ExplorerViewModel>();
        services.AddSingleton<PageViewModel, HeatmapViewModel>();
        services.AddSingleton<PageViewModel, LargestFilesViewModel>();
        services.AddSingleton<PageViewModel, FileTypesViewModel>();
        services.AddSingleton<PageViewModel, HistoryViewModel>();
        services.AddSingleton<PageViewModel, SettingsViewModel>();
        services.AddSingleton<PageViewModel, SearchViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe error has been logged to {AppPaths.LogDirectory}. A running scan is not affected.",
            "Disk Analyzer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.GetService<IScanOrchestrator>()?.Cancel();
            _logger?.LogInformation("Disk Analyzer exiting");
            _services?.Dispose();
            Serilog.Log.CloseAndFlush();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
