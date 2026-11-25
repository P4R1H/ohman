// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: what the interaction test does, page by page. src\Ui.cs (the WPF window) is the specification:
// where a check says what should happen, that is what the Windows window does.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Ohman {
    static partial class UiTest {
        static T X<T>(string xamlName) where T : class {
            var scope = NameScope.GetNameScope((StyledElement)win.Content);
            return (scope != null ? scope.Find(xamlName) : null) as T;
        }
        static NavBtn Nav(int i) { return G<NavBtn[]>("nav")[i]; }
        static Control[] Pages { get { return G<Control[]>("pages"); } }
        static readonly string[] PageNames = { "Home", "Fans", "Keyboard", "Settings", "Bench" };
        static readonly Dictionary<string, Size> natural = new Dictionary<string, Size>();

        static void Run(HashSet<string> only) {
            Func<string, bool> want = delegate(string n) { return only.Count == 0 || only.Contains(n); };
            // One scenario falling over (a control that is not there) must not take the rest of the run with it.
            Action<string, Action> run = delegate(string n, Action a) {
                if (!want(n)) return;
                try { a(); } catch (Exception ex) { Check(false, n + " stopped: " + ex); }
            };
            try { Baseline(); } catch (Exception ex) { Check(false, "the first look at the pages stopped: " + ex); }
            run("rail", Rail);
            run("home", Home);
            run("fans", Fans);
            run("keyboard", Keyboard);
            run("settings", SettingsPage);
            run("flash", Flashes);
            run("tray", Tray);
            run("window", ShowHide);
            run("bench", Benchmark);
            try { Final(only.Count == 0); } catch (Exception ex) { Check(false, "the end of the run stopped: " + ex); }
        }

        /// <summary>Go to a page by its rail button and let the morph land.</summary>
        static void Go(int page) {
            if (Cur == PageNames[page]) return;
            Click(Nav(page), "rail: " + PageNames[page]);
            Settle(200);
        }

        /// <summary>Every page once, before anything is changed: its size, and a picture to compare the end of the run with.</summary>
        static void Baseline() {
            Section("pages at start");
            foreach (int p in new[] { 1, 2, 4, 3, 0 }) {
                Go(p);
                natural[PageNames[p]] = win.ClientSize;
                Shot("start-" + PageNames[p].ToLowerInvariant());
            }
            // the pictures the page comparison made, on the same board: the window is as large as the page is there
            string[] files = { "home", "fans", "keyboard", "settings" };
            for (int i = 0; i < files.Length; i++) {
                int w, h;
                if (staticDir.Length == 0 || !PngSize(System.IO.Path.Combine(staticDir, files[i] + ".png"), out w, out h)) { if (i == 0) Note("no static screenshots to compare sizes with (STATIC is not set)"); continue; }
                var s = natural[PageNames[i]];
                Check(Math.Abs(s.Width * 2 - w) < 0.01 && Math.Abs(s.Height * 2 - h) < 0.01, PageNames[i] + " is the size of its static screenshot (" + s.Width + " x " + s.Height + " DIPs, picture " + w + " x " + h + " px)");
            }
        }

        static void Rail() {
            Section("rail");
            var pill = G<TranslateTransform>("railPillT");
            var canvas = G<Canvas>("railCanvas");
            foreach (int p in new[] { 1, 2, 4, 3, 0 }) {
                string name = PageNames[p];
                Size before = win.ClientSize;
                Control old = Pages[Array.IndexOf(PageNames, Cur)];
                if (!Click(Nav(p), "rail: " + name)) continue;
                Check(Cur == name, "rail: " + name + " becomes the current page");
                Check(Pages[p].IsVisible, "rail: the " + name + " page is visible at once");
                Size want = natural[name];
                bool resizes = Math.Abs(want.Width - before.Width) > 1 || Math.Abs(want.Height - before.Height) > 1;
                Frames(90);
                Size mid = win.ClientSize;
                if (resizes) Check(G<bool>("morphing") && (Math.Abs(mid.Width - before.Width) > 0.4 || Math.Abs(mid.Height - before.Height) > 0.4) && (Math.Abs(mid.Width - want.Width) > 0.4 || Math.Abs(mid.Height - want.Height) > 0.4),
                    "rail: the window is on its way after 90 ms (" + before.Width + " x " + before.Height + " -> " + mid.Width.ToString("0.#") + " x " + mid.Height.ToString("0.#") + " -> " + want.Width + " x " + want.Height + ")");
                Settle(200);
                Check(!G<bool>("morphing") && win.ClientSize == want, "rail: the morph settles on the page's own size (" + win.ClientSize.Width + " x " + win.ClientSize.Height + ", wanted " + want.Width + " x " + want.Height + ")");
                for (int i = 0; i < 5; i++) if (i != p && Pages[i].IsVisible) Check(false, "rail: the " + PageNames[i] + " page is still visible behind " + name);
                Check(Pages[p].Opacity == 1 && Pages[p].IsHitTestVisible, "rail: the " + name + " page has faded in");
                Point at = Nav(p).TranslatePoint(new Point(0, 0), canvas).Value;
                Check(Math.Abs(pill.X - at.X) < 0.6 && Math.Abs(pill.Y - at.Y) < 0.6 && G<Border>("railPill").Opacity == 1, "rail: the pill sits behind " + name + " (" + pill.X.ToString("0.#") + "," + pill.Y.ToString("0.#") + " vs " + at.X.ToString("0.#") + "," + at.Y.ToString("0.#") + ")");
                Check(Priv<bool>(Nav(p), "sel"), "rail: the " + name + " button is the selected one");
            }
            Go(1);
            Click(G<Control>("logoHost"), "rail: the logo");
            Check(Cur == "Home", "rail: the logo goes Home");
            Settle(200);
            Check(win.ClientSize == natural["Home"], "rail: and the window is Home's size again");
        }

        static void Home() {
            Section("home");
            Go(0);
            var modeSeg = G<Seg>("modeSeg");
            var title = G<TextBlock>("txtHomeTitle");
            var src = G<ColorSource>("accentSrc");
            var trayModes = G<object[]>("trayModes");
            int[] order = { 0, 2, 1 };
            foreach (int m in order) {
                Color from = src.Color;
                if (!Click(Cells(modeSeg)[m], "home: " + Engine.ModeNames[m])) continue;
                Check(Txt(title) == Engine.ModeNames[m], "home: the title says " + Engine.ModeNames[m] + " at once");
                Frames(120);
                Color mid = src.Color;
                Check(mid != from && mid != Ui.ModeColor(m), "home: the accent is gliding (" + from + " -> " + mid + ")");
                Check(Until(delegate { return E.ModeIndex == m; }), "home: the engine is in " + Engine.ModeNames[m]);
                Settle();
                Check(src.Color == Ui.ModeColor(m) && Ui.Accent.Color == Ui.ModeColor(m), "home: the accent lands on the mode's colour");
                Check(Sel(modeSeg) == m && Math.Abs(PillX(modeSeg) - Math.Round(Cells(modeSeg)[m].Bounds.X)) < 0.01, "home: the segment's pill is on " + Engine.ModeNames[m]);
                Check(Txt(title) == Engine.ModeNames[m], "home: the title still says " + Engine.ModeNames[m] + " after the engine answered");
            }

            var fanLinks = G<LinkSeg>("fanLinks");
            Click(Links(fanLinks)[1], "home: fans Max");
            Check(Until(delegate { return E.S.Fan == FanMode.Max; }), "home: Max sets the engine's fan mode");
            Settle();
            Check(Sel(fanLinks) == 1, "home: the underline is on Max");
            Click(Links(fanLinks)[0], "home: fans Auto");
            Check(Until(delegate { return E.S.Fan == FanMode.Auto; }), "home: Auto sets the engine's fan mode");
            Settle();
            Check(Sel(fanLinks) == 0, "home: the underline is on Auto");
            Click(Links(fanLinks)[2], "home: fans Manual");
            Check(Cur == "Fans", "home: Manual opens the Fans page");
            Check(Until(delegate { return E.S.Fan == FanMode.Manual; }), "home: and the engine's fan mode is Manual");
            Settle();
            Check(G<Control>("manualBlock").IsVisible && Sel(G<Seg>("fanSeg")) == 3, "home: the Fans page shows Manual");
            Go(0);
            Check(Sel(fanLinks) == 2 && Txt((TextBlock)Links(fanLinks)[2]) == "Manual", "home: back on Home the underline is on Manual");
            Click(Links(fanLinks)[0], "home: fans Auto again");
            Until(delegate { return E.S.Fan == FanMode.Auto; });
            Settle();

            var slPower = G<Slider>("slPower");
            var txtPower = G<TextBlock>("txtPower");
            if (!G<Control>("powerRow").IsVisible) Note("the Power gain row is hidden on this simulated board");
            else {
                int max = (int)slPower.Maximum;
                Drag(slPower, 10, "home: power gain to 10");
                Check(slPower.Value == 10 && Txt(txtPower) == "+10 W", "home: the slider and its text follow the drag (" + slPower.Value + ", \"" + Txt(txtPower) + "\")");
                Check(Until(delegate { return E.S.TdpOffset == 10; }), "home: the engine has +10 W");
                Settle();
                Drag(slPower, 7, "home: power gain to 7");
                Check(slPower.Value == 5 && Txt(txtPower) == "+5 W", "home: a drag between ticks snaps to the nearest (" + slPower.Value + ")");
                Check(Until(delegate { return E.S.TdpOffset == 5; }), "home: the engine has +5 W");
                // WPF: the rail beside the thumb is not a control (its repeat buttons carry no command); only the thumb moves the value
                var track = slPower.GetVisualDescendants().OfType<Track>().First();
                ClickAt(track, new Point(track.Bounds.Width - 12, track.Bounds.Height / 2), "home: a click on the slider's rail");
                Check(slPower.Value == 5, "home: a click on the rail beside the thumb does not move it, as on Windows (" + slPower.Value + ")");
                Wait(350);
                Drag(slPower, 0, "home: power gain to 0");
                Check(Until(delegate { return E.S.TdpOffset == 0; }) && slPower.Value == 0, "home: back to +0 W (max is " + max + ")");
                Settle();
            }

            Click(G<Control>("lightRow"), "home: the Lighting row");
            Check(Cur == "Keyboard", "home: the Lighting row opens the Keyboard page");
            Settle(200);
            Go(0);
        }

    }
}
