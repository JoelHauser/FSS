<#
.SYNOPSIS
    Builds First Shot Smoothness, checks its version, and packs the release zip.

.DESCRIPTION
    Run this through PowerShell, not Bash: Bash mangles 'H:\SPT4.1.X' into 'H:SPT4.1.X'.

    The zip is unpacked over the SPT root, so it carries the full path BepInEx/plugins/... and
    only that: no README. Entries are written by hand with forward slashes, because PowerShell
    5.1's Compress-Archive writes backslashes.

.PARAMETER SPTPath
    The SPT install root to compile against (and install into, with -Install). It must have been
    started through the SPT Launcher once, or be a mirror built as CLAUDE.md describes.

.EXAMPLE
    scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install
#>
param(
    [Parameter(Mandatory = $true)][string] $SPTPath,
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\FirstShotSmoothness\FirstShotSmoothness.csproj'
$source = Join-Path $root 'src\FirstShotSmoothness\FirstShotSmoothnessPlugin.cs'

$csprojVersion = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ }
$sourceVersion = (Select-String -Path $source -Pattern 'PluginVersion\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if ($csprojVersion -ne $sourceVersion) {
    throw "Version mismatch: csproj says '$csprojVersion', plugin says '$sourceVersion'."
}
$version = $csprojVersion
Write-Host "Version $version" -ForegroundColor Cyan

dotnet build $project -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$built = Join-Path $root 'src\FirstShotSmoothness\bin\Release\FirstShotSmoothness.dll'

$entry = 'BepInEx/plugins/FirstShotSmoothness.dll'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "FirstShotSmoothness-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $built, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip" -ForegroundColor Green

if ($Install) {
    $dest = Join-Path $SPTPath 'BepInEx\plugins'
    Copy-Item $built $dest -Force
    Write-Host "Installed to $dest" -ForegroundColor Green
}
