using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Threading;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.Scanner;

/// <summary>
/// Coordinates one scan at a time: chooses the best available provider (MFT when possible,
/// otherwise standard enumeration), falls back on provider failure, supports pause/resume/cancel,
/// aggregates folder totals and produces a <see cref="ScanResult"/>. Has no UI dependency;
/// the UI polls <see cref="GetProgress"/>.
/// </summary>
public sealed class ScanOrchestrator : IScanOrchestrator
{
    private readonly IReadOnlyList<IFileSystemScanner> _scanners;
    private readonly IFolderAggregationService _aggregation;
    private readonly ILogger<ScanOrchestrator> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private AsyncPauseGate? _pause;
    private ScanProgressTracker? _tracker;
    private string? _providerName;
    private ScanState _state = ScanState.Idle;
    private ScanState _stateBeforePause;

    public ScanOrchestrator(IEnumerable<IFileSystemScanner> scanners, IFolderAggregationService aggregation, ILogger<ScanOrchestrator> logger)
    {
        _scanners = scanners.OrderByDescending(s => s.Priority).ToList();
        _aggregation = aggregation;
        _logger = logger;
    }

    public event EventHandler<ScanState>? StateChanged;

    public ScanState State => _state;

    public bool IsBusy => _state is ScanState.Preparing or ScanState.Scanning or ScanState.Paused or ScanState.Aggregating;

    public ScanProgress GetProgress() => _tracker?.Snapshot(_state, _providerName) ?? ScanProgress.Idle with { State = _state };

    public async Task<ScanResult> ScanAsync(ScanConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("A scan is already running.");
            }

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pause = new AsyncPauseGate();
            _tracker = new ScanProgressTracker
            {
                ExpectedBytes = configuration.IsDriveRoot ? configuration.Drive!.UsedBytes : 0,
            };
            _providerName = null;
        }

        CancellationToken token = _cts.Token;
        ScanProgressTracker tracker = _tracker;
        DateTime startedUtc = DateTime.UtcNow;
        SetState(ScanState.Preparing);
        tracker.Start();

        try
        {
            var notes = new List<string>();
            ScanTree? tree = null;
            IFileSystemScanner? usedScanner = null;
            foreach (IFileSystemScanner scanner in _scanners)
            {
                ScannerAvailability availability = scanner.CheckAvailability(configuration);
                if (!availability.IsAvailable)
                {
                    if (scanner.Priority > 0)
                    {
                        notes.Add(availability.Reason);
                    }

                    _logger.LogInformation("Provider {Provider} skipped: {Reason}", scanner.Name, availability.Reason);
                    continue;
                }

                _providerName = scanner.Name;
                SetState(ScanState.Scanning);
                try
                {
                    tree = await scanner.ScanAsync(configuration, tracker, _pause!, token).ConfigureAwait(false);
                    usedScanner = scanner;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && scanner != _scanners[^1])
                {
                    _logger.LogWarning(ex, "Provider {Provider} failed; falling back", scanner.Name);
                    notes.Add($"{scanner.Name} failed ({ex.Message}); used standard enumeration instead.");
                    tracker = ResetTracker(configuration);
                }
            }

            if (tree is null || usedScanner is null)
            {
                throw new InvalidOperationException(notes.Count > 0 ? string.Join(" ", notes) : $"No scanner can read {configuration.RootPath}.");
            }

            SetState(ScanState.Aggregating);
            tracker.Phase = "Calculating folder sizes";
            await Task.Run(() => _aggregation.Aggregate(tree), CancellationToken.None).ConfigureAwait(false);
            tracker.Stop();

            bool cancelled = token.IsCancellationRequested;
            ScanStatus status = cancelled ? ScanStatus.Cancelled : tracker.ErrorCount > 0 ? ScanStatus.CompletedWithErrors : ScanStatus.Completed;
            var result = new ScanResult
            {
                Tree = tree,
                RootPath = tree.RootPath,
                Drive = configuration.Drive,
                Source = ScanSource.Live,
                Status = status,
                StartedUtc = startedUtc,
                Duration = tracker.Elapsed,
                ProviderName = usedScanner.Name,
                ErrorCount = tracker.ErrorCount,
                ErrorSamples = tracker.ErrorSamples,
                ProviderNote = notes.Count > 0 ? string.Join(" ", notes) : null,
            };

            _logger.LogInformation(
                "Scan of {Root} {Status} in {Duration}: {Files:N0} files, {Folders:N0} folders, {Bytes:N0} bytes, {Errors} errors, provider {Provider}",
                result.RootPath, status, result.Duration, result.TotalFiles, result.TotalFolders, result.TotalBytes, result.ErrorCount, result.ProviderName);

            SetState(cancelled ? ScanState.Cancelled : ScanState.Completed);
            return result;
        }
        catch (Exception ex)
        {
            tracker.Stop();
            _logger.LogError(ex, "Scan of {Root} failed", configuration.RootPath);
            SetState(ScanState.Failed);
            throw;
        }
    }

    private ScanProgressTracker ResetTracker(ScanConfiguration configuration)
    {
        var tracker = new ScanProgressTracker { ExpectedBytes = configuration.IsDriveRoot ? configuration.Drive!.UsedBytes : 0 };
        tracker.Start();
        _tracker = tracker;
        return tracker;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state is not (ScanState.Scanning or ScanState.Preparing) || _pause is null)
            {
                return;
            }

            _stateBeforePause = _state;
            _pause.Pause();
            _tracker?.PauseClock();
        }

        SetState(ScanState.Paused);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != ScanState.Paused || _pause is null)
            {
                return;
            }

            _tracker?.ResumeClock();
            _pause.Resume();
        }

        SetState(_stateBeforePause);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!IsBusy)
            {
                return;
            }

            _cts?.Cancel();
            _pause?.Resume(); // let paused workers observe cancellation
            _tracker?.ResumeClock();
        }
    }

    private void SetState(ScanState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
