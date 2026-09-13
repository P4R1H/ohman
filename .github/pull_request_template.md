## What this changes

<!-- One or two sentences. -->

## Evidence

<!-- For anything that writes to the firmware: the OGH log line, the hp-wmi source, or the measurement that
     proves the bytes. A new laptop profile needs one line per field it overrides. -->

## Checks

- [ ] `build.cmd` succeeds
- [ ] Ran the real build on the machine, or this cannot affect the running app
- [ ] Fan behaviour unchanged, or the change was measured on a device
- [ ] No model-specific code outside `src/Platform.cs`
