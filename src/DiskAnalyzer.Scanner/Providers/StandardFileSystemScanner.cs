using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading.Channels;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Threading;
using DiskAnalyzer.Scanner.Enumeration;
using DiskAnalyzer.Scanner.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Scanner.Providers;

/// <summary>
/// Portable scanner built on <c>System.IO.Enumeration</c>.
/// <para>
/// Pipeline: a fixed pool of enumeration workers pulls folders from an (unbounded) work queue and
/// streams entries in batches through a <b>bounded</b> channel to a single aggregator, which is the only
/// writer of the <see cref="ScanTree"/> and which enqueues newly found sub-folders. No task per folder,
/// no recursion, and back-pressure caps memory held in flight.
/// </para>
/// </summary>
public sealed class StandardFileSystemScanner(ILogger<StandardFileSystemScanner> logger) : IFileSystemScanner
{
    private const int BatchSize = 4096;
    private const int BatchChannelCapacity = 256;

    public string Name => "Standard enumeration";

    public int Priority => 0;

    public ScannerAvailability CheckAvailability(ScanConfiguration configuration) =>
        Directory.Exists(configuration.RootPath)
            ? ScannerAvailability.Available
            : ScannerAvailability.Unavailable($"Folder not found: {configuration.RootPath}");

    public async Task<ScanTree> ScanAsync(ScanConfiguration configuration, ScanProgressTracker progress, AsyncPauseGate pauseGate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(pauseGate);

        string rootPath = NormalizeRoot(configuration.RootPath);
        var rootInfo = new DirectoryInfo(rootPath);
        if (!rootInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {rootPath}");
        }

        var tree = new ScanTree(rootPath, rootInfo.LastWriteTimeUtc.Ticks, rootInfo.Attributes);
        var run = new ScanRun(configuration, tree, progress, pauseGate, logger, cancellationToken);
        await run.ExecuteAsync().ConfigureAwait(false);
        return tree;
    }

    internal static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        return string.Equals(root, full, StringComparison.OrdinalIgnoreCase) ? full : Path.TrimEndingDirectorySeparator(full);
    }

    private readonly record struct WorkItem(int NodeIndex, string Path);

    private sealed class DirectoryBatch
    {
        public required int ParentIndex { get; init; }

        public required string ParentPath { get; init; }

        public EntryData[]? Entries { get; init; }

        public int Count { get; init; }

        /// <summary>Last batch for this folder; the folder is complete once processed.</summary>
        public bool IsFinal { get; init; }

        public int ErrorCode { get; init; }

        public bool FailedToOpen { get; init; }
    }

    /// <summary>State of one scan execution.</summary>
    private sealed class ScanRun
    {
        private readonly ScanConfiguration _config;
        private readonly ScanTree _tree;
        private readonly ScanProgressTracker _progress;
        private readonly AsyncPauseGate _pause;
        private readonly ILogger _logger;
        private readonly CancellationToken _token;
        private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly Channel<DirectoryBatch> _batches;
        private readonly ConcurrentDictionary<ulong, byte> _hardLinks = new();
        private readonly ConcurrentDictionary<ulong, byte> _visitedReparseTargets = new();
        private readonly long _clusterSize;
        private readonly int _workerCount;
        private int _pendingDirectories;

        public ScanRun(ScanConfiguration config, ScanTree tree, ScanProgressTracker progress, AsyncPauseGate pause, ILogger logger, CancellationToken token)
        {
            _config = config;
            _tree = tree;
            _progress = progress;
            _pause = pause;
            _logger = logger;
            _token = token;
            _clusterSize = config.Drive?.ClusterSize is > 0 ? config.Drive.ClusterSize : 4096;
            _workerCount = config.ResolveConcurrency();
            _batches = Channel.CreateBounded<DirectoryBatch>(new BoundedChannelOptions(BatchChannelCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public async Task ExecuteAsync()
        {
            _progress.Phase = "Scanning folders";
            _logger.LogInformation("Standard scan of {Root} with {Workers} workers", _tree.RootPath, _workerCount);

            _pendingDirectories = 1;
            _work.Writer.TryWrite(new WorkItem(ScanTree.RootIndex, _tree.RootPath));
            _progress.AddFolders(1);

            Task aggregator = Task.Run(AggregateAsync, CancellationToken.None);
            var workers = new Task[_workerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(WorkerAsync, CancellationToken.None);
            }

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                _batches.Writer.TryComplete();
            }

            await aggregator.ConfigureAwait(false);

            if (_token.IsCancellationRequested)
            {
                _tree.AddFlags(ScanTree.RootIndex, NodeFlags.Partial);
                DrainPendingBatches();
            }
        }

        private void DrainPendingBatches()
        {
            while (_batches.Reader.TryRead(out DirectoryBatch? batch))
            {
                ReturnBuffer(batch);
            }
        }

        // ------------------------------------------------------------ workers

        private async Task WorkerAsync()
        {
            try
            {
                while (await _work.Reader.WaitToReadAsync(_token).ConfigureAwait(false))
                {
                    while (_work.Reader.TryRead(out WorkItem item))
                    {
                        await _pause.WaitIfPausedAsync(_token).ConfigureAwait(false);
                        await EnumerateDirectoryAsync(item).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                // Cancellation: stop quietly, the partial tree is returned.
            }
        }

        private async Task EnumerateDirectoryAsync(WorkItem item)
        {
            _progress.CurrentPath = item.Path;
            EntryData[] buffer = ArrayPool<EntryData>.Shared.Rent(BatchSize);
            int count = 0;
            int error = 0;
            bool failedToOpen = false;
            try
            {
                using var enumerator = new DirectoryEntryEnumerator(item.Path, _config.IncludeHidden, _config.IncludeSystem);
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _progress.RecordError(item.Path, ex.Message);
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    EntryData entry = enumerator.Current;
                    if (!entry.IsDirectory)
                    {
                        CompleteFileEntry(ref entry, item.Path);
                    }

                    buffer[count++] = entry;
                    if (count == BatchSize)
                    {
                        _token.ThrowIfCancellationRequested();
                        await _batches.Writer.WriteAsync(new DirectoryBatch { ParentIndex = item.NodeIndex, ParentPath = item.Path, Entries = buffer, Count = count }, _token).ConfigureAwait(false);
                        buffer = ArrayPool<EntryData>.Shared.Rent(BatchSize);
                        count = 0;
                        _pause.WaitIfPaused(_token);
                    }
                }

                error = enumerator.LastError;
                failedToOpen = enumerator.FailedToOpen;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                error = ex.HResult;
                failedToOpen = count == 0;
                _progress.RecordError(item.Path, ex.Message);
            }

            await _batches.Writer.WriteAsync(new DirectoryBatch
            {
                ParentIndex = item.NodeIndex,
                ParentPath = item.Path,
                Entries = buffer,
                Count = count,
                IsFinal = true,
                ErrorCode = error,
                FailedToOpen = failedToOpen,
            }, _token).ConfigureAwait(false);
        }

        /// <summary>Fills allocated size and hard-link status for a file (worker thread, before batching).</summary>
        private void CompleteFileEntry(ref EntryData entry, string directory)
        {
            const FileAttributes physicalSizeAttributes = FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline
                | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

            string? path = null;
            if ((entry.Attributes & physicalSizeAttributes) != 0)
            {
                path = Path.Join(directory, entry.Name);
                entry.Allocated = RoundToCluster(NativeMethods.TryGetCompressedFileSize(path) ?? entry.Length);
            }
            else
            {
                entry.Allocated = RoundToCluster(entry.Length);
            }

            if (_config.DeduplicateHardLinks)
            {
                path ??= Path.Join(directory, entry.Name);
                using SafeFileHandle handle = NativeMethods.OpenForAttributes(path);
                if (!handle.IsInvalid && NativeMethods.GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1)
                {
                    ulong key = info.FileIndex ^ ((ulong)info.VolumeSerialNumber << 48);
                    if (!_hardLinks.TryAdd(key, 0))
                    {
                        entry.Flags |= NodeFlags.HardLinkDuplicate;
                    }
                }
            }
        }

        private long RoundToCluster(long length) => length <= 0 ? 0 : (length + _clusterSize - 1) / _clusterSize * _clusterSize;

        // ------------------------------------------------------------ aggregator (single writer)

        private async Task AggregateAsync()
        {
            try
            {
                await foreach (DirectoryBatch batch in _batches.Reader.ReadAllAsync(_token).ConfigureAwait(false))
                {
                    Apply(batch);
                    ReturnBuffer(batch);
                    if (batch.IsFinal && --_pendingDirectories == 0)
                    {
                        _work.Writer.TryComplete();
                    }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                _work.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan aggregator failed");
                _work.Writer.TryComplete(ex);
                _batches.Writer.TryComplete(ex); // unblocks workers waiting on a full channel
                throw;
            }
        }

        private void Apply(DirectoryBatch batch)
        {
            if (batch.FailedToOpen && batch.Count == 0)
            {
                _tree.AddFlags(batch.ParentIndex, NodeFlags.Inaccessible);
                if (batch.ErrorCode != 0)
                {
                    _progress.RecordError(batch.ParentPath, DescribeError(batch.ErrorCode));
                }

                return;
            }

            if (batch.ErrorCode != 0 && batch.IsFinal)
            {
                _tree.AddFlags(batch.ParentIndex, NodeFlags.Partial);
                _progress.RecordError(batch.ParentPath, DescribeError(batch.ErrorCode));
            }

            EntryData[] entries = batch.Entries!;
            long files = 0, bytes = 0, folders = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                ref EntryData e = ref entries[i];
                if (e.IsDirectory)
                {
                    int index = _tree.AddDirectory(batch.ParentIndex, e.Name, e.LastWriteUtcTicks, e.Attributes, e.Flags & ~NodeFlags.Directory);
                    folders++;
                    string childPath = Path.Join(batch.ParentPath, e.Name);
                    if ((e.Flags & NodeFlags.ReparsePoint) == 0 || ShouldFollowReparsePoint(childPath))
                    {
                        _pendingDirectories++;
                        _work.Writer.TryWrite(new WorkItem(index, childPath));
                    }
                }
                else
                {
                    _tree.AddFile(batch.ParentIndex, e.Name, e.Length, e.Allocated, e.LastWriteUtcTicks, e.Attributes, e.Flags);
                    files++;
                    if ((e.Flags & NodeFlags.HardLinkDuplicate) == 0)
                    {
                        bytes += e.Length;
                    }
                }
            }

            _progress.AddFiles(files, bytes);
            _progress.AddFolders(folders);
        }

        /// <summary>
        /// Directory reparse points (junctions, symlinks, mount points) are not followed unless enabled.
        /// When following, a target is visited at most once and never when it is an ancestor of the link (loop guard).
        /// </summary>
        private bool ShouldFollowReparsePoint(string linkPath)
        {
            if (!_config.FollowReparsePoints)
            {
                return false;
            }

            using SafeFileHandle handle = NativeMethods.OpenForAttributes(linkPath, followReparsePoint: true);
            if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var info))
            {
                return false;
            }

            ulong key = info.FileIndex ^ ((ulong)info.VolumeSerialNumber << 48);
            if (!_visitedReparseTargets.TryAdd(key, 0))
            {
                return false;
            }

            string? target = NativeMethods.TryGetFinalPath(handle);
            if (target is null)
            {
                return false;
            }

            string normalizedTarget = Path.TrimEndingDirectorySeparator(target) + "\\";
            return !(linkPath + "\\").StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase);
        }

        private static void ReturnBuffer(DirectoryBatch batch)
        {
            if (batch.Entries is not null)
            {
                ArrayPool<EntryData>.Shared.Return(batch.Entries, clearArray: true);
            }
        }

        private static string DescribeError(int error) => error switch
        {
            NativeMethods.ErrorAccessDenied => "Access denied",
            NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound => "Folder no longer exists",
            _ => new Win32Exception(error & 0xFFFF).Message,
        };
    }
}
