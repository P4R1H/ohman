// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the support report: everything needed to add or fix a laptop, gathered in one go.
//
// This exists because the first day of reports was mostly people being asked for one more thing. Every question
// that came up (which commands does this firmware answer, which thermal-policy version, does the keyboard
// declare a backlight, which ACPI zone is being read, what did OMEN Gaming Hub itself decide) is answered here
// without a second round trip. Ohman is already running elevated, so it can ask the firmware directly rather
// than sending somebody to find a script and elevate it again.
//
// Everything read here is read-only, and every line is scrubbed of the user name, profile path and machine name
// before it goes out: this text exists to be pasted into a public issue.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;

namespace Ohman {

    public static class Support {

        /// <summary>Takes out anything that identifies the machine or its owner. The report is meant to be pasted
        /// into a public issue, and paths under C:\Users carry a real name.</summary>
        public static string Scrub(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            try {
                string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(prof)) s = Replace(s, prof, "<userprofile>");
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length > 2) s = Replace(s, user, "<user>");
                string host = Environment.MachineName;
                if (!string.IsNullOrEmpty(host) && host.Length > 2) s = Replace(s, host, "<host>");
            } catch { }
            return s;
        }
        static string Replace(string hay, string needle, string with) {
            int i;
            int from = 0;
            while ((i = hay.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase)) >= 0) {
                hay = hay.Substring(0, i) + with + hay.Substring(i + needle.Length);
                from = i + with.Length;
            }
            return hay;
        }

        /// <summary>An OGH log line is "timestamp [INF] [PID: n] [TID: n] the interesting part". Drop the
        /// timestamp, then every bracketed field that follows, and keep what is left.</summary>
        static string Strip(string line) {
            int at = line.IndexOf("[INF]", StringComparison.Ordinal);
            string v = at >= 0 ? line.Substring(at + 5).TrimStart() : line.TrimStart();
            while (v.Length > 0 && v[0] == '[') {
                int close = v.IndexOf(']');
                if (close < 0) break;
                v = v.Substring(close + 1).TrimStart();
            }
            return v.Trim();
        }

        static string OghLogDir() {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Packages";
            if (!System.IO.Directory.Exists(root)) return null;
            foreach (string d in System.IO.Directory.GetDirectories(root, "AD2F1837.OMENCommandCenter*")) {
                string c = d + "\\LocalCache\\Local\\HPOMEN";
                if (System.IO.Directory.Exists(c)) return c;
            }
            return null;
        }
        static List<System.IO.FileInfo> OghLogs(string dir) {
            var files = new List<System.IO.FileInfo>();
            foreach (string f in System.IO.Directory.GetFiles(dir, "HPOMEN*.log")) files.Add(new System.IO.FileInfo(f));
            files.Sort(delegate(System.IO.FileInfo a, System.IO.FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            return files;
        }

        static string Hex(byte[] d, int max) {
            if (d == null) return "(null)";
            var sb = new StringBuilder();
            for (int i = 0; i < d.Length && i < max; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            return sb.ToString();
        }

        /// <summary>Lines of a file another program is still writing. File.ReadLines asks for exclusive write, and
        /// OMEN Gaming Hub keeps today's log open, so the newest log (the one that matters) was skipped in silence.</summary>
        static IEnumerable<string> ReadShared(string path) {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            using (var r = new System.IO.StreamReader(fs)) {
                string line;
                while ((line = r.ReadLine()) != null) yield return line;
            }
        }

        /// <summary>One read-only BIOS query, reported whether it answers or not. A refusal is data: it is how we
        /// learned that board 8574 implements 0x20009 and none of 0x20008.</summary>
        static byte[] Probe(StringBuilder sb, string label, uint cmd, uint op, byte[] data, int outSize, int show = 24) {
            try {
                var d = Bios.Call(cmd, op, data, outSize);
                sb.AppendLine("  " + label.PadRight(22) + "ok   " + Hex(d, show));
                return d;
            } catch (Exception ex) {
                sb.AppendLine("  " + label.PadRight(22) + "NO   " + Scrub(ex.Message));
                return null;
            }
        }

        /// <summary>Measured against the real endpoint: a new-issue URL up to about 6,100 characters answers 200,
        /// from roughly 7,000 it answers 500, and past 8,300 it answers 414. This is that ceiling with room to
        /// spare.</summary>
        const int UrlBudget = 5900;

        /// <summary>The handful of facts that decide whether a board can be driven, short enough to travel in a
        /// URL. The full report cannot: squeezed and escaped it is over 8,000 characters, and GitHub answers 500
        /// from about 7,000 and 414 past 8,300. So the issue opens already saying what this machine is and what
        /// its firmware refused, and the report itself arrives with one Ctrl+V from the clipboard.</summary>
        static string Summary(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("(filled in by Ohman " + Program.Version + " - the full report is on your clipboard, paste it in the box below)");
            sb.AppendLine();
            sb.AppendLine("Board " + e.Board + ", " + (e.Supported ? (e.Generic ? "driven from the firmware's own answers" : "verified profile") : "NOT DRIVEN - read-only"));
            if (e.P != null)
                sb.AppendLine("Mode bytes: eco 0x" + e.P.ModeEco.ToString("X2") + " balanced 0x" + e.P.ModeBalanced.ToString("X2")
                    + " performance 0x" + e.P.ModePerformance.ToString("X2") + (e.P.ModeUnleashed != 0 ? " unleashed 0x" + e.P.ModeUnleashed.ToString("X2") : "") + " (v" + e.P.ThermalPolicy + ")"
                    + (e.P.ModeEco == e.P.ModeBalanced ? "  [eco and balanced are the same byte on this firmware]" : ""));
            sb.AppendLine("Fans: " + e.FanCount + "   Lighting: " + (e.Light == null ? "none detected" : e.Light.Describe));
            sb.AppendLine("Power gain: " + (e.P != null && e.P.HasPowerGain ? "yes" : "no") + "   GPU power: " + (e.P != null && e.P.HasGpuPower ? "yes" : "no"));
            if (e.LastError.Length > 0) sb.AppendLine("Last BIOS error: " + Scrub(e.LastError));
            sb.AppendLine();
            sb.AppendLine("What went wrong:");
            return sb.ToString();
        }

        /// <summary>A prefilled "New laptop support" form. The report rides along only when it fits, because
        /// GitHub's failure past the limit is not graceful: a reporter landed in the 500 band and watched the form
        /// throw his data away and fall back to the blank template, which is what "the template is corrupted"
        /// turned out to mean. The clipboard and the saved file carry the report the rest of the time.</summary>
        public static string IssueUrl(Engine e, string report) {
            string model = "";
            try {
                using (var s = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) model = "" + o["Model"];
            } catch { }
            const string Base = "https://github.com/P4R1H/ohman/issues/new?template=new-laptop-support.yml";
            string url = Base
                + "&title=" + Uri.EscapeDataString("Support: " + (model.Length > 0 ? model : "board " + e.Board) + " (board " + e.Board + ")")
                + "&model=" + Uri.EscapeDataString(model)
                + "&board=" + Uri.EscapeDataString(e.Board)
                + "&wrong=" + Uri.EscapeDataString(Summary(e));
            // Column padding is a quarter of the report and escapes to %20 three times over, so squeeze it for
            // the URL only. The clipboard and the file keep the aligned version, which is the readable one.
            // Escaping never shrinks a string, so if the raw report already overflows the budget the squeezed
            // and escaped one certainly will. Check that before building ~15 KB of throwaway strings on a click.
            if (report == null || url.Length + report.Length > UrlBudget) return url;
            var lines = report.Replace("\r\n", "\n").Split('\n');
            var sb2 = new StringBuilder();
            foreach (string line in lines) {
                string s = line;
                while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
                sb2.Append(s.TrimEnd()).Append('\n');
            }
            string tight = sb2.ToString();
            string with = url + "&supportinfo=" + Uri.EscapeDataString(tight);
            return with.Length <= UrlBudget ? with : url;   // almost always the latter; the clipboard carries it
        }

        /// <summary>--driver and the Troubleshoot link: the driver alone, in enough detail to say why it is not
        /// working from a pasted text. Every line is read-only and scrubbed like the rest of the report.</summary>
        public static string DriverReport(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("Ohman driver check  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Ohman " + Program.Version + (e.Hw.IsDemo ? "  (SIMULATED HARDWARE - not a real reading)" : "") + "   board " + e.Board + "   " + Scrub(Platforms.ReadModel()));
            try {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("CPU:       " + o["Name"]);
                using (var s = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("Windows:   " + o["Caption"] + " " + o["Version"]);
            } catch { }
            sb.AppendLine("----------------------------------------");
            DriverSection(sb, e);
            return sb.ToString();
        }

        static string RegDword(string key, string value) {
            try {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)) {
                    if (k == null) return "no key";
                    object v = k.GetValue(value);
                    return v == null ? "not set" : Convert.ToInt32(v) != 0 ? "on" : "off";
                }
            } catch (Exception ex) { return "unreadable (" + ex.Message + ")"; }
        }
        static string Num(double v, string unit) { return double.IsNaN(v) ? "--" : v.ToString("0", CultureInfo.InvariantCulture) + unit; }

        /// <summary>The driver section shared by the support report and the driver check.</summary>
        static void DriverSection(StringBuilder sb, Engine e) {
            sb.AppendLine("Driver (PawnIO, optional)");
            if (e.Hw.IsDemo) { sb.AppendLine("  (simulated hardware: a pretend driver with pretend registers)"); }
            else {
                Version v = e.DriverVersion;
                sb.AppendLine("  installed:   " + (v != null ? v + "  at " + Scrub(PawnIo.InstallFolder() ?? "?") + (e.DriverOutdated ? "   <- older than " + PawnIo.MinVersion + ", update it" : "") : "no"));
                sb.AppendLine("  service:     " + PawnIo.ServiceState());
                sb.AppendLine("  setting:     " + (e.S.DriverUse ? "on" : "off") + "   restart pending: " + (e.S.DriverRestartPending ? "yes" : "no") + "   installed by Ohman: " + (e.S.DriverInstalledByOhman ? "yes" : "no"));
                // The two Windows features that decide whether a driver loads at all. PawnIO is signed and
                // HVCI-compatible, so both should read "on" without trouble; they are here because "on" plus a
                // driver that will not load is a different bug from "off".
                sb.AppendLine("  Secure Boot: " + RegDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled")
                    + "   Memory Integrity: " + RegDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled"));
                var others = new List<string>();
                foreach (string name in new string[] { "OmenMon", "FanControl", "LibreHardwareMonitor", "HWiNFO64", "HWiNFO32", "OmenCore", "OmenCommandCenterBackground", "ThrottleStop", "XTU" })
                    try { if (Process.GetProcessesByName(name).Length > 0) others.Add(name); } catch { }
                sb.AppendLine("  other EC/MSR users running: " + (others.Count > 0 ? string.Join(", ", others.ToArray()) : "none seen"));
            }
            sb.AppendLine("  cpu module:  " + (e.Cpu != null ? e.Cpu.Describe : "unavailable (" + Scrub(e.DriverWhy) + ")"));
            sb.AppendLine("  ec map:      " + (e.Ec != null ? e.Ec.Map.Name + (e.Ec.Resting ? "   (resting after " + e.Ec.Timeouts + " timeouts)" : "") : e.P != null && e.P.Ec != null ? "have one, not open (" + Scrub(e.DriverWhy) + ")" : "none for board " + e.Board + " - EC is never touched here"));
            // Whether the map was believed, and on what evidence. Nothing is written to a controller that has
            // not recognised its own registers here, so this line is the difference between a board the EC
            // route can rescue and one where it would have been writing into the dark.
            if (e.Ec != null) sb.AppendLine("  ec proof:    " + (e.EcVerified ? "fits: " : "REJECTED: ") + Scrub(e.EcProof));
            sb.AppendLine("  needs:       " + (e.P != null && e.P.DriverFor != DriverFor.None ? e.P.DriverFor.ToString() : "nothing the mailbox cannot do") + "   fan route: " + e.Route);
            if (e.Cpu == null && e.Ec == null) return;
            sb.AppendLine();
            sb.AppendLine("Readings (driver on the left, what Ohman had without it on the right)");
            CpuTelemetry t = null;
            try { t = e.Cpu != null ? e.Cpu.Poll() : null; } catch (Exception ex) { sb.AppendLine("  cpu poll failed: " + Scrub(ex.Message)); }
            if (t != null) {
                // The die answers in microseconds and the panel shows one reading every two seconds, so "why does
                // it jump around" is the first thing anybody will ask. Sample it properly, once, and let the
                // spread answer: a single glance at this sensor is one draw from the distribution below.
                var series = new List<int>();
                DateTime until = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < until) {
                    try { CpuTelemetry s2 = e.Cpu.Poll(false); if (!double.IsNaN(s2.DieTemp)) series.Add((int)Math.Round(s2.DieTemp)); } catch { break; }
                    System.Threading.Thread.Sleep(25);
                }
                System.Threading.Thread.Sleep(600);      // the energy counter needs a window it can divide by
                try { CpuTelemetry t2 = e.Cpu.Poll(); if (!double.IsNaN(t2.Watts)) t.Watts = t2.Watts; if (!double.IsNaN(t2.DieTemp)) t.DieTemp = t2.DieTemp; if (!double.IsNaN(t2.Mhz)) t.Mhz = t2.Mhz; } catch { }
                if (series.Count > 2) {
                    var sorted = new List<int>(series);
                    sorted.Sort();
                    int hot = e.P != null ? e.P.Guard.CpuHot : 90, over = 0;
                    foreach (int x in series) if (x >= hot) over++;
                    sb.AppendLine("  die over 2 s:    " + series.Count + " samples · min " + sorted[0] + " · median " + sorted[sorted.Count / 2]
                        + " · max " + sorted[sorted.Count - 1] + " · spread " + (sorted[sorted.Count - 1] - sorted[0]) + " degrees"
                        + "   at or above " + hot + ": " + over + " of " + series.Count);
                    sb.AppendLine("  first 40:        " + string.Join(" ", new List<string>(series.ConvertAll(delegate(int x) { return x.ToString(); })).GetRange(0, Math.Min(40, series.Count)).ToArray()));
                    sb.AppendLine("  The spread is the sensor answering faster than anything can be shown: any work on the");
                    sb.AppendLine("  machine lifts it tens of degrees for a few milliseconds. The panel, the fan curve and");
                    sb.AppendLine("  the thermal guard all follow the median of three readings, so a lone spike drives nothing.");
                }
                double acpi = Sensors.AcpiZoneOnce();
                int mailbox = -1;
                try { mailbox = e.Hw.GetTemperature(); } catch { }
                sb.AppendLine("  die temperature: " + Num(t.DieTemp, " C").PadRight(10) + " ACPI zone: " + Num(acpi, " C").PadRight(10) + " mailbox 0x23 (ambient): " + (mailbox >= 0 ? mailbox + " C" : "--") + (t.TjMax > 0 ? "   TjMax " + t.TjMax : ""));
                sb.AppendLine("  power limits:    PL1 " + Num(t.Pl1, " W") + (t.Pl1On ? "" : " (off)") + "   PL2 " + Num(t.Pl2, " W") + (t.Pl2On ? "" : " (off)") + (t.PlLocked ? "   locked by firmware" : "") + "   package " + Num(t.Watts, " W"));
                sb.AppendLine("  throttling:      " + (t.Throttle.Length > 0 ? t.Throttle : "no"));
                // Both numbers, because the counter one is what got reported as wrong and a report that shows
                // only the replacement cannot settle whether it is better.
                double counterMhz = double.NaN;
                try {
                    using (var pf = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true))
                    using (var pp = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true)) {
                        pf.NextValue(); pp.NextValue();
                        System.Threading.Thread.Sleep(400);
                        double b_ = pf.NextValue(), pct = pp.NextValue();
                        if (pct > 1 && pct < 500) counterMhz = b_ * pct / 100.0;
                    }
                } catch { }
                sb.AppendLine("  clock:           " + Num(t.Mhz, " MHz").PadRight(12) + " Windows counter: " + Num(counterMhz, " MHz"));
            }
            if (e.Ec != null) {
                EcReading r = e.Ec.Read();
                int[] f = null;
                try { f = e.Hw.GetFanLevels(); } catch { }
                if (!r.Any) sb.AppendLine("  ec: no answer (" + Scrub(e.Ec.LastError) + ")");
                else {
                    sb.AppendLine("  ec temperatures: CPU " + (r.Cpu >= 0 ? r.Cpu + " C" : "--") + "   GPU " + (r.Gpu >= 0 ? r.Gpu + " C" : "--"));
                    sb.AppendLine("  ec fan rpm:      " + (r.Rpm1 >= 0 ? r.Rpm1.ToString() : "--") + " / " + (r.Rpm2 >= 0 ? r.Rpm2.ToString() : "--")
                        + "   mailbox 0x2D: " + (f != null ? (f[0] * 100) + " / " + (f[1] * 100) : "--") + "   <- these should agree; if not, the map does not fit this board");
                    sb.AppendLine("  ec control:      manual 0x" + (r.Manual >= 0 ? r.Manual.ToString("X2") : "??") + "   countdown " + (r.Countdown >= 0 ? r.Countdown + " s" : "--")
                        + "   mode 0x" + (r.Mode >= 0 ? r.Mode.ToString("X2") : "??") + "   charge " + (r.Charge >= 0 ? r.Charge.ToString() : "--"));
                }
                // The one thing about this controller that reading it cannot answer. Only when asked for, because
                // everything else in this report is read-only and that is what it is advertised as.
                if (Program.FanProbe && e.EcVerified && !e.EcFanRouteWanted) {
                    sb.AppendLine();
                    sb.AppendLine("Which register drives the fans: not run.");
                    sb.AppendLine("  The firmware's own fan channel works on this board, so Ohman never writes these registers");
                    sb.AppendLine("  here and the answer would not be used anywhere. Writing them regardless is what stopped both");
                    sb.AppendLine("  fans on an 8C58 in 1.1: the firmware reads the same control byte, and a tool that changes it");
                    sb.AppendLine("  behind the firmware's back leaves it refusing every level afterwards.");
                } else if (Program.FanProbe && e.EcVerified) {
                    sb.AppendLine();
                    sb.AppendLine("Which register drives the fans (writes, briefly; only ever asks for more air than is already moving)");
                    int pair;
                    sb.Append(e.Ec.ProbeFanWrite(e.P.Curve.Fallback, e.P.Curve.Ceiling, out pair));
                    e.EcPairFound = pair;
                } else if (e.EcVerified && e.EcFanRouteWanted) {
                    sb.AppendLine("  Run tools\\drivertest.cmd and answer yes to the fan test to find which register actually drives them.");
                }
            }
        }

        public static string Report(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("Ohman support report  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Ohman " + Program.Version + (e.Hw.IsDemo ? "  (SIMULATED HARDWARE - not a real reading)" : ""));
            sb.AppendLine("----------------------------------------");

            // ---- the machine
            try {
                string model = "", sku = "", family = "";
                using (var s = new ManagementObjectSearcher("SELECT Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) { model = "" + o["Model"]; sku = "" + o["SystemSKUNumber"]; family = "" + o["SystemFamily"]; }
                sb.AppendLine("Model:     " + model + "   (family " + family + ", SKU " + sku + ")");
            } catch (Exception ex) { sb.AppendLine("Model:     unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine("Board:     " + e.Board);
            try {
                using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("BIOS:      " + o["SMBIOSBIOSVersion"] + "  " + ("" + o["ReleaseDate"]).Substring(0, 8));
            } catch { }
            try {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("CPU:       " + o["Name"]);
                var gpus = new List<string>();
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get()) gpus.Add("" + o["Name"]);
                sb.AppendLine("GPU:       " + string.Join(" / ", gpus.ToArray()));
                using (var s = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("Windows:   " + o["Caption"] + " " + o["Version"]);
            } catch { }
            sb.AppendLine("Elevated:  " + (!e.Hw.IsDemo));
            sb.AppendLine();

            // ---- what Ohman decided, and why
            sb.AppendLine("Ohman's decision");
            sb.AppendLine("  profile:  " + (e.Supported ? e.P.Name + (e.Generic ? "  [generic, built from the firmware]" : "  [verified entry]") : "UNSUPPORTED - read-only, nothing is written"));
            sb.AppendLine("  notes:    " + (e.P != null && e.P.Notes != null ? e.P.Notes : ""));
            sb.AppendLine("  bios ok:  " + e.BiosOk + (e.LastError.Length > 0 ? "   last error: " + Scrub(e.LastError) : ""));
            if (e.P != null)
                sb.AppendLine("  modes:    eco 0x" + e.P.ModeEco.ToString("X2") + " balanced 0x" + e.P.ModeBalanced.ToString("X2")
                    + " performance 0x" + e.P.ModePerformance.ToString("X2") + (e.P.ModeUnleashed != 0 ? " unleashed 0x" + e.P.ModeUnleashed.ToString("X2") : "") + " cool 0x" + e.P.ModeCool.ToString("X2")
                    + "   (v" + e.P.ThermalPolicy + ")"
                    + (e.P.ModeEco == e.P.ModeBalanced ? "   <- eco and balanced are the same byte on this firmware" : ""));
            sb.AppendLine("  lighting: " + (e.Light == null ? "none detected" : e.Light.Describe + (e.Light.Inert ? "  (firmware answers but drives nothing; per-key board)" : "")));
            sb.AppendLine("  fans:     " + e.FanCount + "   graphics: " + e.GpuMode);
            sb.AppendLine();

            // ---- the firmware, asked directly
            sb.AppendLine("BIOS queries (read-only; NO means the firmware refused, which is itself useful)");
            byte[] sys = null, fanTable = null;
            if (!e.Hw.IsDemo) {
                var z4 = new byte[4];
                sys = Probe(sb, "0x28 system data", Bios.CMD_DEFAULT, 0x28, z4, 128);
                Probe(sb, "0x2D fan levels", Bios.CMD_DEFAULT, 0x2D, z4, 128);
                fanTable = Probe(sb, "0x2F fan table", Bios.CMD_DEFAULT, 0x2F, z4, 128);
                Probe(sb, "0x2C fan types", Bios.CMD_DEFAULT, 0x2C, z4, 128);
                // 0x23 takes a sensor index. OGH's own device library names four: 0 IR, 1 Ambient, 2 PCH,
                // 3 VR. Ohman drives the curve from 1, and on a board whose ACPI zone reports a constant
                // that is the only moving number we have - so collect all four and find out which do move.
                Probe(sb, "0x23 temp 0 (IR)", Bios.CMD_DEFAULT, 0x23, new byte[] { 0, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 1 (ambient)", Bios.CMD_DEFAULT, 0x23, new byte[] { 1, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 2 (PCH)", Bios.CMD_DEFAULT, 0x23, new byte[] { 2, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 3 (VR)", Bios.CMD_DEFAULT, 0x23, new byte[] { 3, 0, 0, 0 }, 4);
                Probe(sb, "0x21 gpu power", Bios.CMD_DEFAULT, 0x21, z4, 4);
                Probe(sb, "0x26 max fan", Bios.CMD_DEFAULT, 0x26, z4, 4);
                Probe(sb, "0x2B keyboard type", Bios.CMD_DEFAULT, 0x2B, z4, 4);
                // The legacy mailbox, not the default one. Graphics mode is command 1 (read) / 2 (write) with
                // type 0x52, the way Bios.GetGpuMode asks for it. Sent to CMD_DEFAULT it is simply an unknown
                // command, so this line read NO on every laptop - including 8E10 and 8BCD, whose owners had
                // confirmed graphics switching works. It was then used as evidence that board 8BC2 refused the
                // read, which it never did: 8BC2 reported a mode on 1.0.5 and on 1.0.6 alike.
                Probe(sb, "0x52 graphics mode", Bios.CMD_BIOS_READ, 0x52, z4, 4);
                // 0x10 is deliberately not here: that query is the firmware's user-defined-fan trigger, not a read.
                Probe(sb, "0x20009/01 support", BiosLighting.CMD, 0x01, z4, 128);
                Probe(sb, "0x20009/02 colours", BiosLighting.CMD, 0x02, new byte[] { 0 }, 128, 40);   // the zone colours start at byte 25
                Probe(sb, "0x20009/04 backlight", BiosLighting.CMD, 0x04, new byte[] { 0 }, 128);
            } else sb.AppendLine("  (simulated hardware: nothing was asked)");
            sb.AppendLine();

            // ---- the same bytes, read out loud
            sb.AppendLine("Decoded");
            if (sys != null && sys.Length >= 9) {
                sb.AppendLine("  thermal policy:   v" + sys[3] + "   <- picks the mode bytes (v0 = 00/01/02, v1 = 30/31/50)");
                sb.AppendLine("  software fan:     " + (((sys[4] & 1) != 0) ? "yes" : "no"));
                sb.AppendLine("  PL4 default:      " + sys[5] + " W");
                sb.AppendLine("  base concurrent:  " + sys[8] + " W   <- where the power gain slider starts");
                var gm = new List<string>();
                if ((sys[7] & 1) != 0) gm.Add("iGPU only");
                if ((sys[7] & 2) != 0) gm.Add("Hybrid");
                if ((sys[7] & 4) != 0) gm.Add("Discrete");
                if ((sys[7] & 8) != 0) gm.Add("Advanced Optimus");
                sb.AppendLine("  graphics modes:   0x" + sys[7].ToString("X2") + "  " + (gm.Count > 0 ? string.Join(", ", gm.ToArray()) : "none offered"));
                if (fanTable != null && fanTable.Length >= 2) {
                    int rows = Math.Min((int)fanTable[1], 40), top = 0;
                    for (int i = 0; i < rows; i++) { int o = 2 + 3 * i; if (o + 1 < fanTable.Length) top = Math.Max(top, Math.Max(fanTable[o], fanTable[o + 1])); }
                    sb.AppendLine("  fan table:        " + fanTable[0] + " fans, " + fanTable[1] + " curve rows, top level " + top + " (about " + (top * 100) + " rpm)");
                }
            } else if (!e.Hw.IsDemo) {
                sb.AppendLine("  system data was refused. The thermal-policy version is what decides the mode bytes, so");
                sb.AppendLine("  without it this board cannot be driven from the firmware alone and needs a contributed");
                sb.AppendLine("  readback. An OMEN Gaming Hub log is the best source; see docs/laptops.md.");
            }
            sb.AppendLine();

            // ---- which sensor the temperature comes from
            sb.AppendLine("ACPI thermal zones (Ohman uses the hottest valid one)");
            try {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                string[] inst = cat.GetInstanceNames();
                Array.Sort(inst);
                if (inst.Length == 0) sb.AppendLine("  (none exposed by this machine)");
                foreach (string n in inst) {
                    PerformanceCounter pc = null;
                    try {
                        pc = new PerformanceCounter("Thermal Zone Information", "Temperature", n, true);
                        pc.NextValue();
                        System.Threading.Thread.Sleep(60);
                        double k = pc.NextValue();
                        bool usable = k >= 283 && k <= 398;
                        sb.AppendLine("  " + Scrub(n).PadRight(30) + Math.Round(k - 273.15, 1).ToString(CultureInfo.InvariantCulture).PadLeft(7)
                            + " C" + (usable ? "" : "   (outside 10-125 C, ignored)"));
                    } catch { sb.AppendLine("  " + Scrub(n).PadRight(30) + " unreadable"); }
                    finally { if (pc != null) try { pc.Dispose(); } catch { } }
                }
                sb.AppendLine("  If this never moves while the machine is busy, it is not the CPU. Say so in the report.");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the driver, if there is one, and what it reads
            DriverSection(sb, e);
            sb.AppendLine();

            // ---- what HP's own app concluded about this machine
            sb.AppendLine("OMEN Gaming Hub's own answers (from its log, if it has ever run)");
            try {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Packages";
                string dir = null;
                if (System.IO.Directory.Exists(root))
                    foreach (string d in System.IO.Directory.GetDirectories(root, "AD2F1837.OMENCommandCenter*")) {
                        string c = d + "\\LocalCache\\Local\\HPOMEN";
                        if (System.IO.Directory.Exists(c)) { dir = c; break; }
                    }
                if (dir == null) sb.AppendLine("  (OMEN Gaming Hub has never run here)");
                else {
                    string[] keys = { "IsCtgpModeSupport", "IsIccMaxSupport", "IsSurfaceTempSupport", "ChangeTppToDynamicBoost",
                                      "GetUnleashedModePowerLimit4", "GetUnleashedModeTppOffset", "IsEnableTgpPpab", "SetPL1DefaultValue",
                                      "TppMinValue", "TppMaxValue", "IsExtremeModeSupport", "IsUnleashedModeSupport" };
                    // PL1 differs per mode, and the first line alone said nothing about which: collect each distinct value.
                    var pl1 = new List<string>();
                    var found = new Dictionary<string, string>();
                    var files = new List<System.IO.FileInfo>();
                    foreach (string f in System.IO.Directory.GetFiles(dir, "HPOMEN*.log")) files.Add(new System.IO.FileInfo(f));
                    files.Sort(delegate(System.IO.FileInfo a, System.IO.FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    int n = 0;
                    foreach (var f in files) {
                        if (++n > 4) break;
                        try {
                            foreach (string line in ReadShared(f.FullName))
                                foreach (string k in keys)
                                    if (line.IndexOf(k, StringComparison.Ordinal) >= 0) {
                                        if (k == "SetPL1DefaultValue") { string v = Scrub(Strip(line)); if (pl1.Count < 8 && !pl1.Contains(v)) pl1.Add(v); }
                                        if (!found.ContainsKey(k)) found[k] = Scrub(Strip(line)).PadRight(64) + "  (" + f.LastWriteTime.ToString("yyyy-MM-dd") + ")";
                                    }
                        } catch { }
                    }
                    if (found.Count == 0) sb.AppendLine("  (no capability lines in the most recent logs)");
                    else sb.AppendLine("  (the date is the log each line came from - OGH may not have run recently)");
                    foreach (string k in keys) if (found.ContainsKey(k) && k != "SetPL1DefaultValue") sb.AppendLine("  " + found[k]);
                    foreach (string v in pl1) sb.AppendLine("  " + v);
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- OGH's cached copy of system data. This is the one that matters when the firmware refuses
            //      0x28: HP's own app stores the same bytes, and byte 3 is the thermal-policy version, which is
            //      the single thing standing between an undrivable board and a working one.
            sb.AppendLine("OMEN Gaming Hub's cached system data (works even when 0x28 is refused)");
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HP\OMEN Ally\Settings")) {
                    if (k == null) sb.AppendLine("  (no OGH settings key)");
                    else {
                        var raw = k.GetValue("SystemDesignData") as byte[];
                        if (raw == null) sb.AppendLine("  (key present, no SystemDesignData)");
                        else {
                            sb.AppendLine("  SystemDesignData = " + Hex(raw, 12));
                            if (raw.Length > 8) sb.AppendLine("  -> thermal policy v" + raw[3] + ", PL4 " + raw[5] + " W, base concurrent " + raw[8] + " W");
                        }
                        foreach (string n in new[] { "LoadedJsonSku", "LastLoadedJsonSku" }) {
                            object v = k.GetValue(n);
                            if (v != null) sb.AppendLine("  " + n + " = " + Scrub("" + v));
                        }
                    }
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the payloads OGH itself sends, which is ground truth for any byte we are unsure of
            sb.AppendLine("Payloads OMEN Gaming Hub sent (its own writes; ground truth for a byte we guessed)");
            try {
                string dir2 = OghLogDir();
                if (dir2 == null) sb.AppendLine("  (OMEN Gaming Hub has never run here)");
                else {
                    var counts = new Dictionary<string, int>();
                    var files2 = OghLogs(dir2);
                    int n2 = 0;
                    foreach (var f in files2) {
                        if (++n2 > 4) break;
                        try {
                            foreach (string line in ReadShared(f.FullName)) {
                                int at = line.IndexOf("inputData=", StringComparison.Ordinal);
                                if (at < 0) continue;
                                string v = line.Substring(at + 10).Trim().TrimEnd(',');
                                if (v.Length == 0 || v.Length > 40 || v.IndexOf(',') < 0 || v == "0,0,0,0" || v == "is null") continue;   // single values are not payloads
                                if (counts.ContainsKey(v)) counts[v]++;
                                else counts[v] = 1;
                            }
                        } catch { }
                    }
                    if (counts.Count == 0) sb.AppendLine("  (none in the most recent logs)");
                    else if (files2.Count > 0) {
                        var newest = files2[0].LastWriteTime;
                        int days = (int)(DateTime.Now - newest).TotalDays;
                        sb.AppendLine("  from logs last written " + newest.ToString("yyyy-MM-dd")
                            + (days >= 14 ? "  -- " + days + " days ago, so these are stale" : ""));
                    }
                    var keys2 = new List<string>(counts.Keys);
                    keys2.Sort(delegate(string a, string b) { return counts[b].CompareTo(counts[a]); });
                    int shown = 0;
                    foreach (string key in keys2) { if (++shown > 12) break; sb.AppendLine("  " + key.PadRight(24) + " x" + counts[key]); }
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the GPU's own limits, for any report about wattage
            sb.AppendLine("NVIDIA power limits");
            try {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,power.limit,power.default_limit,power.min_limit,power.max_limit --format=csv,noheader") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var pr = Process.Start(psi)) {
                    string o = pr.StandardOutput.ReadToEnd();
                    pr.WaitForExit(8000);
                    if (o.Trim().Length == 0) sb.AppendLine("  (no output; AMD or iGPU only)");
                    foreach (string line in o.Split('\n')) if (line.Trim().Length > 0) sb.AppendLine("  " + Scrub(line.Trim()));
                }
            } catch { sb.AppendLine("  (nvidia-smi not present; AMD or iGPU only)"); }
            sb.AppendLine();

            // ---- and what Ohman itself logged getting here
            sb.AppendLine("Ohman log (the decisions, not the whole file)");
            try {
                var keep = new List<string>();
                foreach (string line in System.IO.File.ReadLines(Log.Path)) {
                    if (line.IndexOf("platform:", StringComparison.Ordinal) >= 0 || line.IndexOf("generic profile", StringComparison.Ordinal) >= 0
                        || line.IndexOf("BIOS ok", StringComparison.Ordinal) >= 0 || line.IndexOf("self-test", StringComparison.Ordinal) >= 0
                        || line.IndexOf("read-only", StringComparison.Ordinal) >= 0 || line.IndexOf("no fan table", StringComparison.Ordinal) >= 0
                        || line.IndexOf("system data", StringComparison.Ordinal) >= 0 || line.IndexOf("thermal zone", StringComparison.Ordinal) >= 0
                        || line.IndexOf("keyboard lighting", StringComparison.Ordinal) >= 0 || line.IndexOf("THERMAL GUARD", StringComparison.Ordinal) >= 0
                        || line.IndexOf("FAIL ", StringComparison.Ordinal) >= 0 || line.IndexOf("driver:", StringComparison.Ordinal) >= 0
                        || line.IndexOf("fan route", StringComparison.Ordinal) >= 0 || line.IndexOf("EC ", StringComparison.Ordinal) >= 0
                        || line.IndexOf("EC:", StringComparison.Ordinal) >= 0 || line.IndexOf("giving up", StringComparison.Ordinal) >= 0
                        || line.IndexOf("lighting:", StringComparison.Ordinal) >= 0 || line.IndexOf("lamparray", StringComparison.Ordinal) >= 0
                        || line.IndexOf("max fan:", StringComparison.Ordinal) >= 0)
                        keep.Add(line);
                }
                int from = Math.Max(0, keep.Count - 60);    // a start writes about ten of these; keep several sessions
                for (int i = from; i < keep.Count; i++) sb.AppendLine("  " + Scrub(keep[i]));
                if (keep.Count == 0) sb.AppendLine("  (nothing notable yet)");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the keyboard's own HID interface, which is what matters on a per-key board
            sb.AppendLine("HID lighting interface");
            try {
                var arrays = LampArray.All();
                if (arrays.Count == 0) sb.AppendLine("  no HID Lighting And Illumination collection (usage page 0x59) on this machine");
                foreach (var a in arrays) {
                    var mi = System.Text.RegularExpressions.Regex.Match(a.Path ?? "", @"&mi_([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    sb.AppendLine("  VID_" + a.VendorId.ToString("X4") + "&PID_" + a.ProductId.ToString("X4") + (mi.Success ? "&MI_" + mi.Groups[1].Value.ToUpperInvariant() : "")
                        + (a.Internal ? "  internal  " : "  external  ") + Scrub(a.Describe));
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- and the running state, which is what Diagnostics already said well
            sb.AppendLine("Current state");
            foreach (string line in e.Diagnostics().Split('\n')) {
                string l = line.TrimEnd();
                if (l.Length > 0) sb.AppendLine("  " + Scrub(l));
            }
            return sb.ToString();
        }
    }
}
