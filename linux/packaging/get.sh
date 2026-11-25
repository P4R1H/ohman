#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# The one-line installer. Published with every release as ohman-install.sh:
#
#   curl -fsSL https://github.com/P4R1H/Ohman/releases/latest/download/ohman-install.sh | sudo bash
#
# It downloads that release's tarball and the checksum published beside it, refuses to go on if they do not
# agree, unpacks into a temporary directory and runs install.sh from it. Arguments go through to install.sh:
#
#   curl -fsSL .../ohman-install.sh | sudo bash -s -- --install-deps
#
# What the checksum is worth: it catches a download that was cut short or damaged. It comes from the same
# place as the tarball, so it says nothing about who made the release. To read before you run, download the
# tarball from the releases page, unpack it and run ./install.sh --check.

# Piped into sh instead of bash (dash on Debian and Ubuntu): say so rather than die on the next line.
if [ -z "${BASH_VERSION:-}" ]; then
    echo "This installer needs bash: pipe it into 'sudo bash', not 'sudo sh'." >&2
    exit 1
fi

set -euo pipefail

# publish.sh writes the release's version in here. Empty (run straight from the repository) means: ask
# GitHub which release is the latest.
VERSION=""
REPO_URL=https://github.com/P4R1H/Ohman

# Everything is in a function that is called on the last line, so a download of this script that stops
# half way runs nothing at all.
main() {
    local tmp name url sum_expected sum_actual latest

    [ "$(uname -s)" = Linux ] || { echo "Ohman's installer is for Linux." >&2; exit 1; }
    case "$(uname -m)" in
        x86_64|amd64) ;;
        *) echo "Ohman is built for x86-64; this machine is $(uname -m)." >&2; exit 1 ;;
    esac
    for tool in curl tar sha256sum; do
        command -v "$tool" >/dev/null 2>&1 || { echo "This needs $tool, which is not installed." >&2; exit 1; }
    done
    if [ "$(id -u)" != 0 ]; then
        echo "Run it as root: put sudo in front of bash, as in" >&2
        echo "  curl -fsSL $REPO_URL/releases/latest/download/ohman-install.sh | sudo bash" >&2
        exit 1
    fi

    if [ -z "$VERSION" ]; then
        # /releases/latest answers with a redirect to /releases/tag/v1.2.3.
        latest=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "$REPO_URL/releases/latest")
        VERSION=${latest##*/tag/v}
        case "$VERSION" in
            ''|*[!0-9.]*) echo "Could not find the latest release at $REPO_URL/releases" >&2; exit 1 ;;
        esac
    fi

    name=ohman-$VERSION-linux-x64
    url=$REPO_URL/releases/download/v$VERSION/$name.tar.gz

    tmp=$(mktemp -d)
    # shellcheck disable=SC2064  # the directory known now is the one to remove
    trap "rm -rf '$tmp'" EXIT

    echo "Downloading Ohman $VERSION"
    curl -fL --proto '=https' --tlsv1.2 --progress-bar -o "$tmp/$name.tar.gz" "$url"
    curl -fsSL --proto '=https' --tlsv1.2 -o "$tmp/$name.tar.gz.sha256" "$url.sha256"

    sum_expected=$(awk '{print $1; exit}' "$tmp/$name.tar.gz.sha256")
    sum_actual=$(sha256sum "$tmp/$name.tar.gz" | awk '{print $1}')
    if [ -z "$sum_expected" ] || [ "$sum_expected" != "$sum_actual" ]; then
        echo "The download does not match its checksum. Nothing was installed." >&2
        echo "  expected $sum_expected" >&2
        echo "  got      $sum_actual" >&2
        exit 1
    fi
    echo "Checksum matches: $sum_actual"

    tar -xzf "$tmp/$name.tar.gz" -C "$tmp" --no-same-owner
    [ -f "$tmp/$name/install.sh" ] || { echo "The release has no install.sh in it." >&2; exit 1; }

    # Through bash, not by executing the file: /tmp is mounted noexec on some systems.
    # Not exec: the temporary directory is removed when this returns.
    bash "$tmp/$name/install.sh" "$@"
}

main "$@"
