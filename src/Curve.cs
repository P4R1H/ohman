// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the fan curve graph: seven points on a temperature axis, dragged vertically. The y axis is percent of the
// fan's top speed. What the chip reads right now is a dot on the curve with a label above it, so the number the fans
// are following is always legible. Read-only it shows a curve without handles (Auto and Max).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ohman {

    public sealed class CurveView : FrameworkElement {
        public int[] Temps = { 30, 40, 50, 60, 70, 80, 90 };
        public int[] Levels = { 18, 20, 24, 30, 38, 48, 57 };
        public int Floor = 18, Ceiling = 57, UserFloor = 0;   // UserFloor > Floor: the lowest level the curve may drive (drawn as a line)
        public bool ReadOnly;
        public double LiveTemp = double.NaN; public int LiveLevel = -1;
        public event Action<int[]> Changed;          // fires while dragging (the caller debounces) and on release
        int drag = -1, hover = -1;
        const double PlotH = 196, PillH = 20, LabelW = 22, LabelGap = 8, AxisGap = 10, AxisH = 14;
        static Typeface face;
        static readonly Brush GridH = Ui.Brush("#292623"), GridV = Ui.Brush("#211D1A"), Axis = Ui.Brush("#847F7B"),
            Live = Ui.Brush("#847F7B"), Bg = Ui.Brush("#161311"), PillBg = Ui.Brush("#312D2A"), PillFg = Ui.Brush("#EDEAE8");

        protected override Size MeasureOverride(Size a) { return new Size(double.IsInfinity(a.Width) ? 400 : a.Width, PillH + PlotH + AxisGap + AxisH); }
        double X0 { get { return LabelW + LabelGap; } }
        double X1 { get { return ActualWidth - 7; } }
        double X(double t) { return X0 + (t - Temps[0]) / (double)(Temps[Temps.Length - 1] - Temps[0]) * (X1 - X0); }
        double Y(double lvl) { return PillH + PlotH * (1 - lvl / (double)Ceiling); }          // the axis is 0..100 % of top speed
        int LevelAt(double y) { return (int)Math.Round((1 - (y - PillH) / PlotH) * Ceiling); }
        public void Repaint() { InvalidateVisual(); }
        public CurveView() { IsVisibleChanged += delegate { Animate(IsVisible); }; }

        // the live dot breathes once every two seconds, so a still screenshot and a running app look the same but the
        // running one shows the reading is alive
        bool pulsing; TimeSpan pulseStart;
        void Animate(bool on) {
            if (on == pulsing) return;
            pulsing = on;
            if (on) { pulseStart = TimeSpan.Zero; CompositionTarget.Rendering += Pulse; } else CompositionTarget.Rendering -= Pulse;
        }
        double pulseT;
        void Pulse(object o, EventArgs e) {
            var re = e as RenderingEventArgs; if (re == null) return;
            if (pulseStart == TimeSpan.Zero) pulseStart = re.RenderingTime;
            double t = (re.RenderingTime - pulseStart).TotalSeconds % 2.2;
            if (LiveLevel <= 0 || double.IsNaN(LiveTemp)) return;
            pulseT = t; InvalidateVisual();
        }

        FormattedText Text(string s, double size, Brush b) { return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, b, 1.0); }

        protected override void OnRender(DrawingContext dc) {
            if (face == null) face = new Typeface(Ui.MonoFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Color ac = Ui.Accent.Color; var accent = Ui.Accent;
            var gridH = new Pen(GridH, 1); var gridV = new Pen(GridV, 1);
            foreach (int pct in new[] { 100, 75, 50, 25, 0 }) {
                double y = Math.Round(Y(Ceiling * pct / 100.0)) + 0.5;
                dc.DrawLine(gridH, new Point(X0, y), new Point(X1, y));
                var ft = Text(pct.ToString(CultureInfo.InvariantCulture), 10, Axis);
                dc.DrawText(ft, new Point(LabelW - ft.Width, Math.Min(PillH + PlotH - ft.Height, Math.Max(PillH, y - ft.Height / 2))));
            }
            for (int i = 0; i < Temps.Length; i++) {
                double x = Math.Round(X(Temps[i])) + 0.5;
                dc.DrawLine(gridV, new Point(x, PillH), new Point(x, PillH + PlotH));
                var ft = Text(Temps[i] + "°", 10.5, Axis);
                double tx = i == 0 ? X0 : i == Temps.Length - 1 ? X1 - ft.Width : x - ft.Width / 2;
                dc.DrawText(ft, new Point(tx, PillH + PlotH + AxisGap));
            }
            // the curve: a flat fill below it, then the line
            var geo = new StreamGeometry();
            using (var g = geo.Open()) {
                g.BeginFigure(new Point(X0, PillH + PlotH), true, true);
                for (int i = 0; i < Temps.Length; i++) g.LineTo(new Point(X(Temps[i]), Y(Levels[i])), false, false);
                g.LineTo(new Point(X1, PillH + PlotH), false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(Ui.Brush(Color.FromArgb(0x1F, ac.R, ac.G, ac.B)), null, geo);
            var linePen = new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 1; i < Temps.Length; i++) dc.DrawLine(linePen, new Point(X(Temps[i - 1]), Y(Levels[i - 1])), new Point(X(Temps[i]), Y(Levels[i])));
            if (UserFloor > Floor) {
                double y = Math.Round(Y(Math.Min(Ceiling, UserFloor))) + 0.5;
                dc.DrawLine(new Pen(Ui.Brush(Color.FromArgb(0x90, ac.R, ac.G, ac.B)), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }, new Point(X0, y), new Point(X1, y));
            }
            if (!ReadOnly)
                for (int i = 0; i < Temps.Length; i++) {
                    double r = (i == drag || i == hover) ? 6 : 4.5;
                    dc.DrawEllipse(Bg, new Pen(accent, 2.5), new Point(X(Temps[i]), Y(Levels[i])), r, r);
                }
            // where the chips are right now: a line down the graph, a dot on the curve, and the reading above it
            if (double.IsNaN(LiveTemp) || LiveLevel <= 0) return;
            double lx = Math.Round(X(Math.Max(Temps[0], Math.Min(Temps[Temps.Length - 1], LiveTemp)))) + 0.5;
            double ly = Y(Math.Max(Floor, Math.Min(Ceiling, LiveLevel)));
            dc.DrawLine(new Pen(Live, 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) }, new Point(lx, PillH), new Point(lx, PillH + PlotH));
            if (pulsing) {
                double k = Math.Min(1, pulseT / 1.1);                                   // one ring every 2.2 s, gone by half way
                if (k < 1) dc.DrawEllipse(null, new Pen(Ui.Brush(Color.FromArgb((byte)(0x50 * (1 - k)), ac.R, ac.G, ac.B)), 1.5), new Point(lx, ly), 5 + 9 * k, 5 + 9 * k);
            }
            dc.DrawEllipse(accent, new Pen(Ui.Brush("#FCFAF9"), 2), new Point(lx, ly), 4, 4);
            var label = Text((int)Math.Round(LiveTemp) + "° → " + (int)Math.Round(100.0 * LiveLevel / Ceiling) + "%", 10, PillFg);
            double pw = label.Width + 14, px = Math.Max(X0, Math.Min(X1 - pw, lx - pw / 2));
            dc.DrawRoundedRectangle(PillBg, null, new Rect(px, 0, pw, 16), 5, 5);
            dc.DrawText(label, new Point(px + 7, (16 - label.Height) / 2));
        }

        int HandleAt(Point p) {
            if (ReadOnly) return -1;
            for (int i = 0; i < Temps.Length; i++) if (Math.Abs(p.X - X(Temps[i])) <= 13 && Math.Abs(p.Y - Y(Levels[i])) <= 40) return i;
            return -1;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            var p = e.GetPosition(this);
            if (drag >= 0 && e.LeftButton == MouseButtonState.Pressed) {
                int lvl = Math.Max(Floor, Math.Min(Ceiling, LevelAt(p.Y)));
                if (lvl != Levels[drag]) {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) {                  // shift-drag moves the whole curve
                        int d = lvl - Levels[drag];
                        for (int i = 0; i < Levels.Length; i++) Levels[i] = Math.Max(Floor, Math.Min(Ceiling, Levels[i] + d));
                    } else {
                        Levels[drag] = lvl;
                        for (int i = drag + 1; i < Levels.Length; i++) if (Levels[i] < lvl) Levels[i] = lvl;     // a fan curve never falls as it gets hotter
                        for (int i = drag - 1; i >= 0; i--) if (Levels[i] > lvl) Levels[i] = lvl;
                    }
                    InvalidateVisual(); var h = Changed; if (h != null) h((int[])Levels.Clone());
                }
                return;
            }
            int hv = HandleAt(p);
            if (hv != hover) { hover = hv; Cursor = hv >= 0 ? Cursors.Hand : Cursors.Arrow; InvalidateVisual(); }
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            drag = HandleAt(e.GetPosition(this)); if (drag >= 0) { CaptureMouse(); InvalidateVisual(); }
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if (drag >= 0) { drag = -1; ReleaseMouseCapture(); InvalidateVisual(); var h = Changed; if (h != null) h((int[])Levels.Clone()); }
        }
        protected override void OnMouseLeave(MouseEventArgs e) { if (hover != -1 && drag < 0) { hover = -1; InvalidateVisual(); } }
    }
}
