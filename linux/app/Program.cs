// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: entry point. The counterpart of src\Program.cs, with the same switches where they mean something:
//   ohman                     normal start
//   ohman --hidden            start in the tray (used by the autostart entry)
//   ohman --demo              force simulated hardware
//   ohman --support           write the support report
//   ohman --demo --board 8A25   simulate another board id (shows the generic profile the engine would build)
//   ohman --screenshot f.png [--settings] [--page fans] [--set Key=Value]   render the window to a PNG and exit;
//                             needs no display. Also writes f.png.icon.png, f.png.tray.png and, with --flash, f.png.osd.png
//   ohman --lamps             list the HID lighting devices this machine has and exit (read-only; support data)
//   ohman --driver            what the driver, the CPU registers and the EC say on this machine (read-only)
//   ohman --driver --fantest  the same, and then briefly raise the fans to find which EC register drives them
//   ohman --exit              stop the running instance
//
// Differences from the Windows original
//  - No elevation. The app runs as the user and the core reaches the firmware through the kernel module or the root
//    helper; Mailbox.Reachable says whether there is a way. Without one the hardware is simulated, exactly as the
//    Windows app does when it is not elevated (DEMO badge).
//  - Single instance. A lock file and a Unix socket under $XDG_RUNTIME_DIR/ohman/ stand in for the named mutex and
//    the two named events: a second start writes "show" or "exit" to the socket and leaves. ShowEvent and ExitEvent
//    are still what the window waits on.
//  - A terminal is a terminal here, so the console juggling (AttachConsole and friends) is gone.
//  - --screenshot renders off-screen through Avalonia's headless platform, at --scale device pixels per DIP
//    (default 2, the scale the Windows reference pictures were taken at). --all-rows keeps the Settings rows this
//    system has no use for, so the page can be laid beside the Windows one.
//  - --headless runs the whole app with no window system (no window, no tray, no hotkeys): the engine, the single
//    instance socket and the exit path, for a machine or a test with no display.
//  - --updated and --make-ico are accepted and do nothing: Ohman does not replace its own files here
//    (Host.CanSelfUpdate) and the icon is a PNG in the package.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;

namespace Ohman {
    /// <summary>The Avalonia application: the Simple theme (the window brings its own look; this is for what it does
    /// not restyle, tool tips and popups), dark, and the one window.</summary>
    sealed class App : Application {
        public override void Initialize() {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new SimpleTheme());
        }
        public override void OnFrameworkInitializationCompleted() {
            Program.Started(ApplicationLifetime as IClassicDesktopStyleApplicationLifetime);
            base.OnFrameworkInitializationCompleted();
        }
    }

    public static partial class Program {
        // The app's name and version are shared with every platform and live in src\App.cs.
        public static EventWaitHandle ShowEvent, ExitEvent;   // another instance can ask us to show or exit (set by the socket listener)
        public static bool JustUpdated;                       // --updated: this build was started by the one it replaced
        public static bool FlashTest;                         // --flash: show the key OSD at start (preview/screenshot aid)
        public static bool KeyboardTest;                      // --keyboard: open the keyboard page at start (screenshot aid)
        public static string StartPage = "";                  // --page home|fans|keyboard|settings|update|driver
        public static string BenchPose;                       // BENCH: --bench-state first|ready|waiting|warming|measuring|paused|result|runs
        public static bool BenchSelfTest;                     // BENCH: --bench-selftest
        public static bool AllRows;                           // --all-rows: keep the Settings rows this system has no use for (screenshot aid)
        public static double ShotScale = 2;                   // --scale: device pixels per DIP of a screenshot
        /// <summary>The session is ending (logout, shutdown, SIGTERM): Application.SessionEnding in the WPF app.</summary>
        public static event Action SessionEnding;

        static Engine engine;
        static Sensors sensors;
        static PowerProfileWatch profileWatch;      // started once the window is up; the system's own power profile, followed
        static bool hwDemo;                          // the engine runs on simulated hardware: nothing outside may be followed or pushed
        static string shot;
        static bool hidden, settings, headless;
        static int exitCode;
        static IClassicDesktopStyleApplicationLifetime lifetime;
        static FileStream instanceLock;
        static List<PosixSignalRegistration> signals = new List<PosixSignalRegistration>();

        [STAThread]
        public static int Main(string[] args) {
            bool demo = false;
            var overrides = new List<string>();
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
                else if (a == "--bench-state" && i + 1 < args.Length) BenchPose = args[++i].ToLowerInvariant();
                else if (a == "--bench-selftest") BenchSelfTest = true;
                else if (a == "--all-rows") AllRows = true;
                else if (a == "--headless") headless = true;
                else if (a == "--scale" && i + 1 < args.Length) { double k; if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out k) && k >= 1 && k <= 4) ShotScale = k; }
                else if (a == "--board" && i + 1 < args.Length) Platforms.BoardOverride = args[++i];   // pretend to be another board (with --demo: see what generic mode would build)
            }
            // Before anything logs or loads: a simulated run keeps its log, settings and data in a folder of its own,
            // so a preview never reads or overwrites the real laptop's.
            AppPaths.Demo = demo || shot != null;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--lamps") return ListLamps();
            // Before the single-instance guard, like --lamps: with Ohman already running, anything after it
            // just signals the live window and exits, which made this silently do nothing.
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--support") return WriteSupport(args);
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--fantest") FanProbe = true;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--driver") return WriteDriverReport(args);
            bool wantExit = false;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--exit") wantExit = true;   // ohman --exit: stop the running instance
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--updated") JustUpdated = true;   // started by the build we replaced
            if (shot != null) demo = true;                                         // screenshots never touch firmware and may run beside a live instance
            // a simulated instance is its own app: it can sit next to the real one, and --exit aimed at one never stops the other
            string instance = FileStem + (demo ? "_demo" : "");
            if (shot == null) {
                bool created = TakeInstance(instance);
                if (!created) {
                    // The build that has just replaced the one still shutting down waits for it instead of reading it
                    // as the owner starting Ohman twice.
                    if (JustUpdated) for (int i = 0; i < 60 && !created; i++) { Thread.Sleep(250); created = TakeInstance(instance); }
                    if (!created) {
                        if (!Tell(instance, wantExit ? "exit" : "show")) Console.Error.WriteLine(AppName + " is already running, but did not answer.");
                        return 0;
                    }
                }
                if (wantExit) return 0;
                try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset); } catch { }
                try { ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset); } catch { }
                Listen(instance);
            }

            bool route = !demo && FirmwareReachable();
            IHardware hw = (demo || !route) ? (IHardware)new DemoHardware() : new Bios();
            hwDemo = hw.IsDemo;
            Log.Write("---- " + AppName + " " + Version + " start · route=" + route + " · hardware=" + (hw.IsDemo ? "demo" : "bios") + (hidden ? " · hidden" : ""));
            if (!demo && !route) {
                Log.Write("no route to the firmware: " + NoRouteWhy());
                Console.Error.WriteLine(AppName + ": " + NoRouteWhy() + " Running on simulated hardware.");
            }

            var settingsObj = Settings.Load();
            foreach (string o in overrides) { int eq = o.IndexOf('='); if (eq > 0) settingsObj.Apply(o.Substring(0, eq), o.Substring(eq + 1)); }
            if (overrides.Count > 0 || shot != null) settingsObj.NoPersist = true;
            if (settingsObj.StartHidden) hidden = true;
            if (!string.IsNullOrEmpty(settingsObj.Name)) DisplayName = settingsObj.Name.Trim();
            engine = new Engine(hw, settingsObj);
            engine.Init();
            sensors = new Sensors();
            sensors.CpuSource = delegate { return engine.Cpu; };
            sensors.DeadZoneHint = settingsObj.DeadZone;
            sensors.Start();

            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e) { Log.Write("UNHANDLED: " + e.ExceptionObject); };

            if (shot != null) headless = true;
            AppBuilder app = AppBuilder.Configure<App>();
            // Off-screen: Skia draws for real, into memory, and no display is needed or touched.
            if (headless) app = app.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            else {
                app = app.UsePlatformDetect();
                // Drawn by the processor unless asked otherwise. A GL context pulls the whole 3D driver into the
                // process (Mesa and LLVM, or the vendor's), 100 to 250 MB that stays for as long as Ohman sits in the
                // tray, to draw one small window that changes once a second. OHMAN_GPU=1 puts it back.
                if (Environment.GetEnvironmentVariable("OHMAN_GPU") != "1")
                    app = app.With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } });
            }
            try { app.StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) {
                Log.Write("the window system failed: " + ex);
                Console.Error.WriteLine(AppName + " could not start its window: " + ex.Message);
                // No display, or no window system: the fans must not be left where this process put them.
                try { engine.Park(true); } catch { }
                try { sensors.Dispose(); } catch { }
                try { engine.Dispose(); } catch { }
                return 1;
            }
            GC.KeepAlive(instanceLock);
            return exitCode;
        }

        /// <summary>The framework is up: build the window (Main's second half in the WPF app).</summary>
        internal static void Started(IClassicDesktopStyleApplicationLifetime desktop) {
            lifetime = desktop;
            Dispatcher.UIThread.UnhandledException += delegate(object o, DispatcherUnhandledExceptionEventArgs e) { Log.Write("UI EXCEPTION: " + e.Exception); e.Handled = true; };
            MainWindow win;
            try { win = new MainWindow(engine, sensors, shot, settings); }
            catch (Exception ex) {
                Log.Write("window init failed: " + ex);
                Console.Error.WriteLine(AppName + " could not build its window:\n\n" + ex + "\n\nSee " + Log.Path + ".");
                try { engine.Park(true); } catch { }
                exitCode = 1;
                Dispatcher.UIThread.Post(Shutdown);
                return;
            }
            if (desktop != null) desktop.ShutdownRequested += delegate { RaiseSessionEnding(); };
            // systemd stopping the session, or a plain kill: leave the way a logout does, fans handed back.
            foreach (PosixSignal sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }) {
                try { signals.Add(PosixSignalRegistration.Create(sig, delegate(PosixSignalContext c) { c.Cancel = true; Dispatcher.UIThread.Post(RaiseSessionEnding); })); } catch { }
            }
            if (!hidden || shot != null) win.Show();
            // The system's own power profile, followed as anything else moves it: the desktop's quick settings,
            // tuned-adm in a terminal. Not in a screenshot or demo run (both drive simulated hardware), and
            // not headless, where the app exists for tests and machines without a session bus at hand.
            if (shot == null && !headless && !hwDemo) profileWatch = PowerProfileWatch.Start(engine);
        }

        static void RaiseSessionEnding() { var h = SessionEnding; if (h != null) h(); }

        /// <summary>Application.Current.Shutdown().</summary>
        public static void Shutdown() {
            try { if (profileWatch != null) profileWatch.Dispose(); } catch { }
            try { if (lifetime != null) lifetime.Shutdown(exitCode); else Environment.Exit(exitCode); }
            catch { Environment.Exit(exitCode); }
        }

        /// <summary>Off-screen there is no display to drive the render timer, so anything animated by the framework
        /// (a switch's knob on its way across) would be caught wherever it stood. Two ticks a moment apart let it arrive.</summary>
        public static void Settle() {
            if (!headless) return;
            try {
                for (int i = 0; i < 3; i++) {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(120);
                }
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            } catch (Exception ex) { Log.Write("settle: " + ex.Message); }
        }

        /// <summary>The headless platform lays windows out at one device pixel per DIP and offers no way to say
        /// otherwise, while the Windows pictures were taken at two: positions there fall on half DIPs. The one
        /// field that holds the scale is set by hand, before the window has laid anything out. Off-screen only.</summary>
        public static void HeadlessScale(TopLevel w) {
            if (!headless || ShotScale == 1) return;
            try {
                object impl = w.PlatformImpl;
                bool done = false;
                for (Type t = impl.GetType(); t != null && !done; t = t.BaseType)
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                        if (f.FieldType == typeof(double) && f.Name.IndexOf("RenderScaling", StringComparison.Ordinal) >= 0) { f.SetValue(impl, ShotScale); done = true; }
                if (done) {
                    var pi = impl.GetType().GetProperty("ScalingChanged");
                    var changed = pi == null ? null : pi.GetValue(impl) as Action<double>;
                    if (changed != null) changed(ShotScale);
                }
                if (!done) Log.Write("screenshot: the headless window has no scale to set; laid out at 1x");
            } catch (Exception ex) { Log.Write("screenshot scale: " + ex.Message); }
        }

        // ---------- the route to the firmware ----------
        /// <summary>CORE: whether the firmware can be reached from this unprivileged process: the kernel module's
        /// device opened directly, or the root helper answering on its socket. The core decides; this only asks.</summary>
        static bool FirmwareReachable() {
            try { return Mailbox.Reachable; } catch (Exception ex) { Log.Write("route: " + ex.Message); return false; }
        }
        /// <summary>What is missing, in the core's own sentence.</summary>
        static string NoRouteWhy() { try { return Route.WhyNone; } catch { return "no route to the firmware"; } }

        // ---------- one instance ----------
        static string RuntimeDir() {
            string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.Combine(Path.GetTempPath(), FileStem + "-" + Environment.UserName);
            dir = Path.Combine(dir, FileStem);
            try { Directory.CreateDirectory(dir); File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            return dir;
        }
        /// <summary>The named mutex: a file held open with no sharing, which the kernel lets go of however the
        /// process ends. True when this process now owns it.</summary>
        static bool TakeInstance(string instance) {
            try {
                instanceLock = new FileStream(Path.Combine(RuntimeDir(), instance + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            } catch (IOException) { return false; }
            catch (Exception ex) { Log.Write("instance lock: " + ex.Message); return true; }      // no runtime directory to lock in: run anyway
        }
        /// <summary>The named events, from the other side: one word to the running instance.</summary>
        static bool Tell(string instance, string word) {
            try {
                using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
                    s.SendTimeout = 2000;
                    s.Connect(new UnixDomainSocketEndPoint(Path.Combine(RuntimeDir(), instance + ".sock")));
                    s.Send(Encoding.ASCII.GetBytes(word + "\n"));
                }
                return true;
            } catch { return false; }
        }
        static void Listen(string instance) {
            string path = Path.Combine(RuntimeDir(), instance + ".sock");
            Socket server;
            try {
                try { File.Delete(path); } catch { }             // left by an instance that did not get to clean up; the lock says it is gone
                server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                server.Bind(new UnixDomainSocketEndPoint(path));
                server.Listen(4);
            } catch (Exception ex) { Log.Write("instance socket: " + ex.Message); return; }
            var t = new Thread(delegate() {
                var buf = new byte[64];
                while (true) {
                    try {
                        using (var c = server.Accept()) {
                            c.ReceiveTimeout = 2000;
                            int n = c.Receive(buf);
                            string word = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n)).Trim();
                            if (word == "exit") { if (ExitEvent != null) ExitEvent.Set(); }
                            else if (word == "show") { if (ShowEvent != null) ShowEvent.Set(); }
                        }
                    } catch (SocketException) { }
                    catch { break; }
                }
            }) { IsBackground = true, Name = "instance-socket" };
            t.Start();
            AppDomain.CurrentDomain.ProcessExit += delegate { try { File.Delete(path); } catch { } };
        }

        // ---------- the console switches ----------
        /// <summary>--support: the report the Settings button produces, for anyone who would rather not open the
        /// window. --driver: the same thing for the driver alone. Both build their own engine, because they run
        /// before the single-instance guard and have to work beside a live Ohman.</summary>
        static int WriteSupport(string[] args) { return WriteReport(args, "support-info.txt", Support.Report, "the firmware was not asked anything"); }
        static int WriteDriverReport(string[] args) { return WriteReport(args, "driver-check.txt", Support.DriverReport, "the driver and the firmware were not asked anything"); }

        static int WriteReport(string[] args, string fileName, Func<Engine, string> build, string noRoute) {
            bool wantDemo = false;
            foreach (string a in args) if (a.ToLowerInvariant() == "--demo") wantDemo = true;
            AppPaths.Demo = wantDemo;
            bool route = !wantDemo && FirmwareReachable();
            IHardware hw2 = (wantDemo || !route) ? (IHardware)new DemoHardware() : new Bios();
            if (!wantDemo && !route) Console.WriteLine("NO ROUTE TO THE FIRMWARE - " + noRoute + ". " + NoRouteWhy() + ".\n");
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
                try { File.WriteAllText(Engine.EcPairPath, eng.EcPairFound == 2 ? "percent" : "rpm"); Console.WriteLine("recorded which fan register pair this board uses; Ohman reads it at its next start"); }
                catch (Exception ex) { Console.WriteLine("could not record the fan register pair: " + ex.Message); }
            }
            try {
                string p = Path.Combine(Path.GetDirectoryName(Log.Path), fileName);
                File.WriteAllText(p, rep);
                Console.WriteLine("saved to " + p);
            } catch (Exception ex) { Console.WriteLine("could not save: " + ex.Message); }
            try { eng.Dispose(); } catch { }
            return 0;
        }

        /// <summary>--lamps. Everything the keyboard's own HID lighting interface will tell us, and nothing written.
        /// On a four-zone laptop this finds HP's virtual device with its four lamps; on a per-key one it should find
        /// the keyboard itself with a lamp per key, which is what Ohman would drive.</summary>
        static int ListLamps() {
            // Also goes to the log: the log is what people end up attaching to an issue anyway.
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
    }
}
