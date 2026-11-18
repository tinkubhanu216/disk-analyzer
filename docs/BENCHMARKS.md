# Benchmarks

Measured with `tools/DiskAnalyzer.Benchmarks` (Release build), 18 Nov 2026.

**Machine:** 12 logical CPUs, NVMe SSD (NTFS), Windows 11 (10.0.26200), .NET 10.0.15.
**Scanner:** standard enumeration provider (not elevated, so no MFT reader), automatic worker count (12).

Each scenario runs one discarded warm-up scan, then 3–5 measured scans, so the figures are **warm-cache** numbers:
the file-system metadata is already in the Windows cache. A cold first scan is dominated by storage latency; see the
real-world row below. Each generated tree contains one extra marker file.

## Synthetic scenarios

| Scenario | Files | Folders | Median scan | Items/s | CPU | Tree memory | Managed heap | Max sched. delay | Aggregate | Search | Treemap tiles | Layout | Render 1080p |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 files, balanced | 100,001 | 5,000 | 0.22 s | 471,760 | 60% | 11 MB | 70 MB | 12 ms | 3.6 ms | 22 ms | 42,463 | 44 ms | 38 ms |
| 500,000 files, balanced | 500,001 | 25,000 | 1.07 s | 490,656 | 56% | 55 MB | 106 MB | 21 ms | 5.9 ms | 93 ms | 27,489 | 53 ms | 31 ms |
| 1,000,000 files, balanced | 1,000,001 | 50,000 | 2.26 s | 464,706 | 55% | 107 MB | 152 MB | 18 ms | 11.3 ms | 202 ms | 34,000 | 47 ms | 11 ms |
| Deep: 250 levels × 20 files | 5,001 | 250 | 0.03 s | 197,350 | 15% | 3 MB | 36 MB | 14 ms | 0.1 ms | 0.2 ms | 1,325 | <1 ms | 8 ms |
| 200,000 files in one folder | 200,001 | 1 | 0.09 s | 2,252,038 | 13% | 25 MB | 74 MB | 9 ms | 1.5 ms | 11 ms | 111,543 | 35 ms | 18 ms |
| 50 × 4 GB sparse files | 51 | 0 | <0.01 s | — | — | 3 MB | 33 MB | 0 ms | 0.5 ms | 0.2 ms | 51 | <1 ms | 19 ms |

Columns:
- **Items/s**: files + folders per second.
- **CPU**: process CPU time ÷ wall time ÷ logical CPUs.
- **Tree memory**: `ScanTree.EstimateMemoryBytes()`, about 107 bytes per entry including names.
- **Managed heap**: after a full GC, with the tree still alive.
- **Max sched. delay**: worst lateness of a 10 ms timer on a separate thread during the scan. This is a proxy for UI responsiveness; the UI thread itself is never used by the scanner and polls progress at 8 Hz.
- **Search**: substring search over every name, run in parallel.
- **Layout/Render**: full 1920×1080 treemap of the scan root.

## Real-world scans (from the application log)

| Location | Files | Folders | Duration | Notes |
|---|---:|---:|---:|---|
| `C:\Users\<user>` (first scan, cold cache) | 1,444,597 | 245,113 | 37.8 s | ~45k items/s, 131 GB, 2 inaccessible folders; history saved in 1.4 s |
| `C:\Users\<user>\Documents` | 116,877 | 15,862 | 0.5–0.7 s | Heatmap: 1,982 tiles, layout 11 ms, render 203 ms (UI build with labels) |
| `C:\` whole volume, **MFT reader** (elevated) | 2,042,776 | 543,228 | **3.2 s** | 2,589,440 MFT records. Size on disk 237.6 GB vs. Windows used 239.2 GB (0.7% gap = unattributed NTFS metadata such as directory indexes); logical 241.2 GB; hard links counted once |

## Targets (this hardware class: 8+ cores, NVMe SSD)

| Metric | Target | Status |
|---|---|---|
| Warm scan, 1M files | < 5 s | 2.3 s ✔ |
| Throughput (warm, SSD) | > 250k items/s | ~470k ✔ |
| Memory per scanned entry | < 150 bytes | ~107 bytes ✔ |
| Folder aggregation, 1M entries | < 50 ms | 11 ms ✔ |
| Full-name search, 1M entries | < 300 ms | 202 ms ✔ |
| Treemap layout + render, 1080p | < 250 ms | < 100 ms ✔ |
| UI-thread blocking during scan | none | scanner runs entirely off the UI thread ✔ |

Cold-cache speed depends on the storage device and on how many folders are protected. HDDs, USB sticks and network
shares are much slower, so the scanner automatically uses fewer workers on them (4, 2 and 4–8). The NTFS MFT
provider, available when running as administrator, avoids per-folder enumeration entirely: the whole 2.6M-entry
system drive above took 3.2 s.

## Reproduce

```powershell
dotnet run -c Release --project tools/DiskAnalyzer.Benchmarks -- suite --data E:\BenchData
dotnet run -c Release --project tools/DiskAnalyzer.Benchmarks -- scan --root C:\Users --runs 3
```

Generating the 1M-file tree takes several minutes and needs ~1–2 GB of MFT space on the target volume. The data is deleted afterwards (`--keep` retains it).
