# Collects everything that decides which lights Ohman can drive on this laptop, into lighttest.txt.
# Reads only: no lighting device is opened for writing and nothing in the registry is changed.
# Asked of owners whose keyboard or light bar will not change colour. Run lighttest.cmd, paste the file.
$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot 'lighttest.txt'
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'Ohman.exe'
function W([string]$s) { $s; $s | Out-File $out -Append -Encoding utf8 }

"lighttest $(Get-Date -Format 'yyyy-MM-dd HH:mm')" | Out-File $out -Encoding utf8
W ""
W "Model:  $((Get-CimInstance Win32_ComputerSystem).Model)"
W "Board:  $((Get-CimInstance Win32_BaseBoard).Product)"
W "SKU:    $((Get-CimInstance Win32_ComputerSystemProduct).Name)"
W "BIOS:   $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"
W "Windows: $((Get-CimInstance Win32_OperatingSystem).Version)"

# 1. What Ohman itself can see. This is the list it picks from, so a device missing here is a device it
#    can never drive, and a device here that is not the keyboard is one it may be switching off by mistake.
W ""
W "===== Ohman --lamps ====="
if (-not (Test-Path $exe)) {
    W "  Ohman.exe not found beside the tools folder - keep this folder next to Ohman.exe"
} elseif (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Ohman asks for administrator, so a non-elevated parent cannot start it and the error it gives is a
    # page of PowerShell rather than a sentence. Say the one thing that fixes it instead.
    W "  not elevated - run lighttest.cmd rather than the .ps1, it asks for administrator and this section then fills in"
} else {
    try { & $exe --lamps 2>&1 | ForEach-Object { W ("  " + $_) } }
    catch { W "  failed to run: $($_.Exception.Message)" }
}

# 2. Windows Dynamic Lighting. Ohman switches these off to take a keyboard, and the light bar on some
#    machines is a separate entry: if one is still enabled it is Windows painting over us, and if one is
#    disabled and never came back that is Ohman having taken something it should not have.
W ""
W "===== Windows Dynamic Lighting (HKCU\Software\Microsoft\Lighting) ====="
$root = 'HKCU:\Software\Microsoft\Lighting'
if (Test-Path $root) {
    $g = (Get-ItemProperty $root -ErrorAction SilentlyContinue).AmbientLightingEnabled
    W ("  AmbientLightingEnabled (global): " + $(if ($null -eq $g) { "not set (default on)" } else { $g }))
    if (Test-Path "$root\Devices") {
        foreach ($d in Get-ChildItem "$root\Devices" -ErrorAction SilentlyContinue) {
            $v = (Get-ItemProperty $d.PSPath -ErrorAction SilentlyContinue).AmbientLightingEnabled
            W ("  [{0}]  AmbientLightingEnabled={1}" -f $d.PSChildName, $(if ($null -eq $v) { "not set" } else { $v }))
        }
    } else { W "  no Devices subkey" }
} else { W "  no Lighting key - this build of Windows has no Dynamic Lighting" }

# 3. The HID lighting devices themselves, so a device that Windows lists but Ohman did not find shows up
#    as the difference between this section and section 1.
W ""
W "===== HID LampArray devices ====="
try {
    Get-PnpDevice -Class HIDClass -ErrorAction Stop |
        Where-Object { $_.InstanceId -match 'VHF|LampArray|HID_DEVICE_SYSTEM_VHF' -or $_.FriendlyName -match 'Light|Lamp|RGB' } |
        ForEach-Object { W ("  {0,-8} {1}" -f $_.Status, $_.FriendlyName); W ("           {0}" -f $_.InstanceId) }
} catch { W "  could not enumerate: $_" }

# 4. Anything of HP's that also drives these lights. Two owners of the same board have reached opposite
#    results depending on whether Light Studio was installed, so it has to be recorded, not assumed.
W ""
W "===== HP lighting software present ====="
foreach ($p in 'OmenCommandCenterBackground', 'LightStudio', 'HPOmenLightStudio', 'Ohman') {
    $r = Get-Process $p -ErrorAction SilentlyContinue
    W ("  {0,-30} {1}" -f $p, $(if ($r) { "running" } else { "not running" }))
}
try {
    Get-AppxPackage -Name '*OMEN*', '*LightStudio*' -ErrorAction SilentlyContinue |
        ForEach-Object { W ("  installed: {0} {1}" -f $_.Name, $_.Version) }
} catch { }

W ""
W "Done. Paste lighttest.txt into the GitHub issue."
