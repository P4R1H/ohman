# Ohman support information. Collects what is needed to add a laptop model, with NO personal data
# (no user name, serial number, MAC or account). Read-only BIOS queries; needs administrator rights for those.
# Usage:  powershell -ExecutionPolicy Bypass -File support-info.ps1     (or double-click support-info.cmd)
# Paste the contents of support-info.txt into a "New laptop support" issue on GitHub.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot 'support-info.txt'
$lines = New-Object System.Collections.Generic.List[string]
function L($s) { $lines.Add($s); $s }
# Exception text carries the script's own path, which lives under C:\Users\<name>, and the OGH log lines are
# third-party text we did not write. This file is meant to be pasted into a public issue, so nothing goes in
# it without the user profile and machine name taken out first.
function Scrub($s) {
    if (-not $s) { return "" }
    $s = [string]$s
    if ($env:USERPROFILE) { $s = $s -replace [regex]::Escape($env:USERPROFILE), '<userprofile>' }
    if ($env:USERNAME)    { $s = $s -replace [regex]::Escape($env:USERNAME), '<user>' }
    if ($env:COMPUTERNAME){ $s = $s -replace [regex]::Escape($env:COMPUTERNAME), '<host>' }
    $s = $s -replace '(?i)[A-Z]:\\Users\\[^\\/:*?<>|
]+', '<userprofile>'
    ($s -replace '\s+', ' ').Trim()
}

L "Ohman support info  $(Get-Date -Format 'yyyy-MM-dd')"
L "----------------------------------------"
$cs = Get-CimInstance Win32_ComputerSystem; $bb = Get-CimInstance Win32_BaseBoard; $bios = Get-CimInstance Win32_BIOS; $os = Get-CimInstance Win32_OperatingSystem
L ("Model:        " + $cs.Model + "   (family " + $cs.SystemFamily + ", SKU " + $cs.SystemSKUNumber + ")")
L ("Board:        " + $bb.Product)
L ("BIOS:         " + $bios.SMBIOSBIOSVersion + "  " + $bios.ReleaseDate.ToString('yyyy-MM-dd'))
L ("CPU:          " + (Get-CimInstance Win32_Processor).Name)
L ("GPU:          " + ((Get-CimInstance Win32_VideoController).Name -join ' / '))
L ("Windows:      " + $os.Caption + " " + $os.Version)
$ogh = Get-AppxPackage -Name '*OMENCommandCenter*' -ErrorAction SilentlyContinue | Select-Object -First 1
L ("OGH:          " + $(if ($ogh) { $ogh.Version } else { "not installed" }))

L ""; L "WMI classes (root\wmi):"
foreach ($c in 'hpqBIntM','hpqBDataIn','hpqBDataOut4','hpqBDataOut128','hpqBEvnt') {
    $ok = $false; try { $null = Get-CimClass -Namespace root\wmi -ClassName $c -ErrorAction Stop; $ok = $true } catch { }
    L ("  {0,-16} {1}" -f $c, $(if ($ok) { "present" } else { "MISSING" }))
}

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
L ""; L ("BIOS queries (read-only)" + $(if (-not $elevated) { " - SKIPPED, run as administrator to include them" } else { "" }))
if ($elevated) {
    try {
        # Get-WmiObject, not Get-CimInstance: the calls below are ManagementObject's GetMethodParameters and
        # InvokeMethod, which a CimInstance does not have. It is legacy but present in Windows PowerShell 5.1,
        # which is what the .cmd launches. (wmic.exe is the thing that is gone on 24H2+, not this.)
        $intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
        $script:RAW = @{}
        function Q($type, $data, $outSize) {
            $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
            $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20008; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = [byte[]]$data
            $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
            # One call that throws (WMI "Generic failure", seen on 0x21 on the 2025 boards) used to abort every query after
            # it and read as "not answered". It is the call failing, not the firmware saying no, and the rest still run.
            try { $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null) }   # the parameter-object form is the one that returns OutData
            catch { $script:RAW[$type] = @{ rc = "wmi"; bytes = @() }; return ("FAILED (" + (Scrub $_.Exception.Message) + ") - the call failed, not a firmware no") }
            $od = $r.OutData
            $bytes = @(); if ($od -and $od.Data) { $bytes = @($od.Data) }
            $rc = $(if ($od) { $od.rwReturnCode } else { "?" })
            $script:RAW[$type] = @{ rc = $rc; bytes = $bytes }
            "rc=" + $rc + " data=" + (($bytes | Select-Object -First 24 | ForEach-Object { $_.ToString('X2') }) -join ' ')
        }
        # no 0x10 here: that query is the firmware's user-defined-fan trigger, not a plain read. Fan count = byte 0 of the fan table.
        L ("  0x28 system data:    " + (Q 0x28 @(0,0,0,0) 128))
        L ("  0x2D fan levels:     " + (Q 0x2D @(0,0,0,0) 128))
        L ("  0x2F fan table:      " + (Q 0x2F @(0,0,0,0) 128))
        L ("  0x2C fan types:      " + (Q 0x2C @(0,0,0,0) 128))
        L ("  0x23 temp 0 (IR):      " + (Q 0x23 @(0,0,0,0) 4))
        L ("  0x23 temp 1 (ambient): " + (Q 0x23 @(1,0,0,0) 4))
        L ("  0x23 temp 2 (PCH):     " + (Q 0x23 @(2,0,0,0) 4))
        L ("  0x23 temp 3 (VR):      " + (Q 0x23 @(3,0,0,0) 4))
        L ("  0x21 gpu power:      " + (Q 0x21 @(0,0,0,0) 4))
        L ("  0x26 max fan:        " + (Q 0x26 @(0,0,0,0) 4))
        L ("  0x2B keyboard type:  " + (Q 0x2B @() 4))
        L ("  0x52 graphics mode:  " + (Q 0x52 @(0,0,0,0) 4))
        function K($type, $data, $outSize) {
            $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
            $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20009; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = [byte[]]$data
            $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
            $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null); $od = $r.OutData
            $bytes = @(); if ($od -and $od.Data) { $bytes = @($od.Data) }
            "rc=" + $(if ($od) { $od.rwReturnCode } else { "?" }) + " data=" + (($bytes | Select-Object -First 40 | ForEach-Object { $_.ToString('X2') }) -join ' ')
        }
        L ("  0x20009/01 support:  " + (K 0x01 @(0,0,0,0) 128) + "   <- bit 0 of byte 0 = keyboard backlight supported")
        L ("  0x20009/02 colours:  " + (K 0x02 @(0) 128))
        L ("  0x20009/04 backlight:" + (K 0x04 @(0) 128))
    } catch { L ("  BIOS query failed: " + (Scrub $_.Exception.Message)) }

    # Everything above is raw. This is the same data read out loud: it is what actually gets typed into a
    # profile, and leaving it to be decoded by hand in an issue thread is how the back-and-forth starts.
    L ""; L "Decoded"
    try {
        $sd = $script:RAW[0x28]
        if ($sd -and $sd.rc -eq 0 -and $sd.bytes.Count -ge 9) {
            $b = $sd.bytes
            L ("  thermal policy:      v" + $b[3] + "   <- decides the mode bytes (v0 = 00/01/02, v1 = 30/31/50)")
            L ("  software fan control:" + $(if ($b[4] -band 1) { " yes" } else { " no" }))
            L ("  PL4 default:         " + $b[5] + " W")
            L ("  base concurrent TDP: " + $b[8] + " W   <- the 'power gain' slider starts here")
            $gm = @(); if ($b[7] -band 1) { $gm += "iGPU only" }; if ($b[7] -band 2) { $gm += "Hybrid" }
            if ($b[7] -band 4) { $gm += "Discrete" }; if ($b[7] -band 8) { $gm += "Advanced Optimus" }
            L ("  graphics modes:      0x" + $b[7].ToString('X2') + "  " + $(if ($gm) { $gm -join ', ' } else { "none offered" }))
        } else {
            L "  system data (0x28):  NOT ANSWERED. Without it the thermal-policy version is unknown, and that"
            L "                       decides the mode bytes. This board needs a readback before it can be driven:"
            L "                       say so in the issue and attach an OMEN Gaming Hub log if you have one."
        }
        $ft = $script:RAW[0x2F]
        if ($ft -and $ft.rc -eq 0 -and $ft.bytes.Count -ge 2) {
            $rows = [Math]::Min([int]$ft.bytes[1], 40); $top = 0
            for ($i = 0; $i -lt $rows; $i++) { $o = 2 + 3*$i; if ($o+1 -lt $ft.bytes.Count) { $top = [Math]::Max($top, [Math]::Max($ft.bytes[$o], $ft.bytes[$o+1])) } }
            L ("  fans:                " + $ft.bytes[0] + "   curve rows: " + $ft.bytes[1] + "   top level: " + $top + " (about " + ($top*100) + " rpm)")
        } else { L "  fan table (0x2F):    not answered - no fan readout or curve on this board" }
        $kb = $script:RAW[0x2B]
        if ($kb -and $kb.rc -eq 0 -and $kb.bytes.Count -ge 1) {
            $kt = [int]$kb.bytes[0]; if ($kt -gt 127) { $kt = $kt - 256 }      # the byte is signed; none is -1
            $name = switch ($kt) { 0 { "none" } 1 { "four zones" } 2 { "four zones" } 3 { "per key (firmware table is inert, HID is the real interface)" } 4 { "single zone" } 5 { "single zone" } default { "unknown / none" } }
            L ("  keyboard type:       " + $kt + "  -> " + $name)
        }
        $gp = $script:RAW[0x21]
        if ($gp -and $gp.rc -eq 0 -and $gp.bytes.Count -ge 4) {
            L ("  gpu power now:       cTGP=" + $gp.bytes[0] + " PPAB=" + $gp.bytes[1] + " dState=" + $gp.bytes[2] + " peakTemp=" + $gp.bytes[3] + " C")
        } elseif ($gp -and $gp.rc -eq "wmi") { L "  gpu power (0x21):    the call failed (it can depend on the GPU's state); not proof the board lacks it" }
        else { L "  gpu power (0x21):    not answered - no GPU power control on this board" }
    } catch { L ("  decode failed: " + (Scrub $_.Exception.Message)) }
}

L ""; L "OMEN key: press it now (and any other Fn keys you want mapped) - listening 12 s"
try {
    $scope = New-Object System.Management.ManagementScope('root\wmi')
    $query = New-Object System.Management.WqlEventQuery('SELECT * FROM hpqBEvnt')
    $w = New-Object System.Management.ManagementEventWatcher($scope, $query)
    $seen = @(); $end = (Get-Date).AddSeconds(12); $w.Options.Timeout = [TimeSpan]::FromSeconds(1)
    while ((Get-Date) -lt $end) { try { $e = $w.WaitForNextEvent(); $seen += ("EventID=" + $e.EventID + " EventData=" + $e.EventData) } catch { } }
    $w.Stop()
    if ($seen.Count -eq 0) { L "  (no events)" } else { $seen | Select-Object -Unique | ForEach-Object { L ("  " + $_) } }
} catch { L ("  watcher failed: " + (Scrub $_.Exception.Message)) }

L ""; L "Keyboard HID lighting (what the keyboard itself reports; read-only):"
# On a per-key board the firmware's colour table is inert and this is the interface that matters, so it is the
# single most useful thing an owner of one can send us.
try {
    $exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'Ohman.exe'
    if (Test-Path $exe) { & $exe --lamps 2>&1 | ForEach-Object { L ("  " + $_) } }
    else { L "  (Ohman.exe not found beside tools\; run this from the folder you unzipped)" }
} catch { L ("  lamp query failed: " + (Scrub $_.Exception.Message)) }

L ""; L "OGH platform file (which per-model JSON OGH loaded, if any):"
try {
    $k = Get-ItemProperty 'HKCU:\Software\HP\OMEN Ally\Settings' -ErrorAction Stop
    foreach ($n in 'LoadedJsonSku','LastLoadedJsonSku') { if ($k.PSObject.Properties[$n]) { L ("  {0} = {1}" -f $n, $k.$n) } }
    if ($k.PSObject.Properties['SystemDesignData']) { L ("  SystemDesignData = " + (($k.SystemDesignData | Select-Object -First 12 | ForEach-Object { $_.ToString('X2') }) -join ' ')) }
} catch { L "  (no OGH registry settings)" }

L ""; L "OGH's own capability answers (from its log, if OGH has ever run):"
# This is the part that settles the questions raw bytes cannot: whether cTGP exists on this board, what the
# Smart Performance Gain offset actually is, which power limits OGH believes in. It answered a GPU-power bug
# report in one read, after a day of guessing from payloads.
try {
    $logDir = Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'AD2F1837.OMENCommandCenter*' -ErrorAction Stop |
              ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\HPOMEN' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $logDir) { L "  (OGH has never run on this machine)" }
    else {
        # OGH keeps its log open while it runs; ReadLines asks for it unshared and fails ("being used by another process").
        function SharedLines($path) {
            $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $sr = New-Object IO.StreamReader($fs)
            try { while ($null -ne ($line = $sr.ReadLine())) { $line } } finally { $sr.Dispose() }
        }
        $logs = Get-ChildItem $logDir -Filter 'HPOMEN*.log' -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 4
        $keys = 'IsCtgpModeSupport','IsIccMaxSupport','IsSurfaceTempSupport','ChangeTppToDynamicBoost',
                'GetUnleashedModePowerLimit4','GetUnleashedModeSurfaceTemp','GetUnleashedModeTppOffset',
                'GetUnleashedModeTppMode','IsEnableTgpPpab','SetPL1DefaultValue'
        $hits = @{}
        foreach ($f in $logs) {
            foreach ($line in (SharedLines $f.FullName)) {
                foreach ($k in $keys) {
                    if ($line -like "*$k*" -and -not $hits.ContainsKey($k)) {
                        $hits[$k] = ($line -replace '^.*\[INF\]\s*', '' -replace '\[PID: \d+\] \[TID: \d+\] ', '').Trim()
                    }
                }
            }
        }
        if ($hits.Count -eq 0) { L "  (no capability lines found in the last $($logs.Count) logs)" }
        else { foreach ($k in $keys) { if ($hits.ContainsKey($k)) { L ("  " + (Scrub $hits[$k])) } } }
        # The payloads OGH sends are the ground truth for any byte we are unsure about.
        $sent = @{}
        foreach ($f in $logs) {
            foreach ($line in (SharedLines $f.FullName)) {
                if ($line -match 'inputData=([0-9,]{3,40}),\s*$') {
                    $v = $matches[1]
                    if ($v -ne '0,0,0,0') { if ($sent.ContainsKey($v)) { $sent[$v]++ } else { $sent[$v] = 1 } }
                }
            }
        }
        if ($sent.Count -gt 0) {
            L "  payloads OGH sent (value x times seen, short ones only):"
            $sent.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object { L ("    " + $_.Key + "   x" + $_.Value) }
        }
    }
} catch { L ("  (could not read OGH logs: " + (Scrub $_.Exception.Message) + ")") }

L ""; L "ACPI thermal zones (what Windows exposes; Ohman picks the hottest valid one):"
try {
    $cat = New-Object System.Diagnostics.PerformanceCounterCategory('Thermal Zone Information')
    $names = $cat.GetInstanceNames() | Sort-Object
    if (-not $names) { L "  (none - this machine exposes no ACPI thermal zone)" }
    foreach ($n in $names) {
        try {
            $pc = New-Object System.Diagnostics.PerformanceCounter('Thermal Zone Information','Temperature',$n,$true)
            $null = $pc.NextValue(); Start-Sleep -Milliseconds 120
            $k = $pc.NextValue()
            $c = [Math]::Round($k - 273.15, 1)
            $note = if ($k -lt 283 -or $k -gt 398) { '  (outside 10-125 C, Ohman ignores it)' } else { '' }
            L ('  {0,-34} {1,7} C{2}' -f (Scrub $n), $c, $note)
            $pc.Dispose()
        } catch { L ('  {0,-34}  unreadable' -f (Scrub $n)) }
    }
    L "  If the number above never moves while the machine is busy, that zone is not the CPU and that is worth saying."
} catch { L ('  (thermal zones unavailable: ' + (Scrub $_.Exception.Message) + ')') }

L ""; L "Ohman's own log (what it decided about this board, and why):"
# If they have run Ohman at all, this says which profile it picked, which commands failed and what the
# thermal zone reads. It is the single most useful thing in a bug report and people rarely think to send it.
try {
    $cand = @((Join-Path (Split-Path $PSScriptRoot -Parent) 'ohman.log'), (Join-Path $env:LOCALAPPDATA 'Ohman\ohman.log'))
    $log = $cand | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $log) { L "  (Ohman has not been run yet, or its log is elsewhere)" }
    else {
        $keep = Get-Content $log -Tail 400 | Where-Object {
            $_ -match 'platform:|generic profile|BIOS ok|self-test|read-only|no fan table|system data|thermal zone|keyboard lighting|lighting:|graphics|THERMAL GUARD|park' }
        if (-not $keep) { $keep = Get-Content $log -Tail 25 }
        $keep | Select-Object -Last 30 | ForEach-Object { L ('  ' + (Scrub $_)) }
    }
} catch { L ('  (could not read the Ohman log: ' + (Scrub $_.Exception.Message) + ')') }

L ""; L "NVIDIA GPU power limits (for GPU power reports):"
try {
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $smi) { L "  (nvidia-smi not present - AMD or iGPU only)" }
    else {
        $q = & nvidia-smi --query-gpu=name,power.limit,power.default_limit,power.min_limit,power.max_limit --format=csv,noheader 2>&1
        $q | ForEach-Object { L ('  ' + (Scrub $_)) }
    }
} catch { L ('  (nvidia-smi failed: ' + (Scrub $_.Exception.Message) + ')') }

$lines | Out-File $out -Encoding utf8
""; "Saved to $out - paste its contents into the GitHub issue."
