using System.Diagnostics;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DiskAnalyzer.Infrastructure.Database;

/// <summary>
/// SQLite-backed scan history and cache.
/// <para>
/// Every folder of a scan is stored (with its aggregated totals), plus files at or above a size threshold,
/// so the database stays a sensible size while folder totals remain exact. Loaded scans are always
/// labelled <see cref="ScanSource.Cached"/> or <see cref="ScanSource.Historical"/>, never live.
/// </para>
/// </summary>
public sealed class SqliteScanCacheService : IScanCacheService
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly ILogger<SqliteScanCacheService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteScanCacheService(ILogger<SqliteScanCacheService> logger)
        : this(AppPaths.DatabaseFile, logger)
    {
    }

    public SqliteScanCacheService(string databasePath, ILogger<SqliteScanCacheService> logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        _logger = logger;
    }

    /// <summary>Older scans of the same root beyond this count are deleted after each save (0 = keep all).</summary>
    public int MaxScansPerRoot { get; set; } = 10;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, """
            PRAGMA auto_vacuum = INCREMENTAL;
            CREATE TABLE IF NOT EXISTS scans (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                drive_id        TEXT    NOT NULL,
                drive_name      TEXT    NOT NULL,
                root_path       TEXT    NOT NULL,
                scanned_utc     INTEGER NOT NULL,
                total_bytes     INTEGER NOT NULL,
                total_allocated INTEGER NOT NULL,
                file_count      INTEGER NOT NULL,
                folder_count    INTEGER NOT NULL,
                duration_ms     INTEGER NOT NULL,
                status          INTEGER NOT NULL,
                provider        TEXT    NOT NULL,
                error_count     INTEGER NOT NULL,
                drive_capacity  INTEGER NOT NULL,
                drive_free      INTEGER NOT NULL,
                file_threshold  INTEGER NOT NULL,
                stored_nodes    INTEGER NOT NULL,
                complete        INTEGER NOT NULL DEFAULT 0,
                schema_version  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_scans_root ON scans (drive_id, root_path, scanned_utc);
            CREATE TABLE IF NOT EXISTS nodes (
                scan_id    INTEGER NOT NULL,
                node_id    INTEGER NOT NULL,
                parent_id  INTEGER NOT NULL,
                name       TEXT    NOT NULL,
                flags      INTEGER NOT NULL,
                size       INTEGER NOT NULL,
                allocated  INTEGER NOT NULL,
                modified   INTEGER NOT NULL,
                attributes INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                dir_count  INTEGER NOT NULL,
                PRIMARY KEY (scan_id, node_id)
            ) WITHOUT ROWID;
            DELETE FROM nodes WHERE scan_id IN (SELECT id FROM scans WHERE complete = 0);
            DELETE FROM scans WHERE complete = 0;
            """, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task<long> SaveAsync(ScanResult result, long fileThresholdBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        ScanTree tree = result.Tree;
        fileThresholdBytes = Math.Max(0, fileThresholdBytes);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            long scanId;
            await using (SqliteCommand insertScan = connection.CreateCommand())
            {
                insertScan.Transaction = transaction;
                insertScan.CommandText = """
                    INSERT INTO scans (drive_id, drive_name, root_path, scanned_utc, total_bytes, total_allocated, file_count, folder_count,
                                       duration_ms, status, provider, error_count, drive_capacity, drive_free, file_threshold, stored_nodes, complete, schema_version)
                    VALUES ($driveId, $driveName, $root, $scanned, $total, $alloc, $files, $folders, $duration, $status, $provider, $errors,
                            $capacity, $free, $threshold, 0, 0, $schema);
                    SELECT last_insert_rowid();
                    """;
                insertScan.Parameters.AddWithValue("$driveId", DriveIdFor(result));
                insertScan.Parameters.AddWithValue("$driveName", result.Drive?.DisplayName ?? result.RootPath);
                insertScan.Parameters.AddWithValue("$root", result.RootPath);
                insertScan.Parameters.AddWithValue("$scanned", result.StartedUtc.Ticks);
                insertScan.Parameters.AddWithValue("$total", result.TotalBytes);
                insertScan.Parameters.AddWithValue("$alloc", result.TotalAllocatedBytes);
                insertScan.Parameters.AddWithValue("$files", result.TotalFiles);
                insertScan.Parameters.AddWithValue("$folders", result.TotalFolders);
                insertScan.Parameters.AddWithValue("$duration", (long)result.Duration.TotalMilliseconds);
                insertScan.Parameters.AddWithValue("$status", (int)result.Status);
                insertScan.Parameters.AddWithValue("$provider", result.ProviderName);
                insertScan.Parameters.AddWithValue("$errors", result.ErrorCount);
                insertScan.Parameters.AddWithValue("$capacity", result.Drive?.TotalBytes ?? 0);
                insertScan.Parameters.AddWithValue("$free", result.Drive?.FreeBytes ?? 0);
                insertScan.Parameters.AddWithValue("$threshold", fileThresholdBytes);
                insertScan.Parameters.AddWithValue("$schema", SchemaVersion);
                scanId = (long)(await insertScan.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            int stored = 0;
            await using (SqliteCommand insertNode = connection.CreateCommand())
            {
                insertNode.Transaction = transaction;
                insertNode.CommandText = """
                    INSERT INTO nodes (scan_id, node_id, parent_id, name, flags, size, allocated, modified, attributes, file_count, dir_count)
                    VALUES ($scan, $id, $parent, $name, $flags, $size, $alloc, $modified, $attrs, $files, $dirs)
                    """;
                SqliteParameter pScan = insertNode.Parameters.Add("$scan", SqliteType.Integer);
                SqliteParameter pId = insertNode.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter pParent = insertNode.Parameters.Add("$parent", SqliteType.Integer);
                SqliteParameter pName = insertNode.Parameters.Add("$name", SqliteType.Text);
                SqliteParameter pFlags = insertNode.Parameters.Add("$flags", SqliteType.Integer);
                SqliteParameter pSize = insertNode.Parameters.Add("$size", SqliteType.Integer);
                SqliteParameter pAlloc = insertNode.Parameters.Add("$alloc", SqliteType.Integer);
                SqliteParameter pModified = insertNode.Parameters.Add("$modified", SqliteType.Integer);
                SqliteParameter pAttrs = insertNode.Parameters.Add("$attrs", SqliteType.Integer);
                SqliteParameter pFiles = insertNode.Parameters.Add("$files", SqliteType.Integer);
                SqliteParameter pDirs = insertNode.Parameters.Add("$dirs", SqliteType.Integer);
                insertNode.Prepare();
                pScan.Value = scanId;

                // Stored ids are dense and keep the parent-before-child order of the tree.
                int[] storedId = new int[tree.Count];
                for (int i = 0; i < tree.Count; i++)
                {
                    bool isDir = tree.IsDirectory(i);
                    if (!isDir && tree.GetSize(i) < fileThresholdBytes)
                    {
                        storedId[i] = -1;
                        continue;
                    }

                    if ((i & 0x3FFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report((double)i / tree.Count);
                    }

                    storedId[i] = stored;
                    pId.Value = stored;
                    pParent.Value = i == 0 ? -1 : storedId[tree.GetParent(i)];
                    pName.Value = tree.GetName(i);
                    pFlags.Value = (int)tree.GetFlags(i);
                    pSize.Value = tree.GetSize(i);
                    pAlloc.Value = tree.GetAllocated(i);
                    pModified.Value = tree.GetLastWriteUtcTicks(i);
                    pAttrs.Value = (int)tree.GetAttributes(i);
                    pFiles.Value = tree.GetFileCount(i);
                    pDirs.Value = tree.GetDirectoryCount(i);
                    await insertNode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    stored++;
                }
            }

            await using (SqliteCommand finish = connection.CreateCommand())
            {
                finish.Transaction = transaction;
                finish.CommandText = "UPDATE scans SET stored_nodes = $n, complete = 1 WHERE id = $id";
                finish.Parameters.AddWithValue("$n", stored);
                finish.Parameters.AddWithValue("$id", scanId);
                await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            _logger.LogInformation("Saved scan {ScanId} of {Root}: {Stored:N0} of {Total:N0} nodes in {Elapsed}", scanId, result.RootPath, stored, tree.Count, stopwatch.Elapsed);

            await PruneAsync(connection, DriveIdFor(result), result.RootPath, cancellationToken).ConfigureAwait(false);
            result.HistoryId = scanId;
            return scanId;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ScanHistory>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {ScanColumns} FROM scans WHERE complete = 1 ORDER BY scanned_utc DESC";
        return await ReadHistoryAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanHistory?> GetLatestForRootAsync(string driveId, string rootPath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {ScanColumns} FROM scans WHERE complete = 1 AND drive_id = $d AND root_path = $r COLLATE NOCASE ORDER BY scanned_utc DESC LIMIT 1";
        command.Parameters.AddWithValue("$d", driveId);
        command.Parameters.AddWithValue("$r", rootPath);
        IReadOnlyList<ScanHistory> rows = await ReadHistoryAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<ScanResult> LoadAsync(long scanId, ScanSource source, CancellationToken cancellationToken = default)
    {
        if (source == ScanSource.Live)
        {
            throw new ArgumentException("Stored scans cannot be presented as live results.", nameof(source));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ScanHistory info = await GetScanAsync(connection, scanId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Scan {scanId} was not found.");

        ScanTree? tree = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT node_id, parent_id, name, flags, size, allocated, modified, attributes, file_count, dir_count FROM nodes WHERE scan_id = $s ORDER BY node_id";
            command.Parameters.AddWithValue("$s", scanId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int parent = reader.GetInt32(1);
                string name = reader.GetString(2);
                var flags = (NodeFlags)reader.GetInt32(3);
                long size = reader.GetInt64(4);
                long allocated = reader.GetInt64(5);
                long modified = reader.GetInt64(6);
                var attributes = (FileAttributes)reader.GetInt32(7);
                int index;
                if (tree is null)
                {
                    tree = new ScanTree(name, modified, attributes);
                    tree.AddFlags(ScanTree.RootIndex, flags & ~NodeFlags.Directory);
                    index = ScanTree.RootIndex;
                }
                else if ((flags & NodeFlags.Directory) != 0)
                {
                    index = tree.AddDirectory(parent, name, modified, attributes, flags & ~NodeFlags.Directory);
                }
                else
                {
                    index = tree.AddFile(parent, name, size, allocated, modified, attributes, flags);
                }

                if ((flags & NodeFlags.Directory) != 0)
                {
                    tree.SetSize(index, size, allocated);
                    tree.SetCounts(index, reader.GetInt32(8), reader.GetInt32(9));
                    if (info.StoredFileThresholdBytes > 0)
                    {
                        tree.AddFlags(index, NodeFlags.Partial);
                    }
                }
            }
        }

        if (tree is null)
        {
            throw new InvalidDataException($"Scan {scanId} has no stored nodes.");
        }

        // Folder totals come from the stored (complete) aggregation; re-aggregating would lose unstored small files.
        tree.IsAggregated = true;
        _logger.LogInformation("Loaded scan {ScanId} ({Nodes:N0} nodes) in {Elapsed}", scanId, tree.Count, stopwatch.Elapsed);

        return new ScanResult
        {
            Tree = tree,
            RootPath = info.RootPath,
            Drive = new DriveInfoModel
            {
                RootPath = Path.GetPathRoot(info.RootPath) ?? info.RootPath,
                VolumeLabel = info.DriveName,
                TotalBytes = info.DriveCapacityBytes,
                FreeBytes = info.DriveFreeBytes,
                IsReady = false,
            },
            Source = source,
            Status = info.Status,
            StartedUtc = info.ScannedUtc,
            Duration = info.Duration,
            ProviderName = info.ProviderName,
            ErrorCount = info.ErrorCount,
            StoredFileThresholdBytes = info.StoredFileThresholdBytes,
            HistoryId = scanId,
        };
    }

    public async Task DeleteAsync(long scanId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await DeleteScanAsync(connection, scanId, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ScanComparison> CompareAsync(long olderScanId, long newerScanId, CancellationToken cancellationToken = default)
    {
        ScanResult a = await LoadAsync(olderScanId, ScanSource.Historical, cancellationToken).ConfigureAwait(false);
        ScanResult b = await LoadAsync(newerScanId, ScanSource.Historical, cancellationToken).ConfigureAwait(false);
        if (a.StartedUtc > b.StartedUtc)
        {
            (a, b) = (b, a);
            (olderScanId, newerScanId) = (newerScanId, olderScanId);
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ScanHistory olderInfo = (await GetScanAsync(connection, olderScanId, cancellationToken).ConfigureAwait(false))!;
        ScanHistory newerInfo = (await GetScanAsync(connection, newerScanId, cancellationToken).ConfigureAwait(false))!;
        return await Task.Run(() => ScanComparer.Compare(olderInfo, a.Tree, newerInfo, b.Tree, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private const string ScanColumns = "id, drive_id, drive_name, root_path, scanned_utc, total_bytes, total_allocated, file_count, folder_count, duration_ms, status, provider, error_count, drive_capacity, drive_free, file_threshold, stored_nodes";

    public static string DriveIdFor(ScanResult result) => result.Drive is { VolumeSerialNumber: > 0 } drive ? drive.DriveId : "path:" + (Path.GetPathRoot(result.RootPath) ?? result.RootPath).ToUpperInvariant();

    private async Task PruneAsync(SqliteConnection connection, string driveId, string rootPath, CancellationToken cancellationToken)
    {
        if (MaxScansPerRoot <= 0)
        {
            return;
        }

        var stale = new List<long>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM scans WHERE drive_id = $d AND root_path = $r COLLATE NOCASE ORDER BY scanned_utc DESC LIMIT -1 OFFSET $keep";
            command.Parameters.AddWithValue("$d", driveId);
            command.Parameters.AddWithValue("$r", rootPath);
            command.Parameters.AddWithValue("$keep", MaxScansPerRoot);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stale.Add(reader.GetInt64(0));
            }
        }

        foreach (long id in stale)
        {
            await DeleteScanAsync(connection, id, cancellationToken).ConfigureAwait(false);
        }

        if (stale.Count > 0)
        {
            _logger.LogInformation("Pruned {Count} old scans of {Root}", stale.Count, rootPath);
            await ExecuteAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteScanAsync(SqliteConnection connection, long scanId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM nodes WHERE scan_id = $id; DELETE FROM scans WHERE id = $id;";
        command.Parameters.AddWithValue("$id", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScanHistory?> GetScanAsync(SqliteConnection connection, long scanId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {ScanColumns} FROM scans WHERE id = $id AND complete = 1";
        command.Parameters.AddWithValue("$id", scanId);
        IReadOnlyList<ScanHistory> rows = await ReadHistoryAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<IReadOnlyList<ScanHistory>> ReadHistoryAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<ScanHistory>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ScanHistory
            {
                ScanId = reader.GetInt64(0),
                DriveId = reader.GetString(1),
                DriveName = reader.GetString(2),
                RootPath = reader.GetString(3),
                ScannedUtc = new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                TotalBytes = reader.GetInt64(5),
                TotalAllocatedBytes = reader.GetInt64(6),
                FileCount = reader.GetInt32(7),
                FolderCount = reader.GetInt32(8),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(9)),
                Status = (ScanStatus)reader.GetInt32(10),
                ProviderName = reader.GetString(11),
                ErrorCount = reader.GetInt64(12),
                DriveCapacityBytes = reader.GetInt64(13),
                DriveFreeBytes = reader.GetInt64(14),
                StoredFileThresholdBytes = reader.GetInt64(15),
                StoredNodeCount = reader.GetInt32(16),
            });
        }

        return list;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
