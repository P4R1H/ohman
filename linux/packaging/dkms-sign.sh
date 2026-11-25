#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Signs the installed ohman-wmi module with this machine's own Ohman key, if there is one and the module
# is not signed already. The installer puts this file into the DKMS source tree as sign.sh and names it as
# POST_INSTALL, so DKMS runs it after every install, including the automatic rebuild for a new kernel.
#
# It exists for one case: Secure Boot is on, and the DKMS on this distribution does not sign what it builds
# (DKMS before 3.0, or one that could not find the kernel's sign-file). Where DKMS signs by itself, the
# module arrives here signed and this does nothing. Where the installer never made a key, there is nothing
# to sign with and this does nothing.
#
# It never fails the DKMS install: an unsigned module is reported by the installer, with what to do.

kver="${1:-$(uname -r)}"
dir="${OHMAN_MOK_DIR:-/var/lib/ohman/mok}"
key="$dir/ohman-mok.key"
cert="$dir/ohman-mok.der"
# Where the kernels' modules are; only a test points this anywhere else.
mods="${OHMAN_MODULES_DIR:-/lib/modules}"

[ -r "$key" ] && [ -r "$cert" ] || exit 0

signfile=""
for d in "$mods/$kver/build/scripts" "/usr/src/linux-headers-$kver/scripts" \
         "/usr/src/kernels/$kver/scripts" /usr/lib/linux-kbuild-*/scripts; do
    if [ -x "$d/sign-file" ]; then signfile="$d/sign-file"; break; fi
done
if [ -z "$signfile" ] && command -v kmodsign >/dev/null 2>&1; then signfile="kmodsign"; fi
if [ -z "$signfile" ]; then
    echo "ohman-wmi: no sign-file for kernel $kver, the module stays unsigned" >&2
    exit 0
fi

# The digest the kernel was configured to expect; sha256 is accepted everywhere if that cannot be read.
hash=""
for c in "$mods/$kver/build/.config" "/boot/config-$kver"; do
    if [ -r "$c" ]; then
        hash=$(sed -n 's/^CONFIG_MODULE_SIG_HASH="\(.*\)"$/\1/p' "$c" | head -n 1)
        [ -n "$hash" ] && break
    fi
done
[ -n "$hash" ] || hash=sha256

find "$mods/$kver" -name 'ohman-wmi.ko*' 2>/dev/null | while IFS= read -r f; do
    # Kernels that compress their modules get the module compressed by DKMS too. The signature goes on
    # the uncompressed file, which is then packed again the way the kernel's own loader can read.
    case "$f" in
        *.ko)     ko="$f"; pack="" ;;
        *.ko.xz)  ko="${f%.xz}";  xz -d -f "$f" || continue;   pack="xz --check=crc32 --lzma2=dict=1MiB -f" ;;
        *.ko.zst) ko="${f%.zst}"; zstd -q -d -f --rm "$f" || continue; pack="zstd -q -f --rm -19" ;;
        *.ko.gz)  ko="${f%.gz}";  gzip -d -f "$f" || continue; pack="gzip -9 -f" ;;
        *)        continue ;;
    esac
    if tail -c 28 "$ko" | grep -qa 'Module signature appended'; then
        echo "ohman-wmi: $kver is signed already"
    elif "$signfile" "$hash" "$key" "$cert" "$ko"; then
        echo "ohman-wmi: signed for $kver with the key in $dir"
    else
        echo "ohman-wmi: signing for $kver failed, the module stays unsigned" >&2
    fi
    if [ -n "$pack" ]; then $pack "$ko"; fi
done
exit 0
