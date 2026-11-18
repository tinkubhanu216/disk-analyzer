<#
.SYNOPSIS
  Creates a realistic-looking demo folder for README screenshots, without using real disk space.
.DESCRIPTION
  Files are created as NTFS sparse files: their logical size (what Disk Analyzer shows as "Size")
  is realistic, but they occupy almost no space on disk. Contains no personal data.
.EXAMPLE
  ./tools/make-demo-data.ps1 -Root "$PWD\.demo-data\Demo Workspace"
#>
param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.demo-data\Demo Workspace"),
    # Multiply every file size by this factor (e.g. 0.06 for real, non-sparse files that fit on disk).
    [double]$Scale = 1.0,
    # Real allocation: files occupy their size on disk (use with a small -Scale).
    [switch]$NoSparse
)
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class Demo {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
    public static void File(string path, long size, DateTime modified, bool sparse) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) {
            int ret;
            if (sparse)
            DeviceIoControl(fs.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
            fs.SetLength(size);
        }
        System.IO.File.SetLastWriteTimeUtc(path, modified);
    }
}
"@

$rand = New-Object System.Random 42
$now = [DateTime]::UtcNow
function MB([double]$v) { [long]($v * 1MB) }
function GB([double]$v) { [long]($v * 1GB) }
function Rnd([double]$lo, [double]$hi) { $lo + ($hi - $lo) * $rand.NextDouble() }
function Days([int]$max) { $now.AddDays(-$rand.Next(0, $max)).AddMinutes(-$rand.Next(0, 1440)) }
function F([string]$rel, [long]$size, [int]$maxAgeDays = 900) { [Demo]::File((Join-Path $Root $rel), [long]($size * $Scale), (Days $maxAgeDays), (-not $NoSparse)) }

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$Root = [IO.Path]::GetFullPath($Root)
Write-Host "Generating demo data in $Root ..."

# Virtual machines and games: a few very large files
F "Virtual Machines\Ubuntu 24.04\ubuntu-dev.vhdx" (GB 38.4) 20
F "Virtual Machines\Windows Test\win11-test.vhdx" (GB 27.9) 60
F "Virtual Machines\Windows Test\snapshot-01.avhdx" (GB 6.2) 60
foreach ($g in @("Starfield Odyssey", "Racing Pro 2026", "Puzzle Quest")) {
    $n = $rand.Next(4, 9)
    for ($i = 0; $i -lt $n; $i++) { F "Games\SteamLibrary\$g\Content\data$i.pak" (GB (Rnd 0.8 5.5)) 200 }
    F "Games\SteamLibrary\$g\$($g -replace ' ','').exe" (MB (Rnd 40 180)) 200
    for ($i = 0; $i -lt 30; $i++) { F "Games\SteamLibrary\$g\Bin\module$i.dll" (MB (Rnd 0.2 12)) 200 }
}

# Videos
foreach ($trip in @("Vacation 2025", "Family Day", "Mountains")) {
    for ($i = 1; $i -le $rand.Next(5, 12); $i++) { F "Videos\$trip\clip_$('{0:D3}' -f $i).mp4" (GB (Rnd 0.4 3.2)) 500 }
}
for ($i = 1; $i -le 18; $i++) { F "Videos\Screen Recordings\recording_$i.mkv" (MB (Rnd 150 900)) 120 }

# Photos
foreach ($year in 2023..2026) {
    for ($m = 1; $m -le 12; $m += 2) {
        for ($i = 0; $i -lt $rand.Next(15, 45); $i++) { F "Photos\$year\$('{0:D2}' -f $m)\IMG_$($rand.Next(1000,9999)).jpg" (MB (Rnd 2 9)) (365 * (2027 - $year)) }
        for ($i = 0; $i -lt $rand.Next(3, 10); $i++) { F "Photos\$year\$('{0:D2}' -f $m)\RAW\DSC_$($rand.Next(1000,9999)).cr3" (MB (Rnd 22 38)) (365 * (2027 - $year)) }
    }
}

# Music
foreach ($album in @("Ambient Nights", "Jazz Classics", "Road Trip Mix", "Piano Sessions", "Synthwave", "Live Concert")) {
    for ($t = 1; $t -le $rand.Next(8, 16); $t++) { F "Music\$album\$('{0:D2}' -f $t) Track.flac" (MB (Rnd 18 55)) 1200 }
}

# Development projects
$exts = ".js", ".ts", ".json", ".map", ".md", ".d.ts"
foreach ($proj in @("web-dashboard", "mobile-app", "api-service")) {
    for ($p = 0; $p -lt 60; $p++) {
        for ($f = 0; $f -lt $rand.Next(5, 20); $f++) { F "Projects\$proj\node_modules\package-$p\lib\file$f$($exts[$rand.Next($exts.Length)])" ([long](Rnd 2KB 900KB)) 90 }
    }
    for ($f = 0; $f -lt 40; $f++) { F "Projects\$proj\src\component$f.tsx" ([long](Rnd 1KB 30KB)) 14 }
    F "Projects\$proj\.git\objects\pack\pack-$($rand.Next(100000,999999)).pack" (MB (Rnd 80 600)) 14
}
for ($f = 0; $f -lt 25; $f++) { F "Projects\game-engine\build\Release\obj\unit$f.obj" (MB (Rnd 2 40)) 7 }
F "Projects\game-engine\build\Release\engine.pdb" (MB 640) 7
F "Projects\game-engine\build\Release\engine.dll" (MB 96) 7

# Downloads, backups, documents
F "Downloads\ubuntu-24.04-desktop-amd64.iso" (GB 5.7) 150
F "Downloads\windows-sdk.iso" (GB 1.9) 300
F "Downloads\VisualStudioSetup.exe" (MB 4.2) 30
F "Downloads\tools-bundle.zip" (MB 820) 45
F "Downloads\drivers\gpu-driver-installer.exe" (MB 680) 20
F "Backups\photos-2024.7z" (GB 11.3) 400
F "Backups\projects-archive.zip" (GB 3.4) 220
F "Backups\phone-backup.tar" (GB 7.8) 600
foreach ($d in "Invoices", "Reports", "Manuals") {
    for ($i = 0; $i -lt 25; $i++) { F "Documents\$d\document_$i$((".pdf", ".docx", ".xlsx", ".pptx")[$rand.Next(4)])" ([long](Rnd 80KB 12MB)) 700 }
}

$files = (Get-ChildItem $Root -Recurse -File).Count
Write-Host "Done: $files files."
