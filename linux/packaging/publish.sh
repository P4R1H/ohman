#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the Linux release: ohman-<version>-linux-x64.tar.gz, its .sha256, and ohman-install.sh (the
# one-line installer with this version written into it). Runs on Linux or WSL with the .NET 8 SDK.
#
#   linux/packaging/publish.sh                 all three programs must build
#   linux/packaging/publish.sh --allow-missing package whatever builds, and say what did not
#   linux/packaging/publish.sh --out DIR       where the three files go (default linux/.build/publish)
#   linux/packaging/publish.sh --single-file   one large file per program instead of a shared runtime (below)
#
# The programs are self-contained: nobody needs .NET installed to run them. The three are published into
# one directory, where they share a single copy of the .NET runtime: a couple of hundred files in
# /opt/ohman, and a download of about 39 MB.
#
# --single-file makes each program one file with its own copy of the runtime inside (about 35 MB apiece,
# a 93 MB download). The window then still has the two native libraries Avalonia draws with beside it
# (libSkiaSharp, libHarfBuzzSharp), which is cheaper than unpacking them at every start.
# The version is the one in src/Meta.cs, like everything else Ohman builds.

set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
LINUX=$(dirname "$HERE")
REPO=$(dirname "$LINUX")
RID=linux-x64

OUT=$LINUX/.build/publish
ALLOW_MISSING=0
SHARED=1
while [ $# -gt 0 ]; do
    case "$1" in
        --allow-missing) ALLOW_MISSING=1 ;;
        --single-file)   SHARED=0 ;;
        --shared-runtime) SHARED=1 ;;
        --out)           OUT=$2; shift ;;
        -h|--help)       sed -n '3,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)               echo "publish.sh: unknown option $1" >&2; exit 2 ;;
    esac
    shift
done

DOTNET=${DOTNET:-}
if [ -z "$DOTNET" ]; then
    if command -v dotnet >/dev/null 2>&1; then DOTNET=dotnet
    elif [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET=$HOME/.dotnet/dotnet; export DOTNET_ROOT=$HOME/.dotnet
    else echo "publish.sh: no dotnet found (install the .NET 8 SDK, or set DOTNET=/path/to/dotnet)" >&2; exit 1
    fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

VERSION=$(sed -n 's/.*Version = "\([0-9][0-9.]*\)".*/\1/p' "$REPO/src/Meta.cs" | head -n 1)
[ -n "$VERSION" ] || { echo "publish.sh: no version found in src/Meta.cs" >&2; exit 1; }

NAME=ohman-$VERSION-$RID
TARBALL=$OUT/$NAME.tar.gz
# Staged in a temporary directory on a Linux file system: under WSL the repository sits on a Windows drive,
# where every file is mode 0777 and chmod does nothing, and those modes would end up in the tarball.
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
STAGE=$TMP/$NAME
rm -rf "$OUT/work" "$OUT/$NAME"
mkdir -p "$STAGE/bin" "$OUT/work"
if [ "$SHARED" = 1 ]; then
    FLAVOUR=(-p:PublishSingleFile=false)
else
    FLAVOUR=(-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true)
fi

echo "Ohman $VERSION for $RID"

BUILT=()
MISSING=()

# project directory, name of the program it makes
publish() {
    # Its own intermediate directory: a release build for linux-x64 must not disturb the debug build
    # somebody has open in linux/.build/obj.
    local proj=$1 exe=$2 dir=$OUT/work/$2 f
    if [ ! -d "$LINUX/$proj" ] || ! ls "$LINUX/$proj"/*.csproj >/dev/null 2>&1; then
        echo "  $exe: linux/$proj has no project yet"
        MISSING+=("$exe")
        return 0
    fi
    echo "  $exe: publishing"
    if ! "$DOTNET" publish "$LINUX/$proj" -c Release -r "$RID" --self-contained true \
            "${FLAVOUR[@]}" \
            -p:DebugType=none -p:DebugSymbols=false \
            -p:Version="$VERSION"             -p:BaseIntermediateOutputPath="$OUT/obj/$exe/" \
            -o "$dir" -v q >"$OUT/work/$exe.log" 2>&1; then
        echo "  $exe: DID NOT BUILD (log: $OUT/work/$exe.log)"
        grep -E 'error' "$OUT/work/$exe.log" | sort -u | head -n 15 | sed 's/^/      /' || true
        MISSING+=("$exe")
        return 0
    fi
    if [ ! -f "$dir/$exe" ]; then
        echo "  $exe: built, but $dir/$exe is not there"
        MISSING+=("$exe")
        return 0
    fi
    # The program, and whatever native libraries it needs beside it. Nothing else belongs in a release.
    for f in "$dir"/*; do
        case "$f" in
            *.pdb|*.xml|*.dbg) ;;
            *) cp -r "$f" "$STAGE/bin/" ;;
        esac
    done
    BUILT+=("$exe")
}

publish app    ohman
publish helper ohman-helper
publish probe  ohman-probe

if [ ${#BUILT[@]} -eq 0 ]; then
    echo "publish.sh: nothing built" >&2; exit 1
fi
if [ ${#MISSING[@]} -gt 0 ] && [ "$ALLOW_MISSING" = 0 ]; then
    echo "publish.sh: not built: ${MISSING[*]}. A release needs all three (--allow-missing packages the rest)." >&2
    exit 1
fi

# As a single file, the helper must really be one file: its unit gives it nowhere to unpack anything.
if [ -f "$STAGE/bin/ohman-helper" ] && [ "$SHARED" = 0 ]; then
    for f in "$OUT/work/ohman-helper"/*; do
        case "$f" in
            */ohman-helper|*.pdb|*.xml|*.dbg) ;;
            *) echo "  note: ohman-helper published an extra file, $(basename "$f"); check the systemd unit still fits" ;;
        esac
    done
fi

# Everything install.sh needs, laid out the way linux/ is, so the same script runs from both.
mkdir -p "$STAGE/driver" "$STAGE/packaging" "$STAGE/docs"
for f in "$LINUX/driver"/*; do
    case "$f" in
        *.c|*.h|*/Makefile|*/Kbuild|*/dkms.conf) cp "$f" "$STAGE/driver/" ;;
    esac
done
cp "$LINUX/install.sh" "$LINUX/uninstall.sh" "$STAGE/"
for f in ohman-helper.service ohman.desktop ohman.png 70-ohman.rules modules-load.conf dkms-sign.sh ohman.sysusers; do
    cp "$HERE/$f" "$STAGE/packaging/"
done
cp "$REPO/LICENSE" "$STAGE/LICENSE"
# OFL 1.1 asks that the licence travels with the fonts, and the window carries them inside it.
mkdir -p "$STAGE/fonts"; cp "$REPO/fonts/OFL.txt" "$STAGE/fonts/"
if [ -f "$REPO/docs/linux.md" ]; then cp "$REPO/docs/linux.md" "$STAGE/docs/"; fi
printf '%s\n' "$VERSION" >"$STAGE/VERSION"

# A checkout on Windows may have turned line endings into CRLF, which a shell does not forgive.
find "$STAGE" -type f \( -name '*.sh' -o -name '*.service' -o -name '*.rules' -o -name '*.conf' \
     -o -name '*.desktop' -o -name '*.sysusers' -o -name 'Makefile' -o -name 'Kbuild' -o -name '*.c' -o -name '*.h' \) \
     -exec sed -i 's/\r$//' {} +
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE/install.sh" "$STAGE/uninstall.sh" "$STAGE/packaging/dkms-sign.sh"
for f in ohman ohman-helper ohman-probe createdump; do
    if [ -f "$STAGE/bin/$f" ]; then chmod 755 "$STAGE/bin/$f"; fi
done

# Same bytes for the same sources: fixed order, fixed owner, the commit's time if there is one.
MTIME=$(git -C "$REPO" log -1 --format=%cI 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)
tar -C "$TMP" --sort=name --owner=0 --group=0 --numeric-owner --mtime="$MTIME" -cf - "$NAME" | gzip -9n >"$TARBALL"
(cd "$OUT" && sha256sum "$NAME.tar.gz" >"$NAME.tar.gz.sha256")

# The one-line installer, tied to this release: it fetches exactly this tarball.
sed "s/^VERSION=\"\"/VERSION=\"$VERSION\"/" "$HERE/get.sh" | sed 's/\r$//' >"$OUT/ohman-install.sh"
chmod 755 "$OUT/ohman-install.sh"

rm -rf "$OUT/work" "$OUT/obj"

echo
echo "Sizes:"
for f in ohman ohman-helper ohman-probe libSkiaSharp.so libHarfBuzzSharp.so; do
    if [ -f "$STAGE/bin/$f" ]; then printf '  %-28s %s\n' "$f" "$(du -h --apparent-size "$STAGE/bin/$f" | cut -f1)"; fi
done
printf '  %-28s %s in %s files\n' "bin/" "$(du -sh --apparent-size "$STAGE/bin" | cut -f1)" "$(find "$STAGE/bin" -type f | wc -l)"
printf '  %-28s %s\n' "unpacked, all of it" "$(du -sh --apparent-size "$STAGE" | cut -f1)"
printf '  %-28s %s\n' "$NAME.tar.gz" "$(du -h --apparent-size "$TARBALL" | cut -f1)"
echo
echo "Built:   ${BUILT[*]}"
if [ ${#MISSING[@]} -gt 0 ]; then echo "MISSING: ${MISSING[*]}  (this is not a complete release)"; fi
echo "Release files in $OUT:"
echo "  $NAME.tar.gz"
echo "  $NAME.tar.gz.sha256"
echo "  ohman-install.sh"
