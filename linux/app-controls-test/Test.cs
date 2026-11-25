// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the harness for the ported controls. It shows each one in a headless window in the state the Windows
// reference screenshots show it, saves a PNG at 2x, and then drives the pointer over a few of them and checks what
// they did. Run: ohman <output directory>. Exit code 0 when every check passed.
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Simple;
using Avalonia.Threading;

namespace Ohman {
    sealed class App : Application {
        public override void Initialize() { Styles.Add(new SimpleTheme()); }
    }

    public static partial class Program {
        const string LEAF = "M4 20 C4 11 10 4 20 4 C20 13 14 20 4 20 Z M4 20 L13 11";
        const string BOLT = "M13 2 L4 14 L11 14 L10 22 L20 9 L13 9 Z";
        const string ICO_HOME_RING = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 Z";
        const string ICO_HOME_DOT = "M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z";
        const string ICO_FANS = "M3 8.5 C5.5 5.5 8.5 11.5 12 8.5 C15.5 5.5 18.5 11.5 21 8.5 M3 15.5 C5.5 12.5 8.5 18.5 12 15.5 C15.5 12.5 18.5 18.5 21 15.5";
        const string ICO_BENCH = "M4.2 17.5 A8.5 8.5 0 1 1 19.8 17.5 M12 13.5 L16.2 9.3", ICO_BENCH_DOT = "M12 12 A1.5 1.5 0 1 0 12 15 A1.5 1.5 0 1 0 12 12 Z";
        const string ICO_KBD = "M2.5 7.5 A2 2 0 0 1 4.5 5.5 H19.5 A2 2 0 0 1 21.5 7.5 V16.5 A2 2 0 0 1 19.5 18.5 H4.5 A2 2 0 0 1 2.5 16.5 Z M6 9.5 H6.4 M9.8 9.5 H10.2 M13.6 9.5 H14 M17.4 9.5 H17.8 M6 12.5 H6.4 M9.8 12.5 H10.2 M13.6 12.5 H14 M17.4 12.5 H17.8 M7.5 15.5 H16.5";
        static string F2(double v) { return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }
        static string GearPath(double cx, double cy, double rOut, double rIn, int teeth, double hole) {
            var sb = new System.Text.StringBuilder();
            double step = 2 * Math.PI / teeth;
            for (int i = 0; i < teeth; i++) {
                double a0 = i * step;
                double[] angs = { a0 + 0.06 * step, a0 + 0.44 * step, a0 + 0.56 * step, a0 + 0.94 * step }; double[] rads = { rOut, rOut, rIn, rIn };
                for (int k = 0; k < 4; k++) {
                    double x = cx + rads[k] * Math.Cos(angs[k]), y = cy + rads[k] * Math.Sin(angs[k]);
                    sb.Append(i == 0 && k == 0 ? "M" : "L").Append(F2(x)).Append(' ').Append(F2(y)).Append(' ');
                }
            }
            sb.Append("Z ");
            string h = F2(hole), cys = F2(cy);
            sb.Append("M" + F2(cx - hole) + " " + cys + " A" + h + " " + h + " 0 1 0 " + F2(cx + hole) + " " + cys + " A" + h + " " + h + " 0 1 0 " + F2(cx - hole) + " " + cys + " Z");
            return "F0 " + sb.ToString();
        }

        const RawInputModifiers L = RawInputModifiers.LeftMouseButton;
        static string dir;
        static int failed;
        static TimeSpan now = TimeSpan.FromSeconds(1);
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); if (!ok) failed++; }
        static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        /// <summary>Move the animation clock on by ms, in 60 Hz frames.</summary>
        static void Run(double ms) {
            for (double t = 0; t < ms; t += 1000.0 / 60) { now += TimeSpan.FromMilliseconds(1000.0 / 60); Ui.Step(now); Dispatcher.UIThread.RunJobs(); }
            Pump();
        }

        static Window Host(Control c, double width, IBrush bg, Thickness pad) {
            var w = new Window { Width = width + pad.Left + pad.Right, SizeToContent = SizeToContent.Height, Background = bg, UseLayoutRounding = true,
                Content = new Border { Background = bg, Padding = pad, Child = c } };
            Scale2(w);
            w.Show();
            Pump();
            return w;
        }
        /// <summary>The references were rendered at 200 %, where layout rounds to half pixels. The headless window says
        /// its scale is 1 and offers no way to change it, so the harness reaches in: with this the layout is the one a
        /// 200 % display gives.</summary>
        static void Scale2(Window w) {
            var impl = w.PlatformImpl;
            var f = impl.GetType().GetField("<RenderScaling>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f == null) { Console.WriteLine("      (no way to set the headless scale: layout is rounded for 100 %)"); return; }
            f.SetValue(impl, 2.0);
            var pi = impl.GetType().GetProperty("ScalingChanged");
            var changed = pi == null ? null : pi.GetValue(impl) as Action<double>;
            if (changed != null) changed(2.0);
        }
        static void Save(Window w, string name) {
            Pump();
            var root = (Control)w.Content;
            var px = new PixelSize((int)Math.Ceiling(root.Bounds.Width * 2), (int)Math.Ceiling(root.Bounds.Height * 2));
            using (var rtb = new RenderTargetBitmap(px, new Vector(192, 192))) { rtb.Render(root); rtb.Save(System.IO.Path.Combine(dir, name + ".png")); }
        }
        static Window Shot(Control c, double width, string name) { return Shot(c, width, name, Ui.Card); }
        static Window Shot(Control c, double width, string name, IBrush bg) {
            var w = Host(c, width, bg, new Thickness(10));
            Save(w, name);
            return w;
        }
        static Point At(Control c, Window w, double x, double y) { return c.TranslatePoint(new Point(x, y), w).Value; }

        public static int Main(string[] args) {
            dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Ui.ManualFrames = true;
            Ui.LoadFonts();
            Check(Ui.UiFont.Name.Contains("Plex"), "fonts resolve from avares://ohman/fonts");

            // ---- the accent: one source, every brush follows ----
            var src = new ColorSource { Color = Ui.ModeColor(1) };
            Ui.Accent = src.MakeBrush();
            var grad = src.MakeGradient();
            Check(Ui.Accent.Color == Ui.BalColor, "accent brush starts at the source's colour");

            Segs();
            Links();
            Rail(grad);
            Curve();
            Kbd();
            Strips();
            Small();
            Flash();
            Accent(src, grad);

            Console.WriteLine(failed == 0 ? "PASS" : "FAILED " + failed);
            return failed == 0 ? 0 : 1;
        }

        static void Segs() {
            var mode = new Seg(new[] { "Eco", "Balanced", "Performance" }, null, null, Seg.Kind.Page);
            mode.Select(1, false);
            var w = Shot(mode, 350, "seg-mode");
            int picked = -1;
            mode.Picked += delegate(int i) { picked = i; };
            var c0 = (Border)((Panel)((Grid)mode.Child).Children[1]).Children[0];
            Console.WriteLine("      scale " + Avalonia.Layout.LayoutHelper.GetLayoutScale(mode) + ", seg " + mode.Bounds + ", cell " + c0.Bounds + ", label " + c0.Child.Bounds + " desired " + c0.Child.DesiredSize);
            var pill = (Border)((Grid)mode.Child).Children[0];
            var cells = (Panel)((Grid)mode.Child).Children[1];
            Check(Math.Abs(pill.Width - cells.Bounds.Width / 3) < 1 && Math.Abs(((TranslateTransform)pill.RenderTransform).X - Math.Round(cells.Bounds.Width / 3)) < 1, "seg: the pill sits on the selected cell");
            // hover the first cell, then click the third
            w.MouseMove(At(cells.Children[0], w, 20, 10));
            Pump();
            Save(w, "seg-mode-hover");
            w.MouseDown(At(cells.Children[2], w, 20, 10), MouseButton.Left);
            w.MouseUp(At(cells.Children[2], w, 20, 10), MouseButton.Left);
            Pump();
            Check(picked == 2, "seg: a click picks the cell under it (" + picked + ")");
            double x0 = ((TranslateTransform)pill.RenderTransform).X;
            Run(100);
            double xm = ((TranslateTransform)pill.RenderTransform).X;
            Save(w, "seg-mode-sliding");
            Run(400);
            double x1 = ((TranslateTransform)pill.RenderTransform).X;
            Check(xm > x0 + 20 && xm < x1 + 8 && Math.Abs(x1 - Math.Round(cells.Children[2].Bounds.X)) < 0.01, "seg: the pill glides (" + x0.ToString("0.0") + " -> " + xm.ToString("0.0") + " -> " + x1.ToString("0.0") + ")");
            // press on one cell, release on another: WPF picks the one under the release
            picked = -1;
            w.MouseDown(At(cells.Children[2], w, 20, 10), MouseButton.Left);
            w.MouseMove(At(cells.Children[0], w, 20, 10));
            w.MouseUp(At(cells.Children[0], w, 20, 10), MouseButton.Left);
            Pump();
            Check(picked == 0, "seg: the release decides, as MouseLeftButtonUp does (" + picked + ")");
            w.Close();

            var fan = new Seg(Choice.Fan, null, null, Seg.Kind.Page);
            fan.Select(0, false);
            Shot(fan, 350, "seg-fan").Close();

            var key = new Seg(Choice.Key, null, null, Seg.Kind.Compact);
            key.Select(1, false);
            Shot(key, 341, "seg-key").Close();

            var gpu = new Seg(new[] { "Base", "Boost", "Max", "Auto" }, null, null, Seg.Kind.Row);
            gpu.Select(3, false);
            Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { gpu } }, 341, "seg-gpu").Close();

            var gran = new Seg(new[] { "Key", "Row", "Zone", "All" }, null, null, Seg.Kind.Row);
            gran.SetEnabled(0, false, "no");
            gran.SetEnabled(1, false, "no");
            gran.Select(2, false);
            picked = -1;
            gran.Picked += delegate(int i) { picked = i; };
            w = Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { gran } }, 341, "seg-gran");
            var gc = (Panel)((Grid)gran.Child).Children[1];
            w.MouseDown(At(gc.Children[0], w, 8, 8), MouseButton.Left); w.MouseUp(At(gc.Children[0], w, 8, 8), MouseButton.Left);
            Check(picked == -1, "seg: a disabled cell does not pick");
            w.Close();

            var stop = new Seg(new[] { "15m", "30m", "60m", "Never" }, null, new object[] { 15, 30, 60, 0 }, Seg.Kind.Row); stop.SetMono(11.5);
            stop.Select(1, false);
            Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { stop } }, 341, "seg-stop").Close();
        }

        static void Links() {
            var fans = new LinkSeg(new[] { "Auto", "Max", "Manual" }, 18, 13, 3, null);
            fans.Select(0, false);
            var w = Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { fans } }, 350, "link-fans");
            int picked = -1;
            fans.Picked += delegate(int i) { picked = i; };
            var sp = (StackPanel)fans.Children[0];
            var line = (Border)fans.Children[1];
            // the padding under the text is part of the link
            var p = At(sp.Children[2], w, 10, sp.Children[2].Bounds.Height - 1);
            w.MouseMove(p); w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left);
            Pump();
            Check(picked == 2, "link: a click on the padding under the word picks it (" + picked + ")");
            Run(500);
            Check(Math.Abs(((TranslateTransform)line.RenderTransform).X - Math.Round(sp.Children[2].Bounds.X)) < 0.01 && Math.Abs(line.Width - Math.Round(sp.Children[2].Bounds.Width)) < 0.01, "link: the underline lands under the picked word");
            Save(w, "link-fans-manual");
            w.Close();

            var light = new LinkSeg(Choice.Light, 22, 13.5, 4, null);
            light.Select(1, false);
            Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { light } }, 620, "link-light").Close();

            var which = new LinkSeg(new[] { "CPU", "GPU" }, 12, 13, 3, null);
            which.SetEnabled(1, false, "no");
            which.Select(0, false);
            Shot(new StackPanel { Orientation = Orientation.Horizontal, Children = { which } }, 350, "link-which").Close();
        }

        static void Rail(IBrush grad) {
            var nav = new NavBtn[6];
            nav[0] = new NavBtn(0, "Home", new[] { ICO_HOME_RING }, new[] { ICO_HOME_DOT });
            nav[1] = new NavBtn(1, "Fans", new[] { ICO_FANS }, new string[0]);
            nav[2] = new NavBtn(2, "Keyboard", new[] { ICO_KBD }, new string[0]);
            nav[4] = new NavBtn(4, "Benchmark", new[] { ICO_BENCH }, new[] { ICO_BENCH_DOT });
            nav[3] = new NavBtn(3, "Settings", new string[0], new[] { GearPath(12, 12, 10, 7.6, 8, 3.4) });
            nav[5] = new NavBtn(-1, "Update available", new[] { "M12 4 V14.2 M7.6 10.2 L12 14.8 L16.4 10.2 M4.6 19 H19.4" }, new string[0]);
            nav[5].Accent = true;
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (int i in new[] { 0, 1, 2, 4, 3, 5 }) sp.Children.Add(nav[i]);
            int clicked = -99;
            foreach (var n in nav) n.Clicked += delegate(int i) { clicked = i; };
            var w = Shot(sp, 42, "rail", Ui.Sunken);
            nav[0].SetSelected(true);
            w.MouseMove(At(nav[1], w, 21, 21));
            Pump();
            Save(w, "rail-home-selected-fans-hover");
            w.MouseDown(At(nav[2], w, 6, 6), MouseButton.Left);
            Check(clicked == 2, "nav: the whole 42 px square clicks, on press (" + clicked + ")");
            w.MouseUp(At(nav[2], w, 6, 6), MouseButton.Left);
            clicked = -99;
            w.MouseDown(At(nav[2], w, 1, 1), MouseButton.Left); w.MouseUp(At(nav[2], w, 1, 1), MouseButton.Left);
            Check(clicked == -99, "nav: the cut-off corner is not part of the button, as in WPF");
            w.Close();
            Shot(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = grad, RenderTransform = new RotateTransform(45) }, 42, "mark", Ui.Sunken).Close();
        }

        static void Curve() {
            // the simulated Transcend 14 in Auto, as fans-curve.png shows it
            var cv = new CurveView { Floor = 18, Ceiling = 57, Temps = new[] { 30, 40, 50, 60, 70, 80, 90 }, Levels = new[] { 23, 23, 23, 25, 39, 46, 49 }, ReadOnly = true, LiveTemp = 51, LiveLevel = 35 };
            var w = Shot(cv, 350, "curve-auto");
            w.Close();

            cv = new CurveView { Floor = 18, Ceiling = 57, Levels = new[] { 18, 20, 24, 30, 38, 48, 57 }, LiveTemp = 62, LiveLevel = 31, UserFloor = 22 };
            int changes = 0;
            int[] last = null;
            cv.Changed += delegate(int[] v) { changes++; last = v; };
            w = Shot(cv, 350, "curve-edit");
            // the fourth point is at 60 degrees, level 30
            double px = 30 + (60 - 30) / 60.0 * (350 - 7 - 30), py = 20 + 196 * (1 - 30 / 57.0);
            w.MouseMove(At(cv, w, px, py));
            Pump();
            Save(w, "curve-hover");
            w.MouseDown(At(cv, w, px, py), MouseButton.Left);
            Check(cv.Dragging, "curve: pressing a point starts a drag");
            double ty = 20 + 196 * (1 - 45 / 57.0);
            w.MouseMove(At(cv, w, px + 60, ty), L);                // sideways movement does not matter, and the pointer may leave the handle
            Pump();
            Check(cv.Levels[3] == 45 && cv.Levels[4] == 45 && cv.Levels[5] == 48 && cv.Levels[2] == 24, "curve: the point follows the pointer and lifts the ones after it (" + string.Join(",", cv.Levels) + ")");
            Save(w, "curve-drag");
            w.MouseMove(At(cv, w, px, 20 + 196 * (1 - 10 / 57.0)), L);
            Check(cv.Levels[3] == 18 && cv.Levels[0] == 18 && cv.Levels[2] == 18, "curve: below the floor is the floor, and the earlier points come down (" + string.Join(",", cv.Levels) + ")");
            w.MouseMove(At(cv, w, px, 20 + 196 + 30), L);
            Check(cv.Levels[3] == 0, "curve: all the way down is off (" + string.Join(",", cv.Levels) + ")");
            w.MouseMove(At(cv, w, px, 20 + 196 * (1 - 30 / 57.0)), L);
            w.MouseMove(At(cv, w, px, 20 + 196 * (1 - 34 / 57.0)), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            Check(cv.Levels[3] == 34 && cv.Levels[6] == 57 && cv.Levels[5] == 52 && cv.Levels[0] == 18, "curve: shift moves the whole curve (" + string.Join(",", cv.Levels) + ")");
            int before = changes;
            w.MouseUp(At(cv, w, px, ty), MouseButton.Left);
            Check(!cv.Dragging && changes == before + 1 && last != null && last[3] == 34, "curve: release ends the drag and reports once more");
            Pump();
            Save(w, "curve-after");
            // a read-only curve has no handles
            cv.ReadOnly = true;
            w.MouseDown(At(cv, w, px, 20 + 196 * (1 - 34 / 57.0)), MouseButton.Left);
            Check(!cv.Dragging, "curve: read-only does not drag");
            w.MouseUp(At(cv, w, px, ty), MouseButton.Left);
            // the pulse
            cv.ReadOnly = false;
            Run(300);
            Save(w, "curve-pulse");
            w.Close();
        }

        static void Kbd() {
            var layout = KeyboardLayouts.Build(false, 4);
            var colors = new Rgb[4];
            Rgb.TryParse("2E6BFF", out colors[KeyboardLayouts.ZoneRight]);
            Rgb.TryParse("33A5E6", out colors[KeyboardLayouts.ZoneMiddle]);
            Rgb.TryParse("5FCBE0", out colors[KeyboardLayouts.ZoneLeft]);
            Rgb.TryParse("F2ECE6", out colors[KeyboardLayouts.ZoneWasd]);
            var big = new KeyboardView { Interactive = true, Gap = 5, RowPitch = 39 }; big.SetLayout(layout);
            big.SetColors(colors, false);
            var w = Shot(big, 590, "kbd-big");
            KeyDef hovered = null, clicked = null;
            int hovers = 0;
            big.KeyHovered += delegate(KeyDef k) {
                hovered = k; hovers++;
                big.Hover.Clear();
                if (k != null) foreach (var x in big.Keys) if (x.Zone == k.Zone) big.Hover.Add(x.Index);
                big.Repaint();
            };
            big.KeyClicked += delegate(KeyDef k) {
                clicked = k;
                big.Selected.Clear();
                if (k != null) foreach (var x in big.Keys) if (x.Zone == k.Zone) big.Selected.Add(x.Index);
                big.Repaint();
            };
            double u = (590 + 5) / 15.5;
            w.MouseMove(At(big, w, 0.5 * u, 10));                       // Esc
            Check(hovered != null && hovered.Label == "Esc", "keyboard: the key under the pointer is hovered");
            Pump();
            Save(w, "kbd-hover-left");
            int n = hovers;
            w.MouseMove(At(big, w, u - 2.5, 10));                       // the gap between Esc and F1
            Check(hovered != null && hovers <= n + 1, "keyboard: a gap belongs to the nearest key (" + (hovered == null ? "none" : hovered.Label) + ")");
            w.MouseMove(At(big, w, 2.5 * u, 2 * 39 + 10));              // W, on the third row
            Check(hovered != null && hovered.Label == "W", "keyboard: W is hovered (" + (hovered == null ? "none" : hovered.Label) + ")");
            w.MouseDown(At(big, w, 2.5 * u, 2 * 39 + 10), MouseButton.Left);
            w.MouseUp(At(big, w, 2.5 * u, 2 * 39 + 10), MouseButton.Left);
            Check(clicked != null && clicked.Label == "W" && big.Selected.Count == 4, "keyboard: a click selects the group (" + big.Selected.Count + " keys)");
            w.MouseMove(At(big, w, 300, 39 * 6 - 5 + 30));              // below the board, inside the window
            Check(hovered == null, "keyboard: leaving the board drops the hover");
            Pump();
            Save(w, "kbd-selected-wasd");
            // an effect frame eases in rather than jumping
            var next = (Rgb[])colors.Clone();
            next[KeyboardLayouts.ZoneRight] = new Rgb(255, 0, 0);
            big.Smooth = true;
            big.SetColors(next, true);
            Run(34);
            Save(w, "kbd-easing");
            Run(600);
            Save(w, "kbd-eased-smooth");
            big.Off = true; big.Smooth = false; big.Selected.Clear(); big.Repaint();
            Save(w, "kbd-off");
            w.Close();

            var mini = new KeyboardView { Interactive = false, Gap = 1.5, RowPitch = 9.5 }; mini.SetLayout(layout);
            mini.SetColors(colors, false);
            Shot(new Border { Width = 145, HorizontalAlignment = HorizontalAlignment.Left, Child = mini }, 145, "kbd-mini").Close();
        }

        static void Strips() {
            var hue = new StripPicker { Cells = 36 };
            var shade = new StripPicker { Cells = 30, Shade = true };
            var sp = new StackPanel();
            sp.Children.Add(new Border { Height = 33, Child = hue });
            sp.Children.Add(new Border { Height = 33, Child = shade });
            var w = Shot(sp, 402, "strips");
            Rgb got = new Rgb();
            int picks = 0;
            hue.Picked += delegate(Rgb c) { got = c; picks++; };
            double cw = 402 / 36.0;
            w.MouseDown(At(hue, w, cw * 12.5, 10), MouseButton.Left);
            Check(picks == 1 && got.Hex == hue.ColorAt(12).Hex, "strip: a press picks the cell under it (" + got.Hex + ")");
            w.MouseMove(At(hue, w, cw * 20.5, 60), RawInputModifiers.LeftMouseButton);      // dragged, and off the strip downwards
            Check(picks == 2 && got.Hex == hue.ColorAt(20).Hex, "strip: a drag keeps picking, even outside it (" + got.Hex + ")");
            w.MouseUp(At(hue, w, cw * 20.5, 60), MouseButton.Left);
            w.MouseMove(At(hue, w, cw * 3.5, 10));
            Check(picks == 2, "strip: moving without the button does not pick");
            hue.Current = got; hue.HasCurrent = true; hue.Repaint();
            shade.Hue = 120; shade.Current = shade.ColorAt(9); shade.HasCurrent = true; shade.Repaint();
            Save(w, "strips-current");
            w.Close();
        }

        static void Small() {
            var t = new Tracked { Text = "OMEN KEY", HorizontalAlignment = HorizontalAlignment.Left };
            Shot(t, 341, "tracked").Close();

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var a = new ValueLink { Items = new[] { "85°", "90°", "95°" }, ToolTip = "tip" };
            a.Index = 1;
            var b = new ValueLink { Items = new[] { "Max fan", "Nothing" } };
            b.Index = 0; b.Editable = true;
            line.Children.Add(new TextBlock { Text = "Above ", FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            line.Children.Add(a);
            line.Children.Add(new TextBlock { Text = " do ", FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            line.Children.Add(b);
            var host = new StackPanel { Children = { line, new Border { Height = 90 } } };
            var w = Shot(host, 341, "valuelink");
            int changed = -1;
            b.Changed += delegate(int i) { changed = i; };
            var p = At(b, w, 8, 8);
            w.MouseMove(p);
            Pump();
            Save(w, "valuelink-hover");
            Check(ReferenceEquals(b.Background, Ui.Pill), "value link: an editable one shows the pill on hover");
            w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left);
            Pump();
            bool open = false;
            foreach (var l in ((Avalonia.LogicalTree.ILogical)b).LogicalChildren) { var pop = l as Avalonia.Controls.Primitives.Popup; if (pop != null) open = pop.IsOpen; }
            Check(open, "value link: a click opens the list");
            Save(w, "valuelink-open");
            p = At(a, w, 8, 8);
            b.Editable = false;
            w.Close();
        }

        static void Flash() {
            var osd = new Osd();
            Scale2(osd);
            osd.Flash("Performance mode", "Performance thermal policy · GPU max", Ui.ModeColor(2), BOLT);
            Pump();
            var box = (Border)osd.Content;
            Check(osd.IsVisible && box.Opacity < 0.01, "osd: shown, starting transparent");
            Run(60);
            Check(box.Opacity > 0.3 && box.Opacity < 0.7, "osd: fading in over 120 ms (" + box.Opacity.ToString("0.00") + " at 60 ms)");
            Run(100);
            Check(box.Opacity == 1, "osd: fully shown");
            var px = new PixelSize((int)Math.Ceiling(box.Bounds.Width * 2), (int)Math.Ceiling(box.Bounds.Height * 2));
            using (var rtb = new RenderTargetBitmap(px, new Vector(192, 192))) { rtb.Render(box); rtb.Save(System.IO.Path.Combine(dir, "osd.png")); }
            Console.WriteLine("      osd size " + box.Bounds.Width + " x " + box.Bounds.Height + ", window " + osd.ClientSize);
            osd.Flash("Max fan", "", Ui.Accent.Color, LEAF);
            Run(200);
            using (var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(box.Bounds.Width * 2), (int)Math.Ceiling(box.Bounds.Height * 2)), new Vector(192, 192))) { rtb.Render(box); rtb.Save(System.IO.Path.Combine(dir, "osd-oneline.png")); }
            // the hide timer is a real 1400 ms one
            using (var cts = new System.Threading.CancellationTokenSource(1500)) Dispatcher.UIThread.MainLoop(cts.Token);
            Run(130);
            Check(box.Opacity > 0.3 && box.Opacity < 0.7 && osd.IsVisible, "osd: fading out after 1.4 s (" + box.Opacity.ToString("0.00") + ")");
            Run(200);
            Check(!osd.IsVisible, "osd: hidden once faded");
            osd.Close();
        }

        static void Accent(ColorSource src, IBrush grad) {
            var seg = new Seg(new[] { "Eco", "Balanced", "Performance" }, null, null, Seg.Kind.Page);
            seg.Select(2, false);
            var link = new LinkSeg(new[] { "Auto", "Max", "Manual" }, 18, 13, 3, null);
            link.Select(1, false);
            var cv = new CurveView { LiveTemp = 55, LiveLevel = 30 };
            var mark = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = grad, HorizontalAlignment = HorizontalAlignment.Left };
            var sp = new StackPanel { Spacing = 10, Children = { mark, seg, new StackPanel { Orientation = Orientation.Horizontal, Children = { link } }, cv } };
            var w = Shot(sp, 350, "accent-0-balanced");
            Ui.GlideColor(src, Ui.ModeColor(2), 320);
            Run(100);
            var mid = Ui.Accent.Color;
            cv.Repaint();
            Save(w, "accent-1-midway");
            Run(300);
            cv.Repaint();
            Save(w, "accent-2-performance");
            Check(mid != Ui.BalColor && mid != Ui.PerfColor && Ui.Accent.Color == Ui.PerfColor, "accent: one glide of the source moves the shared brush (" + mid + " on the way)");
            var stops = ((LinearGradientBrush)grad).GradientStops;
            Check(stops[0].Color == Ui.Mix(Ui.PerfColor, Colors.White, 0.26) && stops[1].Color == Ui.Mix(Ui.PerfColor, Colors.Black, 0.30), "accent: the gradient's stops follow too");
            w.Close();
        }
    }
}
