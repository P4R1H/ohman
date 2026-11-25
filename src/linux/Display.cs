// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the built-in panel's refresh rate, through xrandr. src\win\Display.cs is the same class
// for Windows.
//
// Only an X11 session can be asked this way. A Wayland compositor owns its outputs and each has its own tool
// (gnome's mutter over D-Bus, kscreen-doctor, wlr-randr), and xrandr under XWayland describes a pretend screen
// whose rate cannot be set. So there Available is false, every list is empty, and the UI hides the row rather
// than offer a switch that does nothing.
//
// UNTESTED on a real laptop: written against xrandr's documented output, checked only against captured text.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ohman {

    public static class Display {
        /// <summary>Whether the refresh rate can be read and set in this session.</summary>
        public static bool Available {
            get {
                try {
                    string type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
                    if (type.Equals("wayland", StringComparison.OrdinalIgnoreCase)) return false;
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
                    return Host.Find("xrandr") != null;
                } catch { return false; }
            }
        }

        /// <summary>What xrandr says about the laptop's own panel: its output name, the current mode's name, the
        /// rate in use and every rate that mode offers, each with the exact text xrandr printed for it (a rate
        /// asked for as 144 where the mode is 143.98 is refused by some drivers).</summary>
        internal sealed class PanelState {
            public string Output, Mode;
            public double Current;
            public readonly List<KeyValuePair<double, string>> Rates = new List<KeyValuePair<double, string>>();
        }

        static bool IsPanel(string output) {
            return output.StartsWith("eDP", StringComparison.OrdinalIgnoreCase) || output.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase)
                || output.StartsWith("DSI", StringComparison.OrdinalIgnoreCase);
        }

        static PanelState Query() {
            if (!Available) return null;
            string text;
            try { if (Host.Run(Host.Find("xrandr"), "--current", 4000, out text) != 0) return null; }
            catch (Exception ex) { Log.Write("xrandr: " + ex.Message); return null; }
            return Parse(text);
        }

        /// <summary>The parser, apart from the process so it can be checked against captured output
        /// (ohman-probe selftest).</summary>
        internal static PanelState Parse(string text) {
            PanelState found = null;
            bool inPanel = false;
            foreach (string raw in text.Split('\n')) {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line[0] != ' ' && line[0] != '\t') {
                    // "eDP-1 connected primary 1920x1080+0+0 (...)": an output header. Mode lines are indented.
                    string[] head = line.Split(' ');
                    inPanel = found == null && head.Length > 1 && head[1] == "connected" && IsPanel(head[0]);
                    if (inPanel) found = new PanelState { Output = head[0] };
                    continue;
                }
                if (!inPanel || found.Mode != null) continue;
                // "   1920x1080    144.00*+  60.00": the mode, then its rates; * marks the one in use, + the preferred.
                string[] parts = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || line.IndexOf('*') < 0) continue;
                found.Mode = parts[0];
                for (int i = 1; i < parts.Length; i++) {
                    string t = parts[i].TrimEnd('*', '+');
                    double hz;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out hz) || hz <= 1) continue;
                    found.Rates.Add(new KeyValuePair<double, string>(hz, t));
                    if (parts[i].IndexOf('*') >= 0) found.Current = hz;
                }
            }
            return found != null && found.Mode != null ? found : null;
        }

        /// <summary>The built-in panel's current refresh rate, 0 when unknown.</summary>
        public static int CurrentHz() {
            PanelState p = Query();
            return p == null ? 0 : (int)Math.Round(p.Current);
        }

        /// <summary>Refresh rates the panel offers at its current resolution, ascending.</summary>
        public static int[] Rates() {
            PanelState p = Query();
            if (p == null) return new int[0];
            var set = new SortedDictionary<int, bool>();
            foreach (var r in p.Rates) set[(int)Math.Round(r.Key)] = true;
            return new List<int>(set.Keys).ToArray();
        }

        /// <summary>The rates worth a button: the highest, and 60 Hz; the lowest at or above 48 Hz only on a panel
        /// without 60. The same rule as Windows, so the two pages offer the same buttons.</summary>
        public static int[] Choices() {
            var all = Rates();
            if (all.Length < 2) return new int[0];
            var pick = new SortedDictionary<int, bool>();
            int max = all[all.Length - 1];
            pick[max] = true;
            foreach (int r in all) if (r == 60) pick[60] = true;
            if (!pick.ContainsKey(60)) foreach (int r in all) if (r >= 48) { pick[r] = true; break; }
            var l = new List<int>(pick.Keys);
            return l.Count < 2 ? new int[0] : l.ToArray();
        }
        /// <summary>The battery rate: 60 Hz when the panel has it, else the lowest rate at or above 48 Hz, else the lowest.</summary>
        public static int BatteryHz() {
            var all = Rates();
            if (all.Length == 0) return 0;
            foreach (int r in all) if (r == 60) return 60;
            foreach (int r in all) if (r >= 48) return r;
            return all[0];
        }
        /// <summary>The panel's fastest rate.</summary>
        public static int HighestHz() {
            var all = Rates();
            return all.Length == 0 ? 0 : all[all.Length - 1];
        }

        /// <summary>Switch the built-in panel's refresh rate, keeping the resolution. Not persisted: X11 has no
        /// one place to persist it, the desktop's own display settings own that.</summary>
        public static bool SetHz(int hz) {
            try {
                PanelState p = Query();
                if (p == null) return false;
                if ((int)Math.Round(p.Current) == hz) return true;
                string exact = null;
                foreach (var r in p.Rates) if ((int)Math.Round(r.Key) == hz) { exact = r.Value; break; }
                if (exact == null) return false;
                string err;
                int rc = Host.Run(Host.Find("xrandr"), "--output " + p.Output + " --mode " + p.Mode + " --rate " + exact, 8000, out err);
                int now = CurrentHz();
                Log.Write("refresh rate " + (int)Math.Round(p.Current) + " -> " + hz + " Hz: xrandr rc " + rc + ", reads back " + now + " Hz");
                return rc == 0;
            } catch (Exception ex) { Log.Write("refresh rate: " + ex.Message); return false; }
        }

        /// <summary>Put the display to sleep; any input wakes it. X11 only (DPMS through xset).</summary>
        public static void Off() {
            try {
                string xset = Available ? Host.Find("xset") : null;
                if (xset == null) return;
                string o;
                Host.Run(xset, "dpms force off", 3000, out o);
            } catch (Exception ex) { Log.Write("display off: " + ex.Message); }
        }
    }
}
