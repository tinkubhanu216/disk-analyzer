using System.IO;
using DiskAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.Services;

/// <summary>Starts scans from any page with consistent navigation and error reporting.</summary>
public sealed class ScanLauncher(ScanSessionService session, ShellService shell, NavigationService navigation, ILogger<ScanLauncher> logger)
{
    public bool IsBusy => session.Orchestrator.IsBusy;

    public async Task StartAsync(string rootPath, DriveInfoModel? drive = null)
    {
        if (session.Orchestrator.IsBusy)
        {
            shell.ShowInfo("A scan is already running. Cancel it first to start another.");
            return;
        }

        if (!Directory.Exists(rootPath))
        {
            shell.ShowWarning($"{rootPath} is not available. The drive may have been disconnected.");
            return;
        }

        navigation.Navigate(PageKind.Overview);
        try
        {
            await session.ScanAsync(rootPath, drive).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan of {Root} failed", rootPath);
            shell.ShowError($"The scan of {rootPath} could not be completed.\n\n{ex.Message}");
        }
    }

    public Task RescanCurrentAsync()
    {
        ScanResult? current = session.Current;
        // Drive info is re-queried so capacity/free space are current, never taken from a stored scan.
        return current is null ? Task.CompletedTask : StartAsync(current.RootPath, null);
    }
}
