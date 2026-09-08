---
name: New laptop support
about: Ask for your HP OMEN / Victus model to be supported
title: "Support request: <model name>"
labels: new-laptop
---

Ohman only writes to firmware it has been verified on. To add your laptop we need the read-only
information below. It contains no personal data (no user name, serial, MAC or account).

**1. Run the collector** (from the repository folder, accept the UAC prompt, press the OMEN key when asked):

```
tools\support-info.cmd
```

**2. Paste the contents of `tools\support-info.txt` here:**

```
(paste)
```

**3. What OMEN Gaming Hub shows on your model** (helps map the modes):

- Performance modes offered (e.g. Eco / Balanced / Performance, or Quiet / Default / Performance):
- Fan options (Auto / Max / Manual sliders?):
- Any power slider (name and range, e.g. "Smart Performance Gain 0–15 W"):
- GPU options (Discrete/Hybrid switch, GPU power/boost toggles):

**4. Optional, very helpful:** the OGH background log from a session where you switched every mode once:
`%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter_v10z8vjag6ke6\LocalCache\Local\HPOMEN\HPOMENBG_<date>.log`
(search it for `inputData=` lines; they contain the exact bytes OGH sends). Remove anything you consider private.
