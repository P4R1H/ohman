# Ohman fan check. For boards where fan speed changes do nothing (the firmware refuses them and the driver route
# should take over). Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-fans.ps1 | iex
# What it does to the machine: closes Ohman, and if the optional driver is installed spins the fans up for about
# 15 seconds to see which register moves them, then hands them back and reopens Ohman. Writes ohman-fans.txt
# to the Desktop.
$ErrorActionPreference = 'Continue'
# The enum, not the string: IsInRole('Administrator') looks up the built-in *user* account of that name (RID 500)
# and is false in every elevated window except that account's own, whatever the language of Windows.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-fans.txt'
Set-Content $out "ohman fan check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Shared([string]$p) {
    try { $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite,Delete'); $r = New-Object IO.StreamReader($fs); $t = $r.ReadToEnd(); $r.Close(); $t -split "`r?`n" } catch { @() }
}
function FindOhman {
    $p = Get-Process Ohman -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.Path) { return $p.Path }
    foreach ($d in @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", "$env:LOCALAPPDATA\Ohman")) {
        $f = Get-ChildItem $d -Filter Ohman.exe -Recurse -Depth 2 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    $null
}

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$exe = FindOhman
if (-not $exe) { W "Ohman.exe not found. Open Ohman once, then run this again."; return }
$dir = Split-Path $exe
W ("Ohman:  " + (Get-Item $exe).VersionInfo.FileVersion)
$pair = Join-Path $dir 'ecpair.txt'; if (Test-Path $pair) { W ("ecpair: " + (Get-Content $pair -Raw).Trim()) }

W ""; W "===== Ohman's own decisions (last 80) ====="
Shared (Join-Path $dir 'ohman.log') | Where-Object { $_ -match '---- Ohman|driver|fan route|EC|FAIL|giving up|THERMAL GUARD|max fan' } | Select-Object -Last 80 | ForEach-Object { W "  $_" }

W ""; W "===== OMEN Gaming Hub ====="
W ("running: " + ((Get-Process | Where-Object { $_.Name -match 'OmenCommandCenter|HP\.Omen|OMEN' } | ForEach-Object { $_.Name } | Sort-Object -Unique) -join ', '))
$ogh = Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'AD2F1837.OMENCommandCenter*' -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\HPOMEN' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($ogh) {
    Get-ChildItem $ogh -Filter 'HPOMENBG_*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
        W "  -- $($_.Name)"
        Shared $_.FullName | Where-Object { $_ -match '(?i)fan|inputData|ThruDriver|SetFanMode|MaxFan' } | Select-Object -Last 120 | ForEach-Object { W "  $_" }
    }
} else { W "  (no OMEN Gaming Hub logs on this machine)" }

W ""; W "===== Closing Ohman ====="
if (Get-Process Ohman -ErrorAction SilentlyContinue) {
    Start-Process $exe -ArgumentList '--exit' -NoNewWindow
    for ($i = 0; $i -lt 15 -and (Get-Process Ohman -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
}
$running = [bool](Get-Process Ohman -ErrorAction SilentlyContinue)
W ("closed: " + (-not $running))

W ""; W "===== Ohman support report ====="
Start-Process $exe -ArgumentList '--support' -NoNewWindow -Wait
$rep = Join-Path $dir 'support-info.txt'
if (Test-Path $rep) { Get-Content $rep | ForEach-Object { W $_ } } else { W "  (no report written)" }

W ""; W "===== Driver fan test ====="
$zone = $null
try { $zone = (Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature -ErrorAction Stop | ForEach-Object { $_.CurrentTemperature / 10 - 273.15 } | Measure-Object -Maximum).Maximum } catch { }
if ($running) { W "  skipped: Ohman did not close" }
elseif ($zone -ge 85) { W ("  skipped: the machine is at " + [math]::Round($zone) + " C, run this again when it is idle") }
else {
    Start-Process $exe -ArgumentList '--driver', '--fantest' -NoNewWindow -Wait
    $drv = Join-Path $dir 'driver-check.txt'
    if (Test-Path $drv) { Get-Content $drv | ForEach-Object { W $_ } } else { W "  (no driver report written)" }
}

Start-Process $exe
Write-Host ""
Write-Host "Done. Ohman is open again. Drag ohman-fans.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
