// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the half of the update path that would touch the installed files, which here does not.
// The binaries are in /opt/ohman and belong to root, with a kernel module and a service beside them that have
// to match, so a newer release is reported (the shared half, src\Update.cs, asks GitHub) and the installer puts
// it in. Host.CanSelfUpdate says so to the UI; these members exist so the engine's calls mean "nothing staged".
// src\win\Update.cs is the same half for Windows.
using System;
using System.Diagnostics;
using System.Reflection;

namespace Ohman {

    public static partial class Update {
        /// <summary>The release asset that says a release has a Linux build: the installer script, which is the
        /// one Linux file whose name does not carry the version (linux\packaging\publish.sh writes it beside
        /// the tarball). Only its presence is reported.</summary>
        const string AssetName = "ohman-install.sh";

        public static string ExePath {
            get {
                try { string p = Environment.ProcessPath; if (!string.IsNullOrEmpty(p)) return p; } catch { }
                try { return Process.GetCurrentProcess().MainModule.FileName; } catch { return null; }
            }
        }
        public static string StagePath { get { return null; } }
        public static string OldPath { get { return null; } }

        /// <summary>Never: this build does not replace itself.</summary>
        public static bool CanReplace { get { return false; } }
        public static bool Stage(Release r) { return false; }
        public static string Staged() { return null; }
        public static bool Swap(out string error) { error = "updates are installed with the installer on Linux: " + Host.InstallHint; return false; }
        public static void CleanOld() { }
    }
}
