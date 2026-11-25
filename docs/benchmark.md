# Benchmark

How a run is measured, what the numbers mean, and what Ohman keeps.

## A run

1. **Start.** Ctrl+Alt+R in the game, or Start on the Benchmark page.
2. **Finding the game.** The game is the program that owns the window in front and draws at least 10 frames a
   second for 3 seconds running. Launchers, browsers, overlays, recorders and Windows itself are skipped
   (the list is `Games.Deny` in [src/Bench.cs](../src/Bench.cs)). Ohman waits up to 10 minutes.
3. **Warm-up.** Auto by default: at least 60 s, then until the CPU temperature moves less than 1° a minute, and
   never more than 5 minutes. Or a fixed 2:00, or none. Only time with the game in front counts.
4. **Countdown.** 4 seconds. The overlay leaves 2 seconds before measuring starts.
5. **Measuring.** 30 s, 1 or 3 minutes. Alt-tab pauses it and nothing is counted while you are away; coming
   back waits 1 second before counting again. Away for more than 5 minutes ends the run.
6. **Result.** Saved, compared with your last run of the same game in the same mode, and ready as a card.

A run ends early on a mode change or a charger plugged in or out, because the result would mix two setups. A
run stopped after at least half its measuring time is kept and says why it ended.

## The numbers

Frame times come from Intel's [PresentMon](https://github.com/GameTechDev/PresentMon) 2.6.0
(`MsBetweenPresents`, dropped frames included), on the game's main swap chain only. The definitions are
[CapFrameX](https://github.com/CXWorld/CapFrameX)'s, checked against a transcription of its source:

| | |
|---|---|
| **Average FPS** | Frames divided by the time they took. Never the mean of per-frame FPS. |
| **1% low** | 1000 ÷ the 99th percentile frame time. Needs at least 1,000 frames, otherwise not shown. |
| **0.1% low** | 1000 ÷ the 99.9th percentile frame time. Needs at least 10,000 frames. |
| **Percentiles** | R-8 quantiles, in frame-time space. |
| **Stutter** | The share of time spent in frames slower than 2.5× the moving average of the frames before them. |

**Frame generation.** When present-to-present times alternate short and long while the screen sees an even
cadence, the run is measured on display times instead and is marked as such.

**Frame cap.** A run whose display intervals sit tight on a usual limit (60, 120, 144, 165, the panel's own
rate, 3 below one of those, or NVIDIA Reflex's cap) is marked as capped: its FPS is the limit, not the laptop.

**Beside the frames**, once a second: CPU and GPU temperature, power and clock, GPU load, fan speeds, and
whether the GPU was held back by its power limit (NVIDIA only).

## PresentMon

PresentMon reads presents from Windows' own event tracing (ETW). Nothing is injected into the game.

It works on **any GPU: NVIDIA, AMD and Intel**, and with DirectX 9 to 12, Vulkan and OpenGL games, because it
reads what Windows itself records about each frame rather than asking the graphics driver. The frame numbers
(average FPS, lows, frame times, stutter) are the same on all of them. GPU temperature, power, clock and load
come from NVIDIA's driver, so on an AMD Radeon those columns stay empty; CPU, fans and every frame number still
work.

It is not shipped inside Ohman. The first Start downloads `PresentMon-2.6.0-x64.exe` from Intel's GitHub
release into `bench\` beside Ohman, and before every run Ohman checks its size, its SHA-256 and that it is
signed by Intel Corporation. A file that fails any of these is deleted, and the next Start downloads it again.

While a run is active PresentMon runs in a job object that ends it with Ohman, however Ohman ends.

## Linux: MangoHud

Linux has no PresentMon. Frame times there come from [MangoHud](https://github.com/flightlessmango/MangoHud),
which sits inside the game and is put there by the game's launch option. Ohman loads nothing into a game: it
asks a MangoHud that is already running to log, over MangoHud's own control socket, and reads that log.

Start the game with (Steam: Properties, Launch options; Lutris and Heroic have the same field):

    MANGOHUD_CONFIG=read_cfg,control=mangohud-%p,log_interval=0 mangohud %command%

`control=` opens the socket Ohman talks to, `log_interval=0` makes the log one row a frame, `read_cfg` keeps
your own MangoHud.conf. The page shows this line and copies it when no such game is found.

What differs from Windows:

- The game is the program with MangoHud that is drawing, not whatever window is in front. The window in front
  only decides whether the run is paused, and only X11 (and XWayland) windows can be seen: on Wayland, with a
  native Wayland window in front, the run is not paused.
- MangoHud logs the time between presents and nothing about the display, so frame generation is not detected
  and a frame cap is judged on present times alone. The present mode is stored as "Other".
- The log MangoHud writes for a run (in its `output_folder`, your home directory by default) is removed again
  when the run ends. A log you were already recording is read and left alone.
- Runs are kept in `~/.local/share/ohman/bench/runs`, cards in Pictures/Ohman, exports in Documents/Ohman.

None of this has been run against a real game yet: it was written from MangoHud's source and tried against an
emulation of its socket and log (`linux/test/fake-mangohud.py`).

## What is kept

| | |
|---|---|
| `bench\runs\*.run` | One small text file per run, beside the exe. The last 200 are kept. |
| `Pictures\Ohman` | Share cards, only when you press Save. |
| `Documents\Ohman` | CSV exports, only when you press Export. |

Nothing is uploaded anywhere. Uninstall in Settings removes `bench\` along with the rest.
