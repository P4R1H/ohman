# Ohman GPU power check. For "the GPU pulls less with Ohman than with OMEN Gaming Hub". Run it WHILE A GAME IS
# RUNNING with Ohman in Performance, from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-gpupower.ps1 | iex
# What it does to the machine: nothing. It reads Ohman's log and settings, asks the firmware which GPU power setting
# it holds (a read, 0x21), and samples what the GPU draws for 30 seconds. Writes ohman-gpupower.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-gpupower.txt'
Set-Content $out "ohman gpu power check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Shared([string]$p) { try { $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite,Delete'); $r = New-Object IO.StreamReader($fs); $t = $r.ReadToEnd(); $r.Close(); $t -split "`r?`n" } catch { @() } }

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$proc = Get-Process Ohman -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -or -not $proc.Path) { W "Ohman is not running. Start it, put it in Performance, start a game, then paste the line again."; return }
$dir = Split-Path $proc.Path
W ("Ohman:  " + (Get-Item $proc.Path).VersionInfo.FileVersion)
$st = Join-Path $dir 'ohman.state'
if (Test-Path $st) { Shared $st | Where-Object { $_ -match '^(ModeIndex|Gpu|GpuAuto|TdpOffset|M\d\.TdpOffset|M\d\.Gpu|M\d\.GpuAuto)=' } | ForEach-Object { W "  $_" } }

W ""; W "===== Ohman's GPU power decisions ====="
Shared (Join-Path $dir 'ohman.log') | Where-Object { $_ -match '---- Ohman|GPU power|gpuPower|graphics mode' } | Select-Object -Last 40 | ForEach-Object { W "  $_" }

W ""; W "===== The NVIDIA GPU right now ====="
$gpu = Get-PnpDevice -PresentOnly -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'PCI\VEN_10DE*' } | Select-Object -First 1
if ($gpu) {
    try { $d = (Get-PnpDeviceProperty -InstanceId $gpu.InstanceId -KeyName DEVPKEY_Device_PowerData -ErrorAction Stop).Data; W ("  power state: D" + ([BitConverter]::ToInt32($d, 4) - 1) + "   (D0 = awake)") } catch { W "  power state: unknown" }
} else { W "  no NVIDIA GPU found" }

W ""; W "===== Firmware GPU power read (0x21), five tries ====="
$intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM -ErrorAction SilentlyContinue | Select-Object -First 1
function Q($type, $outSize) {
    $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
    $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20008; $in.CommandType = $type; $in.Size = 4; $in.hpqBData = [byte[]](0,0,0,0)
    $mp = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $mp.InData = $in
    try { $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $mp, $null) } catch { return ("call failed (" + (Scrub $_.Exception.Message) + ")") }
    $od = $r.OutData; $b = @(); if ($od -and $od.Data) { $b = @($od.Data) }
    "rc=" + $(if ($od) { $od.rwReturnCode } else { "?" }) + " data=" + (($b | Select-Object -First 4 | ForEach-Object { $_.ToString('X2') }) -join ' ')
}
if ($intf) { for ($i = 1; $i -le 5; $i++) { W ("  try $i   out4: " + (Q 0x21 4) + "   out128: " + (Q 0x21 128)); Start-Sleep 2 } } else { W "  HP's BIOS interface is not on this machine" }
W "  (data = custom TGP, Dynamic Boost, D-state, peak temp; 01 01 01 57 is what Hub sets in Performance)"

W ""; W "===== What the GPU draws, 30 s (draw, enforced limit, default, max, pstate, load) ====="
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    for ($i = 0; $i -lt 15; $i++) { (& nvidia-smi --query-gpu=power.draw,enforced.power.limit,power.default_limit,power.max_limit,pstate,utilization.gpu --format=csv,noheader) | ForEach-Object { W "  $_" }; Start-Sleep 2 }
} else { W "  nvidia-smi not found" }
Write-Host ""; Write-Host "Done. Drag ohman-gpupower.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
