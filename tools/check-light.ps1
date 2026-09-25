# Ohman keyboard lighting check. For keyboards whose colours or effects do not change. Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-light.ps1 | iex
# What it does to the machine: changes only the keyboard colours for about 30 seconds and puts them back. It
# touches no fans, modes or power. Writes ohman-light.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # HP's firmware calls need Windows PowerShell's WMI objects; PowerShell 7 does not have them. Say so rather than
    # relaunching: a script that starts powershell.exe with a download-and-run command is what Defender blocks as ClickFix.
    Write-Host "This one needs Windows PowerShell. Search the Start menu for Windows PowerShell, right click it, Run as administrator, and paste the line there." -ForegroundColor Yellow; return
}
# The enum, not the string: IsInRole('Administrator') looks up the built-in *user* account of that name (RID 500)
# and is false in every elevated window except that account's own, whatever the language of Windows.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
if (Get-Process Ohman -ErrorAction SilentlyContinue) {
    Write-Host "Quit Ohman first (right click its tray icon, Exit), then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-light.txt'
Set-Content $out "ohman lighting check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Hex($b, $n) { ($b | Select-Object -First $n | ForEach-Object { $_.ToString('X2') }) -join ' ' }
function Ask([string]$q) { $a = Read-Host "$q (y/n)"; W "  $q -> $a" }

$intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM | Select-Object -First 1
if (-not $intf) { W "HP's firmware interface is not present."; return }
function Call($cmd, $type, [byte[]]$data, $outSize) {
    $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
    $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = $cmd; $in.CommandType = $type; $in.Size = $data.Length; $in.hpqBData = $data
    $p = $intf.GetMethodParameters("hpqBIOSInt$outSize"); $p.InData = $in
    $od = $intf.InvokeMethod("hpqBIOSInt$outSize", $p, $null).OutData
    $bytes = [byte[]]@(); if ($od -and $od.Data) { $bytes = [byte[]]@($od.Data) }
    [pscustomobject]@{ rc = $(if ($od) { $od.rwReturnCode } else { -1 }); data = $bytes }
}
$L = 0x20009
function Colors { Call $L 0x02 ([byte[]]@(0)) 128 }
function Level { Call $L 0x04 ([byte[]]@(0)) 128 }
function Paint([byte[]]$rgb) {
    $t = (Colors).data; $buf = New-Object byte[] 128; [Array]::Copy($t, $buf, [Math]::Min($t.Length, 128))
    for ($z = 0; $z -lt 4; $z++) { $buf[25 + 3 * $z] = $rgb[0]; $buf[26 + 3 * $z] = $rgb[1]; $buf[27 + 3 * $z] = $rgb[2] }
    (Call $L 0x03 $buf 4).rc
}

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$kt = Call 0x20008 0x2B ([byte[]]@(0,0,0,0)) 4
W ("keyboard type (0x2B): rc=" + $kt.rc + " " + (Hex $kt.data 4))
$sup = Call $L 0x01 ([byte[]]@(0,0,0,0)) 128; W ("support    (01): rc=" + $sup.rc + " " + (Hex $sup.data 16))
$orig = Colors;                                 W ("colours    (02): rc=" + $orig.rc + " " + (Hex $orig.data 40))
$lvl = Level;                                   W ("backlight  (04): rc=" + $lvl.rc + " " + (Hex $lvl.data 4))

W ""; W "===== Lighting devices ====="
Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction SilentlyContinue).Data -join ' ') -match 'HID_DEVICE_UP:0059' } | ForEach-Object {
    $id = if ($_.InstanceId -match 'VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?') { $Matches[0] } elseif ($_.InstanceId -match 'VHF') { 'HP virtual' } else { '?' }
    W ("  " + $id + "  " + $_.FriendlyName)
}
W "===== Windows Dynamic Lighting ====="
try {
    $g = Get-ItemProperty 'HKCU:\Software\Microsoft\Lighting' -ErrorAction Stop; W ("  global AmbientLightingEnabled=" + $g.AmbientLightingEnabled)
    Get-ChildItem 'HKCU:\Software\Microsoft\Lighting\Devices' -ErrorAction Stop | ForEach-Object {
        $v = (Get-ItemProperty $_.PSPath).AmbientLightingEnabled
        $n = if ($_.PSChildName -match 'VID_[0-9A-F]{4}&PID_[0-9A-F]{4}') { $Matches[0] } elseif ($_.PSChildName -match 'VHF') { 'HP virtual' } else { 'other' }
        W ("  $n AmbientLightingEnabled=$v")
    }
} catch { W "  (no Dynamic Lighting settings)" }
W ("HP lighting software running: " + ((Get-Process | Where-Object { $_.Name -match 'OMEN|Omen|HyperX|LightStudio' } | ForEach-Object { $_.Name } | Sort-Object -Unique) -join ', '))

W ""; W "===== Test ====="
if ($orig.rc -ne 0 -or $orig.data.Length -lt 37 -or $lvl.rc -ne 0 -or $lvl.data.Length -lt 1) {
    W "  skipped: the firmware did not answer the colour or backlight read, so nothing could be put back afterwards"
    Write-Host ""; Write-Host "Done. Drag ohman-light.txt from your Desktop into the GitHub issue." -ForegroundColor Green
    Start-Process explorer.exe "/select,`"$out`""; return
}
try {
    [void](Call $L 0x05 ([byte[]]@(0xE4,0,0,0)) 4)
    W ("  red:  rc=" + (Paint ([byte[]]@(255,0,0))))
    Start-Sleep -Milliseconds 500; W ("  read back after 0.5 s: " + (Hex ((Colors).data | Select-Object -Skip 25) 12))
    Start-Sleep -Milliseconds 2500; W ("  read back after 3 s:   " + (Hex ((Colors).data | Select-Object -Skip 25) 12))
    Ask "Is the keyboard red now?"
    for ($i = 0; $i -lt 20; $i++) { [void](Paint $(if ($i % 2) { [byte[]]@(0,0,255) } else { [byte[]]@(255,0,0) })); Start-Sleep -Milliseconds 120 }
    Ask "Did it flash between red and blue?"
    $before = Hex (Level).data 1
    Read-Host "Now press your keyboard backlight key once (usually Fn + a key with a keyboard icon), then press Enter" | Out-Null
    W ("  backlight byte before the key: $before, after: " + (Hex (Level).data 1))
} finally {
    # Put back exactly what was there: the whole colour table and the backlight byte.
    $buf = New-Object byte[] 128; [Array]::Copy($orig.data, $buf, [Math]::Min($orig.data.Length, 128)); [void](Call $L 0x03 $buf 4)
    [void](Call $L 0x05 ([byte[]]@($lvl.data[0],0,0,0)) 4)
    W "  colours and backlight put back"
}

Write-Host ""
Write-Host "Done. Drag ohman-light.txt from your Desktop into the GitHub issue, then open Ohman again." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
