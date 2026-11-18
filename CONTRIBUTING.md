# Contributing to Disk Analyzer

Thanks for your interest! Bug reports, ideas and pull requests are all welcome.

## Before you start

- For anything larger than a small fix, please open an issue first so we can agree on the approach.
- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). It explains the data model (`ScanTree`), the scanning pipeline and the rules for accuracy.

## Build and test

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build DiskAnalyzer.sln
dotnet test DiskAnalyzer.sln
dotnet run --project src/DiskAnalyzer.App
```

`./build.ps1` runs the full release pipeline (tests, self-contained publish and the MSI installer).

## Ground rules

- **Read-only by design.** Scanning, searching, comparing and exporting must never modify, move or delete files on the user's drives. Pull requests that add destructive operations will not be accepted.
- **Accuracy first.** Sizes are `long` bytes everywhere; formatting happens only in the UI. Don't trade correctness for speed.
- **Performance.** The scanner must not block the UI thread or allocate per-file objects. If you change the scanner, layout or renderer, run the benchmark before and after and include the numbers in the PR:
  `dotnet run -c Release --project tools/DiskAnalyzer.Benchmarks -- suite --data <dir on a spare volume>`
- **Tests.** Add or update tests for behaviour changes. Filesystem tests should use real temporary directories (see `tests/DiskAnalyzer.Scanner.Tests/TempTree.cs`).
- **Style.** Follow the existing code style. The build uses the latest recommended .NET analyzers; keep it at zero warnings.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each PR focused on one change, and describe what changed and why.
3. Make sure `dotnet test` passes. CI runs the build and tests on Windows for every PR.

By submitting a contribution you agree that it is licensed under the project's [MIT License](LICENSE).
