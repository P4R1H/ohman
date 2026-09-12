// Ohman — keyboard drawing. One control renders both the small live glyph in the main panel and the editor's
// full keyboard; the layout is a list of keys in key units, zones are vertical bands (what HP's 4-zone firmware lights).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ohman {

    public sealed class KeyDef {
        public string Label; public double X, Y, W, H = 1; public int Zone; public int Index;
    }

    public static class KeyboardLayouts {
        const double Gap = 0.0;
        /// <summary>A compact laptop keyboard (the Transcend 14 shape); with numpad adds the 4-column block on the right.</summary>
        public static List<KeyDef> Build(bool numpad, int zones) {
            var keys = new List<KeyDef>();
            string[][] rows = {
                new[] { "Esc:1", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Del:1" },
                new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "⌫:2" },
                new[] { "Tab:1.5", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\:1.5" },
                new[] { "Caps:1.75", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "Enter:2.25" },
                new[] { "Shift:2.25", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "Shift:2.75" },
                new[] { "Ctrl:1.25", "Fn:1", "Win:1", "Alt:1.25", " :6", "Alt:1", "Ctrl:1", "◀:1", "▲▼:1", "▶:1" }
            };
            double y = 0; int idx = 0;
            foreach (var row in rows) {
                double x = 0; double h = y == 0 ? 0.7 : 1;
                foreach (string spec in row) {
                    string label = spec; double w = 1;
                    int c = spec.LastIndexOf(':');
                    if (c > 0) { label = spec.Substring(0, c); w = double.Parse(spec.Substring(c + 1), CultureInfo.InvariantCulture); }
                    keys.Add(new KeyDef { Label = label.Trim(), X = x, Y = y, W = w, H = h, Index = idx++ });
                    x += w + Gap;
                }
                y += h + Gap;
            }
            if (numpad) {
                double nx = 15.3; string[][] np = { new[] { "Num", "/", "*", "-" }, new[] { "7", "8", "9", "+" }, new[] { "4", "5", "6", "" }, new[] { "1", "2", "3", "Ent" }, new[] { "0:2", ".", "" } };
                double ny = 0.7;
                foreach (var row in np) { double x = nx; foreach (string spec in row) { string label = spec; double w = 1; int c = spec.LastIndexOf(':'); if (c > 0) { label = spec.Substring(0, c); w = double.Parse(spec.Substring(c + 1), CultureInfo.InvariantCulture); } if (label.Length > 0) keys.Add(new KeyDef { Label = label, X = x, Y = ny, W = w, H = 1, Index = idx++ }); x += w; } ny += 1; }
            }
            double total = 0; foreach (var k in keys) total = Math.Max(total, k.X + k.W);
            foreach (var k in keys) { double mid = (k.X + k.W / 2) / total; k.Zone = zones <= 1 ? 0 : Math.Min(zones - 1, (int)(mid * zones)); }
            return keys;
        }
    }

    /// <summary>Draws a keyboard as light on dark keys: halo under each cap, a tinted cap, a coloured edge and a bright
    /// legend; zones read through a wash over the deck and a plate around the selected bands. The same class draws the
    /// small glyph in the panel (no deck, no legends). Frames arriving during an effect are smoothed per rendered frame.</summary>
    public sealed class KeyboardView : FrameworkElement {
        public List<KeyDef> Keys = new List<KeyDef>();
        public bool Interactive, PerKey, Selectable = true, Off, WindowsOwned;
        public double Level = 1.0;                                // 0..1: the Level slider dims the drawing too
        public HashSet<int> Selected = new HashSet<int>();        // zone indices (key indices when PerKey)
        public event Action<KeyDef> KeyClicked;
        Rgb[] shown = new Rgb[0], target = new Rgb[0]; bool animating;
        int hoverZone = -1, zones = 1; double unitsW = 15.5, unitsH = 5.7;
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Dictionary<uint, SolidColorBrush> Brushes_ = new Dictionary<uint, SolidColorBrush>();
        static readonly Color Deck = Color.FromRgb(0x12, 0x15, 0x1C), DeckEdge = Color.FromRgb(0x23, 0x28, 0x33), CapTop = Color.FromRgb(0x1A, 0x1E, 0x26), CapBottom = Color.FromRgb(0x12, 0x16, 0x20),
            OffCap = Color.FromRgb(0x1A, 0x1E, 0x26), OffEdge = Color.FromRgb(0x20, 0x24, 0x2D), OffLegend = Color.FromRgb(0x5C, 0x63, 0x73), WinLegend = Color.FromRgb(0x6E, 0x77, 0x89), Accent = Color.FromRgb(0x5B, 0x8D, 0xEF), MiniCap = Color.FromRgb(0x19, 0x1D, 0x25);

        public Rgb[] ZoneColors { get { return shown; } set { SetColors(value, false); } }
        /// <summary>animate = true eases the drawing toward the new colours (35 % per rendered frame) instead of jumping.</summary>
        public void SetColors(Rgb[] c, bool animate) {
            if (c == null) c = new Rgb[0];
            if (!animate || shown.Length != c.Length) { shown = (Rgb[])c.Clone(); target = (Rgb[])c.Clone(); StopAnim(); InvalidateVisual(); return; }
            target = (Rgb[])c.Clone();
            if (!animating) { animating = true; CompositionTarget.Rendering += Tick; }
        }
        void Tick(object o, EventArgs e) {
            bool done = true;
            for (int i = 0; i < shown.Length; i++) {
                var a = shown[i]; var b = target[i];
                shown[i] = new Rgb(Ease(a.R, b.R), Ease(a.G, b.G), Ease(a.B, b.B));
                if (shown[i].R != b.R || shown[i].G != b.G || shown[i].B != b.B) done = false;
            }
            InvalidateVisual();
            if (done) StopAnim();
        }
        static byte Ease(byte a, byte b) { int d = b - a; if (Math.Abs(d) <= 1) return b; return (byte)(a + d * 0.35); }
        void StopAnim() { if (animating) { animating = false; CompositionTarget.Rendering -= Tick; } }

        public void SetLayout(List<KeyDef> keys) {
            Keys = keys; unitsW = 0; unitsH = 0; zones = 1;
            foreach (var k in keys) { unitsW = Math.Max(unitsW, k.X + k.W); unitsH = Math.Max(unitsH, k.Y + k.H); zones = Math.Max(zones, k.Zone + 1); }
            InvalidateMeasure(); InvalidateVisual();
        }
        public void Repaint() { InvalidateVisual(); }

        double Pad { get { return Interactive ? 15 : 5; } }
        double Gap { get { return Interactive ? 3 : 1.2; } }
        protected override Size MeasureOverride(Size a) {
            double w = double.IsInfinity(a.Width) ? 480 : a.Width;
            return new Size(w, (w - 2 * Pad) / unitsW * unitsH + 2 * Pad);
        }
        Rect KeyRect(KeyDef k, double u) { double p = Pad, g = Gap; return new Rect(p + k.X * u + g / 2, p + k.Y * u + g / 2, Math.Max(1, k.W * u - g), Math.Max(1, k.H * u - g)); }
        /// <summary>The band of keys belonging to a zone, inflated into a plate.</summary>
        Rect Band(int zone, double u) {
            double minX = double.MaxValue, maxX = 0;
            foreach (var k in Keys) if (k.Zone == zone) { minX = Math.Min(minX, k.X); maxX = Math.Max(maxX, k.X + k.W); }
            if (minX == double.MaxValue) return Rect.Empty;
            return new Rect(Pad + minX * u - 5, Pad - 5, (maxX - minX) * u + 10, unitsH * u + 10);
        }

        static SolidColorBrush B(Color c) {
            uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B); SolidColorBrush b;
            if (!Brushes_.TryGetValue(key, out b)) { b = new SolidColorBrush(c); b.Freeze(); Brushes_[key] = b; }
            return b;
        }
        static Color WithA(Color c, double a) { return Color.FromArgb((byte)Math.Round(Math.Max(0, Math.Min(1, a)) * 255), c.R, c.G, c.B); }
        static Color Blend(Color a, Color b, double t) { t = Math.Max(0, Math.Min(1, t)); return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t)); }
        static Color ToColor(Rgb c) { return Color.FromRgb(c.R, c.G, c.B); }
        Color ZoneColor(KeyDef k) { int i = PerKey ? k.Index : k.Zone; return ToColor(shown[Math.Min(shown.Length - 1, Math.Max(0, i))]); }
        LinearGradientBrush Wash(double alpha) {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            int n = Math.Max(1, Math.Min(zones, shown.Length));
            for (int i = 0; i < n; i++) g.GradientStops.Add(new GradientStop(WithA(ToColor(shown[i]), alpha), n == 1 ? 0.5 : (i + 0.5) / n));
            g.Freeze(); return g;
        }

        protected override void OnRender(DrawingContext dc) {
            double W = ActualWidth, u = (W - 2 * Pad) / unitsW, fieldH = unitsH * u, H = fieldH + 2 * Pad;
            bool lit = !Off && !WindowsOwned && shown.Length > 0;
            double lv = 0.40 + 0.60 * Math.Max(0, Math.Min(1, Level));
            if (!Interactive) {
                // the panel glyph: no frame, no legends; caps carry the colour, one soft halo around the field
                if (lit) dc.DrawRoundedRectangle(Wash(0.18), null, new Rect(Pad - 4, Pad - 4, W - 2 * Pad + 8, fieldH + 8), 6, 6);
                foreach (var k in Keys) {
                    var r = KeyRect(k, u);
                    dc.DrawRoundedRectangle(B(lit ? Blend(MiniCap, ZoneColor(k), 0.55 * lv) : OffCap), null, r, 2, 2);
                }
                return;
            }
            // deck
            dc.DrawRoundedRectangle(B(Deck), new Pen(B(DeckEdge), 1), new Rect(0.5, 0.5, W - 1, H - 1), 14, 14);
            dc.DrawLine(new Pen(B(WithA(Colors.White, 0.04)), 1), new Point(14, 1.5), new Point(W - 14, 1.5));
            if (WindowsOwned) dc.DrawRoundedRectangle(null, new Pen(B(WithA(Accent, 0.35)), 1), new Rect(2.5, 2.5, W - 5, H - 5), 12, 12);
            if (lit) dc.DrawRoundedRectangle(Wash(0.10 * lv), null, new Rect(1, 1, W - 2, H - 2), 13, 13);
            // zone plates: the selection as one plate per run of adjacent zones, the hovered zone as a faint plate
            bool anySel = Selectable && Selected.Count > 0 && !PerKey;
            if (Selectable && zones > 1 && !PerKey) {
                int z = 0;
                while (z < zones) {
                    if (!Selected.Contains(z)) { z++; continue; }
                    int from = z; while (z + 1 < zones && Selected.Contains(z + 1)) z++;
                    var a = Band(from, u); var b = Band(z, u);
                    var plate = new Rect(a.X, a.Y, b.Right - a.X, a.Height);
                    dc.DrawRoundedRectangle(B(WithA(Colors.White, 0.05)), new Pen(B(WithA(Colors.White, 0.20)), 1), plate, 9, 9);
                    z++;
                }
                if (hoverZone >= 0 && !Selected.Contains(hoverZone)) dc.DrawRoundedRectangle(null, new Pen(B(WithA(Colors.White, 0.10)), 1), Band(hoverZone, u), 9, 9);
            }
            // keys
            foreach (var k in Keys) {
                var r = KeyRect(k, u);
                Color legend;
                if (lit) {
                    Color c = ZoneColor(k);
                    bool dim = anySel && !Selected.Contains(k.Zone), hov = Selectable && hoverZone == k.Zone;
                    double halo = lv * (dim ? 0.45 : 1) * (hov ? 1.3 : 1), tint = lv * (dim ? 0.70 : 1);
                    var h1 = r; h1.Inflate(4, 4); dc.DrawRoundedRectangle(B(WithA(c, 0.045 * halo)), null, h1, 9, 9);
                    var h2 = r; h2.Inflate(2.5, 2.5); dc.DrawRoundedRectangle(B(WithA(c, 0.08 * halo)), null, h2, 7.5, 7.5);
                    var h3 = r; h3.Inflate(1, 1); dc.DrawRoundedRectangle(B(WithA(c, 0.13 * halo)), null, h3, 6, 6);
                    var cap = new LinearGradientBrush(Blend(CapTop, c, 0.30 * tint), Blend(CapBottom, c, 0.12 * tint), 90); cap.Freeze();
                    dc.DrawRoundedRectangle(cap, new Pen(B(WithA(c, 0.55 * lv)), 1), r, 5, 5);
                    legend = WithA(Blend(c, Colors.White, 0.62), 0.92 * lv);
                } else {
                    dc.DrawRoundedRectangle(B(OffCap), new Pen(B(OffEdge), 1), r, 5, 5);
                    legend = WindowsOwned ? WinLegend : OffLegend;
                }
                string label = k.Label.Trim();
                if (label.Length > 0 && r.Width >= 16 && r.Height >= 14) {
                    double size = label.Length == 1 ? 9 : label.Length <= 3 ? 8 : 7.5;
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, B(legend), 1.0);
                    dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
                }
            }
        }

        KeyDef Hit(Point p) {
            double u = (ActualWidth - 2 * Pad) / unitsW;
            foreach (var k in Keys) if (KeyRect(k, u).Contains(p)) return k;
            return null;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if (!Interactive) return;
            var k = Selectable ? Hit(e.GetPosition(this)) : null; int z = k == null ? -1 : (PerKey ? k.Index : k.Zone);
            if (z != hoverZone) { hoverZone = z; Cursor = k == null ? Cursors.Arrow : Cursors.Hand; InvalidateVisual(); }
        }
        protected override void OnMouseLeave(MouseEventArgs e) { if (hoverZone != -1) { hoverZone = -1; InvalidateVisual(); } }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            var k = Hit(e.GetPosition(this));
            var h = KeyClicked; if (h != null) h(k);
        }
    }
}
