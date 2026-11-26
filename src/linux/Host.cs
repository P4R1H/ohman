// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: what this operating system can do for Ohman, and the few things the shared engine asks
// of it that have nothing to do with the laptop's firmware. src\win\Host.cs is the same class for Windows.
// The UI reads the flags to decide which rows exist; the engine calls the methods and never asks which system
// it is on.
using System;
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
        /// <summary>power-profiles-daemon is there to follow Ohman's mode.</summary>
        public static bool HasPowerModeSync { get { return Find("powerprofilesctl") != null; } }
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
        /// <summary>0 eco, 1 balanced, 2 performance, as power-profiles-daemon names them. A machine without the
        /// daemon (TLP, tuned, nothing) is left alone, and so is one whose firmware offers no "performance".</summary>
        public static void SetPowerMode(int modeIndex) {
            string tool = Find("powerprofilesctl");
            if (tool == null) return;
            string name = modeIndex == 0 ? "power-saver" : modeIndex == 2 ? "performance" : "balanced";
            try {
                string err;
                int rc = Run(tool, "set " + name, 4000, out err);
                if (rc != 0) Log.Write("powerprofilesctl set " + name + ": rc " + rc + (err.Length > 0 ? " (" + err.Trim() + ")" : ""));
            } catch (Exception ex) { Log.Write("power profile: " + ex.Message); }
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
