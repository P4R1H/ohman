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
- Return code 5 is `HPWMI_RET_INVALID_PARAMETERS` in `hp-wmi.c`, and OmenMon reads it as an insufficient
  buffer: it is about the request, not the SKU. Seen on `0x13`, `0x2A`, `0x35`. Return code 3 is
  `HPWMI_RET_UNKNOWN_COMMAND`, which a zero-length input buffer can also provoke on a command the firmware
  does support. Codes 1, 4, 6 and 46 are undocumented; 6 from the graphics write is accepted-pending-restart,
  since OGH gets the same code on the same board and the mode changes at the next boot.

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
| `0x2F` | out128 | Fan table: `[0]` fan count, `[1]` entry count, then `{fan1, fan2, noise dB}` triplets. The third byte is noise, not temperature (kernel `victus_s_fan_table_entry`). The first row is the firmware's own minimum, and on some boards it is `0/0` | measured; corrected against `hp-wmi.c` |

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

Every mode drives the fans explicitly: Auto steps OGH's own curve for this model every 5 s (ceiling 5700 rpm,
and 0 allowed, since HP's own tables use it) with the `0x10` query in front of every write, Manual holds the slider levels, Max
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

## 6. Graphics mode (MUX / iGPU only)

Legacy mailbox, not the performance one: command `1` (read BIOS config) / `2` (write), CommandType `0x52`. Read
returns out4 `[0] & 0x7F`: 0 Hybrid, 1 Discrete, 2 Optimus, 3 iGPU only (OGH `GraphicsSwitcherMode`). Write sends
`{mode, 0, 0, 0}`; OGH sets bit 7 ("no reboot") only on platforms from its cycle "26C1" on, everything earlier,
including the Transcend 14, applies the change at the next restart. Which modes a model offers is system-design
data byte 7 as a bitmask: 1 iGPU only, 2 Hybrid, 4 Discrete, 8 Advanced Optimus. This unit reports `0x03`.

## 7. Keyboard lighting

Same mailbox, second command id. Layout from OGH's own lighting module (`HP.Omen.Background.FourZone`,
`HP.Omen.Core.Model.Device`), matched against OmenMon and read back on this machine.

| Command / type | Payload | Meaning |
|---|---|---|
| `0x20008` / `0x2B` | in size 0, out4 `[0]` | Keyboard type: 0 none, 1 four zones with numpad, 2 four zones without, 3 per-key RGB, 4 one zone with numpad, 5 one zone without |
| `0x20009` / `0x01` | out128 `[0]` bit 0 | Lighting supported (legacy models only; OGH uses it for Pirates/Marlins/Gamora/Milos/Santorini) |
| `0x20009` / `0x02` | out128 | Colour table. Zone i = bytes `25+3i .. 27+3i` (R, G, B). Bytes 0..24 are left as read |
| `0x20009` / `0x03` | 128 bytes in, out4 | Write the colour table (read-modify-write, as OGH does) |
| `0x20009` / `0x04` | out128 `[0]` | Backlight byte: bit 7 = on, low bits = level. OGH writes `0xE4` (on) / `0x64` (off) and nothing else |
| `0x20009` / `0x05` | `{byte,0,0,0}`, out4 | Set the backlight byte |
| `0x20009` / `0x06` / `0x07` | out128 / in | LED animation table (OmenMon: writing it "takes no effect") |

- `hpqBEvnt` EventID 13 is the Fn backlight key; data 0 = off. OGH mirrors it into its own state.
- OGH factory colours: `0F84FA`, `710FFA`, `F9350F`, `FAAC0F` (zones 0..3); NvStudio SKUs white.
- Transcend 14 (8C58): four zones. HP's `OMENLighting.sys` (a KMDF lower filter on the virtual HID framework, talking to
  `ACPI\PNP0C14`, i.e. the same WMI mailbox) publishes the keyboard to Windows as a LampArray with four lamps
  (VID 0x0461 PID 0 = "FourZone" in OGH's own device table) so Windows Dynamic Lighting can paint it.
- Arbitration: Windows owns the keyboard while `HKCU\Software\Microsoft\Lighting\Devices\<VHF id>\AmbientLightingEnabled`
  is 1 (and the global value). OGH flips that value to take or return control; Ohman does the same and only for HP's
  virtual device, never for external LampArray peripherals.
- Measured 2026-09-12 on this machine: keyboard type byte = 2; writing `FF0000 / 00FF00 / 0000FF / FFFFFF` through
  `0x20009/0x03` read back byte-exact and lit the four bands left to right in that order; the backlight byte read `00`
  while Windows was driving the keyboard and `E4` after the app wrote it, so bit 7 is not a reliable "is lit" probe
  when Dynamic Lighting owns the device.
- The WinRT `LampArray` path reported `IsAvailable = false` for a background desktop app even with ambient off, so
  the app writes the firmware table directly. Brightness is applied by scaling the colours; effects are frames
  written every 120 ms (OGH animates the same way, on the CPU, at about 15 frames per second).

### 7a. Per-key keyboards (type 3): the firmware interface is inert

Keyboard type `3` reports "per-key RGB" and keeps answering `0x20009/0x02..0x05`, but **nothing it is told
reaches the hardware**. Reported independently by owners of an OMEN 17-ck, board `88FE` and a Transcend 16,
and stated by OmenMon's maintainer: the whole four-zone colour and backlight interface is there, and it does
not control anything. It could not work anyway, 128 bytes cannot address 176 LEDs.

Ohman therefore treats type 3 as `ILighting.Inert`: the keyboard is drawn, our own modes are greyed out, and
Windows Dynamic Lighting (which talks to the keyboard directly) is offered instead.

The real interface is the keyboard's own USB HID device. Two pieces of it are known:

**2025 boards (OMEN MAX class), documented, two independent implementations that agree byte for byte.**
Device `VID_0D62 PID_54BF`, Darfon "HP Gaming Keyboard II"; the lighting lives on interface `MI_03`, which has
a 65-byte output report. Report byte 0 is the report id and is stripped, so the 64 wire bytes are
`[cmd][index][bLen lo][bLen hi][60-byte payload]` (the MCU ignores `bLen`). Commands: `0x09 {01}` select the
static map, `0x05`/`0x06`/`0x07` write the R/G/B channels as three 60-byte pages each, `0x0A {AC 53}` commits
to flash, `0x03` installs one of twelve device-side animations, `0x83` reads the effect record back. Every
frame is acked `EC AC` (parsed) or `EC FA` (refused), an ack does not mean anything lit. 180 slots, 176 real
LEDs, 176–179 padding; index order is contiguous physical raster order with the numpad interleaved per row.
`0x0A` is a real flash write and must never run per animation frame. Sources: `theantipopau/omencore`
(`DojoKeyboardMcu.cs`, MIT, from a decompile of OGH's `McuSDK2.dll` plus a USB capture on board `8D87`) and
`arfelious/omen-rgb-linux` (`driver.py` + `data/keys.json`, GPL-3.0). Both were read as evidence; neither was
copied, this file is the protocol, and the code here was written from it. They cross-confirm the numbering: one predicts LEDs 137, 138 and 146 are the `.` key and
the last cell of right shift; the other's independently-built map says exactly that.

The same device also exposes `MI_04` as a **standard HID LampArray** (usage page `0x59`): 120 lamps, each
carrying its key's HID usage, 33 ms minimum interval, no admin rights needed. No readback, no brightness, and
a LampArray picture does not survive the Fn overlay, but it needs no reverse engineering. Windows Dynamic
Lighting contends for the same device, which is the arbitration problem we already solve for four zones.

**Every per-key OMEN before 2025 (17-ck, 16-b, Transcend 16, OMEN 16/17 2023–24): nobody has published a
protocol.** The likely controller is Primax `0461:4E9B` "HP OMEN 16 KBM"; no capture of OMEN Light Studio
driving it exists publicly. That capture is the blocker, and it is what the upstream projects have asked for
and never received.
Superseded for the Primax keyboards by 7b: the protocol is in OGH's own assemblies, no capture needed.


**Do not write the `0x0F / 0x42 / 0x52 / 0x50` command set with `VID 0x03F0`** that circulates in a couple of
projects citing "OpenRGB's HPOmenKeyboard controller". That controller does not exist: OpenRGB has no per-key
HP keyboard driver at all, only a WMI four-zone one that explicitly refuses type 3. Mainline `hp-wmi.c` has no
lighting code of any kind.

**Caution on `0x20009/0x01`.** The row above reads bit 0 of the out128 as "lighting supported". On board
`8D87` that byte was observed as a saturating accumulator (`0x0F, 0x1F, 0x3F, 0x7F, 0xFF`), so bit 0 can read
true on a machine with no controllable lighting. Ohman only consults it for boards whose type byte is not
1–5, and still disables lighting if the colour table then fails to read, but it is a weak signal and not
something to build on. `0x20009/0x04` must be called with a 128-byte output buffer; a smaller one returns
`rwReturnCode 5`.

### 7b. Primax per-key keyboards (OMEN 17 "Cybug" 0461:4E9A, OMEN 16 "Ralph" 0461:4E9B)

Read from OGH 1101.2609.3.0 (`DraxLightingBg` -> `Starmade.NbPerKeyRgbLightingControl` -> `McuSDK2`), confirmed on an
OMEN 17-ck2013nl, board `8BAD`, Italian keyboard, 2026-09-22 (`tools/check-perkey.ps1`, issue #20). It is the same MCU
protocol as the Darfon in 7a; only the interface number, LED count and index map differ. OGH picks it when the BIOS
keyboard type is 3 and the device is Cybug (board ids 88F7 88FE 8A17-8A1A 8BAD 8BB0) or Ralph (88F4-88F6 88FD 8A13-8A16).

- Interface `mi_02`, usage page `FF13` usage `01`, 65/65/0-byte reports. HidP value caps: input and output report
  id **0**. Written with `WriteFile`, answered with a 65-byte input report on the same handle. No LampArray exists
  on these keyboards (the only one on 8BAD was a Logitech driver's), so Windows Dynamic Lighting cannot drive them.
- Packet `[cmd][index][len lo][len hi][60 bytes]`. Reply echoes cmd and index; byte 2 is the reply length, byte 3 a
  status (`0x80` on a SET ack, `0xD0` on a GET); a SET is acked `EC AC` at bytes 4..5. Key events arrive as `EC BD`.
- `80 01` device info on 8BAD: firmware `00 03 21 00` (the USB bcdDevice is 0321), type 1 = keyboard, language byte
  `0x01` although the keyboard is Italian (McuSDK2's enum says 0x0A; OGH's layout loader falls back to the global
  map either way, so the byte is not used), then an ASCII string from byte 24. `83 00` returned 24 bytes (effect
  `0x0F`, the rest 0), not the 36-byte `03` layout, so it is logged, not replayed.
- `09 00 {01}` lighting on, then `05`/`06`/`07` x pages 0..2 (R, G, B; 60 LEDs a page, length 0): every packet acked,
  the whole keyboard lit, with **no flash write**. Single LEDs: 36, 37, 38 = left Shift; 140 = the key right of `]`
  (ù on Italian); 146, 147, 148 = Enter. The Italian board is ANSI-shaped: its `<>` key is in the bottom row where the
  map has the right Fn (163).
- Map: 168 slots (Cybug) / 167 (Ralph) from `PerKey.{Cybug,Ralph}KBKeysGlobalData.json`, slots OGH zeroes from
  `SetNullBytes`. Ohman's copy, key by key in reading order, is in `src/McuKeyboard.cs`.
- Never sent: `0x0A` (store to flash, OGH only on "save") and `0x10` (factory restore / firmware update mode).
  `McuKeyboard.Guard` allows only `80 01`, `80 02`, `83 00`, `09 00 {0|1}` and `05..07` pages 0..2.
- Arbitration: OGH holds the named mutex `omen_device_vid[0461]_pid[4e9a]_interface_string[mi_02]` per exchange;
  Ohman takes the same one per map.

## 8. Generic support for other boards

A board without a verified profile gets one built at run time, the way the Linux driver decides: the thermal-policy
version from system-design byte 3 selects the mode bytes (v1 `0x30/0x31/0x50`, v0 `0x00/0x01/0x02`), the Victus
board lists from `hp-wmi.c` override them (`88F8`, `8A25`: `0x00/0x01/0x03`; the Victus S boards: `0x00/0x01`), the
`force_v0` boards (`8607`, `8746`..`874A`) keep v0 bytes, power gain is offered only when system-design byte 8 is
non-zero, GPU power only when `0x21` answers, and the fan ceiling is raised to the highest level in the firmware's
own fan table when that is above 57. The fan floor, the keep-alive rule and the thermal guard are unchanged.

## 9. Where the OGH internals live

- Logs: `%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\HPOMENBG_<date>.log`
  record `[ExecuteBiosWmiCommandThruDriver] inputData=…` for every call, with the helper names around them.
- The package folder under `C:\Program Files\WindowsApps\` is readable. Assemblies load with
  `Assembly.LoadFrom` in PowerShell for reflection (enums, resource strings, embedded per-model JSON under
  `HP.Omen.Core.Common.PowerControl.JSON.*`); method IL can be read with `GetMethodBody().GetILAsByteArray()`.

## 10. Open questions

- Whether the mode command `0x1A` alone (without `0x10`) keeps the firmware in user-defined state, and whether
  performance mode reverts after 120 s without the query (`fantest` phase D).
- Whether the firmware keeps running its own curve when only the query is sent and no levels were written
  (`fantest` phase E).
- The exact sensor behind `0x23` and what Windows' `\_TZ.TZ01` zone measures relative to the CPU package.
- What launches the OGH main app on Fn+F12 when `OmenCommandCenterBackground` is not running (observed once;
  HP's `OMENKeyboardRemapper` and the `OmenOverlay` tasks are candidates).

## 11. Battery charge limit: not reachable from here

Recorded so nobody spends another evening on it. `MasonDye/OmenXHub` took this apart on an OMEN 16-am0xxx
(BIOS F.12) in August 2026 and published the negative result, which matches what the mailbox looks like from
our side:

- Scanning `hpqBIntM` across commands `0x2000C`–`0x20020`, every cmdType, returns `rc = 0x3`, invalid command.
  That BIOS implements only `0x20008` (system and design data) and `0x20009` (keyboard and lighting), the two
  Ohman already drives. **There is no charge-limit command in the mailbox to find.**
- myHP does not use the mailbox for this. It goes through WinRT, `HP.AppFramework.PowerManagerClient`, whose
  implementation is inside `HP.HPX.dll`, 198 MB of CoreRT AOT with ~340k functions, which ILSpy cannot
  decompile. Activating `BatteryParticulars` from outside the package fails with `E_INVALIDARG`, because the
  class is gated on UWP package identity.
- Writing HP's own feature flags (`HKLM\SOFTWARE\HP\HP App\SysControl\BatteryExtenderMode\Enabled`, and the
  scheduled-charge key) and restarting `HPAppHelperCap` succeeds and changes nothing.

The only path left is writing EC registers directly, which needs a kernel driver to reach port I/O. That is
exactly the thing Ohman does not have and does not want: one executable, no driver, no service. So this is not
a gap in our coverage, it is out of scope by construction, and if a laptop's own vendor app does not offer
the setting, its firmware very likely does not implement it either.

## Measured: what the app itself costs (2026-09-13)

Two findings from profiling Ohman on the Transcend 14, both now handled in code.

**Polling nvidia-smi keeps the discrete GPU awake.** Every `nvidia-smi` invocation wakes the dGPU out of its
idle power state. Ohman used to ask every other sensor tick: every 4 s with the window open, every 10 s with it
hidden. On a hybrid machine that is often enough that the GPU never reaches its deepest idle state, and the log
showed it sitting at 54–55 °C with nothing running. That is several watts inside a shared thermal budget, which
raises the chassis reading and makes the fan curve ask for more. `Sensors` now backs off to one read every two
minutes after three consecutive idle readings (GPU utilisation ≤ 1 % and package under 12 W), and skips the read
entirely when the graphics mode is iGPU-only. A busy GPU is still sampled at the normal rate, because the fan
curve needs it.

Ohman's own CPU cost, measured over 3.7 hours of normal use: 261 s of CPU time, which is 1.95 % of one core, or
about 0.12 % of the whole 16-thread package. That is not enough to change a temperature; the GPU wake is.

**The curve was chasing sensor noise.** The CPU temperature swings several degrees every few seconds at idle. Fed
straight into the curve, the target moved every 5 s tick and the fans hunted audibly between roughly 2600 and
3500 rpm while the machine did nothing. `AutoTick` now follows a smoothed reading (rising temperatures are taken
immediately; falling ones decay at 40 % per tick) and ignores a downward change smaller than two levels. Upward
moves and the 30 s keep-alive are unaffected, so nothing about the safety behaviour changes.

## 12. The driver: PawnIO, the EC and the CPU registers (2026-09-18)

Everything above goes through the WMI mailbox, which is all the firmware offers from user mode. Two things
live behind it that the mailbox does not reach: the embedded controller's own registers and the CPU's
model-specific registers. Both need ring 0, and the only way there that Windows still allows is a signed
driver. Ohman's is [PawnIO](https://pawnio.eu), installed on request from Settings and never otherwise.

**Why PawnIO and not a driver of our own.** Windows blocklists WinRing0 and its relatives (FanControl below
V238 shipped one and was flagged `Trojan:Win32/Vigorf.A` for it). PawnIO is signed, HVCI-compatible, and
executes only modules its author has signed - small scripts with an allow-list each - so the surface a caller
can reach is fixed by the module, not by the caller. LibreHardwareMonitor, FanControl, ZenTimings and OmenCore
all moved to it. The installer is redistributable unmodified (its own text says so); Ohman downloads it from
`namazso/PawnIO.Setup` releases, checks the Authenticode chain and that the signer is `namazso.eu`, and runs
`-install -silent`. Exit 0 is installed, 3010 wants a restart, 183 was already there. The device is opened
with `CreateFile(\?\GLOBALROOT\Device\PawnIO)` and driven with two `DeviceIoControl` codes, load and
execute; no DLL. `third_party\PawnIO.Modules` holds the three signed modules Ohman embeds.

**What the CPU registers give.** Intel: `IA32_TEMPERATURE_TARGET` (0x1A2) bits 23:16 are TjMax;
`IA32_PACKAGE_THERM_STATUS` (0x1B1) bits 22:16 the distance below it, bit 31 says the reading is valid, and
bits 0 / 2 / 10 say thermal, PROCHOT and power-limit throttling right now; `IA32_THERM_STATUS` (0x19C) bit 12
is the current limit. `MSR_PKG_POWER_LIMIT` (0x610) carries PL1 in bits 14:0 (enable bit 15) and PL2 in bits
46:32 (enable bit 47), in the unit `MSR_RAPL_POWER_UNIT` (0x606) bits 3:0 declare; bit 63 is the firmware
lock. `MSR_PKG_ENERGY_STATUS` (0x611) is a 32-bit energy counter. AMD: the die temperature is not an MSR but
SMN register `THM_TCON_CUR_TMP` (0x59800), bits 31:21 in eighths of a degree with bit 19 selecting the
-49 range; energy is `MSR_PKG_ENERGY_STAT` (0xC001029B) in the unit of `MSR_PWR_UNIT` (0xC0010299). All
reads. The `IntelMSR` module would allow writing PL1/PL2; that waits for a tester and a slider.

**The EC map.** OmenMon's (`Hardware/EcData.cs`), which agrees with omen-fan's probes (`docs/probes.md`) and
OmenCore's fan code:

| Register | Name | Meaning |
|---|---|---|
| `0x34` / `0x35` | SRP1 / SRP2 | set fan 1 / fan 2, rpm/100 - the mailbox's own unit |
| `0x2C` / `0x2D` | XSS1 / XSS2 | set fan 1 / 2 in percent (not used) |
| `0xB0..0xB3` | RPM1..RPM4 | read rpm, 16-bit little-endian per fan |
| `0x57` / `0xB7` | CPUT / GPTM | CPU and GPU temperature, degrees |
| `0x62` | OMCC | `0x06` = the caller has the fans, `0x00` = the EC has them |
| `0x63` | XFCD | seconds until the EC takes the fans back; the firmware sets `0x78` = 120 |
| `0xEC` | FFFF | max fan (`0x0C` on); omen-fan marks it unreliable, not used yet |
| `0x95` | HPCM | the performance-mode byte, the same value 0x1A writes |
| `0x96` | XBCH | "battery charge level"; nobody has shown it is writable |

`XFCD` is the 120 s expiry §4 describes from the outside: the countdown the 0x10 keep-alive refreshes.
Through the EC Ohman sets it to 255 while it holds the fans and back to 120 when it lets go.

**What the EC is used for, and where.** Reads for the report everywhere a map exists. Writes only on a board
whose mailbox refuses fan levels: `878A` by profile (0x2E answers rc 46 forever), or any board that has just
refused eight writes in a row and has a map. The hold is `OMCC=06`, `SRP1/2=level`, `XFCD=FF`, then `OMCC`
read back - an EC that does not keep `06` there is not one this map fits, and the route goes back to the
mailbox after three such failures. Release is omen-fan's sequence: levels 0, `OMCC=00`, `XFCD=78`. The
mailbox's max-fan flag (0x27) is left in charge of max fan and of the thermal guard: it works on these boards.

**Where the map is trusted: nowhere, until it proves itself.** The first version of this decided from the hex
prefix of the board id, every published map was made on an 84xx–8Bxx board (OmenMon on 8A14, omen-fan on a
16-c0xxx, OmenCore's field report on 8574), so those were in and everything later was out. That is a guess
about what a number near another number means, and it was wrong in both directions: too generous, because it
handed the map to ninety boards nobody had touched, and too mean, because the 2024 Transcend 14 turns out to
follow it exactly.

So the map is now checked on the machine, in `EmbeddedController.Verify`, before a single byte is written
anywhere. Four questions, cheapest first:

- `OMCC` is a two-valued register. If it holds anything but `0x00` or `0x06`, this address is not `OMCC` here,   and that is the register that takes the fans away from the firmware, so it is the one worth checking first.
- `CPUT` has to read like a temperature, and has to agree within 25 °C with the CPU's own die sensor, which the
  driver has already given us.
- `RPM1`/`RPM3` have to read like fan speeds, and have to agree within 25 % with the firmware's own `0x2D`.

Measured on 8C58 (2024 Transcend 14), which the old prefix rule would have refused:

```
ec proof:    fits, CPU 69 C, fans 3232/3218 rpm, control 0x06
ec fan rpm:  3242 / 3227   mailbox 0x2D: 3200 / 3200
ec temps:    CPU 62 C      die 62 C
```

Agreement within 1.3 % on the tachometers and exact on the temperature. `tools\drivertest.cmd` prints those
lines, which is why a contributed `drivertest.txt` is now the evidence a board needs.

The one hard exclusion left is the 2025 OMEN MAX (`8D41`, `8D42`, `8D87`, `8D88`): OmenCore issue #60 reports
that the registers are somewhere else entirely and that writing these addresses corrupts EC state until the
Caps Lock light blinks. There is nothing to prove there and no reason to go looking, so those boards are never
opened at all.

**Why the EC is treated gently.** It also answers the battery, the lid and the keyboard. OmenCore 2.8.6 traced
a "Critical Battery Trigger Met" shutdown on plugged-in laptops to ACPI Event 13 - EC transactions timing out
under a flood of fan writes and battery polls - after which Windows read 0 % and acted on it. So: every wait in
`Ec.cs` is bounded, an identical write within five seconds is skipped, one burst per 5 s tick, five timeouts
in a row and the EC is left alone for ten minutes with the fans back on the mailbox route. The `Global\
Access_EC` mutex is held for every transaction; OmenMon, LibreHardwareMonitor, OmenCore and HWiNFO all take it.

**Not done, on purpose.** Undervolting (the module allows `MSR_OC_MAILBOX`, HP's BIOS locks it on nearly
every board). Keyboard lighting through the EC (`0xB2..0xBE`: OmenCore reports hard crashes on a 17-ck2xxx).
Any EC write on a board without a map.

### Reading the die is an observation that changes it (2026-09-18)

The first driver build read the package sensor immediately after the ACPI thermal-zone performance counters,
because that is where the sensor loop already sat. That is the worst possible moment. Measured at rest on the
reference machine, 8C58, Core Ultra 9 185H, TjMax 110:

| how the package sensor (`0x1B1`) was sampled | median | max | floor |
|---|---|---|---|
| free-running, 25 ms apart (two runs) | 58, 61 | 99, 102 | 57, 54 |
| immediately after one perflib counter read | 68 | 95 | 55 |

The floor is the same either way, and the floor is the truth: this laptop idles at 54–55 °C. The die answers in
under a millisecond and settles over a few hundred, so a perflib round trip, or anything else the machine
happens to be doing, lifts the reading by tens of degrees until it settles, and a reading taken straight
afterwards reports that transient instead of the temperature. A series sampled out of process startup shows it
as a decay curve: `76 70 72 85 91 78 72 75 66 73 70 81 58 72 66 70 61 55 55 54 54 55 55`.

Two things follow, and both are in `Sensors.Loop`. The die is read at the top of the tick, a whole interval
after that thread last did anything. And every consumer, the panel, the fan curve, the thermal guard, follows
the **median of three readings** rather than one, because about one reading a minute still lands inside somebody
else's burst (3 of 64 samples at or above 90 while the floor was 55), and a single one of those was enough to
move the fans and to engage the thermal guard. The guard could then never release: release needs sixty
consecutive cool seconds and the next spike always came first. It engaged three times in one evening on an idle
laptop, the third time at `cpu=102 chassis=45 fans=34/34`.

The guard now also needs two consecutive ten-second ticks rather than one. That is a second layer, not the fix.
Twenty seconds is far inside the time the chips take to come to any harm and they throttle themselves long
before it; and what the guard is actually for, a chassis heating up, fans that have stopped, does not happen
in ten seconds either.

For the record: LibreHardwareMonitor and OmenCore read the same registers with the same decode (OmenCore's own
comment is "package temperature (0x1B1), more stable than per-core"), and neither smooths what it displays.
Neither of them reads the sensor immediately after a WMI call either.

### The EC fan register is not the same one on every board (2026-09-18)

NoteBook FanControl keeps a per-model, community-tested EC map for hundreds of laptops, which is the largest
independent body of evidence about these controllers that exists. Its HP configs (`nbfc-linux`, decimal in the
files, hex here):

| Config | writes | reads | manual control |
|---|---|---|---|
| HP Omen 16 n0xxx | `0x34` / `0x35`, rpm/100 | `0x2E` / `0x2F` | sets `0x62` = `6` |
| HP OMEN Laptop 15-en0xxx | `0x2C` / `0x2D`, **percent** | `0x2E` / `0x2F` | none |
| HP Victus 16-e0xxx | `0x2C` / `0x2D`, **percent** | `0xB1` / `0xB3` | none |

This corroborates the map above, `0x62` = `0x06` for manual control, `0x34`/`0x35` in rpm/100, `0x2E`/`0x2F`
and `0xB0`–`0xB3` for reading, and it also says something the map does not: **which pair actually drives the
fans is a per-model fact, not a per-generation one.** The Omen 16 uses the rpm registers; the 15-en0xxx, which
is the closest relative of `878A` in the table (same 2020 OMEN generation), uses the percent registers.

That matters because `878A` is the one board the EC write path exists for, and no NBFC config exists for the
15-ek0xxx chassis at all, so the nearest evidence we have suggests our map's `0x34`/`0x35` is the wrong pair
there.

`EmbeddedController.Verify` cannot tell the two apart. It checks the control register, the temperature and the
tachometers, and all three are identical in both variants: a 15-ek would pass verification and then be written
on registers that do nothing. Verifying the *write* register means writing to it and watching a tachometer
move, which is a different and more careful thing than reading four registers, and it is the only thing that
can actually answer the question.

So the read half of the driver is evidence-backed everywhere and the write half is not, on the one board it was
written for. The registers themselves are not the unknown; which of them is live on a given chassis is.

**Which pair is live, measured (2026-09-18, 8C58).** `tools\drivertest.cmd` now writes each candidate pair and
watches the tachometer, because reading cannot tell them apart:

```
fans at rest:              4329 rpm
0x34/0x35 = 50 (rpm/100)   5022 rpm, +693   <-- this pair drives the fans on this board
0x2C/0x2D = 80%            3929 rpm, -400   no change
```

5022 against a request of 50, which is 5000, so the unit is confirmed as well as the register. The percent pair
accepts the write and does nothing, exactly as the corpus suggested it might. That is the fact a new board has
to supply before its fans are driven, and it takes fifteen seconds to collect.
