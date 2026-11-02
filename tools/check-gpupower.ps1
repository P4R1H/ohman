# Ohman GPU power check. For "the GPU pulls less with Ohman than with OMEN Gaming Hub". Start a game first, then
# paste this in Terminal (Admin) and switch back to the game when it says so:
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-gpupower.ps1 | iex
# What it does to the machine: it reads Ohman's log and settings, asks the firmware which GPU power setting it holds
# (0x21, a read) and measures what the GPU draws for 20 seconds. Then it sends the GPU setting OMEN Gaming Hub sends
# in Performance (0x22 {1,1,1,87}: custom TGP and Dynamic Boost on, peak 87 C, as Hub's own log shows on 8D40, #91),
# measures again for 30 seconds, and asks whether to keep it or send Hub's Balanced one {0,0,1,87}. Nothing else is
# written. Writes ohman-gpupower.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Core') {
    Write-Host "This one needs Windows PowerShell. Search the Start menu for Windows PowerShell, right click it, Run as administrator, and paste the line there." -ForegroundColor Yellow; return
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-gpupower.txt'
Set-Content $out "ohman gpu power check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Shared([string]$p) { try { $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite,Delete'); $r = New-Object IO.StreamReader($fs); $t = $r.ReadToEnd(); $r.Close(); $t -split "`r?`n" } catch { @() } }

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$dir = $null
$proc = Get-Process Ohman -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -and $proc.Path) { $dir = Split-Path $proc.Path; W ("Ohman:  " + (Get-Item $proc.Path).VersionInfo.FileVersion + "   running") }
else {
    foreach ($d in @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", "$env:LOCALAPPDATA\Ohman")) {
        $f = Get-ChildItem $d -Filter Ohman.exe -Recurse -Depth 2 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($f) { $dir = $f.DirectoryName; W ("Ohman:  " + $f.VersionInfo.FileVersion + "   not running"); break }
    }
}
if ($dir) {
    Shared (Join-Path $dir 'ohman.state') | Where-Object { $_ -match '^(ModeIndex|Gpu|GpuAuto|TdpOffset|M\d\.TdpOffset|M\d\.Gpu|M\d\.GpuAuto)=' } | ForEach-Object { W "  $_" }
    W ""; W "===== Ohman's GPU power decisions ====="
    Shared (Join-Path $dir 'ohman.log') | Where-Object { $_ -match '---- Ohman|GPU power|gpuPower|graphics mode' } | Select-Object -Last 40 | ForEach-Object { W "  $_" }
} else { W "Ohman not found (fine, the test below does not need it)" }

W ""; W "===== The NVIDIA GPU right now ====="
$gpu = Get-PnpDevice -PresentOnly -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'PCI\VEN_10DE*' } | Select-Object -First 1
if ($gpu) {
    try { $d = (Get-PnpDeviceProperty -InstanceId $gpu.InstanceId -KeyName DEVPKEY_Device_PowerData -ErrorAction Stop).Data; W ("  power state: D" + ([BitConverter]::ToInt32($d, 4) - 1) + "   (D0 = awake)") } catch { W "  power state: unknown" }
} else { W "  no NVIDIA GPU found" }

$intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $intf) { W "  HP's BIOS interface is not on this machine"; Start-Process explorer.exe "/select,`"$out`""; return }
# One mailbox call. ok = the call itself went through; rc then says whether the firmware took it (0 = yes).
function Call($type, [byte[]]$data, $outSize) {
    $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
    $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20008; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = $data
    try { $mp = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $mp.InData = $in; $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $mp, $null) } catch { return [pscustomobject]@{ ok = $false; rc = $null; text = "call failed (" + (Scrub $_.Exception.Message).Trim() + ")" } }
    $od = $r.OutData; $b = @(); if ($od -and $od.Data) { $b = @($od.Data) }
    $rc = $(if ($od) { $od.rwReturnCode } else { $null })
    [pscustomobject]@{ ok = $true; rc = $rc; text = "rc=" + $rc + " data=" + (($b | Select-Object -First 4 | ForEach-Object { $_.ToString('X2') }) -join ' ') }
}
function Read21 { for ($i = 1; $i -le 3; $i++) { W ("  try $i   out4: " + (Call 0x21 ([byte[]](0,0,0,0)) 4).text + "   out128: " + (Call 0x21 ([byte[]](0,0,0,0)) 128).text); Start-Sleep 1 } }
# Ohman sends 0x22 with no output buffer; if that fails as a call, the 4 byte one is tried too.
function Send22([byte[]]$p) {
    foreach ($size in 0, 4) {
        $r = Call 0x22 $p $size
        W ("  sent 0x22 {" + ($p -join ',') + "} out" + $size + ": " + $r.text)
        if ($r.ok) { return ($r.rc -eq 0) }
    }
    $false
}

W ""; W "===== Firmware GPU power read (0x21), before ====="
Read21
W "  (data = custom TGP, Dynamic Boost, D-state, peak temp; 01 01 01 57 is Hub's Performance setting)"

$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
function Sample([string]$label, [int]$seconds) {
    $draws = @(); $utils = @(); $limit = ''
    for ($t = 0; $t -lt $seconds; $t += 2) {
        $l = (& nvidia-smi --query-gpu=power.draw,enforced.power.limit,temperature.gpu,pstate,utilization.gpu,power.max_limit --format=csv,noheader,nounits) | Select-Object -First 1
        W ("  " + $label + "  " + $l)
        $f = @($l -split ',\s*')
        # an idle laptop GPU can report a nonsense draw (365.89 W in P3); a reading above the card's own maximum is dropped
        $v = 0.0; $top = 0.0; $inv = [Globalization.CultureInfo]::InvariantCulture
        if ($f.Count -ge 6 -and [double]::TryParse($f[0], [Globalization.NumberStyles]::Float, $inv, [ref]$v) -and
            (-not [double]::TryParse($f[5], [Globalization.NumberStyles]::Float, $inv, [ref]$top) -or $v -le $top + 5)) { $draws += $v }
        $u = 0; if ($f.Count -ge 5 -and [int]::TryParse($f[4].Trim(), [ref]$u)) { $utils += $u }
        if ($f.Count -ge 5) { $limit = $f[1] }
        Start-Sleep 2
    }
    $avg = 0; $peak = 0; $load = 0
    if ($draws.Count) { $avg = ($draws | Measure-Object -Average).Average; $peak = ($draws | Measure-Object -Maximum).Maximum }
    if ($utils.Count) { $load = ($utils | Measure-Object -Average).Average }
    [pscustomobject]@{ avg = [math]::Round($avg); peak = [math]::Round($peak); load = [math]::Round($load); limit = $limit }
}

if ($smi) {
    Write-Host ""
    Write-Host "Switch back to your game now and play normally. Measuring starts in 10 seconds and takes under a minute; this window beeps when it is done." -ForegroundColor Cyan
    Start-Sleep 10
    W ""; W "===== Before: the GPU for 20 s (draw W, enforced limit W, temp C, pstate, load %, max W) ====="
    $before = Sample "before" 20
} else { W ""; W "nvidia-smi not found, so the draw is not measured; watch the GPU power in your usual tool instead" }

W ""; W "===== Hub's Performance GPU setting ====="
$took = Send22 ([byte[]](1, 1, 1, 87))
if ($smi) { Start-Sleep 3; W ""; W "===== After: the GPU for 30 s ====="; $after = Sample "after " 30 }
W ""; W "===== Firmware GPU power read (0x21), after ====="
Read21

W ""; W "===== Summary ====="
W ("  the firmware " + $(if ($took) { "took" } else { "did NOT take" }) + " Hub's Performance GPU setting")
if ($smi) {
    W ("  before: " + $before.avg + " W average, " + $before.peak + " W peak, load " + $before.load + "%, limit " + $before.limit + " W")
    W ("  after:  " + $after.avg + " W average, " + $after.peak + " W peak, load " + $after.load + "%, limit " + $after.limit + " W")
    if ($before.load -lt 40) { W "  the GPU was not busy before, so the numbers may not say much: was the game running?" }
    elseif ($after.peak -ge $before.peak + 8) { W "  the GPU draws more with Hub's setting" }
    else { W "  no real change in what the GPU draws" }
}

Write-Host ""
[console]::Beep(880, 250)
$k = Read-Host "Keep Hub's Performance GPU setting? Type K and Enter to keep it, or just Enter to send Hub's Balanced one"
if ($k -match '^\s*[kK]') { W "  kept Hub's Performance GPU setting" }
else { W ""; W "===== Back to Hub's Balanced GPU setting ====="; [void](Send22 ([byte[]](0, 0, 1, 87))) }
Write-Host ""; Write-Host "Done. Drag ohman-gpupower.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
