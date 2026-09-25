# Ohman GPU check. For "the NVIDIA GPU never goes to sleep". Measures how often it is awake with Ohman running
# and with Ohman closed. Run from Terminal (Admin), unplugged if you can, and leave the laptop alone while it runs:
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-gpu.ps1 | iex
# What it does to the machine: nothing. It watches for 2 minutes, asks you to quit Ohman, watches 2 more minutes.
# Writes ohman-gpu.txt to the Desktop.
$ErrorActionPreference = 'Continue'
# The enum, not the string: IsInRole('Administrator') looks up the built-in *user* account of that name (RID 500)
# and is false in every elevated window except that account's own, whatever the language of Windows.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-gpu.txt'
Set-Content $out "ohman gpu check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }

$gpu = Get-PnpDevice -PresentOnly -Class Display | Where-Object { $_.InstanceId -like 'PCI\VEN_10DE*' } | Select-Object -First 1
W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model)
if (-not $gpu) { W "No NVIDIA GPU found."; return }
W ("GPU:    " + $gpu.FriendlyName + "   driver " + (Get-PnpDeviceProperty -InstanceId $gpu.InstanceId -KeyName DEVPKEY_Device_DriverVersion).Data)
$power = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
W ("Power:  " + $(if ($power -and $power.BatteryStatus -eq 1) { "battery" } else { "plugged in" }))
$p = Get-Process Ohman -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p -and $p.Path) {
    $st = Join-Path (Split-Path $p.Path) 'ohman.state'
    if (Test-Path $st) { Get-Content $st | Where-Object { $_ -match '^(PollMs|RefreshHz|LowHzOnBattery|ModeIndex)=' } | ForEach-Object { W "Ohman:  $_" } }
}

function Awake { $d = (Get-PnpDeviceProperty -InstanceId $gpu.InstanceId -KeyName DEVPKEY_Device_PowerData).Data; [BitConverter]::ToInt32($d, 4) -eq 1 }
function Watch([string]$label) {
    Register-CimIndicationEvent -Query "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='nvidia-smi.exe'" -SourceIdentifier "ohmansmi" -ErrorAction SilentlyContinue
    $awake = 0; $drain = @()
    for ($i = 0; $i -lt 120; $i++) {
        if (Awake) { $awake++ }
        if ($i % 10 -eq 0) { try { $r = (Get-CimInstance -Namespace root\wmi BatteryStatus -ErrorAction Stop | Select-Object -First 1).DischargeRate; if ($r -gt 0) { $drain += $r } } catch { } }
        Write-Progress -Activity "Watching the GPU ($label)" -SecondsRemaining (120 - $i) -PercentComplete ($i / 1.2)
        Start-Sleep 1
    }
    Write-Progress -Activity "Watching the GPU" -Completed
    $smi = @(Get-Event -SourceIdentifier "ohmansmi" -ErrorAction SilentlyContinue).Count
    Get-Event -SourceIdentifier "ohmansmi" -ErrorAction SilentlyContinue | Remove-Event
    Unregister-Event -SourceIdentifier "ohmansmi" -ErrorAction SilentlyContinue
    W ""; W "===== $label ====="
    W ("  GPU awake " + [math]::Round($awake / 1.2) + "% of 2 minutes")
    W ("  nvidia-smi started " + $smi + " times")
    if ($drain.Count) { W ("  battery drain about " + [math]::Round((($drain | Measure-Object -Average).Average) / 1000, 1) + " W") }
    W "  programs holding GPU memory:"
    try {
        (Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction Stop).CounterSamples | Where-Object { $_.CookedValue -gt 1MB } | ForEach-Object {
            $procId = if ($_.InstanceName -match 'pid_(\d+)') { [int]$Matches[1] } else { 0 }
            $name = (Get-Process -Id $procId -ErrorAction SilentlyContinue).ProcessName
            $luid = if ($_.InstanceName -match 'luid_(0x[0-9a-f]+_0x[0-9a-f]+)') { $Matches[1] } else { '' }
            [pscustomobject]@{ n = $name; mb = [math]::Round($_.CookedValue / 1MB); luid = $luid }
        } | Sort-Object mb -Descending | Select-Object -First 10 | ForEach-Object { W ("    " + $_.n + "  " + $_.mb + " MB  (adapter " + $_.luid + ")") }
    } catch { W "    (not available)" }
}

Write-Host "Leave the laptop alone for the next 2 minutes." -ForegroundColor Cyan
Watch "with Ohman running"
[console]::beep(880, 300)
Write-Host ""
Write-Host "Now quit Ohman: right click its tray icon, Exit. This continues by itself once it has closed." -ForegroundColor Cyan
for ($i = 0; $i -lt 180 -and (Get-Process Ohman -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
if (Get-Process Ohman -ErrorAction SilentlyContinue) { W ""; W "Ohman was not closed, so there is nothing to compare." }
else { Watch "with Ohman closed" }

Write-Host ""
Write-Host "Done. You can open Ohman again. Drag ohman-gpu.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
