<#
.SYNOPSIS
    Zips everything to send back after testing: reports, captures, logs, and the settings of the
    mods involved. No profile or save data.

.EXAMPLE
    scripts\pack-results.ps1 -SPTPath H:\SPT4.1.X
#>
param([Parameter(Mandatory = $true)][string] $SPTPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$files = @{}
$reports = Join-Path $SPTPath 'FirstShotSmoothness-reports'
if (Test-Path $reports) {
    $base = (Resolve-Path $reports).Path
    Get-ChildItem $reports -Recurse -File | ForEach-Object {
        $files["reports/" + $_.FullName.Substring($base.Length + 1).Replace('\', '/')] = $_.FullName
    }
}
if ($files.Count -eq 0) { Write-Host 'No reports found: did a raid finish with shots fired?' -ForegroundColor Yellow }

$single = @{
    'LogOutput.log'                = Join-Path $SPTPath 'BepInEx\LogOutput.log'
    'Player.log'                   = Join-Path $env:USERPROFILE 'AppData\LocalLow\Battlestate Games\EscapeFromTarkov\Player.log'
    'firstshotsmoothness.cfg'      = Join-Path $SPTPath 'BepInEx\config\com.mybutthasarash.firstshotsmoothness.cfg'
    'hollywoodfx.cfg'              = Join-Path $SPTPath 'BepInEx\config\com.janky.hollywoodfx.cfg'
}
foreach ($name in $single.Keys) {
    if (Test-Path $single[$name]) { $files[$name] = $single[$name] } else { Write-Host "Not found, skipped: $($single[$name])" }
}

$zip = Join-Path $SPTPath "FirstShotSmoothness-results-$(Get-Date -Format yyyyMMdd-HHmmss).zip"
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $files.GetEnumerator()) {
        # Shared read, because the game or BepInEx may still have a log open.
        $stream = [System.IO.File]::Open($entry.Value, 'Open', 'Read', 'ReadWrite')
        try {
            $target = $archive.CreateEntry($entry.Key, [System.IO.Compression.CompressionLevel]::Optimal).Open()
            try { $stream.CopyTo($target) } finally { $target.Dispose() }
        }
        finally { $stream.Dispose() }
    }
}
finally { $archive.Dispose() }
Write-Host "Packed $($files.Count) files: $zip" -ForegroundColor Green
