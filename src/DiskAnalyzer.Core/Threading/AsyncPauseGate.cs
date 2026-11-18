namespace DiskAnalyzer.Core.Threading;

/// <summary>
/// A gate that workers await between units of work. While paused, awaiting blocks
/// (asynchronously) until <see cref="Resume"/> is called or the token is cancelled.
/// </summary>
public sealed class AsyncPauseGate
{
    private TaskCompletionSource? _paused;

    public bool IsPaused => Volatile.Read(ref _paused) is not null;

    public void Pause() =>
        Interlocked.CompareExchange(ref _paused, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    public void Resume() => Interlocked.Exchange(ref _paused, null)?.TrySetResult();

    public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? paused = Volatile.Read(ref _paused);
        return paused is null ? ValueTask.CompletedTask : new ValueTask(paused.Task.WaitAsync(cancellationToken));
    }

    /// <summary>Synchronous variant for tight loops on worker threads.</summary>
    public void WaitIfPaused(CancellationToken cancellationToken)
    {
        TaskCompletionSource? paused = Volatile.Read(ref _paused);
        if (paused is not null)
        {
            paused.Task.Wait(cancellationToken);
        }
    }
}
