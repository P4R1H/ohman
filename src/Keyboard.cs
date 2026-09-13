// Ohman — keyboard drawing. One control renders both the small glyph on the Home page and the Keyboard page's map:
// flat coloured keys, nothing more. The layout is a list of keys in key units; zones follow HP's four-zone firmware
// order (0 right, 1 middle, 2 left, 3 WASD), which is also the order of the colour table.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ohman {

    public sealed class KeyDef {
        public string Label; public double X, Y, W, H = 1; public int Zone; public int Index;
        public bool OnNumpad;
        /// <summary>HID usage on the keyboard page (0x07), 0 when we have no name for this key. A LampArray
        /// reports the usage of the key each of its lamps lights, which is how a lamp finds its key here.</summary>
        public ushort Usage;
        public int Row { get { return (int)Y; } }
    }

    public static class KeyboardLayouts {
        public const int ZoneRight = 0, ZoneMiddle = 1, ZoneLeft = 2, ZoneWasd = 3;
        /// <summary>Zone indices in left-to-right reading order, for chips and legends.</summary>
        public static int[] DisplayOrder(int zones) { return zones == 4 ? new[] { ZoneLeft, ZoneMiddle, ZoneRight, ZoneWasd } : new[] { 0 }; }
        public static string ZoneName(int zones, int zone) {
            if (zones != 4) return "Keyboard";
            return zone == ZoneLeft ? "Left" : zone == ZoneMiddle ? "Middle" : zone == ZoneRight ? "Right" : "WASD";
        }
        /// <summary>A compact laptop keyboard (the Transcend 14 shape); with numpad adds the 4-column block on the right.</summary>
        public static List<KeyDef> Build(bool numpad, int zones) {
            var keys = new List<KeyDef>();
            string[][] rows = {
                new[] { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Del" },
                new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "⌫:2.2" },
                new[] { "Tab:1.5", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\:1.7" },
                new[] { "Caps:1.8", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "Enter:2.4" },
                new[] { "Shift:2.3", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "Shift:2.9" },
                new[] { "Ctrl", "Fn", "Win", "Alt", " :6.4", "Alt", "Ctrl", "◀", "▲▼", "▶" }
            };
            double y = 0; int idx = 0;
            foreach (var row in rows) {
                double x = 0;
                foreach (string spec in row) {
                    string label = spec; double w = 1;
                    int c = spec.LastIndexOf(':');
                    if (c > 0) { label = spec.Substring(0, c); w = double.Parse(spec.Substring(c + 1), CultureInfo.InvariantCulture); }
                    keys.Add(new KeyDef { Label = label.Trim(), X = x, Y = y, W = w, H = 1, Index = idx++ });
                    x += w;
                }
                y += 1;
            }
            if (numpad) {
                double nx = 15.3; string[][] np = { new[] { "Num", "/", "*", "-" }, new[] { "7", "8", "9", "+" }, new[] { "4", "5", "6", "" }, new[] { "1", "2", "3", "Ent" }, new[] { "0:2", ".", "" } };
                double ny = 1;
                foreach (var row in np) { double x = nx; foreach (string spec in row) { string label = spec; double w = 1; int c = spec.LastIndexOf(':'); if (c > 0) { label = spec.Substring(0, c); w = double.Parse(spec.Substring(c + 1), CultureInfo.InvariantCulture); } if (label.Length > 0) keys.Add(new KeyDef { Label = label, X = x, Y = ny, W = w, H = 1, Index = idx++, OnNumpad = true }); x += w; } ny += 1; }
            }
            foreach (var k in keys) k.Usage = UsageOf(k);
            foreach (var k in keys) {
                if (zones != 4) { k.Zone = 0; continue; }
                double mid = k.X + k.W / 2;
                bool wasd = k.Label == "W" || k.Label == "A" || k.Label == "S" || k.Label == "D";
                k.Zone = wasd ? ZoneWasd : mid < 4.6 ? ZoneLeft : mid < 9.2 ? ZoneMiddle : ZoneRight;
            }
            return keys;
        }

        // HID Usage Tables, Keyboard/Keypad page (0x07). Only the keys this layout draws.
        static readonly Dictionary<string, ushort> Usages = Build(
            "Esc 29 Enter 28 Tab 2B Caps 39 Del 4C ` 35 - 2D = 2E [ 2F ] 30 \\ 31 ; 33 ' 34 , 36 . 37 / 38 " +
            "F1 3A F2 3B F3 3C F4 3D F5 3E F6 3F F7 40 F8 41 F9 42 F10 43 F11 44 F12 45 " +
            "1 1E 2 1F 3 20 4 21 5 22 6 23 7 24 8 25 9 26 0 27 " +
            "Num 53 Ent 58 Win E3 Fn 00");
        static Dictionary<string, ushort> Build(string pairs) {
            var d = new Dictionary<string, ushort>(StringComparer.Ordinal);
            string[] p = pairs.Split(' ');
            for (int i = 0; i + 1 < p.Length; i += 2) d[p[i]] = ushort.Parse(p[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (ushort)(0x04 + (c - 'A'));
            return d;
        }
        /// <summary>Point each drawn key at the lamp that lights it. The device says which key each of its lamps is
        /// under, so the match is the hardware's own; any key the device did not name falls back to the nearest lamp by
        /// position, which is what the bounding box is for. Afterwards a key's Zone is its lamp id and the rest of the
        /// app — selection, painting, effect frames, drawing — carries on in zone indices exactly as it does with four.</summary>
        public static void BindLamps(List<KeyDef> keys, LampArray la) {
            double maxX = 0, maxY = 0;
            foreach (var k in keys) { maxX = Math.Max(maxX, k.X + k.W); maxY = Math.Max(maxY, k.Y + k.H); }
            if (maxX <= 0 || maxY <= 0 || la.LampCount <= 0) return;
            var byUsage = new Dictionary<ushort, int>();
            for (int i = 0; i < la.LampCount; i++) {
                ushort u = la.KeyUsage[i];
                if (u != 0 && u != 0xFFFF && !byUsage.ContainsKey(u)) byUsage[u] = i;
            }
            int named = 0;
            foreach (var k in keys) {
                int lamp;
                if (k.Usage != 0 && byUsage.TryGetValue(k.Usage, out lamp)) { k.Zone = lamp; named++; continue; }
                k.Zone = Nearest(la, (k.X + k.W / 2) / maxX, (k.Y + k.H / 2) / maxY);
            }
            Log.Write("lamparray: " + named + " of " + keys.Count + " drawn keys matched a lamp by HID usage; the rest by position");
        }
        /// <summary>The lamp closest to a point given as a fraction of the board, in the device's own bounding box.</summary>
        static int Nearest(LampArray la, double fx, double fy) {
            double want = fx * la.WidthMicrometres, wantY = fy * la.HeightMicrometres;
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < la.LampCount; i++) {
                double dx = la.X[i] - want, dy = la.Y[i] - wantY, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>The HID usage for one drawn key. Labels alone are not enough: this layout has two Shifts, two
        /// Ctrls, two Alts and a numpad that repeats every digit, so the key's place on the board decides.</summary>
        static ushort UsageOf(KeyDef k) {
            string l = k.Label;
            if (k.OnNumpad) {
                if (l.Length == 1 && l[0] >= '1' && l[0] <= '9') return (ushort)(0x59 + (l[0] - '1'));
                if (l == "0") return 0x62;
                if (l == ".") return 0x63;
                if (l == "/") return 0x54;
                if (l == "*") return 0x55;
                if (l == "-") return 0x56;
                if (l == "+") return 0x57;
            }
            bool left = k.X < 7;
            if (l == "Shift") return left ? (ushort)0xE1 : (ushort)0xE5;
            if (l == "Ctrl") return left ? (ushort)0xE0 : (ushort)0xE4;
            if (l == "Alt") return left ? (ushort)0xE2 : (ushort)0xE6;
            if (l == "\u232B") return 0x2A;                      // backspace
            if (l == "\u25C0") return 0x50;                      // left
            if (l == "\u25B6") return 0x4F;                      // right
            if (l == "\u25B2\u25BC") return 0x52;               // one key for up and down on this shape; call it up
            if (l.Length == 0) return 0x2C;                      // the space bar is drawn with a blank label
            ushort u; return Usages.TryGetValue(l, out u) ? u : (ushort)0;
        }
    }

    /// <summary>Flat keys in their zone colour; legends on the page map, none on the glyph. The selection is a set of
    /// key indices the page fills in (one key, a row, a zone or the whole board); hovering a group steps the rest back.
    /// Frames arriving during an effect are smoothed per rendered frame.</summary>
    public sealed class KeyboardView : FrameworkElement {
        public List<KeyDef> Keys = new List<KeyDef>();
        public bool Interactive, Selectable = true, Off, WindowsOwned;
        public bool Smooth;                                       // effects: blend between the zone colours across the board instead of four hard bands
        public double Level = 1.0;                                // 0..1: the brightness slider dims the drawing too
        public double Gap = 5, RowPitch = 39;                     // px between keys; vertical pitch per key row
        public readonly HashSet<int> Selected = new HashSet<int>();   // key indices being edited
        public readonly HashSet<int> Hover = new HashSet<int>();      // key indices in the group under the pointer
        public event Action<KeyDef> KeyClicked;
        public event Action<KeyDef> KeyHovered;                   // null when the pointer leaves the board
        Rgb[] shown = new Rgb[0], target = new Rgb[0]; bool animating;
        int zones = 1; double unitsW = 15.5, unitsH = 6;
        static Typeface face;
        static readonly Dictionary<uint, SolidColorBrush> Brushes_ = new Dictionary<uint, SolidColorBrush>();
        static readonly Color OffCap = Color.FromRgb(0x1F, 0x1C, 0x1A), OffLegend = Color.FromRgb(0x84, 0x7F, 0x7B),
            WinLegend = Color.FromRgb(0xA2, 0x9D, 0x99), MiniOff = Color.FromRgb(0x2B, 0x28, 0x26), Card = Color.FromRgb(0x16, 0x13, 0x11);

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

        protected override Size MeasureOverride(Size a) {
            double w = double.IsInfinity(a.Width) ? 590 : a.Width;
            return new Size(w, unitsH * RowPitch - Gap);
        }
        double Unit { get { return (ActualWidth + Gap) / unitsW; } }
        Rect KeyRect(KeyDef k) { double u = Unit; return new Rect(k.X * u, k.Y * RowPitch, Math.Max(1, k.W * u - Gap), Math.Max(1, k.H * RowPitch - Gap)); }

        static SolidColorBrush B(Color c) {
            uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B); SolidColorBrush b;
            if (Brushes_.TryGetValue(key, out b)) return b;
            if (Brushes_.Count > 2048) Brushes_.Clear();     // an effect paints a new colour every frame; the cache is a
            b = new SolidColorBrush(c); b.Freeze();          // frame-to-frame saving, not somewhere to keep hours of them
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
            int n = Math.Min(zones, shown.Length);
            double t = (k.X + k.W / 2) / unitsW * n - 0.5;                       // position in zone-centre units
            int i0 = (int)Math.Floor(t), i1 = i0 + 1; double f = t - i0;
            i0 = Math.Max(0, Math.Min(n - 1, i0)); i1 = Math.Max(0, Math.Min(n - 1, i1));
            var order = KeyboardLayouts.DisplayOrder(n);
            int a = i0 < order.Length ? order[i0] : 0, b = i1 < order.Length ? order[i1] : 0;
            return Mix(ToColor(shown[a]), ToColor(shown[b]), f);
        }
        static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0; }

        protected override void OnRender(DrawingContext dc) {
            if (face == null) face = new Typeface(Ui.MonoFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            // A FrameworkElement is only hit-testable where it has drawn something, so without this the gaps
            // between the keys are not part of the control: the pointer crossing one counts as leaving
            // altogether, MouseLeave fires, and the hover drops. Transparent is hit-testable; null is not.
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
                    fill = Interactive ? OffCap : MiniOff; legend = WindowsOwned ? WinLegend : OffLegend;
                }
                // keys outside the group under the pointer step back rather than disappear
                if (anyHover && !hov && !sel) { fill = Mix(Card, fill, 0.32); legend = WithA(legend, 0.45); }
                dc.DrawRoundedRectangle(B(fill), null, r, radius, radius);
                if (!Interactive) continue;
                if (sel) dc.DrawRoundedRectangle(null, new Pen(B(Color.FromRgb(0xFC, 0xFA, 0xF9)), 2), new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), radius, radius);
                else if (hov) dc.DrawRoundedRectangle(null, new Pen(B(WithA(Colors.White, 0.5)), 2), new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), radius, radius);
                else dc.DrawRoundedRectangle(null, new Pen(B(WithA(Colors.Black, 0.22)), 1), new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1)), radius, radius);
                string label = k.Label.Trim();
                if (label.Length > 0 && r.Width >= 14) {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, B(legend), 1.0);
                    dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
                }
            }
        }

        const double Snap = 11;        // a little over the gap between keys, and between rows

        /// <summary>The key under the pointer, or the nearest one within Snap. The gaps are five pixels wide, and
        /// treating them as empty meant dragging across the board dropped the hover between every pair of keys, so
        /// the whole group flicked off and on again. A point in a gap belongs to whoever is closest. It also means
        /// there are no dead clicks: clicking a gap selects the key you were obviously aiming at.</summary>
        KeyDef Hit(Point p) {
            KeyDef best = null; double bestD = double.MaxValue;
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
        protected override void OnMouseMove(MouseEventArgs e) {
            if (!Interactive || !Selectable) return;
            var k = Hit(e.GetPosition(this));
            if (k == hovered) return;
            hovered = k; Cursor = k == null ? Cursors.Arrow : Cursors.Hand;
            var h = KeyHovered; if (h != null) h(k);
        }
        protected override void OnMouseLeave(MouseEventArgs e) {
            if (!Interactive || hovered == null) return;
            hovered = null; var h = KeyHovered; if (h != null) h(null);
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if (!Interactive || !Selectable) return;
            var k = Hit(e.GetPosition(this));
            var h = KeyClicked; if (h != null) h(k);
        }
    }
}
