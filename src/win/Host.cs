// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: what this operating system can do for Ohman, and the few things the shared engine asks
// of it that have nothing to do with the laptop's firmware. src\linux\Host.cs is the same class for Linux.
// The UI reads the flags to decide which rows exist; the engine calls the methods and never asks which system
// it is on.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ohman {
    static class Host {
        public const bool IsWindows = true, IsLinux = false;
        public const string OsName = "Windows";

        /// <summary>The vendor's own app exists here and fights for the OMEN key and the fans (OMEN Gaming Hub).</summary>
        public static bool HasVendorApp { get { return true; } }
        /// <summary>Windows Dynamic Lighting exists and can be handed the keyboard.</summary>
        public static bool HasDynamicLighting { get { return true; } }
        /// <summary>Ohman can fetch and install the optional driver itself, from the Settings row.</summary>
        public static bool CanInstallDriver { get { return true; } }
        /// <summary>Ohman can replace its own files with a newer build.</summary>
        public static bool CanSelfUpdate { get { return true; } }
        /// <summary>The display's refresh rate can be listed and set.</summary>
        public static bool HasRefreshRate { get { return true; } }
        /// <summary>The operating system has a power mode of its own that can follow Ohman's.</summary>
        public static bool HasPowerModeSync { get { return true; } }
        /// <summary>The benchmark's frame capture (PresentMon) exists here.</summary>
        public static bool HasFrameCapture { get { return true; } }
        /// <summary>What the optional driver is called on this system, for the UI's wording.</summary>
        public const string DriverName = "PawnIO";
        /// <summary>What to tell somebody who has to install or update something by hand. Empty: Ohman does it.</summary>
        public const string InstallHint = "";

        // ---------- the vendor app ----------
        // OGH's key handler (OmenCommandCenterBackground) is launched at logon by HP's "OmenInstallMonitor" scheduled
        // tasks (its own startup task is disabled). Taking over the key = stop both processes now and disable those
        // tasks so they do not come back at the next logon. Turning the switch off re-enables the tasks.
        public static void StopVendorApp() {
            foreach (string name in new string[] { "OmenCommandCenterBackground", "OmenInstallMonitor" }) {
                try {
                    foreach (var p in Process.GetProcessesByName(name)) {
                        using (p) { try { p.Kill(); Log.Write("stopped " + name + " (pid " + p.Id + ")"); } catch (Exception ex) { Log.Write("kill " + name + ": " + ex.Message); } }
                    }
                } catch { }
            }
        }

        public static void SetVendorTasks(bool disable) {
            try {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                string csv = "";
                var outLines = new System.Text.StringBuilder();
                using (var q = new Process()) {
                    q.StartInfo = psi;
                    q.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outLines) outLines.Append(e.Data).Append("\n"); };
                    q.Start();
                    q.BeginOutputReadLine();
                    if (q.WaitForExit(5000)) { q.WaitForExit(); csv = outLines.ToString(); }
                    else { try { q.Kill(); } catch { } q.WaitForExit(2000); if (!q.HasExited) Log.Write("could not stop schtasks query"); Log.Write("schtasks query timed out"); }
                }
                int n = 0;
                foreach (string line in csv.Split('\n')) {
                    if (!line.StartsWith("\"\\OmenInstallMonitor", StringComparison.OrdinalIgnoreCase)) continue;
                    string task = line.Split('"')[1];
                    using (var c = Process.Start(new ProcessStartInfo("schtasks.exe", "/Change /TN \"" + task + "\" " + (disable ? "/DISABLE" : "/ENABLE")) { CreateNoWindow = true, UseShellExecute = false })) {
                        if (c.WaitForExit(5000)) { if (c.ExitCode == 0) n++; else Log.Write("schtasks " + task + " rc=" + c.ExitCode); }
                        else { try { c.Kill(); } catch { } c.WaitForExit(2000); if (!c.HasExited) Log.Write("could not stop " + task); Log.Write("schtasks " + task + " timed out"); }
                    }
                }
                Log.Write((disable ? "disabled " : "re-enabled ") + n + " OmenInstallMonitor task(s)");
            } catch (Exception ex) { Log.Write("OGH tasks: " + ex.Message); }
        }

        // ---------- the system's own power mode ----------
        static readonly Guid OverlayEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid OverlayBalanced = Guid.Empty;
        static readonly Guid OverlayPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(ref Guid overlay);
        /// <summary>0 eco, 1 balanced, 2 performance: the Windows power-mode overlay that goes with it.</summary>
        public static void SetPowerMode(int modeIndex) {
            try {
                Guid g = modeIndex == 0 ? OverlayEfficiency : modeIndex == 2 ? OverlayPerformance : OverlayBalanced;
                uint rc = PowerSetActiveOverlayScheme(ref g);
                if (rc != 0) Log.Write("PowerSetActiveOverlayScheme rc=" + rc);
            } catch (Exception ex) { Log.Write("power overlay: " + ex.Message); }
        }
    }
}
