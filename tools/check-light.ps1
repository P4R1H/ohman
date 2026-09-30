# Ohman keyboard lighting check. For keyboards whose colours or effects do not change. Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-light.ps1 | iex
# What it does to the machine: changes only the keyboard colours and backlight for about a minute and puts them
# back. Windows Dynamic Lighting is switched off for HP's own virtual lighting device during the test, the way Ohman
# does before it paints, and switched back to exactly what it was afterwards. It touches no fans, modes or power.
# Writes ohman-light.txt to the Desktop.
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
function Ask([string]$q) { $a = Read-Host "$q (y/n)"; W "  $q -> $a"; return $a }

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
# The same colour written a second time where some newer tables keep a zone count and a copy of the colours
# (byte 6, bytes 7..18), as well as at 25+. Only used when the normal write did not light anything.
function PaintBoth([byte[]]$rgb) {
    $t = (Colors).data; $buf = New-Object byte[] 128; [Array]::Copy($t, $buf, [Math]::Min($t.Length, 128))
    $buf[6] = 4
    for ($z = 0; $z -lt 4; $z++) { foreach ($o in 7, 25) { $buf[$o + 3 * $z] = $rgb[0]; $buf[$o + 1 + 3 * $z] = $rgb[1]; $buf[$o + 2 + 3 * $z] = $rgb[2] } }
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
# Windows Dynamic Lighting repaints the keyboard through HP's virtual devices while their switch is on, over
# anything written here, so the test switches it off for them (Ohman does the same for the keyboard). Every value is
# recorded first (a missing value stays missing) and put back in the finally below, whatever happens in between. A
# device whose value cannot be read is left alone rather than risk putting back the wrong thing.
$dlSaved = @()
try {
    $dlSaved = @(Get-ChildItem 'HKCU:\Software\Microsoft\Lighting\Devices' -ErrorAction Stop | Where-Object { $_.PSChildName -match 'VHF' } | ForEach-Object {
        try { $props = Get-ItemProperty $_.PSPath -ErrorAction Stop; [pscustomobject]@{ Path = $_.PSPath; Value = $props.AmbientLightingEnabled } } catch { }
    })
} catch { }
if ($dlSaved.Count -gt 0) {
    Write-Host "If this window is closed before it finishes, turn Dynamic Lighting back on in Windows Settings > Personalization > Dynamic Lighting." -ForegroundColor DarkGray
}
try {
    foreach ($d in $dlSaved) { Set-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -Value 0 -Type DWord -ErrorAction Stop }
    W ("  Dynamic Lighting switched off for " + $dlSaved.Count + " HP virtual device(s) for the test")
    Start-Sleep -Seconds 2
    $s1 = Hex ((Colors).data | Select-Object -Skip 25) 12; Start-Sleep -Seconds 1; $s2 = Hex ((Colors).data | Select-Object -Skip 25) 12
    W ("  table with Windows off: $s1, 1 s later: $s2" + $(if ($s1 -eq $s2) { "  (steady)" } else { "  (STILL CHANGING: something other than Windows is painting)" }))
    [void](Call $L 0x05 ([byte[]]@(0xE4,0,0,0)) 4)
    W ("  red:  rc=" + (Paint ([byte[]]@(255,0,0))))
    Start-Sleep -Milliseconds 500; W ("  read back after 0.5 s: " + (Hex ((Colors).data | Select-Object -Skip 25) 12))
    Start-Sleep -Milliseconds 2500; W ("  read back after 3 s:   " + (Hex ((Colors).data | Select-Object -Skip 25) 12))
    $red = Ask "Is the keyboard red now?"
    for ($i = 0; $i -lt 20; $i++) { [void](Paint $(if ($i % 2) { [byte[]]@(0,0,255) } else { [byte[]]@(255,0,0) })); Start-Sleep -Milliseconds 120 }
    [void](Ask "Did it flash between red and blue?")
    if ($red -notmatch '^\s*y') {
        W ("  red, second layout (bytes 6..18 as well):  rc=" + (PaintBoth ([byte[]]@(255,0,0))))
        Start-Sleep -Milliseconds 500; $t = (Colors).data
        W ("  read back: 6..18 " + (Hex ($t | Select-Object -Skip 6) 13) + " / 25.. " + (Hex ($t | Select-Object -Skip 25) 12))
        [void](Ask "Is the keyboard red now?")
    }
    # The level bits: the Fn key sets them (E4 -> B2 on board 8EDE); does a written level dim the keyboard too?
    [void](Call $L 0x05 ([byte[]]@(0x99,0,0,0)) 4)
    Start-Sleep -Milliseconds 500; W ("  backlight written 99 (on, level 25), reads back " + (Hex (Level).data 1))
    [void](Ask "Did the keyboard get dimmer?")
    [void](Call $L 0x05 ([byte[]]@(0xE4,0,0,0)) 4)
    $before = Hex (Level).data 1
    Read-Host "Now press your keyboard backlight key once (usually Fn + a key with a keyboard icon), then press Enter" | Out-Null
    W ("  backlight byte before the key: $before, after: " + (Hex (Level).data 1))
} finally {
    # Put back exactly what was there: the whole colour table, the backlight byte, then Windows' switches. Each step
    # on its own, so one that fails does not leave the others undone.
    try { $buf = New-Object byte[] 128; [Array]::Copy($orig.data, $buf, [Math]::Min($orig.data.Length, 128)); [void](Call $L 0x03 $buf 4) } catch { W ("  colour restore failed: " + $_) }
    try { [void](Call $L 0x05 ([byte[]]@($lvl.data[0],0,0,0)) 4) } catch { W ("  backlight restore failed: " + $_) }
    foreach ($d in $dlSaved) {
        try {
            if ($null -eq $d.Value) { Remove-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -Value $d.Value -Type DWord -ErrorAction Stop }
        } catch { W ("  Dynamic Lighting restore failed: " + $_) }
    }
    W "  colours, backlight and Windows Dynamic Lighting put back"
}

Write-Host ""
Write-Host "Done. Drag ohman-light.txt from your Desktop into the GitHub issue, then open Ohman again." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
