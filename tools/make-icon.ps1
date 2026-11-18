# Generates src/DiskAnalyzer.App/Resources/AppIcon.ico (PNG-compressed ICO, 16–256 px).
# The design is a small treemap: four rounded tiles in the app's category colors.
param([string]$OutFile = (Join-Path $PSScriptRoot "..\src\DiskAnalyzer.App\Resources\AppIcon.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 16.0
    $gap = [Math]::Max(1.0, 0.8 * $s)
    $r = [Math]::Max(0.8, 1.4 * $s)
    $tiles = @(
        @(0.5, 0.5, 9.0, 15.0, "#3B82F6"),
        @(9.5, 0.5, 6.0, 8.5, "#EF4444"),
        @(9.5, 9.0, 6.0, 3.5, "#22C55E"),
        @(9.5, 12.5, 6.0, 3.0, "#EAB308")
    )
    foreach ($t in $tiles) {
        $x = $t[0] * $s + $gap / 2; $y = $t[1] * $s + $gap / 2
        $w = $t[2] * $s - $gap; $h = $t[3] * $s - $gap
        $path = New-RoundedRect $x $y $w $h $r
        $color = [System.Drawing.ColorTranslator]::FromHtml($t[4])
        $light = [System.Drawing.Color]::FromArgb(255, [Math]::Min(255, $color.R + 45), [Math]::Min(255, $color.G + 45), [Math]::Min(255, $color.B + 45))
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.RectangleF]::new($x, $y, $w, $h)), $light, $color, 45.0
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
# Also emit a 256 px PNG for in-app use and the installer.
[System.IO.File]::WriteAllBytes((Join-Path (Split-Path $OutFile) "AppLogo.png"), $images[-1])
Write-Output "Wrote $OutFile ($($out.Length) bytes)"
