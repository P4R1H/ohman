// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: keyboard drawing. One control renders both the small glyph on the Home page and the Keyboard page's map:
// flat coloured keys, nothing more. The layouts it draws are shared and live in src\Keyboard.cs.
// The Avalonia port of src\KeyboardView.cs, kept member for member so a change there can be carried over from a diff.
//
// Differences from the WPF original
//  - FrameworkElement is Control; OnRender is Render; ActualWidth is Bounds.Width; the Mouse* overrides are the
//    Pointer* ones. Public members are unchanged.
//  - The effect smoothing listens to Ui.Rendering (CompositionTarget.Rendering): 35 % per frame, at 60 frames a
//    second, where WPF ran at the display's refresh rate.
//  - The legends ⌫ ◀ ▲▼ ▶ are not in IBM Plex Mono. Windows took them from Segoe UI Symbol; here they come from
//    whatever the system's font fallback offers (DejaVu Sans on most desktops), so their shape differs slightly.
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Ohman {

    /// <summary>Flat keys in their zone colour; legends on the page map, none on the glyph. The selection is a set of
    /// key indices the page fills in (one key, a row, a zone or the whole board); hovering a group steps the rest back.
    /// Frames arriving during an effect are smoothed per rendered frame.</summary>
    public sealed class KeyboardView : Control {
        public List<KeyDef> Keys = new List<KeyDef>();
        public bool Interactive, Selectable = true, Off, WindowsOwned;
        public bool Smooth;                                       // effects: blend between the zone colours across the board instead of four hard bands
        public double Level = 1.0;                                // 0..1: the brightness slider dims the drawing too
        public double Gap = 5, RowPitch = 39;                     // px between keys; vertical pitch per key row
        public readonly HashSet<int> Selected = new HashSet<int>();   // key indices being edited
        public readonly HashSet<int> Hover = new HashSet<int>();      // key indices in the group under the pointer
        public event Action<KeyDef> KeyClicked;
        public event Action<KeyDef> KeyHovered;                   // null when the pointer leaves the board
        Rgb[] shown = new Rgb[0], target = new Rgb[0];
        bool animating;
        int zones = 1;
        double unitsW = 15.5, unitsH = 6;
        static Typeface face;
        static bool faced;
        static readonly Dictionary<uint, ImmutableSolidColorBrush> Brushes_ = new Dictionary<uint, ImmutableSolidColorBrush>();
        static readonly Color OffCap = Color.FromRgb(0x1F, 0x1C, 0x1A), OffLegend = Color.FromRgb(0x84, 0x7F, 0x7B),
            WinLegend = Color.FromRgb(0xA2, 0x9D, 0x99), MiniOff = Color.FromRgb(0x2B, 0x28, 0x26), Card = Color.FromRgb(0x16, 0x13, 0x11);

        /// <summary>animate = true eases the drawing toward the new colours (35 % per rendered frame) instead of jumping.</summary>
        public void SetColors(Rgb[] c, bool animate) {
            if (c == null) c = new Rgb[0];
            if (!animate || shown.Length != c.Length) { shown = (Rgb[])c.Clone(); target = (Rgb[])c.Clone(); StopAnim(); InvalidateVisual(); return; }
            target = (Rgb[])c.Clone();
            if (!animating) { animating = true; Ui.Rendering += Tick; }
        }
        void Tick(object o, EventArgs e) {
            bool done = true;
            for (int i = 0; i < shown.Length; i++) {
                var a = shown[i];
                var b = target[i];
                shown[i] = new Rgb(Ease(a.R, b.R), Ease(a.G, b.G), Ease(a.B, b.B));
                if (shown[i].R != b.R || shown[i].G != b.G || shown[i].B != b.B) done = false;
            }
            InvalidateVisual();
            if (done) StopAnim();
        }
        static byte Ease(byte a, byte b) { int d = b - a; if (Math.Abs(d) <= 1) return b; return (byte)(a + d * 0.35); }
        void StopAnim() { if (animating) { animating = false; Ui.Rendering -= Tick; } }

        public void SetLayout(List<KeyDef> keys) {
            Keys = keys;
            unitsW = 0;
            unitsH = 0;
            zones = 1;
            foreach (var k in keys) { unitsW = Math.Max(unitsW, k.X + k.W); unitsH = Math.Max(unitsH, k.Y + k.H); zones = Math.Max(zones, k.Zone + 1); }
            InvalidateMeasure();
            InvalidateVisual();
        }
        public void Repaint() { InvalidateVisual(); }

        protected override Size MeasureOverride(Size a) {
            double w = double.IsInfinity(a.Width) ? 590 : a.Width;
            return new Size(w, unitsH * RowPitch - Gap);
        }
        double ActualWidth { get { return Bounds.Width; } }
        double ActualHeight { get { return Bounds.Height; } }
        double Unit { get { return (ActualWidth + Gap) / unitsW; } }
        Rect KeyRect(KeyDef k) { double u = Unit; return new Rect(k.X * u, k.Y * RowPitch, Math.Max(1, k.W * u - Gap), Math.Max(1, k.H * RowPitch - Gap)); }

        static ImmutableSolidColorBrush B(Color c) {
            uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
            ImmutableSolidColorBrush b;
            if (Brushes_.TryGetValue(key, out b)) return b;
            if (Brushes_.Count > 2048) Brushes_.Clear();     // an effect paints a new colour every frame; the cache is a
            b = new ImmutableSolidColorBrush(c);             // frame-to-frame saving, not somewhere to keep hours of them
            Brushes_[key] = b;
            return b;
        }
        static Color WithA(Color c, double a) { return Color.FromArgb((byte)Math.Round(Math.Max(0, Math.Min(1, a)) * 255), c.R, c.G, c.B); }
        static Color Scale(Color c, double f) { f = Math.Max(0, Math.Min(1, f)); return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)); }
        static Color ToColor(Rgb c) { return Color.FromRgb(c.R, c.G, c.B); }
        static Color Mix(Color a, Color b, double t) {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
        Color ZoneColor(KeyDef k) {
            if (shown.Length == 0) return OffCap;
            if (Smooth && shown.Length > 1) return SmoothColor(k);
            return ToColor(shown[Math.Min(shown.Length - 1, Math.Max(0, k.Zone))]);
        }
        /// <summary>The colour a key shows when the zones are blended: the two nearest zone centres, mixed by how far
        /// across the board the key sits. Four bands read as one sweep, which is what a wave actually looks like.</summary>
        Color SmoothColor(KeyDef k) {
            // Only a four-zone board has bands to blend; a per-key board already has a colour per key (blending it
            // painted every key in lamp 0's colour). WASD sits inside the left band and keeps its own colour, and the
            // sweep runs over the three bands across the board: before, WASD's colour was drawn as a fourth band on
            // the right-hand keys, where the keyboard never shows it (#87).
            if (zones != 4 || shown.Length < 4) return ToColor(shown[Math.Min(shown.Length - 1, Math.Max(0, k.Zone))]);
            if (k.Zone == KeyboardLayouts.ZoneWasd) return ToColor(shown[KeyboardLayouts.ZoneWasd]);
            int[] order = { KeyboardLayouts.ZoneLeft, KeyboardLayouts.ZoneMiddle, KeyboardLayouts.ZoneRight };
            const int n = 3;
            double t = (k.X + k.W / 2) / unitsW * n - 0.5;                       // position in zone-centre units
            int i0 = (int)Math.Floor(t), i1 = i0 + 1;
            double f = t - i0;
            i0 = Math.Max(0, Math.Min(n - 1, i0));
            i1 = Math.Max(0, Math.Min(n - 1, i1));
            return Mix(ToColor(shown[order[i0]]), ToColor(shown[order[i1]]), f);
        }
        static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0; }

        public override void Render(DrawingContext dc) {
            if (!faced) { face = new Typeface(Ui.MonoFont, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal); faced = true; }
            // A control is only hit-testable where it has drawn something, so without this the gaps
            // between the keys are not part of the control: the pointer crossing one counts as leaving
            // altogether, PointerExited fires, and the hover drops. Transparent is hit-testable; null is not.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            bool lit = !Off && !WindowsOwned && shown.Length > 0;
            double lv = 0.35 + 0.65 * Math.Max(0, Math.Min(1, Level));
            double radius = Interactive ? 4 : 2;
            bool anyHover = Interactive && Hover.Count > 0, anySel = Interactive && Selected.Count > 0;
            foreach (var k in Keys) {
                var r = KeyRect(k);
                bool sel = anySel && Selected.Contains(k.Index), hov = anyHover && Hover.Contains(k.Index);
                Color fill, legend;
                if (lit) {
                    fill = Scale(ZoneColor(k), lv);
                    legend = Luma(fill) > 0.62 ? Color.FromArgb(0x80, 0, 0, 0) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
                } else {
                    fill = Interactive ? OffCap : MiniOff;
                    legend = WindowsOwned ? WinLegend : OffLegend;
                }
                // keys outside the group under the pointer step back rather than disappear
                if (anyHover && !hov && !sel) { fill = Mix(Card, fill, 0.32); legend = WithA(legend, 0.45); }
                dc.DrawRectangle(B(fill), null, r, radius, radius);
                if (!Interactive) continue;
                if (sel) dc.DrawRectangle(null, new Pen(B(Color.FromRgb(0xFC, 0xFA, 0xF9)), 2), new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), radius, radius);
                else if (hov) dc.DrawRectangle(null, new Pen(B(WithA(Colors.White, 0.5)), 2), new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), radius, radius);
                else dc.DrawRectangle(null, new Pen(B(WithA(Colors.Black, 0.22)), 1), new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1)), radius, radius);
                string label = k.Label.Trim();
                if (label.Length > 0 && r.Width >= 14 && DrawSymbol(dc, label, r, B(legend))) continue;
                if (label.Length > 0 && r.Width >= 14) {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, B(legend));
                    dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
                }
            }
        }

        /// <summary>The four legends that are not letters (backspace and the arrows). IBM Plex Mono has none of
        /// them, so as text they would come from whatever fallback font the distro happens to have, in whatever
        /// size that font draws them, or as an empty box. Drawn as shapes they are the same everywhere, sized to
        /// what Windows shows. False for every other legend.</summary>
        static bool DrawSymbol(DrawingContext dc, string label, Rect r, IBrush ink) {
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + 0.5;
            if (label == "◀" || label == "▶") {                       // left, right
                double s = label == "◀" ? -1 : 1;
                dc.DrawGeometry(ink, null, Tri(cx - s * 3.4, cy - 4.2, cx - s * 3.4, cy + 4.2, cx + s * 3.6, cy));
                return true;
            }
            if (label == "▲▼") {                                      // up and down share one key
                dc.DrawGeometry(ink, null, Tri(cx - 5.6, cy + 3.0, cx, cy + 3.0, cx - 2.8, cy - 2.2));
                dc.DrawGeometry(ink, null, Tri(cx, cy - 2.2, cx + 5.6, cy - 2.2, cx + 2.8, cy + 3.0));
                return true;
            }
            if (label == "⌫") {                                            // backspace: a tag pointing left with a cross in it
                var g = new StreamGeometry();
                using (var c = g.Open()) {
                    c.BeginFigure(new Point(cx - 5.8, cy), false);
                    c.LineTo(new Point(cx - 2.3, cy - 3.4)); c.LineTo(new Point(cx + 5.2, cy - 3.4));
                    c.LineTo(new Point(cx + 5.2, cy + 3.4)); c.LineTo(new Point(cx - 2.3, cy + 3.4));
                    c.EndFigure(true);
                }
                var pen = new Pen(ink, 0.8);
                dc.DrawGeometry(null, pen, g);
                dc.DrawLine(pen, new Point(cx - 0.2, cy - 1.7), new Point(cx + 3.2, cy + 1.7));
                dc.DrawLine(pen, new Point(cx - 0.2, cy + 1.7), new Point(cx + 3.2, cy - 1.7));
                return true;
            }
            return false;
        }

        static StreamGeometry Tri(double x1, double y1, double x2, double y2, double x3, double y3) {
            var g = new StreamGeometry();
            using (var c = g.Open()) {
                c.BeginFigure(new Point(x1, y1), true);
                c.LineTo(new Point(x2, y2)); c.LineTo(new Point(x3, y3));
                c.EndFigure(true);
            }
            return g;
        }

        const double Snap = 11;        // a little over the gap between keys, and between rows

        /// <summary>The key under the pointer, or the nearest one within Snap. The gaps are five pixels wide, and
        /// treating them as empty meant dragging across the board dropped the hover between every pair of keys, so
        /// the whole group flicked off and on again. A point in a gap belongs to whoever is closest. It also means
        /// there are no dead clicks: clicking a gap selects the key you were obviously aiming at.</summary>
        KeyDef Hit(Point p) {
            KeyDef best = null;
            double bestD = double.MaxValue;
            foreach (var k in Keys) {
                Rect r = KeyRect(k);
                if (r.Contains(p)) return k;
                double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
                double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = k; }
            }
            return bestD <= Snap * Snap ? best : null;   // past the edge of the board, nothing is hovered
        }
        KeyDef hovered;
        protected override void OnPointerMoved(PointerEventArgs e) {
            if (!Interactive || !Selectable) return;
            var k = Hit(e.GetPosition(this));
            if (k == hovered) return;
            hovered = k;
            Cursor = k == null ? Cursors.Arrow : Cursors.Hand;
            var h = KeyHovered;
            if (h != null) h(k);
        }
        protected override void OnPointerExited(PointerEventArgs e) {
            if (!Interactive || hovered == null) return;
            hovered = null;
            var h = KeyHovered;
            if (h != null) h(null);
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e) {
            if (!Interactive || !Selectable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var k = Hit(e.GetPosition(this));
            var h = KeyClicked;
            if (h != null) h(k);
        }
    }
}
