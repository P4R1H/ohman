# OMEN Transcend 14 firmware interface

Platform: HP OMEN Transcend 14 (14-fb0xxx, board 8C58, BIOS F.12, Core Ultra 9 185H, RTX 4070 Laptop).
Reference software: OMEN Gaming Hub 1101.2608.3.0. Every value below was either read from OGH's own
background log on this machine (it logs the bytes it sends), taken from OGH's decompiled code and embedded
data, or measured directly through the interface. Inferred values are marked as such.

## 1. The interface

- WMI namespace `root\wmi`, class `hpqBIntM`, methods `hpqBIOSInt0/4/128/1024/4096` (the number is the
  output buffer size). Input is an `hpqBDataIn` instance: `Sign` = ASCII `SECU`, `Command` = `0x20008`,
  `CommandType` = the opcode, `Size`, `hpqBData` = payload. Output: `Data`, `rwReturnCode` (0 = OK),
  `Sign` = `PASS`/`FAIL`. Instances are only visible to administrators.
- Events: class `hpqBEvnt` with `EventID` and `EventData`.
- Return code 5 = unsupported on this SKU (`0x13`, `0x2A`, `0x35`).

## 2. Commands (CommandType under 0x20008)

| Opcode | Payload | Meaning | Source |
|---|---|---|---|
| `0x10` | `{0,0,0,0}` → out4 `[0]` = fan count | Fan count. Also arms and refreshes the firmware's user-defined thermal/fan state (§4) | measured |
| `0x1A` | `{FF, mode, byBios, 0}` | Set performance mode. `mode`: `0x30` Balanced/Default, `0x31` Performance, `0x50` Cool. `byBios` = "fan control by BIOS", set to 1 on battery | decompiled `PerformanceControlHelper.SetFanMode`; OGH log `255,49,0,0` / `255,48,1,0` |
| `0x21` / `0x22` | `{cTGP, PPAB, dState, peakTemp}` | GPU power get / set. OGH sends `{0,0,1,75}` for Eco, `{0,1,1,87}` for Balanced, `{1,1,1,87}` for Performance | OGH log; read-back `00 01 01 57` |
| `0x23` | `{1,0,0,0}` → out4 `[0]` °C | Chassis / IR sensor, reads 34–50 °C; not the CPU die | measured; OGH's IR thresholds (40/52 °C) match this range |
| `0x26` / `0x27` | `{on}` | Max fan get / set | OGH log `SetMaxFan` → `1` / `0`; measured |
| `0x28` | out128 | System design data. Byte 3 = thermal policy version (1), byte 4 bit 0 = software fan control supported, byte 5 = default PL4 (159 W), byte 8 = default concurrent TDP (30 W). This unit: `8C 00 35 01 01 9F 00 03 1E` | measured; OGH caches the same bytes in `HKCU\Software\HP\OMEN Ally\Settings\SystemDesignData` |
| `0x29` | `{PL1, PL2, PL4, concurrent}`, `0xFF` = unchanged | Power limits. Only the 4th byte is used on this SKU (OGH reports PL1/2/4 unsupported). It is OGH's **Smart Performance Gain**: "increases the total power allocated between CPU and GPU, allowing extra power capacity for NVIDIA Dynamic Boost to increase GPU performance" (OGH tooltip string). Base 30, max 45 (`TppMaxValue` in OGH's platform file) | OGH log `SetConcurrentTdp value=45` → `255,255,255,45`; resource strings |
| `0x2C` | out128 `[0]` | Fan types, one nibble each (`0x21` = fan 1 CPU, fan 2 GPU) | measured |
| `0x2D` | out128 `[0]`, `[1]` | Fan levels, RPM ÷ 100. Max fan reads 59 / 57 | measured |
| `0x2E` | `{fan1, fan2, …}` (128 bytes) | Set fan levels. Level 0 switches the fan off (§4) | OGH log; measured |
| `0x2F` | out128 | Fan table: `[0]` fan count, `[1]` entry count, then `{fan1, fan2, temp}` triplets | measured |

## 3. Eco

OGH's UI enum `PerformanceModeOnUI` has Eco, but its BIOS-level enum
(`Hp.Bridge.Client.SDKs.PerformanceControl.Enums.PerformanceMode`, in `PerformanceControl.dll`) gives Eco the
value 256, which cannot be a payload byte. The disassembled `SetFanMode` translation for thermal-policy v1
is Default → `0x30`, Performance → `0x31`, Cool → `0x50`, Eco → `0x30`. Eco's savings are software: the
Windows power-mode overlay, GPU power `{0,0,1,75}`, and OGH's own governor. `0x11` is OGH's legacy "L5"
slider mode and is not sent on this platform.

## 4. Fan control

### OGH's behaviour

OGH keeps the machine in user-defined fan state permanently and drives the fans itself. Its stored curve for
this model (from the embedded `profiles.json`):

| CPU °C | 50 | 55 | 60 | 65 | 70 | 75 | 80 | 85 | 90 |
|---|---|---|---|---|---|---|---|---|---|
| CPU fan level | 23 | 23 | 25 | 32 | 39 | 46 | 46 | 46 | 49 |
| GPU fan level | 23 | 25 | 31 | 35 | 46 | 46 | 46 | 46 | 46 |

IR (chassis) sensor: 40 °C → 0, 52 °C → 46. Bounds: lower 18 (35 at 90 °C), upper 57. Smoothing λ = 0.1.
OGH writes a level pair every 10–20 s and sends the `0x10` query every ~15 s (its heartbeat constant is
30 s). OGH's CPU temperature (from its own driver) reads lower than the ACPI zone `\_TZ.TZ01`: with TZ01 at
77 °C, OGH's levels correspond to about 63 °C on its table.

### Firmware semantics, measured 2026-09-08 with OGH stopped

| Step | Fan levels read back (0x2D) |
|---|---|
| Baseline, OGH driving | 35 / 33 (3500 / 3300 rpm), chassis 45 °C |
| Write `{0,0}` with the `0x10` query, query repeated every 20 s for 60 s | 0 / 0 for the whole period and 40 s beyond |
| Max fan on (0x27) | 43 / 43 five seconds later, rising |
| No further commands: max fan expires | 0 / 0 again (the stale user-defined level) at +5:40 and +5:55 after the last query |
| Firmware curve resumes on its own | 29 / 27 at +6:05 |

Consequences:

- Level 0 is off, not "automatic".
- The `0x10` query arms user-defined fan state and refreshes it; the state expires about 120 s after the
  last query.
- When user-defined state expires, the firmware first re-applies the last written level pair before its own
  curve resumes. Leaving a low level behind and stopping the queries is therefore not safe either.
- Windows logs a critical thermal event (Kernel-Power 88, hibernation) when the fans are held at 0 under load.

### What Ohman does with this

Every mode drives the fans explicitly: Auto steps OGH's own curve for this model every 5 s (floor 1800 rpm,
ceiling 5700) with the `0x10` query in front of every write, Manual holds the slider levels (floor 1800), Max
holds the max-fan flag. The hardware layer refuses any level below 18. A thermal guard forces max fan at
CPU ≥ 90 °C, chassis ≥ 56 °C, or stalled fans while warm. Unknown boards are read-only, and the `0x10`
query is never sent without a fan write behind it.

## 5. The OMEN key

Fn+F12 raises `hpqBEvnt` EventID 29 / EventData 8613 (sometimes twice per press; the app debounces 400 ms).
A second key produces 29/8615. `131073/0` is a power-source notification. Modifier keys are not reported while
Fn is held (Shift+Fn+F12 arrives as a plain event), and Windows reserves F12 for the debugger
(`AeDebug\UserDebuggerHotKey` = 0), so `RegisterHotKey` accepts Shift+F12 but the press never arrives. OGH's key handler
(`OmenCommandCenterBackground`) is launched at logon by HP's `OmenInstallMonitor` scheduled tasks, not by its
own (disabled) startup task; the OGH main app also starts it. Ohman stops the process and disables those
tasks while it owns the key, and re-enables them when told.

## 6. Where the OGH internals live

- Logs: `%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\HPOMENBG_<date>.log`
  record `[ExecuteBiosWmiCommandThruDriver] inputData=…` for every call, with the helper names around them.
- The package folder under `C:\Program Files\WindowsApps\` is readable. Assemblies load with
  `Assembly.LoadFrom` in PowerShell for reflection (enums, resource strings, embedded per-model JSON under
  `HP.Omen.Core.Common.PowerControl.JSON.*`); method IL can be read with `GetMethodBody().GetILAsByteArray()`.

## 7. Open questions

- Whether the mode command `0x1A` alone (without `0x10`) keeps the firmware in user-defined state, and whether
  performance mode reverts after 120 s without the query (`fantest` phase D).
- Whether the firmware keeps running its own curve when only the query is sent and no levels were written
  (`fantest` phase E).
- The exact sensor behind `0x23` and what Windows' `\_TZ.TZ01` zone measures relative to the CPU package.
- What launches the OGH main app on Fn+F12 when `OmenCommandCenterBackground` is not running (observed once;
  HP's `OMENKeyboardRemapper` and the `OmenOverlay` tasks are candidates).
