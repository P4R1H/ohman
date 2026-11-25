#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Removes everything install.sh put on the machine.
#
#   sudo ./uninstall.sh            remove Ohman; keep your settings and the module signing key
#   sudo ./uninstall.sh --purge    remove those too
#   ./uninstall.sh --root DIR      undo a staged install in DIR, running nothing that changes the system
#
# The installer leaves a copy of this file in /opt/ohman, so it works without the release it came from.

# "sudo sh uninstall.sh" lands in dash on Debian and Ubuntu; this line is plain sh and hands over to bash.
if [ -z "${BASH_VERSION:-}" ]; then exec bash "$0" "$@"; fi

set -euo pipefail

PREFIX=/opt/ohman
MODULE=ohman-wmi
GROUP=ohman
SERVICE=ohman-helper.service
STATE_DIR=/var/lib/ohman
OUR_CERT=$STATE_DIR/mok/ohman-mok.der

ROOT=""
STAGED=0
PURGE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --purge)   PURGE=1 ;;
        --root)    [ $# -ge 2 ] || { echo "--root needs a directory" >&2; exit 2; }
                   ROOT=$2; STAGED=1; shift ;;
        --root=*)  ROOT=${1#--root=}; STAGED=1 ;;
        -h|--help) sed -n '3,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)         echo "uninstall.sh: unknown option $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

# A staged root is a directory of its own. "/", "//", "/." and the like are the real system.
if [ "$STAGED" = 1 ]; then
    [ -n "$ROOT" ] || { echo "--root needs a directory" >&2; exit 2; }
    ROOT=$(realpath -m -- "$ROOT") || { echo "--root: cannot make sense of that directory" >&2; exit 2; }
    if [ "$ROOT" = / ]; then
        echo "--root / is the real system: leave --root out for that" >&2; exit 2
    fi
fi
if [ "$STAGED" = 0 ] && [ "$(id -u)" != 0 ]; then
    echo "uninstall.sh: this has to run as root:  sudo $0" >&2; exit 1
fi

if [ -t 1 ]; then B=$'\033[1m'; N=$'\033[0m'; else B=""; N=""; fi
step() { printf '\n%s%s%s\n' "$B" "$*" "$N"; }
say()  { printf '  %s\n' "$*"; }
warn() { printf '  warning: %s\n' "$*" >&2; }
have() { command -v "$1" >/dev/null 2>&1; }

sys() {
    if [ "$STAGED" = 1 ]; then
        printf '  would run: %s\n' "$*"
    else
        "$@"
    fi
}

# Removes a file or directory and says so; silent if it was not there. ROOT is empty on a real run and a
# directory on a staged one; the path is never empty, so this can never be "rm -rf" of the root itself.
gone() {
    local p
    for p in "$@"; do
        [ -n "$p" ] && [ "$p" != / ] || continue
        if [ -e "$ROOT$p" ] || [ -L "$ROOT$p" ]; then
            rm -rf -- "$ROOT$p"
            say "removed $p"
        fi
    done
}

TARGET_USER=${SUDO_USER:-}
[ "$TARGET_USER" = root ] && TARGET_USER=""
USER_HOME=""
# A staged tree has nobody's home directory in it; the real one is not this run's business.
if [ -n "$TARGET_USER" ] && [ "$STAGED" = 0 ]; then
    USER_HOME=$(getent passwd "$TARGET_USER" 2>/dev/null | cut -d: -f6 || true)
fi

# ---------------------------------------------------------------------------------------------------------
step "Stopping Ohman"

# The window first: on a clean exit it hands the fans and the keyboard back to the firmware itself.
if [ "$STAGED" = 0 ] && pgrep -x ohman >/dev/null 2>&1; then
    say "asking the running Ohman to quit"
    pkill -TERM -x ohman 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -x ohman >/dev/null 2>&1 || break
        sleep 0.5
    done
fi

# Then the fans, whatever the window managed to do: the kernel's own hp-wmi driver gets them back in
# automatic. (A fan level the firmware was given expires by itself after two minutes in any case.)
if [ "$STAGED" = 0 ]; then
    for f in /sys/devices/platform/hp-wmi/hwmon/hwmon*/pwm1_enable; do
        [ -w "$f" ] || continue
        if echo 2 >"$f" 2>/dev/null; then say "fans back to automatic (hp-wmi)"; fi
    done
fi

if have systemctl || [ "$STAGED" = 1 ]; then
    sys systemctl disable --now "$SERVICE" 2>/dev/null || true
fi
gone "/etc/systemd/system/$SERVICE"
if have systemctl || [ "$STAGED" = 1 ]; then
    sys systemctl daemon-reload || true
fi
gone /run/ohman

# ---------------------------------------------------------------------------------------------------------
step "Kernel module"

gone "/etc/modules-load.d/$MODULE.conf"
sys modprobe -r "$MODULE" 2>/dev/null || true
if [ "$STAGED" = 1 ]; then
    sys dkms remove "$MODULE/VERSION" --all
elif have dkms; then
    # Every version, for every kernel it was built for.
    for v in $(dkms status 2>/dev/null | sed -n "s|^$MODULE[/,] *\([^,:]*\)[,:].*|\1|p" | sort -u); do
        if dkms remove -m "$MODULE" -v "$v" --all >/dev/null 2>&1; then
            say "removed $MODULE $v from DKMS, for all kernels"
        else
            warn "dkms remove $MODULE/$v did not work; look at: dkms status"
        fi
    done
fi
# What an interrupted build can leave in DKMS's own tree, which "dkms remove" no longer knows about.
if [ "$STAGED" = 0 ]; then gone "/var/lib/dkms/$MODULE"; fi
for d in "$ROOT"/usr/src/"$MODULE"-*; do
    [ -d "$d" ] || continue
    gone "${d#"$ROOT"}"
done
# What a DKMS that is gone, or a build by hand, may have left in the module directories.
if [ "$STAGED" = 0 ]; then
    left=$(find /lib/modules -name "$MODULE.ko*" 2>/dev/null || true)
    if [ -n "$left" ]; then
        printf '%s\n' "$left" | while IFS= read -r f; do
            rm -f -- "$f"; say "removed $f"
            # The module index of the kernel that file belonged to, not only the running one's.
            k=${f#/lib/modules/}; k=${k%%/*}
            depmod -a "$k" 2>/dev/null || true
        done
    fi
fi

# ---------------------------------------------------------------------------------------------------------
step "Files"

gone /usr/bin/ohman /usr/bin/ohman-probe
gone /usr/share/applications/ohman.desktop /usr/share/icons/hicolor/256x256/apps/ohman.png
gone /etc/udev/rules.d/70-ohman.rules
gone "$PREFIX" "$PREFIX.new" "$PREFIX.old"

if have udevadm || [ "$STAGED" = 1 ]; then
    sys udevadm control --reload || true
    sys udevadm trigger --subsystem-match=hidraw --action=change || true
fi
if have update-desktop-database || [ "$STAGED" = 1 ]; then
    sys update-desktop-database -q "$ROOT/usr/share/applications" || true
fi
if have gtk-update-icon-cache || [ "$STAGED" = 1 ]; then
    sys gtk-update-icon-cache -q -t -f "$ROOT/usr/share/icons/hicolor" || true
fi

# Ohman's own "start at login" entry would point at a program that is gone.
if [ -n "$USER_HOME" ] && [ -e "$USER_HOME/.config/autostart/ohman.desktop" ]; then
    rm -f "$USER_HOME/.config/autostart/ohman.desktop"
    say "removed $USER_HOME/.config/autostart/ohman.desktop"
fi

step "Group"
if [ "$STAGED" = 1 ]; then
    sys groupdel "$GROUP"
elif getent group "$GROUP" >/dev/null 2>&1; then
    if groupdel "$GROUP" 2>/dev/null; then say "removed the group $GROUP"; else warn "could not remove the group $GROUP"; fi
fi

# ---------------------------------------------------------------------------------------------------------
# What stays unless --purge: the things that are yours rather than the program's.

user_dirs() {
    [ -n "$USER_HOME" ] || return 0
    printf '%s\n' "$USER_HOME/.config/ohman" "$USER_HOME/.local/state/ohman" "$USER_HOME/.local/share/ohman"
}

key_enrolled() {
    local fp list
    have mokutil && have openssl && [ -r "$OUR_CERT" ] || return 1
    fp=$(openssl x509 -inform DER -in "$OUR_CERT" -noout -fingerprint -sha1 2>/dev/null) || return 1
    fp=${fp#*=}; fp=${fp//:/}; fp=${fp,,}
    list=$(mokutil --list-enrolled 2>/dev/null | tr -d ': \t' | tr '[:upper:]' '[:lower:]' || true)
    case "$list" in *"sha1fingerprint$fp"*) return 0 ;; esac
    return 1
}

if [ "$PURGE" = 1 ]; then
    step "Purge"
    if [ "$STAGED" = 0 ] && key_enrolled; then
        say "Ohman's module signing key is enrolled with Secure Boot. To take it out again, mokutil asks"
        say "for a one-time password, and for it once more on the next boot (Delete MOK)."
        if [ -t 0 ]; then
            mokutil --delete "$OUR_CERT" || warn "the key stays enrolled; it is harmless without its private half"
        elif (: </dev/tty) 2>/dev/null; then
            mokutil --delete "$OUR_CERT" </dev/tty || warn "the key stays enrolled; it is harmless without its private half"
        else
            warn "no terminal to ask on: the key stays enrolled. It is harmless without its private half."
        fi
    fi
    gone "$STATE_DIR"
    while IFS= read -r d; do
        [ -n "$d" ] || continue
        if [ -d "$d" ]; then rm -rf "$d"; say "removed $d"; fi
    done < <(user_dirs)
    say "Other users' settings, if any, are in their own ~/.config/ohman, ~/.local/state/ohman, ~/.local/share/ohman."
fi

step "Ohman is removed"
if [ "$PURGE" = 0 ]; then
    kept=0
    while IFS= read -r d; do
        [ -n "$d" ] && [ -d "$d" ] || continue
        [ "$kept" = 1 ] || say "Left in place (remove with --purge, or by hand):"
        kept=1
        say "  $d   (your settings, log and benchmark runs)"
    done < <(user_dirs)
    if [ -d "$ROOT$STATE_DIR" ]; then
        [ "$kept" = 1 ] || say "Left in place (remove with --purge, or by hand):"
        say "  $STATE_DIR   (the module signing key, so a reinstall needs no new enrolment)"
    fi
fi
say "A signing key that DKMS or the distribution owns (/var/lib/dkms, /var/lib/shim-signed) is not Ohman's"
say "and is never touched: other modules on this machine are signed with it."
say "The packages the installer asked for (dkms, kernel headers, mokutil) are still installed."
say "hp-wmi, the kernel's own HP driver, was never changed."
