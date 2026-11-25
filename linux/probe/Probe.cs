// SPDX-License-Identifier: GPL-3.0-or-later
// ohman-probe: what Ohman's shared engine makes of this laptop on Linux, from a terminal.
//
//   ohman-probe                        the support report (src/linux/Support.cs): the machine, the routes to the
//                                      firmware, the profile the engine builds, the read-only firmware queries,
//                                      the sensors, the driver. Writes nothing to the firmware.
//   ohman-probe --driver [--fantest]   the driver check alone; --fantest also finds which EC register pair
//                                      drives the fans (writes, briefly), where the board needs the EC for them
//   ohman-probe --demo [--board 8A25]  either report on simulated hardware (no root, no firmware)
//   ohman-probe --run SECONDS          run the whole engine for that long, as the GUI would (it APPLIES the
//                                      saved settings to the laptop), print what the sensors read, hand the fans
//                                      back and exit. With --demo it drives simulated hardware.
//   ohman-probe --sensors [N]          N sensor snapshots (default 3), nothing else
//   ohman-probe --scenario [FILE]      the scripted run (src/Scenario.cs); linux/test/parity.sh compares it with
//                                      the Windows build's
//   ohman-probe call <commandHex> <typeHex> <outSize> [bytes...]
//                                      one raw mailbox call, e.g. call 20008 28 128 00 00 00 00
//   ohman-probe ec <registerHex>       one EC register, read
//   ohman-probe msr <registerHex>      one MSR (of the ones Ohman reads), core 0
//   ohman-probe hid                    the HID collections hidraw shows, as the lighting code sees them
//   ohman-probe helper-test            every operation of ohman-helper, including the ones it must refuse
//   ohman-probe selftest               the parsers that cannot be tried on real devices here, against known input
//   ohman-probe bench-selftest | bench-watch [SECONDS] | bench-run
//                                      the benchmark: see BenchProbe.cs
//
// The hardware is reached through /dev/ohman-wmi when this runs as root, else through ohman-helper's socket
// (OHMAN_HELPER_SOCKET overrides the path). Neither there: say so, and suggest --demo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ohman {
    public static partial class Program {
        public static int Main(string[] args) {
            bool demo = false, driver = false, scenario = false, sensors = false;
            int run = -1, sensorCount = 3;
            string scenarioFile = null;
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++) {
                string a = args[i].ToLowerInvariant();
                if (a == "--demo") demo = true;
                else if (a == "--driver") driver = true;
                else if (a == "--fantest") FanProbe = true;
                else if (a == "--board" && i + 1 < args.Length) Platforms.BoardOverride = args[++i];
                else if (a == "--run" && i + 1 < args.Length) run = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--sensors") { sensors = true; if (i + 1 < args.Length && int.TryParse(args[i + 1], out int n)) { sensorCount = n; i++; } }
                else if (a == "--scenario") { scenario = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) scenarioFile = args[++i]; }
                else rest.Add(args[i]);
            }
            AppPaths.Demo = demo || scenario;          // before anything is logged: simulated runs keep their own files
            try {
                if (scenario) return RunScenario(scenarioFile);
                if (rest.Count > 0) {
                    switch (rest[0].ToLowerInvariant()) {
                        case "call": return RawCall(rest);
                        case "ec": return RawEc(rest);
                        case "msr": return RawMsr(rest);
                        case "hid": return ListHid();
                        case "helper-test": return HelperTest();
                        case "selftest": return SelfTest();
                        case "bench-selftest": return BenchSelfTest();
                        case "bench-watch": return BenchWatch(rest);
                        case "bench-run": return BenchRun_();
                        default: Console.Error.WriteLine("unknown argument: " + rest[0]); return 2;
                    }
                }
                if (sensors) return ShowSensors(null, sensorCount);
                if (!demo && !Mailbox.Reachable) {
                    Console.WriteLine("Ohman probe " + Version + " (Linux)");
                    Console.WriteLine(Route.WhyNone);
                    Console.WriteLine("Run this as root with the ohman-wmi module loaded, or start ohman-helper, or add --demo to see the engine on simulated hardware.");
                    return 1;
                }
                if (run >= 0) return RunEngine(demo, run);
                return Report(demo, driver);
            } catch (Exception ex) {
                Console.Error.WriteLine("failed: " + ex.Message);
                return 1;
            }
        }

        static int RunScenario(string file) {
            string text = Scenario.Run();
            if (file != null) File.WriteAllText(file, text, new UTF8Encoding(false));
            else Console.Write(text);
            return 0;
        }

        /// <summary>The report, built by an engine that describes the laptop and does not drive it: Init(false)
        /// writes no mode byte, starts no fan timer and stops nothing.</summary>
        static int Report(bool demo, bool driverOnly) {
            IHardware hw = demo ? new DemoHardware() : new Bios();
            var s = Settings.Load();
            s.NoPersist = true;
            var e = new Engine(hw, s);
            try { e.Init(false); } catch (Exception ex) { Console.WriteLine("engine init failed: " + ex.Message); }
            string rep;
            try { rep = driverOnly ? Support.DriverReport(e) : Support.Report(e); } catch (Exception ex) { rep = "report failed: " + ex; }
            Console.Write(rep);
            if (e.EcPairFound != 0) {
                try { File.WriteAllText(Engine.EcPairPath, e.EcPairFound == 2 ? "percent" : "rpm"); Console.WriteLine("recorded which fan register pair this board uses; Ohman reads it at its next start"); }
                catch (Exception ex) { Console.WriteLine("could not record the fan register pair: " + ex.Message); }
            }
            try {
                string p = Path.Combine(Path.GetDirectoryName(Log.Path), driverOnly ? "driver-check.txt" : "support-info.txt");
                File.WriteAllText(p, rep);
                Console.WriteLine("saved to " + p);
            } catch (Exception ex) { Console.WriteLine("could not save: " + ex.Message); }
            try { e.Dispose(); } catch { }
            return 0;
        }

        static string Fmt(double v, string unit) { return double.IsNaN(v) ? "--" : v.ToString("0", CultureInfo.InvariantCulture) + unit; }
        static string Line(SensorSnapshot s) {
            return "cpu " + Fmt(s.CpuTemp, " C") + (s.CpuFromDriver ? "" : " (zone)") + " load " + Fmt(s.CpuLoad, "%") + " " + Fmt(s.CpuMhz, " MHz") + " " + Fmt(s.CpuWatts, " W")
                + (double.IsNaN(s.Pl1) ? "" : " PL1 " + Fmt(s.Pl1, "") + " PL2 " + Fmt(s.Pl2, ""))
                + " · gpu " + Fmt(s.GpuTemp, " C") + " " + Fmt(s.GpuLoad, "%") + " " + Fmt(s.GpuWatts, " W") + (s.GpuAsleep ? " asleep" : "")
                + " · acpi " + Fmt(s.AcpiTemp, " C") + " · " + (s.OnBattery ? "battery" : "mains") + (s.BatteryPercent >= 0 ? " " + s.BatteryPercent + "%" : "");
        }

        static int ShowSensors(Engine e, int count) {
            using var sensors = new Sensors();
            if (e != null) sensors.CpuSource = () => e.Cpu;
            using var done = new ManualResetEvent(false);
            int seen = 0;
            sensors.Updated += s => {
                Console.WriteLine("  sensors: " + Line(s));
                if (++seen >= count) done.Set();
            };
            sensors.SetInterval(1000);
            sensors.Start();
            done.WaitOne(count * 3000 + 5000);
            return 0;
        }

        /// <summary>The engine as the GUI hosts it, for a while: the same Init, the same sensor feed.</summary>
        static int RunEngine(bool demo, int seconds) {
            IHardware hw = demo ? new DemoHardware() : new Bios();
            Console.WriteLine("Ohman probe " + Version + ": running the engine for " + seconds + " s on " + (demo ? "SIMULATED hardware" : "this laptop (through " + Mailbox.RouteName + ")"));
            var s = Settings.Load();
            s.NoPersist = true;
            var e = new Engine(hw, s);
            e.Toast += (m, err) => Console.WriteLine("  " + (err ? "toast! " : "toast  ") + m);
            e.KeyPressed += a => Console.WriteLine("  OMEN key -> " + a);
            e.AnyKeyEvent += (id, data) => Console.WriteLine("  firmware event " + id + "/" + data);
            e.Init();
            Console.WriteLine("  profile: " + (e.Supported ? e.P.Name : "unsupported") + " · bios ok " + e.BiosOk + " · driver " + (e.DriverReady ? "ready" : "none (" + e.DriverWhy + ")")
                + " · cpu " + (e.Cpu != null ? e.Cpu.Describe : "none") + " · ec " + (e.Ec != null ? (e.EcVerified ? "verified: " : "rejected: ") + e.EcProof : "none") + " · fan route " + e.Route);
            using (var sensors = new Sensors()) {
                sensors.CpuSource = () => e.Cpu;
                sensors.DeadZoneHint = s.DeadZone;
                sensors.Updated += snap => {
                    // What the window does with each snapshot (MainWindow.OnSensors), minus the drawing.
                    e.CpuTemp = snap.CpuTemp; e.CpuTempNow = snap.CpuTempNow; e.GpuTemp = snap.GpuAsleep ? double.NaN : snap.GpuTemp; e.GpuAsleep = snap.GpuAsleep;
                    e.NoteCpuSource(snap);
                    Console.WriteLine("  sensors: " + Line(snap) + " · written " + e.AutoLevel1 + "/" + e.AutoLevel2 + (e.GuardActive ? " GUARD" : ""));
                };
                sensors.Start();
                Thread.Sleep(Math.Max(0, seconds) * 1000);
            }
            foreach (string line in e.Diagnostics().Split('\n')) if (line.Trim().Length > 0) Console.WriteLine("  " + line.TrimEnd());
            e.Park();
            e.Dispose();
            Console.WriteLine("  fans handed back; done");
            return 0;
        }

        static int RawCall(List<string> a) {
            if (a.Count < 4) { Console.Error.WriteLine("usage: call <commandHex> <typeHex> <outSize> [bytes...]"); return 2; }
            uint command = uint.Parse(a[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint type = uint.Parse(a[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int outSize = int.Parse(a[3], CultureInfo.InvariantCulture);
            var data = new byte[Math.Max(0, a.Count - 4)];
            for (int i = 4; i < a.Count; i++) data[i - 4] = byte.Parse(a[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            try {
                byte[] o = Bios.Call(command, type, data, outSize);
                Console.WriteLine("rc=0  " + Bios.Hex(o, 64));
            } catch (BiosException ex) {
                Console.WriteLine("rc=" + ex.Code + "  (the firmware refused)");
            }
            return 0;
        }

        static int RawEc(List<string> a) {
            if (a.Count < 2) { Console.Error.WriteLine("usage: ec <registerHex>"); return 2; }
            byte reg = byte.Parse(a[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int rc = new LinuxEc().Read(reg, out byte v);
            Console.WriteLine(rc == 0 ? "ec[0x" + reg.ToString("X2") + "] = 0x" + v.ToString("X2") + " (" + v + ")" : "ec read failed: " + Native.Why(rc));
            return rc == 0 ? 0 : 1;
        }

        static int RawMsr(List<string> a) {
            if (a.Count < 2) { Console.Error.WriteLine("usage: msr <registerHex>"); return 2; }
            uint msr = uint.Parse(a[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            bool ok = new LinuxCpuPort().ReadMsr(msr, out ulong v);
            Console.WriteLine(ok ? "msr[0x" + msr.ToString("X") + "] = 0x" + v.ToString("X16") : "msr read failed (not one Ohman reads, no msr driver, or no route)");
            return ok ? 0 : 1;
        }

        static int ListHid() {
            List<Hid.Info> all = Hid.Enumerate();
            Console.WriteLine(all.Count + " HID collections under /sys/class/hidraw");
            foreach (Hid.Info h in all) {
                IntPtr fd = Hid.Open(h.Path);
                bool can = fd != Hid.Invalid;
                if (can) Hid.CloseHandle(fd);
                Console.WriteLine("  " + h + "  " + h.Path + (can ? "" : "  (cannot open)"));
            }
            foreach (LampArray la in LampArray.All()) {
                Console.WriteLine("LampArray " + la.VendorId.ToString("X4") + ":" + la.ProductId.ToString("X4") + "  " + la.Describe + "  per-key usable: " + la.UsableAsPerKey);
                la.Dispose();
            }
            return 0;
        }

        // ---------- the helper, operation by operation ----------
        static int failures;
        static void Check(string what, bool ok, string detail) {
            Console.WriteLine("  " + (ok ? "ok    " : "FAIL  ") + what + (string.IsNullOrEmpty(detail) ? "" : "   " + detail));
            if (!ok) failures++;
        }
        static Socket RawConnect() {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.SendTimeout = 3000; s.ReceiveTimeout = 3000;
            s.Connect(new UnixDomainSocketEndPoint(HelperProtocol.SocketPath));
            return s;
        }
        static int Count(Dictionary<string, string> status, string key) { return status.TryGetValue(key, out string v) && int.TryParse(v, out int n) ? n : -1; }
        static byte MsrWriteStatus(ulong value, out string said) {
            said = "";
            var p = new byte[12];
            Native.Put32(p, 0, Msr.PackagePowerLimit);
            Native.Put64(p, 4, value);
            try { HelperClient.Request(HelperProtocol.OpMsrWrite, p); return HelperProtocol.Ok; }
            catch (HelperException ex) { said = ex.Message; return ex.Status; }
        }
        static byte StatusOf(Action a) {
            try { a(); return HelperProtocol.Ok; }
            catch (HelperException ex) { return ex.Status; }
        }

        /// <summary>Every operation once, and every refusal the helper owes: a register no EC map names, an MSR
        /// Ohman does not read, a write to an MSR that is not the power limit, a malformed request. On a real
        /// helper nothing here writes to the hardware except the one MSR read-back test, which writes the power
        /// limit register the value it already holds. A --demo helper also gets its key-event hook exercised.
        /// Then the server's own limits: the connection cap, a half-sent frame, and a reply that never comes.
        /// Takes about half a minute, most of it waiting for timeouts to pass.</summary>
        static int HelperTest() {
            Console.WriteLine("ohman-helper at " + HelperProtocol.SocketPath);
            if (!HelperClient.Reachable) { Console.WriteLine("  not reachable: " + HelperClient.LastError); return 1; }
            Dictionary<string, string> st = HelperClient.Status();
            var sb = new StringBuilder();
            foreach (var kv in st) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");
            Check("status", st.ContainsKey("protocol"), sb.ToString().Trim());
            bool demo = st.TryGetValue("demo", out string d) && d == "1";

            uint rc;
            byte[] sys = null;
            try { sys = HelperClient.Mailbox(Bios.CMD_DEFAULT, Bios.OP_SYSTEM_DATA, new byte[4], 128, out rc); Check("mailbox 0x28 system data", rc == 0 && sys.Length == 128, "rc " + rc + "  " + Bios.Hex(sys, 12)); }
            catch (Exception ex) { Check("mailbox 0x28 system data", false, ex.Message); }
            try { HelperClient.Mailbox(Bios.CMD_DEFAULT, 0xEE, new byte[4], 4, out rc); Check("mailbox, a command the firmware does not have", rc != 0, "rc " + rc + " (a refusal by the firmware, passed through)"); }
            catch (Exception ex) { Check("mailbox, a command the firmware does not have", false, ex.Message); }
            Check("mailbox, a reply size that does not exist is refused", StatusOf(() => HelperClient.Mailbox(Bios.CMD_DEFAULT, 0x28, new byte[4], 77, out rc)) == HelperProtocol.BadRequest, "");

            int e = HelperClient.EcRead(0x57, out byte cpu);
            Check("ec read 0x57 (CPU temperature)", e == 0, e == 0 ? cpu + " C" : Native.Why(e));
            Check("ec write to a register no map names (0x00) is refused", StatusOf(() => HelperClient.Request(HelperProtocol.OpEcWrite, new byte[] { 0x00, 0x00 })) == HelperProtocol.NotAllowed, "");
            Check("ec write to the mode register (0x95, read-only in the map) is refused", StatusOf(() => HelperClient.Request(HelperProtocol.OpEcWrite, new byte[] { 0x95, 0x00 })) == HelperProtocol.NotAllowed, "");
            st.TryGetValue("ec_writes", out string ecWrites);
            bool ecOff = ecWrites != null && ecWrites.StartsWith("none");
            if (ecOff) {
                // A board no EC map applies to (an OMEN MAX): the helper must write nothing there, map register or not.
                string said = "";
                byte stt = HelperProtocol.Ok;
                try { HelperClient.Request(HelperProtocol.OpEcWrite, new byte[] { 0x34, 33 }); } catch (HelperException ex) { stt = ex.Status; said = ex.Message; }
                Check("ec write to a fan register is refused on this board (" + ecWrites + ")", stt == HelperProtocol.NotAllowed && said.Contains("switched off"), said);
                Check("ec read still works there", HelperClient.EcRead(0x34, out _) == 0, "");
            } else if (demo) {
                int w = HelperClient.EcWrite(0x34, 33);
                HelperClient.EcRead(0x34, out byte back);
                Check("ec write 0x34 (a fan level, simulated) and read back", w == 0 && back == 33, "wrote 33, reads " + back);
            } else Console.WriteLine("  skip  ec write to a fan register: not on real hardware from a test");

            bool intel = Driver.CpuVendor().IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 || demo;
            uint probe = intel ? 0x1A2u : 0xC0010299u;
            e = HelperClient.MsrRead(probe, out ulong mv);
            Check("msr read 0x" + probe.ToString("X"), e == 0, e == 0 ? "0x" + mv.ToString("X") : Native.Why(e));
            var p4 = new byte[4]; Native.Put32(p4, 0, 0x10);
            Check("msr read of a register Ohman does not read (0x10, the time stamp counter) is refused", StatusOf(() => HelperClient.Request(HelperProtocol.OpMsrRead, p4)) == HelperProtocol.NotAllowed, "");
            var p12 = new byte[12]; Native.Put32(p12, 0, 0x1A2);
            Check("msr write to anything but the power limit (0x1A2) is refused", StatusOf(() => HelperClient.Request(HelperProtocol.OpMsrWrite, p12)) == HelperProtocol.NotAllowed, "");
            if (intel && HelperClient.MsrRead(Msr.PackagePowerLimit, out ulong limit) == 0) {
                int w = HelperClient.MsrWrite(Msr.PackagePowerLimit, limit, out string why);
                // Writing back the value it holds changes nothing. A lockdown refusal is a correct answer too.
                Check("msr write 0x610 (the value it already holds)", w == 0 || why != null, w == 0 ? "accepted" : "refused cleanly: " + why);
                // The refusals below are decided before anything is written, so they are safe on a real laptop too.
                ulong unitReg;
                HelperClient.MsrRead(0x606, out unitReg);
                double unit = 1.0 / (1 << (int)(unitReg & 0xF));
                string said;
                Check("msr write 0x610 that sets the lock bit is refused", MsrWriteStatus(limit | (1ul << 63), out said) == HelperProtocol.NotAllowed, said);
                Check("msr write 0x610 that changes a time-window bit is refused", MsrWriteStatus(limit ^ (1ul << 17), out said) == HelperProtocol.NotAllowed, said);
                ulong hot = (limit & ~0x7FFFul) | (ulong)Math.Round(200 / unit);
                Check("msr write 0x610 with a 200 W limit is refused", (limit & (1ul << 63)) != 0 || MsrWriteStatus(hot, out said) == HelperProtocol.NotAllowed, said);
                ulong cold = (limit & ~(0x7FFFul << 32)) | ((ulong)Math.Round(5 / unit) << 32);
                Check("msr write 0x610 with a 5 W limit is refused", (limit & (1ul << 63)) != 0 || MsrWriteStatus(cold, out said) == HelperProtocol.NotAllowed, said);
                if (demo) {
                    ulong ok = (limit & ~Msr.LimitWritable) | (ulong)Math.Round(40 / unit) | (1ul << 15) | ((ulong)Math.Round(70 / unit) << 32) | (1ul << 47);
                    Check("msr write 0x610 with 40 W / 70 W is accepted (simulated)", MsrWriteStatus(ok, out said) == HelperProtocol.Ok && HelperClient.MsrRead(Msr.PackagePowerLimit, out ulong now) == 0 && now == ok, said);
                    Check("msr write 0x610 putting the first value back is accepted", MsrWriteStatus(limit, out said) == HelperProtocol.Ok, said);
                }
            } else Console.WriteLine("  skip  msr write 0x610: not an Intel CPU, or the register is unreadable");

            Check("energy counter", HelperClient.Energy(out ulong uj, out ulong max) || !demo, "energy " + uj + " uJ, wraps at " + max);
            Check("an operation that does not exist is refused", StatusOf(() => HelperClient.Request(0x7F, null)) == HelperProtocol.BadRequest, "");

            using var got = new ManualResetEvent(false);
            uint gotId = 0, gotData = 0;
            IDisposable sub = null;
            try {
                sub = HelperClient.Subscribe((id, data) => { gotId = id; gotData = data; got.Set(); });
                Check("subscribe to key events", true, "");
                if (demo) {
                    HelperClient.InjectKey(HpHotkeys.OmenEventId, HpHotkeys.OmenKeyData);
                    bool arrived = got.WaitOne(3000);
                    Check("an injected OMEN key arrives as event 29 / 8613", arrived && gotId == 29 && gotData == 8613, arrived ? gotId + "/" + gotData : "nothing arrived in 3 s");
                } else {
                    Check("injecting a key into a real helper is refused", StatusOf(() => HelperClient.InjectKey(29, 8613)) == HelperProtocol.NotAllowed, "");
                    Console.WriteLine("        press the OMEN key within 5 s to see it arrive...");
                    if (got.WaitOne(5000)) Console.WriteLine("        event " + gotId + "/" + gotData); else Console.WriteLine("        (no key pressed)");
                }
            } catch (Exception ex) { Check("subscribe to key events", false, ex.Message); }
            finally { sub?.Dispose(); }

            // ---- the server's limits. Raw sockets from here: these are things HelperClient never does.
            HelperClient.Close();
            Thread.Sleep(600);                              // the helper notices our closed connections on its own threads
            var held = new List<Socket>();
            int accepted = 0;
            string refusal = null;
            try {
                for (int i = 0; i < 20 && refusal == null; i++) {
                    Socket s = RawConnect();
                    held.Add(s);
                    try {
                        var ns = new NetworkStream(s, false);
                        HelperProtocol.WriteFrame(ns, HelperProtocol.OpStatus, null);
                        byte[] r = HelperProtocol.ReadFrame(ns, out byte code);
                        if (code == HelperProtocol.Ok) accepted++; else refusal = Encoding.UTF8.GetString(r);
                    } catch (Exception ex) { refusal = "closed on us: " + ex.Message; }
                }
            } catch (Exception ex) { refusal = refusal ?? "connect failed: " + ex.Message; }
            Check("sixteen connections are served and the seventeenth is refused", accepted == 16 && refusal != null, accepted + " served, then: " + refusal);
            foreach (Socket s in held) { try { s.Dispose(); } catch { } }
            Thread.Sleep(600);

            try {
                using Socket half = RawConnect();
                half.Send(new byte[] { 0x05, 0x00 });        // the first two bytes of a frame, and then nothing
                half.ReceiveTimeout = 14000;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int n;
                try { n = half.Receive(new byte[16]); } catch (SocketException ex) when (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock) { n = 0; }
                catch (SocketException) { n = -1; }
                double secs = clock.Elapsed.TotalSeconds;
                Check("a client that stops in the middle of a frame is dropped after about 10 s", n == 0 && secs > 8 && secs < 13, n == 0 ? "closed after " + secs.ToString("0.0") + " s" : "still open after " + secs.ToString("0.0") + " s");
                Check("the helper still answers afterwards", HelperClient.Status().ContainsKey("protocol"), "");
            } catch (Exception ex) { Check("a client that stops in the middle of a frame is dropped", false, ex.Message); }

            if (demo) {
                // A reply that does not come: the request must fail in about three seconds, the next one at once
                // (not after another three), and the helper must have been asked exactly once.
                int before = Count(HelperClient.Status(), "test_delays");
                var ms = new byte[4]; Native.Put32(ms, 0, 6000);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                bool threw = false;
                try { HelperClient.Request(HelperProtocol.OpTestDelay, ms); } catch (IOException) { threw = true; }
                double first = clock.Elapsed.TotalSeconds;
                Check("a request the helper does not answer fails after about 3 s", threw && first > 2.5 && first < 4.5, first.ToString("0.0") + " s");
                clock.Restart();
                bool fast = false;
                try { HelperClient.Status(); } catch (IOException) { fast = true; }
                Check("the next request fails at once instead of waiting again", fast && clock.Elapsed.TotalSeconds < 0.5, clock.Elapsed.TotalMilliseconds.ToString("0") + " ms");
                Check("and reads as unreachable, not as a value", !HelperClient.Reachable && HelperClient.EcRead(0x57, out _) != 0, HelperClient.LastError);
                Thread.Sleep(2300);
                int after = Count(HelperClient.Status(), "test_delays");
                Check("after two seconds it connects again, and the timed-out request was sent once only", after == before + 1, "helper counted " + (after - before));
            }

            Console.WriteLine(failures == 0 ? "PASS" : "FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>The pieces of the Linux layer that read other programs' formats, checked against known
        /// input: xrandr's output, a HID report descriptor, the hotkey translation, the ioctl numbers. None of
        /// it touches a device, so it runs anywhere.</summary>
        static int SelfTest() {
            failures = 0;
            const string xr = "Screen 0: minimum 320 x 200, current 4480 x 1440, maximum 16384 x 16384\n"
                + "HDMI-1 connected 2560x1440+1920+0 (normal left inverted right x axis y axis) 597mm x 336mm\n"
                + "   2560x1440     59.95*+  74.97  \n"
                + "eDP-1 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis) 344mm x 194mm\n"
                + "   1920x1080    144.00*+  60.01    59.97  \n"
                + "   1680x1050    144.00    60.00  \n"
                + "DP-1 disconnected (normal left inverted right x axis y axis)\n";
            Display.PanelState ps = Display.Parse(xr);
            Check("xrandr: the laptop panel is found behind an external monitor", ps != null && ps.Output == "eDP-1" && ps.Mode == "1920x1080", ps == null ? "nothing parsed" : ps.Output + " " + ps.Mode);
            Check("xrandr: current rate and the rates of the current mode", ps != null && Math.Round(ps.Current) == 144 && ps.Rates.Count == 3 && ps.Rates[1].Value == "60.01", ps == null ? "" : ps.Current + " Hz of " + ps.Rates.Count);
            Check("xrandr: a machine with no built-in panel has none", Display.Parse("HDMI-1 connected 1920x1080+0+0\n   1920x1080 60.00*+\n") == null, "");

            // Two top-level collections on one interface: a LampArray with numbered feature reports (1: 25 bytes,
            // 2: 2 bytes) and a vendor collection with a 64-byte input and output report under report id 3.
            byte[] desc = {
                0x05, 0x59, 0x09, 0x01, 0xA1, 0x01,             // Usage Page (Lighting), Usage (LampArray), Collection (Application)
                0x85, 0x01, 0x09, 0x02, 0xA1, 0x02,             //   Report ID 1, Usage, Collection (Logical)
                0x75, 0x08, 0x95, 0x19, 0xB1, 0x03, 0xC0,       //     Report Size 8, Count 25, Feature, End
                0x85, 0x02, 0x75, 0x10, 0x95, 0x01, 0xB1, 0x02, //   Report ID 2, Size 16, Count 1, Feature
                0xC0,
                0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01,       // Usage Page (Vendor FF00), Usage 1, Collection (Application)
                0x85, 0x03, 0x75, 0x08, 0x95, 0x40, 0x81, 0x02, //   Report ID 3, Size 8, Count 64, Input
                0x95, 0x40, 0x91, 0x02, 0xC0                    //   Count 64, Output, End
            };
            List<Hid.Info> cols = Hid.ParseDescriptor(desc, null);
            Check("hid descriptor: two top-level collections", cols.Count == 2, cols.Count + " found");
            if (cols.Count == 2) {
                Check("hid descriptor: the LampArray collection and its longest feature report (25 + the id)", cols[0].UsagePage == 0x59 && cols[0].Usage == 1 && cols[0].FeatureLen == 26 && cols[0].InputLen == 0, cols[0].ToString());
                Check("hid descriptor: the vendor collection's 64-byte reports (65 with the id)", cols[1].UsagePage == 0xFF00 && cols[1].InputLen == 65 && cols[1].OutputLen == 65 && cols[1].FeatureLen == 0, cols[1].ToString());
            }

            uint id, data;
            Check("hotkeys: KEY_PROG2 with the firmware's code is event 29 / 8613", HpHotkeys.Translate(149, 0x21A5, out id, out data) && id == 29 && data == 8613, id + "/" + data);
            Check("hotkeys: KEY_PROG2 with no scan code still is", HpHotkeys.Translate(149, 0, out id, out data) && id == 29 && data == 8613, id + "/" + data);
            Check("hotkeys: another 0x21xx key keeps its own code", HpHotkeys.Translate(0x1d1, 0x21A7, out id, out data) && data == 0x21A7, id + "/" + data);
            Check("hotkeys: a brightness key is not a firmware key event", !HpHotkeys.Translate(224, 0x02, out id, out data), "");

            // The request numbers, against the values the C compiler gives for the module's own macros:
            // _IOWR('O',1,4120 bytes) = 0xD0184F01, _IOWR('O',2,2) = 0xC0024F02, _IOW('O',3,2) = 0x40024F03, _IOR('O',4,4) = 0x80044F04.
            Check("ioctl numbers match the kernel module's", Native.IoWR('O', 1, 4120).ToUInt32() == 0xD0184F01 && Native.IoWR('O', 2, 2).ToUInt32() == 0xC0024F02
                && Native.IoW('O', 3, 2).ToUInt32() == 0x40024F03 && Native.IoR('O', 4, 4).ToUInt32() == 0x80044F04, "");
            Check("ioctl numbers: HIDIOCGFEATURE(26) is what linux/hidraw.h gives", Native.IoWR('H', 0x07, 26).ToUInt32() == 0xC01A4807, "");

            BenchChecks();

            // The stock driver's fan mode, against directories of ordinary files standing in for its hwmon node.
            string tmp = Path.Combine(Path.GetTempPath(), "ohman-selftest-" + Environment.ProcessId);
            try {
                string note;
                string older = Path.Combine(tmp, "older"), newer = Path.Combine(tmp, "newer");
                Directory.CreateDirectory(older); Directory.CreateDirectory(newer);
                // A kernel before the manual-fan rewrite: no pwm1, and pwm1_enable=0 is the live flag "max fan is on".
                File.WriteAllText(Path.Combine(older, "pwm1_enable"), "0\n");
                bool wrote = HpWmi.TakeOverFrom(older, out note);
                Check("hp-wmi without pwm1: pwm1_enable is never written, even reading 0", !wrote && File.ReadAllText(Path.Combine(older, "pwm1_enable")) == "0\n", note);
                File.WriteAllText(Path.Combine(newer, "pwm1"), "128\n");
                foreach (string mode in new[] { "0", "1" }) {
                    File.WriteAllText(Path.Combine(newer, "pwm1_enable"), mode + "\n");
                    wrote = HpWmi.TakeOverFrom(newer, out note);
                    Check("hp-wmi with pwm1, left in mode " + mode + ": set to automatic once", wrote && File.ReadAllText(Path.Combine(newer, "pwm1_enable")) == "2", note);
                }
                wrote = HpWmi.TakeOverFrom(newer, out note);
                Check("hp-wmi with pwm1, already automatic: left alone", !wrote && File.ReadAllText(Path.Combine(newer, "pwm1_enable")) == "2", note);
                Check("hp-wmi with no hwmon node: left alone", !HpWmi.TakeOverFrom(null, out note), note);

                Check("socket directory: one owned by an ordinary user is refused", Native.IsRoot || HelperProtocol.UnsafeSocketDirectory(tmp) != null, HelperProtocol.UnsafeSocketDirectory(tmp) ?? "accepted");
                Check("socket directory: root's own, not writable by others, is accepted", HelperProtocol.UnsafeSocketDirectory("/usr") == null, HelperProtocol.UnsafeSocketDirectory("/usr") ?? "");
                Check("socket directory: a sticky one (/tmp) is accepted", HelperProtocol.UnsafeSocketDirectory("/tmp") == null, HelperProtocol.UnsafeSocketDirectory("/tmp") ?? "");
            } finally { try { Directory.Delete(tmp, true); } catch { } }
            Check("groups: root is 0, and a group that does not exist is not found", HelperProtocol.GroupId("root") == 0 && HelperProtocol.GroupId("ohman-no-such-group") == -1, "");

            // The one MSR write, as the helper judges it. Unit 3 = eighths of a watt; 45 W and 80 W enabled.
            ulong u = 3, cur = (45ul * 8) | (1ul << 15) | ((80ul * 8) << 32) | (1ul << 47) | (0x6Eul << 17);
            Func<ulong, bool, ulong, string> judge = (w, have, orig) => Msr.LimitWriteRefusal(cur, w, u, have, orig);
            ulong fine = (cur & ~Msr.LimitWritable) | (35ul * 8) | (1ul << 15) | ((65ul * 8) << 32) | (1ul << 47);
            Check("msr 0x610: the limits Ohman sets are allowed", judge(fine, false, 0) == null, judge(fine, false, 0) ?? "");
            Check("msr 0x610: the lock bit is never set", judge(fine | (1ul << 63), false, 0) != null, judge(fine | (1ul << 63), false, 0) ?? "allowed");
            Check("msr 0x610: a time-window or clamp bit may not change", judge(fine ^ (1ul << 17), false, 0) != null && judge(fine ^ (1ul << 16), false, 0) != null, "");
            Check("msr 0x610: 126 W and 14 W are outside the range", judge((fine & ~0x7FFFul) | (127ul * 8), false, 0) != null && judge((fine & ~0x7FFFul) | (13ul * 8), false, 0) != null, "");
            Check("msr 0x610: a locked register is not written", Msr.LimitWriteRefusal(cur | (1ul << 63), fine | (1ul << 63), u, false, 0) != null, "");
            ulong firmware = (cur & ~Msr.LimitWritable) | (135ul * 8) | (1ul << 15) | ((157ul * 8) << 32) | (1ul << 47);      // what an HX part ships with
            Check("msr 0x610: the value found before the first write may always be put back", Msr.LimitWriteRefusal(fine, firmware, u, true, firmware) == null && Msr.LimitWriteRefusal(fine, firmware, u, true, cur) != null, "");

            string autostart = Autostart.File_;
            Check("autostart: the entry is under the XDG config directory", autostart.EndsWith("/autostart/ohman.desktop"), autostart);
            Console.WriteLine(failures == 0 ? "PASS" : "FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }
    }
}
