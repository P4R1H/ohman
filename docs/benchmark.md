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

It is not shipped inside Ohman. The first Start downloads `PresentMon-2.6.0-x64.exe` from Intel's GitHub
release into `bench\` beside Ohman, and before every run Ohman checks its size, its SHA-256 and that it is
signed by Intel Corporation. A file that fails any of these is deleted, and the next Start downloads it again.

While a run is active PresentMon runs in a job object that ends it with Ohman, however Ohman ends.

## What is kept

| | |
|---|---|
| `bench\runs\*.run` | One small text file per run, beside the exe. The last 200 are kept. |
| `Pictures\Ohman` | Share cards, only when you press Save. |
| `Documents\Ohman` | CSV exports, only when you press Export. |

Nothing is uploaded anywhere. Uninstall in Settings removes `bench\` along with the rest.
