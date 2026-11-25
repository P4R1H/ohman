// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: keyboard layouts. Shared by every platform; the control that draws them is src\KeyboardView.cs (WPF).
// The layout is a list of keys in key units; zones follow HP's four-zone firmware
// order (0 right, 1 middle, 2 left, 3 WASD), which is also the order of the colour table.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ohman {

    public sealed class KeyDef {
        public string Label;
        public double X, Y, W, H = 1;
        public int Zone;
        public int Index;
        public bool OnNumpad;
        /// <summary>HID usage on the keyboard page (0x07), 0 when we have no name for this key. A LampArray
        /// reports the usage of the key each of its lamps lights, which is how a lamp finds its key here.</summary>
        public ushort Usage;
        public int Row { get { return (int)Y; } }
    }

    public static class KeyboardLayouts {
        /// <summary>Which colour-table entry lights which part of the board: OmenMon's order, confirmed by owners on 8912
        /// (a photo of the zones lit in four colours) and 8E35 (#87). HP's virtual lighting device reports the same lamp
        /// positions on a 14-inch and a 16-inch board, so they are its own constant and say nothing about the order.</summary>
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
            double y = 0;
            int idx = 0;
            foreach (var row in rows) {
                double x = 0;
                foreach (string spec in row) {
                    string label = spec;
                    double w = 1;
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
                // Edges from an owner's photo of an 8912 lit in four colours (#65): R, F, V, 5 and F5 are the left zone's last
                // keys, ], ', / and F12 the middle's, and on the bottom row everything after the right Alt is the right zone.
                double rightFrom = k.Row == 5 ? 11.4 : 13.25;
                k.Zone = wasd ? ZoneWasd : mid < 5.9 ? ZoneLeft : mid < rightFrom ? ZoneMiddle : ZoneRight;
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
        /// app (selection, painting, effect frames, drawing) carries on in zone indices exactly as it does with four.</summary>
        public static void BindLamps(List<KeyDef> keys, LampArray la) { BindLamps(keys, la, true); }
        static void BindLamps(List<KeyDef> keys, LampArray la, bool log) {
            double maxX = 0, maxY = 0;
            foreach (var k in keys) { maxX = Math.Max(maxX, k.X + k.W); maxY = Math.Max(maxY, k.Y + k.H); }
            if (maxX <= 0 || maxY <= 0 || la.LampCount <= 0) return;
            var byUsage = new Dictionary<ushort, int>();
            for (int i = 0; i < la.LampCount; i++) {
                ushort u = la.KeyUsage[i];
                if (u != 0 && u != 0xFFFF && !byUsage.ContainsKey(u)) byUsage[u] = i;
            }
            int named = 0;
            var claimed = new bool[la.LampCount];
            var rest = new List<KeyDef>();
            foreach (var k in keys) {
                int lamp;
                if (k.Usage != 0 && byUsage.TryGetValue(k.Usage, out lamp)) { k.Zone = lamp; claimed[lamp] = true; named++; }
                else rest.Add(k);
            }
            // By position second, and to a lamp no other key has. Fn, and the right Ctrl a MAX 16 does not have
            // (it has Copilot there), used to land on the left arrow's lamp and paint it twice.
            foreach (var k in rest) {
                k.Zone = Nearest(la, (k.X + k.W / 2) / maxX, (k.Y + k.H / 2) / maxY, claimed);
                claimed[k.Zone] = true;
            }
            if (log) Log.Write("lamparray: " + named + " of " + keys.Count + " drawn keys matched a lamp by HID usage; the rest by position");
        }
        /// <summary>The lamp closest to a point given as a fraction of the board, in the device's own bounding box.
        /// An unclaimed lamp wins only when it is within a key's width of the nearest one: a key whose own lamp is
        /// taken shares it rather than reaching across the board for a free one.</summary>
        static int Nearest(LampArray la, double fx, double fy, bool[] claimed) {
            double want = fx * la.WidthMicrometres, wantY = fy * la.HeightMicrometres;
            int best = -1, any = 0;
            double bestD = double.MaxValue, anyD = double.MaxValue;
            for (int i = 0; i < la.LampCount; i++) {
                double dx = la.X[i] - want, dy = la.Y[i] - wantY, d = dx * dx + dy * dy;
                if (d < anyD) { anyD = d; any = i; }
                if (!claimed[i] && d < bestD) { bestD = d; best = i; }
            }
            const double Reach = 10000;                                            // micrometres, about half a key
            return best >= 0 && Math.Sqrt(bestD) <= Math.Sqrt(anyD) + Reach ? best : any;
        }

        /// <summary>For every lamp no drawn key owns, the owned lamp it should copy: the one with the same HID usage
        /// (a MAX 16 lights its spacebar with five lamps and the drawing has one key there), else the nearest owned
        /// lamp on the same row, to its left first. -1 for owned lamps. The drawing is built here from the same data
        /// the keyboard page uses, so the engine gets the same answer with no window open.</summary>
        public static int[] Followers(LampArray la) {
            var lead = new int[la.LampCount];
            for (int i = 0; i < lead.Length; i++) lead[i] = -1;
            try {
                var keys = Build(la.LampCount > 90, la.LampCount);
                BindLamps(keys, la, false);
                var owned = new bool[la.LampCount];
                foreach (var k in keys) if (k.Zone >= 0 && k.Zone < owned.Length) owned[k.Zone] = true;
                var byUsage = new Dictionary<ushort, int>();
                for (int i = 0; i < la.LampCount; i++) {
                    ushort u = la.KeyUsage[i];
                    if (owned[i] && u > 0x03 && u != 0xFFFF && !byUsage.ContainsKey(u)) byUsage[u] = i;
                }
                const double Row = 5000;                                                // micrometres; rows sit ~18 mm apart
                for (int i = 0; i < la.LampCount; i++) {
                    if (owned[i]) continue;
                    int l;
                    ushort u = la.KeyUsage[i];
                    if (u > 0x03 && u != 0xFFFF && byUsage.TryGetValue(u, out l)) { lead[i] = l; continue; }
                    int left = -1, right = -1;
                    double leftD = double.MaxValue, rightD = double.MaxValue;
                    for (int j = 0; j < la.LampCount; j++) {
                        if (!owned[j] || Math.Abs(la.Y[j] - la.Y[i]) > Row) continue;
                        double dx = la.X[j] - la.X[i];
                        if (dx <= 0 && -dx < leftD) { leftD = -dx; left = j; }
                        if (dx > 0 && dx < rightD) { rightD = dx; right = j; }
                    }
                    lead[i] = left >= 0 ? left : right;
                }
            } catch (Exception ex) { Log.Write("lamparray followers: " + ex.Message); }
            return lead;
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
            ushort u;
            return Usages.TryGetValue(l, out u) ? u : (ushort)0;
        }
    }
}
