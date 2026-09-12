# Contributing

## Adding a laptop

1. Open a **New laptop support** issue (the template asks for `tools\support-info.cmd` output and,
   ideally, OMEN Gaming Hub's background log with every mode clicked once).
2. A maintainer, or you with Claude Code, runs the `add-laptop` skill in `.claude/skills/add-laptop/`:
   it turns the issue's evidence into one `PlatformProfile` entry in `src/Platform.cs` and opens a PR
   whose body lists the proving log line for every byte.
3. The PR is merged only after the checklist in it has been run on the real machine by the issue author.

Everything model-specific lives in `src/Platform.cs`: verified profiles, the board families generic mode uses,
and the firmware probes. Please don't special-case a model anywhere else.

## Code

- C# 5 only: the project builds with the compiler that ships inside Windows (`build.cmd`), no SDK.
- Every firmware write must be backed by a primary source (OGH's own log or code) and, for anything
  touching fans, a measurement on the device. `docs/research.md` documents the interface.
- Keep the UI free of prose; short labels with a subscript for context.

## Reporting a problem

Use the **Bug report** template and attach `ohman.log` (no personal data in it). If fans or temperatures
did anything surprising, say when: the log shows whether the thermal guard engaged.
