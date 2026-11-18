# Disk Analyzer — Architecture

This document records the architecture, the main technical decisions and the performance
considerations behind Disk Analyzer. It is the reference for anyone changing the scanner,
the data model or the treemap.

## 1. Goals and non-goals

| Goal | How it is met |
|---|---|
| Accurate sizes | Bytes (`long`) everywhere; units only in the UI. Hard links, reparse points and inaccessible folders are tracked explicitly and never guessed. |
| Fast scans | Bounded multithreaded enumeration with `System.IO.Enumeration`, a single-writer tree, and an optional raw NTFS MFT reader. |
| Low memory | One compact struct-of-arrays tree; full paths are never stored, they are rebuilt from parent links on demand. |
| Responsive UI | Scans run off the UI thread; progress is polled at ~8 Hz instead of pushed per item; lists are virtualized; the treemap is drawn into a single bitmap. |
| Strictly read-only | No code path writes to, renames or deletes scanned files. Directory and file handles are opened with read-attributes access only. |

Non-goal: the app never needs elevation to start. The MFT provider is used only when the process
already runs elevated (the user can choose *Restart as administrator*).

## 2. Solution layout

```
DiskAnalyzer.sln
src/
  DiskAnalyzer.Core            net10.0          Models, interfaces, aggregation, analysis, search, formatting
  DiskAnalyzer.Scanner         net10.0-windows  Drive discovery, standard + MFT providers, orchestrator
  DiskAnalyzer.Visualization   net10.0-windows  Squarified layout, cushion renderer, TreemapControl (WPF)
  DiskAnalyzer.Infrastructure  net10.0-windows  SQLite history/cache, CSV/XLSX/JSON export, Serilog, settings
  DiskAnalyzer.App             net10.0-windows  WPF shell, MVVM view models, views, themes
tests/
  DiskAnalyzer.Core.Tests / Scanner.Tests / Visualization.Tests   (xUnit)
tools/
  DiskAnalyzer.Benchmarks       synthetic-tree generator + scan/layout/render benchmark
installer/
  DiskAnalyzer.Installer        WiX v6 MSI
```

Dependency direction is strictly inward: `App → {Scanner, Visualization, Infrastructure} → Core`.
`Core` has no Windows or WPF dependency, so the data model and algorithms are unit-testable
in isolation, and the scanner has no UI dependency at all.

## 3. Data model: `ScanTree`

A drive scan can produce millions of entries, so the tree is **not** a graph of objects.
`ScanTree` stores nodes as parallel *chunked arrays* (64 K elements per chunk, so growing never
copies a large-object-heap array):

| Column | Type | Notes |
|---|---|---|
| Name | `string` | file or folder name only |
| Parent, FirstChild, NextSibling | `int` | index links; -1 = none |
| Size | `long` | logical bytes; aggregated for folders |
| Allocated | `long` | size on disk (exact from MFT, estimated from cluster size otherwise) |
| LastWriteUtc | `long` | ticks |
| Attributes | `int` | `FileAttributes` |
| Flags | `byte` | Directory, ReparsePoint, Inaccessible, HardLinkDuplicate, … |
| ExtensionId | `int` | index into an interned extension table |
| FileCount, DirCount | `int` | descendant counts for folders |

About 60 bytes per node plus the name string. A node is identified by its `int` index; view
models wrap indices lazily, only for rows that are actually on screen.

**Invariant:** a parent's index is always lower than its children's. Both providers guarantee
it (the standard scanner appends a folder before its contents; the MFT provider emits nodes in
breadth-first order). Folder aggregation is therefore one reverse linear pass —
`for i = n-1 … 1: size[parent[i]] += size[i]` — O(n), allocation-free and cache-friendly.

Full paths are rebuilt by walking parent links, so no path string is kept per node.

## 4. Scanning engine

### 4.1 Standard provider (`StandardFileSystemScanner`)

```
            ┌──────────── unbounded work queue (dir index + path) ◄──────────┐
            ▼                                                                 │
   N enumeration workers  ──► bounded Channel<DirectoryBatch> ──► single aggregator
   (FileSystemEnumerator)        (back-pressure, cap 256)          writes ScanTree, enqueues subdirs
```

* **Bounded concurrency:** a fixed pool of `N` workers (default: logical processors, capped at 16;
  lower for network/removable drives). No task per directory, no recursion.
* **Allocation discipline:** a custom `FileSystemEnumerator<EntryData>` turns each
  `FileSystemEntry` straight into a small struct — no `FileInfo`/`DirectoryInfo`. Only the name
  string is allocated. Large folders are streamed in batches of 4 096 entries.
* **Single writer:** only the aggregator touches `ScanTree`, so the tree needs no locks.
  The work queue is unbounded (it holds only pending folder paths) and the batch channel is
  bounded; because the aggregator never blocks on the unbounded queue, the cycle cannot deadlock.
* **Completion:** an outstanding-folder counter; the aggregator closes the work queue when it
  reaches zero.
* **Pause/resume:** an `AsyncPauseGate` checked by workers between folders and every batch.
* **Cancellation:** `CancellationToken` flows to every await; a cancelled scan yields a tree
  marked *partial* that the UI labels as such.

### 4.2 MFT provider (`MftScanner`) — optional

Used for whole-volume scans of local NTFS volumes **when the process is already elevated**.
It reads `$MFT` directly through the volume handle:

1. Read the NTFS boot sector → bytes per cluster, MFT record size, `$MFT` location.
2. Read record 0 and decode the `$DATA` run list (the MFT itself can be fragmented).
3. Stream the MFT in 4 MB chunks; apply update-sequence fixups; parse `$STANDARD_INFORMATION`,
   `$FILE_NAME`, `$DATA` and `$REPARSE_POINT`. Extension records are folded into their base
   record, so attribute lists are handled.
4. Build the tree breadth-first from the root (record 5).

Correctness rules:
* **Hard links** — one MFT record with several `$FILE_NAME` attributes. Size is counted once
  (first link); other links appear with the *HardLinkDuplicate* flag and contribute 0 bytes.
* **DOS 8.3 names** are skipped (they are aliases, not links).
* **Reparse points / junctions / symlinks** — the MFT is flat, so targets are never reached
  through the link. Nothing can be counted twice and no loop is possible.
* **Sizes** — logical size comes from the unnamed `$DATA` stream (first extent). Size on disk is the
  sum of the *non-sparse runs* of every `$DATA` extent, including named streams (ADS, `$UsnJrnl:$J`), times the
  cluster size. Attribute header flags are not trusted for this: `$BadClus:$Bad` spans the whole volume
  but is sparse without being flagged as such, and counting its nominal allocation once doubled the
  drive total. Verified on a real system drive: 237.6 GB on disk vs. 239.2 GB used reported by Windows.

If anything fails (not NTFS, not elevated, unreadable volume, unexpected structure), the
orchestrator logs why and falls back to the standard provider. The app never asks for elevation
on launch.

### 4.3 Filesystem safety

| Case | Handling |
|---|---|
| Access denied / locked | `ContinueOnError` records the error; the folder is flagged *Inaccessible* and the scan continues. |
| Junctions, symlinks, reparse points | Recorded as entries but **not followed** by default (setting exists, with a visited-set loop guard keyed by volume+file ID). |
| Long paths | .NET uses `\\?\` internally; the manifest declares `longPathAware`. |
| Files deleted/changed mid-scan | Enumeration just sees the state at read time; vanished folders are counted as errors. |
| Disconnected / network drives | Drive readiness is queried with a timeout; network scans use lower concurrency; I/O errors end that branch, not the scan. |
| Hard links (standard provider) | Optional: opens each file for attributes only and dedupes by file ID. Off by default because it costs one handle per file. |

### 4.4 Sizes shown to the user

* **Size** — logical file length.
* **Size on disk** — allocated clusters. Exact from the MFT; estimated (cluster rounding, or
  `GetCompressedFileSizeW` for compressed/sparse/cloud files) in the standard provider.
* **Drive used space** — what Windows reports (`total - free`).

The UI explains why folder totals don't match drive usage: filesystem metadata, the MFT,
shadow copies, reserved space, inaccessible folders, hard links, compression and cloud
placeholders.

## 5. Analysis and search

`StorageAnalysisService` runs over the arrays without building objects: top-N largest files
and folders via a bounded min-heap, extension and category totals by `ExtensionId`, and search
as a parallel partitioned scan over the name column (debounced 250 ms in the UI, cancellable,
capped result count). Nothing is rescanned for search.

## 6. Treemap

* **Layout** (`SquarifiedTreemapLayout`, pure C#): Bruls/Huizing/van Wijk squarified algorithm.
  Folders get a frame and header strip and their children are laid out recursively. Recursion stops
  when a rectangle falls under the minimum pixel size; the remaining tiny children are merged into a
  single "smaller items" tile so the area still adds up.
* **Rendering** (`CushionTreemapRenderer`): cushion shading (van Wijk) is computed per pixel
  into a `int[]` buffer on a background thread, rows in parallel, then copied into one
  `WriteableBitmap`. One bitmap — not one WPF element per rectangle — keeps 100 000+ tiles smooth.
* **Interaction** (`TreemapControl`): a `DrawingVisual` overlay draws hover outline, selection,
  labels, search hits and top-N highlights. Hit testing walks the layout tree (depth × siblings).
* **Colors:** by file category, with a stable per-extension hue shift; folders use neutral frames.

## 7. Persistence (SQLite)

`%LOCALAPPDATA%\DiskAnalyzer\history.db`:

* `scans` — id, drive id (volume serial + letter), drive name, root path, timestamps, totals,
  duration, status, provider, stored-file threshold.
* `nodes` — per scan: node id, parent id, name, is-dir, size, allocated, modified, counts.
  All folders are stored; files are stored when ≥ the configured threshold (default 1 MB), so
  history stays a sensible size. Folder totals are always complete.

Results are always labelled with their **source**: *Live*, *Cached* (latest stored scan of this
volume, shown with its age) or *Historical*. A cached scan is never used for drive utilization,
and it is refused if the volume serial no longer matches. Comparison joins two scans by path to
list added and removed files and folders that grew or shrank.

## 8. Export

Streaming writers so a million rows never sit in memory: CSV (RFC 4180), JSON
(`Utf8JsonWriter`) and XLSX (hand-written OpenXML parts streamed into a zip; Excel's
1 048 576-row limit is enforced and reported). "Top items" export writes only the largest files
and folders.

## 9. UI

WPF with the built-in Fluent theme (`Application.ThemeMode`), light/dark/system. MVVM with
CommunityToolkit.Mvvm; services come from `Microsoft.Extensions.DependencyInjection`.
Pages: Overview, Drives, Folder Explorer, Storage Heatmap, Largest Files, File Types,
Scan History, Settings. All large lists use `VirtualizingStackPanel` with recycling.

## 10. Logging and errors

Serilog via `Microsoft.Extensions.Logging` into rolling files under
`%LOCALAPPDATA%\DiskAnalyzer\logs`. Scanner errors are counted and sampled (first 200 paths kept)
so a drive full of access-denied folders cannot flood the log. Unhandled UI exceptions are logged
and shown once; they do not kill a running scan.

## 11. Performance targets

Targets are measured with `tools/DiskAnalyzer.Benchmarks` on the build machine and recorded
in `docs/BENCHMARKS.md`; they are not promises for other hardware.
