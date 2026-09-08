# Bounded experiment: what do fan level 0 and the 0x10 keep-alive query really do on this firmware?
# Run ONLY when the laptop is cool and idle (CPU < 65 C). Needs administrator rights (fantest.cmd handles that).
# Every step reads fan RPM (0x2D), the BIOS chassis sensor (0x23) and the ACPI CPU zone, and ABORTS to max fan
# the moment CPU >= 85 C or chassis >= 55 C. Total runtime about 6 minutes. Results go to fantest.txt.
$ErrorActionPreference = 'Continue'
$probe = Join-Path $PSScriptRoot 'omenprobe.exe'
$out = Join-Path $PSScriptRoot 'fantest.txt'
function P([string]$a) { [string]::Join(' ', @(& $probe $a.Split(' '))) }
function Hex2([string]$l, [int]$i) { $m = [regex]::Match($l, 'data=\[([0-9A-F]{2}) ([0-9A-F]{2})'); if ($m.Success) { [int][Convert]::ToInt32($m.Groups[$i].Value, 16) } else { -1 } }
function Fans { $l = P 'call 2D 128 00 00 00 00'; $a = (Hex2 $l 1); $b = (Hex2 $l 2); @(($a * 100), ($b * 100)) }   # parentheses matter: ',' binds tighter than '*'
function Chassis { $l = P 'call 23 4 01 00 00 00'; $m = [regex]::Match($l, 'data=\[([0-9A-F]{2})'); if ($m.Success) { [int][Convert]::ToInt32($m.Groups[1].Value, 16) } else { -1 } }
function CpuTemp { try { $c = (Get-Counter '\Thermal Zone Information(*)\Temperature' -ErrorAction Stop).CounterSamples | Sort-Object InstanceName | Select-Object -First 1; [math]::Round($c.CookedValue - 273.15) } catch { -1 } }
function Trigger { [void](P 'call 10 4 00 00 00 00') }            # the keep-alive / user-define trigger
function MaxFan($on) { [void](P ("call 27 0 " + ($(if ($on) {'01'} else {'00'})))) }
function Levels($a, $b) { [void](P ("callpad 2E 0 128 {0:X2} {1:X2}" -f $a, $b)) }
$script:aborted = $false
function Row($phase) {
    $f = Fans; $c = Chassis; $t = CpuTemp
    $line = "{0:HH:mm:ss}  {1,-34} fans {2,5} / {3,5} rpm   chassis {4,3} C   cpu {5,3} C" -f (Get-Date), $phase, $f[0], $f[1], $c, $t
    $line; $line | Out-File $out -Append -Encoding utf8
    if ($t -ge 85 -or $c -ge 55) { "ABORT: too hot -> max fan"; "ABORT too hot" | Out-File $out -Append; MaxFan $true; $script:aborted = $true }
}
function Wait($sec, $phase, $every, [scriptblock]$each) {
    $end = (Get-Date).AddSeconds($sec); $n = 0
    while ((Get-Date) -lt $end -and -not $script:aborted) { Start-Sleep -Seconds $every; $n += $every; if ($each) { & $each $n }; Row $phase }
}

"fantest $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Out-File $out -Append -Encoding utf8
"This test stops OMEN Gaming Hub's background process so it cannot interfere. It comes back at next logon."
Get-Process OmenCommandCenterBackground, OmenLite, Vane -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); "stopped $($_.ProcessName)" } catch { "could not stop $($_.ProcessName): $_" } }
$t0 = CpuTemp; if ($t0 -gt 70) { "CPU is $t0 C - too warm to start. Let it cool below 65 C and run again."; exit 1 }

"`n--- Phase A (30 s): baseline, nothing sent. Fans should sit on the firmware curve. ---"
Wait 30 'A baseline' 10 $null

"`n--- Phase B (60 s): trigger + max off + levels {0,0}, trigger repeated every 20 s. If RPM falls to 0 the hypothesis is confirmed. ---"
if (-not $script:aborted) { Trigger; MaxFan $false; Levels 0 0; Row 'B set {0,0} + trigger' }
Wait 60 'B holding {0,0}' 10 { param($n) if ($n % 20 -eq 0) { Trigger; "  (trigger sent)" } }

"`n--- Phase C (150 s): no more triggers. The firmware should fall back to its own curve within ~120 s. ---"
Wait 150 'C no trigger' 15 $null

"`n--- Phase D (90 s): mode 0x31 re-sent every 30 s WITHOUT the trigger. Do the fans stay on the curve? ---"
Wait 90 'D mode only' 15 { param($n) if ($n % 30 -eq 0) { [void](P 'call 1A 0 FF 31 00 00'); "  (mode 0x31 sent)" } }

"`n--- Phase E (60 s): trigger every 20 s but NO fan levels ever written since fallback. Does the EC keep following its curve? ---"
Wait 60 'E trigger only' 10 { param($n) if ($n % 20 -eq 0) { Trigger; "  (trigger sent)" } }

"`n--- Restore: max fan off, levels released, no triggers -> firmware curve within 2 min. ---"
MaxFan $false
Row 'end'
"done. Results in $out"
