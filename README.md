<p align="center"><img src="docs/hero.png" alt="Ohman: fan curves, keyboard lighting and the OMEN key, without the vendor adware"></p>

<p align="center">
<a href="https://github.com/P4R1H/Ohman/releases/latest/download/Ohman.exe"><img alt="Download Ohman.exe" src="https://img.shields.io/badge/Download%20Ohman.exe-3F8CFF?style=for-the-badge&logo=windows&logoColor=white"></a>
&nbsp;
<a href="https://p4r1h.github.io/ohman/"><img alt="Website" src="https://img.shields.io/badge/Website-21262D?style=for-the-badge"></a>
<a href="docs/laptops.md"><img alt="Supported laptops" src="https://img.shields.io/badge/Supported%20laptops-21262D?style=for-the-badge"></a>
<a href="docs/research.md"><img alt="How the firmware works" src="https://img.shields.io/badge/How%20it%20works-21262D?style=for-the-badge"></a>
</p>

Don't you love paying $2,500 for a laptop and still having ads pushed down your throat by mandatory software
with no alternative? Ohman is the alternative. One executable, no services, no drivers, no account, no ads.
Same firmware interface as OMEN Gaming Hub, same bytes, nothing else.

Ohman interacts with the BIOS through the same commands OGH uses. Use at your own risk.

## Features

| | |
|---|---|
| **Modes** | Eco · Balanced · Performance, one click, the OMEN key, or a hotkey. Each mode remembers its own fans, power gain and GPU choice, and the whole window takes that mode's colour. |
| **Fans** | Auto (this model's own curve), Max, Manual per fan, or your own curve: seven points you drag, a floor, a ramp delay, and a separate GPU curve if you unlink it. The live reading rides the curve. |
| **Max fan** | Full speed with a way back: it returns to Auto once the chips are below 60° for two minutes, or after 15, 30 or 60 minutes. |
| **Power gain** | OGH's "Smart Performance Gain": +0 to +15 W on the CPU+GPU budget NVIDIA Dynamic Boost draws from. |
| **GPU power** | Base · Boost · Max, or follow the mode. |
| **Graphics** | Hybrid, Discrete (the MUX) or iGPU only, whichever the firmware offers, with the restart it needs. |
| **Lighting** | The keyboard drawn as it actually lights. Select a key, a row, a zone or the whole board, then pick a hue and a shade; or Breathe, Cycle, Wave, or hand it to Windows Dynamic Lighting. Four zones or per key, whichever your keyboard has. |
| **Display** | Refresh rate, and the lowest rate on battery if you want it. |
| **Live** | CPU and GPU temperature, CPU package watts, fan speeds, load, clocks, chassis sensor, battery. CPU temperature on the tray icon. |
| **Tray** | Every control above without opening the window: modes, fan mode, refresh rate, GPU power, graphics, lighting, brightness and all the switches. |
| **OMEN key** | Opens the panel, cycles modes, toggles max fan, or runs a command of your choice. OGH's key handler is stopped, reversibly. Shift+F11 cycles modes; Ctrl+Alt+E/B/P/M/O for the rest. |
| **Safety** | A thermal guard forces max fan on a hot CPU, a hot chassis or stalled fans. It can be switched off, with a warning. Fans are never set below 1800 rpm. |
| **Extras** | Starts with Windows without a UAC prompt, Eco on battery, Windows power-mode sync, an on-screen flash when a key changes something, an update check that never installs anything for you. |

Settings live in `ohman.state`, everything the app does goes to `ohman.log`. Both sit beside the executable.
Uninstalling is deleting it.

## Install

Download `Ohman.exe` from [Releases](../../releases/latest) and run it. It asks for administrator rights once,
because the firmware interface needs them, and adds itself to your startup apps so it is there after a reboot
(one switch in Settings turns that off).

Or build it with the compiler that already ships inside Windows. No SDK, no NuGet, no toolchain:

```
build.cmd
```

`preview\Ohman.exe` is the same UI on simulated hardware and needs no administrator rights, which is what the
screenshots above are.

## Your laptop

**Verified on the HP OMEN Transcend 14 (2024, board 8C58).** Every other OMEN and Victus laptop is *supported*:
Ohman asks the firmware what generation it is, drives it with the mode bytes documented for that generation,
and enables power, GPU and lighting controls only where the firmware answers.

The full table is in **[docs/laptops.md](docs/laptops.md)**.

> ### Verify your laptop
> If your machine is not marked verified, this is the most useful thing you can contribute, and it takes five
> minutes. Run through [the checklist](docs/laptops.md#verifying-your-laptop), run `tools\support-info.cmd`, and
> open a **Verify my laptop** issue with the file it writes. Your model moves to verified and everybody with that
> board gets a tested profile instead of a deduced one.

If a control is wrong on your model, open a **New laptop support** issue with the same file and, if you can get
it, OGH's own log from a session where you clicked every mode:
`%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\`. That log is what
made the Transcend 14 profile exact.

## How it works

HP exposes a BIOS mailbox as the WMI class `hpqBIntM`. Ohman uses the commands OGH uses: performance mode
(`0x1A`), max fan (`0x27`), fan levels (`0x2E`), CPU+GPU power budget (`0x29`), GPU power (`0x22`), graphics
mode (`0x52`), keyboard lighting (`0x20009`), plus read-only queries. The OMEN key arrives as a WMI event
(`hpqBEvnt`). Every byte and every measured firmware behaviour is written down in
[docs/research.md](docs/research.md), including the things that are *not* safe to do and why.

Two of those are worth knowing about:

- The firmware forgets a user-defined fan state after about 120 seconds, so Ohman re-asserts it. When it
  expires the firmware first re-applies the *last written level* before its own curve resumes, so handing the
  fans back is not as simple as it sounds. Ohman never writes a level below 1800 rpm.
- The thermal guard is independent of everything else: it reads the fans and the chassis sensor every ten
  seconds and forces maximum fan if the machine is running away, regardless of what mode you picked.

## Adding a laptop in code

A verified laptop is one entry in [`src/Platform.cs`](src/Platform.cs):

```csharp
new PlatformProfile {
    Name = "HP OMEN 16 (2023, 16-wf0xxx)",
    Boards = new[] { "8BAA" },
    Notes  = "Verified 2026-09-20 against OGH 1101.x logs."
}
```

Everything else has a default on `PlatformProfile` or is read from the firmware at run time: the mode bytes,
the fan curve and its units, the guard limits, the GPU payloads, the OMEN key event. Override only what the
evidence says is different and put the evidence in `Notes`. The `add-laptop` skill in `.claude/skills/` turns a
support issue into exactly that entry and a pull request; see [CONTRIBUTING.md](CONTRIBUTING.md).

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
src\Platform.cs   platform profiles: the only model-specific file
src\Hardware.cs   the WMI/BIOS mailbox + simulated hardware
src\Lighting.cs   keyboard lighting (0x20009) + the Windows Dynamic Lighting hand-over
src\Engine.cs     settings, apply logic, keep-alive, thermal guard, OMEN key, OGH takeover
src\Sensors.cs    perf counters + nvidia-smi
src\Curve.cs      the fan-curve graph
src\Keyboard.cs   the keyboard drawing
src\Ui.xaml, .cs  window, pages, tray, hotkeys, on-screen flash
src\Program.cs    entry point
fonts\            IBM Plex, embedded in the exe (OFL, see fonts\OFL.txt)
```

Pushing a `v*` tag builds on a Windows runner and attaches the binaries to a release.

## Later

- **Benchmarking tab**: run a short load, record clocks, watts, temperatures and throttle events, and let you
  compare two settings honestly instead of guessing whether +15 W did anything.
- **Fan curve import/export** so a verified model's curve can be shared as a file.

Ideas and issues are welcome.

## Licence

GPL-3.0-or-later. OMEN is a trademark of HP Inc. This project is not affiliated with HP.
