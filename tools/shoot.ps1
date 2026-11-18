# Dev helper: launch the app with arguments, wait, capture the window, close the app.
param([string]$AppArgs = "", [int]$Wait = 8, [string]$Out = "$env:TEMP\da-shot.png", [switch]$KeepOpen, [string]$Exe = (Join-Path $PSScriptRoot "..\src\DiskAnalyzer.App\bin\Debug\net10.0-windows\DiskAnalyzer.exe"))
Get-Process DiskAnalyzer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if ($AppArgs) { $p = Start-Process $exe -ArgumentList $AppArgs -PassThru } else { $p = Start-Process $exe -PassThru }
Start-Sleep -Seconds $Wait
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessName DiskAnalyzer -OutFile $Out
if (-not $KeepOpen) { $p | Stop-Process -Force }
