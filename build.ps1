<#
.SYNOPSIS
  Builds, tests, publishes and packages Disk Analyzer.
.EXAMPLE
  ./build.ps1                 # test + publish + MSI
  ./build.ps1 -SkipTests
  ./build.ps1 -Runtime win-arm64
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$SkipInstaller
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($cmd) { $cmd.Source } else { "C:\Program Files\dotnet\dotnet.exe" }

function Step($name, [scriptblock]$block) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

Step "Build" { & $dotnet build "$root\DiskAnalyzer.sln" -c $Configuration -nologo -v minimal }

if (-not $SkipTests) {
    Step "Test" { & $dotnet test "$root\DiskAnalyzer.sln" -c $Configuration --no-build -nologo -v minimal }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Step "Publish ($Runtime, self-contained)" {
    & $dotnet publish "$root\src\DiskAnalyzer.App\DiskAnalyzer.App.csproj" -c $Configuration -r $Runtime --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none -o $publishDir -nologo -v minimal
}
Write-Host "Executable: $publishDir\DiskAnalyzer.exe"

if (-not $SkipInstaller -and $Runtime -eq "win-x64") {
    Step "Installer (WiX MSI)" {
        & $dotnet build "$root\installer\DiskAnalyzer.Installer\DiskAnalyzer.Installer.wixproj" -c $Configuration -p:PublishDir=$publishDir -nologo -v minimal
    }
    $msi = Get-ChildItem "$root\installer\DiskAnalyzer.Installer\bin" -Recurse -Filter *.msi | Sort-Object LastWriteTime | Select-Object -Last 1
    New-Item -ItemType Directory -Force "$root\artifacts" | Out-Null
    Copy-Item $msi.FullName "$root\artifacts\" -Force
    Write-Host "Installer: $root\artifacts\$($msi.Name)"
}
