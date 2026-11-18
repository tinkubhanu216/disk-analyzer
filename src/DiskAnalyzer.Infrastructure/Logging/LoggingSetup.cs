using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace DiskAnalyzer.Infrastructure.Logging;

/// <summary>Configures Serilog (rolling files) behind Microsoft.Extensions.Logging.</summary>
public static class LoggingSetup
{
    public static ILoggerFactory CreateLoggerFactory(string logDirectory, bool verbose = false)
    {
        Directory.CreateDirectory(logDirectory);
        Serilog.Core.Logger serilog = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("App", "DiskAnalyzer")
            .WriteTo.File(
                Path.Combine(logDirectory, "diskanalyzer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Logger = serilog;
        return LoggerFactory.Create(builder => builder.AddSerilog(serilog, dispose: true).SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
    }
}
