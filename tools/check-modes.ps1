# Ohman mode check: what the firmware's own fan control does in each mode, with Ohman closed. For "Performance
# sends the fans to full at idle" and "Eco and Balanced sound the same" (#98). Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-modes.ps1 | iex
# What it does to the machine: with Ohman closed it switches the laptop to Balanced, then Performance, then Eco,
# then back to Balanced, about a minute each, using exactly the mode bytes Ohman already sends on this laptop (read
# from Ohman's own log), and reads the fan speed every 5 seconds. It never sets a fan speed, so the firmware drives
# the fans the whole time. If the machine gets hot it turns max fan on and stops. Writes ohman-modes.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Core') {
    Write-Host "This one needs Windows PowerShell. Search the Start menu for Windows PowerShell, right click it, Run as administrator, and paste the line there." -ForegroundColor Yellow; return
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
if (Get-Process Ohman -ErrorAction SilentlyContinue) {
    Write-Host "Quit Ohman first (right click its tray icon, Exit), then paste the line again." -ForegroundColor Yellow; return
}
if ((Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1).BatteryStatus -eq 1) {
    Write-Host "Plug the charger in first, then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-modes.txt'
Set-Content $out "ohman mode check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Shared([string]$p) {
    try { $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite,Delete'); $r = New-Object IO.StreamReader($fs); $t = $r.ReadToEnd(); $r.Close(); $t -split "`r?`n" } catch { @() }
}
W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)

# The bytes come from Ohman's own decision on this machine ("generic profile: ... modes EE/BB/PP"), never from here.
$log = $null
foreach ($d in @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", "$env:LOCALAPPDATA\Ohman")) {
    $f = Get-ChildItem $d -Filter ohman.log -Recurse -Depth 3 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { $log = $f.FullName; break }
}
$m = $null
if ($log) { $m = Shared $log | Where-Object { $_ -match 'generic profile: .* modes ([0-9A-F]{2})/([0-9A-F]{2})/([0-9A-F]{2})' } | Select-Object -Last 1 }
if (-not $m -or $m -notmatch 'modes ([0-9A-F]{2})/([0-9A-F]{2})/([0-9A-F]{2})') {
    W "Ohman's log does not say which mode bytes it uses on this laptop, so nothing was sent. Open Ohman once, quit it, and paste the line again."
    Start-Process explorer.exe "/select,`"$out`""; return
}
$eco = [Convert]::ToByte($Matches[1], 16); $bal = [Convert]::ToByte($Matches[2], 16); $perf = [Convert]::ToByte($Matches[3], 16)
W ("Ohman's bytes on this laptop: eco 0x{0:X2}  balanced 0x{1:X2}  performance 0x{2:X2}" -f $eco, $bal, $perf)

$intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
if (-not $intf) { W "HP's firmware interface is not present."; return }
function Call($type, [byte[]]$data, $outSize) {
    $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
    $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20008; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = $data
    $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
    $od = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null).OutData
    $bytes = [byte[]]@(); if ($od -and $od.Data) { $bytes = [byte[]]@($od.Data) }
    [pscustomobject]@{ rc = $(if ($od) { $od.rwReturnCode } else { -1 }); data = $bytes }
}
# Only these leave the script: 0x1A set mode {FF, byte, 0, 0} as Ohman sends it plugged in, 0x2D read fan levels,
# 0x27 max fan on (only if it gets hot) and off (at the start, so the firmware's curve is what is measured).
function Mode([byte]$b) { (Call 0x1A ([byte[]]@(0xFF, $b, 0, 0)) 0).rc }
function Fans { $r = Call 0x2D ([byte[]]@(0,0,0,0)) 128; if ($r.rc -eq 0 -and $r.data.Length -ge 2) { "{0,5} / {1,5} rpm" -f ($r.data[0] * 100), ($r.data[1] * 100) } else { "fans: rc " + $r.rc } }
function Cpu {
    try { [math]::Round(((Get-Counter '\Thermal Zone Information(*)\Temperature' -ErrorAction Stop).CounterSamples | Measure-Object -Property CookedValue -Maximum).Maximum - 273.15) } catch { -1 }
}
$script:hot = $false
function Watch([string]$name, [int]$seconds) {
    for ($t = 0; $t -le $seconds -and -not $script:hot; $t += 5) {
        $c = Cpu
        W ("  {0:HH:mm:ss}  {1,-12} {2}   cpu {3} C" -f (Get-Date), $name, (Fans), $c)
        if ($c -ge 85) { W "  too hot: max fan on, stopping"; [void](Call 0x27 ([byte[]]@(1)) 0); $script:hot = $true; return }
        Start-Sleep -Seconds 5
    }
}

$c0 = Cpu
if ($c0 -ge 70) { W ("The laptop is at $c0 C. Let it idle for a few minutes and paste the line again."); Start-Process explorer.exe "/select,`"$out`""; return }
Read-Host "Leave the laptop idle (no games, no downloads) for the next five minutes, then press Enter" | Out-Null
try {
    W ("max fan off: rc " + (Call 0x27 ([byte[]]@(0)) 0).rc)
    # The fans keep the last level Ohman wrote for up to two minutes after it quits, so Balanced runs longer first.
    W ("balanced:    rc " + (Mode $bal)); Watch "balanced" 120
    if (-not $script:hot) { W ("performance: rc " + (Mode $perf)); Watch "performance" 60 }
    if (-not $script:hot -and $eco -ne $bal) { W ("eco:         rc " + (Mode $eco)); Watch "eco" 60 }
} finally {
    W ("balanced again: rc " + (Mode $bal))
}
W ""
W "Did the fans sound different between the steps? Say so in the issue."
Write-Host ""
Write-Host "Done. Drag ohman-modes.txt from your Desktop into the GitHub issue, then open Ohman again." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
