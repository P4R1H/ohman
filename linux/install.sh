#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Installs Ohman on Linux from an unpacked release (or from linux/ in the repository after publish.sh).
#
#   sudo ./install.sh                  install, or upgrade what is there
#   ./install.sh --check               say what is present and what is missing; change nothing
#   sudo ./install.sh --install-deps   also install the missing build packages with the distro's package manager
#   sudo ./install.sh --no-module      everything except the kernel module
#   sudo ./install.sh --uninstall      remove it again (the same as uninstall.sh)
#   ./install.sh --root DIR            install into DIR as if it were /, running nothing that changes the system
#
# What lands where is written down in docs/linux.md. Run twice, it upgrades: the helper is stopped, files are
# replaced, the module is rebuilt only if its source changed, the helper is started again.

# "sudo sh install.sh" lands in dash on Debian and Ubuntu, which knows neither pipefail nor arrays. This
# line is plain sh so that it is understood there, and hands over to bash.
if [ -z "${BASH_VERSION:-}" ]; then exec bash "$0" "$@"; fi

set -euo pipefail

# Whatever umask root has here (077 on hardened systems), what gets installed must be readable by the user
# who runs Ohman.
umask 022

PREFIX=/opt/ohman
MODULE=ohman-wmi
GROUP=ohman
SERVICE=ohman-helper.service
MOK_DIR=/var/lib/ohman/mok
SOCKET=/run/ohman/helper.sock

# Temporary files and directories, removed however the script ends.
TEMPS=()
cleanup() { [ ${#TEMPS[@]} -eq 0 ] || rm -rf -- "${TEMPS[@]}"; }
trap cleanup EXIT
REPO_URL=https://github.com/P4R1H/Ohman

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

ROOT=""
STAGED=0
CHECK=0
WITH_MODULE=1
INSTALL_DEPS=0
SIGN=auto
BIN_DIR=""
TARGET_USER=""
UNINSTALL_ARGS=()
DO_UNINSTALL=0

usage() {
    sed -n '3,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<'EOF'

Less common:
  --user NAME    the user to add to the group "ohman" (default: whoever ran sudo)
  --bin DIR      take the binaries from DIR instead of ./bin
  --sign         sign the module even if this kernel does not seem to demand it
  --no-sign      do not sign and do not touch any key
  --purge        with --uninstall: also remove the signing key and your settings
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --check)        CHECK=1 ;;
        --no-module)    WITH_MODULE=0 ;;
        --install-deps) INSTALL_DEPS=1 ;;
        --uninstall)    DO_UNINSTALL=1 ;;
        --purge)        UNINSTALL_ARGS+=(--purge) ;;
        --sign)         SIGN=yes ;;
        --no-sign)      SIGN=no ;;
        --root)         [ $# -ge 2 ] || { echo "--root needs a directory" >&2; exit 2; }
                        ROOT=$2; STAGED=1; shift ;;
        --root=*)       ROOT=${1#--root=}; STAGED=1 ;;
        --user)         [ $# -ge 2 ] || { echo "--user needs a name" >&2; exit 2; }
                        TARGET_USER=$2; shift ;;
        --bin)          [ $# -ge 2 ] || { echo "--bin needs a directory" >&2; exit 2; }
                        BIN_DIR=$2; shift ;;
        -h|--help)      usage; exit 0 ;;
        *)              echo "install.sh: unknown option $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

# A staged root is a directory of its own. "/", "//", "/." and the like are the real system, where staging
# would write real files and only print the commands that go with them.
if [ "$STAGED" = 1 ]; then
    [ -n "$ROOT" ] || { echo "--root needs a directory" >&2; exit 2; }
    ROOT=$(realpath -m -- "$ROOT") || { echo "--root: cannot make sense of that directory" >&2; exit 2; }
    if [ "$ROOT" = / ]; then
        echo "--root / is the real system: leave --root out for that" >&2; exit 2
    fi
fi
if [ "$DO_UNINSTALL" = 1 ]; then
    [ "$STAGED" = 1 ] && UNINSTALL_ARGS+=(--root "$ROOT")
    exec bash "$HERE/uninstall.sh" ${UNINSTALL_ARGS[@]+"${UNINSTALL_ARGS[@]}"}
fi

# ---------------------------------------------------------------------------------------------------------
# Talking

if [ -t 1 ]; then B=$'\033[1m'; N=$'\033[0m'; else B=""; N=""; fi
step() { printf '\n%s%s%s\n' "$B" "$*" "$N"; }
say()  { printf '  %s\n' "$*"; }
ok()   { printf '  ok       %s\n' "$*"; }
miss() { printf '  MISSING  %s\n' "$*"; }
warn() { printf '  warning: %s\n' "$*" >&2; }
die()  { printf '\ninstall.sh: %s\n' "$*" >&2; exit 1; }

# Things that change the running system (services, modules, groups, keys). Under --root they are printed
# instead of run, so a staged install can be made and inspected without root.
sys() {
    if [ "$STAGED" = 1 ]; then
        printf '  would run: %s\n' "$*"
    else
        "$@"
    fi
}

have() { command -v "$1" >/dev/null 2>&1; }

NEXT=()          # what the user still has to do, printed at the end
FAILED=()        # steps that did not work; the exit code says so too
next() { NEXT+=("$*"); }
failed() { FAILED+=("$*"); warn "$*"; }

# ---------------------------------------------------------------------------------------------------------
# What is in this release

DRIVER_DIR=$HERE/driver
PKG_DIR=$HERE/packaging
[ -n "$BIN_DIR" ] || BIN_DIR=$HERE/bin

# The installer leaves a copy of this script in /opt/ohman so that --check is always at hand. That copy
# has no release around it: it can look, and nothing else.
INSTALLED_COPY=0
if [ ! -d "$DRIVER_DIR" ] && [ ! -d "$PKG_DIR" ] && [ -r "$HERE/VERSION" ]; then
    INSTALLED_COPY=1
    if [ "$CHECK" = 0 ]; then
        echo "This copy of install.sh only checks:  $HERE/install.sh --check" >&2
        echo "To install or upgrade, use a release: $REPO_URL/releases/latest" >&2
        exit 1
    fi
fi

VERSION=unknown
if [ -r "$HERE/VERSION" ]; then
    VERSION=$(head -n 1 "$HERE/VERSION")
elif [ -r "$HERE/../src/Meta.cs" ]; then
    VERSION=$(sed -n 's/.*Version = "\([0-9][0-9.]*\)".*/\1/p' "$HERE/../src/Meta.cs" | head -n 1)
fi

MODULE_VERSION=""
if [ -r "$DRIVER_DIR/dkms.conf" ]; then
    MODULE_VERSION=$(sed -n 's/^PACKAGE_VERSION="\{0,1\}\([^"]*\)"\{0,1\}[[:space:]]*$/\1/p' "$DRIVER_DIR/dkms.conf" | head -n 1)
fi

HAVE_GUI=0;    [ -x "$BIN_DIR/ohman" ] && HAVE_GUI=1
HAVE_HELPER=0; [ -x "$BIN_DIR/ohman-helper" ] && HAVE_HELPER=1
HAVE_PROBE=0;  [ -x "$BIN_DIR/ohman-probe" ] && HAVE_PROBE=1

# ---------------------------------------------------------------------------------------------------------
# What this machine is

KVER=$(uname -r)
DISTRO_ID=unknown; DISTRO_LIKE=""; DISTRO_NAME="an unknown distribution"
if [ -r /etc/os-release ]; then
    DISTRO_ID=$(. /etc/os-release; printf '%s' "${ID:-unknown}")
    DISTRO_LIKE=$(. /etc/os-release; printf '%s' "${ID_LIKE:-}")
    DISTRO_NAME=$(. /etc/os-release; printf '%s' "${PRETTY_NAME:-${NAME:-unknown}}")
fi
case " $DISTRO_ID $DISTRO_LIKE " in
    *" debian "*|*" ubuntu "*)              FAMILY=debian ;;
    *" fedora "*|*" rhel "*|*" centos "*)   FAMILY=fedora ;;
    *" arch "*)                             FAMILY=arch ;;
    *suse*)                                 FAMILY=suse ;;
    *)                                      FAMILY=unknown ;;
esac

# The headers package that matches the running kernel, by the name this distribution gives it.
headers_package() {
    case "$FAMILY" in
        debian) printf 'linux-headers-%s' "$KVER" ;;
        fedora) printf 'kernel-devel-%s' "$KVER" ;;
        arch)   # Arch kernels say which package they came from: linux, linux-lts, linux-zen, linux612 ...
                if [ -r "/usr/lib/modules/$KVER/pkgbase" ]; then
                    printf '%s-headers' "$(head -n 1 "/usr/lib/modules/$KVER/pkgbase")"
                else
                    printf 'linux-headers'
                fi ;;
        suse)   # 6.4.0-150600.23-default -> kernel-default-devel
                printf 'kernel-%s-devel' "${KVER##*-}" ;;
        *)      printf 'the kernel headers for %s' "$KVER" ;;
    esac
}

pm_install_command() {
    case "$FAMILY" in
        debian) printf 'apt-get install -y' ;;
        fedora) printf 'dnf install -y' ;;
        arch)   printf 'pacman -S --needed --noconfirm' ;;
        suse)   printf 'zypper --non-interactive install' ;;
        *)      return 1 ;;
    esac
}

# Secure Boot: on, off, or unknown (no way to tell from here).
secure_boot_state() {
    local out f byte
    if have mokutil; then
        out=$(mokutil --sb-state 2>&1 || true)
        case "$out" in
            *"SecureBoot enabled"*)  echo on; return ;;
            *"SecureBoot disabled"*) echo off; return ;;
        esac
    fi
    [ -d /sys/firmware/efi ] || { echo off; return; }
    for f in /sys/firmware/efi/efivars/SecureBoot-*; do
        [ -r "$f" ] || continue
        byte=$(od -An -tu1 -j4 -N1 "$f" 2>/dev/null | tr -d ' \n' || true)
        if [ "$byte" = 1 ]; then echo on; else echo off; fi
        return
    done
    echo unknown
}

# Whether this kernel refuses a module without a signature it trusts. Secure Boot alone does not say:
# Fedora, Debian, Ubuntu and openSUSE lock the kernel down when it is on, Arch does not.
kernel_demands_signature() {
    local l
    if [ -r /sys/module/module/parameters/sig_enforce ] &&
       [ "$(cat /sys/module/module/parameters/sig_enforce 2>/dev/null)" = Y ]; then
        return 0
    fi
    if [ -r /sys/kernel/security/lockdown ]; then
        l=$(cat /sys/kernel/security/lockdown 2>/dev/null || true)
        case "$l" in *"[integrity]"*|*"[confidentiality]"*) return 0 ;; esac
        return 1
    fi
    # Lockdown state not readable. Go by what the distribution is known to do.
    [ "$SB" = on ] && [ "$FAMILY" != arch ]
}

SB=$(secure_boot_state)
case "$SIGN" in
    yes)  NEED_SIGN=1 ;;
    no)   NEED_SIGN=0 ;;
    *)    if kernel_demands_signature; then NEED_SIGN=1; else NEED_SIGN=0; fi ;;
esac

IS_HP=unknown
if [ -r /sys/class/dmi/id/sys_vendor ]; then
    case "$(cat /sys/class/dmi/id/sys_vendor 2>/dev/null)" in
        HP*|Hewlett*) IS_HP=yes ;;
        *)            IS_HP=no ;;
    esac
fi

if [ -z "$TARGET_USER" ]; then
    TARGET_USER=${SUDO_USER:-}
    if [ -z "$TARGET_USER" ] && [ -n "${PKEXEC_UID:-}" ]; then
        TARGET_USER=$(id -nu "$PKEXEC_UID" 2>/dev/null || true)
    fi
    if [ -z "$TARGET_USER" ] && [ "$(id -u)" != 0 ]; then
        TARGET_USER=$(id -nu)
    fi
fi
[ "$TARGET_USER" = root ] && TARGET_USER=""

# ---------------------------------------------------------------------------------------------------------
# Prerequisites: what has to be installed before the module can be built and trusted

MISSING_PKGS=()

need_cmd() {   # command, package, why
    if have "$1"; then ok "$1"; else miss "$1  ($3)"; MISSING_PKGS+=("$2"); fi
}

# What the running kernel was compiled with, as the kernel itself says it: the line from its build
# configuration if the headers are installed, otherwise /proc/version.
kernel_compiler_text() {
    local f t
    for f in "/lib/modules/$KVER/build/.config" "/lib/modules/$KVER/build/include/config/auto.conf"; do
        [ -r "$f" ] || continue
        t=$(sed -n 's/^CONFIG_CC_VERSION_TEXT="\(.*\)"$/\1/p' "$f" | head -n 1)
        if [ -n "$t" ]; then printf '%s' "$t"; return 0; fi
    done
    cat /proc/version 2>/dev/null || true
}

# A module has to be built with the compiler its kernel was built with. A kernel built with clang (CachyOS
# and other rolling kernels) needs clang and the LLVM linker and tools; one built with gcc wants the same
# major version of gcc, which matters on Ubuntu's newer-kernel series (kernel by gcc-12, default gcc 11).
check_compiler() {
    local text major have_major
    text=$(kernel_compiler_text)
    case "$text" in
        *clang*)
            need_cmd clang clang "this kernel was built with clang, so the module must be too"
            need_cmd ld.lld lld "the linker that goes with clang"
            need_cmd llvm-ar llvm "the LLVM tools the kernel build uses with clang"
            return 0 ;;
    esac
    major=$(printf '%s\n' "$text" | sed -n 's/.*gcc[^)]*) \([0-9][0-9]*\)\..*/\1/p' | head -n 1)
    if [ -n "$major" ] && have "gcc-$major"; then
        ok "gcc-$major  (the compiler this kernel was built with)"
        return 0
    fi
    if ! have gcc; then
        miss "gcc  (builds the kernel module)"
        MISSING_PKGS+=(gcc)
        return 0
    fi
    have_major=$(gcc -dumpversion 2>/dev/null | cut -d. -f1 || true)
    if [ -z "$major" ] || [ "$major" = "$have_major" ]; then
        ok "gcc${have_major:+ $have_major}"
        return 0
    fi
    ok "gcc $have_major"
    warn "this kernel was built with gcc $major and the gcc here is $have_major. The build usually works anyway."
    if [ "$FAMILY" = debian ]; then
        say "         If it does not:  sudo apt-get install -y gcc-$major   and run this again."
    else
        say "         If it does not, install gcc $major, or update the system so kernel and compiler match."
    fi
}

check_prerequisites() {
    MISSING_PKGS=()
    if [ "$WITH_MODULE" = 1 ]; then
        need_cmd dkms dkms "builds the kernel module, and again for every new kernel"
        need_cmd make make "builds the kernel module"
        if [ -e "/lib/modules/$KVER/build/Makefile" ]; then
            ok "kernel headers for $KVER"
        else
            miss "kernel headers for $KVER  (the module is built against them)"
            MISSING_PKGS+=("$(headers_package)")
        fi
        check_compiler
        if [ "$NEED_SIGN" = 1 ]; then
            need_cmd mokutil mokutil "enrols the module signing key with Secure Boot"
            need_cmd openssl openssl "makes and reads the module signing key"
        fi
    fi
    have systemctl || warn "no systemctl: this installer sets the helper up as a systemd service only"
}

print_missing() {
    local cmd
    [ ${#MISSING_PKGS[@]} -gt 0 ] || return 0
    echo
    if cmd=$(pm_install_command); then
        say "Install what is missing with:"
        say "    sudo $cmd ${MISSING_PKGS[*]}"
        say "or run this installer again with --install-deps and it does that for you."
        if [ "$FAMILY" = fedora ] || [ "$FAMILY" = arch ]; then
            say "If the headers package for $KVER is not on offer, the running kernel is older than the"
            say "one in the repositories: update the system, reboot, and run this again."
        fi
    else
        say "This distribution ($DISTRO_NAME) is not one this installer knows the package names for."
        say "Install, by whatever name it has them: ${MISSING_PKGS[*]}"
    fi
}

install_missing() {
    local cmd
    cmd=$(pm_install_command) || die "do not know this distribution's package manager; install by hand: ${MISSING_PKGS[*]}"
    step "Installing: ${MISSING_PKGS[*]}"
    # shellcheck disable=SC2086  # the command is several words on purpose
    sys $cmd "${MISSING_PKGS[@]}" || die "the package manager could not install them (see above)"
}

# ---------------------------------------------------------------------------------------------------------
# Module signing

OUR_KEY=$MOK_DIR/ohman-mok.key
OUR_CERT=$MOK_DIR/ohman-mok.der

# SHA-1 fingerprint of a certificate (DER or PEM), lowercase, no colons. That is how mokutil lists keys.
cert_fingerprint() {
    local out
    out=$(openssl x509 -inform DER -in "$1" -noout -fingerprint -sha1 2>/dev/null) ||
        out=$(openssl x509 -in "$1" -noout -fingerprint -sha1 2>/dev/null) || return 1
    out=${out#*=}; out=${out//:/}
    printf '%s' "${out,,}"
}

cert_serial() {
    local out
    out=$(openssl x509 -inform DER -in "$1" -noout -serial 2>/dev/null) ||
        out=$(openssl x509 -in "$1" -noout -serial 2>/dev/null) || return 1
    out=${out#*=}
    printf '%s' "${out,,}"
}

# enrolled: the firmware trusts it now.  pending: imported, waiting for the reboot that confirms it.
# absent: neither.
mok_state() {
    local fp list
    fp=$(cert_fingerprint "$1") || { echo absent; return; }
    list=$(mokutil --list-enrolled 2>/dev/null | tr -d ': \t' | tr '[:upper:]' '[:lower:]' || true)
    case "$list" in *"sha1fingerprint$fp"*) echo enrolled; return ;; esac
    list=$(mokutil --list-new 2>/dev/null | tr -d ': \t' | tr '[:upper:]' '[:lower:]' || true)
    case "$list" in *"sha1fingerprint$fp"*) echo pending; return ;; esac
    # Older mokutil prints the lists differently; ask it about this one key instead.
    list=$(mokutil --test-key "$1" 2>&1 || true)
    case "$list" in
        *"already enrolled"*)                   echo enrolled ;;
        *"already in the enrollment request"*)  echo pending ;;
        *)                                      echo absent ;;
    esac
}

# The certificate DKMS signs with on this machine: its own setting, or its default.
dkms_certificate() {
    (
        set +eu
        mok_certificate=""
        for f in /etc/dkms/framework.conf /etc/dkms/framework.conf.d/*.conf; do
            # shellcheck disable=SC1090
            [ -r "$f" ] && . "$f" >/dev/null 2>&1
        done
        printf '%s' "${mok_certificate:-/var/lib/dkms/mok.pub}"
    )
}

installed_module_file() {
    # No such directory is an answer (nothing installed), not an error.
    find "/lib/modules/${1:-$KVER}" -name "$MODULE.ko*" 2>/dev/null | head -n 1 || true
}

module_is_signed() {
    local f=$1
    case "$f" in
        *.xz)  xz -dc "$f" 2>/dev/null | tail -c 28 ;;
        *.zst) zstd -dc "$f" 2>/dev/null | tail -c 28 ;;
        *.gz)  gzip -dc "$f" 2>/dev/null | tail -c 28 ;;
        *)     tail -c 28 "$f" ;;
    esac | grep -qa 'Module signature appended'
}

# Which certificate the installed module was signed with. modinfo names the signing key by its serial
# number; that is matched against the keys a module on this machine can have been signed with.
signer_certificate() {
    local ko=$1 serial c s
    local candidates=("$(dkms_certificate)" /var/lib/dkms/mok.pub /var/lib/shim-signed/mok/MOK.der "$OUR_CERT")
    serial=$(modinfo -F sig_key "$ko" 2>/dev/null | tr -d ': \n' | tr '[:upper:]' '[:lower:]' || true)
    if [ -n "$serial" ]; then
        for c in "${candidates[@]}"; do
            [ -r "$c" ] || continue
            s=$(cert_serial "$c") || continue
            if [ "${s#0}" = "${serial#0}" ]; then printf '%s' "$c"; return 0; fi
        done
        # The module names its key and none of the keys here is that one. Guessing would enrol, or
        # vouch for, a key the module was not signed with.
        return 1
    fi
    # A modinfo that cannot read signatures at all: take the key DKMS is set up to use.
    for c in "${candidates[@]}"; do
        if [ -r "$c" ]; then printf '%s' "$c"; return 0; fi
    done
    return 1
}

make_our_key() {
    local cnf host
    [ -r "$OUR_KEY" ] && [ -r "$OUR_CERT" ] && return 0
    say "Making this machine's module signing key in $MOK_DIR (root only; it never leaves this machine)."
    install -d -m 700 "$MOK_DIR"
    cnf=$(mktemp); TEMPS+=("$cnf")
    # A certificate's name may be 64 characters; a long host name must not be what breaks key making.
    host=$(uname -n | cut -c1-30)
    # The second usage is the "module signing only" marker: the boot loader will not accept this key for
    # anything but kernel modules, so enrolling it does not let it vouch for a kernel or a boot loader.
    cat >"$cnf" <<EOF
[ req ]
default_bits = 2048
distinguished_name = dn
prompt = no
string_mask = utf8only
x509_extensions = v3
[ dn ]
commonName = Ohman module signing key ($host)
[ v3 ]
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always,issuer
basicConstraints = critical,CA:FALSE
extendedKeyUsage = codeSigning,1.3.6.1.4.1.2312.16.1.2
EOF
    (
        umask 077
        openssl req -new -x509 -newkey rsa:2048 -nodes -days 36500 -config "$cnf" \
            -keyout "$OUR_KEY" -outform DER -out "$OUR_CERT" >/dev/null 2>&1
    ) || { rm -f "$cnf" "$OUR_KEY" "$OUR_CERT"; return 1; }
    rm -f "$cnf"
    chmod 600 "$OUR_KEY" "$OUR_CERT"
}

# Ask the firmware to trust a key. mokutil asks for a one-time password, typed by the person at the keyboard:
# it is never chosen, stored or passed on by this script.
import_key() {
    local cert=$1
    echo
    say "Secure Boot is on, so the kernel only loads modules signed by a key the firmware trusts."
    say "The module is signed with this machine's own key:"
    say "    $cert"
    say "That key now has to be enrolled, once. mokutil will ask you to choose a password."
    say "Any password will do; you type it one more time on the next boot and never again."
    echo
    if [ "$STAGED" = 1 ]; then
        sys mokutil --import "$cert"
        return 0
    fi
    if [ -t 0 ]; then
        mokutil --import "$cert" && return 0
    elif (: </dev/tty) 2>/dev/null; then
        # Run through a pipe (curl | sudo bash): the password still comes from the terminal.
        mokutil --import "$cert" </dev/tty && return 0
    else
        warn "no terminal to ask for the password on"
    fi
    return 1
}

ENROL_PENDING=0
MODULE_TRUSTED=1     # 0 while the kernel would refuse the module

handle_signing() {
    local ko cert state
    [ "$NEED_SIGN" = 1 ] || return 0
    step "Secure Boot"

    if [ "$STAGED" = 1 ]; then
        say "A real install would now check that the built module is signed, sign it with a key in"
        say "$MOK_DIR if DKMS did not, and ask you to enrol that key (mokutil --import)."
        return 0
    fi

    ko=$(installed_module_file)
    [ -n "$ko" ] || { failed "the module is not installed for $KVER, nothing to sign"; MODULE_TRUSTED=0; return 0; }

    if module_is_signed "$ko"; then
        cert=$(signer_certificate "$ko") || cert=""
    else
        # DKMS did not sign it (DKMS 2, or no sign-file). Sign it with a key of our own; the same script
        # runs again from DKMS whenever it installs the module for another kernel.
        make_our_key || { failed "could not make a signing key (is openssl installed?)"; MODULE_TRUSTED=0; return 0; }
        OHMAN_MOK_DIR=$MOK_DIR sh "$PKG_DIR/dkms-sign.sh" "$KVER" | sed 's/^/  /'
        ko=$(installed_module_file)
        cert=$OUR_CERT
        if [ -z "$ko" ] || ! module_is_signed "$ko"; then
            failed "the module could not be signed (no sign-file in the kernel headers?)"
            MODULE_TRUSTED=0
            return 0
        fi
    fi

    if [ -z "$cert" ]; then
        failed "the module is signed, but the certificate it was signed with was not found; see docs/linux.md, Secure Boot"
        MODULE_TRUSTED=0
        return 0
    fi

    state=$(mok_state "$cert")
    case "$state" in
        enrolled)
            ok "the module is signed, and its key is enrolled: $cert"
            ;;
        pending)
            say "The module is signed. Its key is imported and waits for the reboot that confirms it."
            ENROL_PENDING=1; MODULE_TRUSTED=0
            ;;
        *)
            if import_key "$cert"; then
                ENROL_PENDING=1; MODULE_TRUSTED=0
            else
                MODULE_TRUSTED=0
                failed "the key was not imported"
                next "Import the signing key yourself, then reboot and enrol it:  sudo mokutil --import $cert"
            fi
            ;;
    esac

    if [ "$ENROL_PENDING" = 1 ]; then
        next "REBOOT to enrol the key. A blue screen (\"Perform MOK management\") appears before the system starts:
       press a key within ten seconds, choose Enroll MOK > Continue > Yes, type the password you just chose
       (the keyboard layout there is US), then Reboot. Miss it, and you run this installer again.
       Until that is done the kernel refuses the module and Ohman cannot reach the firmware."
    fi
}

# ---------------------------------------------------------------------------------------------------------
# The kernel module, through DKMS

DKMS_SRC=""    # /usr/src/ohman-wmi-<version>, under ROOT

# The tree DKMS builds from: the driver sources as shipped, plus the signing hook.
assemble_dkms_tree() {
    local out=$1 f
    mkdir -p "$out"
    for f in "$DRIVER_DIR"/*; do
        case "$f" in
            *.c|*.h|*/Makefile|*/Kbuild|*/dkms.conf) install -m 644 "$f" "$out/" ;;
        esac
    done
    install -m 755 "$PKG_DIR/dkms-sign.sh" "$out/sign.sh"
    if ! grep -q '^POST_INSTALL=' "$out/dkms.conf"; then
        # shellcheck disable=SC2016  # DKMS expands ${kernelver}, not this script
        printf 'POST_INSTALL="sign.sh ${kernelver}"\n' >>"$out/dkms.conf"
    fi
}

# Versions of the module DKMS knows about. It prints "ohman-wmi/0.1, 6.8.0, x86_64: installed" (DKMS 3)
# or "ohman-wmi, 0.1, 6.8.0, x86_64: installed" (DKMS 2).
dkms_versions() {
    dkms status 2>/dev/null | sed -n "s|^$MODULE[/,] *\([^,:]*\)[,:].*|\1|p" | sort -u
}

# Whether DKMS has the module installed for a kernel (default: the running one). The copy of this script in
# /opt/ohman has no dkms.conf beside it and so no version to look for: any version counts there.
dkms_installed_for() {
    local k=${1:-$KVER} out
    out=$(dkms status 2>/dev/null || true)
    printf '%s\n' "$out" | grep "^$MODULE[/,] *${MODULE_VERSION:-[^,:]*}[,:]" | grep -F ", $k," | grep installed >/dev/null
}
dkms_installed_for_running_kernel() { dkms_installed_for "$KVER"; }

MODULE_OK=0

install_module() {
    local tmp v changed=1 log out
    step "Kernel module $MODULE $MODULE_VERSION"
    [ -n "$MODULE_VERSION" ] || { failed "no version in $DRIVER_DIR/dkms.conf"; return 0; }
    DKMS_SRC=$ROOT/usr/src/$MODULE-$MODULE_VERSION

    tmp=$(mktemp -d); TEMPS+=("$tmp")
    assemble_dkms_tree "$tmp/src"
    if [ -d "$DKMS_SRC" ] && diff -r "$tmp/src" "$DKMS_SRC" >/dev/null 2>&1; then
        changed=0
    fi

    if [ "$STAGED" = 1 ]; then
        rm -rf "$DKMS_SRC"; mkdir -p "$ROOT/usr/src"; cp -r "$tmp/src" "$DKMS_SRC"
        say "source in $DKMS_SRC"
        sys dkms add -m "$MODULE" -v "$MODULE_VERSION"
        sys dkms install -m "$MODULE" -v "$MODULE_VERSION" -k "$KVER"
        MODULE_OK=1
        return 0
    fi

    if [ "$changed" = 0 ] && dkms_installed_for_running_kernel; then
        say "source unchanged and already built for $KVER: not rebuilding"
        MODULE_OK=1
        return 0
    fi

    # An older or half-installed copy goes first, for every kernel it was built for.
    for v in $(dkms_versions); do
        say "removing $MODULE $v from DKMS"
        dkms remove -m "$MODULE" -v "$v" --all >/dev/null 2>&1 || true
        [ "$v" = "$MODULE_VERSION" ] || rm -rf "/usr/src/$MODULE-$v"
    done
    # With every version removed, DKMS should hold nothing of ours. What is still there is the debris of an
    # interrupted build or of a DKMS that was removed and reinstalled, and "dkms add" trips over it.
    if [ -z "$(dkms_versions)" ] && [ -d "/var/lib/dkms/$MODULE" ]; then
        say "clearing a stale /var/lib/dkms/$MODULE"
        rm -rf "/var/lib/dkms/$MODULE"
    fi
    rm -rf "$DKMS_SRC"
    mkdir -p /usr/src
    cp -r "$tmp/src" "$DKMS_SRC"
    chown -R root:root "$DKMS_SRC"
    chmod -R u+rwX,go+rX,go-w "$DKMS_SRC"

    if ! out=$(dkms add -m "$MODULE" -v "$MODULE_VERSION" 2>&1); then
        failed "dkms add did not accept the module source"
        printf '%s\n' "$out" | sed 's/^/    /'
        return 0
    fi
    say "building for $KVER (DKMS rebuilds it by itself for every kernel installed later)"
    if dkms install -m "$MODULE" -v "$MODULE_VERSION" -k "$KVER" 2>&1 | sed 's/^/    /'; then
        MODULE_OK=1
        ok "built and installed"
    else
        log=/var/lib/dkms/$MODULE/$MODULE_VERSION/build/make.log
        failed "the module did not build"
        if [ -r "$log" ]; then say "last lines of $log:"; tail -n 20 "$log" | sed 's/^/    /'; fi
    fi
}

# The other kernels on this machine that have their headers installed. Somebody with a newer kernel waiting
# for a reboot (and enrolling a Secure Boot key is a reboot) must not come back up without the module.
# DKMS does this by itself for kernels installed from now on; these were installed before.
build_for_other_kernels() {
    local d k
    [ "$MODULE_OK" = 1 ] && [ "$STAGED" = 0 ] || return 0
    for d in /lib/modules/*/build; do
        [ -e "$d/Makefile" ] || continue
        k=${d%/build}; k=${k##*/}
        [ "$k" != "$KVER" ] || continue
        if dkms_installed_for "$k"; then
            say "already built for $k"
        elif dkms install -m "$MODULE" -v "$MODULE_VERSION" -k "$k" >/dev/null 2>&1; then
            ok "also built for $k"
        else
            warn "could not build for $k (the other installed kernel). After booting into it:  sudo dkms autoinstall"
        fi
    done
}

load_module() {
    local err
    [ "$MODULE_OK" = 1 ] || return 0
    if [ "$STAGED" = 1 ]; then
        sys modprobe "$MODULE"
        return 0
    fi
    if [ "$MODULE_TRUSTED" = 0 ]; then
        say "not loading the module yet: the kernel will accept it once its key is enrolled"
        return 0
    fi
    # Reload, so that a rebuilt module is the one running. The helper is stopped at this point.
    modprobe -r "$MODULE" 2>/dev/null || true
    if err=$(modprobe "$MODULE" 2>&1); then
        ok "loaded; /dev/$MODULE is there"
        return 0
    fi
    case "$err" in
        *"No such device"*)
            warn "the module is installed but would not load: this machine has no HP BIOS mailbox."
            say "Ohman drives HP OMEN and Victus laptops. On anything else only 'ohman --demo' does something."
            ;;
        *"Key was rejected"*|*"Required key not available"*|*"Operation not permitted"*)
            failed "the kernel refused the module: its signature is not trusted (Secure Boot)"
            next "See docs/linux.md, Secure Boot: enrol the key, or run this installer again with --sign."
            ;;
        *)
            failed "modprobe $MODULE: $err"
            ;;
    esac
}

# ---------------------------------------------------------------------------------------------------------
# Files

install_files() {
    local new old f
    step "Files"

    # A whole new directory swapped into place: a running Ohman keeps the files it has open, and nothing
    # from an older version is left behind.
    new=$ROOT$PREFIX.new; old=$ROOT$PREFIX.old
    rm -rf "$new" "$old"
    mkdir -p "$new"
    cp -r "$BIN_DIR"/. "$new/"
    chmod 755 "$new"
    for f in ohman ohman-helper ohman-probe; do
        if [ -e "$new/$f" ]; then chmod 755 "$new/$f"; fi
    done
    install -m 755 "$HERE/uninstall.sh" "$new/uninstall.sh"
    install -m 755 "$HERE/install.sh" "$new/install.sh"
    printf '%s\n' "$VERSION" >"$new/VERSION"
    for f in "$HERE/LICENSE" "$HERE/../LICENSE"; do
        if [ -r "$f" ]; then install -m 644 "$f" "$new/LICENSE"; break; fi
    done
    # However the release was unpacked (a tarball extracted under umask 077 keeps 0600 files): everybody
    # may read and run it, only root may change it.
    chmod -R u+rwX,go+rX,go-w "$new"
    if [ "$(id -u)" = 0 ]; then chown -R root:root "$new"; fi
    if [ -d "$ROOT$PREFIX" ]; then mv "$ROOT$PREFIX" "$old"; fi
    mkdir -p "$(dirname "$ROOT$PREFIX")"
    mv "$new" "$ROOT$PREFIX"
    rm -rf "$old"
    say "$PREFIX  (Ohman $VERSION)"

    mkdir -p "$ROOT/usr/bin"
    if [ "$HAVE_GUI" = 1 ]; then ln -sfn "$PREFIX/ohman" "$ROOT/usr/bin/ohman"; say "/usr/bin/ohman"; fi
    if [ "$HAVE_PROBE" = 1 ]; then ln -sfn "$PREFIX/ohman-probe" "$ROOT/usr/bin/ohman-probe"; say "/usr/bin/ohman-probe"; fi

    if [ "$HAVE_GUI" = 1 ]; then
        install -D -m 644 "$PKG_DIR/ohman.desktop" "$ROOT/usr/share/applications/ohman.desktop"
        install -D -m 644 "$PKG_DIR/ohman.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/ohman.png"
        say "/usr/share/applications/ohman.desktop, and its icon"
    fi

    install -D -m 644 "$PKG_DIR/70-ohman.rules" "$ROOT/etc/udev/rules.d/70-ohman.rules"
    say "/etc/udev/rules.d/70-ohman.rules  (per-key keyboards, for the group $GROUP)"

    if [ "$HAVE_HELPER" = 1 ]; then
        install -D -m 644 "$PKG_DIR/$SERVICE" "$ROOT/etc/systemd/system/$SERVICE"
        say "/etc/systemd/system/$SERVICE"
    fi
}

# What loads at every boot. msr always: the helper reads the CPU through it whether or not our own module
# is there. ohman-wmi only when it is installed, so that a machine without it does not log a failed load at
# every start.
write_modules_load() {
    local dst=$ROOT/etc/modules-load.d/$MODULE.conf with_module=0 d
    if [ "$MODULE_OK" = 1 ]; then
        with_module=1
    else
        # --no-module on a machine that has the module from an earlier install: it stays in the list.
        for d in "$ROOT"/usr/src/"$MODULE"-*; do
            if [ -d "$d" ] && [ "$WITH_MODULE" = 0 ]; then with_module=1; fi
        done
    fi
    mkdir -p "$(dirname "$dst")"
    if [ "$with_module" = 1 ]; then
        cat "$PKG_DIR/modules-load.conf" >"$dst"
        say "/etc/modules-load.d/$MODULE.conf  ($MODULE and msr load at every boot)"
    else
        grep -v "^$MODULE\$" "$PKG_DIR/modules-load.conf" >"$dst"
        say "/etc/modules-load.d/$MODULE.conf  (msr loads at every boot; $MODULE is not installed)"
    fi
    chmod 644 "$dst"
}

# id complains (and exits non-zero) about a user who has a group id without a name, while still listing the
# rest; its exit code is not the answer to this question.
user_in_group() {
    local groups members
    groups=$(id -nG "$1" 2>/dev/null || true)
    case " $groups " in *" $GROUP "*) return 0 ;; esac
    # And the group's own member list, which is what usermod writes.
    members=$(getent group "$GROUP" 2>/dev/null | cut -d: -f4 || true)
    case ",$members," in *",$1,"*) return 0 ;; esac
    return 1
}

setup_group() {
    step "Group $GROUP"
    if [ "$STAGED" = 0 ] && getent group "$GROUP" >/dev/null 2>&1; then
        say "exists"
    elif ! sys groupadd --system "$GROUP"; then
        failed "could not create the group $GROUP; the helper will not start without it"
        return 0
    fi
    if [ -z "$TARGET_USER" ]; then
        warn "could not tell which user will run Ohman"
        next "Add yourself to the group:  sudo usermod -aG $GROUP YOUR_USER_NAME   (then log out and in)"
        return 0
    fi
    if [ "$STAGED" = 0 ] && user_in_group "$TARGET_USER"; then
        say "$TARGET_USER is a member"
    elif sys usermod -aG "$GROUP" "$TARGET_USER"; then
        say "$TARGET_USER added"
        next "LOG OUT and back in (or reboot): $TARGET_USER was added to the group \"$GROUP\", and that only
       counts for sessions started afterwards. Until then Ohman cannot reach its helper."
    else
        # A mistyped --user, or an account that is not in /etc/passwd (systemd-homed, a directory service).
        failed "could not add $TARGET_USER to the group $GROUP"
        next "Add your user to the group \"$GROUP\" by the means your system has for it, then log out and in.
       For a local account:  sudo usermod -aG $GROUP YOUR_USER_NAME
       For systemd-homed:    sudo homectl update YOUR_USER_NAME --member-of=$GROUP   (plus the groups you already have)"
    fi
}

refresh_system() {
    step "Service"
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
    if [ "$HAVE_HELPER" = 0 ]; then
        warn "this build carries no ohman-helper: no service was set up"
        return 0
    fi
    if ! have systemctl && [ "$STAGED" = 0 ]; then
        failed "no systemd here: start $PREFIX/ohman-helper as root by your init system's means"
        return 0
    fi
    # The helper's sandbox allows the msr device class only if it exists when the helper starts.
    sys modprobe msr 2>/dev/null || true
    sys systemctl daemon-reload || failed "systemctl daemon-reload did not work"
    sys systemctl enable "$SERVICE" || failed "could not enable $SERVICE"
    # restart, not start: after an upgrade the new binary has to be the one running.
    if ! sys systemctl restart "$SERVICE"; then
        helper_did_not_start
        return 0
    fi
    [ "$STAGED" = 0 ] || return 0
    verify_helper
}

helper_did_not_start() {
    failed "$SERVICE did not start"
    say "what it said:"
    journalctl -u "$SERVICE" -n 15 --no-pager 2>/dev/null | sed 's/^/    /' || true
}

# "restart" coming back without an error says the program was started, not that it stayed up. Give it a
# moment, then look: is it still running, and is its socket there with the owner and mode Ohman needs.
verify_helper() {
    local i want_gid got
    for i in 1 2 3 4 5 6 7 8 9 10; do
        systemctl is-active --quiet "$SERVICE" 2>/dev/null || break
        if [ -S "$SOCKET" ] && [ "$i" -ge 3 ]; then break; fi
        sleep 0.5
    done
    if ! systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
        helper_did_not_start
        return 0
    fi
    if [ ! -S "$SOCKET" ]; then
        failed "$SERVICE is running, but its socket $SOCKET is not there"
        journalctl -u "$SERVICE" -n 15 --no-pager 2>/dev/null | sed 's/^/    /' || true
        return 0
    fi
    want_gid=$(getent group "$GROUP" 2>/dev/null | cut -d: -f3 || true)
    got=$(stat -c '%u:%g %a' "$SOCKET" 2>/dev/null || true)
    if [ "$got" = "0:$want_gid 660" ]; then
        ok "$SERVICE is running; $SOCKET is root:$GROUP 0660"
    else
        failed "$SOCKET has owner:group mode $got, expected 0:$want_gid 660 (root:$GROUP): Ohman may not reach the helper"
    fi
}

stop_helper() {
    [ "$STAGED" = 0 ] || return 0
    have systemctl || return 0
    if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
        say "stopping the running helper for the upgrade"
        systemctl stop "$SERVICE" || true
    fi
}

# ---------------------------------------------------------------------------------------------------------
# --check: what is on this machine now

report_installed() {
    local v ko cert user=${TARGET_USER:-}
    step "Installed now"
    if [ -r "$ROOT$PREFIX/VERSION" ]; then
        ok "Ohman $(head -n 1 "$ROOT$PREFIX/VERSION") in $PREFIX"
    else
        say "not installed ($PREFIX is not there)"
    fi
    [ "$STAGED" = 0 ] || return 0

    if have dkms; then
        v=$(dkms_versions | tr '\n' ' ')
        if [ -n "$v" ]; then
            if dkms_installed_for_running_kernel; then ok "module $MODULE ${v}built for $KVER"
            else say "module $MODULE ${v}known to DKMS but not built for $KVER"; fi
        else
            say "module $MODULE: not in DKMS"
        fi
    fi
    if [ -d /sys/module/ohman_wmi ]; then ok "module loaded"; else say "module not loaded"; fi
    if [ -e "/dev/$MODULE" ]; then ok "/dev/$MODULE"; else say "/dev/$MODULE is not there"; fi
    if [ -d /sys/module/hp_wmi ]; then ok "hp-wmi (the kernel's own HP driver) is loaded; it stays"; fi

    if [ "$NEED_SIGN" = 1 ] && have mokutil && have openssl; then
        ko=$(installed_module_file)
        if [ -n "$ko" ]; then
            if module_is_signed "$ko"; then
                if cert=$(signer_certificate "$ko"); then
                    case "$(mok_state "$cert")" in
                        enrolled) ok "module signed; its key is enrolled ($cert)" ;;
                        pending)  say "module signed; its key waits for the enrolment reboot ($cert)" ;;
                        *)        say "module signed, but its key is NOT enrolled ($cert): the kernel will refuse it" ;;
                    esac
                elif [ "$(id -u)" != 0 ]; then
                    say "module signed; run this check with sudo to see whether its key is enrolled"
                else
                    say "module signed, by a key that is not on this machine"
                fi
            else
                say "module NOT signed: the kernel will refuse it"
            fi
        fi
    fi

    if have systemctl; then
        if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then ok "$SERVICE is running"
        elif systemctl cat "$SERVICE" >/dev/null 2>&1; then say "$SERVICE is installed but not running"
        else say "$SERVICE is not installed"; fi
    fi
    if [ -S "$SOCKET" ]; then
        ok "$SOCKET  ($(stat -c '%U:%G %a' "$SOCKET" 2>/dev/null || echo unreadable))"
    elif [ -d /run/ohman ] && [ "$(id -u)" != 0 ] && [ ! -x /run/ohman ]; then
        say "$SOCKET cannot be seen from here: you are not in the group $GROUP in this session"
    fi

    if getent group "$GROUP" >/dev/null 2>&1; then
        if [ -n "$user" ]; then
            if user_in_group "$user"; then ok "$user is in the group $GROUP"
            else say "$user is NOT in the group $GROUP"; fi
        else
            ok "group $GROUP exists"
        fi
    else
        say "group $GROUP does not exist"
    fi
    if [ -r /etc/udev/rules.d/70-ohman.rules ]; then ok "udev rule for per-key keyboards"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Go

step "Ohman $VERSION for Linux"
say "system:       $DISTRO_NAME, kernel $KVER"
case "$SB" in
    on)  if [ "$NEED_SIGN" = 1 ]; then say "Secure Boot:  on; the kernel wants modules signed"
         else say "Secure Boot:  on, but this kernel loads unsigned modules"; fi ;;
    off) if [ "$NEED_SIGN" = 1 ]; then say "Secure Boot:  off, but the kernel wants modules signed"
         else say "Secure Boot:  off"; fi ;;
    *)   say "Secure Boot:  could not tell" ;;
esac
case "$IS_HP" in
    yes) say "machine:      HP" ;;
    no)  say "machine:      not an HP. Ohman installs, but only 'ohman --demo' will do anything here." ;;
    *)   say "machine:      no DMI information (a virtual machine or WSL?). Only 'ohman --demo' works here." ;;
esac
[ "$STAGED" = 0 ] || say "staging into: $ROOT  (nothing on the running system is changed)"

if [ -e /run/ostree-booted ] && [ "$STAGED" = 0 ] && [ "$CHECK" = 0 ]; then
    die "this is an image-based system (Silverblue, Kinoite, Bazzite ...). /usr is read-only there and DKMS
does not work, so this installer cannot set Ohman up. Not supported yet."
fi

if [ "$INSTALLED_COPY" = 0 ]; then
step "What this release carries"
[ "$HAVE_GUI" = 1 ]    && ok "ohman (the window)"            || miss "ohman (the window)"
[ "$HAVE_HELPER" = 1 ] && ok "ohman-helper (root service)"   || miss "ohman-helper (root service)"
[ "$HAVE_PROBE" = 1 ]  && ok "ohman-probe (diagnostic)"      || miss "ohman-probe (diagnostic)"
[ -n "$MODULE_VERSION" ] && ok "$MODULE $MODULE_VERSION (kernel module source)" || miss "$MODULE (kernel module source)"
if [ "$HAVE_GUI$HAVE_HELPER$HAVE_PROBE" = 000 ]; then
    if [ "$CHECK" = 1 ]; then
        warn "no binaries in $BIN_DIR"
    else
        die "no binaries in $BIN_DIR. Unpack a release tarball and run its install.sh,
or build one from the repository with linux/packaging/publish.sh."
    fi
fi
fi

step "Needed to build and load the module"
if [ "$WITH_MODULE" = 1 ]; then
    check_prerequisites
else
    say "skipped (--no-module)"
fi

if [ "$CHECK" = 1 ]; then
    print_missing
    report_installed
    echo
    if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
        say "Not ready to install: packages are missing (above)."
        exit 1
    fi
    [ "$INSTALLED_COPY" = 1 ] || say "Ready to install:  sudo ./install.sh"
    exit 0
fi

if [ "$STAGED" = 0 ] && [ "$(id -u)" != 0 ]; then
    print_missing
    die "this has to run as root:  sudo ./install.sh   (or --check to only look, --root DIR to stage)"
fi

if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
    if [ "$STAGED" = 1 ]; then
        say "(staging: the packages above would be needed on a real install)"
    elif [ "$INSTALL_DEPS" = 1 ]; then
        install_missing
        step "Checking again"
        check_prerequisites
        [ ${#MISSING_PKGS[@]} -eq 0 ] || { print_missing; die "still missing after installing: ${MISSING_PKGS[*]}"; }
    else
        print_missing
        die "packages are missing (above). Nothing was changed.
Use --no-module to install without the kernel module; Ohman then runs, but cannot reach the firmware."
    fi
fi

stop_helper
install_files
setup_group
if [ "$WITH_MODULE" = 1 ]; then
    install_module
    [ "$MODULE_OK" = 1 ] && handle_signing
    # After signing: if the installer had to make its own key, the hook signs these builds with it.
    build_for_other_kernels
    load_module
fi
write_modules_load
refresh_system

step "Done"
if [ ${#FAILED[@]} -gt 0 ]; then
    say "Installed, but not everything worked:"
    for f in "${FAILED[@]}"; do say "  - $f"; done
    echo
fi
if [ "$WITH_MODULE" = 0 ]; then
    say "The kernel module was left out (--no-module): Ohman starts, but cannot reach the firmware."
fi
if [ ${#NEXT[@]} -gt 0 ]; then
    say "${B}What you still have to do:${N}"
    i=1
    for f in "${NEXT[@]}"; do printf '  %d. %s\n' "$i" "$f"; i=$((i + 1)); done
    echo
fi
if [ "$HAVE_GUI" = 1 ]; then
    say "Then start Ohman from the applications menu, or run:  ohman"
fi
say "Check what Ohman makes of this laptop:  ohman-probe"
say "Remove it again:  sudo $PREFIX/uninstall.sh"
say "Not verified on real hardware under Linux yet. If it works, or does not, tell us: $REPO_URL/issues"
[ ${#FAILED[@]} -eq 0 ] || exit 1
exit 0
