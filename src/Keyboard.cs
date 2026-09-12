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

    /// <summary>Draws a keyboard with each key in its zone's (or its own) colour. Interactive instances report clicks.</summary>
    public sealed class KeyboardView : FrameworkElement {
        public List<KeyDef> Keys = new List<KeyDef>();
        public Rgb[] ZoneColors = new Rgb[0];
        public bool Off, Interactive, PerKey;
        public HashSet<int> Selected = new HashSet<int>();       // zone indices (or key indices when PerKey)
        public event Action<KeyDef> KeyClicked;
        int hover = -1; double unitsW = 15, unitsH = 5.7;
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public void SetLayout(List<KeyDef> keys) {
            Keys = keys; unitsW = 0; unitsH = 0;
            foreach (var k in keys) { unitsW = Math.Max(unitsW, k.X + k.W); unitsH = Math.Max(unitsH, k.Y + k.H); }
            InvalidateMeasure(); InvalidateVisual();
        }
        public void Repaint() { InvalidateVisual(); }

        protected override Size MeasureOverride(Size a) {
            double w = double.IsInfinity(a.Width) ? 480 : a.Width;
            double pad = Interactive ? 12 : 3;
            return new Size(w, (w - 2 * pad) / unitsW * unitsH + 2 * pad);
        }

        Rect KeyRect(KeyDef k, double scale, double pad, double gap) {
            return new Rect(pad + k.X * scale + gap, pad + k.Y * scale + gap, Math.Max(1, k.W * scale - 2 * gap), Math.Max(1, k.H * scale - 2 * gap));
        }

        static Color Mix(Color a, Color b, double t) { return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t)); }
        static Color WithA(Color c, double a) { return Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B); }
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Color ToColor(Rgb c) { return Color.FromRgb(c.R, c.G, c.B); }

        protected override void OnRender(DrawingContext dc) {
            double pad = Interactive ? 12 : 3, gap = Interactive ? 2.2 : 0.6;
            double scale = (ActualWidth - 2 * pad) / unitsW;
            double h = unitsH * scale + 2 * pad;
            if (!Interactive) {
                // the small glyph: flat colour blocks read best at this size
                dc.DrawRoundedRectangle(Ui.Brush("#141820"), new Pen(Ui.Brush("#1F232C"), 1), new Rect(0.5, 0.5, ActualWidth - 1, h - 1), 4, 4);
                foreach (var k in Keys) {
                    var r = KeyRect(k, scale, pad, gap);
                    Color fill = Off || ZoneColors.Length == 0 ? Color.FromRgb(0x2A, 0x2F, 0x3A) : ToColor(ZoneColors[Math.Min(ZoneColors.Length - 1, k.Zone)]);
                    dc.DrawRoundedRectangle(B(fill), null, r, 1.2, 1.2);
                }
                return;
            }
            // the editor: a dark deck, dark keycaps, and the zone colour as the light under and through each cap
            var deck = new LinearGradientBrush(Color.FromRgb(0x14, 0x18, 0x20), Color.FromRgb(0x0F, 0x12, 0x18), 90); deck.Freeze();
            dc.DrawRoundedRectangle(deck, new Pen(Ui.Brush("#232833"), 1), new Rect(0.5, 0.5, ActualWidth - 1, h - 1), 12, 12);
            Color capTop = Color.FromRgb(0x23, 0x27, 0x30), capBottom = Color.FromRgb(0x18, 0x1C, 0x23), offLegend = Color.FromRgb(0x55, 0x5C, 0x6B);
            foreach (var k in Keys) {
                var r = KeyRect(k, scale, pad, gap);
                bool lit = !Off && ZoneColors.Length > 0;
                Color zone = lit ? ToColor(ZoneColors[Math.Min(ZoneColors.Length - 1, PerKey ? Math.Min(k.Index, ZoneColors.Length - 1) : k.Zone)]) : offLegend;
                bool sel = Selected.Contains(PerKey ? k.Index : k.Zone), hov = k.Index == hover;
                if (lit) {
                    // underglow: two soft layers around the cap
                    var g1 = r; g1.Inflate(5, 5); dc.DrawRoundedRectangle(B(WithA(zone, 0.13)), null, g1, 7, 7);
                    var g2 = r; g2.Inflate(2.2, 2.2); dc.DrawRoundedRectangle(B(WithA(zone, 0.28)), null, g2, 5.5, 5.5);
                }
                var cap = new LinearGradientBrush(capTop, capBottom, 90); cap.Freeze();
                Pen edge = new Pen(B(lit ? WithA(zone, sel ? 0.95 : hov ? 0.8 : 0.55) : WithA(offLegend, sel ? 0.9 : 0.35)), sel ? 1.5 : 1);
                dc.DrawRoundedRectangle(cap, edge, r, 4, 4);
                if (sel) { var s2 = r; s2.Inflate(3.2, 3.2); dc.DrawRoundedRectangle(null, new Pen(B(WithA(Colors.White, 0.35)), 1), s2, 6, 6); }
                if (k.Label.Length > 0 && r.Width > 14) {
                    // legends glow in the zone colour, the way a backlit keyboard shows them
                    Color legend = lit ? Mix(zone, Colors.White, 0.42) : offLegend;
                    var ft = new FormattedText(k.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, k.Label.Length > 3 ? 8 : k.Label.Length > 1 ? 9 : 10, B(legend), 1.0);
                    dc.DrawText(ft, new Point(r.X + 5, r.Y + 3.5));
                }
            }
        }

        KeyDef Hit(Point p) {
            double pad = Interactive ? 12 : 3, gap = Interactive ? 2.2 : 0.6;
            double scale = (ActualWidth - 2 * pad) / unitsW;
            foreach (var k in Keys) if (KeyRect(k, scale, pad, gap).Contains(p)) return k;
            return null;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if (!Interactive) return;
            var k = Hit(e.GetPosition(this)); int h = k == null ? -1 : k.Index;
            if (h != hover) { hover = h; Cursor = k == null ? Cursors.Arrow : Cursors.Hand; InvalidateVisual(); }
        }
        protected override void OnMouseLeave(MouseEventArgs e) { if (hover != -1) { hover = -1; InvalidateVisual(); } }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            var k = Hit(e.GetPosition(this));
            var h = KeyClicked; if (h != null) h(k);      // null = clicked the chassis (main-panel glyph opens the editor)
        }
    }
}
