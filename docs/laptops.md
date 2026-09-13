# Laptops

Ohman talks to the HP BIOS mailbox (`hpqBIntM`), which every OMEN and Victus laptop exposes. What differs
between models is which commands the firmware answers and which bytes each performance mode wants. That is
the only model-specific thing in the app, and it lives in one table: [`src/Platform.cs`](../src/Platform.cs).

## The two states

Every OMEN and Victus laptop is **supported**, and there is no "unsupported" list. A board nobody has ever seen
still runs: Ohman reads the firmware's own system-design data, works out which generation it is, and drives it
with the mode bytes documented for that generation. Power, GPU and lighting appear only where the firmware
answers for them. If it answers with nothing usable, Ohman stays read-only and says so in the window.

A model becomes **verified** once somebody has run the checklist below on that exact board and every control
did what it says. Its settings are then fixed rather than worked out at run time.

## Verified

| Model | Board | Verified on | Notes |
|---|---|---|---|
| OMEN Transcend 14 (2024, 14-fb0xxx) | `8C58` | 2026-09-12 | Core Ultra 9 185H + RTX 4070. Modes, fans, power gain, GPU power and four-zone lighting all confirmed against OMEN Gaming Hub 1101.2608. Graphics switching writes correctly but the restart it needs was never taken, so it is unconfirmed. |

See [Verifying your laptop](#verifying-your-laptop).

## Supported

Board ids come from the Linux `hp-wmi` driver's tables plus HP's own service documentation. Grouping is by
firmware generation, which is what decides the bytes; marketing names are approximate because HP
reuses a board across several SKUs.

### OMEN Transcend

| Model | Board ids |
|---|---|
| OMEN Transcend 14 (2024, other SKU) | `8E41` |
| OMEN Transcend 16 (2023–2025) | `8BB3`, `8C3B`, `8C4D`, `8E10` |

### OMEN 16 and OMEN MAX

| Model | Board ids |
|---|---|
| OMEN 16 (2021–2022) | `8A42`, `8A43` |
| OMEN 16 (2023–2025) | `8BAA`, `8BAB`, `8BCA`, `8BCD`, `8C76`, `8C77`, `8C78`, `8D24`, `8D26`, `8D2F`, `8E35` |
| OMEN MAX 16 (2025) | `8D41`, `8D87` |

### OMEN 15 and OMEN 17

| Model | Board ids |
|---|---|
| OMEN 15 (2019, 15-dc / 15-dh) | `8574`, `8600` |
| OMEN 17 (2019, 17-cb0) | `8603` |
| OMEN 15 (2020) | `8A15` |
| OMEN 15 / 17 (2021) | `8BAD` |
| OMEN 15 / 17, 2018–2021 generations | `84DA`, `84DB`, `84DC`, `8572`, `8573`, `8575`, `8601`, `8602`, `8604`, `8605`, `8606`, `8607`, `860A`, `8746`, `8747`, `8748`, `8749`, `874A`, `8786`, `8787`, `8788`, `878A`, `878B`, `878C`, `87B5`, `886B`, `886C`, `88C8`, `88CB`, `88D1`, `88D2`, `88F4`, `88F5`, `88F6`, `88F7`, `88FD`, `88FE`, `88FF`, `8900`, `8901`, `8902`, `8912`, `8917`, `8918`, `8949`, `894A`, `89EB` |

### Victus

| Model | Board ids |
|---|---|
| Victus 16 (2021–2023) | `88F8`, `8A25` |
| Victus 16 S / R (2023–2024) | `8A3D`, `8B2F`, `8BBE`, `8BD4`, `8BD5`, `8C99`, `8C9C` |
| Victus 15 | `88D9`, `88DA`, `8A3E`, `8C2F`, `8C30`, `8C3F`, `8D07`, `8DCD`, `8E5E` |

Victus firmware uses different mode bytes from OMEN: `0x00` default, `0x01` performance, `0x03` quiet on the
2021–2023 boards, and no quiet mode on the S/R boards. Ohman uses those bytes for the boards named in the
Linux `hp-wmi` driver's Victus tables. Any other board, Victus or not, is driven from the generation the
firmware reports, which is the same path every unlisted OMEN takes.

## Finding your board id

Open Ohman and look at the top right of **Settings**, or run:

```
wmic baseboard get product
```

## Verifying your laptop

This takes about five minutes and it is the most useful thing you can contribute.

1. **Modes.** Switch Eco → Balanced → Performance. The fans should audibly change within a few seconds, and
   the chassis reading on the Home page should drift. If nothing changes on any mode, say so.
2. **Fans.** Try Max (both fans should go loud), then Manual (set 50%, the reading should land near half of
   your maximum), then Curve (drag a point up, the fans should follow within one ramp delay), then Auto.
3. **Power gain.** If the row is there, move it to +15 W and check that nothing throttles or resets.
4. **GPU power.** If the row is there, try each of Base / Boost / Max.
5. **Graphics.** If your machine offers Discrete or iGPU only, *do not* switch it unless you are willing to
   restart. If you do, confirm it comes back correctly.
6. **Lighting.** If your keyboard lights, check that the zones in the app match the zones on the keyboard,
   and that Static, Breathe, Cycle and Wave all do something.
7. **Exit.** Close Ohman and confirm the fans return to the firmware's own behaviour within two minutes and
   nothing is stuck.

Then run:

```
tools\support-info.cmd
```

and open a [Verify my laptop](https://github.com/P4R1H/Ohman/issues/new?template=verify-laptop.yml) issue
with `tools\support-info.txt` attached and a line per step above. The file contains the model, board id, BIOS
version, the firmware's system-design bytes, the fan table and the OMEN key event id. No personal data, no
serial numbers.

## If your keyboard lights per key

HP's firmware lighting interface answers on these boards and drives nothing at all, so Ohman uses the
keyboard's own HID lighting interface instead. That part is written against a published standard but is
**unverified**: nobody working on Ohman has a per-key machine. Run this, which writes nothing and changes
nothing:

```
Ohman.exe --lamps
```

and open an issue with what it prints. It reports how many lamps your keyboard has, where each one is, and
which key each one lights. That is everything needed to confirm the feature works.

## If a control is wrong on your model

Open a [New laptop support](https://github.com/P4R1H/Ohman/issues/new?template=new-laptop-support.yml) issue
with the same file, what OMEN Gaming Hub shows for the same control, and, if you can get it, OGH's own
background log from a session where you clicked every mode:

```
%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\
```

That log is what made the Transcend 14 profile exact, and it is the fastest route to an exact profile for any
other model.

## Adding a laptop in code

A laptop is one entry in `Platforms.Known` in [`src/Platform.cs`](../src/Platform.cs):

```csharp
new PlatformProfile {
    Name = "HP OMEN 16 (2023, 16-wf0xxx)",
    Boards = new[] { "8BAA" },
    Notes  = "Verified 2026-09-10 against OGH 1101.x logs."
}
```

Everything else (fan ranges, mode bytes, which features exist) either has a sane default on
`PlatformProfile` or is read from the firmware at run time. Override only what the evidence says is
different, and put the evidence in `Notes`.
