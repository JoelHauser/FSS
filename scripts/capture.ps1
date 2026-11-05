<#
.SYNOPSIS
    Records what the mod cannot see while you play: the PC's setup, GPU load and temperature, and
    optionally PresentMon's presented frames.

.DESCRIPTION
    Start this before the game. It stops when the game closes. Nothing is changed on the PC; it
    only reads information and writes into <SPTPath>\FirstShotSmoothness-reports\_capture-<label>.

    PresentMon is not included. It is Intel's free tool (github.com/GameTechDev/PresentMon) and
    usually needs an administrator prompt, or membership of "Performance Log Users".

.PARAMETER SPTPath
    The SPT install root.

.PARAMETER Label
    A name for this capture, matching the mod's Run Label.

.PARAMETER PresentMon
    Path to the PresentMon console exe. Leave out to skip it.

.PARAMETER Seconds
    Stop after this long instead of waiting for the game. For trying the script out.

.EXAMPLE
    scripts\capture.ps1 -SPTPath H:\SPT4.1.X -Label G1 -PresentMon C:\Tools\PresentMon.exe
#>
param(
    [Parameter(Mandatory = $true)][string] $SPTPath,
    [Parameter(Mandatory = $true)][string] $Label,
    [string] $PresentMon,
    [int] $Seconds = 0
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $SPTPath "FirstShotSmoothness-reports\_capture-$Label"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Section($name, [scriptblock] $read) {
    "[$name]"
    try { & $read | Out-String -Width 200 } catch { "unavailable: $($_.Exception.Message)" }
}

function Get-RegistryValue($path, $name) {
    try { (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name } catch { 'not set' }
}

$env = & {
    "captured=$(Get-Date -Format s)"
    Section 'system' { Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory | Format-List }
    Section 'os' { Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber | Format-List }
    Section 'cpu' { Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed | Format-List }
    Section 'gpu' { Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate | Format-List }
    Section 'game_drive' {
        $letter = (Resolve-Path $SPTPath).Path.Substring(0, 1)
        $disk = Get-Partition -DriveLetter $letter | Get-Disk
        Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$($disk.Number)" } | Select-Object FriendlyName, MediaType, BusType, Size | Format-List
        Get-Volume -DriveLetter $letter | Select-Object DriveLetter, FileSystem, SizeRemaining, Size | Format-List
    }
    Section 'power_plan' { powercfg /getactivescheme }
    Section 'windows_settings' {
        "game_mode=$(Get-RegistryValue 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled')"
        "game_dvr=$(Get-RegistryValue 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled')"
        "hardware_gpu_scheduling=$(Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode')"
    }
    Section 'overlay_and_capture_processes' {
        $known = 'RTSS|MSIAfterburner|Discord|steam|NVIDIA Overlay|nvsphelper|obs64|obs32|GameBar|Medal|Overwolf|Xbox|EpicGames|HWiNFO|iCUE|LGHUB|Razer|Wallpaper'
        Get-Process | Where-Object { $_.ProcessName -match $known } | Select-Object -ExpandProperty ProcessName -Unique | Sort-Object
    }
    Section 'injected_files_in_game_folder' {
        Get-ChildItem $SPTPath -File | Where-Object { $_.Name -match '^(dxgi|d3d11|d3d12|dinput8|version|opengl32)\.dll$|reshade' } | Select-Object -ExpandProperty Name
    }
    Section 'plugins' { Get-ChildItem (Join-Path $SPTPath 'BepInEx\plugins') | Select-Object -ExpandProperty Name }
}
$env | Set-Content -Path (Join-Path $out 'environment.txt') -Encoding utf8
Write-Host "PC details written to $out" -ForegroundColor Green

$started = @()
try {
    if ($PresentMon) {
        if (-not (Test-Path $PresentMon)) { throw "PresentMon not found at '$PresentMon'." }
        $started += Start-Process -FilePath $PresentMon -PassThru -WindowStyle Minimized -ArgumentList @(
            '--process_name', 'EscapeFromTarkov.exe',
            '--output_file', "`"$(Join-Path $out 'presentmon.csv')`"",
            '--qpc_time')
        Write-Host 'PresentMon started. If it asks for permission or closes, run this script as administrator.' -ForegroundColor Green
    }

    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($smi) {
        $started += Start-Process -FilePath $smi.Source -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $out 'gpu.csv') -ArgumentList @(
            '--query-gpu=timestamp,temperature.gpu,utilization.gpu,utilization.memory,memory.used,clocks.gr,power.draw,clocks_throttle_reasons.active',
            '--format=csv', '-l', '1')
        Write-Host 'GPU logging started.' -ForegroundColor Green
    }
    else {
        Write-Host 'nvidia-smi not found: no GPU log.' -ForegroundColor Yellow
    }

    if ($Seconds -gt 0) {
        Start-Sleep -Seconds $Seconds
    }
    else {
        Write-Host 'Waiting for the game to start. This window can stay open.'
        while (-not (Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
        Write-Host 'Game running. This stops by itself when the game closes.'
        while (Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
    }
}
finally {
    foreach ($process in $started) {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}
Write-Host 'Capture finished.' -ForegroundColor Green
