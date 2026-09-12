---
name: add-laptop
description: Add support for a new HP OMEN / Victus laptop to Ohman from a "New laptop support" issue, and open a pull request with the evidence. Use when asked to support a model, add a board, or handle a support-request issue.
---

# Add a laptop profile and open a PR

Boards without an entry already run in generic mode (see `Platforms.Generic` in `src/Platform.cs`: mode bytes
from the thermal-policy version and the hp-wmi board families, capabilities probed from the firmware). A verified
entry pins what generic mode guessed and unlocks model-specific data such as OGH's fan curve. Adding a laptop means adding ONE entry to
`src/Platform.cs` whose values are backed by evidence from that machine, then opening a PR that
shows the evidence. Never guess a byte; when a value is unknown, keep the safe default and say so.

## Inputs you need

From the GitHub issue (template `.github/ISSUE_TEMPLATE/new-laptop-support.md`):

1. `support-info.txt` output: model, **board id** (`Win32_BaseBoard.Product`, e.g. `8C58`), BIOS,
   the WMI class check, and the read-only BIOS queries:
   - `0x28 system data`: byte 3 = thermal policy version, byte 4 bit 0 = software fan control,
     byte 5 = default PL4, byte 8 = default concurrent TDP (the power-gain base).
   - `0x2F fan table`: byte 0 = fan count, byte 1 = entry count, then `{fan1, fan2, temp}` triplets.
   - `0x2D fan levels`, `0x2C fan types`, `0x23 sensor`, `0x21 gpu power`, `0x26 max fan`.
   - `0x2B keyboard type` and the `0x20009` colour table / backlight byte (lighting is detected at run time; nothing to add to the profile).
   - the `hpqBEvnt` line(s) captured when the OMEN key was pressed (EventID / EventData).
2. What OMEN Gaming Hub shows on that model (mode names, fan options, power slider, GPU options).
3. Ideally OGH's background log (`HPOMENBG_<date>.log`) from a session where every mode was
   clicked. Its `inputData=` lines are the ground truth for the bytes the firmware expects:
   - `SetFanModeAsync(), mode = L?` followed by `inputData=255,<mode>,<dc>,0` → the mode byte per mode.
   - `SetMaxFan(), mode = On/Off` → `inputData=1` / `0`.
   - `SetConcurrentTdp - value=N` → `inputData=255,255,255,N` (power gain; N − base = offset).
   - 4-byte `inputData=a,b,1,t` lines → GPU power payloads `{cTGP, PPAB, dState, peakTemp}`.
   - 128-byte `inputData=f1,f2,0,0,…` lines → fan levels OGH writes (its software curve).

If the issue lacks the OGH log, ask for it before mapping modes. Without it, only the values that come
from the system-data query are trustworthy (thermal policy, base TDP), and the mode bytes must follow
the documented thermal-policy tables (v1: `0x30/0x31/0x50`; v0: `0x00/0x01/0x02`), flagged as unverified.

## Steps

1. **Read the evidence** and write down, with the line that proves each:
   board id(s), thermal policy, fan count, fan level ceiling (highest level in the fan table or OGH's
   slider bound), TDP base (system data byte 8) and gain range (OGH slider), mode bytes, GPU payloads,
   OMEN key EventID/EventData. Anything you cannot prove keeps the Transcend 14 default and gets a
   `// UNVERIFIED` comment.
2. **Add the profile** to `Platforms.Known` in `src/Platform.cs`:

   ```csharp
   new PlatformProfile {
       Name = "HP OMEN <model> (<year>, <family id>)",
       Boards = new[] { "<board id>" },
       ThermalPolicy = 1,                       // system data byte 3
       ModeEco = 0x30, ModeBalanced = 0x30, ModePerformance = 0x31, ModeCool = 0x50,
       TdpBase = 30, TdpGainMax = 15,           // system data byte 8; OGH slider
       GpuBase = new byte[] { 0, 0, 1, 75 }, GpuBoost = new byte[] { 0, 1, 1, 87 }, GpuMax = new byte[] { 1, 1, 1, 87 },
       KeyEventId = 29, KeyEventData = 8613,
       HasPowerGain = true, HasGpuPower = true,   // false when the support-info shows system-data byte 8 = 0 / 0x21 rc != 0
       Verified = true,
       Curve = new FanCurve { Floor = 18, Ceiling = 57 /* CpuTemps/CpuLevels, GpuTemps/GpuLevels, IrTemps/IrLevels from OGH's profiles.json; omitted = the Transcend 14 curve is inherited, say so in Notes */ },
       Notes = "Verified <date> from issue #<n>: <what was verified, what is inferred>."
   }
   ```

   Keep the engine and UI untouched. If the model needs something the profile cannot express, stop and
   say what is missing rather than special-casing it elsewhere.
3. **Build**: `build.cmd preview` must succeed. Run `preview\Ohman.exe --demo --board <id>` once and check
   `preview\ohman.log` shows the new profile being picked (not a generic one). The real build needs no change.
4. **Update the README** "Built for" line to list the new model.
5. **Commit** on a branch named `platform/<board id>` with a message like
   `Add <model> (<board id>) platform profile` and the issue reference.
6. **Open the PR** with `gh pr create`. The body must contain:
   - the evidence table from step 1 (value → proving line),
   - a checklist for the issue author to run on the real machine **before merge**:
     - [ ] `tools\verify.cmd` runs: system data (0x28), fan table (0x2F) and fan levels (0x2D) return rc=0 (other queries may legitimately return rc=5)
     - [ ] mode switch changes fan behaviour as OGH did
     - [ ] Max fan on/off works; fans never read 0 rpm in Auto
     - [ ] power gain applies (rc=0) and `tools\powertest.ps1` shows the expected direction
     - [ ] Fn+F12 is captured (footer shows the event)
     - [ ] ran for 30 min idle and 10 min gaming with no thermal-guard trip
   - the sentence "Unsupported until this checklist is confirmed" if any value is UNVERIFIED.

## Safety rules that override everything above

- Fan level 0 switches fans off on HP firmware; the app's floor stays 1800 rpm. Do not lower it for a
  model without measured proof that its firmware behaves differently.
- The `0x10` fan-count query holds the firmware in user-defined fan state; do not add code paths that
  send it without also writing a fan level or the max-fan flag.
- If the thermal policy in the system data is not 0 or 1, refuse: the mode bytes are unknown.
- Never commit `support-info.txt`, logs, or state files (they are git-ignored for a reason).
