// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the few shared types the controls touch, cut down, for building the harness with -p:Stub=true when
// the shared core is mid-change and does not compile. The real ones are in src\Lighting.cs, src\Keyboard.cs,
// src\Hardware.cs and src\Choice.cs; the normal build uses those and never sees this file.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ohman {
    public struct Rgb {
        public byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public string Hex { get { return R.ToString("X2") + G.ToString("X2") + B.ToString("X2"); } }
        public static bool TryParse(string s, out Rgb c) {
            c = new Rgb();
            int v;
            if (s == null || s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
    }
    public static class Log { public static void Write(string s) { Console.WriteLine("      log: " + s); } }
    static class Choice {
        public static readonly string[] Fan = { "Auto", "Max", "Curve", "Manual" };
        public static readonly string[] Key = { "Cycle", "Panel", "Max fan", "Run", "Off" };
        public static readonly string[] Light = { "Off", "Static", "Breathe", "Cycle", "Wave", "Windows" };
    }
    public sealed class KeyDef {
        public string Label;
        public double X, Y, W, H = 1;
        public int Zone;
        public int Index;
        public int Row;
    }
    public static class KeyboardLayouts {
        public const int ZoneRight = 0, ZoneMiddle = 1, ZoneLeft = 2, ZoneWasd = 3;
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
            int idx = 0, rowNo = 0;
            foreach (var row in rows) {
                double x = 0;
                foreach (string spec in row) {
                    string label = spec;
                    double w = 1;
                    int c = spec.LastIndexOf(':');
                    if (c > 0) { label = spec.Substring(0, c); w = double.Parse(spec.Substring(c + 1), CultureInfo.InvariantCulture); }
                    keys.Add(new KeyDef { Label = label.Trim(), X = x, Y = y, W = w, H = 1, Index = idx++, Row = rowNo });
                    x += w;
                }
                y += 1; rowNo++;
            }
            foreach (var k in keys) {
                if (zones != 4) { k.Zone = 0; continue; }
                double mid = k.X + k.W / 2;
                bool wasd = k.Label == "W" || k.Label == "A" || k.Label == "S" || k.Label == "D";
                double rightFrom = k.Row == 5 ? 11.4 : 13.25;
                k.Zone = wasd ? ZoneWasd : mid < 5.9 ? ZoneLeft : mid < rightFrom ? ZoneMiddle : ZoneRight;
            }
            return keys;
        }
    }
}
