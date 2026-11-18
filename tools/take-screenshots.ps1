<#
.SYNOPSIS
  Regenerates the README screenshots in docs/images from the demo data (tools/make-demo-data.ps1).
.EXAMPLE
  ./tools/make-demo-data.ps1; ./tools/take-screenshots.ps1
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\artifacts\publish\win-x64\DiskAnalyzer.exe"),
    [string]$DemoRoot = (Join-Path $PSScriptRoot "..\.demo-data\Demo Workspace"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\docs\images"),
    [int]$Width = 1600
)
$ErrorActionPreference = "Stop"
$DemoRoot = [IO.Path]::GetFullPath($DemoRoot)
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing

$pages = [ordered]@{ overview = "overview"; heatmap = "heatmap"; explorer = "explorer"; "file-types" = "types"; "largest-files" = "largest" }
foreach ($name in $pages.Keys) {
    $raw = Join-Path $env:TEMP "da-$name-raw.png"
    & (Join-Path $PSScriptRoot "shoot.ps1") -AppArgs "--scan `"$DemoRoot`" --page $($pages[$name])" -Wait 9 -Out $raw -Exe $Exe | Out-Null
    $src = [System.Drawing.Image]::FromFile($raw)
    $h = [int]($src.Height * $Width / $src.Width)
    $dst = New-Object System.Drawing.Bitmap $Width, $h
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $Width, $h)
    $g.Dispose(); $src.Dispose()
    $out = Join-Path $OutDir "$name.png"
    $dst.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $dst.Dispose()
    Write-Host "Saved $out"
}
