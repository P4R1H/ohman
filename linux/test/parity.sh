#!/bin/bash
# parity.sh: prove that the Windows build and the Linux build drive the hardware the same way.
#
# Both run the scripted scenario (src/Scenario.cs): one engine, a fixed script, simulated hardware, every
# hardware call written down. Windows: preview\Ohman.exe --demo --scenario FILE. Linux: ohman-probe --scenario
# FILE, in WSL. The two transcripts must be byte for byte the same; any difference is printed and the script
# fails. Run from Git Bash on Windows, after building both:
#
#     cmd /c ".\build.cmd preview"
#     wsl -d Ubuntu -- bash -c 'cd linux/probe && ~/.dotnet/dotnet build'
#     bash linux/test/parity.sh
#
# Options: --build   build the Linux probe first (the Windows preview is never built from here: only the
#                    Windows check does that)
#          --keep    leave the two transcripts in linux/.build/parity/ even when they match
# Environment: OHMAN_WSL_DISTRO (default Ubuntu), OHMAN_DOTNET (default ~/.dotnet/dotnet, inside WSL).
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
DISTRO="${OHMAN_WSL_DISTRO:-Ubuntu}"
DOTNET="${OHMAN_DOTNET:-~/.dotnet/dotnet}"
OUT="$REPO/linux/.build/parity"
build=0; keep=0
for a in "$@"; do case "$a" in --build) build=1 ;; --keep) keep=1 ;; *) echo "unknown option: $a"; exit 2 ;; esac; done

mkdir -p "$OUT" || exit 2
rm -f "$OUT/windows.txt" "$OUT/linux.txt"
export MSYS_NO_PATHCONV=1

# The repository as WSL sees it: C:\x\y -> /mnt/c/x/y.
win="$(cd "$REPO" && pwd -W)"
drive="$(printf '%s' "${win:0:1}" | tr 'A-Z' 'a-z')"
WSLREPO="/mnt/$drive${win:2}"

EXE="$REPO/preview/Ohman.exe"
[ -x "$EXE" ] || { echo "FAIL: preview/Ohman.exe is not built (cmd /c \".\\build.cmd preview\")"; exit 1; }

if [ $build -eq 1 ]; then
  wsl.exe -d "$DISTRO" -- bash -c "export DOTNET_ROOT=\$(dirname $DOTNET); cd \"$WSLREPO/linux/probe\" && $DOTNET build -nologo -v q 2>&1 | grep -E ' error |rror MSB' | sort -u | head -20"
fi

# Windows. A file, not the console: a GUI-subsystem exe has no console to print to, and a code page would
# get between the two texts if it had. The exe is copied aside so its log lands beside the copy, not in preview\.
cp "$EXE" "$OUT/Ohman.exe" || exit 2
( cd "$OUT" && ./Ohman.exe --demo --scenario "$(cd "$OUT" && pwd -W)\\windows.txt" )
rc=$?
[ $rc -eq 0 ] && [ -s "$OUT/windows.txt" ] || { echo "FAIL: the Windows build did not write a transcript (exit $rc)"; exit 1; }

# Linux, in WSL. OHMAN_HOME keeps its log and settings out of the user's own.
wsl.exe -d "$DISTRO" -- bash -c "export DOTNET_ROOT=\$(dirname $DOTNET); export OHMAN_HOME=\"$WSLREPO/linux/.build/parity/home\"; \"$WSLREPO/linux/.build/bin/ohman-probe/Debug/net8.0/ohman-probe\" --scenario \"$WSLREPO/linux/.build/parity/linux.txt\""
rc=$?
[ $rc -eq 0 ] && [ -s "$OUT/linux.txt" ] || { echo "FAIL: the Linux build did not write a transcript (exit $rc; is linux/probe built?)"; exit 1; }

n=$(wc -l < "$OUT/windows.txt")
if cmp -s "$OUT/windows.txt" "$OUT/linux.txt"; then
  echo "PASS: Windows and Linux made the same $n-line transcript ($(grep -c '^board ' "$OUT/windows.txt") boards, $(grep -c '^    hw ' "$OUT/windows.txt") hardware calls)"
  [ $keep -eq 1 ] || rm -f "$OUT/windows.txt" "$OUT/linux.txt" "$OUT/Ohman.exe" "$OUT/ohman.log"
  exit 0
fi
echo "FAIL: the transcripts differ (windows < > linux); both are kept in linux/.build/parity/"
diff "$OUT/windows.txt" "$OUT/linux.txt" | head -40
exit 1
