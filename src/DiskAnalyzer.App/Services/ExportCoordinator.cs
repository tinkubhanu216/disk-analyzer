using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.App.Services;

/// <summary>Runs an export end to end: file picker, background export with progress, result message.</summary>
public sealed partial class ExportCoordinator(IExportService export, ShellService shell, ILogger<ExportCoordinator> logger) : ObservableObject
{
    [ObservableProperty]
    public partial string? Status { get; private set; }

    public async Task ExportAsync(ScanResult result, ExportFormat preferred, ExportScope scope, int topCount)
    {
        if (Status is not null)
        {
            shell.ShowInfo("An export is already in progress.");
            return;
        }

        string baseName = new(Path.GetFileName(result.RootPath.TrimEnd('\\')).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "Drive-" + result.RootPath[..1];
        }

        string suffix = scope switch
        {
            ExportScope.TopFiles => $"-top{topCount}-files",
            ExportScope.TopFolders => $"-top{topCount}-folders",
            ExportScope.FoldersOnly => "-folders",
            _ => "-all-items",
        };
        var target = shell.PickExportFile($"{baseName}{suffix}-{result.StartedUtc.ToLocalTime():yyyyMMdd-HHmm}", preferred);
        if (target is null)
        {
            return;
        }

        var progress = new Progress<double>(p => Status = $"Exporting… {p:P0}");
        try
        {
            Status = "Exporting…";
            ExportSummary summary = await export.ExportAsync(result, new ExportOptions(target.Value.Path, target.Value.Format, scope, topCount), progress).ConfigureAwait(true);
            Status = null;
            string message = $"Exported {summary.RowsWritten:N0} rows to\n{summary.FilePath}";
            if (summary.Note is not null)
            {
                message += "\n\n" + summary.Note;
            }

            shell.ShowInfo(message, "Export complete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = null;
            logger.LogError(ex, "Export failed");
            shell.ShowError($"Export failed: {ex.Message}");
        }
    }
}
