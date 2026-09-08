# How the OMEN Transcend 14 is controlled, and how we know

Everything below was verified on an HP OMEN Transcend 14 (14-fb0xxx, board 8C58, BIOS F.12, Core Ultra 9
185H + RTX 4070 Laptop) against OMEN Gaming Hub 1101.2608.3.0, on 2026-09-08. Three independent sources
were used for every claim: OGH's own background log on the machine (it logs the bytes it sends), OGH's
decompiled code and embedded data, and two open-source implementations of the same interface, the Linux
`hp-wmi.c` driver and OmenMon. Where only inference is available, it says so.

## 1. The interface

- WMI namespace `root\wmi`, class `hpqBIntM`, methods `hpqBIOSInt0/4/128/1024/4096` (the number is the
  output buffer size). Input is an `hpqBDataIn` instance: `Sign` = ASCII `SECU`, `Command` = `0x20008`,
  `CommandType` = the opcode, `Size`, `hpqBData` = payload. Output: `Data`, `rwReturnCode` (0 = OK),
  `Sign` = `PASS`/`FAIL`. Instances are only visible to administrators.
- Events: class `hpqBEvnt` with `EventID` and `EventData`.
- Linux calls the same thing `HPWMI_GM` with the signature `0x55434553`, which is `SECU` little-endian.

## 2. Verified commands (CommandType under 0x20008)

| Opcode | Payload | Meaning | Evidence |
|---|---|---|---|
| `0x10` | `{0,0,0,0}` → out4 `[0]`=fan count | Fan count. **Also the keep-alive trigger for user-defined thermal and fan state** (see §5) | hp-wmi.c comment; probe |
| `0x1A` | `{FF, mode, byBios, 0}` | Set performance mode. `mode`: `0x30` Balanced/Default, `0x31` Performance, `0x50` Cool. `byBios` = "fan control by BIOS", OGH sets 1 on battery | decompiled `PerformanceControlHelper.SetFanMode`; OGH log `255,49,0,0` / `255,48,1,0`; hp-wmi V1 constants |
| `0x21` / `0x22` | `{cTGP, PPAB, dState, peakTemp}` | GPU power get / set. OGH sends `{0,0,1,75}` low, `{0,1,1,87}` Balanced, `{1,1,1,87}` Performance | OGH log tally; probe read back `00 01 01 57` |
| `0x23` | `{1,0,0,0}` → out4 `[0]` °C | A thermal sensor that reads 34–50 °C: chassis / IR, not the CPU die | probe; OGH's IR thresholds (40/52 °C) match this range |
| `0x26` / `0x27` | `{on}` | Max fan get / set | OGH log `SetMaxFan` → `1`/`0`; probe |
| `0x28` | out128 | System design data. Byte 3 = thermal policy version (1 here), byte 4 bit 0 = software fan control supported, byte 5 = default PL4 (159 W), byte 8 = default concurrent TDP (30 W). This unit: `8C 00 35 01 01 9F 00 03 1E` | probe; OGH caches the same bytes in `HKCU\Software\HP\OMEN Ally\Settings\SystemDesignData` |
| `0x29` | `{PL1, PL2, PL4, concurrent}`, `0xFF` = unchanged | Power limits. Only the 4th byte is used on this SKU (OGH reports PL1/2/4 unsupported). It is OGH's **Smart Performance Gain**: "increases the total power allocated between CPU and GPU, allowing extra power capacity for NVIDIA Dynamic Boost to increase GPU performance" (OGH's own tooltip string). hp-wmi names it `cpu_gpu_concurrent_limit`. Base 30, max 45 (`TppMaxValue` in OGH's platform file) | OGH log `SetConcurrentTdp value=45` → `255,255,255,45`; resource strings |
| `0x2D` | out128 `[0]`,`[1]` | Fan levels, RPM ÷ 100. Max fan reads 59/57 | probe; hp-wmi `fan_data[fan] * 100` |
| `0x2E` | `{fan1, fan2, …}` | Set fan levels. OGH sends a 128-byte buffer. **Level 0 switches a fan off** | OmenMon docs; §5 |
| `0x2F` | out128 | Fan table: `[0]` count, `[1]` entries, then `{fan1, fan2, temp}` triplets | OmenMon struct; probe |
| `0x2C` | out128 `[0]` | Fan types, one nibble each (`0x21` = fan1 CPU, fan2 GPU) | probe |

Return code 5 = unsupported (`0x13`, `0x2A`, `0x35` on this SKU).

## 3. Eco, and why the old note was wrong

OGH's UI enum `PerformanceModeOnUI` has Eco, but its BIOS-level enum
(`Hp.Bridge.Client.SDKs.PerformanceControl.Enums.PerformanceMode`, in `PerformanceControl.dll`) gives Eco the
value **256**, which cannot be a payload byte. The disassembled `SetFanMode` shows the translation for
thermal-policy v1: Default → `0x30`, Performance → `0x31`, Cool → `0x50`, **Eco → `0x30`**. Eco's savings are
software: the Windows power-mode overlay, GPU power `{0,0,1,75}`, and OGH's own governor. An earlier note that
Eco was `0x11` was wrong; `0x11` is OGH's legacy "L5" slider mode.

## 4. Fan control on this platform

OGH keeps the machine in user-defined fan state permanently and drives the fans itself. Its stored curve for
this model (from `profiles.json`):

| CPU °C | 50 | 55 | 60 | 65 | 70 | 75 | 80 | 85 | 90 |
|---|---|---|---|---|---|---|---|---|---|
| CPU fan level | 23 | 23 | 25 | 32 | 39 | 46 | 46 | 46 | 49 |
| GPU fan level | 23 | 25 | 31 | 35 | 46 | 46 | 46 | 46 | 46 |

IR (chassis) sensor: 40 °C → 0, 52 °C → 46. Bounds: lower 18 (35 at 90 °C), upper 57. Smoothing λ = 0.1.
OGH writes a level pair every 10–20 s and calls a four-byte-zero query every ~15 s (its heartbeat constant is
30 s). Note that OGH's CPU temperature (from its own driver) reads lower than the ACPI zone `\_TZ.TZ01`: with
TZ01 at 77 °C, OGH's levels correspond to about 63 °C on its table.

## 5. The incident

Version 2.0 of this app implemented "Auto" as *write levels {0,0}* and ran a heartbeat that sent `0x10` every
45 s in every mode, after stopping OGH. The Linux driver's comment on `0x10`: "Calling this function also
enables and/or maintains the laptop in user defined thermal and fan states, instead of using a fallback state.
After a 120 seconds timeout however, the laptop goes back to its fallback state." Its Auto mode sends the
query once and then **cancels** the keep-alive. OmenMon's documentation: "the minimum value will be
interpreted as a 0, i.e. switching the fan off." So the firmware was held, indefinitely, in user-defined fan
state with both fans at 0. Windows logged "the system was hibernated due to a critical thermal event"
(Kernel-Power 88) 20 minutes after Auto was selected.

**Measured on 2026-09-08, 20:25–20:34, with OGH stopped:**

| Step | Fan levels read back (0x2D) |
|---|---|
| Baseline, OGH driving | 35 / 33 (3500 / 3300 rpm), chassis 45 °C |
| Write `{0,0}` with the 0x10 trigger, trigger repeated every 20 s for 60 s | **0 / 0 for the whole period and 40 s beyond** |
| Max fan on (0x27) | 43 / 43 five seconds later, rising |
| No further commands: max fan expires | fans read **0 / 0 again** (the stale user-defined level) at +5:40 and +5:55 after the last trigger |
| Firmware curve resumes on its own | 29 / 27 at +6:05 |

So: level 0 is off; the keep-alive holds it; and when user-defined state expires the firmware first re-applies
the last written level before its own curve returns. "Hand the fans back to the firmware" is therefore not
a safe Auto either. Since 2.1 the app drives the fans explicitly in every mode: Auto steps OGH's own curve for
this model every 5 s (floor 1800 rpm, ceiling 5700) with the keep-alive, Manual holds the slider levels
(floor 1800), Max holds the flag; nothing can write below the floor. A thermal guard forces max fan at
CPU ≥ 90 °C, chassis ≥ 56 °C, or stalled fans while warm, and unknown boards are read-only.

## 6. The OMEN key

Fn+F12 raises `hpqBEvnt` EventID 29 / EventData 8613 (sometimes twice per press; the app debounces 400 ms).
A second key produces 29/8615. `131073/0` is a power-source notification. OGH's key handler
(`OmenCommandCenterBackground`) is launched at logon by HP's `OmenInstallMonitor` scheduled tasks, not by its
own (disabled) startup task; Ohman disables those tasks while it owns the key and re-enables them when told.

## 7. How the OGH internals were read

- Logs: `%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\HPOMENBG_<date>.log`
  record `[ExecuteBiosWmiCommandThruDriver] inputData=…` for every call, and the helper names around them.
- The package folder under `C:\Program Files\WindowsApps\` is readable. Assemblies load with
  `Assembly.LoadFrom` in PowerShell for reflection (enums, resource strings, embedded per-model JSON under
  `HP.Omen.Core.Common.PowerControl.JSON.*`), and method IL can be read with `GetMethodBody().GetILAsByteArray()`
  and disassembled with a small script.
- Cross-checks: Linux `drivers/platform/x86/hp/hp-wmi.c` (`enum hp_wmi_gm_commandtype`, thermal-profile
  constants, keep-alive comments) and OmenMon (`Hardware/BiosCtl.cs`, `BiosData.cs`, documentation).

## 8. Open questions

- Whether the mode command `0x1A` alone (without `0x10`) keeps the firmware in user-defined state, and whether
  performance mode reverts after 120 s without the keep-alive. `fantest` phase D.
- Whether the firmware keeps running its own curve when only the keep-alive is sent and no levels were
  written. `fantest` phase E. If yes, Auto can keep the mode alive safely; if no, Auto should become a
  software curve like OGH's.
- The exact sensor behind `0x23` (IR/chassis) and what Windows' `\_TZ.TZ01` zone measures relative to the CPU
  package.
