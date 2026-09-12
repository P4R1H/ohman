// Ohman — entry point.
//   Ohman.exe                 normal start (elevated build asks for UAC once)
//   Ohman.exe --hidden        start minimised to the tray (used by the autostart task)
//   Ohman.exe --demo          force simulated hardware
//   Ohman.exe --demo --board 8A25   simulate another board id (shows the generic profile the engine would build)
//   Ohman.exe --screenshot f.png [--settings]   render the window to a PNG and exit (UI preview)
using System;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace Ohman {
    public static class Program {
        // To rename the app: change AppName here and the /out: names in build.cmd. Everything else follows
        // (window title, tray, scheduled task, single-instance names, log/state file names).
        public const string AppName = "Ohman";                 // internal id: file names, mutex, scheduled task
        public static string DisplayName = AppName;           // what the UI shows; override with Name=... in ohman.state
        public const string Version = "1.1";
        public static string FileStem { get { return AppName.ToLowerInvariant(); } }
        public static EventWaitHandle ShowEvent, ExitEvent;   // named events: another instance can ask us to show or exit
        public static bool FlashTest;                         // --flash: show the key OSD at start (preview/screenshot aid)
        public static bool KeyboardTest;                      // --keyboard: open the keyboard editor at start (screenshot aid)

        [STAThread]
        public static int Main(string[] args) {
            bool demo = false, hidden = false, settings = false; string shot = null;
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
                else if (a == "--board" && i + 1 < args.Length) Platforms.BoardOverride = args[++i];   // pretend to be another board (with --demo: see what generic mode would build)
            }
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "--make-ico") { MainWindow.WriteIco(args[i + 1], Ui.BalColor); return 0; }   // build aid: writes the app icon
            bool wantExit = false;
            foreach (string a0 in args) if (a0.ToLowerInvariant() == "--exit") wantExit = true;   // Ohman.exe --exit: stop the running instance (used when updating)
            bool created;
            var mutex = new Mutex(true, AppName + "_SingleInstance", out created);
            if (shot != null) demo = true;                                         // screenshots never touch firmware and may run beside a live instance
            if (!created && shot == null) {
                try { EventWaitHandle.OpenExisting(AppName + (wantExit ? "_Exit" : "_ShowPanel")).Set(); } catch { }
                return 0;
            }
            if (wantExit) return 0;
            try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppName + "_ShowPanel"); } catch { }
            try { ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppName + "_Exit"); } catch { }

            bool elevated = false;
            try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            IHardware hw = (demo || !elevated) ? (IHardware)new DemoHardware() : new Bios();
            Log.Write("---- " + AppName + " " + Version + " start · elevated=" + elevated + " · hardware=" + (hw.IsDemo ? "demo" : "bios") + (hidden ? " · hidden" : ""));

            var settingsObj = Settings.Load();
            foreach (string o in overrides) { int eq = o.IndexOf('='); if (eq > 0) settingsObj.Apply(o.Substring(0, eq), o.Substring(eq + 1)); }
            if (overrides.Count > 0 || shot != null) settingsObj.NoPersist = true;
            if (settingsObj.StartHidden) hidden = true;
            if (!string.IsNullOrEmpty(settingsObj.Name)) DisplayName = settingsObj.Name.Trim();
            var engine = new Engine(hw, settingsObj);
            engine.Init();
            var sensors = new Sensors();
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
    }
}

