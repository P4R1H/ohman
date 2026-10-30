# Ohman thermal guard check. For "the fans stay loud after a game and only quitting Ohman brings them down".
# Run it while the fans are still stuck high, before quitting Ohman, from Terminal (normal or Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-guard.ps1 | iex
# What it does to the machine: nothing. Ohman keeps running and the fans are not touched. It reads Ohman's own log
# and settings file and the Windows thermal zone, and writes ohman-guard.txt to the Desktop.
$ErrorActionPreference = 'Continue'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-guard.txt'
Set-Content $out "ohman guard check $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding utf8
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
W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   SKU: " + (Get-CimInstance Win32_ComputerSystem).SystemSKUNumber + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$exe = FindOhman
if (-not $exe) { W "Ohman.exe not found. Open Ohman once, then run this again."; return }
$dir = Split-Path $exe
W ("Ohman:  " + (Get-Item $exe).VersionInfo.FileVersion + "   running: " + [bool](Get-Process Ohman -ErrorAction SilentlyContinue))

W ""; W "===== Windows thermal zones now ====="
try { Get-CimInstance Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction Stop | ForEach-Object { W ("  " + $_.Name + "  " + [math]::Round($_.Temperature - 273.15) + " C") } } catch { W "  unavailable" }

W ""; W "===== Fan and guard settings (Fan: 0 auto, 1 max, 2 manual, 3 curve; M0 eco, M1 balanced, M2 performance) ====="
Shared (Join-Path $dir 'ohman.state') | Where-Object { $_ -match '^(ModeIndex|M\d\.(Fan\d?|Curve\w*|GpuCurve\w*)|Guard\w*|Max\w*|FanCeilingSeen|FanLevelsRefusedOn|PollMs|DriverUse)=' } | ForEach-Object { W "  $_" }

W ""; W "===== Guard, curve and hand-off lines from Ohman's log (last 6 hours) ====="
$since = (Get-Date).AddHours(-6)
$pat = '---- Ohman|THERMAL GUARD|thermal guard|curve \d+/\d+|fans held by the firmware|fans back with the curve|max fan|Max fan|parked fans|display (on|off)|resume|FAIL|giving up|fan ceiling'
$hits = @(Shared (Join-Path $dir 'ohman.log') | Where-Object {
    $t = [datetime]::MinValue
    $_ -match $pat -and $_.Length -ge 19 -and [datetime]::TryParseExact($_.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$t) -and $t -ge $since })
$hits | Select-Object -Last 400 | ForEach-Object { W "  $_" }

W ""; W "===== Summary ====="
$eng = $hits | Where-Object { $_ -match 'THERMAL GUARD engaged' } | Select-Object -Last 1
if (-not $eng) { W "  the thermal guard has not engaged in the last 6 hours" }
else {
    $i = [array]::LastIndexOf($hits, $eng)
    $end = $hits[$i..($hits.Count - 1)] | Where-Object { $_ -match 'thermal guard (released|switched off)|---- Ohman|parked fans' } | Select-Object -First 1
    if ($end) { W ("  guard last engaged " + $eng.Substring(0, 19) + ", ended: " + $end.Trim()) }
    else { W ("  guard engaged at " + $eng.Substring(0, 19) + " and is STILL HOLDING the fans") }
}
$hold = $hits | Where-Object { $_ -match 'fans held by the firmware|fans back with the curve' } | Select-Object -Last 1
if ($hold -match 'held by') { W "  the firmware is holding the fans: $hold" }
Write-Host ""
Write-Host "Done. Drag ohman-guard.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
