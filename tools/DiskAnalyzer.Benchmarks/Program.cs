using System.Diagnostics;
using System.Globalization;
using System.Text;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Core.Threading;
using DiskAnalyzer.Scanner.Providers;
using DiskAnalyzer.Visualization.Rendering;
using DiskAnalyzer.Visualization.Treemap;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskAnalyzer.Benchmarks;

/// <summary>
/// Benchmark utility.
///   DiskAnalyzer.Benchmarks suite  --data &lt;dir&gt; [--keep] [--sizes 100000,500000,1000000]
///   DiskAnalyzer.Benchmarks scan   --root &lt;dir&gt; [--runs 3]
/// "suite" generates synthetic trees (balanced, deep, one huge folder, large sparse files), scans each,
/// and measures duration, throughput, memory, CPU, scheduling latency, aggregation, search and treemap times.
/// Generated data is deleted afterwards unless --keep is given.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.FirstOrDefault() ?? "help";
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        try
        {
            switch (command)
            {
                case "suite":
                {
                    string data = Arg("--data") ?? Path.Combine(Path.GetTempPath(), "DiskAnalyzerBench");
                    int[] sizes = (Arg("--sizes") ?? "100000,500000,1000000").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    RunSuite(data, sizes, keep: args.Contains("--keep"));
                    return 0;
                }

                case "scan":
                {
                    string root = Arg("--root") ?? throw new ArgumentException("--root is required");
                    int runs = int.Parse(Arg("--runs") ?? "3", CultureInfo.InvariantCulture);
                    Report(new[] { Measure($"Scan {root}", root, runs) });
                    return 0;
                }

                default:
                    Console.WriteLine("Usage:\n  DiskAnalyzer.Benchmarks suite --data <dir> [--sizes 100000,500000,1000000] [--keep]\n  DiskAnalyzer.Benchmarks scan --root <dir> [--runs 3]");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static void RunSuite(string dataDir, int[] sizes, bool keep)
    {
        Directory.CreateDirectory(dataDir);
        var results = new List<BenchResult>();
        try
        {
            foreach (int files in sizes)
            {
                string dir = Path.Combine(dataDir, $"balanced-{files}");
                Generate($"balanced tree, {files:N0} files", () => Generator.Balanced(dir, files));
                results.Add(Measure($"{files:N0} files, balanced tree", dir, files <= 100_000 ? 5 : 3));
            }

            string deep = Path.Combine(dataDir, "deep");
            Generate("deep structure", () => Generator.Deep(deep, depth: 250, filesPerLevel: 20));
            results.Add(Measure("Deep: 250 nested levels × 20 files", deep, 5));

            string wide = Path.Combine(dataDir, "wide");
            Generate("one large folder", () => Generator.Wide(wide, 200_000));
            results.Add(Measure("200,000 small files in one folder", wide, 3));

            string large = Path.Combine(dataDir, "large");
            Generate("large sparse files", () => Generator.LargeSparse(large, count: 50, sizeBytes: 4L << 30));
            results.Add(Measure("50 large files (4 GB each, sparse)", large, 5));
        }
        finally
        {
            if (!keep)
            {
                Console.WriteLine($"Deleting generated data in {dataDir} …");
                Generator.DeleteTree(dataDir);
            }
        }

        Report(results);
    }

    private static void Generate(string label, Action action)
    {
        var sw = Stopwatch.StartNew();
        Console.Write($"Generating {label} … ");
        action();
        Console.WriteLine($"{sw.Elapsed.TotalSeconds:0.0} s");
    }

    private sealed record BenchResult(
        string Name, int Runs, long Files, long Folders, long Bytes,
        double MedianSeconds, double BestSeconds, double ItemsPerSecond,
        double CpuUtilizationPercent, long PeakWorkingSetMb, long ManagedAfterGcMb, long TreeMemoryMb,
        double MaxSchedulingDelayMs, double AggregationMs, double SearchMs, int TreemapTiles, double LayoutMs, double RenderMs);

    private static BenchResult Measure(string name, string root, int runs)
    {
        Console.WriteLine($"Benchmark: {name}");
        var scanner = new StandardFileSystemScanner(NullLogger<StandardFileSystemScanner>.Instance);
        var aggregation = new FolderAggregationService();
        var durations = new List<double>();
        double cpu = 0, latency = 0;
        ScanTree? tree = null;
        ScanProgressTracker? progress = null;
        Process self = Process.GetCurrentProcess();

        for (int run = 0; run < runs + 1; run++) // first run warms the OS cache and JIT and is discarded
        {
            tree = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            self.Refresh();
            TimeSpan cpuBefore = self.TotalProcessorTime;
            progress = new ScanProgressTracker();
            using var probe = new LatencyProbe();
            var sw = Stopwatch.StartNew();
            tree = scanner.ScanAsync(new ScanConfiguration { RootPath = root }, progress, new AsyncPauseGate(), CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            self.Refresh();
            if (run > 0)
            {
                durations.Add(sw.Elapsed.TotalSeconds);
                cpu = Math.Max(cpu, (self.TotalProcessorTime - cpuBefore).TotalSeconds / sw.Elapsed.TotalSeconds / Environment.ProcessorCount * 100);
                latency = Math.Max(latency, probe.MaxDelayMs);
            }

            Console.WriteLine($"  run {run}{(run == 0 ? " (warm-up)" : string.Empty)}: {sw.Elapsed.TotalSeconds:0.000} s, {progress.FilesScanned:N0} files");
        }

        var aggSw = Stopwatch.StartNew();
        aggregation.Aggregate(tree!);
        double aggMs = aggSw.Elapsed.TotalMilliseconds;

        var searchSw = Stopwatch.StartNew();
        SearchResult search = new StorageAnalysisService().SearchAsync(tree!, new SearchQuery { Text = "7" }).GetAwaiter().GetResult();
        double searchMs = searchSw.Elapsed.TotalMilliseconds;

        const int width = 1920, height = 1080;
        var layoutSw = Stopwatch.StartNew();
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree!, ScanTree.RootIndex, new TreemapRect(0, 0, width, height), new TreemapLayoutOptions());
        double layoutMs = layoutSw.Elapsed.TotalMilliseconds;
        int[] pixels = new int[width * height];
        var renderSw = Stopwatch.StartNew();
        new CushionTreemapRenderer().Render(layout, pixels, width, height, n => n.IsLeaf ? TreemapColorScheme.GetExtensionColor(tree!.GetExtension(n.TreeIndex)) : 0x303030u);
        double renderMs = renderSw.Elapsed.TotalMilliseconds;

        self.Refresh();
        long peakWs = self.PeakWorkingSet64 / (1024 * 1024);
        GC.Collect();
        long managed = GC.GetTotalMemory(true) / (1024 * 1024);
        double median = durations.Order().ElementAt(durations.Count / 2);
        long items = tree!.Count;

        Console.WriteLine($"  search: {search.TotalMatches:N0} matches in {searchMs:0.0} ms; treemap: {layout.Nodes.Count:N0} tiles");
        return new BenchResult(name, runs, tree.GetFileCount(ScanTree.RootIndex), tree.GetDirectoryCount(ScanTree.RootIndex), tree.GetSize(ScanTree.RootIndex),
            median, durations.Min(), items / median, cpu, peakWs, managed, tree.EstimateMemoryBytes() / (1024 * 1024),
            latency, aggMs, searchMs, layout.Nodes.Count, layoutMs, renderMs);
    }

    private static void Report(IEnumerable<BenchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Environment.MachineName.Length} chars hidden · {Environment.ProcessorCount} logical CPUs · {Environment.OSVersion} · .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Files | Folders | Median scan | Best | Items/s | CPU | Peak WS | Managed | Tree | Max sched. delay | Aggregate | Search | Treemap tiles | Layout | Render 1080p |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (BenchResult r in results)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Files:N0} | {r.Folders:N0} | {r.MedianSeconds:0.00} s | {r.BestSeconds:0.00} s | {r.ItemsPerSecond:N0} | {r.CpuUtilizationPercent:0}% | {r.PeakWorkingSetMb} MB | {r.ManagedAfterGcMb} MB | {r.TreeMemoryMb} MB | {r.MaxSchedulingDelayMs:0} ms | {r.AggregationMs:0.0} ms | {r.SearchMs:0.0} ms | {r.TreemapTiles:N0} | {r.LayoutMs:0} ms | {r.RenderMs:0} ms |");
        }

        string text = sb.ToString().Replace($"{Environment.MachineName.Length} chars hidden · ", string.Empty, StringComparison.Ordinal);
        Console.WriteLine();
        Console.WriteLine(text);
        string outFile = Path.Combine(AppContext.BaseDirectory, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(outFile, text);
        Console.WriteLine($"Saved {outFile}");
    }

    /// <summary>
    /// Measures how late a 10 ms periodic timer fires on a dedicated thread while scanning: a proxy for whether the
    /// scanner starves the machine (the UI thread is never used by the scanner itself).
    /// </summary>
    private sealed class LatencyProbe : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;

        public LatencyProbe()
        {
            _thread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (!_stop)
                {
                    double before = sw.Elapsed.TotalMilliseconds;
                    Thread.Sleep(10);
                    double late = sw.Elapsed.TotalMilliseconds - before - 10;
                    if (late > MaxDelayMs)
                    {
                        MaxDelayMs = late;
                    }
                }
            }) { IsBackground = true, Priority = ThreadPriority.Normal };
            _thread.Start();
        }

        public double MaxDelayMs { get; private set; }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }
}
