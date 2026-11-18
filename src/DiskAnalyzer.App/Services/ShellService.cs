using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DiskAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DiskAnalyzer.App.Services;

/// <summary>Windows shell integration and dialogs. Opens Explorer read-only; never modifies files.</summary>
public sealed class ShellService(ILogger<ShellService> logger)
{
    /// <summary>Opens Explorer with the item selected (or the folder itself).</summary>
    public void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || (Directory.Exists(path) && Path.GetPathRoot(path) != path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && Directory.Exists(parent))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
                }
                else
                {
                    ShowWarning("The item no longer exists. It may have been moved or deleted since the scan.");
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open Explorer for {Path}", path);
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
        }
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            logger.LogWarning(ex, "Clipboard is busy");
        }
    }

    /// <summary>Restarts the app elevated (UAC prompt). Returns false if the user declined.</summary>
    public bool RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // user cancelled UAC
        }
    }

    public string? PickFolder(string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", InitialDirectory = initial ?? string.Empty };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public (string Path, ExportFormat Format)? PickExportFile(string suggestedName, ExportFormat preferred)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export scan results",
            FileName = suggestedName,
            Filter = "CSV (*.csv)|*.csv|Excel workbook (*.xlsx)|*.xlsx|JSON (*.json)|*.json",
            FilterIndex = preferred switch { ExportFormat.Excel => 2, ExportFormat.Json => 3, _ => 1 },
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        ExportFormat format = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
        {
            ".xlsx" => ExportFormat.Excel,
            ".json" => ExportFormat.Json,
            ".csv" => ExportFormat.Csv,
            _ => dialog.FilterIndex switch { 2 => ExportFormat.Excel, 3 => ExportFormat.Json, _ => ExportFormat.Csv },
        };
        return (dialog.FileName, format);
    }

    public bool Confirm(string message, string title = "Disk Analyzer") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowInfo(string message, string title = "Disk Analyzer") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title = "Disk Analyzer") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title = "Disk Analyzer") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
