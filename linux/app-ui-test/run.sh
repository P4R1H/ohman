#!/bin/bash
# run.sh [scenario ...]: build the interaction test and run it. Inside WSL / Linux, no display needed or used.
# Everything the app writes goes under one temporary home: settings, log, benchmark runs, the autostart entry,
# the share card. The programs the app would hand a file or a URL to (xdg-open, systemctl) are replaced by a stub
# that only writes down what it was asked, so nothing opens and nothing restarts.
# Exit code 0 when every check passed. OUT (default: <temp home>/shots) receives a PNG of every page.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DOTNET="$DOTNET_ROOT/dotnet"
T="${OHMAN_UITEST_DIR:-/tmp/ohman-ui-test}"
rm -rf "$T"
mkdir -p "$T/home" "$T/run" "$T/bin"
for tool in xdg-open gnome-open kfmclient wslview systemctl; do
    printf '#!/bin/sh\necho "%s $*" >> "%s/spawned.txt"\n' "$tool" "$T" > "$T/bin/$tool"
    chmod +x "$T/bin/$tool"
done
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ "${NOBUILD:-}" != 1 ]; then
    (cd "$HERE" && "$DOTNET" build -nologo -v q > "$T/build.txt" 2>&1)
    rc=$?
    grep -E " error |rror\(s\)|Elapsed" "$T/build.txt" | sed -E "s# \[/mnt.*##" | sort -u | head -30
    if [ $rc != 0 ]; then echo "the build failed"; exit 4; fi
fi
export HOME="$T/home" OHMAN_HOME="$T/home/ohman" XDG_RUNTIME_DIR="$T/run" OHMAN_UITEST_DIR="$T"
export PATH="$T/bin:/usr/bin:/bin"
unset DISPLAY WAYLAND_DISPLAY XDG_SESSION_TYPE XDG_CONFIG_HOME XDG_STATE_HOME XDG_DATA_HOME XDG_PICTURES_DIR
# No network: the update check is given a proxy that refuses, so it fails at once instead of asking GitHub.
export https_proxy=http://127.0.0.1:9 http_proxy=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 HTTP_PROXY=http://127.0.0.1:9 ALL_PROXY=http://127.0.0.1:9
export OUT="${OUT:-$T/shots}"
mkdir -p "$OUT"
cd "$HERE/../.build/bin/uitest/Debug/net8.0" || exit 4
timeout 1200 "$DOTNET" ohman.dll "$@" | tee "$T/result.txt"
# The app's own exit path is one of the things under test: only a run that got as far as its verdict counts.
grep -q "^PASS " "$T/result.txt"
