// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: what this operating system can do for Ohman, and the few things the shared engine asks
// of it that have nothing to do with the laptop's firmware. src\win\Host.cs is the same class for Windows.
// The UI reads the flags to decide which rows exist; the engine calls the methods and never asks which system
// it is on.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ohman {
    static class Host {
        public const bool IsWindows = false, IsLinux = true;
        public const string OsName = "Linux";

        /// <summary>The vendor's own app. HP ships none for Linux, so there is nothing to take the key from.</summary>
        public static bool HasVendorApp { get { return false; } }
        /// <summary>Windows Dynamic Lighting has no counterpart here.</summary>
        public static bool HasDynamicLighting { get { return false; } }
        /// <summary>The driver is a kernel module (DKMS) and a root service; only the installer can put those in.</summary>
        public static bool CanInstallDriver { get { return false; } }
        /// <summary>The binaries live in /opt/ohman, which a user process cannot write. A newer release is
        /// reported; the installer command puts it in.</summary>
        public static bool CanSelfUpdate { get { return false; } }
        /// <summary>The display's refresh rate can be listed and set: an X11 session with xrandr.</summary>
        public static bool HasRefreshRate { get { return Display.Available; } }
        /// <summary>tuned or power-profiles-daemon is there to follow Ohman's mode.</summary>
        public static bool HasPowerModeSync { get { return TunedAdm != null || Find("powerprofilesctl") != null; } }
        /// <summary>The benchmark's frame capture: MangoHud, which the owner loads into the game. True when it is
        /// installed, or a game with its control socket is already running. When it is false the Benchmark page
        /// still opens, and says what is missing.</summary>
        public static bool HasFrameCapture { get { return FrameCapture.Ready; } }
        /// <summary>What the optional driver is called on this system, for the UI's wording.</summary>
        public const string DriverName = "ohman-wmi";
        /// <summary>What to tell somebody whose module or helper is not there. The installer leaves a copy of
        /// itself in /opt/ohman that only reports: with --check it says what is missing (headers, a signing key
        /// that was never enrolled, the group) and how to put it right, and changes nothing.</summary>
        public const string InstallHint = "sudo /opt/ohman/install.sh --check";

        // ---------- the vendor app: there is none ----------
        public static void StopVendorApp() { }
        public static void SetVendorTasks(bool disable) { }

        // ---------- the system's own power mode ----------
        /// <summary>tuned's own names for the three modes, tried in this order. Which of them a release ships, and
        /// which of those an admin kept, differs, so the first one `tuned-adm list` offers is used. The set is the
        /// one Victus Hub asks for; its author picked it on a Victus 16-s running Fedora.</summary>
        static readonly string[][] TunedModes = new string[][] {
            new string[] { "powersave", "balanced-battery" },                                    // eco
            new string[] { "balanced", "desktop" },                                             // balanced
            new string[] { "throughput-performance", "accelerator-performance", "latency-performance" },   // performance
        };

        /// <summary>tuned-adm's full path, or null. It lives in /usr/sbin, which is not on every desktop
        /// session's PATH, so the two places it is packaged into are looked at before PATH is.</summary>
        public static string TunedAdm {
            get {
                try {
                    if (File.Exists("/usr/sbin/tuned-adm")) return "/usr/sbin/tuned-adm";
                    if (File.Exists("/usr/bin/tuned-adm")) return "/usr/bin/tuned-adm";
                } catch { }
                return Find("tuned-adm");
            }
        }

        /// <summary>What follows the mode on this machine, as the support report says it.</summary>
        public static string PowerModeSync {
            get {
                bool tuned = TunedAdm != null, ppd = Find("powerprofilesctl") != null;
                return tuned && ppd ? "tuned and power-profiles-daemon" : tuned ? "tuned" : ppd ? "power-profiles-daemon" : "none (no tuned-adm, no powerprofilesctl)";
            }
        }

        /// <summary>0 eco, 1 balanced, 2 performance. tuned is asked first: it is what moves the CPU here
        /// (governor, EPP), where the BIOS mode Ohman sets by itself moves only the GPU and the fans, and
        /// Fedora ships it with no power-profiles-daemon at all. A machine with neither (TLP, nothing) is
        /// left alone, and so is one whose firmware offers no "performance".</summary>
        public static void SetPowerMode(int modeIndex) {
            string tuned = TunedAdm;
            if (tuned != null && SetTunedProfile(tuned, modeIndex)) return;
            string tool = Find("powerprofilesctl");
            if (tool == null) return;
            string name = modeIndex == 0 ? "power-saver" : modeIndex == 2 ? "performance" : "balanced";
            try {
                string err;
                int rc = Run(tool, "set " + name, 4000, out err);
                if (rc != 0) Log.Write("powerprofilesctl set " + name + ": rc " + rc + (err.Length > 0 ? " (" + err.Trim() + ")" : ""));
            } catch (Exception ex) { Log.Write("power profile: " + ex.Message); }
        }

        /// <summary>One tuned switch. False when this tuned offers none of the mode's profiles, so the caller can
        /// fall back to power-profiles-daemon. A profile that was picked and then refused by the daemon is logged
        /// and counts as done: the mode is the owner's choice, not something to retry on another daemon.</summary>
        static bool SetTunedProfile(string tuned, int modeIndex) {
            try {
                string list;
                int rc = Run(tuned, "list", 4000, out list);
                if (rc == 0) {
                    string[] want = TunedModes[Math.Max(0, Math.Min(2, modeIndex))];
                    string name = TunedOffered(list, want);
                    if (name == null) { Log.Write("tuned offers none of " + string.Join("/", want) + "; the mode is left to it"); return false; }
                    string err;
                    rc = Run(tuned, "profile " + name, 4000, out err);
                    if (rc != 0) Log.Write("tuned-adm profile " + name + ": rc " + rc + (err.Length > 0 ? " (" + err.Trim() + ")" : ""));
                    return true;
                }
                Log.Write("tuned-adm list: rc " + rc);
            } catch (Exception ex) { Log.Write("tuned profile: " + ex.Message); }
            return false;
        }

        /// <summary>The first of Ohman's candidates that this tuned offers. `tuned-adm list` prints one profile
        /// per line as "- name   - what it is", under a header and a "Current active profile" line of its own,
        /// so a line names a profile when it starts with the dash.</summary>
        static string TunedOffered(string list, string[] candidates) {
            var offered = new List<string>();
            foreach (string line in list.Split('\n')) {
                string t = line.TrimStart();
                if (t.Length > 1 && t[0] == '-') {
                    t = t.Substring(1).TrimStart();
                    int e = 0;
                    while (e < t.Length && !char.IsWhiteSpace(t[e])) e++;
                    if (e > 0) offered.Add(t.Substring(0, e));
                }
            }
            foreach (string c in candidates) if (offered.Contains(c)) return c;
            return null;
        }

        /// <summary>The mode a profile name stands for, or -1 when it is not one of ours: tuned's names
        /// (TunedModes) and the three power-profiles-daemon spells. Anything else -- a profile an admin made,
        /// "virtual-guest" and its like -- is the caller's to leave alone.</summary>
        public static int ProfileIndexFor(string profile) {
            if (string.IsNullOrEmpty(profile)) return -1;
            profile = profile.Trim();
            for (int i = 0; i < TunedModes.Length; i++) if (Array.IndexOf(TunedModes[i], profile) >= 0) return i;
            if (profile == "power-saver") return 0;
            if (profile == "performance") return 2;
            return -1;
        }

        // ---------- small things the other Linux classes share ----------
        /// <summary>The full path of a program on PATH, or null. Looked up each time: PATH is short and this is
        /// asked a few times a session.</summary>
        public static string Find(string program) {
            try {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
                foreach (string dir in path.Split(':')) {
                    if (dir.Length == 0) continue;
                    string p = Path.Combine(dir, program);
                    if (File.Exists(p)) return p;
                }
            } catch { }
            return null;
        }

        /// <summary>Run a program and wait for it, bounded. Returns its exit code, -1 when it had to be killed.
        /// Standard output is returned through <paramref name="output"/> (standard error when it failed).</summary>
        public static int Run(string exe, string args, int timeoutMs, out string output) {
            output = "";
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = new Process()) {
                p.StartInfo = psi;
                var so = new System.Text.StringBuilder();
                var se = new System.Text.StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) so.Append(e.Data).Append('\n'); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (se) se.Append(e.Data).Append('\n'); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                p.WaitForExit();
                output = p.ExitCode == 0 ? so.ToString() : se.ToString();
                return p.ExitCode;
            }
        }

        /// <summary>The first line of a small text file, trimmed. Empty when it cannot be read.</summary>
        public static string ReadText(string path) {
            try { return File.ReadAllText(path).Trim(); } catch { return ""; }
        }

        /// <summary>Everything a stream has, read until it says there is no more. For files under /sys, whose
        /// stated length is the size of the kernel's buffer and not of what is in it.</summary>
        public static byte[] ReadToEnd(Stream s) {
            var all = new MemoryStream();
            var buf = new byte[4096];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) all.Write(buf, 0, n);
            return all.ToArray();
        }

        /// <summary>A path as its owner would type it, the home directory as "~": for a line that is printed
        /// to a terminal and then pasted somewhere public.</summary>
        public static string Tilde(string path) {
            try {
                string home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home) && home.Length > 1 && path.StartsWith(home + "/", StringComparison.Ordinal)) return "~" + path.Substring(home.Length);
            } catch { }
            return path;
        }
    }
}
