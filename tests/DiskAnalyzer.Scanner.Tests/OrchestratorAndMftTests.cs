using System.Buffers.Binary;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Core.Threading;
using DiskAnalyzer.Scanner.Drives;
using DiskAnalyzer.Scanner.Providers;
using DiskAnalyzer.Scanner.Providers.Mft;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskAnalyzer.Scanner.Tests;

public class OrchestratorAndMftTests
{
    private static ScanOrchestrator CreateOrchestrator(params IFileSystemScanner[] extra) =>
        new([new StandardFileSystemScanner(NullLogger<StandardFileSystemScanner>.Instance), .. extra], new FolderAggregationService(), NullLogger<ScanOrchestrator>.Instance);

    [Fact]
    public async Task Orchestrator_ProducesAggregatedLiveResult()
    {
        using var temp = new TempTree();
        temp.File(@"a\b.bin", 1_234);
        ScanOrchestrator orchestrator = CreateOrchestrator();
        var states = new List<ScanState>();
        orchestrator.StateChanged += (_, s) => states.Add(s);

        ScanResult result = await orchestrator.ScanAsync(new ScanConfiguration { RootPath = temp.Root });

        Assert.Equal(ScanSource.Live, result.Source);
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(1_234, result.TotalBytes);
        Assert.True(result.Tree.IsAggregated);
        Assert.Contains(ScanState.Scanning, states);
        Assert.Equal(ScanState.Completed, states[^1]);
        Assert.False(orchestrator.IsBusy);
    }

    [Fact]
    public async Task Orchestrator_PauseResumeCancel()
    {
        using var temp = new TempTree();
        for (int d = 0; d < 40; d++)
        {
            for (int f = 0; f < 40; f++)
            {
                temp.File($@"d{d}\f{f}.bin", 10);
            }
        }

        var gate = new BlockingScanner();
        ScanOrchestrator orchestrator = CreateOrchestrator(gate);
        Task<ScanResult> scan = orchestrator.ScanAsync(new ScanConfiguration { RootPath = temp.Root });
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        orchestrator.Pause();
        Assert.Equal(ScanState.Paused, orchestrator.State);
        Assert.True(gate.PauseGate!.IsPaused);
        orchestrator.Resume();
        Assert.Equal(ScanState.Scanning, orchestrator.State);
        orchestrator.Cancel();

        ScanResult result = await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(ScanState.Cancelled, orchestrator.State);
    }

    [Fact]
    public async Task Orchestrator_FallsBack_WhenPreferredProviderFails()
    {
        using var temp = new TempTree();
        temp.File("x.bin", 77);
        ScanOrchestrator orchestrator = CreateOrchestrator(new FailingScanner());
        ScanResult result = await orchestrator.ScanAsync(new ScanConfiguration { RootPath = temp.Root });
        Assert.Equal("Standard enumeration", result.ProviderName);
        Assert.Equal(77, result.TotalBytes);
        Assert.Contains("failed", result.ProviderNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mft_IsUnavailable_ForFolderScans_AndNonNtfs()
    {
        var mft = new MftScanner(NullLogger<MftScanner>.Instance);
        var ntfs = new DriveInfoModel { RootPath = @"C:\", FileSystem = "NTFS", Kind = DriveKind.Fixed, IsReady = true };
        Assert.False(mft.CheckAvailability(new ScanConfiguration { RootPath = @"C:\Users", Drive = ntfs }).IsAvailable);
        Assert.False(mft.CheckAvailability(new ScanConfiguration { RootPath = @"E:\", Drive = ntfs with { RootPath = @"E:\", FileSystem = "exFAT" } }).IsAvailable);
        Assert.False(mft.CheckAvailability(new ScanConfiguration { RootPath = @"C:\", Drive = ntfs, UseMftWhenAvailable = false }).IsAvailable);
        Assert.Equal(MftScanner.IsProcessElevated, mft.CheckAvailability(new ScanConfiguration { RootPath = @"C:\", Drive = ntfs }).IsAvailable);
    }

    [Fact]
    public void Ntfs_RunListDecoding()
    {
        // 0x21: 1-byte length, 2-byte offset. Run 1: 0x18 clusters at LCN 0x5634. Run 2: 0x10 clusters at -0x10 relative. Sparse run of 8.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x21, 0x10, 0xF0, 0xFF, 0x01, 0x08, 0x00];
        List<DataRun> decoded = NtfsRecord.DecodeRuns(runs);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new DataRun(0x5634, 0x18, false), decoded[0]);
        Assert.Equal(new DataRun(0x5634 - 0x10, 0x10, false), decoded[1]);
        Assert.True(decoded[2].IsSparse);
        Assert.Equal(8, decoded[2].ClusterCount);
    }

    [Fact]
    public void Ntfs_AllocatedClusters_ExcludeSparseRuns()
    {
        // 0x18 real clusters, 0x10 real clusters, then a huge sparse run (like $BadClus:$Bad) of 0x3A0000 clusters.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x21, 0x10, 0xF0, 0xFF, 0x03, 0x00, 0x00, 0x3A, 0x00];
        Assert.Equal(0x18 + 0x10, NtfsRecord.CountAllocatedClusters(runs));
        Assert.Equal(0, NtfsRecord.CountAllocatedClusters([0x03, 0x00, 0x00, 0x3A, 0x00])); // entirely sparse
        Assert.Equal(-1, NtfsRecord.CountAllocatedClusters([0x21, 0x18])); // truncated
    }

    [Fact]
    public void Ntfs_FixupRestoresSectorTails_AndDetectsTornWrites()
    {
        byte[] record = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(record, NtfsRecord.SignatureFile);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0x30); // USA offset
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3); // USN + 2 entries
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30), 0xBEEF); // USN
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x32), 0x1111); // original tail of sector 1
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x34), 0x2222); // original tail of sector 2
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xBEEF);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0xBEEF);

        byte[] copy = (byte[])record.Clone();
        Assert.True(NtfsRecord.ApplyFixup(record));
        Assert.Equal(0x1111, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));

        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(1022), 0xDEAD); // torn write
        Assert.False(NtfsRecord.ApplyFixup(copy));
    }

    [Fact]
    public void Ntfs_BootSectorParsing()
    {
        byte[] boot = new byte[512];
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(0x0B), 512);
        boot[0x0D] = 8; // 4 KB clusters
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x30), 786432);
        boot[0x40] = 0xF6; // -10 → 1024-byte records
        NtfsBootSector parsed = NtfsBootSector.Parse(boot);
        Assert.Equal(4096, parsed.BytesPerCluster);
        Assert.Equal(1024, parsed.RecordSize);
        Assert.Equal(786432, parsed.MftLcn);
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(new byte[512]));
    }

    [Fact]
    public async Task DriveDiscovery_FindsSystemDrive()
    {
        var discovery = new DriveDiscoveryService(NullLogger<DriveDiscoveryService>.Instance);
        IReadOnlyList<DriveInfoModel> drives = await discovery.GetDrivesAsync();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        DriveInfoModel system = Assert.Single(drives, d => string.Equals(d.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(system.IsReady);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.UtilizationPercent, 0, 100);
        Assert.Equal(system.TotalBytes - system.FreeBytes, system.UsedBytes);
        Assert.False(string.IsNullOrEmpty(system.FileSystem));
        Assert.True(system.ClusterSize > 0);
    }

    /// <summary>Waits (cooperatively) so pause/cancel can be exercised deterministically.</summary>
    private sealed class BlockingScanner : IFileSystemScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncPauseGate? PauseGate { get; private set; }

        public string Name => "blocking";

        public int Priority => 50;

        public ScannerAvailability CheckAvailability(ScanConfiguration configuration) => ScannerAvailability.Available;

        public async Task<ScanTree> ScanAsync(ScanConfiguration configuration, ScanProgressTracker progress, AsyncPauseGate pauseGate, CancellationToken cancellationToken)
        {
            PauseGate = pauseGate;
            var tree = new ScanTree(configuration.RootPath);
            Started.TrySetResult();
            try
            {
                while (true)
                {
                    await pauseGate.WaitIfPausedAsync(cancellationToken);
                    await Task.Delay(10, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                tree.AddFlags(ScanTree.RootIndex, NodeFlags.Partial);
                return tree;
            }
        }
    }

    private sealed class FailingScanner : IFileSystemScanner
    {
        public string Name => "failing";

        public int Priority => 100;

        public ScannerAvailability CheckAvailability(ScanConfiguration configuration) => ScannerAvailability.Available;

        public Task<ScanTree> ScanAsync(ScanConfiguration configuration, ScanProgressTracker progress, AsyncPauseGate pauseGate, CancellationToken cancellationToken) =>
            throw new IOException("simulated volume failure");
    }
}
