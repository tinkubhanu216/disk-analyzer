using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.Infrastructure.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>User preferences persisted as JSON.</summary>
public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    // Scanning
    public bool IncludeHidden { get; set; } = true;

    public bool IncludeSystem { get; set; } = true;

    public bool FollowReparsePoints { get; set; }

    public bool DeduplicateHardLinks { get; set; }

    public bool UseMftWhenAvailable { get; set; } = true;

    /// <summary>0 = automatic.</summary>
    public int MaxConcurrency { get; set; }

    // History
    public bool SaveScanHistory { get; set; } = true;

    /// <summary>Files smaller than this are not stored in history (folders always are).</summary>
    public long HistoryFileThresholdBytes { get; set; } = 1024 * 1024;

    public int MaxScansPerRoot { get; set; } = 10;

    public bool OfferCachedResults { get; set; } = true;

    // Treemap
    public bool TreemapShowLabels { get; set; } = true;

    public string TreemapColorMode { get; set; } = "FileType";

    public double TreemapMinTilePixels { get; set; } = 3;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    void Save(AppSettings settings);
}

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
        : this(AppPaths.SettingsFile, logger)
    {
    }

    public JsonSettingsService(string path, ILogger<JsonSettingsService> logger)
    {
        _path = path;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings.Clone();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings to {Path}", _path);
        }

        Changed?.Invoke(this, Current);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Settings file {Path} is unreadable; using defaults", _path);
        }

        return new AppSettings();
    }
}
