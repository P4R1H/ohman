// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the harness for the ported benchmark page. It shows BenchPage in each state the Windows build can
// pose (--bench-state), in a headless window at 200 % and at the page's own width, saves a PNG of each, and renders
// the three share cards at their real size. Run: ohman <output directory> [state ...]. The stored runs the result,
// the list and the cards show are read from the data directory (OHMAN_HOME/demo/bench/runs).
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Simple;
using Avalonia.Threading;

namespace Ohman {
    sealed class App : Application {
        public override void Initialize() { Styles.Add(new SimpleTheme()); }
    }

    public static partial class Program {
        static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

        /// <summary>The references were rendered at 200 %, where layout rounds to half pixels. The headless window says
        /// its scale is 1 and offers no way to change it, so the harness reaches in (as the controls harness does).</summary>
        static void Scale2(Window w) {
            var impl = w.PlatformImpl;
            var f = impl.GetType().GetField("<RenderScaling>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f == null) { Console.WriteLine("      (no way to set the headless scale: layout is rounded for 100 %)"); return; }
            f.SetValue(impl, 2.0);
            var pi = impl.GetType().GetProperty("ScalingChanged");
            var changed = pi == null ? null : pi.GetValue(impl) as Action<double>;
            if (changed != null) changed(2.0);
        }

        /// <summary>The main window spaces its text the way WPF's Display mode does (MainWindow.WpfAdvance in
        /// ../app/Ui.cs, a class handler on every TextBlock). The page lives in that window, so the harness does the
        /// same here, with the same numbers, or its text would be a little narrower than what the app shows.</summary>
        static void WpfAdvance(TextBlock tb) {
            double k = 2, fs = tb.FontSize;
            var f = tb.FontFamily;
            if (f == null || fs <= 0) return;
            bool mono = f.Name != null && f.Name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0;
            bool sans = !mono && tb.FontWeight < FontWeight.Medium && f.Name != null && f.Name.IndexOf("Plex", StringComparison.OrdinalIgnoreCase) >= 0;
            double ls = mono ? Math.Round(0.6 * fs * k, MidpointRounding.AwayFromZero) / k - 0.6 * fs : sans ? 0.11 / k : 0;
            if (Math.Abs(tb.LetterSpacing - ls) > 0.001) tb.LetterSpacing = ls;
        }

        public static int Main(string[] args) {
            string dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            var states = new List<string>();
            for (int i = 1; i < args.Length; i++) states.Add(args[i]);
            if (states.Count == 0) states.AddRange(new[] { "first", "ready", "waiting", "warming", "measuring", "paused", "result", "runs", "share0", "share1", "share2", "result3", "result7" });
            AppPaths.Demo = true;
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Ui.ManualFrames = true;
            Ui.LoadFonts();
            if (Array.IndexOf(args, "--plain") < 0) {
                TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
                TextBlock.FontFamilyProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
                TextBlock.FontWeightProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
            }
            var src = new ColorSource { Color = Ui.ModeColor(1) };
            Ui.Accent = src.MakeBrush();

            var s = new Settings();
            s.NoPersist = true;
            var engine = new Engine(new DemoHardware(), s);
            engine.Init();
            var bg = Ui.Brush("#161311");
            int failed = 0;
            foreach (string state in states) {
                if (state.StartsWith("--")) continue;
                try {
                    var bench = new Bench(engine, true);
                    var page = new BenchPage(engine, bench);
                    // what the window feeds the page: a reading a second, and the fans
                    var rnd = new Random(3);
                    for (int i = 0; i < 40; i++) {
                        page.OnSensors(new SensorSnapshot { CpuTemp = 52 + rnd.Next(3), CpuWatts = 14 + rnd.Next(4), CpuMhz = 2900 + rnd.Next(300), GpuTemp = 52, GpuWatts = 14, GpuMhz = 300, GpuLoad = 0, CpuLoad = 30 });
                        page.OnFans(new[] { 33, 31 });
                    }
                    bench.Pose(state);
                    var w = new Window { Width = 398, Height = 900, Background = bg, UseLayoutRounding = true, Content = new Border { Background = bg, Child = page, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top } };
                    Scale2(w);
                    w.Show();
                    page.IsVisible = true;
                    page.Pose(state);
                    Pump();
                    double h = Math.Ceiling(page.Fit(2000));          // the window is a whole number of DIPs tall, as on Windows
                    w.Height = h;
                    Pump(); Pump();
                    var root = (Control)w.Content;
                    var px = new PixelSize(796, (int)Math.Round(h * 2));
                    using (var rtb = new RenderTargetBitmap(px, new Vector(192, 192))) { rtb.Render(root); rtb.Save(Path.Combine(dir, state + ".png")); }
                    Console.WriteLine("ok    " + state + "   " + px.Width + " x " + px.Height);
                    if (state.StartsWith("share")) {
                        // the card itself, at its real size, the file Save writes
                        var runs = RunStore.List();
                        if (runs.Count > 0) {
                            var full = RunStore.Full(runs[0]);
                            BenchRun prev = null;
                            foreach (var o in runs) if (o.Id != full.Id && o.Started < full.Started && full.SameSetup(o)) { prev = o; break; }
                            int layout = int.Parse(state.Substring(5));
                            using (var card = Card.Render(full, prev, layout, 0)) card.Save(Path.Combine(dir, "card" + layout + ".png"));
                            Console.WriteLine("ok    card" + layout);
                        }
                    }
                    w.Close();
                    bench.Dispose();
                } catch (Exception ex) { Console.WriteLine("FAIL  " + state + "   " + ex); failed++; }
            }
            engine.Dispose();
            return failed == 0 ? 0 : 1;
        }
    }
}
