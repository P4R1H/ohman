// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the support report: everything needed to add or fix a laptop, gathered in one go.
// src\win\Support.cs is the same class for Windows, and the sections that describe the firmware and the engine's
// decision are worded and ordered the same here, so a report from either system reads the same way: "Ohman's
// decision", the read-only BIOS queries, "Decoded", the driver, the log, the keyboard's HID interface, the
// current state. What describes the operating system is Linux's: the kernel, the distribution, lockdown, the
// kernel module and the helper, the stock hp-wmi driver, hwmon instead of Windows' thermal zones.
//
// Everything read here is read-only, and every line is scrubbed of the user name, home directory and host name
// before it goes out: this text exists to be pasted into a public issue.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ohman {

    public static class Support {

        /// <summary>Takes out anything that identifies the machine or its owner.</summary>
        public static string Scrub(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            try {
                string home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home) && home.Length > 1) s = s.Replace(home, "<home>");
                string user = Environment.UserName;
                // "root" is no secret and is a word the report uses; a user named after the laptop would turn the
                // model name into "<user>", so only a whole path segment is replaced for those.
                if (!string.IsNullOrEmpty(user) && user.Length > 2 && user != "root") {
                    if (Array.Exists(ProductWords, delegate(string w) { return string.Equals(w, user, StringComparison.OrdinalIgnoreCase); }))
                        s = s.Replace("/" + user + "/", "/<user>/");
                    else s = Replace(s, user, "<user>");
                }
                string host = Environment.MachineName;
                // A machine named after the laptop ("victus", "omen") would turn the model name into "<host> by HP
                // Laptop" on every line, and such a name gives nobody away.
                if (!string.IsNullOrEmpty(host) && host.Length > 2 && host != "localhost"
                    && !Array.Exists(ProductWords, delegate(string w) { return string.Equals(w, host, StringComparison.OrdinalIgnoreCase); })) s = Replace(s, host, "<host>");
            } catch { }
            return s;
        }
        static readonly string[] ProductWords = { "HP", "OMEN", "Victus", "HyperX", "Transcend", "Pavilion", "Gaming", "Laptop", "Envy", "Spectre" };
        static string Replace(string hay, string needle, string with) {
            int i, from = 0;
            while ((i = hay.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase)) >= 0) {
                hay = hay.Substring(0, i) + with + hay.Substring(i + needle.Length);
                from = i + with.Length;
            }
            return hay;
        }

        static string Hex(byte[] d, int max) {
            if (d == null) return "(null)";
            var sb = new StringBuilder();
            for (int i = 0; i < d.Length && i < max; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            return sb.ToString();
        }
        static string Num(double v, string unit) { return double.IsNaN(v) ? "--" : v.ToString("0", CultureInfo.InvariantCulture) + unit; }
        static string Dmi(string name) { return Host.ReadText("/sys/class/dmi/id/" + name); }

        /// <summary>One read-only BIOS query, reported whether it answers or not. A refusal is data.</summary>
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

        static string CpuName() {
            try {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal)) { int c = line.IndexOf(':'); if (c > 0) return line.Substring(c + 1).Trim(); }
            } catch { }
            return "";
        }
        static string Distro() {
            try {
                foreach (string line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) return line.Substring(12).Trim('"');
            } catch { }
            return "";
        }
        static string Gpus() {
            var list = new List<string>();
            try {
                const string root = "/sys/bus/pci/devices";
                foreach (string d in Directory.GetDirectories(root)) {
                    if (!Host.ReadText(Path.Combine(d, "class")).StartsWith("0x03", StringComparison.Ordinal)) continue;
                    string vendor = Host.ReadText(Path.Combine(d, "vendor")), device = Host.ReadText(Path.Combine(d, "device"));
                    string who = vendor == "0x10de" ? "NVIDIA" : vendor == "0x1002" ? "AMD" : vendor == "0x8086" ? "Intel" : vendor;
                    string driver = "";
                    try { driver = Path.GetFileName(Directory.ResolveLinkTarget(Path.Combine(d, "driver"), false).FullName); } catch { }
                    list.Add(who + " " + device.Replace("0x", "") + (driver.Length > 0 ? " (" + driver + ")" : " (no driver)"));
                }
            } catch { }
            return string.Join(" / ", list.ToArray());
        }

        const int UrlBudget = 5900;

        static string Summary(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("(filled in by Ohman " + Program.Version + " on Linux - the full report is on your clipboard, paste it in the box below)");
            sb.AppendLine();
            sb.AppendLine("Board " + e.Board + ", " + (e.Supported ? (e.Generic ? "driven from the firmware's own answers" : "verified profile") : "NOT DRIVEN - read-only") + (e.Desktop ? " (desktop)" : ""));
            if (e.P != null && e.Supported)
                sb.AppendLine("Mode bytes: eco 0x" + e.P.ModeEco.ToString("X2") + " balanced 0x" + e.P.ModeBalanced.ToString("X2")
                    + " performance 0x" + e.P.ModePerformance.ToString("X2") + " (v" + e.P.ThermalPolicy + ")"
                    + (e.P.ModeEco == e.P.ModeBalanced ? "  [eco and balanced are the same byte on this firmware]" : ""));
            sb.AppendLine("Fans: " + (e.FanCount > 0 ? "" + e.FanCount : "not reported") + "   Lighting: " + (e.Light == null ? (e.LightHidden != null ? "hidden in Settings" : "none detected") : e.Light.Describe));
            sb.AppendLine("Power gain: " + (e.Supported && e.P != null && e.P.HasPowerGain ? "yes" : "no") + "   GPU power: " + (e.Supported && e.P != null && e.P.HasGpuPower ? "yes" : "no"));
            if (e.LastError.Length > 0) sb.AppendLine("Last BIOS error: " + Scrub(e.LastError));
            sb.AppendLine();
            sb.AppendLine("What went wrong:");
            return sb.ToString();
        }

        /// <summary>A prefilled "New laptop support" form, the same one Windows opens. The report rides along
        /// only when it fits; the clipboard and the saved file carry it the rest of the time.</summary>
        public static string IssueUrl(Engine e, string report) {
            string model = Dmi("product_name");
            const string Base = "https://github.com/P4R1H/ohman/issues/new?template=new-laptop-support.yml";
            string url = Base
                + "&title=" + Uri.EscapeDataString("Support: " + (model.Length > 0 ? model : "board " + e.Board) + " (board " + e.Board + ", Linux)")
                + "&model=" + Uri.EscapeDataString(model)
                + "&board=" + Uri.EscapeDataString(e.Board)
                + "&wrong=" + Uri.EscapeDataString(Summary(e));
            if (report == null || url.Length + report.Length > UrlBudget) return url;
            var sb2 = new StringBuilder();
            foreach (string line in report.Replace("\r\n", "\n").Split('\n')) {
                string s = line;
                while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
                sb2.Append(s.TrimEnd()).Append('\n');
            }
            string with = url + "&supportinfo=" + Uri.EscapeDataString(sb2.ToString());
            return with.Length <= UrlBudget ? with : url;
        }

        /// <summary>The driver alone, in enough detail to say why it is not working from a pasted text.</summary>
        public static string DriverReport(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("Ohman driver check  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Ohman " + Program.Version + (e.Hw.IsDemo ? "  (SIMULATED HARDWARE - not a real reading)" : "") + "   board " + e.Board + "   " + Scrub(Platforms.ReadModel()));
            sb.AppendLine("CPU:       " + CpuName());
            sb.AppendLine("Linux:     " + Distro() + "   kernel " + Host.ReadText("/proc/sys/kernel/osrelease"));
            sb.AppendLine("----------------------------------------");
            DriverSection(sb, e);
            return sb.ToString();
        }

        /// <summary>How this process reaches the hardware and what stands in the way, for both reports.</summary>
        static void RouteSection(StringBuilder sb, Engine e) {
            sb.AppendLine("Routes to the firmware");
            sb.AppendLine("  kernel module:    " + (KernelModule.Present ? KernelModule.Device + " present" : "absent (ohman-wmi is not loaded)"));
            sb.AppendLine("  helper socket:    " + HelperProtocol.SocketPath + (File.Exists(HelperProtocol.SocketPath) ? " present" : " absent (ohman-helper is not running)"));
            sb.AppendLine("  this process:     " + (Native.IsRoot ? "root" : "an ordinary user") + ", reaches the hardware through: " + Mailbox.RouteName
                + (Mailbox.Reachable ? "" : "   <- " + Scrub(Route.WhyNone)));
            if (Route.Now == Route.Via.Helper) {
                try {
                    Dictionary<string, string> st = HelperClient.Status();
                    var parts = new List<string>();
                    foreach (string k in new string[] { "helper", "protocol", "demo", "module", "module_version", "lockdown", "msr", "hp_wmi", "pwm1_enable", "hotkeys" }) {
                        string v;
                        if (st.TryGetValue(k, out v)) parts.Add(k + "=" + v);
                    }
                    sb.AppendLine("  helper says:      " + Scrub(string.Join("  ", parts.ToArray())));
                    string pv;
                    if (st.TryGetValue("protocol", out pv) && pv != HelperProtocol.Version.ToString()) sb.AppendLine("  NOTE: the helper speaks protocol " + pv + " and this build " + HelperProtocol.Version + "; they are from different versions of Ohman");
                } catch (Exception ex) { sb.AppendLine("  helper status:    failed (" + Scrub(ex.Message) + ")"); }
            }
            string lockdown = Msr.Lockdown();
            sb.AppendLine("  lockdown:         " + (lockdown.Length == 0 ? "not reported by this kernel" : lockdown) + (lockdown == "integrity" || lockdown == "confidentiality" ? "   <- MSR writes are refused; an unsigned module will not load" : ""));
            sb.AppendLine("  stock hp-wmi:     " + (HpWmi.Present ? "loaded" : "not loaded"));
            sb.AppendLine("  platform_profile: " + Host.ReadText("/sys/firmware/acpi/platform_profile") + "   choices: " + Host.ReadText("/sys/firmware/acpi/platform_profile_choices"));
            string h = HpWmi.Hwmon();
            if (h != null) {
                string en = Host.ReadText(h + "/pwm1_enable");
                bool cached = HpWmi.CachesFanMode(h);
                sb.AppendLine("  hp-wmi fans:      pwm1_enable=" + en + (!cached ? "   (no pwm1: on this kernel that is the firmware's live max-fan flag, 0 = max fan on; Ohman never writes it)"
                        : en == "2" || en == "" ? "" : "   <- not automatic: hp-wmi re-sends its own fan state every 90 s")
                    + "   fan1=" + Host.ReadText(h + "/fan1_input") + " fan2=" + Host.ReadText(h + "/fan2_input")
                    + "   manual pwm: " + (File.Exists(h + "/pwm1") ? "yes" : "no"));
            }
            sb.AppendLine("  hotkey device:    " + (HpWmi.HotkeyDevice() ?? "none named \"" + HpWmi.HotkeyName + "\""));
        }

        /// <summary>The driver section shared by the support report and the driver check.</summary>
        static void DriverSection(StringBuilder sb, Engine e) {
            sb.AppendLine("Driver (" + Driver.Name + " kernel module and ohman-helper)");
            if (e.Hw.IsDemo) { sb.AppendLine("  (simulated hardware: a pretend driver with pretend registers)"); }
            else {
                Version v = e.DriverVersion;
                sb.AppendLine("  installed:   " + (v != null ? "interface " + v + (e.DriverOutdated ? "   <- older than " + Driver.MinVersion + ", update it" : "") : "no"));
                sb.AppendLine("  service:     " + Driver.ServiceState());
                sb.AppendLine("  setting:     " + (e.S.DriverUse ? "on" : "off"));
                // The msr driver is the helper's to see when the helper is the route; the routes section printed its word.
                if (Route.Now == Route.Via.Device) sb.AppendLine("  msr driver:  " + (Msr.Present ? "loaded" : "not loaded (modprobe msr)") + "   lockdown: " + (Msr.Lockdown().Length == 0 ? "not reported" : Msr.Lockdown()));
            }
            sb.AppendLine("  cpu module:  " + (e.Cpu != null ? e.Cpu.Describe : "unavailable (" + Scrub(e.DriverWhy) + ")"));
            sb.AppendLine("  ec map:      " + (e.Ec != null ? e.Ec.Map.Name + (e.Ec.Resting ? "   (resting after " + e.Ec.Timeouts + " timeouts)" : "") : e.P != null && e.P.Ec != null ? "have one, not open (" + Scrub(e.DriverWhy) + ")" : "none for board " + e.Board + " - EC is never touched here"));
            if (e.Ec != null) sb.AppendLine("  ec proof:    " + (e.EcVerified ? "fits: " : "REJECTED: ") + Scrub(e.EcProof));
            sb.AppendLine("  needs:       " + (e.P != null && e.P.DriverFor != DriverFor.None ? e.P.DriverFor.ToString() : "nothing the mailbox cannot do") + "   fan route: " + e.Route);
            if (e.Cpu == null && e.Ec == null) return;
            sb.AppendLine();
            sb.AppendLine("Readings (driver on the left, what Ohman had without it on the right)");
            CpuTelemetry t = null;
            try { t = e.Cpu != null ? e.Cpu.Poll() : null; } catch (Exception ex) { sb.AppendLine("  cpu poll failed: " + Scrub(ex.Message)); }
            if (t != null) {
                System.Threading.Thread.Sleep(600);      // the energy counter and the clock need a window to divide by
                try { CpuTelemetry t2 = e.Cpu.Poll(); if (!double.IsNaN(t2.Watts)) t.Watts = t2.Watts; if (!double.IsNaN(t2.DieTemp)) t.DieTemp = t2.DieTemp; if (!double.IsNaN(t2.Mhz)) t.Mhz = t2.Mhz; } catch { }
                string source;
                double hwmon = Sensors.PackageTempOnce(out source);
                int mailbox = -1;
                try { mailbox = e.Hw.GetTemperature(); } catch { }
                sb.AppendLine("  die temperature: " + Num(t.DieTemp, " C").PadRight(10) + " hwmon (" + (source ?? "none") + "): " + Num(hwmon, " C").PadRight(10) + " ACPI zone: " + Num(Sensors.AcpiZoneOnce(), " C").PadRight(10)
                    + " mailbox 0x23 (ambient): " + (mailbox >= 0 ? mailbox + " C" : "--") + (t.TjMax > 0 ? "   TjMax " + t.TjMax : ""));
                sb.AppendLine("  power limits:    PL1 " + Num(t.Pl1, " W") + (t.Pl1On ? "" : " (off)") + "   PL2 " + Num(t.Pl2, " W") + (t.Pl2On ? "" : " (off)") + (t.PlLocked ? "   locked by firmware" : "") + "   package " + Num(t.Watts, " W"));
                sb.AppendLine("  throttling:      " + (t.Throttle.Length > 0 ? t.Throttle : "no"));
                double khz;
                double cpufreq = double.TryParse(Host.ReadText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq"), out khz) ? khz / 1000.0 : double.NaN;
                sb.AppendLine("  clock:           " + Num(t.Mhz, " MHz").PadRight(12) + " cpufreq (core 0): " + Num(cpufreq, " MHz"));
            }
            if (e.Ec != null) {
                EcReading r = e.Ec.Read();
                int[] f = null;
                try { f = e.Hw.GetFanLevels(); } catch { }
                if (!r.Any) sb.AppendLine("  ec: no answer (" + Scrub(e.Ec.LastError) + ")");
                else {
                    sb.AppendLine("  ec temperatures: CPU " + (r.Cpu >= 0 ? r.Cpu + " C" : "--") + "   GPU " + (r.Gpu >= 0 ? r.Gpu + " C" : "--"));
                    string mb1 = (f != null && f.Length > 0 && f[0] >= 0) ? (f[0] * 100).ToString() : "--";
                    string mb2 = (f != null && f.Length > 1 && f[1] >= 0) ? (f[1] * 100).ToString() : "--";
                    sb.AppendLine("  ec fan rpm:      " + (r.Rpm1 >= 0 ? r.Rpm1.ToString() : "--") + " / " + (r.Rpm2 >= 0 ? r.Rpm2.ToString() : "--") + "   mailbox 0x2D: " + mb1 + " / " + mb2);
                    sb.AppendLine("  ec control:      manual 0x" + (r.Manual >= 0 ? r.Manual.ToString("X2") : "??") + "   countdown " + (r.Countdown >= 0 ? r.Countdown + " s" : "--")
                        + "   mode 0x" + (r.Mode >= 0 ? r.Mode.ToString("X2") : "??") + "   charge " + (r.Charge >= 0 ? r.Charge.ToString() : "--"));
                }
                if (Program.FanProbe && e.EcVerified && e.EcFanRouteWanted) {
                    sb.AppendLine();
                    sb.AppendLine("Which register drives the fans (writes, briefly; only ever asks for more air than is already moving)");
                    int pair;
                    sb.Append(e.Ec.ProbeFanWrite(e.P.Curve.Fallback, e.P.Curve.Ceiling, out pair));
                    e.EcPairFound = pair;
                } else if (e.EcVerified && e.EcFanRouteWanted) {
                    sb.AppendLine("  Run ohman-probe --driver --fantest (as root) to find which register actually drives the fans.");
                }
            }
        }

        public static string Report(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("Ohman support report  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Ohman " + Program.Version + " (Linux)" + (e.Hw.IsDemo ? "  (SIMULATED HARDWARE - not a real reading)" : ""));
            sb.AppendLine("----------------------------------------");

            // ---- the machine
            sb.AppendLine("Model:     " + Dmi("product_name") + "   (family " + Dmi("product_family") + ", SKU " + Dmi("product_sku") + ")");
            sb.AppendLine("Board:     " + e.Board + (string.IsNullOrEmpty(Platforms.BoardOverride) ? "" : "  (--board)"));
            sb.AppendLine("BIOS:      " + Dmi("bios_version") + "  " + Dmi("bios_date"));
            sb.AppendLine("CPU:       " + CpuName());
            sb.AppendLine("GPU:       " + Gpus());
            sb.AppendLine("Linux:     " + Distro() + "   kernel " + Host.ReadText("/proc/sys/kernel/osrelease"));
            sb.AppendLine("Session:   " + (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?") + "   desktop: " + (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"));
            sb.AppendLine("Root:      " + Native.IsRoot);
            sb.AppendLine();

            RouteSection(sb, e);
            sb.AppendLine();

            // ---- what Ohman decided, and why. Word for word the Windows block.
            sb.AppendLine("Ohman's decision");
            sb.AppendLine("  profile:  " + (e.Supported ? e.P.Name + (e.Generic ? "  [generic, built from the firmware]" : "  [verified entry]") : "UNSUPPORTED - read-only, nothing is written"));
            sb.AppendLine("  notes:    " + (e.P != null && e.P.Notes != null ? e.P.Notes : ""));
            sb.AppendLine("  bios ok:  " + e.BiosOk + (e.LastError.Length > 0 ? "   last error: " + Scrub(e.LastError) : ""));
            if (e.P != null && e.Supported)
                sb.AppendLine("  modes:    eco 0x" + e.P.ModeEco.ToString("X2") + " balanced 0x" + e.P.ModeBalanced.ToString("X2")
                    + " performance 0x" + e.P.ModePerformance.ToString("X2") + " cool 0x" + e.P.ModeCool.ToString("X2")
                    + "   (v" + e.P.ThermalPolicy + ")"
                    + (e.P.ModeEco == e.P.ModeBalanced ? "   <- eco and balanced are the same byte on this firmware" : ""));
            sb.AppendLine("  lighting: " + (e.Light == null ? (e.LightHidden != null ? "hidden in Settings by the owner (" + e.LightHidden + ")" : "none detected") : e.Light.Describe + (e.Light.Inert ? "  (firmware answers but drives nothing; per-key board)" : "")));
            sb.AppendLine("  fans:     " + e.FanCount + "   graphics: " + e.GpuMode);
            sb.AppendLine();

            // ---- the firmware, asked directly: the same list in the same order as Windows
            sb.AppendLine("BIOS queries (read-only; NO means the firmware refused, which is itself useful)");
            byte[] sys = null, fanTable = null;
            if (!e.Hw.IsDemo) {
                var z4 = new byte[4];
                sys = Probe(sb, "0x28 system data", Bios.CMD_DEFAULT, 0x28, z4, 128);
                Probe(sb, "0x2D fan levels", Bios.CMD_DEFAULT, 0x2D, z4, 128);
                fanTable = Probe(sb, "0x2F fan table", Bios.CMD_DEFAULT, 0x2F, z4, 128);
                Probe(sb, "0x2C fan types", Bios.CMD_DEFAULT, 0x2C, z4, 128);
                Probe(sb, "0x23 temp 0 (IR)", Bios.CMD_DEFAULT, 0x23, new byte[] { 0, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 1 (ambient)", Bios.CMD_DEFAULT, 0x23, new byte[] { 1, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 2 (PCH)", Bios.CMD_DEFAULT, 0x23, new byte[] { 2, 0, 0, 0 }, 4);
                Probe(sb, "0x23 temp 3 (VR)", Bios.CMD_DEFAULT, 0x23, new byte[] { 3, 0, 0, 0 }, 4);
                Probe(sb, "0x21 gpu power", Bios.CMD_DEFAULT, 0x21, z4, 4);
                Probe(sb, "0x26 max fan", Bios.CMD_DEFAULT, 0x26, z4, 4);
                Probe(sb, "0x2B keyboard type", Bios.CMD_DEFAULT, 0x2B, z4, 4);
                Probe(sb, "0x52 graphics mode", Bios.CMD_BIOS_READ, 0x52, z4, 4);
                // 0x10 is deliberately not here: that query is the firmware's user-defined-fan trigger, not a read.
                Probe(sb, "0x20009/01 support", BiosLighting.CMD, 0x01, z4, 128);
                Probe(sb, "0x20009/02 colours", BiosLighting.CMD, 0x02, new byte[] { 0 }, 128, 40);
                Probe(sb, "0x20009/04 backlight", BiosLighting.CMD, 0x04, new byte[] { 0 }, 128);
            } else sb.AppendLine("  (simulated hardware: nothing was asked)");
            sb.AppendLine();

            sb.AppendLine("Decoded");
            if (sys != null && sys.Length >= 9 && !Array.Exists(sys, delegate(byte b) { return b != 0; })) {
                sb.AppendLine("  all zeros: not an answer, so the thermal-policy version is unknown (not v0)");
            } else if (sys != null && sys.Length >= 9) {
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
                sb.AppendLine("  readback. An OMEN Gaming Hub log (from Windows) is the best source; see docs/laptops.md.");
            }
            sb.AppendLine();

            // ---- which sensor the temperature comes from
            sb.AppendLine("Temperature sources (Ohman uses the CPU's own sensor, else the hottest valid ACPI zone)");
            try {
                string source;
                double pkg = Sensors.PackageTempOnce(out source);
                sb.AppendLine("  hwmon package sensor:  " + (source == null ? "none (no coretemp, k10temp or zenpower)" : source + "  " + Num(pkg, " C")));
                const string hw = "/sys/class/hwmon";
                if (Directory.Exists(hw)) {
                    var names = new List<string>();
                    foreach (string d in Directory.GetDirectories(hw)) names.Add(Host.ReadText(Path.Combine(d, "name")));
                    names.Sort(StringComparer.Ordinal);
                    sb.AppendLine("  hwmon drivers:         " + string.Join(", ", names.ToArray()));
                }
                const string tz = "/sys/class/thermal";
                int zones = 0;
                if (Directory.Exists(tz)) {
                    string[] dirs = Directory.GetDirectories(tz, "thermal_zone*");
                    Array.Sort(dirs, StringComparer.Ordinal);
                    foreach (string d in dirs) {
                        string type = Host.ReadText(Path.Combine(d, "type"));
                        double milli;
                        bool ok = double.TryParse(Host.ReadText(Path.Combine(d, "temp")), NumberStyles.Float, CultureInfo.InvariantCulture, out milli);
                        double c = milli / 1000.0;
                        sb.AppendLine("  " + (Path.GetFileName(d) + " " + type).PadRight(30) + (ok ? c.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7) + " C" + (type != "acpitz" ? "   (not an ACPI zone, not used)" : c < 10 || c > 125 ? "   (outside 10-125 C, ignored)" : "") : " unreadable"));
                        zones++;
                    }
                }
                if (zones == 0) sb.AppendLine("  (no thermal zones exposed by this machine)");
                sb.AppendLine("  If the number Ohman shows never moves while the machine is busy, it is not the CPU. Say so in the report.");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            DriverSection(sb, e);
            sb.AppendLine();

            // ---- the GPU's own limits, for any report about wattage
            sb.AppendLine("NVIDIA power limits (name, limit, default, min, max, enforced now, drawing now, pstate)");
            try {
                string smi = Host.Find("nvidia-smi");
                if (smi == null) sb.AppendLine("  (nvidia-smi not present; AMD or iGPU only, or the NVIDIA driver is not installed)");
                else {
                    string o;
                    int rc = Host.Run(smi, "--query-gpu=name,power.limit,power.default_limit,power.min_limit,power.max_limit,enforced.power.limit,power.draw,pstate --format=csv,noheader", 8000, out o);
                    if (o.Trim().Length == 0) sb.AppendLine("  (no output, rc " + rc + ")");
                    foreach (string line in o.Split('\n')) if (line.Trim().Length > 0) sb.AppendLine("  " + Scrub(line.Trim()));
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            sb.AppendLine("Display");
            try {
                if (!Display.Available) sb.AppendLine("  not available in this session (the refresh rate is set through xrandr, which needs X11)");
                else sb.AppendLine("  now:      " + Display.CurrentHz() + " Hz   offered: " + string.Join(", ", Display.Rates()) + "   buttons: " + string.Join(", ", Display.Choices()));
                sb.AppendLine("  settings: RefreshHz=" + e.S.RefreshHz + "  LowHzOnBattery=" + e.S.LowHzOnBattery + "  on battery: " + e.OnBattery);
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            sb.AppendLine("Host");
            sb.AppendLine("  power profiles:  " + (Host.HasPowerModeSync ? "power-profiles-daemon present" : "no powerprofilesctl") + "   autostart: " + (Autostart.IsOn() ? "on" : "off"));
            sb.AppendLine("  files:           settings " + Scrub(AppPaths.StateFile) + "   log " + Scrub(Log.Path));
            sb.AppendLine();

            // ---- and what Ohman itself logged getting here
            sb.AppendLine("Ohman log (the decisions, not the whole file)");
            try {
                string[] marks = { "platform:", "generic profile", "BIOS ok", "self-test", "read-only", "no fan table", "system data", "thermal zone", "keyboard lighting",
                    "FAIL ", "driver:", "fan route", "EC ", "EC:", "giving up", "lighting:", "lamparray", "max fan:", "fan ceiling", "refresh rate", "mcu keyboard", "per-key board",
                    "hotkey ", "cpu temperature", "display on", "display off", "fans held by the firmware", "fans back with the curve", "GPU power query", "no GPU power control",
                    "parked fans", "fans handed", "---- ", "works again after", "hotkey watcher", "mailbox:", "hid " };
                var keep = new List<string>();
                foreach (string line in File.ReadLines(Log.Path)) {
                    bool want = line.IndexOf("thermal guard", StringComparison.OrdinalIgnoreCase) >= 0;
                    for (int i = 0; !want && i < marks.Length; i++) want = line.IndexOf(marks[i], StringComparison.Ordinal) >= 0;
                    if (want) keep.Add(line);
                }
                int from = Math.Max(0, keep.Count - 60);
                for (int i = from; i < keep.Count; i++) sb.AppendLine("  " + Scrub(keep[i]));
                if (keep.Count == 0) sb.AppendLine("  (nothing notable yet)");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the keyboard's own HID interface, which is what matters on a per-key board
            sb.AppendLine("HID lighting interface");
            try {
                List<Hid.Info> all = Hid.Enumerate();
                int lighting = 0, vendor = 0;
                foreach (Hid.Info h in all) {
                    if (h.UsagePage != LampArray.UsagePageLighting) continue;
                    lighting++;
                    bool canOpen = false;
                    try { IntPtr fd = Hid.Open(h.Path); if (fd != Hid.Invalid) { canOpen = true; Hid.CloseHandle(fd); } } catch { }
                    sb.AppendLine("  " + h + "  " + h.Path + (canOpen ? "" : "   <- cannot open (hidraw is root's without the udev rule)"));
                }
                if (lighting == 0) sb.AppendLine("  no HID Lighting And Illumination collection (usage page 0x59) on this machine");
                foreach (Hid.Info h in all) {
                    if (h.UsagePage < 0xFF00) continue;
                    if (h.VendorId != 0x0461 && h.VendorId != 0x0D62 && h.VendorId != 0x04F2 && h.VendorId != 0x048D) continue;
                    sb.AppendLine("  vendor " + h + "  " + h.Path);
                    vendor++;
                }
                if (vendor == 0) sb.AppendLine("  no vendor collection from a known keyboard maker (Primax, Darfon, Chicony, ITE)");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            sb.AppendLine("Current state");
            foreach (string line in e.Diagnostics().Split('\n')) {
                string l = line.TrimEnd();
                if (l.Length > 0) sb.AppendLine("  " + Scrub(l));
            }
            return sb.ToString();
        }
    }
}
