<p align="center"><img src="docs/logo.webp" width="120" alt=""></p>
<h1 align="center">Ohman</h1>
<p align="center">OMEN Gaming Hub's performance controls, without OMEN Gaming Hub.</p>
<p align="center"><a href="https://github.com/P4R1H/Ohman/releases/latest/download/Ohman.exe"><img src="https://img.shields.io/badge/Download%20for%20Windows-Ohman.exe-5B8DEF?style=for-the-badge" alt="Download Ohman.exe"></a><br><a href="https://p4r1h.github.io/ohman/">p4r1h.github.io/ohman</a></p>

Don't you love paying $2,500 for a laptop and still having ads pushed down your throat by mandatory
software with no alternative? Ohman is the alternative. One executable, no services, no drivers, no account,
no ads. Same firmware interface as OMEN Gaming Hub, same bytes, nothing else.

**Verified on the HP OMEN Transcend 14 (2024, board 8C58).** Every other OMEN and Victus laptop runs in generic
mode: the mode bytes the Linux driver documents for its firmware generation, fan control with the same floor
and thermal guard, and power or GPU controls only where the firmware answers. A banner asks you to report
back so the board can be marked verified; see [Adding your laptop](#adding-your-laptop).

Ohman interacts with the BIOS through the same commands OGH uses. Use at your own risk.

<p align="center"><img src="docs/screenshot.png" width="440" alt="The Ohman panel"></p>

## Features

| | |
|---|---|
| **Modes** | Eco · Balanced · Performance, one tap or Fn+F12. Eco also sets the Windows power mode and GPU base power, as OGH does. |
| **Fans** | Auto (OGH's own curve for this model, 1800 to 5700 rpm), Max, or Manual per fan. |
| **Power gain** | OGH's "Smart Performance Gain": +0 to +15 W on the CPU+GPU budget that NVIDIA Dynamic Boost draws from. |
| **GPU power** | Base · Boost · Max, or follow the mode. |
| **Per mode** | Fan, power gain and GPU choices are remembered per mode. |
| **Graphics** | Hybrid, Discrete or iGPU-only, whichever the firmware offers, with the restart it needs. |
| **Display** | Refresh rate (60 Hz or the panel's maximum, lowest on battery if you like), display off from the tray. |
| **Live** | CPU and GPU temperature, fan speeds, load, clocks, GPU watts, battery. |
| **OMEN key** | Fn+F12 opens the panel (or cycles modes, or toggles max fan). Shift+F11 cycles modes. OGH's key handler is stopped, reversibly. |
| **Safety** | A thermal guard forces max fan on a hot CPU, a hot chassis or stalled fans. Fans are never set below 1800 rpm. |
| **Lighting** | A live keyboard in the panel; click it and an editor slides open beside it: pick zones on the keyboard, a proper colour picker, presets, brightness, Breathe / Cycle / Wave effects, or hand the keyboard to Windows Dynamic Lighting. One-zone and four-zone keyboards; per-key editing is next. |
| **Extras** | CPU temperature on the tray icon, hotkeys (Shift+F11 cycles modes, Ctrl+Alt+E/B/P/M/O), the OMEN key can run any command, starts with Windows without a UAC prompt (on by default, one switch to turn off), Eco on battery, on-screen flash on key presses. |

Settings live in `ohman.state`, everything the app does goes to `ohman.log`.

<details>
<summary>Keyboard editor</summary>
<p align="center"><img src="docs/keyboard.png" width="720" alt="The keyboard editor open beside the panel"></p>
</details>
<details>
<summary>Settings panel</summary>
<p align="center"><img src="docs/settings.png" width="440" alt="Settings panel"></p>
</details>

## Install

Grab `Ohman.exe` from [Releases](../../releases), or build it with the compiler that ships inside Windows:

```
build.cmd
```

Run it (it asks for administrator rights once, the firmware interface needs them), then turn on
**Settings > Start with Windows**. `preview\Ohman.exe` is the same UI on simulated hardware, no admin needed.

## How it works

HP exposes a BIOS mailbox as the WMI class `hpqBIntM`. Ohman uses the commands OGH uses: performance mode
(`0x1A`), max fan (`0x27`), fan levels (`0x2E`), CPU+GPU power budget (`0x29`), GPU power (`0x22`), keyboard
lighting (`0x20009`), plus read-only queries. The OMEN key arrives as a WMI event (`hpqBEvnt`). The bytes and the measured firmware
behaviour are in [docs/research.md](docs/research.md).

## Laptops

Three tiers. Verified means somebody ran the checklist on that machine. Listed means the board id is in the Linux
HP driver's tables, so the mode bytes for its firmware generation are known and generic mode uses them. Anything
else gets generic mode when the firmware reports a known thermal-policy version, and read-only when it does not.

| Model | Board ids | Status |
|---|---|---|
| OMEN Transcend 14 (2024) | `8C58` | Verified on the machine |
| OMEN Transcend 14 (2024, other SKU) | `8E41` | Generic, board known to the Linux HP driver |
| OMEN 15 (2019, 15-dc / 15-dh) | `8574, 8600` | Generic, board known to the Linux HP driver |
| OMEN 17 (2019, 17-cb0) | `8603` | Generic, board known to the Linux HP driver |
| OMEN 15 (2020) | `8A15` | Generic, board known to the Linux HP driver |
| OMEN 15 / 17 (2021) | `8BAD` | Generic, board known to the Linux HP driver |
| OMEN 16 / 17 (2021 to 2022) | `8A42, 8A43` | Generic, board known to the Linux HP driver |
| Victus 16 (2021 to 2023) | `88F8, 8A25` | Generic, board known to the Linux HP driver |
| Victus 16 S / R (2023 to 2024) | `8A3D, 8B2F, 8BBE, 8BD4, 8BD5, 8C99, 8C9C` | Generic, board known to the Linux HP driver |
| Other OMEN 15 / 17 boards in the driver's table (2018 to 2021 generations) | `84DA to 84DC, 8572 to 8575, 8601 to 860A, 8746 to 874A, 8786 to 878C, 87B5, 886B, 886C, 88C8 to 88D2, 88F4 to 88F7, 88FD to 8902, 8912, 8917, 8918, 8949, 894A, 89EB` | Generic, board known to the Linux HP driver |
| OMEN 16 (2023 to 2025) | `8BAA, 8BAB, 8BCA, 8BCD, 8C76 to 8C78, 8D24, 8D26, 8D2F, 8E35` | Generic, from the firmware's own answers |
| OMEN MAX 16 (2025) | `8D41, 8D87` | Generic, from the firmware's own answers |
| OMEN Transcend 16, OMEN 17 (2025) | `8BB3, 8C3B, 8C4D, 8E10` | Generic, from the firmware's own answers |
| Victus 15 | `88D9, 88DA, 8A3E, 8C2F, 8C30, 8C3F, 8D07, 8DCD, 8E5E` | Generic, from the firmware's own answers |

Board id: `Settings > Diagnostics` in Ohman, or `wmic baseboard get product`. Not listed is not unsupported; open a
support issue with `tools\support-info.cmd` and it moves up a tier.

## Adding your laptop

1. Run `tools\support-info.cmd` and press the OMEN key when it asks. It writes `tools\support-info.txt`:
   model, board id, BIOS, system-design data, fan table, key event. No personal data.
2. Open a **New laptop support** issue with that file, what OGH shows on your model, and ideally OGH's
   background log (`%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\`)
   from a session where you clicked every mode.

A verified platform is one entry in `src/Platform.cs`; the board families and firmware probes behind generic
mode live in the same file. Nothing else is model-specific. The `add-laptop` skill in
`.claude/skills/` turns an issue into that entry and a pull request, see [CONTRIBUTING.md](CONTRIBUTING.md).

## Tools

| | |
|---|---|
| `tools\support-info.cmd` | data for a support request |
| `tools\verify.cmd` | read-only check of every query the app uses, plus a key-event capture |
| `tools\powertest.ps1` | A/B the power-gain slider on GPU watts and clocks |
| `tools\fantest.cmd` | firmware fan experiment. Holds the fans at zero for 60 s with an automatic abort; run it cool, idle and watching |
| `tools\omenprobe.cs` | CLI for raw BIOS calls |

## Layout

```
src\Platform.cs   platform profiles (the only model-specific file)
src\Hardware.cs   WMI/BIOS layer + simulated hardware
src\Lighting.cs   keyboard lighting (BIOS 0x20009) + Windows Dynamic Lighting hand-over
src\Engine.cs     apply logic, keep-alive, thermal guard, OMEN key, OGH takeover
src\Sensors.cs    perf counters + nvidia-smi
src\Ui.xaml, .cs  window, tray, hotkeys, on-screen flash
src\Program.cs    entry point
```

Pushing a `v*` tag builds the binaries on a Windows runner and attaches them to a release.

OMEN is a trademark of HP Inc. This project is not affiliated with HP.
