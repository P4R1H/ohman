#!/bin/bash
# windows-baseline.sh: prove that a change to shared code left the Windows build behaving as it did.
#
#     bash linux/test/windows-baseline.sh <baseline Ohman.exe>
#
# The baseline is any Ohman.exe from before the change: the last release, or preview\Ohman.exe built from the
# commit you started from (copy it somewhere first; this script rebuilds preview\Ohman.exe). Both exes are run
# on simulated hardware (--demo) for a set of board ids, writing the support report and the driver report, and
# the two are compared:
#   * the engine's log must be identical, line for line, once the clock is removed;
#   * the reports must be identical once numbers are masked (they carry live readings) and the lines read from
#     the real embedded controller are dropped (they depend on whether a running Ohman holds its lock).
# The baseline is recorded afresh on every run, so whatever the reports quote from this machine (vendor logs,
# installed drivers) is the same on both sides. Nothing touches the firmware, and no window is shown.
#
# Run from Git Bash on Windows. Prints PASS or FAIL; exit code 0 or 1. It says nothing about the elevated,
# real-hardware path: that still needs Ohman.exe run on a laptop.
set -u
[ $# -eq 1 ] && [ -f "$1" ] || { echo "usage: bash linux/test/windows-baseline.sh <baseline Ohman.exe>"; exit 2; }
BASE_EXE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$REPO/linux/.build/windows-baseline"
BOARDS="real 8C58 8A25 8BCD 878A 8786 8D41 8748 8607 8BD4 8574 89B5 ZZZZ"

cd "$REPO" || exit 2
out=$(cmd.exe //c ".\\build.cmd preview" 2>&1 | tail -5)
echo "$out" | grep -q "Built preview" || { echo "FAIL: build.cmd preview did not build"; echo "$out"; exit 1; }

record() {      # record <exe> <dir>
    rm -rf "$2"; mkdir -p "$2"; cp "$1" "$2/Ohman.exe"
    ( cd "$2" || exit 1
      for b in $BOARDS; do
          if [ "$b" = real ]; then board=""; else board="--board $b"; fi
          ./Ohman.exe --demo $board --support > "$b.txt" 2>&1
          ./Ohman.exe --demo $board --driver > "$b.driver.txt" 2>&1
      done
      sed -E 's/^[0-9-]+ [0-9:.]+  //' ohman.log > log.norm )
}
record "$BASE_EXE" "$WORK/base"
record "$REPO/preview/Ohman.exe" "$WORK/after"

cd "$WORK" || exit 2
fail=0
n=$(diff base/log.norm after/log.norm | grep -c '^[<>]')
if [ "$n" -ne 0 ]; then echo "engine log differs ($n lines):"; diff base/log.norm after/log.norm | head -20; fail=1; fi
masked() { grep -v -E '^ +(ec( |:)|tach verification)|^saved to' "$1" | sed -E 's/[0-9]+/N/g'; }
for f in base/*.txt; do
    g="after/$(basename "$f")"
    n=$(diff <(masked "$f") <(masked "$g") | grep -c '^[<>]')
    # two lines of slack: a reading that changes its number of digits moves a column
    if [ "$n" -gt 2 ]; then echo "report $(basename "$f") differs ($n lines):"; diff <(masked "$f") <(masked "$g") | head -10; fail=1; fi
done
if [ $fail -eq 0 ]; then echo "PASS: preview\\Ohman.exe behaves as the baseline does (engine log identical, reports identical apart from live readings)"; else echo "FAIL"; fi
exit $fail
