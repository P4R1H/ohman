# Contributing

## Adding a laptop

1. Open a **New laptop support** issue (the template asks for `tools\support-info.cmd` output and,
   ideally, OMEN Gaming Hub's background log with every mode clicked once).
2. The issue's evidence becomes one `PlatformProfile` entry in `src/Platform.cs`, in a PR whose body
   lists the proving log line for every byte. Anyone can raise that PR; nothing in it is guesswork.
3. The PR is merged only after the checklist in it has been run on the real machine by the issue author.

Everything model-specific lives in `src/Platform.cs`: verified profiles, the board families generic mode uses,
and the firmware probes. Please don't special-case a model anywhere else.

Two fields there are about the optional driver rather than the firmware, and both are applied whichever way a
profile was built:

- `DriverFor` says what this board's mailbox refuses and the driver can do instead, today only
  `DriverFor.FanLevels`, and only for boards whose firmware has been seen refusing `0x2E`. It is what puts the
  "this board needs a driver" note on the Home page, so it wants an issue number in a comment beside it.
- `Ec` is the embedded-controller register map. Being given one earns a board nothing on its own: nothing is
  written until `EmbeddedController.Verify` has recognised the registers on the machine itself. That check is
  what `tools\drivertest.cmd` prints, so a contributed `drivertest.txt` is the evidence for it.

## Windows and Linux: one core

The engine, the profiles, the firmware commands, the lighting protocols and the benchmark's arithmetic are one
copy of the code, compiled for both systems. Only the thin layer that reaches the hardware, and the window,
differ.

- `src\*.cs` listed in `linux\Core.props` is **shared**. Windows builds it with `build.cmd`, Linux with
  `dotnet build`. Keep it C# 5, and keep WPF, WinForms, WMI (`System.Management`), the registry and P/Invoke
  out of it.
- `src\win\` is Windows only and `src\linux\` is Linux only. A class that exists in one exists in the other
  with the same name and the same members: `Mailbox`, `Dmi`, `BiosEvents`, `EcLock`, `Power`, `KeyNames`,
  `AppPaths`, `Host`, `Driver`, `Hid`, `Display`, `Sensors`, `Support`, `WinLighting`, the system half of
  `Update`, and for the benchmark `FrameCapture`, `Machine`, `Cues` and the system half of `Games`. When shared
  code needs something from the operating system, it goes through one of these, or a new pair.
- Whether a feature exists on a system is asked of `Host` (`Host.HasVendorApp`, `Host.CanInstallDriver`,
  `Host.HasRefreshRate`, ...), never decided with `#if` or by sniffing the platform.
- A fix to the engine or a new board in `src\Platform.cs` is made once and reaches both.

**Two windows, kept as mirrors.** The WPF window (`src\Ui.cs`, `Ui.xaml`, `BenchUi.cs`, `Controls.cs`,
`Curve.cs`, `KeyboardView.cs`, `Theme.cs`, `Program.cs`) is Windows only. The Linux window is `linux\app\`
(Avalonia): the same files with the same class names, the same methods in the same order and the same
comments, each with a header listing its differences from the WPF original. A change to one window is ported
to the other by reading its diff and applying it to the file of the same name. No logic belongs in either
window that the other would need too: that goes in the shared core.

After touching a shared file, build both and run the checks:

```
build.cmd preview
wsl -d Ubuntu -- bash -c "cd linux/probe && ~/.dotnet/dotnet build"
bash linux/test/parity.sh
```

`linux\test\parity.sh` runs one scripted scenario (`src\Scenario.cs`: every mode, max fan, manual levels, a
curve climb, the thermal guard, lighting, the benchmark's statistics) on simulated hardware on both systems
and compares every hardware call the engine made. The two transcripts must be identical. A change that is
meant to alter what the engine sends changes both transcripts the same way; one that changes only one of them
is a bug.

When the change was not meant to alter Windows at all (a seam moved, a Linux back end added), prove it:
`bash linux/test/windows-baseline.sh <an Ohman.exe from before the change>` rebuilds the preview and compares
its engine log and reports with the old build's on simulated hardware for thirteen boards. It must print PASS.
Neither check reaches the elevated, real-hardware path, so a change to `src\win\` still wants the real
`Ohman.exe` run once on a laptop before it ships.

Other Linux checks, none of which need a laptop: `ohman-probe selftest`, `ohman-probe bench-selftest`,
`ohman-probe helper-test` against `ohman-helper --demo` (the whole socket path on simulated hardware), and
`linux\app-ui-test\run.sh` (the real window driven without a screen: every page clicked through).
`ohman --demo --page fans --screenshot out.png` renders a page to a picture the same way
`preview\Ohman.exe --demo --page fans --screenshot out.png` does, at the same size, so the two windows can be
laid side by side.

On Linux the firmware is reached through `linux\driver\ohman-wmi.c`, a small kernel module that passes
mailbox calls and embedded-controller bytes through, and `ohman-helper` (`linux\helper\`), the root service
the window talks to. Neither knows what a command means; that stays in the shared core.
[docs/linux.md](docs/linux.md) describes the installed pieces.

## Code

- C# 5 only: the project builds with the compiler that ships inside Windows (`build.cmd`), no SDK.
- Every firmware write must be backed by a primary source (OGH's own log or code) and, for anything
  touching fans, a measurement on the device. `docs/research.md` documents the interface.
- Keep the UI free of prose; short labels with a subscript for context.

To see what generic mode would build for a board without running on it: `preview\Ohman.exe --demo --board 8A25`
and read `preview\ohman.log`.

## Reporting a problem

Use the **Bug report** template and attach `ohman.log` (no personal data in it). If fans or temperatures
did anything surprising, say when: the log shows whether the thermal guard engaged. If you have the driver
installed, add `tools\drivertest.cmd` output, a temperature that looks wrong is usually answered by the one
line in it that compares the CPU's own sensor against the one Windows exposes.
