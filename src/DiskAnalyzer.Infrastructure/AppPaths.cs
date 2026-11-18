namespace DiskAnalyzer.Infrastructure;

/// <summary>Per-user data locations (%LOCALAPPDATA%\DiskAnalyzer).</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskAnalyzer");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string DatabaseFile => Path.Combine(DataDirectory, "history.db");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
