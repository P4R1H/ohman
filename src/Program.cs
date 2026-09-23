// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: entry point.
//   Ohman.exe                 normal start (elevated build asks for UAC once)
//   Ohman.exe --hidden        start minimised to the tray (used by the autostart task)
//   Ohman.exe --demo          force simulated hardware
//   Ohman.exe --support       write the support report (clipboard-free path for issue reports)
//   Ohman.exe --demo --board 8A25   simulate another board id (shows the generic profile the engine would build)
//   Ohman.exe --screenshot f.png [--settings]   render the window to a PNG and exit (UI preview)
//   Ohman.exe --lamps        list the HID lighting devices this machine has and exit (read-only; support data)
//   Ohman.exe --driver       what the PawnIO driver, the CPU registers and the EC say on this machine (read-only; tools\drivertest.cmd)
//   Ohman.exe --driver --fantest   the same, and then briefly raise the fans to find which EC register drives them
//   Ohman.exe --updated      started by the build it replaced: waits for that one to let go, then cleans it up
using System;
using System.Security.Principal;
using System.Threading;
using System.Windows;

// WPF only leaves its "do not scale for DPI changes" quirk behind when the assembly says it targets 4.6.2 or later.
// Without this the PerMonitorV2 declaration in app.manifest would stop Windows scaling the window without WPF
// taking over, and the window would be the wrong physical size on a display with a different scale factor.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

// The title is what the UAC prompt and Task Manager show, so it is the app name rather than the file name.
// The shared publisher and version strings are in src\Meta.cs.
[assembly: System.Reflection.AssemblyTitle("Ohman")]
[assembly: System.Reflection.AssemblyDescription("Fan, thermal and keyboard lighting control for HP OMEN and Victus laptops.")]

namespace Ohman {
    public static class Program {
        // To rename the app: change AppName here and the /out: names in build.cmd. Everything else follows
        // (window title, tray, scheduled task, single-instance names, log/state file names).
        public const string AppName = "Ohman";                 // internal id: file names, mutex, scheduled task
        public static string DisplayName = AppName;           // what the UI shows; override with Name=... in ohman.state
        public const string Version = Meta.Version;           // bump it in src\Meta.cs, not here
        public static string FileStem { get { return AppName.ToLowerInvariant(); } }
        public static EventWaitHandle ShowEvent, ExitEvent;   // named events: another instance can ask us to show or exit
        public static bool JustUpdated;                       // --updated: this build was started by the one it replaced
        public static bool FlashTest;                         // --flash: show the key OSD at start (preview/screenshot aid)
        public static bool KeyboardTest;                      // --keyboard: open the keyboard page at start (screenshot aid)
        public static string StartPage = "";                  // --page home|fans|keyboard|settings|update|driver
        public static bool FanProbe;                          // --fantest with --driver: also find which EC register drives the fans

        [STAThread]
        public static int Main(string[] args) {
            bool demo = false, hidden = false, settings = false;
            string shot = null;
            var overrides = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++) {
                string a = args[i].ToLowerInvariant();
                if (a == "--demo") demo = true;
                else if (a == "--hidden") hidden = true;
                else if (a == "--settings") settings = true;
                else if (a == "--screenshot" && i + 1 < args.Length) shot = args[++i];
                else if (a == "--set" && i + 1 < args.Length) overrides.Add(args[++i]);   // --set Key=Value: override for this run only (nothing is written back)
                else if (a == "--flash") FlashTest = true;
                else if (a == "--keyboard") KeyboardTest = true;
                else if (a == "--page" && i + 1 < args.Length) StartPage = args[++i].ToLowerInvariant();
                else if (a == "--board" && i + 1 < args.Length) Platforms.BoardOverride = args[++i];   // pretend to be another board (with --demo: see what generic mode would build)
            }
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--test") return RunTests();
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--lamps") return ListLamps();
            // Before the single-instance guard, like --lamps: with Ohman already running, anything after it
            // just signals the live window and exits, which made this silently do nothing.
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--support") return WriteSupport(args);
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--fantest") FanProbe = true;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--driver") return WriteDriverReport(args);
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "--make-ico") { MainWindow.WriteIco(args[i + 1], Ui.BalColor); return 0; }   // build aid: writes the app icon
            bool wantExit = false;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--exit") wantExit = true;   // Ohman.exe --exit: stop the running instance (used when updating)
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--updated") JustUpdated = true;   // started by the build we replaced
            bool created;
            if (shot != null) demo = true;                                         // screenshots never touch firmware and may run beside a live instance
            // a simulated instance is its own app: it can sit next to the real one, and --exit aimed at one never stops the other
            string instance = AppName + (demo ? "_Demo" : "");
            var mutex = new Mutex(true, instance + "_SingleInstance", out created);
            if (!created && shot == null) {
                // We are the build that has just replaced the one still shutting down, and it holds the mutex
                // until its process actually ends. Wait for it instead of reading it as the owner starting Ohman
                // twice, which would show the old window and quit - the update would look like the app closing.
                // A mutex whose owner exits without releasing it is "abandoned", which throws here and means we
                // got it.
                if (JustUpdated) {
                    try { created = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { created = true; }
                    catch { }
                }
                if (!created) {
                    try { EventWaitHandle.OpenExisting(instance + (wantExit ? "_Exit" : "_ShowPanel")).Set(); } catch { }
                    return 0;
                }
            }
            if (wantExit) return 0;
            try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "_ShowPanel"); } catch { }
            try { ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "_Exit"); } catch { }

            bool elevated = false;
            try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            IHardware hw = (demo || !elevated) ? (IHardware)new DemoHardware() : new Bios();
            Log.Write("---- " + AppName + " " + Version + " start · elevated=" + elevated + " · hardware=" + (hw.IsDemo ? "demo" : "bios") + (hidden ? " · hidden" : ""));
            // The build we replaced is still on disk under its own name until its process lets go of it, which is
            // why this waits on a thread of its own rather than holding up the window.
            if (JustUpdated) new Thread(Update.CleanOld) { IsBackground = true, Name = "update-cleanup" }.Start();

            var settingsObj = Settings.Load();
            foreach (string o in overrides) { int eq = o.IndexOf('='); if (eq > 0) settingsObj.Apply(o.Substring(0, eq), o.Substring(eq + 1)); }
            if (overrides.Count > 0 || shot != null) settingsObj.NoPersist = true;
            if (settingsObj.StartHidden) hidden = true;
            if (!string.IsNullOrEmpty(settingsObj.Name)) DisplayName = settingsObj.Name.Trim();
            var engine = new Engine(hw, settingsObj);
            engine.Init();
            var sensors = new Sensors();
            sensors.CpuSource = delegate { return engine.Cpu; };
            sensors.Start();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e) { Log.Write("UNHANDLED: " + e.ExceptionObject); };
            app.DispatcherUnhandledException += delegate(object o, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { Log.Write("UI EXCEPTION: " + e.Exception); e.Handled = true; };

            MainWindow win;
            try { win = new MainWindow(engine, sensors, shot, settings); }
            catch (Exception ex) {
                Log.Write("window init failed: " + ex);
                MessageBox.Show(AppName + " could not build its window:\n\n" + ex.Message + "\n\nSee " + FileStem + ".log.", AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            if (!hidden || shot != null) win.Show();
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        /// <summary>--lamps. Everything the keyboard's own HID lighting interface will tell us, and nothing written.
        /// On a four-zone laptop this finds HP's virtual device with its four lamps; on a per-key one it should find
        /// the keyboard itself with a lamp per key, which is what Ohman would drive.</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AllocConsole();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);

        /// <summary>Give the console switches somewhere to print, and say whether the console is ours to hold open.
        ///
        /// A windows-subsystem binary owns no console. AttachConsole(-1) borrows the caller's, which only works from
        /// a prompt that is already elevated: app.manifest asks for administrator, so an ordinary prompt spawns a
        /// fresh process whose parent is not that console, and the output went nowhere with nothing to say why.
        ///
        /// The guard is the stdout handle, not Console.IsOutputRedirected, which reports "redirected" when there is
        /// no console at all - the exact case this serves. NULL or INVALID means nobody is listening; anything else
        /// is a real destination, support-info.cmd's file included, and must be left alone.</summary>
        static bool OpenConsole() {
            try {
                if (AttachConsole(-1)) { PointStdOutAtConsole(); return false; }
                IntPtr h = GetStdHandle(-11);                                  // STD_OUTPUT_HANDLE
                bool nobodyListening = h == IntPtr.Zero || h == new IntPtr(-1);
                if (nobodyListening && AllocConsole()) { PointStdOutAtConsole(); return true; }
                PointStdOutAtConsole();
            } catch { }
            return false;
        }

        static void PointStdOutAtConsole() {
            var w = new System.IO.StreamWriter(Console.OpenStandardOutput());
            w.AutoFlush = true;
            Console.SetOut(w);
        }

        /// <summary>A console we opened dies with the process and takes the output with it, so wait for a person.
        /// The reader is built here rather than through Console.In because the cached one was made when this
        /// process had no console at all.</summary>
        static void HoldConsole(bool ours) {
            if (!ours) return;
            try {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput()));
                Console.ReadLine();
            } catch { }
        }

        /// <summary>--support: the report the Settings button produces, for anyone who would rather not open the
        /// window. --driver: the same thing for the driver alone. Both build their own engine, because they run
        /// before the single-instance guard and have to work beside a live Ohman.</summary>
        static int WriteSupport(string[] args) { return WriteReport(args, "support-info.txt", Support.Report, "the firmware was not asked anything"); }
        static int WriteDriverReport(string[] args) { return WriteReport(args, "driver-check.txt", Support.DriverReport, "the driver and the firmware were not asked anything"); }

        static int WriteReport(string[] args, string fileName, Func<Engine, string> build, string notElevated) {
            bool ownConsole = OpenConsole();
            bool elev = false;
            try { elev = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            bool wantDemo = false;
            foreach (string a in args) if (a.ToLowerInvariant() == "--demo") wantDemo = true;
            IHardware hw2 = (wantDemo || !elev) ? (IHardware)new DemoHardware() : new Bios();
            if (!elev) Console.WriteLine("NOT ELEVATED - " + notElevated + ". Run this from an administrator prompt.\n");
            var s2 = Settings.Load();
            s2.NoPersist = true;
            var eng = new Engine(hw2, s2);
            try { eng.Init(false); } catch (Exception ex) { Console.WriteLine("engine init failed: " + ex.Message); }   // false: describe the laptop, do not touch it
            string rep;
            try { rep = build(eng); } catch (Exception ex) { rep = "report failed: " + ex; }
            Console.WriteLine(rep);
            // The fan test's one durable result. Its own file, because this process holds the settings with
            // NoPersist and a running Ohman would write over anything put there anyway.
            if (eng.EcPairFound != 0) {
                try { System.IO.File.WriteAllText(Engine.EcPairPath, eng.EcPairFound == 2 ? "percent" : "rpm"); Console.WriteLine("recorded which fan register pair this board uses; Ohman reads it at its next start"); }
                catch (Exception ex) { Console.WriteLine("could not record the fan register pair: " + ex.Message); }
            }
            try {
                string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Path), fileName);
                System.IO.File.WriteAllText(p, rep);
                Console.WriteLine("saved to " + p);
            } catch (Exception ex) { Console.WriteLine("could not save: " + ex.Message); }
            try { eng.Dispose(); } catch { }
            HoldConsole(ownConsole);
            return 0;
        }

        static int ListLamps() {
            bool ownConsole = OpenConsole();
            try { return ListLampsBody(); } finally { HoldConsole(ownConsole); }
        }

        static int ListLampsBody() {
            // Also goes to the log: this is launched from support-info.cmd and from shortcuts as often as from a
            // prompt, and the log is what people end up attaching to an issue anyway.
            Action<string> say = delegate(string s) { Console.WriteLine(s); Log.Write("lamps| " + s); };
            var all = Hid.Enumerate();
            int lighting = 0;
            say(AppName + " " + Version + ": HID lighting devices");
            say(all.Count + " HID collections present");
            foreach (var info in all) {
                if (info.UsagePage != LampArray.UsagePageLighting) continue;
                lighting++;
                say("");
                say("  " + info);
                say("  " + info.Path);
            }
            if (lighting == 0) { say(""); say("No HID Lighting And Illumination collection (usage page 0x59) on this machine."); return 0; }
            foreach (var la in LampArray.All()) {
                say("");
                say("LampArray VID_" + la.VendorId.ToString("X4") + " PID_" + la.ProductId.ToString("X4") + "  " + la.Product);
                say("  " + la.Describe);
                say("  usable for per-key painting: " + (la.UsableAsPerKey ? "yes" : "no"));
                say("  lamp   x(mm)   y(mm)  prog  key usage");
                for (int i = 0; i < la.LampCount; i++)
                    say("  " + i.ToString().PadLeft(4) + "  " + (la.X[i] / 1000.0).ToString("0.0").PadLeft(6) +
                        "  " + (la.Y[i] / 1000.0).ToString("0.0").PadLeft(6) +
                        "  " + (la.Programmable(i) ? " yes" : "  no") + "   0x" + la.KeyUsage[i].ToString("X2"));
                la.Dispose();
            }
            return 0;
        }

        static void Assert(bool cond, string msg) {
            if (!cond) throw new InvalidOperationException("ASSERT FAILED: " + msg);
        }

        static int RunTests() {
            bool ownConsole = OpenConsole();
            try {
                Console.WriteLine("Running probe-only and keyboard ownership tests...");

                // 1. IsDeviceInternal location evidence
                Console.Write("  IsDeviceInternal rules... ");
                Assert(LampArray.IsDeviceInternal(@"\\?\hid#hid_device_system_vhf#1", 0x1234, null, null), "VHF path must be internal");
                Assert(LampArray.IsDeviceInternal(@"\\?\ACPI#HPQ6001#1", 0x1234, null, null), "ACPI path must be internal");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_046d&pid_c332", 0x046D, true, false), "Unknown vendor (Logitech) must not be internal");
                // Darfon (0x0D62)
                Assert(LampArray.IsDeviceInternal(@"\\?\hid#vid_0d62&pid_1234", 0x0D62, true, false), "Darfon with inLocalContainer=true must be internal");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_0d62&pid_1234", 0x0D62, false, false), "Darfon with inLocalContainer=false must be rejected as external");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_0d62&pid_1234", 0x0D62, true, true), "Darfon with removable=true must be rejected as external");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_0d62&pid_1234", 0x0D62, false, true), "Darfon with removable=true must be rejected as external");
                // Primax (0x0461)
                Assert(LampArray.IsDeviceInternal(@"\\?\hid#vid_0461&pid_0010", 0x0461, true, false), "Primax with inLocalContainer=true must be internal");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_0461&pid_0010", 0x0461, false, false), "Primax with inLocalContainer=false must be rejected as external");
                // Chicony (0x04F2)
                Assert(LampArray.IsDeviceInternal(@"\\?\hid#vid_04f2&pid_0020", 0x04F2, true, false), "Chicony with inLocalContainer=true must be internal");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_04f2&pid_0020", 0x04F2, false, false), "Chicony with inLocalContainer=false must be rejected as external");
                // ITE (0x048D)
                Assert(LampArray.IsDeviceInternal(@"\\?\hid#vid_048d&pid_0030", 0x048D, true, false), "ITE with inLocalContainer=true must be internal");
                Assert(!LampArray.IsDeviceInternal(@"\\?\hid#vid_048d&pid_0030", 0x048D, false, false), "ITE with inLocalContainer=false must be rejected as external");
                Console.WriteLine("PASS");

                // 2. Init(false) probe-only never acquires or paints
                Console.Write("  Init(false) probe-only isolation... ");
                var fake = new FakeLampArray(100);
                LampArray.KeyboardFinder = delegate { return fake; };
                BiosLighting.Detector = delegate { return new BiosLighting(3, LightKind.PerKey, 4); };
                try {
                    var s = Settings.Load();
                    s.NoPersist = true;
                    s.SuppressOgh = false;
                    s.RefreshHz = 0;
                    s.DriverUse = false;
                    var eng = new Engine(new TestHardware(), s);
                    eng.Init(false);
                    Assert(fake.TakeOverCount == 0, "Init(false) must NEVER call TakeOver");
                    Assert(fake.SetAllCount == 0, "Init(false) must NEVER call SetAll");
                    Assert(fake.SetLampsCount == 0, "Init(false) must NEVER call SetLamps");
                    Assert(!s.TookWinLighting, "Init(false) must not take Windows Dynamic Lighting");
                    Assert(eng.Light != null, "Init(false) should identify lighting device");
                    Assert(eng.Light.Kind == LightKind.PerKey, "Device should be per-key");
                    eng.Dispose();
                    Assert(fake.DisposeCount >= 1, "Engine.Dispose must dispose device");
                    Assert(fake.TakeOverFalseCount == 0, "Unacquired device should not call TakeOver(false)");
                } finally {
                    LampArray.KeyboardFinder = null;
                    BiosLighting.Detector = null;
                }
                Console.WriteLine("PASS");

                // 3. Normal application mode deferred ownership
                Console.Write("  Deferred ownership acquisition... ");
                var fake2 = new FakeLampArray(100);
                var pk = new PerKeyLighting(fake2);
                Assert(fake2.TakeOverCount == 0, "Constructing PerKeyLighting must NOT call TakeOver");
                Assert(!pk.Held, "Device must not be held initially");
                // First write acquires immediately before write
                pk.SetColors(new Rgb[] { new Rgb(255, 0, 0) });
                Assert(fake2.TakeOverCount == 1, "SetColors must acquire device");
                Assert(fake2.TakeOverTrueCount == 1, "SetColors must call TakeOver(true)");
                Assert(fake2.LastTakeOverValue == true, "Last takeover must be true");
                Assert(fake2.SetLampsCount == 1, "SetColors must paint lamps");
                Assert(pk.Held, "Device must be held after write");
                Console.WriteLine("PASS");

                // 4. Disposal releases acquired device
                Console.Write("  Disposal releases acquired device... ");
                pk.Dispose();
                Assert(fake2.TakeOverFalseCount == 1, "Dispose must release device (TakeOver(false))");
                Assert(!pk.Held, "Device must not be held after dispose");
                Assert(fake2.DisposeCount == 1, "Underlying LampArray must be disposed");
                Console.WriteLine("PASS");

                // 5. Write failure releases device
                Console.Write("  Write failure releases device... ");
                var fake3 = new FakeLampArray(100);
                var pk3 = new PerKeyLighting(fake3);
                pk3.SetColors(new Rgb[] { new Rgb(0, 255, 0) });
                Assert(pk3.Held, "Device should be held after successful write");
                fake3.ThrowOnWrite = true;
                bool threw = false;
                try { pk3.SetColors(new Rgb[] { new Rgb(0, 0, 255) }); }
                catch (System.IO.IOException) { threw = true; }
                Assert(threw, "Write failure must bubble exception");
                Assert(fake3.TakeOverFalseCount == 1, "Failure path must release device immediately");
                Assert(!pk3.Held, "Device must not be held after write failure");
                pk3.Dispose();
                Console.WriteLine("PASS");

                // 6. Engine disposal and TryLight failure release
                Console.Write("  Engine disposal and TryLight failure release... ");
                var fake4 = new FakeLampArray(100);
                LampArray.KeyboardFinder = delegate { return fake4; };
                BiosLighting.Detector = delegate { return new BiosLighting(3, LightKind.PerKey, 4); };
                try {
                    var s = Settings.Load();
                    s.NoPersist = true;
                    s.SuppressOgh = false;
                    s.RefreshHz = 0;
                    s.DriverUse = false;
                    s.Light = 1;
                    var eng = new Engine(new TestHardware(), s);
                    eng.Init(true);
                    var pkEngine = eng.Light as PerKeyLighting;
                    Assert(pkEngine != null, "Engine.Light must be PerKeyLighting");
                    Assert(fake4.TakeOverTrueCount >= 1, "Normal mode should acquire on write");
                    eng.Dispose();
                    Assert(fake4.TakeOverFalseCount >= 1, "Engine.Dispose must release per-key device");
                    Assert(fake4.DisposeCount >= 1, "Engine.Dispose must dispose device");
                } finally {
                    LampArray.KeyboardFinder = null;
                    BiosLighting.Detector = null;
                }
                Console.WriteLine("PASS");

                Console.WriteLine("\nAll tests passed successfully!");
                return 0;
            } catch (Exception ex) {
                Console.WriteLine("\nFAIL: " + ex.Message);
                return 1;
            }
        }
    }

    internal sealed class FakeLampArray : ILampArray {
        public int LampCount { get; set; }
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public uint Kind { get; set; }
        public int WidthMicrometres { get; set; }
        public int HeightMicrometres { get; set; }
        public uint MinUpdateMicroseconds { get; set; }
        public string Path { get; set; }
        public string Product { get; set; }
        public ushort[] KeyUsage { get; set; }
        public int[] X { get; set; }
        public int[] Y { get; set; }
        public bool UsableAsPerKey { get; set; }
        public bool Internal { get; set; }
        public bool AnyProgrammable { get; set; }

        public int TakeOverCount;
        public int TakeOverTrueCount;
        public int TakeOverFalseCount;
        public bool? LastTakeOverValue;
        public int SetAllCount;
        public int SetLampsCount;
        public int DisposeCount;
        public bool ThrowOnWrite;

        public FakeLampArray(int lamps) {
            LampCount = lamps;
            VendorId = 0x0D62;
            ProductId = 0x1234;
            Kind = LampArray.KindKeyboard;
            WidthMicrometres = 300000;
            HeightMicrometres = 120000;
            MinUpdateMicroseconds = 16000;
            Path = @"\\?\hid#vid_0d62&pid_1234#1";
            Product = "Fake Internal Keyboard";
            KeyUsage = new ushort[lamps];
            X = new int[lamps];
            Y = new int[lamps];
            UsableAsPerKey = true;
            Internal = true;
            AnyProgrammable = true;
        }

        public string Describe { get { return LampCount + " lamps, fake"; } }
        public bool Programmable(int lamp) { return true; }

        public void TakeOver(bool ours) {
            TakeOverCount++;
            if (ours) TakeOverTrueCount++; else TakeOverFalseCount++;
            LastTakeOverValue = ours;
        }

        public void SetAll(Rgb c, int intensity) {
            if (ThrowOnWrite) throw new System.IO.IOException("Simulated SetAll failure");
            SetAllCount++;
        }

        public void SetLamps(int[] ids, Rgb[] colors, int intensity) {
            if (ThrowOnWrite) throw new System.IO.IOException("Simulated SetLamps failure");
            SetLampsCount++;
        }

        public void Dispose() {
            DisposeCount++;
        }
    }

    internal sealed class TestHardware : IHardware {
        public bool IsDemo { get { return false; } }
        public int GetFanCount() { return 2; }
        public int GetFanCountPassive() { return 2; }
        public int GetFanTableMax() { return 46; }
        public int[] GetFanLevels() { return new int[] { 25, 25 }; }
        public int GetTemperature() { return 45; }
        public bool GetMaxFan() { return false; }
        public GpuPowerState GetGpuPower() { return new GpuPowerState { CustomTgp = true, Ppab = false, DState = 1, PeakTemp = 70 }; }
        public SystemInfo GetSystemInfo() {
            return new SystemInfo { Valid = true, ThermalPolicy = 1, SwFanControl = true, DefaultPl4 = 159, DefaultConcurrentTdp = 30, GpuModes = 3,
                Raw = new byte[] { 0x8C, 0, 0x35, 1, 1, 0x9F, 0, 3, 0x1E } };
        }
        public void SetMode(byte m, bool byBios) { }
        public void SetMaxFan(bool on) { }
        public void SetFanLevels(int a, int b) { }
        public void SetConcurrentTdp(int w) { }
        public void SetGpuPower(bool c, bool p, int t) { }
        public int GetGpuMode() { return 0; }
        public void SetGpuMode(int m) { }
    }
}

