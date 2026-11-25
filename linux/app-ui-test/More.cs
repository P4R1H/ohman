// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the interaction test's on-screen flash, tray menu, show and hide, the benchmark, and the end of the run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WPath = Avalonia.Controls.Shapes.Path;

namespace Ohman {
    static partial class UiTest {
        // ---------- the on-screen flash ----------
        static Osd TheOsd { get { return G<Osd>("osd"); } }
        static string OsdTitle { get { var o = TheOsd; return o == null || !o.IsVisible ? "" : Txt(Priv<TextBlock>(o, "txt")); } }
        static string OsdSub { get { var o = TheOsd; if (o == null || !o.IsVisible) return ""; var s = Priv<TextBlock>(o, "sub"); return s.IsVisible ? Txt(s) : ""; } }
        static Color OsdInk { get { return ((ISolidColorBrush)Priv<WPath>(TheOsd, "icon").Stroke).Color; } }
        static void Do(string what, Action a) { try { a(); Pump(); } catch (Exception ex) { Thrown(what, ex); } }

        static void Flashes() {
            Section("flash");
            Go(0);
            string[] subs = G<string[]>("ModeSubs");
            // the OMEN key set to cycle
            for (int n = 0; n < 3; n++) {
                int next = (E.ModeIndex + 1) % 3;
                Do("flash: OMEN key, cycle", delegate { Call("OnKey", KeyAction.Cycle); });
                Check(OsdTitle == Engine.ModeNames[next] + " mode" && OsdSub == subs[next] && OsdInk == Ui.ModeColor(next), "flash: the OMEN key cycling shows \"" + OsdTitle + "\" / \"" + OsdSub + "\" in the mode's colour");
                Check(Until(delegate { return E.ModeIndex == next; }), "flash: and the engine is in " + Engine.ModeNames[next]);
                Settle(250);
                Check(Txt(G<TextBlock>("txtHomeTitle")) == Engine.ModeNames[next], "flash: the window follows");
            }
            var box = (Border)TheOsd.Content;
            Check(box.Opacity == 1, "flash: fully shown after its fade in");
            Wait(1500);
            Frames(130);
            Check(TheOsd.IsVisible && box.Opacity > 0.2 && box.Opacity < 0.8, "flash: fading out 1.4 s after the last one (" + box.Opacity.ToString("0.00") + ")");
            Frames(300);
            Check(!TheOsd.IsVisible, "flash: gone once faded");

            // the OMEN key set to max fan
            Do("flash: OMEN key, max fan", delegate { Call("OnKey", KeyAction.MaxFan); });
            Check(OsdTitle == "Max fan" && OsdSub == "Both fans to full speed" && OsdInk == Ui.Accent.Color, "flash: max fan on shows \"" + OsdTitle + "\" / \"" + OsdSub + "\"");
            Check(Until(delegate { return E.S.Fan == FanMode.Max; }), "flash: and the fans are at max");
            Settle(250);
            TraySync("flash: max fan");
            // a mode whose own fan setting is Max says so when it is switched to
            int here = E.ModeIndex;
            Do("flash: hotkey " + ((here + 1) % 3), delegate { Call("OnHotkey", (here + 1) % 3); });
            Check(OsdSub == subs[(here + 1) % 3], "flash: a mode whose fans are Auto names only the mode (\"" + OsdSub + "\")");
            Until(delegate { return E.ModeIndex == (here + 1) % 3; });
            Settle(250);
            Do("flash: hotkey " + here, delegate { Call("OnHotkey", here); });
            Check(OsdTitle == Engine.ModeNames[here] + " mode" && OsdSub == subs[here] + " · max fan", "flash: a mode whose fans were left at Max says so (\"" + OsdSub + "\")");
            Until(delegate { return E.ModeIndex == here; });
            Settle(250);
            Do("flash: hotkey, max fan", delegate { Call("OnHotkey", (int)HotkeyAction.MaxFan); });
            Check(OsdTitle == "Fans auto" && OsdSub == "Back to the firmware curve", "flash: max fan off shows \"" + OsdTitle + "\" / \"" + OsdSub + "\"");
            Check(Until(delegate { return E.S.Fan == FanMode.Auto; }), "flash: and the fans are back on Auto");
            Settle(250);
            // each mode's own hotkey
            foreach (int m in new[] { 0, 2, 1 }) {
                Do("flash: hotkey " + m, delegate { Call("OnHotkey", m); });
                Check(OsdTitle == Engine.ModeNames[m] + " mode" && OsdSub == subs[m] && OsdInk == Ui.ModeColor(m), "flash: the " + Engine.ModeNames[m] + " hotkey shows \"" + OsdTitle + "\"");
                Check(Until(delegate { return E.ModeIndex == m; }), "flash: and switches to " + Engine.ModeNames[m]);
                Settle(250);
            }
            Do("flash: hotkey, cycle", delegate { Call("OnHotkey", (int)HotkeyAction.Cycle); });
            Check(OsdTitle == Engine.ModeNames[2] + " mode", "flash: the cycle hotkey goes to the next mode (\"" + OsdTitle + "\")");
            Until(delegate { return E.ModeIndex == 2; });
            Settle(250);
            Do("flash: hotkey 1", delegate { Call("OnHotkey", 1); });
            Until(delegate { return E.ModeIndex == 1; });
            Settle(250);

            // the OMEN key set to the panel, to a command, to nothing
            Do("flash: OMEN key, panel", delegate { Call("OnKey", KeyAction.Show); });
            Check(!win.IsVisible, "flash: the OMEN key set to Panel hides an open window");
            Do("flash: OMEN key, panel", delegate { Call("OnKey", KeyAction.Show); });
            Settle(250);
            Check(win.IsVisible, "flash: and shows a hidden one");
            Do("flash: OMEN key, run, nothing set", delegate { Call("OnKey", KeyAction.Run); });
            Frames(200);
            Check(Toast == "OMEN key: no command set (Settings)", "flash: Run with no command says so (\"" + Toast + "\")");
            string spawned = Spawned;
            E.S.KeyCommand = "xdg-open omen-key-run";
            Do("flash: OMEN key, run", delegate { Call("OnKey", KeyAction.Run); });
            Check(OsdTitle == "OMEN key" && OsdSub == "xdg-open omen-key-run", "flash: Run shows the command (\"" + OsdTitle + "\" / \"" + OsdSub + "\")");
            Check(Until(delegate { return Spawned.Substring(spawned.Length).Contains("omen-key-run"); }, 5000), "flash: and starts it");
            E.S.KeyCommand = "";
            int mode = E.ModeIndex; FanMode fan = E.S.Fan; bool vis = win.IsVisible;
            Do("flash: OMEN key, off", delegate { Call("OnKey", KeyAction.Off); });
            Settle(250);
            Check(E.ModeIndex == mode && E.S.Fan == fan && win.IsVisible == vis, "flash: the OMEN key set to Off does nothing");
            Wait(1500); Frames(500);
        }

        // ---------- the tray menu ----------
        static void MenuClick(NativeMenuItem item) {
            foreach (var i in item.GetType().GetInterfaces()) {
                var m = i.GetMethod("RaiseClicked");
                if (m != null) { try { m.Invoke(item, null); } catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; } return; }
            }
            throw new InvalidOperationException("no way to click a NativeMenuItem");
        }
        static NativeMenuItem exitItem;
        static void Tray() {
            Section("tray");
            var tray = G<TrayIcon>("tray");
            if (!Check(tray != null && tray.Menu != null, "tray: there is a tray icon with a menu")) return;
            var top = tray.Menu.Items.OfType<NativeMenuItem>().ToList();
            Check(top.Count >= 7 && top[0].Header == Program.DisplayName + "   " + Program.Version && !top[0].IsEnabled, "tray: the menu's head is the name and version, greyed");
            Func<string, NativeMenuItem> item = delegate(string h) { return top.FirstOrDefault(x => x.Header == h); };
            Go(0);
            foreach (int m in new[] { 0, 2, 1 }) {
                Do("tray: " + Engine.ModeNames[m], delegate { MenuClick(item(Engine.ModeNames[m])); });
                Check(Until(delegate { return E.ModeIndex == m; }), "tray: " + Engine.ModeNames[m] + " switches the mode");
                Settle(250);
                Check(Txt(G<TextBlock>("txtHomeTitle")) == Engine.ModeNames[m] && Sel(G<Seg>("modeSeg")) == m, "tray: the window follows");
                TraySync("tray: " + Engine.ModeNames[m]);
                Check(tray.ToolTipText.StartsWith(Program.DisplayName + " · " + Engine.ModeNames[m] + " · "), "tray: the tip names the mode (\"" + tray.ToolTipText + "\")");
            }
            var fan = item("Fan");
            if (Check(fan != null && fan.Menu != null && fan.Menu.Items.Count == 4, "tray: a Fan submenu with the four fan modes")) {
                var fans = fan.Menu.Items.OfType<NativeMenuItem>().ToList();
                foreach (int i in new[] { 1, 2, 3, 0 }) {
                    Do("tray: fan " + Choice.Fan[i], delegate { MenuClick(fans[i]); });
                    Check(fans[i].Header == Choice.Fan[i] && Until(delegate { return E.S.Fan == Choice.FanModes[i]; }), "tray: Fan > " + Choice.Fan[i] + " sets the fan mode");
                    Settle(250);
                    TraySync("tray: fan " + Choice.Fan[i]);
                    Check(Sel(G<LinkSeg>("fanLinks")) == (i == 0 ? 0 : i == 1 ? 1 : 2), "tray: Home's fan links follow");
                }
            }
            var screen = item("Screen");
            if (screen == null) Note("no Screen submenu on this simulated board");
            else {
                var gfx = screen.Menu.Items.OfType<NativeMenuItem>().Where(x => x.Header.EndsWith(" graphics")).ToList();
                var unticked = gfx.FirstOrDefault(x => !x.IsChecked);
                if (Check(gfx.Count >= 2 && gfx.Count(x => x.IsChecked) == 1 && unticked != null, "tray: Screen lists the graphics modes with the current one ticked")) {
                    int seen = dialogs, mode = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
                    Answer("Cancel");
                    Do("tray: " + unticked.Header, delegate { MenuClick(unticked); });
                    Settle(250);
                    Check(dialogs == seen + 1 && lastDialog.StartsWith("Switch graphics to") && (E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode) == mode, "tray: a graphics mode asks about the restart, and Cancel writes nothing");
                }
            }
            var light = item("Keyboard lighting");
            if (light == null) Note("no Keyboard lighting item (no keyboard lighting on this board)");
            else {
                Check(light.IsChecked == (E.S.Light != 0), "tray: Keyboard lighting is ticked while the backlight is on");
                Do("tray: keyboard lighting", delegate { MenuClick(light); });
                Check(Until(delegate { return E.S.Light == 0; }), "tray: Keyboard lighting switches the backlight off");
                Settle(250);
                Check(!light.IsChecked && Txt(G<TextBlock>("txtLightSub")) == "Off", "tray: the tick and Home's row follow");
                Do("tray: keyboard lighting", delegate { MenuClick(light); });
                Check(Until(delegate { return E.S.Light == 1; }), "tray: and on again, in the mode it was in");
                Settle(250);
                Check(light.IsChecked, "tray: ticked again");
            }
            // ticks are refreshed when the menu opens, whatever happened before
            foreach (var x in G<object[]>("trayModes").Cast<NativeMenuItem>()) x.IsChecked = false;
            foreach (var i in tray.Menu.GetType().GetInterfaces()) { var m = i.GetMethod("RaiseOpening"); if (m != null) m.Invoke(tray.Menu, null); }
            TraySync("tray: opening the menu");
            var show = item("Show " + Program.DisplayName);
            exitItem = item("Exit");
            Check(show != null && exitItem != null, "tray: Show and Exit close the menu's list");
            Do("tray: hide", delegate { Call("HideToTray"); });
            Check(!win.IsVisible, "tray: (the window is hidden)");
            Do("tray: Show", delegate { MenuClick(show); });
            Settle(250);
            Check(win.IsVisible, "tray: Show brings the window back");
            Do("tray: a click on the icon", delegate { Call("TogglePanel"); });
            Check(!win.IsVisible, "tray: a click on the icon hides an open window");
            Do("tray: a click on the icon", delegate { Call("TogglePanel"); });
            Settle(250);
            Check(win.IsVisible, "tray: and shows a hidden one");
        }

        // ---------- show, hide, a second start ----------
        static void ShowHide() {
            Section("window");
            Go(1);
            var close = G<Button>("btnClose");
            Do("window: pointer in", delegate { win.MouseMove(new Point(200, 200)); });
            Frames(250);
            Check(close.Opacity == 1, "window: the close button fades in when the pointer is over the window");
            var ui = G<DispatcherTimer>("uiTimer");
            Check(ui.IsEnabled, "window: the readouts' timer runs while the window is shown");
            Click(close, "window: the close button");
            Check(!win.IsVisible && !ui.IsEnabled, "window: it hides the window and stops the readouts' timer");
            Check(open.Contains(win) || !G<bool>("exiting"), "window: hidden, not closed");
            Do("window: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            Check(win.IsVisible && Cur == "Fans" && win.ClientSize == natural["Fans"] && ui.IsEnabled, "window: shown again on the same page at its size (" + win.ClientSize.Width + " x " + win.ClientSize.Height + ")");
            var pill = G<TranslateTransform>("railPillT");
            Point at = Nav(1).TranslatePoint(new Point(0, 0), G<Canvas>("railCanvas")).Value;
            Check(Math.Abs(pill.Y - at.Y) < 0.6, "window: the rail pill is where it was");
            Key(Avalonia.Input.Key.Escape, RawInputModifiers.None);
            Check(!win.IsVisible, "window: Escape hides it");
            Do("window: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            Do("window: closing it", delegate { win.Close(); });
            Check(!win.IsVisible && !G<bool>("exiting") && shutdowns == 0, "window: closing the window hides it; the app stays");
            Do("window: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            Check(win.IsVisible, "window: and it can be shown again after that");
            Do("window: minimise", delegate { win.WindowState = WindowState.Minimized; });
            Check(!win.IsVisible && win.WindowState == WindowState.Normal, "window: minimising hides it to the tray instead");
            // navigating while hidden lands at once
            Do("window: Navigate while hidden", delegate { Call("Navigate", Enum.ToObject(typeof(MainWindow).GetNestedType("Page", BF), 2), true); });
            Do("window: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            at = Nav(2).TranslatePoint(new Point(0, 0), G<Canvas>("railCanvas")).Value;
            Check(win.IsVisible && Cur == "Keyboard" && win.ClientSize == natural["Keyboard"] && Math.Abs(pill.Y - at.Y) < 0.6, "window: a page change made while hidden is there when it is shown, at that page's size (" + win.ClientSize.Width + " x " + win.ClientSize.Height + ")");
            // where it is: remembered, and a page that would reach past the screen's edge pulls the window back in
            Go(0);
            Rect wa = (Rect)typeof(MainWindow).GetProperty("WorkArea", BF).GetValue(win);
            double k = win.DesktopScaling;          // window positions are device pixels; the window keeps them in DIPs
            int left = (int)(wa.Right - natural["Home"].Width - 10);
            Do("window: moved", delegate { win.Position = new PixelPoint((int)(left * k), (int)(120 * k)); });
            Check(E.S.WinX == left && E.S.WinY == 120, "window: where it was moved to is remembered (" + E.S.WinX + ", " + E.S.WinY + ")");
            Go(2);
            Check(Math.Abs(win.Position.X / k + win.ClientSize.Width - (wa.Right - 6)) < 1.01 && Math.Abs(win.Position.Y / k - 120) < 0.51 && win.ClientSize == natural["Keyboard"], "window: the wider Keyboard page stays on the screen, 6 px inside its right edge (left " + win.Position.X / k + ", width " + win.ClientSize.Width + ", work area to " + wa.Right + ")");
            Check(E.S.WinX == (int)(wa.Right - 6 - natural["Keyboard"].Width), "window: and that is where it is remembered (" + E.S.WinX + ")");
            Go(0);
            Do("window: moved back", delegate { win.Position = new PixelPoint(0, 0); });
            // a second start: the lock says no, and one word on the socket shows the first
            Check(!(bool)ProgramCall("TakeInstance", "ohman_demo"), "window: a second start cannot take the instance lock");
            Do("window: hide", delegate { Call("HideToTray"); });
            Check((bool)ProgramCall("Tell", "ohman_demo", "show"), "window: it reaches the first instance on its socket");
            Check(Until(delegate { return win.IsVisible; }, 5000), "window: and the first instance shows its window");
            Settle(300);
            Go(0);
        }

        // ---------- benchmark ----------
        static string refDir;
        static void RefSize(string file, string what) {
            int w, h;
            if (string.IsNullOrEmpty(refDir) || !PngSize(System.IO.Path.Combine(refDir, file + ".png"), out w, out h)) return;
            Check(Math.Abs(win.ClientSize.Width * 2 - w) < 0.01 && Math.Abs(win.ClientSize.Height * 2 - h) < 0.01, what + ": the window is the size of the Windows picture " + file + ".png (" + win.ClientSize.Width * 2 + " x " + win.ClientSize.Height * 2 + " px, picture " + w + " x " + h + ")");
        }
        static void Benchmark() {
            Section("benchmark");
            refDir = Environment.GetEnvironmentVariable("REF") ?? "";
            Go(4);
            var bench = G<Bench>("bench");
            var page = G<BenchPage>("benchPage");
            var tray = G<TrayIcon>("tray");
            int warm0 = bench.WarmChoice, measure0 = bench.MeasureChoice;
            foreach (string s in new[] { "first", "waiting", "warming", "measuring", "paused", "ready" }) {
                Do("benchmark: pose " + s, delegate { bench.Pose(s); page.Pose(s); page.Render(); });
                Settle(150);
                Check(page.IsVisible && page.Bounds.Height > 300, "benchmark: the page draws its \"" + s + "\" state");
                WindowFollows("benchmark: " + s);
                if (s == "first") RefSize("page-bench", "benchmark: " + s);      // the one Windows picture of this page that is this page
                Shot("bench-" + s);
            }
            // Pose is a screenshot aid that is used once in a process and never has to come back from a posed run.
            // Here it does, so the phase it left behind is cleared by hand.
            bench.Phase = BenchPhase.Idle;
            Do("benchmark: back from the poses", delegate { page.Render(); });
            Settle(150);
            var main = Priv<ActionButton>(page, "main"); var side = Priv<ActionButton>(page, "side");
            var steps = Priv<StepSeg[]>(page, "steps");
            var links = Priv<StackPanel>(page, "footLinks");
            Func<BenchPhase> phase = delegate { return bench.Snapshot().Phase; };
            for (int n = 0; n < 3 && bench.WarmChoice != 2; n++) Click(steps[0], "benchmark: the warm-up step");
            for (int n = 0; n < 3 && bench.MeasureChoice != 0; n++) Click(steps[1], "benchmark: the measure step");
            Check(bench.WarmChoice == 2 && bench.MeasureChoice == 0 && E.S.BenchWarm == 2 && E.S.BenchMeasure == 0, "benchmark: clicking the steps cycles warm-up to skip and measuring to 0:30, saved");
            Check((string)main.Tag == "start" && (string)side.Tag == "runs" && RunStore.List().Count == 0, "benchmark: ready, with Start and no runs yet");

            // from the page's button; cancelled from the page
            Click(main, "benchmark: Start");
            Check(Until(delegate { return phase() == BenchPhase.Waiting; }), "benchmark: Start begins waiting for a game");
            Check(!win.IsVisible, "benchmark: the window steps out of the way");
            Check(OsdTitle == "Benchmark" && OsdSub.StartsWith("Switch to your game"), "benchmark: the flash says what to do (\"" + OsdSub + "\")");
            Until(delegate { return tray != null && tray.ToolTipText.Contains("benchmark"); }, 2000);
            if (tray != null) Check(tray.ToolTipText.Contains("benchmark · "), "benchmark: the tray tip follows the run (\"" + tray.ToolTipText + "\")");
            var tick = Priv<DispatcherTimer>(page, "tick");
            Check(!tick.IsEnabled, "benchmark: hidden in the tray, the page's own one-second redraw is stopped");
            Do("benchmark: ShowPanel", delegate { win.ShowPanel(); });
            Check(tick.IsEnabled && Find(links, "Cancel") != null, "benchmark: shown again, the page is redrawn at once and its timer runs");
            Settle(300);
            Check(Cur == "Bench" && Txt(Priv<TextBlock>(page, "title")) != "", "benchmark: the page shows the run in progress (\"" + Txt(Priv<TextBlock>(page, "status")) + "\")");
            var cancel = Find(links, "Cancel") ?? Find(links, "Stop") ?? Find(links, "Skip");
            if (cancel == null) { Check(false, "benchmark: no Cancel or Stop link while a run is on"); bench.Stop(); }
            else Click(Find(links, "Cancel") ?? Find(links, "Stop"), "benchmark: the link that ends the run");
            Check(Until(delegate { return phase() == BenchPhase.Idle; }, 8000), "benchmark: the run ends");
            Frames(200);
            Check(Toast == "Benchmark stopped", "benchmark: the window says it was stopped (\"" + Toast + "\")");
            Check(RunStore.List().Count == 0, "benchmark: a run stopped before measuring is not kept");
            Settle(300);

            // from the hotkey, to the end
            Do("benchmark: the hotkey", delegate { Call("OnHotkey", (int)HotkeyAction.Benchmark); });
            Check(Until(delegate { return bench.Running; }) && !win.IsVisible, "benchmark: the hotkey starts a run and the window hides");
            var seen = new List<BenchPhase>();
            bool done = Until(delegate { var p = phase(); if (seen.Count == 0 || seen[seen.Count - 1] != p) seen.Add(p); progress = DateTime.UtcNow; return p == BenchPhase.Idle && RunStore.List().Count == 1; }, 90000);
            Check(done, "benchmark: the simulated run finishes and is stored (" + string.Join(" > ", seen) + ")");
            Check(seen.Contains(BenchPhase.Countdown) && seen.Contains(BenchPhase.Measuring), "benchmark: it went through the countdown and measuring");
            Pump();
            Check(OsdTitle == "Benchmark done" && OsdSub.Contains("fps avg"), "benchmark: with the window hidden the flash gives the result (\"" + OsdSub + "\")");
            Check(!win.IsVisible, "benchmark: the window stays hidden until asked for");
            Do("benchmark: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            var shown = Priv<BenchRun>(page, "shown");
            Check(shown != null && Txt(Priv<TextBlock>(page, "title")) == shown.Game && (string)main.Tag == "share" && (string)side.Tag == "again", "benchmark: the page shows the result (" + (shown == null ? "none" : shown.Game + ", " + shown.AvgFps.ToString("0") + " fps") + ")");
            WindowFollows("benchmark: result");
            Shot("bench-result");
            int g0 = Priv<int>(page, "gi");
            Key(Avalonia.Input.Key.Right, RawInputModifiers.None);
            int g1 = Priv<int>(page, "gi");
            Key(Avalonia.Input.Key.Left, RawInputModifiers.None);
            Check(g1 == g0 + 1 && Priv<int>(page, "gi") == g0, "benchmark: the arrow keys flip through the graphs (" + g0 + " > " + g1 + " > " + Priv<int>(page, "gi") + ")");

            // the game's settings line: a click edits it in place
            var setBtn = Priv<Border>(page, "settingsBtn"); var setBox = Priv<TextBox>(page, "settingsBox"); var setText = Priv<TextBlock>(page, "settingsText");
            Click(setBtn, "benchmark: the settings line");
            Check(setBox.IsVisible && !setBtn.IsVisible && setBox.IsFocused, "benchmark: a click on the settings line opens a field with the keyboard in it");
            Do("benchmark: typing the settings", delegate { win.KeyTextInput("High, DLSS Quality"); });
            Key(Avalonia.Input.Key.Enter, RawInputModifiers.None);
            Check(!setBox.IsVisible && setBtn.IsVisible && Txt(setText) == "High, DLSS Quality" && RunStore.List()[0].Settings == "High, DLSS Quality" && E.S.GameLine(shown.Exe) == "High, DLSS Quality", "benchmark: Enter stores it with the run and for the game (\"" + Txt(setText) + "\")");
            Click(setBtn, "benchmark: the settings line again");
            Do("benchmark: typing over it", delegate { win.KeyTextInput("nonsense"); });
            Key(Avalonia.Input.Key.Escape, RawInputModifiers.None);
            Check(!setBox.IsVisible && Txt(setText) == "High, DLSS Quality" && win.IsVisible, "benchmark: Escape drops the edit and does not hide the window");
            WindowFollows("benchmark: result, after the edit");

            // the share card
            Click(main, "benchmark: Share card");
            Settle(300);
            var shareView = Priv<Grid>(page, "shareView");
            Check(Priv<bool>(page, "shareOpen") && shareView.IsVisible && Priv<Image>(page, "sharePreview").Source != null, "benchmark: Share card opens the card's preview");
            WindowFollows("benchmark: share");
            var layout = Priv<LinkSeg>(page, "shareLayout");
            foreach (int i in new[] { 1, 2, 0 }) {
                Click(Links(layout)[i], "benchmark: layout " + Card.Layouts[i]);
                Settle(200);
                Check(E.S.ShareLayout == i && Sel(layout) == i && Priv<Image>(page, "sharePreview").Source != null, "benchmark: the " + Card.Layouts[i] + " layout is drawn and remembered");
            }
            string pics = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify), Program.AppName);
            string spawned = Spawned;
            Click(Priv<ActionButton>(page, "saveBtn"), "benchmark: Save");
            Frames(200);
            string[] saved = Directory.Exists(pics) ? Directory.GetFiles(pics, "*.png") : new string[0];
            int cw = 0, ch = 0;
            if (saved.Length > 0) PngSize(saved[0], out cw, out ch);
            Check(pics.StartsWith(tmp) && saved.Length == 1 && cw == 1080 && ch == 1350, "benchmark: Save writes the card under the test's home (" + (saved.Length > 0 ? saved[0] + ", " + cw + " x " + ch : "nothing in " + pics) + ")");
            Check(Toast == "Saved to Pictures/" + Program.AppName, "benchmark: and says where (\"" + Toast + "\")");
            Check(Until(delegate { return Spawned.Substring(spawned.Length).Contains(pics); }, 5000), "benchmark: and hands the folder to the system to show");
            if (saved.Length > 0) try { File.Copy(saved[0], System.IO.Path.Combine(outDir, "share-card.png"), true); } catch { }
            Click(Priv<ActionButton>(page, "copyBtn"), "benchmark: Copy");
            Frames(200);
            Check(Toast == "Copied: paste it anywhere", "benchmark: Copy puts the card on the clipboard (\"" + Toast + "\")");
            Click(All<Border>(page.ShareHead).First(x => x.Width == 24), "benchmark: Share's back arrow");
            Settle(300);
            Check(!Priv<bool>(page, "shareOpen") && !shareView.IsVisible && Priv<BenchRun>(page, "shown") != null, "benchmark: back to the result");

            // Run again, stopped by the hotkey past half of the measuring time: kept
            Click(side, "benchmark: Run again");
            Check(Until(delegate { return bench.Running; }) && !win.IsVisible, "benchmark: Run again starts another run");
            Check(Until(delegate { progress = DateTime.UtcNow; return bench.Snapshot().MeasuredSec >= 17; }, 60000), "benchmark: it is measuring");
            Do("benchmark: the hotkey, to stop", delegate { Call("OnHotkey", (int)HotkeyAction.Benchmark); });
            Check(Until(delegate { return phase() == BenchPhase.Idle; }, 15000), "benchmark: the hotkey stops a run that is on");
            Check(Until(delegate { return RunStore.List().Count == 2; }, 5000), "benchmark: stopped past half of its time, the run is kept (" + RunStore.List().Count + " runs)");
            Do("benchmark: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);

            // a run cancelled while waiting leaves the page ready, with the list behind the count
            Click(side, "benchmark: Run again");
            Until(delegate { return phase() == BenchPhase.Waiting; });
            Do("benchmark: ShowPanel", delegate { win.ShowPanel(); });
            Settle(300);
            Click(Find(links, "Cancel"), "benchmark: Cancel");
            Until(delegate { return phase() == BenchPhase.Idle; }, 8000);
            Settle(300);
            Check((string)main.Tag == "start" && (string)side.Tag == "runs" && Find(side, "2") != null, "benchmark: ready again, and the count beside Start says 2");
            Click(side, "benchmark: the runs count");
            Settle(300);
            var runsView = Priv<Grid>(page, "runsView");
            var rows = Priv<Panel>(page, "runRows");
            Check(Priv<bool>(page, "showRuns") && runsView.IsVisible && rows.Children.Count == 2 && Txt(Priv<TextBlock>(page, "runsTotal")) == "2 runs · 1 game", "benchmark: the list shows both runs (\"" + Txt(Priv<TextBlock>(page, "runsTotal")) + "\")");
            WindowFollows("benchmark: runs");
            Shot("bench-runs");
            ClickAt(rows.Children[1], new Point(40, rows.Children[1].Bounds.Height / 2), "benchmark: the older run");
            Settle(300);
            var older = Priv<BenchRun>(page, "shown");
            Check(older != null && !Priv<bool>(page, "showRuns") && (string)side.Tag == "back", "benchmark: a click on a row opens that run, with Back beside Share");
            Click(side, "benchmark: Back");
            Settle(300);
            Check(Priv<bool>(page, "showRuns") && rows.Children.Count == 2, "benchmark: Back returns to the list");
            // delete: the bin appears on hover, the first click asks, the second deletes
            var row = rows.Children[1];
            Do("benchmark: hovering a row", delegate { win.MouseMove(row.TranslatePoint(new Point(40, row.Bounds.Height / 2), win).Value); });
            var bin = All<Border>(row).FirstOrDefault(x => x.Padding == new Thickness(6, 8, 0, 8));
            if (Check(bin != null && bin.IsVisible, "benchmark: a bin appears on the row under the pointer")) {
                Click(bin, "benchmark: the bin");
                Check(Find(bin, "delete") != null && RunStore.List().Count == 2, "benchmark: the first click asks (\"delete\") and deletes nothing");
                Click(bin, "benchmark: the bin, at once");
                Check(RunStore.List().Count == 2, "benchmark: a double-click is not the answer");
                Wait(650);
                Click(bin, "benchmark: the bin, again");
                Frames(200);
                Check(RunStore.List().Count == 1 && Toast == "Run deleted", "benchmark: the second click deletes the run (\"" + Toast + "\")");
                Settle(300);
                Check(rows.Children.Count == 1 && Txt(Priv<TextBlock>(page, "runsTotal")) == "1 run · 1 game", "benchmark: and the list has one row");
                Check(older != null && RunStore.List()[0].Id != older.Id, "benchmark: the one that is left is the other run");
            }
            string docs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), Program.AppName);
            Click(Find(runsView, "Export CSV"), "benchmark: Export CSV");
            Frames(200);
            Check(docs.StartsWith(tmp) && Directory.Exists(docs) && Directory.GetFiles(docs, "*.csv").Length == 1, "benchmark: Export CSV writes the runs under the test's home (\"" + Toast + "\")");
            Click(All<Border>(page.RunsHead).First(x => x.Width == 24), "benchmark: the list's back arrow");
            Settle(300);
            Check(!Priv<bool>(page, "showRuns") && Priv<Grid>(page, "dash").IsVisible, "benchmark: back to the dashboard");
            Do("benchmark: choices back", delegate { bench.SetChoices(warm0, measure0); });
            Settle(300);
            var pillWin = G<BenchPill>("pill");
            if (pillWin != null) { Wait(200); Do("benchmark: pill off", delegate { pillWin.Off(); }); }
        }

        // ---------- the end ----------
        static void Final(bool whole) {
            Section("pages at the end");
            if (natural.Count == 0) { foreach (int p in new[] { 1, 2, 3, 0 }) { Go(p); Settle(200); natural[PageNames[p]] = win.ClientSize; } whole = false; }
            // The simulated CPU is hot after a benchmark, and above 84° its caption on Home grows a second line
            // ("power limit"). The pictures wait for it to cool, as the ones at the start were taken cool.
            var tail = G<Avalonia.Controls.Documents.Run>("runCpuTail");
            Go(0);
            bool cool = Until(delegate { progress = DateTime.UtcNow; return string.IsNullOrEmpty(tail.Text) && E.CpuTemp < 80; }, 240000);
            if (!cool) Note("the simulated CPU did not cool below 80° in four minutes (" + E.CpuTemp.ToString("0") + "°); Home is not compared");
            // every page still draws, at the size it had, and looks as it did before anything was clicked
            foreach (int p in new[] { 1, 2, 3, 0 }) {
                Go(p);
                Settle(300);
                string n = PageNames[p].ToLowerInvariant();
                Shot("end-" + n);
                if (p == 0 && !cool) continue;
                Check(win.ClientSize == natural[PageNames[p]], PageNames[p] + " is the size it was at the start (" + win.ClientSize.Width + " x " + win.ClientSize.Height + ")");
                double d = whole ? Differ(System.IO.Path.Combine(outDir, "start-" + n + ".png"), System.IO.Path.Combine(outDir, "end-" + n + ".png")) : 0;
                Check(d >= 0 && d < 0.02, PageNames[p] + " looks as it did at the start (" + (d * 100).ToString("0.00") + " % of pixels differ; live readings account for a little)");
                string st = System.IO.Path.Combine(staticDir, n + ".png");
                if (staticDir.Length > 0 && File.Exists(st) && p != 3) {      // the static Settings picture has the rows of every system (--all-rows)
                    double ds = Differ(st, System.IO.Path.Combine(outDir, "end-" + n + ".png"));
                    Check(ds >= 0 && ds < 0.05, PageNames[p] + " against its static screenshot: " + (ds * 100).ToString("0.00") + " % of pixels differ (the demo badge, the footer and live readings are expected)");
                }
            }
            Go(4); Settle(300); Shot("end-bench");

            Section("keyboard page switch");
            Go(3);
            var tgLight = G<ToggleButton>("tgLightPage");
            if (E.Light == null) Note("no keyboard lighting to hide");
            else {
                Click(tgLight, "the Keyboard page switch, off");
                Check(Until(delegate { return E.S.HideLight && E.Light == null; }), "switching the Keyboard page off hides the keyboard from the engine");
                Settle(300);
                Check(!Nav(2).IsVisible && !G<Control>("lightRow").IsVisible && tgLight.IsChecked == false, "the rail button and Home's Lighting row go");
                var light = G<object>("trayLight") as NativeMenuItem;
                Check(light == null || !light.IsVisible, "and the tray's Keyboard lighting item");
                Go(0); Settle(300);
                WindowFollows("Home without its Lighting row");
                Go(3);
                Click(tgLight, "the Keyboard page switch, on");
                Check(Until(delegate { return !E.S.HideLight; }), "switching it on is remembered");
                Frames(200);
                Check(Toast == "The keyboard page comes back when Ohman next starts", "and says when the page returns (\"" + Toast + "\")");
                Settle(300);
                Check(Txt(G<TextBlock>("txtLightPageSub")) == "Back when Ohman next starts" && tgLight.IsChecked == true, "the row says so too");
            }

            Section("exit");
            // Three ways out, and all of them are ExitApp. The first one really takes the app down (the engine, the
            // sensors, the tray); for the other two the flag that makes ExitApp run once is cleared, so that the click
            // can be seen arriving at Program.Shutdown again. Each gets a lifetime of its own to shut down.
            Go(3);
            string before = "";
            try { before = File.ReadAllText(Log.Path); } catch { }
            Console.Out.Flush();
            // 1. another start with --exit: one word on the instance socket
            Check((bool)ProgramCall("Tell", "ohman_demo", "exit"), "ohman --exit reaches the running instance on its socket");
            Check(Until(delegate { return shutdowns == 1; }, 6000) && G<bool>("exiting"), "and the instance goes through its exit path to Program.Shutdown (" + shutdowns + ")");
            string after = "";
            try { after = File.ReadAllText(Log.Path); } catch { }
            string tail2 = after.Length >= before.Length ? after.Substring(before.Length) : after;
            Check(tail2.Contains("exit requested by another instance") && tail2.TrimEnd().EndsWith("exit"), "the log says who asked, and its last line is the exit");
            Check(File.Exists(AppPaths.StateFile) && !G<DispatcherTimer>("uiTimer").IsEnabled, "the settings are saved and the timers stopped");
            var tray = G<TrayIcon>("tray");
            Action again = delegate {
                Put("exiting", false);
                var l = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                l.Exit += delegate { shutdowns++; };
                ProgramSet("lifetime", l);
            };
            // 2. the footer's link
            var btnExit = G<TextBlock>("btnExit");
            Check(Txt(btnExit) == "Exit " + Program.DisplayName, "the footer's Exit names the app");
            try {
                again();
                Point at = btnExit.TranslatePoint(Mid(btnExit), win).Value;
                win.MouseMove(at); win.MouseDown(at, MouseButton.Left); win.MouseUp(at, MouseButton.Left);
                Check(shutdowns == 2 && G<bool>("exiting"), "a click on the footer's Exit ends in Program.Shutdown too (" + shutdowns + ")");
            } catch (Exception ex) { Check(false, "the footer's Exit: " + ex.Message); }
            // 3. the tray menu's item
            try {
                again();
                if (exitItem == null && tray != null && tray.Menu != null) exitItem = tray.Menu.Items.OfType<NativeMenuItem>().FirstOrDefault(x => x.Header == "Exit");
                if (exitItem == null) Note("no tray menu to take Exit from");
                else { MenuClick(exitItem); Check(shutdowns == 3 && G<bool>("exiting"), "the tray menu's Exit ends in Program.Shutdown too (" + shutdowns + ")"); }
            } catch (Exception ex) { Check(false, "the tray's Exit: " + ex.Message); }
            try { after = File.ReadAllText(Log.Path); } catch { }

            Section("end of run");
            Check(after.Length > 0, "there is a log (" + Log.Path + ")");
            Check(!after.Contains("UI EXCEPTION"), "the log has no UI EXCEPTION");
            Check(!after.Contains("UNHANDLED"), "the log has no UNHANDLED");
            Check(uiExceptions == 0, "no handler threw (" + uiExceptions + ")");
            Check(!Spawned.Contains("systemctl"), "nothing asked the system to restart");
        }
    }
}
