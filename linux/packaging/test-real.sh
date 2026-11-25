#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Runs install.sh and uninstall.sh in their REAL mode (no --root) without root and without touching the
# machine: inside a user namespace where this user is uid 0, with throw-away overlays over /etc and /usr/bin,
# empty tmpfs mounts on /opt, /var/lib, /usr/src, /lib/modules, /run and the directories written to below /etc and /usr/share, a private process list, and
# stand-ins on PATH for everything that would change the system (dkms, systemctl, modprobe, mokutil,
# udevadm, groupadd, usermod ...). The stand-ins log what they were asked and keep just enough state for
# the scripts to see their own effects (dkms status, a module file, the helper's socket).
#
#   linux/packaging/test-real.sh RELEASE_DIR      an unpacked release, or linux/ with --bin given below
#   OHMAN_TEST_BIN=DIR linux/packaging/test-real.sh linux
#
# Needs unshare with user namespaces and overlayfs in them (any kernel since 5.11). It exits non-zero on the
# first assertion that does not hold. What it cannot show is in docs/linux.md: a real DKMS, a real systemd,
# a real Secure Boot.

set -euo pipefail

if [ "${1:-}" != --inside ]; then
    REL=$(realpath "${1:?give the unpacked release directory}")
    exec unshare -Urm --pid --fork --mount-proc bash "$0" --inside "$REL" "${OHMAN_TEST_BIN:-}"
fi
REL=$2; BIN=${3:-}
[ -n "$BIN" ] || BIN=$REL/bin
KVER=$(uname -r)
OTHER=9.9.9-other

W=$(mktemp -d)
mount -t tmpfs tmpfs "$W"
over() {   # an empty writable layer over a real directory
    local d=$1 n; n=$(printf '%s' "$d" | tr / _)
    mkdir -p "$W/$n/up" "$W/$n/work"
    mount -t overlay overlay -o "lowerdir=$d,upperdir=$W/$n/up,workdir=$W/$n/work,userxattr" "$d"
}
for d in /etc /usr/bin; do over "$d"; done
# Nothing that is already in these matters to the test, and some of them cannot carry an overlay (WSL keeps
# its kernel headers on a mount of its own): they start empty.
for d in /opt /var/lib /usr/src /usr/lib/modules; do mount -t tmpfs tmpfs "$d"; done
# Directories below the overlays still belong to the real root and cannot be written into from here; the
# ones the installer writes into are replaced by empty ones as well.
for d in /usr/share/applications /usr/share/icons /etc/udev/rules.d /etc/systemd/system /etc/modules-load.d; do
    [ -d "$d" ] || mkdir -p "$d"
    mount -t tmpfs tmpfs "$d"
done
mount -t tmpfs tmpfs /run

LOG=$W/calls.log; : >"$LOG"
STATE=$W/state; mkdir -p "$STATE"
STUBS=$W/stubs; mkdir -p "$STUBS"
export LOG STATE KVER OHMAN_TEST_DKMS_FORMAT=${OHMAN_TEST_DKMS_FORMAT:-3}

stub() { cat >"$STUBS/$1"; chmod 755 "$STUBS/$1"; }

for c in udevadm update-desktop-database gtk-update-icon-cache depmod journalctl; do
    stub "$c" <<'X'
#!/bin/bash
echo "$(basename "$0") $*" >>"$LOG"
X
done

stub modprobe <<'X'
#!/bin/bash
echo "modprobe $*" >>"$LOG"
case " $* " in
    *" -r "*) exit 0 ;;
    *" ohman-wmi "*) [ -e "$STATE/modprobe-fails" ] && { cat "$STATE/modprobe-fails" >&2; exit 1; } ;;
esac
exit 0
X

stub groupadd <<'X'
#!/bin/bash
echo "groupadd $*" >>"$LOG"
# gid 0: the only group id that exists inside this namespace, so the socket can really belong to it.
echo "${!#}:x:0:" >>/etc/group
X
stub groupdel <<'X'
#!/bin/bash
echo "groupdel $*" >>"$LOG"
sed -i "/^$1:/d" /etc/group
X
stub usermod <<'X'
#!/bin/bash
echo "usermod $*" >>"$LOG"
[ "${!#}" = nosuchuser ] && { echo "usermod: user 'nosuchuser' does not exist" >&2; exit 6; }
sed -i "s/^\($2:x:[0-9]*:\)\(.*\)/\1\2${!#}/" /etc/group
X

# dkms: status in the format of DKMS 2 or 3, add/install/remove with real files where the scripts look.
stub dkms <<'X'
#!/bin/bash
echo "dkms $*" >>"$LOG"
S=$STATE/dkms; touch "$S"
cmd=$1; shift
m=""; v=""; k=$KVER; all=0
while [ $# -gt 0 ]; do case "$1" in -m) m=$2; shift;; -v) v=$2; shift;; -k) k=$2; shift;; --all) all=1;; */*) m=${1%/*}; v=${1#*/};; esac; shift; done
case "$cmd" in
    status)
        while read -r sm sv sk st; do
            if [ "$OHMAN_TEST_DKMS_FORMAT" = 2 ]; then echo "$sm, $sv, $sk, x86_64: $st"
            else echo "$sm/$sv, $sk, x86_64: $st"; fi
        done <"$S" ;;
    add)
        [ -d "/usr/src/$m-$v" ] || { echo "Error! Could not find module source directory." >&2; exit 2; }
        [ -e "/var/lib/dkms/$m/$v" ] && { echo "Error! DKMS tree already contains: $m-$v" >&2; exit 3; }
        mkdir -p "/var/lib/dkms/$m/$v" ;;
    install)
        [ -e "$STATE/dkms-build-fails" ] && { echo "Error! Bad return status for module build"; exit 10; }
        [ -e "$STATE/dkms-fails-for-$k" ] && exit 10
        mkdir -p "/lib/modules/$k/updates/dkms"
        ko=/lib/modules/$k/updates/dkms/$m.ko
        head -c 4096 /dev/urandom >"$ko"
        [ -e "$STATE/dkms-signs" ] && printf '~Module signature appended~\n' >>"$ko"
        sed -i "/^$m $v $k /d" "$S"; echo "$m $v $k installed" >>"$S"
        # what DKMS does after installing: the POST_INSTALL script, from the source tree
        post=$(sed -n 's/^POST_INSTALL="\([^ "]*\).*/\1/p' "/usr/src/$m-$v/dkms.conf")
        [ -n "$post" ] && (cd "/usr/src/$m-$v" && sh "./$post" "$k")
        echo "$m.ko: installed for $k" ;;
    remove)
        sed -i "/^$m $v /d" "$S"
        rm -f /lib/modules/*/updates/dkms/"$m".ko*
        rm -rf "/var/lib/dkms/$m/$v"; rmdir "/var/lib/dkms/$m" 2>/dev/null || true ;;
esac
exit 0
X

# systemctl: remembers whether the helper is "running"; starting it makes the socket the real one makes.
stub systemctl <<'X'
#!/bin/bash
echo "systemctl $*" >>"$LOG"
A=$STATE/helper-active
case "$1" in
    is-active) [ -e "$A" ] ;;
    cat)       [ -e /etc/systemd/system/ohman-helper.service ] ;;
    stop)      rm -f "$A"; rm -rf /run/ohman ;;
    disable)   rm -f "$A"; rm -rf /run/ohman ;;
    restart)
        [ -e "$STATE/helper-fails" ] && exit 1
        touch "$A"
        [ -e "$STATE/helper-dies" ] && { rm -f "$A"; exit 0; }
        [ -e "$STATE/helper-no-socket" ] && exit 0
        mkdir -p /run/ohman
        python3 -c 'import socket; socket.socket(socket.AF_UNIX).bind("/run/ohman/helper.sock")'
        chmod "$(cat "$STATE/socket-mode" 2>/dev/null || echo 660)" /run/ohman/helper.sock ;;
esac
X

stub mokutil <<'X'
#!/bin/bash
echo "mokutil $*" >>"$LOG"
case "$1" in
    --sb-state)      echo "SecureBoot enabled" ;;
    --list-enrolled) cat "$STATE/mok-enrolled" 2>/dev/null ;;
    --list-new)      cat "$STATE/mok-new" 2>/dev/null ;;
    --import)        exit 1 ;;
esac
exit 0
X

export PATH=$STUBS:$PATH

# A kernel with headers for the running kernel, and a second installed kernel with headers.
for k in "$KVER" "$OTHER"; do
    mkdir -p "/lib/modules/$k/build/scripts"
    echo "# headers" >"/lib/modules/$k/build/Makefile"
    echo 'CONFIG_MODULE_SIG_HASH="sha512"' >"/lib/modules/$k/build/.config"
    cat >"/lib/modules/$k/build/scripts/sign-file" <<'X'
#!/bin/sh
printf '~Module signature appended~\n' >>"$4"
X
    chmod 755 "/lib/modules/$k/build/scripts/sign-file"
done

FAILS=0
pass() { printf '  PASS  %s\n' "$*"; }
fail() { printf '  FAIL  %s\n' "$*"; FAILS=$((FAILS + 1)); }
check() { local what=$1; shift; if "$@" >/dev/null 2>&1; then pass "$what"; else fail "$what"; fi; }
nope()  { local what=$1; shift; if "$@" >/dev/null 2>&1; then fail "$what"; else pass "$what"; fi; }
called() { grep -q -- "$1" "$LOG"; }
section() { printf '\n== %s\n' "$*"; : >"$LOG"; }

install_run() {   # runs the real install.sh as "root"; output kept in $W/out
    set +e
    SUDO_USER=tester bash "$REL/install.sh" --bin "$BIN" "$@" >"$W/out" 2>&1
    RC=$?
    set -e
}
# The account files belong to the real root, which this namespace cannot write to; copies of them can be
# put in their place, and from then on they are ours.
for f in passwd group; do cp "/etc/$f" "$W/$f"; rm -f "/etc/$f"; cp "$W/$f" "/etc/$f"; done
echo "tester:x:1000:1000::/home/tester:/bin/sh" >>/etc/passwd

tree_is_installed() {
    check "/opt/ohman has the programs"              test -x /opt/ohman/ohman-probe
    check "/opt/ohman/uninstall.sh and install.sh"   test -x /opt/ohman/uninstall.sh -a -x /opt/ohman/install.sh
    check "/usr/bin/ohman-probe -> /opt/ohman"       test "$(readlink /usr/bin/ohman-probe)" = /opt/ohman/ohman-probe
    check "udev rule"                                test -f /etc/udev/rules.d/70-ohman.rules
    check "unit file"                                test -f /etc/systemd/system/ohman-helper.service
    check "modules-load lists msr"                   grep -qx msr /etc/modules-load.d/ohman-wmi.conf
    check "group ohman, tester in it"                sh -c 'getent group ohman | grep -q tester'
    check "everything in /opt/ohman is world-readable" sh -c '[ -z "$(find /opt/ohman ! -perm -004)" ]'
    check "nothing in /opt/ohman is group/world-writable" sh -c '[ -z "$(find /opt/ohman -perm /022 ! -type l)" ]'
}
tree_is_gone() {
    local left
    left=$(find /opt/ohman /opt/ohman.new /opt/ohman.old /usr/bin/ohman /usr/bin/ohman-probe /usr/src/ohman-wmi-* \
        /etc/udev/rules.d/70-ohman.rules /etc/systemd/system/ohman-helper.service /etc/modules-load.d/ohman-wmi.conf \
        /usr/share/applications/ohman.desktop /usr/share/icons/hicolor/256x256/apps/ohman.png /run/ohman \
        /var/lib/dkms/ohman-wmi 2>/dev/null; find /lib/modules -name 'ohman-wmi.ko*' 2>/dev/null) || true
    if [ -z "$left" ]; then pass "nothing of Ohman is left on the system"; else fail "left behind: $left"; fi
    nope "group ohman is gone" getent group ohman
}

# ---------------------------------------------------------------------------------------------------------
section "1. fresh install, Secure Boot not enforced, files unpacked under umask 077"
SRC077=$W/rel077; (umask 077; cp -r "$BIN" "$SRC077")
( umask 077; set +e; SUDO_USER=tester bash "$REL/install.sh" --bin "$SRC077" --no-sign >"$W/out" 2>&1; echo $? >"$W/rc" )
RC=$(cat "$W/rc")
check "install.sh exits 0"                       test "$RC" = 0
[ "$RC" = 0 ] || { sed 's/^/      /' "$W/out"; echo "the first install did not work; the rest would say nothing"; exit 1; }
tree_is_installed
check "modules-load lists ohman-wmi"             grep -qx ohman-wmi /etc/modules-load.d/ohman-wmi.conf
check "DKMS source tree with the signing hook"   test -x /usr/src/ohman-wmi-*/sign.sh
check "dkms.conf keeps the driver's CLEAN line"  grep -q '^CLEAN=' /usr/src/ohman-wmi-*/dkms.conf
check "dkms.conf has POST_INSTALL once"          test "$(grep -c '^POST_INSTALL=' /usr/src/ohman-wmi-*/dkms.conf)" = 1
check "module built for the running kernel"      test -f "/lib/modules/$KVER/updates/dkms/ohman-wmi.ko"
check "module built for the other kernel too"    test -f "/lib/modules/$OTHER/updates/dkms/ohman-wmi.ko"
check "modprobe msr was run"                     called "^modprobe msr"
check "modprobe ohman-wmi was run"               called "^modprobe ohman-wmi"
check "service enabled and restarted"            sh -c "grep -q 'systemctl enable' $LOG && grep -q 'systemctl restart' $LOG"
check "reports the helper running with its socket" grep -q "is running; /run/ohman/helper.sock is root:ohman 0660" "$W/out"
check "tells the user to log out and in"         grep -q "LOG OUT" "$W/out"
sed -n '/^Done/,$p' "$W/out" | sed 's/^/      /'

section "2. installed copy: --check, against dkms status in both formats"
for fmt in 3 2; do
    out=$(OHMAN_TEST_DKMS_FORMAT=$fmt SUDO_USER=tester bash /opt/ohman/install.sh --check 2>&1 || true)
    if printf '%s\n' "$out" | grep -q "module ohman-wmi .*built for $KVER"; then pass "DKMS $fmt format: reports the module built"
    else fail "DKMS $fmt format: $(printf '%s\n' "$out" | grep 'module ohman-wmi')"; fi
done
printf '%s\n' "$out" | sed -n '/^Installed now/,$p' | sed 's/^/      /'
nope "the installed copy refuses to install" bash /opt/ohman/install.sh

section "3. run again: an upgrade that rebuilds nothing"
install_run --no-sign
check "exits 0"                                  test "$RC" = 0
check "says it is not rebuilding"                grep -q "not rebuilding" "$W/out"
nope  "dkms install was not called for $KVER"    grep -q "dkms install.*-k $KVER" "$LOG"
check "helper stopped first, restarted after"    sh -c "grep -n 'systemctl' $LOG | grep -q stop && grep -q 'systemctl restart' $LOG"
check "tester is already a member (no usermod)"  sh -c "! grep -q usermod $LOG"
tree_is_installed

section "4. the module source changed: rebuilt, old version removed"
sed -i 's/^PACKAGE_VERSION=.*/PACKAGE_VERSION="0.0-old"/' /usr/src/ohman-wmi-*/dkms.conf
old=$(echo /usr/src/ohman-wmi-*); mv "$old" /usr/src/ohman-wmi-0.0-old
sed -i 's/^ohman-wmi [^ ]* /ohman-wmi 0.0-old /' "$STATE/dkms"; mkdir -p /var/lib/dkms/ohman-wmi/0.0-old
install_run --no-sign
check "exits 0"                                  test "$RC" = 0
check "old version removed from DKMS"            called "dkms remove -m ohman-wmi -v 0.0-old --all"
nope  "old source tree is gone"                  test -e /usr/src/ohman-wmi-0.0-old
check "new version built"                        test -f "/lib/modules/$KVER/updates/dkms/ohman-wmi.ko"

section "5. things that go wrong must not abort the install"
touch "$STATE/helper-dies"
install_run --no-sign --user nosuchuser
check "exits 1 (something did not work)"         test "$RC" = 1
check "reaches the end"                          grep -q "^Done" "$W/out"
check "reports the user could not be added"      grep -q "could not add nosuchuser" "$W/out"
check "reports the helper did not start"         grep -q "ohman-helper.service did not start" "$W/out"
check "asked the journal why"                    called "journalctl -u ohman-helper.service -n 15"
rm "$STATE/helper-dies"; touch "$STATE/helper-no-socket"
install_run --no-sign
check "missing socket is reported"               grep -q "its socket /run/ohman/helper.sock is not there" "$W/out"
rm "$STATE/helper-no-socket"; echo 666 >"$STATE/socket-mode"
install_run --no-sign
check "wrong socket mode is reported"            grep -q "helper.sock has owner:group mode 0:0 666" "$W/out"
rm "$STATE/socket-mode"
echo "modprobe: ERROR: could not insert 'ohman_wmi': No such device" >"$STATE/modprobe-fails"
sed -i 's/^ohman-wmi.*//' "$STATE/dkms"   # force a rebuild so the module is loaded again
install_run --no-sign
check "no HP mailbox: a warning, exit 0"         sh -c "[ $RC = 0 ] && grep -q 'no HP BIOS mailbox' $W/out"
rm "$STATE/modprobe-fails"
touch "$STATE/dkms-build-fails"; sed -i 's/^ohman-wmi.*//' "$STATE/dkms"; rm -rf /var/lib/dkms/ohman-wmi/*
install_run --no-sign
check "failed build: reported, rest installed"   sh -c "[ $RC = 1 ] && grep -q 'the module did not build' $W/out && grep -q '^Done' $W/out"
check "failed build: msr still in modules-load"  grep -qx msr /etc/modules-load.d/ohman-wmi.conf
nope  "failed build: ohman-wmi not in modules-load" grep -qx ohman-wmi /etc/modules-load.d/ohman-wmi.conf
check "failed build: msr still loaded for the helper" called "^modprobe msr"
rm "$STATE/dkms-build-fails"
mkdir -p /var/lib/dkms/ohman-wmi/stale-debris; : >"$STATE/dkms"
install_run --no-sign
check "stale DKMS tree cleared, install works"   sh -c "[ $RC = 0 ] && grep -q 'clearing a stale' $W/out"
touch "$STATE/dkms-fails-for-$OTHER"; : >"$STATE/dkms"; rm -rf /var/lib/dkms/ohman-wmi
install_run --no-sign
check "other kernel fails to build: warned, exit 0" sh -c "[ $RC = 0 ] && grep -q 'could not build for $OTHER' $W/out"
rm "$STATE/dkms-fails-for-$OTHER"

section "6. --no-module on a machine that has the module keeps it in modules-load"
install_run --no-module
check "exits 0"                                  test "$RC" = 0
check "ohman-wmi still listed"                   grep -qx ohman-wmi /etc/modules-load.d/ohman-wmi.conf
check "msr loaded before the helper"             called "^modprobe msr"

section "7. real uninstall"
set +e; SUDO_USER=tester bash /opt/ohman/uninstall.sh >"$W/out" 2>&1; RC=$?; set -e
check "uninstall.sh exits 0"                     test "$RC" = 0
check "reaches the end"                          grep -q "Ohman is removed" "$W/out"
check "service disabled"                         called "systemctl disable --now ohman-helper.service"
check "dkms remove for all kernels"              called "dkms remove -m ohman-wmi .* --all"
check "module unloaded"                          called "modprobe -r ohman-wmi"
tree_is_gone
[ "$RC" = 0 ] || sed 's/^/      /' "$W/out"

section "8. Secure Boot enforced, DKMS does not sign: own key, hook signs every kernel, import needs a terminal"
install_run --sign </dev/null
check "key made, root only"                      sh -c '[ "$(stat -c %a /var/lib/ohman/mok)" = 700 ] && [ "$(stat -c %a /var/lib/ohman/mok/ohman-mok.key)" = 600 ]'
check "module for the running kernel is signed"  sh -c "tail -c 28 /lib/modules/$KVER/updates/dkms/ohman-wmi.ko | grep -qa 'signature appended'"
check "module for the other kernel is signed"    sh -c "tail -c 28 /lib/modules/$OTHER/updates/dkms/ohman-wmi.ko | grep -qa 'signature appended'"
check "tried to import the key"                  called "mokutil --import /var/lib/ohman/mok/ohman-mok.der"
check "says how to import by hand"               grep -q "sudo mokutil --import /var/lib/ohman/mok/ohman-mok.der" "$W/out"
nope  "module not loaded while untrusted"        grep -q "^modprobe ohman-wmi" "$LOG"

section "9. the same key enrolled: no reboot, module loaded"
fp=$(openssl x509 -inform DER -in /var/lib/ohman/mok/ohman-mok.der -noout -fingerprint -sha1 | sed 's/.*=//' | tr 'A-F' 'a-f')
printf '[key 1]\nSHA1 Fingerprint: %s\n' "$fp" >"$STATE/mok-enrolled"
install_run --sign </dev/null
check "exits 0"                                  test "$RC" = 0
check "reports the key enrolled"                 grep -q "its key is enrolled" "$W/out"
nope  "does not ask for a reboot"                grep -q "REBOOT" "$W/out"
out=$(SUDO_USER=tester bash /opt/ohman/install.sh --check --sign 2>&1 || true)
if printf '%s\n' "$out" | grep -q "module signed; its key is enrolled"; then pass "--check reports signed and enrolled"; else fail "--check: $(printf '%s\n' "$out" | grep -i signed)"; fi

section "10. uninstall keeps the key; --purge removes it"
SUDO_USER=tester bash /opt/ohman/uninstall.sh >"$W/out" 2>&1 || fail "uninstall exit"
check "key kept"                                 test -f /var/lib/ohman/mok/ohman-mok.key
check "says it kept it"                          grep -q "/var/lib/ohman" "$W/out"
install_run --sign </dev/null
SUDO_USER=tester bash /opt/ohman/uninstall.sh --purge </dev/null >"$W/out" 2>&1 || fail "purge exit"
nope  "key removed by --purge"                   test -e /var/lib/ohman
tree_is_gone

section "11. --root that is really the root is refused"
for r in / // /. /usr/..; do
    nope "install.sh --root $r"   bash "$REL/install.sh" --bin "$BIN" --root "$r"
    nope "uninstall.sh --root $r" bash "$REL/uninstall.sh" --root "$r"
done
check "sh install.sh --help hands over to bash" sh "$REL/install.sh" --help

echo
if [ "$FAILS" = 0 ]; then echo "ALL PASSED"; else echo "$FAILS FAILED"; exit 1; fi
