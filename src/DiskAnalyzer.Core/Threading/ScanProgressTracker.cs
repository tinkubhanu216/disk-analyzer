using System.Collections.Concurrent;
using System.Diagnostics;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Threading;

/// <summary>
/// Lock-free progress counters written by scanner threads and sampled by the UI on a timer.
/// Scanners never call into the UI; the UI pulls a <see cref="ScanProgress"/> snapshot.
/// </summary>
public sealed class ScanProgressTracker
{
    private const int MaxErrorSamples = 200;

    private readonly Stopwatch _stopwatch = new();
    private readonly ConcurrentQueue<ScanError> _errorSamples = new();
    private long _files;
    private long _folders;
    private long _bytes;
    private long _errors;
    private long _workDone;
    private long _workTotal;
    private int _errorSampleCount;
    private string? _currentPath;
    private string _phase = string.Empty;

    /// <summary>Expected total bytes (e.g. drive used space) used to estimate progress; 0 = unknown.</summary>
    public long ExpectedBytes { get; set; }

    public long FilesScanned => Interlocked.Read(ref _files);

    public long FoldersScanned => Interlocked.Read(ref _folders);

    public long BytesDiscovered => Interlocked.Read(ref _bytes);

    public long ErrorCount => Interlocked.Read(ref _errors);

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public IReadOnlyList<ScanError> ErrorSamples => [.. _errorSamples];

    public string Phase
    {
        get => Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, value);
    }

    public string? CurrentPath
    {
        get => Volatile.Read(ref _currentPath);
        set => Volatile.Write(ref _currentPath, value);
    }

    public void Start() => _stopwatch.Restart();

    public void Stop() => _stopwatch.Stop();

    public void PauseClock() => _stopwatch.Stop();

    public void ResumeClock() => _stopwatch.Start();

    public void AddFiles(long count, long bytes)
    {
        Interlocked.Add(ref _files, count);
        Interlocked.Add(ref _bytes, bytes);
    }

    public void AddFolders(long count) => Interlocked.Add(ref _folders, count);

    /// <summary>Explicit work units (e.g. MFT records) for providers that know their total up front.</summary>
    public void SetWorkTotal(long total) => Interlocked.Exchange(ref _workTotal, total);

    public void AddWorkDone(long units) => Interlocked.Add(ref _workDone, units);

    public void RecordError(string path, string message)
    {
        Interlocked.Increment(ref _errors);
        if (Interlocked.Increment(ref _errorSampleCount) <= MaxErrorSamples)
        {
            _errorSamples.Enqueue(new ScanError(path, message));
        }
    }

    public double? GetFraction()
    {
        long total = Interlocked.Read(ref _workTotal);
        if (total > 0)
        {
            return Math.Clamp((double)Interlocked.Read(ref _workDone) / total, 0, 1);
        }

        long expected = ExpectedBytes;
        if (expected > 0)
        {
            // Bytes found vs. used space: a good estimate for whole-drive scans, never shown as 100% before the end.
            return Math.Clamp((double)BytesDiscovered / expected, 0, 0.99);
        }

        return null;
    }

    public ScanProgress Snapshot(ScanState state, string? providerName)
    {
        double? fraction = GetFraction();
        TimeSpan elapsed = Elapsed;
        TimeSpan? remaining = null;
        if (state == ScanState.Scanning && fraction is > 0.05 and < 0.99 && elapsed.TotalSeconds >= 3)
        {
            double seconds = elapsed.TotalSeconds * (1 - fraction.Value) / fraction.Value;
            if (seconds is > 0 and < 86400)
            {
                remaining = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            }
        }

        return new ScanProgress
        {
            State = state,
            Phase = Phase,
            CurrentPath = CurrentPath,
            FilesScanned = FilesScanned,
            FoldersScanned = FoldersScanned,
            BytesDiscovered = BytesDiscovered,
            ErrorCount = ErrorCount,
            Fraction = fraction,
            Elapsed = elapsed,
            EstimatedRemaining = remaining,
            ProviderName = providerName,
        };
    }
}
