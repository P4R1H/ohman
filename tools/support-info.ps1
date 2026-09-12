# Ohman support information. Collects what is needed to add a laptop model, with NO personal data
# (no user name, serial number, MAC or account). Read-only BIOS queries; needs administrator rights for those.
# Usage:  powershell -ExecutionPolicy Bypass -File support-info.ps1     (or double-click support-info.cmd)
# Paste the contents of support-info.txt into a "New laptop support" issue on GitHub.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot 'support-info.txt'
$lines = New-Object System.Collections.Generic.List[string]
function L($s) { $lines.Add($s); $s }

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
        $intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
        function Q($type, $data, $outSize) {
            $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
            $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20008; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = [byte[]]$data
            $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
            $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null)   # the parameter-object form is the one that returns OutData
            $od = $r.OutData
            $bytes = @(); if ($od -and $od.Data) { $bytes = @($od.Data) }
            "rc=" + $(if ($od) { $od.rwReturnCode } else { "?" }) + " data=" + (($bytes | Select-Object -First 24 | ForEach-Object { $_.ToString('X2') }) -join ' ')
        }
        # no 0x10 here: that query is the firmware's user-defined-fan trigger, not a plain read. Fan count = byte 0 of the fan table.
        L ("  0x28 system data:    " + (Q 0x28 @(0,0,0,0) 128))
        L ("  0x2D fan levels:     " + (Q 0x2D @(0,0,0,0) 128))
        L ("  0x2F fan table:      " + (Q 0x2F @(0,0,0,0) 128))
        L ("  0x2C fan types:      " + (Q 0x2C @(0,0,0,0) 128))
        L ("  0x23 thermal sensor: " + (Q 0x23 @(1,0,0,0) 4))
        L ("  0x21 gpu power:      " + (Q 0x21 @(0,0,0,0) 4))
        L ("  0x26 max fan:        " + (Q 0x26 @(0,0,0,0) 4))
        L ("  0x2B keyboard type:  " + (Q 0x2B @() 4))
        function K($type, $data, $outSize) {
            $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
            $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20009; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = [byte[]]$data
            $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
            $r = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null); $od = $r.OutData
            $bytes = @(); if ($od -and $od.Data) { $bytes = @($od.Data) }
            "rc=" + $(if ($od) { $od.rwReturnCode } else { "?" }) + " data=" + (($bytes | Select-Object -First 40 | ForEach-Object { $_.ToString('X2') }) -join ' ')
        }
        L ("  0x20009/02 colours:  " + (K 0x02 @(0) 128))
        L ("  0x20009/04 backlight:" + (K 0x04 @(0) 128))
    } catch { L ("  BIOS query failed: " + $_.Exception.Message) }
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
} catch { L ("  watcher failed: " + $_.Exception.Message) }

L ""; L "OGH platform file (which per-model JSON OGH loaded, if any):"
try {
    $k = Get-ItemProperty 'HKCU:\Software\HP\OMEN Ally\Settings' -ErrorAction Stop
    foreach ($n in 'LoadedJsonSku','LastLoadedJsonSku') { if ($k.PSObject.Properties[$n]) { L ("  {0} = {1}" -f $n, $k.$n) } }
    if ($k.PSObject.Properties['SystemDesignData']) { L ("  SystemDesignData = " + (($k.SystemDesignData | Select-Object -First 12 | ForEach-Object { $_.ToString('X2') }) -join ' ')) }
} catch { L "  (no OGH registry settings)" }

$lines | Out-File $out -Encoding utf8
""; "Saved to $out - paste its contents into the GitHub issue."
