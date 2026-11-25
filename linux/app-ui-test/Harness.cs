// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the interaction test for the real window. It builds MainWindow the way Program.Main does (simulated
// hardware, a home directory of its own), shows it in a headless Avalonia at 200 %, and then does what a person does:
// moves the pointer, presses buttons and keys. After each action it checks the engine's state and what the window
// shows. This file is the plumbing; Scenarios.cs is what gets clicked.
//
// What it reaches into, and why
//  - The window's fields are private, so they are read by name (G<T>). Nothing is set that way except the two
//    things a headless run cannot have: the platform handle (hwnd, which is what lets the morph animate) and the
//    text scale (the headless screen says 100 % while the window is laid out at 200 %).
//  - Program's statics that Main sets (headless, the lifetime, the show and exit events) are set here, because
//    this is a second entry point. The lifetime is one that was never started, so Exit can be clicked: its
//    Shutdown raises Exit and comes back instead of ending the process.
//  - Time. Animations run on Ui.Step (Frames), timers and the engine's worker on the real clock (Wait, Until).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Ohman {
    sealed class TestApp : Application {
        public override void Initialize() {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new SimpleTheme());
        }
    }

    static partial class UiTest {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const RawInputModifiers L = RawInputModifiers.LeftMouseButton;
        static MainWindow win;
        static Engine E;
        static Sensors sensors;
        static ClassicDesktopStyleApplicationLifetime lifetime;
        static int shutdowns;
        static string tmp, outDir, staticDir;
        static TimeSpan now = TimeSpan.FromSeconds(1);
        static readonly List<Window> open = new List<Window>();
        static int uiExceptions;

        // ---------- results ----------
        static string section = "";
        static readonly List<string> sections = new List<string>();
        static readonly Dictionary<string, int[]> tally = new Dictionary<string, int[]>();
        static readonly List<string> failures = new List<string>(), notes = new List<string>();
        static DateTime progress = DateTime.UtcNow;
        static void Section(string name) {
            section = name;
            if (!tally.ContainsKey(name)) { tally[name] = new int[2]; sections.Add(name); }
            Console.WriteLine("---- " + name);
            progress = DateTime.UtcNow;
        }
        static bool Check(bool ok, string what) {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
            tally[section][ok ? 0 : 1]++;
            if (!ok) failures.Add(section + ": " + what);
            progress = DateTime.UtcNow;
            return ok;
        }
        /// <summary>Something the run could not do, said once in the summary; not a failure of the app.</summary>
        static void Note(string what) { Console.WriteLine("note  " + what); notes.Add(section + ": " + what); }

        // ---------- the window's privates ----------
        static T G<T>(string name) {
            var f = typeof(MainWindow).GetField(name, BF);
            if (f == null) throw new InvalidOperationException("MainWindow has no field " + name);
            return (T)f.GetValue(win);
        }
        static void Put(string name, object v) { typeof(MainWindow).GetField(name, BF).SetValue(win, v); }
        static object Call(string name, params object[] a) {
            foreach (var m in typeof(MainWindow).GetMethods(BF))
                if (m.Name == name && m.GetParameters().Length == a.Length) {
                    try { return m.Invoke(m.IsStatic ? null : win, a); }
                    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                }
            throw new InvalidOperationException("MainWindow has no method " + name + "/" + a.Length);
        }
        static T Priv<T>(object o, string name) {
            for (Type t = o.GetType(); t != null; t = t.BaseType) {
                var f = t.GetField(name, BF);
                if (f != null) return (T)f.GetValue(o);
            }
            throw new InvalidOperationException(o.GetType().Name + " has no field " + name);
        }
        static void ProgramSet(string name, object v) { typeof(Program).GetField(name, BF).SetValue(null, v); }
        static object ProgramCall(string name, params object[] a) { return typeof(Program).GetMethod(name, BF).Invoke(null, a); }
        static string Cur { get { return G<object>("cur").ToString(); } }

        // ---------- time ----------
        static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        /// <summary>Move the animation clock on by ms, in 60 Hz frames.</summary>
        static void Frames(double ms) {
            for (double t = 0; t < ms; t += 1000.0 / 60) { now += TimeSpan.FromMilliseconds(1000.0 / 60); Ui.Step(now); Dispatcher.UIThread.RunJobs(); }
            Pump();
        }
        /// <summary>Let real time pass with the dispatcher running: timers fire, the engine's worker posts back.</summary>
        static void Wait(int ms) {
            using (var cts = new CancellationTokenSource(ms)) Dispatcher.UIThread.MainLoop(cts.Token);
            Pump();
        }
        static bool Until(Func<bool> done, int timeoutMs) {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true) {
                Frames(17);                              // animations go on while waiting, as they do on a screen
                if (done()) return true;
                if (DateTime.UtcNow > end) return false;
                Wait(25);
            }
        }
        static bool Until(Func<bool> done) { return Until(done, 4000); }
        /// <summary>After an action: debounces fire, the engine applies, Refresh comes back, animations finish.</summary>
        static void Settle() { Settle(450); }
        static void Settle(int ms) { Wait(ms); Frames(1300); Wait(30); Frames(100); }

        // ---------- the pointer ----------
        static string Describe(object o) {
            var c = o as Control;
            if (c == null) return o == null ? "nothing" : o.GetType().Name;
            return c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : " " + c.Name);
        }
        /// <summary>Scroll whatever scrolls c into the middle of its viewport.</summary>
        static void Reveal(Control c) {
            foreach (var a in c.GetVisualAncestors()) {
                var sv = a as ScrollViewer;
                if (sv == null) continue;
                var content = sv.Content as Control;
                if (content == null) continue;
                var p = c.TranslatePoint(new Point(0, 0), content);
                if (p == null) continue;
                double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                double top = p.Value.Y - sv.Offset.Y, bottom = top + c.Bounds.Height;
                if (top >= 8 && bottom <= sv.Viewport.Height - 8) continue;
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, Math.Min(max, p.Value.Y - sv.Viewport.Height / 2 + c.Bounds.Height / 2)));
                Pump(); Pump();
            }
        }
        /// <summary>Where a point of c is in its top-level, after checking that a pointer there really reaches c:
        /// something lying over it, or c being off the window, is a failure of the window, not of the test.</summary>
        static bool Aim(Control c, Point local, string what, out TopLevel top, out Point p) {
            p = new Point();
            top = c == null ? null : TopLevel.GetTopLevel(c);
            if (c == null || top == null || !c.IsEffectivelyVisible) { Check(false, what + ": the control is not on screen"); return false; }
            Pump();
            Reveal(c);
            var tp = c.TranslatePoint(local, top);
            if (tp == null) { Check(false, what + ": no position in the window"); return false; }
            p = tp.Value;
            if (p.X < 0 || p.Y < 0 || p.X >= top.ClientSize.Width || p.Y >= top.ClientSize.Height) { Check(false, what + ": the point " + p + " is outside the window " + top.ClientSize); return false; }
            var hit = top.InputHitTest(p) as Visual;
            if (hit == null || !(hit == c || c.IsVisualAncestorOf(hit))) { Check(false, what + ": a pointer at " + p + " reaches " + Describe(hit) + ", not " + Describe(c)); return false; }
            return true;
        }
        static Point Mid(Control c) { return new Point(c.Bounds.Width / 2, c.Bounds.Height / 2); }
        static bool Click(Control c, string what) { return c != null ? ClickAt(c, Mid(c), what) : Check(false, what + ": no such control"); }
        static bool ClickAt(Control c, Point local, string what) { return ClickAt(c, local, what, RawInputModifiers.None); }
        static bool ClickAt(Control c, Point local, string what, RawInputModifiers mods) {
            TopLevel top; Point p;
            if (!Aim(c, local, what, out top, out p)) return false;
            try {
                top.MouseMove(p, mods);
                Pump();
                top.MouseDown(p, MouseButton.Left, mods);
                top.MouseUp(p, MouseButton.Left, mods);
                Pump();
            } catch (Exception ex) { Thrown(what, ex); return false; }
            return true;
        }
        /// <summary>In the app an exception in a handler ends in the dispatcher's filter and the log ("UI EXCEPTION").
        /// Here the handler was called straight from the test, so it lands here: same verdict, same line in the log.</summary>
        static void Thrown(string what, Exception ex) {
            uiExceptions++;
            Log.Write("UI EXCEPTION: (" + what + ") " + ex);
            Check(false, what + ": threw " + ex.GetType().Name + ": " + ex.Message);
        }
        /// <summary>Drag a slider's thumb to where value sits on its track.</summary>
        static bool Drag(Slider s, double value, string what) {
            Pump();
            Reveal(s);
            var thumb = s.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            var track = s.GetVisualDescendants().OfType<Track>().FirstOrDefault();
            if (thumb == null || track == null) return Check(false, what + ": the slider has no thumb or track");
            TopLevel top; Point from;
            if (!Aim(thumb, Mid(thumb), what, out top, out from)) return false;
            double usable = track.Bounds.Width - thumb.Bounds.Width;
            double x = thumb.Bounds.Width / 2 + (value - s.Minimum) / Math.Max(1e-9, s.Maximum - s.Minimum) * usable;
            Point to = track.TranslatePoint(new Point(x, track.Bounds.Height / 2), top).Value;
            try {
                top.MouseMove(from);
                top.MouseDown(from, MouseButton.Left);
                for (int i = 1; i <= 4; i++) top.MouseMove(new Point(from.X + (to.X - from.X) * i / 4, from.Y + 3), L);
                top.MouseUp(new Point(to.X, from.Y + 3), MouseButton.Left);
                Pump();
            } catch (Exception ex) { Thrown(what, ex); return false; }
            return true;
        }
        static void Key(Key k, RawInputModifiers mods) {
            try { win.KeyPress(k, mods, PhysicalKey.None, null); Pump(); }
            catch (Exception ex) { Thrown("key " + k, ex); }
        }
        static void KeyUp(Key k, RawInputModifiers mods) {
            try { win.KeyRelease(k, mods, PhysicalKey.None, null); Pump(); }
            catch (Exception ex) { Thrown("key up " + k, ex); }
        }

        // ---------- reading the window ----------
        static string Txt(TextBlock t) {
            if (t == null) return "";
            if (t.Inlines != null && t.Inlines.Count > 0) return string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text ?? ""));
            return t.Text ?? "";
        }
        static IList<Control> Cells(Seg s) { return ((Panel)((Grid)s.Child).Children[1]).Children; }
        static IList<Control> Links(LinkSeg s) { return ((StackPanel)s.Children[0]).Children; }
        static int Sel(object segOrLink) { return Priv<int>(segOrLink, "sel"); }
        static double PillX(Seg s) { return ((TranslateTransform)((Border)((Grid)s.Child).Children[0]).RenderTransform).X; }
        static IEnumerable<T> All<T>(Visual root) where T : class { return root.GetVisualDescendants().OfType<T>(); }
        /// <summary>The first visible text block under root that says exactly this.</summary>
        static TextBlock Find(Visual root, string text) {
            foreach (var t in All<TextBlock>(root)) if (t.IsEffectivelyVisible && Txt(t) == text) return t;
            return null;
        }

        // ---------- dialogs ----------
        static string lastDialog = "";
        static int dialogs;
        /// <summary>The next message box gets this answer. MessageBox.Show blocks inside the click that raised it and
        /// runs the dispatcher while it waits, so the answer is queued first and runs from inside that wait: it finds
        /// the dialog and clicks the button with the pointer, as its owner would.</summary>
        static void Answer(string button) {
            int tries = 0;
            Action act = null;
            act = delegate {
                Window dlg = null;
                foreach (var w in open) if (w != win && !(w is Osd) && !(w is BenchPill) && w.IsVisible) dlg = w;
                if (dlg == null) { if (++tries < 40) DispatcherTimer.RunOnce(act, TimeSpan.FromMilliseconds(25)); return; }
                try {
                    dlg.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    var texts = All<TextBlock>(dlg).ToList();
                    lastDialog = texts.Count > 0 ? Txt(texts[0]) : "";
                    dialogs++;
                    var label = texts.FirstOrDefault(t => Txt(t) == button);
                    var b = label == null ? null : label.Parent as Control;
                    if (b == null) { Check(false, "dialog: no \"" + button + "\" button"); dlg.Close(); return; }
                    Point p = b.TranslatePoint(Mid(b), dlg).Value;
                    var hit = dlg.InputHitTest(p) as Visual;
                    if (hit == null || !(hit == b || b.IsVisualAncestorOf(hit))) Check(false, "dialog: the pointer on \"" + button + "\" reaches " + Describe(hit));
                    dlg.MouseMove(p);
                    dlg.MouseDown(p, MouseButton.Left);
                    dlg.MouseUp(p, MouseButton.Left);
                    if (dlg.IsVisible) { Check(false, "dialog: clicking \"" + button + "\" did not close it"); dlg.Close(); }
                } catch (Exception ex) { Thrown("dialog", ex); try { dlg.Close(); } catch { } }
            };
            Dispatcher.UIThread.Post(act, DispatcherPriority.Background);
        }

        // ---------- pictures ----------
        static void Shot(string name) {
            try {
                Pump();
                var root = (Control)win.Content;
                root.UpdateLayout();
                var px = new PixelSize((int)Math.Ceiling(root.Bounds.Width * 2), (int)Math.Ceiling(root.Bounds.Height * 2));
                using (var rtb = new RenderTargetBitmap(px, new Vector(192, 192))) { rtb.Render(root); rtb.Save(Path.Combine(outDir, name + ".png")); }
            } catch (Exception ex) { Check(false, "screenshot " + name + ": " + ex.Message); }
        }
        /// <summary>Width and height of a PNG, from its header.</summary>
        static bool PngSize(string path, out int w, out int h) {
            w = h = 0;
            try {
                using (var f = File.OpenRead(path)) {
                    var b = new byte[24];
                    if (f.Read(b, 0, 24) < 24) return false;
                    w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                    h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                    return true;
                }
            } catch { return false; }
        }
        /// <summary>The share of pixels that differ by more than a small tolerance between two PNGs of one size;
        /// -1 when they are not the same size or cannot be read. The measure scratchpad\cmp.py uses.</summary>
        static double Differ(string a, string b) {
            try {
                using (var x = SkiaSharp.SKBitmap.Decode(a)) using (var y = SkiaSharp.SKBitmap.Decode(b)) {
                    if (x == null || y == null || x.Width != y.Width || x.Height != y.Height) return -1;
                    long bad = 0;
                    for (int j = 0; j < x.Height; j++) for (int i = 0; i < x.Width; i++) {
                        var p = x.GetPixel(i, j); var q = y.GetPixel(i, j);
                        int lum = Math.Abs((p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000 - (q.Red * 299 + q.Green * 587 + q.Blue * 114) / 1000);
                        if (lum > 40) bad++;
                    }
                    return bad / (double)(x.Width * (long)x.Height);
                }
            } catch { return -1; }
        }

        // ---------- start ----------
        static int Main(string[] args) {
            tmp = Environment.GetEnvironmentVariable("OHMAN_UITEST_DIR") ?? "";
            string home = Environment.GetEnvironmentVariable("HOME") ?? "", ohome = Environment.GetEnvironmentVariable("OHMAN_HOME") ?? "";
            // Not one file of the real user's may be touched, and no window may open: refuse anything but run.sh's setup.
            if (tmp.Length == 0 || !home.StartsWith(tmp) || !ohome.StartsWith(tmp)
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) {
                Console.Error.WriteLine("Run this through run.sh: it needs a home of its own (OHMAN_UITEST_DIR, HOME, OHMAN_HOME) and no display.");
                return 2;
            }
            outDir = Environment.GetEnvironmentVariable("OUT") ?? Path.Combine(tmp, "shots");
            staticDir = Environment.GetEnvironmentVariable("STATIC") ?? "";
            Directory.CreateDirectory(outDir);
            var only = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));

            // A run that stops answering (a dialog nobody closed, a wait that never ends) says where and leaves.
            new Thread(delegate() {
                while (true) {
                    Thread.Sleep(2000);
                    if ((DateTime.UtcNow - progress).TotalSeconds > 150) { Console.WriteLine("HANG  no progress for 150 s in: " + section); Console.Out.Flush(); Environment.Exit(3); }
                }
            }) { IsBackground = true }.Start();

            // Program.Main, as far as it applies: simulated hardware on the board the reference pictures use.
            AppPaths.Demo = true;
            Platforms.BoardOverride = "8C58";
            ProgramSet("headless", true);
            // A lifetime nobody started: Program.Shutdown ends in its Shutdown, which raises Exit and returns here.
            lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            lifetime.Exit += delegate { shutdowns++; };
            ProgramSet("lifetime", lifetime);
            Section("start");
            Check((bool)ProgramCall("TakeInstance", "ohman_demo"), "the instance lock is taken");
            Program.ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            Program.ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            ProgramCall("Listen", "ohman_demo");

            var settings = Settings.Load();
            E = new Engine(new DemoHardware(), settings);
            E.Init();
            sensors = new Sensors();
            sensors.CpuSource = delegate { return E.Cpu; };
            sensors.DeadZoneHint = settings.DeadZone;
            sensors.Start();
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e) { Log.Write("UNHANDLED: " + e.ExceptionObject); };

            AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Ui.ManualFrames = true;
            Dispatcher.UIThread.UnhandledException += delegate(object o, DispatcherUnhandledExceptionEventArgs e) {
                uiExceptions++;
                Log.Write("UI EXCEPTION: " + e.Exception);
                Console.WriteLine("FAIL  UI EXCEPTION in " + section + ": " + e.Exception);
                failures.Add(section + ": UI EXCEPTION " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                e.Handled = true;
            };
            Window.WindowOpenedEvent.AddClassHandler<Window>(delegate(Window w, Avalonia.Interactivity.RoutedEventArgs e) { if (!open.Contains(w)) open.Add(w); });
            Window.WindowClosedEvent.AddClassHandler<Window>(delegate(Window w, Avalonia.Interactivity.RoutedEventArgs e) { open.Remove(w); });

            try {
                win = new MainWindow(E, sensors, null, false);
                // The headless screen reports 100 % while the window is laid out at 200 % (Program.HeadlessScale), and
                // the window spaces its text for the screen's scale: give it the scale it is really drawn at, as
                // --screenshot does, and respace the text already built.
                typeof(MainWindow).GetField("TextScale", BF).SetValue(null, 2.0);
                foreach (var d in ((Control)win.Content).GetLogicalDescendants().OfType<TextBlock>()) Call("WpfAdvance", d);
                // Started the way the autostart entry starts it (--hidden): the window exists and has never been shown.
                // The hotkeys and the flash work from there, and the first Show is the tray's.
                Wait(300);
                Check(!win.IsVisible, "started hidden, there is no window on screen");
                try {
                    Call("OnHotkey", 0);
                    Pump();
                    Check(OsdTitle == "Eco mode" && Until(delegate { return E.ModeIndex == 0; }), "a hotkey works before the window has ever been shown (\"" + OsdTitle + "\")");
                    Call("OnHotkey", 1);
                    Check(Until(delegate { return E.ModeIndex == 1; }) && !win.IsVisible, "and back, with the window still hidden");
                    Wait(1500); Frames(400);
                    win.ShowPanel();
                } catch (Exception ex) { Thrown("the first show", ex); }
                // The headless window has no platform handle, and the window only animates its size once it has one
                // (WPF: the HWND). Any value will do: nothing here passes it to a window system.
                Put("hwnd", new IntPtr(1));
                Settle(1200);
                Check(win.IsVisible && Txt(G<TextBlock>("txtHomeTitle")) == "Balanced" && Ui.Accent.Color == Ui.BalColor && G<Border>("railPill").Opacity == 1, "the first Show brings up Home in the mode the engine is in");
                Run(only);
            } catch (Exception ex) {
                Section("crash");
                Check(false, "the run stopped: " + ex);
            }

            Console.WriteLine();
            Console.WriteLine("==== summary");
            int pass = 0, fail = 0;
            foreach (string s in sections) { int[] t = tally[s]; pass += t[0]; fail += t[1]; Console.WriteLine((s + "                              ").Substring(0, 30) + t[0].ToString().PadLeft(4) + " passed" + (t[1] > 0 ? t[1].ToString().PadLeft(4) + " FAILED" : "")); }
            foreach (string f in failures) Console.WriteLine("FAIL  " + f);
            foreach (string n in notes) Console.WriteLine("note  " + n);
            Console.WriteLine(fail == 0 ? "PASS  " + pass + " checks" : "FAILED " + fail + " of " + (pass + fail) + " checks");
            Console.Out.Flush();
            Environment.Exit(fail == 0 ? 0 : 1);        // threads of a disposed engine and the instance socket must not hold the process
            return 0;
        }
    }
}
