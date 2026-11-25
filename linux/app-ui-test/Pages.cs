// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the interaction test's Fans, Keyboard and Settings pages.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Ohman {
    static partial class UiTest {
        static bool Same(int[] a, int[] b) { return a != null && b != null && a.SequenceEqual(b); }
        static string Toast { get { return G<Border>("toast").Opacity > 0.01 ? Txt(G<TextBlock>("txtToast")) : ""; } }
        static string StateText { get { try { return File.ReadAllText(AppPaths.StateFile); } catch { return ""; } } }
        /// <summary>What was handed to the system to open or run (run.sh's stand-ins for xdg-open and systemctl).</summary>
        static string Spawned { get { try { return File.ReadAllText(System.IO.Path.Combine(tmp, "spawned.txt")); } catch { return ""; } } }
        static void WindowFollows(string what) {
            double want = Math.Ceiling((double)Call("NaturalHeight"));
            Check(!G<bool>("morphing") && Math.Abs(win.ClientSize.Height - want) < 0.01, what + ": the window is as tall as the page now wants (" + win.ClientSize.Height + ", page " + want + ")");
        }
        static void StaticSize(string file, string what) {
            int w, h;
            if (staticDir.Length == 0 || !PngSize(System.IO.Path.Combine(staticDir, file + ".png"), out w, out h)) return;
            Check(Math.Abs(win.ClientSize.Width * 2 - w) < 0.01 && Math.Abs(win.ClientSize.Height * 2 - h) < 0.01, what + ": the window is the size of the static screenshot " + file + ".png (" + win.ClientSize.Width * 2 + " x " + win.ClientSize.Height * 2 + " px, picture " + w + " x " + h + ")");
        }
        /// <summary>The tray menu's ticks against the engine, after a change made in the window.</summary>
        static void TraySync(string when) {
            if (G<TrayIcon>("tray") == null) return;
            var modes = G<object[]>("trayModes").Cast<NativeMenuItem>().ToList();
            var fans = ((System.Collections.IEnumerable)G<object>("trayFan")).Cast<NativeMenuItem>().ToList();
            bool ok = true;
            for (int i = 0; i < 3; i++) if (modes[i].IsChecked != (i == E.ModeIndex)) ok = false;
            for (int i = 0; i < fans.Count; i++) if (fans[i].IsChecked != (E.S.Fan == Choice.FanModes[i])) ok = false;
            var light = G<object>("trayLight") as NativeMenuItem;
            if (light != null && light.IsChecked != (E.S.Light != 0)) ok = false;
            Check(ok, when + ": the tray menu's ticks agree");
        }
        /// <summary>Click a switch, see the setting change and the switch show it; then put it back.</summary>
        static void Flip(ToggleButton t, string what, Func<bool> get) {
            bool before = get();
            if (!Click(t, what)) return;
            Check(Until(delegate { return get() == !before; }), what + ": the setting becomes " + (!before ? "on" : "off"));
            Settle(250);
            Check(t.IsChecked == !before, what + ": the switch shows " + (!before ? "on" : "off"));
            Click(t, what + " (back)");
            Check(Until(delegate { return get() == before; }) , what + ": and back");
            Settle(250);
            Check(t.IsChecked == before, what + ": the switch is back too");
        }
        static void DragCurve(CurveView cv, int idx, int level, RawInputModifiers extra, string what) {
            Pump();
            double x0 = 30, x1 = cv.Bounds.Width - 7;
            double x = x0 + (cv.Temps[idx] - cv.Temps[0]) / (double)(cv.Temps[cv.Temps.Length - 1] - cv.Temps[0]) * (x1 - x0);
            Func<double, double> y = delegate(double l) { return 20 + 196 * (1 - l / cv.Ceiling); };
            TopLevel top; Point from;
            if (!Aim(cv, new Point(x, y(cv.Levels[idx])), what, out top, out from)) return;
            Point to = cv.TranslatePoint(new Point(x + 25, y(level)), top).Value;      // sideways movement does not matter
            try {
                top.MouseMove(from);
                top.MouseDown(from, MouseButton.Left, extra);
                for (int i = 1; i <= 3; i++) top.MouseMove(new Point(from.X + (to.X - from.X) * i / 3, from.Y + (to.Y - from.Y) * i / 3), L | extra);
                top.MouseUp(to, MouseButton.Left, extra);
                Pump();
            } catch (Exception ex) { Thrown(what, ex); }
        }

        // ---------- fans ----------
        static void Fans() {
            Section("fans");
            Go(1);
            var fanSeg = G<Seg>("fanSeg");
            var cv = G<CurveView>("curveView");
            Control curveBlock = G<Control>("curveBlock"), maxBlock = G<Control>("maxBlock"), manualBlock = G<Control>("manualBlock");
            Control optsAuto = G<Control>("optsAuto"), optsCurve = G<Control>("optsCurve"), optsMax = G<Control>("optsMax"), optsManual = G<Control>("optsManual");
            var action = G<TextBlock>("btnFanAction");
            string[] shots = { "fans", "fan1", "fan3", "fan2" };       // the static pictures of each fan mode (--set Fan=1, 3, 2)
            Action<int> pick = delegate(int i) {
                if (Sel(fanSeg) == i) return;
                if (!Click(Cells(fanSeg)[i], "fans: " + Choice.Fan[i])) return;
                Check(Until(delegate { return E.S.Fan == Choice.FanModes[i]; }), "fans: " + Choice.Fan[i] + " is the engine's fan mode");
                Settle();
            };
            foreach (int i in new[] { 1, 3, 2, 0 }) {
                pick(i);
                Check(curveBlock.IsVisible == (i == 0 || i == 2) && maxBlock.IsVisible == (i == 1) && manualBlock.IsVisible == (i == 3)
                    && optsAuto.IsVisible == (i == 0) && optsCurve.IsVisible == (i == 2) && optsMax.IsVisible == (i == 1) && optsManual.IsVisible == (i == 3), "fans: " + Choice.Fan[i] + " shows its own block and options and no other");
                Check(Sel(fanSeg) == i && Math.Abs(PillX(fanSeg) - Math.Round(Cells(fanSeg)[i].Bounds.X)) < 0.01, "fans: the segment's pill is on " + Choice.Fan[i]);
                WindowFollows("fans: " + Choice.Fan[i]);
                StaticSize(shots[i], "fans: " + Choice.Fan[i]);
                TraySync("fans: " + Choice.Fan[i]);
            }

            // Auto: the vendor curve, read-only, and the way into a curve of one's own
            Check(cv.ReadOnly && Txt(action) == "Edit as curve" && Same(cv.Levels, E.VendorCurveAt(false)), "fans: Auto shows this model's curve, read-only");
            int[] was = (int[])cv.Levels.Clone();
            DragCurve(cv, 3, Math.Min(cv.Ceiling, was[3] + 8), RawInputModifiers.None, "fans: dragging the read-only curve");
            Check(Same(cv.Levels, was), "fans: a read-only curve does not move");
            Flip(G<ToggleButton>("tgEcoCool2"), "fans: Eco cool", delegate { return E.S.EcoCool; });
            Click(action, "fans: Edit as curve");
            Check(Until(delegate { return E.S.Fan == FanMode.Custom; }), "fans: Edit as curve switches to Curve");
            Settle();
            Check(Sel(fanSeg) == 2 && !cv.ReadOnly && Txt(action) == "Reset curve" && Same(cv.Levels, E.S.Cur.CurveLevels) && Same(E.S.Cur.CurveLevels, E.VendorCurveAt(false)), "fans: and the curve starts from the model's own");

            // Curve: drag a point
            was = (int[])cv.Levels.Clone();
            int target = Math.Min(cv.Ceiling, was[3] + 6);
            DragCurve(cv, 3, target, RawInputModifiers.None, "fans: dragging the 60° point");
            Check(cv.Levels[3] == target && cv.Levels[2] == was[2], "fans: the point follows the pointer (" + string.Join(",", was) + " -> " + string.Join(",", cv.Levels) + ")");
            Check(Until(delegate { return Same(E.S.Cur.CurveLevels, cv.Levels); }), "fans: the engine has the dragged curve");
            Settle();
            Check(cv.Levels[3] == target, "fans: the point stays where it was dropped after the refresh");
            Check(Same(Settings.Load().Modes[E.ModeIndex].CurveLevels, cv.Levels), "fans: the curve is in the settings file");
            int[] mid = (int[])cv.Levels.Clone();
            DragCurve(cv, 3, mid[3] - 3, RawInputModifiers.Shift, "fans: shift-dragging");
            Check(cv.Levels[3] == mid[3] - 3 && cv.Levels[5] == mid[5] - 3, "fans: shift-drag moves the whole curve (" + string.Join(",", mid) + " -> " + string.Join(",", cv.Levels) + ")");
            Check(Until(delegate { return Same(E.S.Cur.CurveLevels, cv.Levels); }), "fans: the engine has the shifted curve");
            Settle();

            // one curve or two
            var tgLink = G<ToggleButton>("tgLink");
            var whichHost = G<Border>("curveWhichHost");
            var which = G<LinkSeg>("curveWhich");
            var title = G<TextBlock>("txtCurveTitle");
            Check(tgLink.IsChecked == true && !whichHost.IsVisible && Txt(title) == "CPU curve", "fans: linked, there is one curve and no CPU / GPU switch");
            Click(tgLink, "fans: unlinking the curves");
            Check(Until(delegate { return !E.S.Cur.CurveLinked; }), "fans: the engine's curves are unlinked");
            Settle();
            Check(whichHost.IsVisible && Txt(title) == "Fan curve" && Sel(which) == 0 && Txt(G<TextBlock>("txtCurveHint")) == "shift-drag moves all", "fans: the CPU / GPU switch appears beside the title");
            Click(Links(which)[1], "fans: GPU curve");
            Settle(200);
            Check(G<bool>("curveGpu") && Sel(which) == 1 && Same(cv.Levels, E.S.Cur.GpuCurveLevels), "fans: the GPU curve is shown");
            int[] cpu = (int[])E.S.Cur.CurveLevels.Clone();
            int gt = Math.Min(cv.Ceiling, cv.Levels[4] + 5);
            DragCurve(cv, 4, gt, RawInputModifiers.None, "fans: dragging a GPU point");
            Check(Until(delegate { return E.S.Cur.GpuCurveLevels[4] == gt; }), "fans: the drag is saved to the GPU curve");
            Settle();
            Check(Same(E.S.Cur.CurveLevels, cpu), "fans: and the CPU curve is untouched");
            Click(Links(which)[0], "fans: CPU curve");
            Settle(200);
            Check(!G<bool>("curveGpu") && Same(cv.Levels, cpu), "fans: back on the CPU curve");
            Click(tgLink, "fans: linking the curves");
            Check(Until(delegate { return E.S.Cur.CurveLinked; }), "fans: the engine's curves are linked again");
            Settle();
            Check(!whichHost.IsVisible && Txt(title) == "CPU curve", "fans: the switch is gone again");

            // floor and ramp
            var slFloor = G<Slider>("slFloor"); var txtFloor = G<TextBlock>("txtFloor");
            int floor = E.P.Curve.Floor + 5;
            Drag(slFloor, floor, "fans: floor slider");
            Check(slFloor.Value == floor && Txt(txtFloor) == E.Percent(floor) && cv.UserFloor == floor, "fans: the floor slider's text and the curve's floor line follow (" + slFloor.Value + ", \"" + Txt(txtFloor) + "\")");
            Check(Until(delegate { return E.S.Cur.CurveFloor == floor; }), "fans: the engine has the floor");
            Settle();
            Drag(slFloor, E.P.Curve.Floor, "fans: floor slider to its minimum");
            Check(Txt(txtFloor) == "off" && Until(delegate { return E.S.Cur.CurveFloor == 0; }), "fans: at its minimum the floor is off");
            Settle();
            var slRamp = G<Slider>("slRamp"); var txtRamp = G<TextBlock>("txtRamp");
            int ramp0 = E.S.Cur.CurveRamp;
            Drag(slRamp, 8, "fans: ramp slider");
            Check(slRamp.Value == 8 && Txt(txtRamp) == "8 s" && Until(delegate { return E.S.Cur.CurveRamp == 8; }), "fans: the ramp slider sets 8 s");
            Settle();
            Drag(slRamp, ramp0, "fans: ramp slider back");
            Check(Until(delegate { return E.S.Cur.CurveRamp == ramp0; }), "fans: and back to " + ramp0 + " s");
            Settle();
            Click(action, "fans: Reset curve");
            Check(Until(delegate { return Same(E.S.Cur.CurveLevels, E.VendorCurveAt(false)); }), "fans: Reset curve puts the model's curve back");
            Settle();
            Check(Same(cv.Levels, E.VendorCurveAt(false)), "fans: and the drawing shows it");

            // Max
            pick(1);
            Check(Txt(G<TextBlock>("txtFanApplied")).StartsWith("Loud") && G<TextBlock>("txtFanRight").IsVisible && !action.IsVisible, "fans: Max says so in the footer (\"" + Txt(G<TextBlock>("txtFanApplied")) + "\")");
            Flip(G<ToggleButton>("tgMaxCool"), "fans: back when cool", delegate { return E.S.MaxBackWhenCool; });
            var stop = G<Seg>("stopAfterSeg");
            int[] mins = { 15, 30, 60, 0 };
            foreach (int i in new[] { 0, 2, 1, 3 }) {
                Click(Cells(stop)[i], "fans: stop after " + ((TextBlock)((Border)Cells(stop)[i]).Child).Text);
                Check(Until(delegate { return E.S.MaxStopAfterMin == mins[i]; }), "fans: max fan stops after " + mins[i] + " min in the engine");
                Settle();
                Check(Sel(stop) == i, "fans: the timer segment shows it");
                Check(Txt(G<TextBlock>("maxMinsSub")) == (mins[i] > 0 ? "Until it stops" : "Running at max"), "fans: the fourth tile reads \"" + Txt(G<TextBlock>("maxMinsSub")) + "\" (" + G<Avalonia.Controls.Documents.Run>("maxMins").Text + " min)");
            }

            // Manual
            pick(3);
            Slider sl1 = G<Slider>("slFan1"), sl2 = G<Slider>("slFan2");
            var tgMan = G<ToggleButton>("tgManualLink");
            Check(tgMan.IsChecked == true, "fans: the manual sliders start linked");
            int a = Math.Min(E.P.Curve.Ceiling, 30);
            Drag(sl1, a, "fans: CPU fan slider");
            Check(sl1.Value == a && sl2.Value == a && Txt(G<TextBlock>("txtFan1")) == E.Percent(a) && Txt(G<TextBlock>("txtFan2")) == E.Percent(a), "fans: linked, both sliders and both texts follow (" + sl1.Value + ", " + sl2.Value + ")");
            Check(G<Avalonia.Controls.Documents.Run>("manPct1").Text == E.Percent(a).TrimEnd('%') && Txt(G<TextBlock>("manSub1")) == E.Rpm(a) + " · CPU fan", "fans: the big readout follows");
            Check(Until(delegate { return E.S.Fan1 == a && E.S.Fan2 == a; }), "fans: the engine has both levels");
            Settle();
            Click(tgMan, "fans: unlinking the sliders");
            Check(Until(delegate { return !E.S.ManualLinked; }), "fans: the sliders are unlinked in the engine");
            Settle();
            int b = Math.Min(E.P.Curve.Ceiling, 42);
            Drag(sl2, b, "fans: GPU fan slider");
            Check(sl2.Value == b && sl1.Value == a, "fans: unlinked, only the GPU slider moves");
            Check(Until(delegate { return E.S.Fan1 == a && E.S.Fan2 == b; }), "fans: the engine has the two levels");
            Settle();
            Click(tgMan, "fans: linking the sliders");
            Check(Until(delegate { return E.S.ManualLinked; }), "fans: linked again");
            Settle();
            pick(0);
            TraySync("fans: at the end");
        }

        // ---------- keyboard ----------
        static bool Eq(Rgb a, Rgb b) { return a.R == b.R && a.G == b.G && a.B == b.B; }
        static Point KeyAt(KeyboardView kv, string label) {
            KeyDef k = kv.Keys.First(x => x.Label == label);
            double u = (double)typeof(KeyboardView).GetProperty("Unit", BF).GetValue(kv);
            return new Point(k.X * u + (k.W * u - kv.Gap) / 2, k.Y * kv.RowPitch + (k.H * kv.RowPitch - kv.Gap) / 2);
        }
        static void Keyboard() {
            Section("keyboard");
            if (E.Light == null) { Note("this simulated board has no keyboard lighting"); return; }
            Go(2);
            var modes = G<LinkSeg>("kbdModes");
            var big = G<KeyboardView>("kbdBig");
            var hw = E.Light;
            var status = G<TextBlock>("txtKbdStatus");
            Control selectRow = G<Control>("selectRow"), colorEditor = G<Control>("colorEditor"), effectEditor = G<Control>("effectEditor"), kbdInfo = G<Control>("kbdInfo"), levelInline = G<Control>("levelInline");
            Rgb[] orig = (Rgb[])E.LightColors.Clone();
            int level0 = E.S.LightLevel, speed0 = E.S.LightSpeed;
            Check(Links(modes).Count == 5, "keyboard: five modes, no Windows lighting choice here (" + Links(modes).Count + ")");
            Action<int> mode = delegate(int i) {
                if (!Click(Links(modes)[i], "keyboard: " + Choice.Light[i])) return;
                Check(Until(delegate { return E.S.Light == Choice.LightMode(i) && E.S.LightEffect == Choice.LightEffect(i); }), "keyboard: " + Choice.Light[i] + " is the engine's mode");
                Settle();
                bool lit = i != 0, pick = i == 1 || i == 2, effect = i >= 2;
                Check(Sel(modes) == i && selectRow.IsVisible == pick && colorEditor.IsVisible == pick && effectEditor.IsVisible == effect && levelInline.IsVisible == (pick && !effect) && kbdInfo.IsVisible == !lit,
                    "keyboard: " + Choice.Light[i] + " shows the editors that mode uses");
                Check(((hw.GetBacklight() & 0x80) != 0) == lit, "keyboard: the simulated keyboard's backlight is " + (lit ? "on" : "off"));
                WindowFollows("keyboard: " + Choice.Light[i]);
                TraySync("keyboard: " + Choice.Light[i]);
            };
            mode(0);
            Check(Txt(status) == "backlight off" && big.Off, "keyboard: Off says so and the drawing goes dark (\"" + Txt(status) + "\")");
            StaticSize("kbd-off", "keyboard: Off");
            mode(1);
            Check(Txt(status) == hw.Describe + " · static", "keyboard: Static's status (\"" + Txt(status) + "\")");
            StaticSize("keyboard", "keyboard: Static");

            // what a click takes
            var gran = G<Seg>("granSeg");
            var sel = G<TextBlock>("txtKeySel");
            Click(Cells(gran)[0], "keyboard: Key (not on a four-zone board)");
            Check(G<string>("gran") == "Zone" && Sel(gran) == 2, "keyboard: Key is not offered on a keyboard that lights in zones");
            ClickAt(big, KeyAt(big, "W"), "keyboard: the W key");
            int zw = big.Keys.First(x => x.Label == "W").Zone;
            Check(big.Selected.Count == big.Keys.Count(x => x.Zone == zw) && Txt(sel) == "wasd", "keyboard: a click takes the key's zone (" + big.Selected.Count + " keys, \"" + Txt(sel) + "\")");
            Check(string.Equals(G<TextBox>("txtHex").Text, E.LightColors[zw].Hex, StringComparison.OrdinalIgnoreCase), "keyboard: the hex field shows that zone's colour (" + G<TextBox>("txtHex").Text + ")");
            string other = big.Keys.First(x => x.Zone != zw && x.Label == "P").Label;
            int zp = big.Keys.First(x => x.Label == other).Zone;
            ClickAt(big, KeyAt(big, other), "keyboard: ctrl-click on " + other, RawInputModifiers.Control);
            Check(big.Selected.Count == big.Keys.Count(x => x.Zone == zw || x.Zone == zp) && Txt(sel).Contains(" + "), "keyboard: ctrl-click adds a zone (\"" + Txt(sel) + "\")");
            ClickAt(big, KeyAt(big, other), "keyboard: click on " + other);
            Check(big.Selected.Count == big.Keys.Count(x => x.Zone == zp), "keyboard: a plain click replaces the selection (\"" + Txt(sel) + "\")");
            Click(Cells(gran)[3], "keyboard: All");
            Check(G<string>("gran") == "All" && big.Selected.Count == 0 && Txt(sel) == "Click the map to select", "keyboard: changing what a click takes clears the selection");
            ClickAt(big, KeyAt(big, "W"), "keyboard: the W key, All");
            Check(big.Selected.Count == big.Keys.Count && Txt(sel) == "whole keyboard", "keyboard: All takes the whole board");
            Click(Cells(gran)[2], "keyboard: Zone");
            ClickAt(big, KeyAt(big, "W"), "keyboard: the W key again");

            // colour: the hue strip, the shade strip, the field
            var hue = G<StripPicker>("hueBar"); var shade = G<StripPicker>("shadeBar");
            var hex = G<TextBox>("txtHex"); var chip = G<Border>("hexChip");
            Rgb c = hue.ColorAt(12);
            ClickAt(hue, new Point((12 + 0.5) * hue.Bounds.Width / hue.Cells, 10), "keyboard: a hue");
            Check(Eq(E.LightColors[zw], c) && string.Equals(hex.Text, c.Hex, StringComparison.OrdinalIgnoreCase) && ((ISolidColorBrush)chip.Background).Color == Color.FromRgb(c.R, c.G, c.B), "keyboard: the hue goes to the zone, the field and the swatch at once (" + hex.Text + ")");
            Check(Until(delegate { return Eq(hw.GetColors()[zw], c); }), "keyboard: the simulated keyboard received the hue");
            Check(!Eq(hw.GetColors()[zp], c), "keyboard: and the other zones did not");
            Settle();
            Rgb c2 = shade.ColorAt(22);
            ClickAt(shade, new Point((22 + 0.5) * shade.Bounds.Width / shade.Cells, 10), "keyboard: a shade");
            Check(Eq(E.LightColors[zw], c2) && string.Equals(hex.Text, c2.Hex, StringComparison.OrdinalIgnoreCase), "keyboard: the shade goes to the zone and the field (" + hex.Text + ")");
            Check(Until(delegate { return Eq(hw.GetColors()[zw], c2); }), "keyboard: the simulated keyboard received the shade");
            Settle();
            Click(hex, "keyboard: the hex field");
            Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
            try { win.KeyTextInput("ff8800"); Pump(); } catch (Exception ex) { Thrown("keyboard: typing a colour", ex); }
            Rgb typed; Rgb.TryParse("FF8800", out typed);
            Check(hex.Text == "ff8800" && Eq(E.LightColors[zw], typed), "keyboard: a typed colour is taken as it is typed (field \"" + hex.Text + "\", zone " + E.LightColors[zw].Hex + ")");
            Check(Until(delegate { return Eq(hw.GetColors()[zw], typed); }), "keyboard: the simulated keyboard received the typed colour");
            Check(E.S.LightColors.IndexOf("FF8800", StringComparison.OrdinalIgnoreCase) >= 0 && StateText.IndexOf("FF8800", StringComparison.OrdinalIgnoreCase) >= 0, "keyboard: and it is in the settings file");
            Settle();
            Check(hex.Text == "ff8800", "keyboard: the field keeps what was typed after the refresh (\"" + hex.Text + "\")");
            ClickAt(big, KeyAt(big, "W"), "keyboard: the map, to leave the field");

            // brightness
            var slLevel = G<Slider>("slLevel");
            Drag(slLevel, 50, "keyboard: brightness");
            Check(slLevel.Value == 50 && Txt(G<TextBlock>("txtLevel")) == "50%" && Math.Abs(big.Level - 0.5) < 0.001, "keyboard: the brightness slider, its text and the drawing follow (" + slLevel.Value + ")");
            Check(Until(delegate { return E.S.LightLevel == 50; }), "keyboard: the engine has 50 %");
            Check(Until(delegate { var g = hw.GetColors()[zw]; return Math.Abs(g.R - typed.R / 2.0) <= 2 && Math.Abs(g.G - typed.G / 2.0) <= 2; }), "keyboard: the simulated keyboard's colours are dimmed (" + hw.GetColors()[zw].Hex + ")");
            Settle();
            Drag(slLevel, 100, "keyboard: brightness back");
            Check(Until(delegate { return E.S.LightLevel == 100 && Eq(hw.GetColors()[zw], typed); }), "keyboard: and back to full");
            Settle();

            // the effects
            mode(2);
            Check(Txt(status) == hw.Describe + " · breathe", "keyboard: Breathe's status (\"" + Txt(status) + "\")");
            StaticSize("kbd-breathe", "keyboard: Breathe");
            var slSpeed = G<Slider>("slSpeed");
            Drag(slSpeed, 5, "keyboard: speed");
            Check(slSpeed.Value == 5 && Txt(G<TextBlock>("txtSpeed")) == "5" && Until(delegate { return E.S.LightSpeed == 5; }), "keyboard: the speed slider sets 5");
            Settle();
            var slLevel2 = G<Slider>("slLevel2");
            Drag(slLevel2, 60, "keyboard: the effect's brightness");
            Check(slLevel2.Value == 60 && slLevel.Value == 60 && Txt(G<TextBlock>("txtLevel2")) == "60%" && Until(delegate { return E.S.LightLevel == 60; }), "keyboard: the effect's brightness slider sets 60 % and the other copy follows");
            Settle();
            Rgb f0 = hw.GetColors()[zw];
            Check(Until(delegate { return !Eq(hw.GetColors()[zw], f0); }, 3000), "keyboard: Breathe keeps writing frames to the simulated keyboard");
            Drag(slLevel2, 100, "keyboard: the effect's brightness back");
            Until(delegate { return E.S.LightLevel == 100; });
            Drag(slSpeed, speed0, "keyboard: speed back");
            Until(delegate { return E.S.LightSpeed == speed0; });
            Settle();
            mode(3);
            Check(Txt(status) == hw.Describe + " · cycle · own colours", "keyboard: Cycle's status (\"" + Txt(status) + "\")");
            f0 = hw.GetColors()[zw];
            Check(Until(delegate { return !Eq(hw.GetColors()[zw], f0); }, 3000), "keyboard: Cycle keeps writing frames");
            mode(4);
            Check(Txt(status) == hw.Describe + " · wave · own colours", "keyboard: Wave's status (\"" + Txt(status) + "\")");
            StaticSize("kbd-wave", "keyboard: Wave");
            f0 = hw.GetColors()[zw];
            Check(Until(delegate { return !Eq(hw.GetColors()[zw], f0); }, 3000), "keyboard: Wave keeps writing frames");
            mode(1);
            Check(Until(delegate { return Eq(hw.GetColors()[zw], typed); }), "keyboard: back in Static the zone has its colour again");

            // Home's row follows
            Go(0);
            Check(Txt(G<TextBlock>("txtLightSub")) == "Static · " + hw.Describe, "keyboard: Home's Lighting row says \"" + Txt(G<TextBlock>("txtLightSub")) + "\"");
            // as it was, for the pictures at the end
            for (int z = 0; z < orig.Length; z++) { int zz = z; Rgb col = orig[z]; E.Post(delegate { E.SetLightColor(new[] { zz }, col); }); }
            Until(delegate { return Eq(E.LightColors[zw], orig[zw]) && Eq(hw.GetColors()[zw], orig[zw]); });
            Settle();
        }

        // ---------- settings ----------
        static IList<Control> PopupItems(ValueLink v) {
            var popup = Priv<Popup>(v, "popup");
            if (!popup.IsOpen || popup.Child == null) return new List<Control>();
            var list = ((ScrollViewer)((Border)popup.Child).Child).Content as Panel;
            return list.Children;
        }
        /// <summary>Open a value's list and click another entry of it.</summary>
        static void Choose(ValueLink v, int index, string what) {
            if (!Click(v, what)) return;
            Pump(); Pump();
            var items = PopupItems(v);
            if (!Check(items.Count == v.Items.Length && items.Count > index, what + ": the list opens with every choice (" + items.Count + ")")) return;
            Click(items[index], what + ": \"" + v.Items[index] + "\"");
            Pump();
            Check(!Priv<Popup>(v, "popup").IsOpen && v.Index == index, what + ": the list closes and the word is \"" + v.Items[v.Index] + "\"");
        }
        static void SettingsPage() {
            Section("settings");
            Go(3);
            var scroll = G<ScrollViewer>("scroll");

            // the wheel eases
            Point over = scroll.TranslatePoint(new Point(100, 200), win).Value;
            try { win.MouseMove(over); win.MouseWheel(over, new Vector(0, -2)); } catch (Exception ex) { Thrown("settings: wheel", ex); }
            Frames(50);
            double part = scroll.Offset.Y;
            Frames(800);
            Check(part > 1 && part < 108 && Math.Abs(scroll.Offset.Y - 108) < 0.01, "settings: the wheel scrolls 54 px a notch, eased (" + part.ToString("0.#") + " on the way to " + scroll.Offset.Y.ToString("0.#") + ")");

            // OMEN key
            var keySeg = G<Seg>("keySeg");
            var cmdRow = G<Control>("keyCmdRow");
            KeyAction key0 = E.S.Key;
            foreach (int i in new[] { 0, 2, 4, 3 }) {
                Click(Cells(keySeg)[i], "settings: OMEN key " + Choice.Key[i]);
                Check(Until(delegate { return E.S.Key == Choice.KeyActions[i]; }), "settings: the OMEN key action is " + Choice.KeyActions[i]);
                Settle(250);
                Check(Sel(keySeg) == i && cmdRow.IsVisible == (i == 3), "settings: the segment shows it" + (i == 3 ? ", and the command field appears" : ""));
            }
            var cmd = G<TextBox>("txtKeyCmd");
            Click(cmd, "settings: the command field");
            try { win.KeyTextInput("xdg-open omen-key-test"); Pump(); } catch (Exception ex) { Thrown("settings: typing a command", ex); }
            Check(!G<TextBlock>("txtKeyCmdHint").IsVisible, "settings: the hint under the command goes once something is typed");
            Key(Avalonia.Input.Key.Enter, RawInputModifiers.None);
            Check(Until(delegate { return E.S.KeyCommand == "xdg-open omen-key-test"; }), "settings: Enter stores the command (\"" + E.S.KeyCommand + "\")");
            Frames(200);
            Check(Toast == "OMEN key runs: xdg-open omen-key-test", "settings: and says so (\"" + Toast + "\")");
            Wait(2000); Frames(400);
            Check(G<Border>("toast").Opacity == 0, "settings: the toast fades away after two seconds");
            try { win.KeyTextInput(" --more"); Pump(); } catch (Exception ex) { Thrown("settings: typing more", ex); }
            Click(Cells(keySeg)[0], "settings: somewhere else, to leave the field");
            Check(Until(delegate { return E.S.KeyCommand == "xdg-open omen-key-test --more"; }), "settings: a command is stored when the field loses the keyboard too (\"" + E.S.KeyCommand + "\")");
            Click(Cells(keySeg)[3], "settings: OMEN key Run again");
            Until(delegate { return E.S.Key == KeyAction.Run; });
            Settle(250);
            Click(cmd, "settings: the command field again");
            Check(win.IsVisible, "settings: Enter in the field does not hide the window");
            Key(Avalonia.Input.Key.Escape, RawInputModifiers.None);
            Check(win.IsVisible, "settings: nor does Escape while the field has the keyboard");
            var learn = G<TextBlock>("btnLearn"); var keyInfo = G<TextBlock>("txtKeyInfo");
            string info0 = Txt(keyInfo);
            Click(learn, "settings: Relearn");
            Check(E.Learning && Txt(keyInfo) == "press the OMEN key now… (10 s)", "settings: " + Txt(learn) + " waits for the OMEN key (\"" + Txt(keyInfo) + "\")");
            Check(Until(delegate { progress = DateTime.UtcNow; return !E.Learning; }, 13000) && Txt(keyInfo) == info0, "settings: and gives up after ten seconds, back to \"" + info0 + "\"");
            Check(!X<Control>("SuppressRow").IsVisible, "settings: no \"Take over the OMEN key\" row here (there is no vendor app)");
            WindowFollows("settings");

            // hotkeys
            var tgHot = G<ToggleButton>("tgHotkeys");
            Flip(tgHot, "settings: hotkeys", delegate { return E.S.Hotkeys; });
            var sub = G<TextBlock>("txtHotkeysSub");
            Check(Txt(sub).StartsWith("Not in this session: there is no X display"), "settings: with no display the hotkeys line says why they are off (\"" + Txt(sub) + "\")");
            var btnHot = G<Button>("btnHotkeys");
            var panel = G<StackPanel>("hotkeyPanel");
            Click(btnHot, "settings: the hotkeys pencil");
            Settle(200);
            Check(G<bool>("hotkeysOpen") && panel.IsVisible && !sub.IsVisible, "settings: the pencil opens the shortcut list in place of the line");
            Func<Grid> grid = delegate { return (Grid)panel.Children[0]; };
            Func<int, Control> cell = delegate(int i) { return grid().Children[2 * i + 1]; };
            Func<int, string> cap = delegate(int i) { return Txt(All<TextBlock>(cell(i)).First()); };
            Check(grid().Children.Count == 2 * HotkeyTable.Count && cap(0) == HotkeyTable.Defaults[0].ToString() && cap(4) == HotkeyTable.Defaults[4].ToString(), "settings: six rows, each with its shortcut (" + cap(0) + " … " + cap(4) + ")");
            Click(cell(0), "settings: the Eco shortcut");
            Check(G<int>("listening") == 0 && cap(0) == "Press keys…", "settings: clicking a shortcut starts listening (\"" + cap(0) + "\")");
            Frames(300);
            Check(All<Border>(cell(0)).First().Opacity < 0.99, "settings: the cap pulses while it listens");
            Key(Avalonia.Input.Key.LeftCtrl, RawInputModifiers.None);
            Check(cap(0) == "Ctrl+…", "settings: holding Ctrl shows in the cap (\"" + cap(0) + "\")");
            Key(Avalonia.Input.Key.LeftAlt, RawInputModifiers.Control);
            Check(cap(0) == "Ctrl+Alt+…", "settings: and Alt with it (\"" + cap(0) + "\")");
            KeyUp(Avalonia.Input.Key.LeftAlt, RawInputModifiers.Control | RawInputModifiers.Alt);
            Check(cap(0) == "Ctrl+…", "settings: letting Alt go takes it off the cap (\"" + cap(0) + "\")");
            Key(Avalonia.Input.Key.LeftAlt, RawInputModifiers.Control);
            Key(Avalonia.Input.Key.K, RawInputModifiers.Control | RawInputModifiers.Alt);
            KeyUp(Avalonia.Input.Key.K, RawInputModifiers.Control | RawInputModifiers.Alt);
            KeyUp(Avalonia.Input.Key.LeftAlt, RawInputModifiers.Control | RawInputModifiers.Alt);
            KeyUp(Avalonia.Input.Key.LeftCtrl, RawInputModifiers.Control);
            Check(G<int>("listening") == -1 && E.GetHotkey(HotkeyAction.Eco).ToString() == "Ctrl+Alt+K" && cap(0) == "Ctrl+Alt+K", "settings: Ctrl+Alt+K is stored and shown (engine " + E.GetHotkey(HotkeyAction.Eco) + ", cap \"" + cap(0) + "\")");
            Check(StateText.Contains("Ctrl+Alt+K"), "settings: and it is in the settings file");
            Check(grid().Children.Count == 2 * HotkeyTable.Count + 1 && Find(panel, "Reset all") != null, "settings: \"Reset all\" appears once a shortcut is not the default");
            // a key another action has: it moves
            Click(cell(1), "settings: the Balanced shortcut");
            Key(Avalonia.Input.Key.K, RawInputModifiers.Control | RawInputModifiers.Alt);
            Check(E.GetHotkey(HotkeyAction.Balanced).ToString() == "Ctrl+Alt+K" && E.GetHotkey(HotkeyAction.Eco).IsEmpty && cap(0) == "none" && cap(1) == "Ctrl+Alt+K", "settings: a key already bound moves to the new action and leaves the old one empty (Eco \"" + cap(0) + "\", Balanced \"" + cap(1) + "\")");
            // a bare letter is not a shortcut
            Click(cell(2), "settings: the Performance shortcut");
            Key(Avalonia.Input.Key.J, RawInputModifiers.None);
            Frames(200);
            Check(G<int>("listening") == 2 && Toast.StartsWith("Hold Ctrl, Alt, Shift or Win"), "settings: a letter on its own is refused and it keeps listening (\"" + Toast + "\")");
            Key(Avalonia.Input.Key.Escape, RawInputModifiers.None);
            Check(G<int>("listening") == -1 && E.GetHotkey(HotkeyAction.Performance).Same(HotkeyTable.Defaults[2]) && win.IsVisible, "settings: Escape keeps the old shortcut, and does not hide the window");
            Click(cell(3), "settings: the Max fan shortcut");
            Key(Avalonia.Input.Key.Back, RawInputModifiers.None);
            Check(G<int>("listening") == -1 && E.GetHotkey(HotkeyAction.MaxFan).IsEmpty && cap(3) == "none", "settings: Backspace removes a shortcut");
            Click(cell(4), "settings: the Cycle shortcut");
            Key(Avalonia.Input.Key.F9, RawInputModifiers.None);
            Check(E.GetHotkey(HotkeyAction.Cycle).ToString() == "F9" && cap(4) == "F9", "settings: an F-key needs no modifier (\"" + cap(4) + "\")");
            // another window taking the keyboard ends it
            Click(cell(5), "settings: the Benchmark shortcut");
            Do("settings: the window loses the keyboard", delegate { var d = win.PlatformImpl.GetType().GetProperty("Deactivated").GetValue(win.PlatformImpl) as Action; if (d != null) d(); });
            Check(G<int>("listening") == -1, "settings: the window losing the keyboard stops listening");
            // navigating away while listening ends it
            Click(cell(5), "settings: the Benchmark shortcut");
            Check(G<int>("listening") == 5, "settings: listening for the Benchmark shortcut");
            Go(0);
            Check(G<int>("listening") == -1, "settings: leaving the page stops listening");
            Go(3);
            Check(panel.IsVisible, "settings: the list is still open on return");
            Click(Find(panel, "Reset all"), "settings: Reset all");
            Settle(200);
            bool defaults = true;
            for (int i = 0; i < HotkeyTable.Count; i++) if (!E.GetHotkey((HotkeyAction)i).Same(HotkeyTable.Defaults[i])) defaults = false;
            Check(defaults && !E.HotkeysCustomised && cap(0) == HotkeyTable.Defaults[0].ToString() && grid().Children.Count == 2 * HotkeyTable.Count, "settings: Reset all puts every default back and goes away");
            Click(btnHot, "settings: the hotkeys tick");
            Settle(200);
            Check(!G<bool>("hotkeysOpen") && !panel.IsVisible && sub.IsVisible, "settings: the tick closes the list");

            // power
            var gpuSeg = G<Seg>("gpuSeg");
            if (!G<Control>("gpuRow").IsVisible) Note("no GPU power row on this simulated board");
            else {
                foreach (int i in new[] { 0, 2, 1, 3 }) {
                    Click(Cells(gpuSeg)[i], "settings: GPU power " + i);
                    Check(Until(delegate { return i == 3 ? E.S.GpuAuto : (!E.S.GpuAuto && (int)E.S.Gpu == i); }), "settings: GPU power is " + (i == 3 ? "Auto" : ((GpuLevel)i).ToString()) + " in the engine");
                    Settle(250);
                    Check(Sel(gpuSeg) == i && Txt(G<TextBlock>("txtGpuSub")) == (i == 3 ? "Follows the mode" : i == 2 ? "Custom TGP + PPAB" : i == 1 ? "PPAB" : "Base TGP"), "settings: the row says \"" + Txt(G<TextBlock>("txtGpuSub")) + "\"");
                }
            }
            Flip(G<ToggleButton>("tgEcoBattery"), "settings: Eco on battery", delegate { return E.S.EcoOnBattery; });
            if (X<Control>("SyncPowerRow").IsVisible) Flip(G<ToggleButton>("tgSyncPower"), "settings: sync the power profile", delegate { return E.S.SyncWinPower; });
            else Check(Host.Find("powerprofilesctl") == null, "settings: no power-profile row without power-profiles-daemon");

            // thermal guard
            var tgGuard = G<ToggleButton>("tgGuard");
            Check(E.S.Guard && tgGuard.IsChecked == true, "settings: the thermal guard starts on");
            int seen = dialogs;
            Answer("No");
            Click(tgGuard, "settings: thermal guard off, answering No");
            Settle(250);
            Check(dialogs == seen + 1 && lastDialog.StartsWith("Turn the thermal guard off?"), "settings: switching the guard off asks first");
            Check(E.S.Guard && tgGuard.IsChecked == true, "settings: No leaves the guard on and the switch back on");
            Answer("Yes");
            Click(tgGuard, "settings: thermal guard off, answering Yes");
            Check(Until(delegate { return !E.S.Guard; }), "settings: Yes switches the guard off");
            Settle(250);
            Check(dialogs == seen + 2 && tgGuard.IsChecked == false, "settings: and the switch shows off");
            Click(tgGuard, "settings: thermal guard on");
            Check(Until(delegate { return E.S.Guard; }) && dialogs == seen + 2, "settings: switching it on asks nothing");
            Settle(250);

            // its rule
            var btnGuard = G<Button>("btnGuard");
            ValueLink chipCpu = G<ValueLink>("chipCpu"), chipFans = G<ValueLink>("chipFans"), chipHold = G<ValueLink>("chipHold"), chipCh = G<ValueLink>("chipChassis");
            Click(chipCpu, "settings: a guard value before the pencil");
            Check(!Priv<Popup>(chipCpu, "popup").IsOpen, "settings: the rule's values are plain words until the pencil is pressed");
            Click(btnGuard, "settings: the guard's pencil");
            Check(G<bool>("guardOpen") && chipCpu.Editable && chipHold.Editable, "settings: the pencil makes the values editable");
            int cpu0 = E.GuardCpuHot, ch0 = E.GuardChassisHot, lvl0 = E.GuardLevel, hold0 = E.GuardHoldSeconds;
            int iCpu = chipCpu.Index, iCh = chipCh.Index, iFans = chipFans.Index, iHold = chipHold.Index;
            Choose(chipCpu, iCpu - 2, "settings: guard CPU limit");
            Check(Until(delegate { return E.GuardCpuHot == cpu0 - 2; }), "settings: the guard's CPU limit is " + (cpu0 - 2) + "° in the engine");
            Settle(250);
            Check(Txt(G<TextBlock>("txtGuardNote")).EndsWith("above " + (cpu0 - 2) + "° CPU") && Txt(G<TextBlock>("txtGuardRelease")).Contains("under " + E.GuardCpuSafe + "°"), "settings: the sentences that quote the limit follow (\"" + Txt(G<TextBlock>("txtGuardRelease")) + "\")");
            Choose(chipCh, iCh + 1, "settings: guard chassis limit");
            Check(Until(delegate { return E.GuardChassisHot == ch0 + 1; }), "settings: the guard's chassis limit is " + (ch0 + 1) + "°");
            Choose(chipFans, iFans == 0 ? 1 : 0, "settings: guard fan level");
            int[] levels = G<int[]>("guardLevels");
            Check(Until(delegate { return E.GuardLevel == levels[iFans == 0 ? 1 : 0]; }), "settings: the guard's fan level is " + levels[iFans == 0 ? 1 : 0]);
            Choose(chipHold, iHold == 0 ? 1 : 0, "settings: guard hold time");
            int[] holds = G<int[]>("holdChoices");
            Check(Until(delegate { return E.GuardHoldSeconds == holds[iHold == 0 ? 1 : 0]; }), "settings: the guard holds for " + holds[iHold == 0 ? 1 : 0] + " s");
            Settle(250);
            Check(StateText.Contains("GuardCpu=" + (cpu0 - 2)), "settings: the rule is in the settings file");
            Choose(chipCpu, iCpu, "settings: guard CPU limit back");
            Choose(chipCh, iCh, "settings: guard chassis limit back");
            Choose(chipFans, iFans, "settings: guard fan level back");
            Choose(chipHold, iHold, "settings: guard hold time back");
            Check(Until(delegate { return E.GuardCpuHot == cpu0 && E.GuardChassisHot == ch0 && E.GuardLevel == lvl0 && E.GuardHoldSeconds == hold0; }), "settings: the rule is as it was");
            Settle(250);
            Click(btnGuard, "settings: the guard's tick");
            Check(!G<bool>("guardOpen") && !chipCpu.Editable, "settings: the tick makes them words again");

            // display
            var gfxSeg = G<Seg>("gfxSeg");
            if (gfxSeg == null || !G<Control>("gfxRow").IsVisible) Note("no graphics mode row on this simulated board");
            else {
                int cur = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
                int curIdx = Sel(gfxSeg), otherIdx = curIdx == 0 ? 1 : 0, otherMode = (int)gfxSeg.Tags[otherIdx];
                seen = dialogs;
                Answer("Cancel");
                Click(Cells(gfxSeg)[otherIdx], "settings: graphics mode, answering Cancel");
                Settle(250);
                Check(dialogs == seen + 1 && lastDialog.StartsWith("Switch graphics to " + Engine.GpuModeNames[otherMode] + "?"), "settings: a graphics mode asks about the restart");
                Check((E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode) == cur && Sel(gfxSeg) == curIdx, "settings: Cancel writes nothing and the segment goes back");
                Answer("No");
                Click(Cells(gfxSeg)[otherIdx], "settings: graphics mode, answering No");
                Settle(250);
                Check(dialogs == seen + 2 && E.GpuModePending == otherMode && Sel(gfxSeg) == otherIdx, "settings: No writes the mode and does not restart");
                Check(Txt(G<TextBlock>("txtGfxSub")) == Engine.GpuModeNames[otherMode] + " after the next restart", "settings: the row says \"" + Txt(G<TextBlock>("txtGfxSub")) + "\"");
                Check(!Spawned.Contains("systemctl"), "settings: nothing asked the system to restart");
                Answer("No");
                Click(Cells(gfxSeg)[curIdx], "settings: graphics mode back, answering No");
                Settle(250);
                Check(E.GpuModePending == cur && Sel(gfxSeg) == curIdx && !Spawned.Contains("systemctl"), "settings: and back to " + Engine.GpuModeNames[cur]);
            }
            if (G<Control>("hzRow").IsVisible) Note("a refresh-rate row is shown (not expected without a display); not driven");
            else Check(!G<Control>("lowHzRow").IsVisible && Display.Choices().Length < 2, "settings: no refresh-rate rows without a display to ask");

            // driver
            var tgDriver = G<ToggleButton>("tgDriver");
            var driverSub = G<TextBlock>("txtDriverSub");
            Check(Txt(G<TextBlock>("txtDriverTitle")) == "Hardware driver" && Txt(driverSub) == Host.DriverName + " · simulated" && !G<TextBlock>("btnDriver").IsVisible, "settings: the driver row on simulated hardware (\"" + Txt(driverSub) + "\")");
            // the driver's name is a link to its source
            TopLevel top; Point at;
            if (Aim(driverSub, new Point(12, driverSub.Bounds.Height / 2), "settings: the driver's name", out top, out at)) {
                try { top.MouseMove(at); Pump(); } catch (Exception ex) { Thrown("settings: hovering the driver's name", ex); }
                Check(G<bool>("driverLinkHot"), "settings: the driver's name is a link under the pointer");
                string before = Spawned;
                ClickAt(driverSub, new Point(12, driverSub.Bounds.Height / 2), "settings: the driver's name");
                Check(Until(delegate { return Spawned.Length > before.Length; }) && Spawned.Substring(before.Length).Contains(PawnIo.SourceUrl), "settings: clicking it opens its source (" + Spawned.Substring(before.Length).Trim() + ")");
                try { top.MouseMove(new Point(at.X + 250, at.Y)); Pump(); } catch { }
                Check(!G<bool>("driverLinkHot"), "settings: and past the name it is not");
            }

            if (tgDriver.IsVisible) {
                Click(tgDriver, "settings: driver switch");
                Check(Until(delegate { return !E.S.DriverUse; }), "settings: the driver switch reaches the engine");
                Settle(250);
                Check(Txt(driverSub).StartsWith("Off · adds "), "settings: and the row says it is off (\"" + Txt(driverSub) + "\")");
                // On simulated hardware the switch is drawn on whatever the setting says (the same line in the Windows
                // window), so a second click sends "off" again: the setting is put back from here.
                E.Post(delegate { E.SetDriverUse(true); });
                Until(delegate { return E.S.DriverUse; });
                Settle(250);
                Check(Txt(driverSub) == Host.DriverName + " · simulated", "settings: with the setting back the row names the driver again");
            }
            // app
            Flip(G<ToggleButton>("tgTrayTemp"), "settings: temperature on the tray icon", delegate { return E.S.TrayTemp; });
            if (X<Control>("BenchPillRow").IsVisible) Flip(G<ToggleButton>("tgBenchPill"), "settings: benchmark overlay", delegate { return E.S.BenchPill; });
            else Check(!Host.HasFrameCapture, "settings: no benchmark overlay row without a frame capture tool");
            Check(Txt(X<TextBlock>("TxtAutostart")) == "Start at login", "settings: the autostart row is worded for this system");
            var tgAuto = G<ToggleButton>("tgAutostart");
            Check(Autostart.File_.StartsWith(tmp), "settings: the autostart entry would go under the test's home (" + Autostart.File_ + ")");
            Click(tgAuto, "settings: start at login");
            Check(Until(delegate { return Toast == "Autostart needs real hardware"; }, 3000), "settings: on simulated hardware the switch says it needs real hardware (\"" + Toast + "\")");
            Check(!File.Exists(Autostart.File_), "settings: and writes no entry");
            E.Post(delegate { E.SetTrayTemp(E.S.TrayTemp); });      // any state change: the next refresh puts the switch back
            Settle(400);
            Check(tgAuto.IsChecked == false, "settings: the switch goes back off at the next refresh");
            // the core's half, which the switch calls on real hardware
            Check(Autostart.Set(true) && File.Exists(Autostart.File_) && Autostart.IsOn() && File.ReadAllText(Autostart.File_).Contains(" --hidden"), "settings: Autostart.Set(true) writes an entry that starts hidden");
            Check(Autostart.Set(false) && !File.Exists(Autostart.File_) && !Autostart.IsOn(), "settings: Autostart.Set(false) removes it");
            var poll = G<Seg>("pollSeg");
            int[] ms = { 500, 1000, 2000 };
            foreach (int i in new[] { 0, 1, 2 }) {
                Click(Cells(poll)[i], "settings: sensor refresh " + ms[i]);
                Check(E.S.PollMs == ms[i] && StateText.Contains("PollMs=" + ms[i]), "settings: the sensor refresh is " + ms[i] + " ms, saved");
            }
            Settle(250);

            // reset: No
            var btnReset = G<TextBlock>("btnReset");
            seen = dialogs;
            Answer("No");
            Click(btnReset, "settings: Reset, answering No");
            Settle(250);
            Check(dialogs == seen + 1 && lastDialog.StartsWith("Undo everything"), "settings: Reset asks first");
            Check(Txt(btnReset) == "Reset" && !G<bool>("exiting") && !G<bool>("resetting") && File.Exists(AppPaths.StateFile) && lifetimeShutdowns() == 0, "settings: No changes nothing");

            // updates
            Flip(G<ToggleButton>("tgUpdateAuto"), "settings: check for updates at start", delegate { return E.S.UpdateOnLaunch; });
            var btnUpdate = G<TextBlock>("btnUpdate"); var txtUpdate = G<TextBlock>("txtUpdate");
            Check(Txt(G<TextBlock>("txtUpdateTitle")) == "Check for updates" && Txt(btnUpdate) == "Check now", "settings: the update row offers a check");
            Click(btnUpdate, "settings: Check now");
            Check(Txt(txtUpdate) == "checking…", "settings: the row says it is checking");
            Check(Until(delegate { return Txt(txtUpdate) != "checking…"; }, 30000), "settings: and the check comes back (\"" + Txt(txtUpdate) + "\", toast \"" + Toast + "\")");
            Settle(250);

            // the footer
            var txtDiag = G<TextBlock>("txtDiag");
            Click(G<TextBlock>("btnDiag"), "settings: Diagnostics");
            Check(txtDiag.IsVisible, "settings: Diagnostics opens the text under the rows");
            Check(Until(delegate { return Txt(txtDiag) != "running…" && Txt(txtDiag).Length > 40; }, 20000), "settings: and fills it (" + Txt(txtDiag).Length + " characters)");
            Click(G<TextBlock>("btnDiag"), "settings: Diagnostics again");
            Check(!txtDiag.IsVisible, "settings: a second click closes it");
            string spawned = Spawned;
            var btnSupport = G<TextBlock>("btnSupport");
            Click(btnSupport, "settings: Report Issue");
            Check(Txt(btnSupport) == "collecting…", "settings: Report Issue says it is collecting");
            Check(Until(delegate { return Txt(btnSupport) == "Report Issue" && txtDiag.IsVisible; }, 40000), "settings: the report arrives and is shown");
            string report = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Path), "support-info.txt");
            Check(File.Exists(report) && new FileInfo(report).Length > 200 && report.StartsWith(tmp), "settings: support-info.txt is written beside the log");
            Frames(200);
            Check(Toast.StartsWith("Copied.") || Toast.StartsWith("Saved as support-info.txt"), "settings: a toast says where it went (\"" + Toast + "\")");
            Check(Until(delegate { string s = Spawned.Substring(spawned.Length); return s.Contains(System.IO.Path.GetDirectoryName(report)) && s.Contains("github.com"); }, 5000), "settings: the folder and the issue form are handed to the system to open");
            Settle(300);
            WindowFollows("settings: with the report shown");
            spawned = Spawned;
            Click(G<TextBlock>("btnLog"), "settings: Log");
            Check(Until(delegate { return Spawned.Substring(spawned.Length).Contains(Log.Path); }, 5000), "settings: Log hands the log file to the system to open");
            Click(G<TextBlock>("btnDiag"), "settings: closing the report");
            Click(G<TextBlock>("btnDiag"), "settings: closing the report (second click)");
            if (txtDiag.IsVisible) Click(G<TextBlock>("btnDiag"), "settings: closing the report (third click)");

            // as it was
            Click(Cells(keySeg)[Choice.Of(key0)], "settings: OMEN key back");
            Until(delegate { return E.S.Key == key0; });
            E.Post(delegate { E.SetKeyCommand(""); });
            Until(delegate { return E.S.KeyCommand.Length == 0; });
            Settle(300);
            // where the rail's update button and the driver nudge lead: Settings, with their row eased into view
            foreach (string row in new[] { "ShowUpdateRow", "ShowDriverRow" }) {
                Go(0);
                Do("settings: " + row, delegate { Call(row); });
                Settle(300);
                Control target = row == "ShowUpdateRow" ? (Control)G<Border>("updateRow") : G<Control>("driverRow");
                double y = target.TranslatePoint(new Point(0, 0), scroll).Value.Y, max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                Check(Cur == "Settings" && (Math.Abs(y - 96) < 6 || Math.Abs(scroll.Offset.Y - max) < 1) && y >= 0 && y + target.Bounds.Height <= scroll.Bounds.Height + 1, "settings: " + row + " opens Settings with the row in view (" + y.ToString("0") + " px down, " + target.Bounds.Height.ToString("0") + " tall in " + scroll.Bounds.Height.ToString("0") + ", scrolled " + scroll.Offset.Y.ToString("0") + " of " + max.ToString("0") + ")");
            }
            // the head is a drag strip: a press on it must hand the move to the window system and nothing else
            Click(X<Control>("HeadSettings"), "settings: a press on the page's head");
            Check(Cur == "Settings" && win.IsVisible && !G<bool>("dragging"), "settings: a press on the head is a window drag, and leaves the page alone");
        }
        static int lifetimeShutdowns() { return shutdowns; }
    }
}
