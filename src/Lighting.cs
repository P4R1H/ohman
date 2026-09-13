// Ohman — keyboard lighting.
// HP laptops expose the keyboard backlight through the same BIOS mailbox as the performance controls, under a
// second command id (0x20009). Zone colours sit in a 128-byte table, brightness/backlight in one byte whose bit 7
// is the "on" flag. Which keyboard a model has comes from the performance mailbox (0x20008 / 0x2B).
// Layout from OMEN Gaming Hub's own lighting module (HP.Omen.Background.FourZone), cross-checked with OmenMon.
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace Ohman {

    public enum LightKind { None = 0, Zones = 1, PerKey = 2 }

    public struct Rgb {
        public byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public string Hex { get { return R.ToString("X2") + G.ToString("X2") + B.ToString("X2"); } }
        public static bool TryParse(string s, out Rgb c) {
            c = new Rgb(); int v;
            if (s == null || s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v); return true;
        }
        /// <summary>Hue 0..360 at full saturation and value.</summary>
        public static Rgb FromHue(double h) {
            h = ((h % 360) + 360) % 360; double x = 1 - Math.Abs((h / 60) % 2 - 1);
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = 1; g = x; } else if (h < 120) { r = x; g = 1; } else if (h < 180) { g = 1; b = x; }
            else if (h < 240) { g = x; b = 1; } else if (h < 300) { r = x; b = 1; } else { r = 1; b = x; }
            return new Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
        public static Rgb FromHsv(double h, double sat, double val) {
            h = ((h % 360) + 360) % 360; sat = Math.Max(0, Math.Min(1, sat)); val = Math.Max(0, Math.Min(1, val));
            double c = val * sat, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = val - c, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        public void ToHsv(out double h, out double sat, out double val) {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            val = max; sat = max <= 0 ? 0 : d / max; h = Hue;
        }
        public Rgb Scale(double f) { f = Math.Max(0, Math.Min(1, f)); return new Rgb((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f)); }
        public double Hue {
            get {
                double r = R / 255.0, g = G / 255.0, b = B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                if (d < 1e-6) return 0;
                double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                return ((h * 60) + 360) % 360;
            }
        }
    }

    public interface ILighting {
        LightKind Kind { get; }
        int Zones { get; }                 // addressable colour groups (1 or 4 on HP firmware)
        /// <summary>The firmware answers every lighting call but does not drive this keyboard. True on the per-key
        /// boards: they keep the four-zone colour table and the backlight byte, both of which change nothing on the
        /// hardware. Colours there need the keyboard's own USB HID interface, which Ohman does not speak yet.</summary>
        bool Inert { get; }
        bool Numpad { get; }               // layout hint for the drawing
        string Describe { get; }           // "4 zones", "1 zone", "per-key"
        Rgb[] GetColors();
        void SetColors(Rgb[] zones);
        /// <summary>Backlight byte: bit 7 = on, low bits = level 0..100 (OGH itself only ever writes 100).</summary>
        int GetBacklight();
        void SetBacklight(bool on, int level);
    }

    /// <summary>The firmware path: 0x20008/0x2B for the keyboard type, 0x20009 for colours and backlight.</summary>
    public sealed class BiosLighting : ILighting {
        public const uint CMD = 0x20009;                        // WMI_CMD_LED_LIGHTING_CONTROL in OGH
        public const uint OP_KBD_TYPE = 0x2B;                   // under 0x20008: out4 [0] = keyboard type (see KbdType)
        public const uint OP_PLATFORM_INFO = 0x01;              // legacy models: out128 [0] bit 0 = lighting supported
        public const uint OP_COLOR_GET = 0x02, OP_COLOR_SET = 0x03, OP_LIGHT_GET = 0x04, OP_LIGHT_SET = 0x05;
        public const int COLOR_OFFSET = 25;                     // zone i = bytes 25+3i .. 27+3i (R, G, B)
        public const int ON_FLAG = 0x80;
        // keyboard type byte (OGH NbKeyboardLightingType): 0 none, 1/2 four zones (with/without numpad), 3 per-key RGB, 4/5 one zone
        public readonly int KbdType;
        readonly LightKind kind; readonly int zones;
        public LightKind Kind { get { return kind; } }
        public int Zones { get { return zones; } }
        public string Describe { get { return kind == LightKind.PerKey ? "per-key" : zones == 1 ? "1 zone" : zones + " zones"; } }
        public bool Numpad { get { return KbdType == 1 || KbdType == 4; } }
        public bool Inert { get { return kind == LightKind.PerKey; } }

        BiosLighting(int kbdType, LightKind k, int z) { KbdType = kbdType; kind = k; zones = z; }

        /// <summary>Asks the firmware what keyboard this is. Read-only. Null when there is nothing to control.</summary>
        public static BiosLighting Detect() {
            int type = -1;
            try { var d = Bios.Call(Bios.CMD_DEFAULT, OP_KBD_TYPE, new byte[0], 4); if (d.Length > 0) type = d[0]; }
            catch (Exception ex) { Log.Write("keyboard type query: " + ex.Message); }
            LightKind k = LightKind.None; int z = 0;
            if (type == 1 || type == 2) { k = LightKind.Zones; z = 4; }
            else if (type == 4 || type == 5) { k = LightKind.Zones; z = 1; }
            // Type 3 keeps the four-zone table and the backlight byte, and neither does anything: reported by three
            // separate owners (OMEN 17-ck, board 88FE, Transcend 16) and confirmed by OmenMon's maintainer. The real
            // interface is the keyboard's own USB HID device. See docs/research.md, "Per-key keyboards".
            else if (type == 3) { k = LightKind.PerKey; z = 4; }
            else {
                // older models (OGH: Pirates/Marlins/Gamora/Milos/Santorini) answer a platform-info query instead
                try { var d = Bios.Call(CMD, OP_PLATFORM_INFO, new byte[0], 128); if (d.Length > 0 && (d[0] & 1) != 0) { k = LightKind.Zones; z = 4; } }
                catch (Exception ex) { Log.Write("lighting platform info: " + ex.Message); }
            }
            if (k == LightKind.None) { Log.Write("keyboard lighting: none (type " + type + ")"); return null; }
            if (k == LightKind.PerKey) Log.Write("keyboard lighting: type 3 (per-key). The firmware interface answers but drives nothing on these boards; colours are left to Windows Dynamic Lighting.");
            var l = new BiosLighting(type, k, z);
            try { var c = l.GetColors(); int b = l.GetBacklight(); Log.Write("keyboard lighting: type " + type + " -> " + l.Describe + ", colours " + Join(c) + ", backlight 0x" + b.ToString("X2")); }
            catch (Exception ex) { Log.Write("keyboard lighting: type " + type + " but the colour table failed (" + ex.Message + "); disabled"); return null; }
            return l;
        }

        static string Join(Rgb[] c) { var s = new List<string>(); foreach (var x in c) s.Add(x.Hex); return string.Join(",", s.ToArray()); }

        byte[] Table() { var d = Bios.Call(CMD, OP_COLOR_GET, new byte[] { 0 }, 128); if (d.Length < COLOR_OFFSET + 3 * zones) throw new InvalidOperationException("short colour table (" + d.Length + " bytes)"); return d; }

        public Rgb[] GetColors() {
            var d = Table(); var c = new Rgb[zones];
            for (int i = 0; i < zones; i++) c[i] = new Rgb(d[COLOR_OFFSET + 3 * i], d[COLOR_OFFSET + 3 * i + 1], d[COLOR_OFFSET + 3 * i + 2]);
            return c;
        }

        public void SetColors(Rgb[] c) {
            var d = Table();                                     // read-modify-write, exactly as OGH does; bytes 0..24 are left alone
            var buf = new byte[128]; Array.Copy(d, buf, Math.Min(d.Length, 128));
            for (int i = 0; i < zones && i < c.Length; i++) { buf[COLOR_OFFSET + 3 * i] = c[i].R; buf[COLOR_OFFSET + 3 * i + 1] = c[i].G; buf[COLOR_OFFSET + 3 * i + 2] = c[i].B; }
            Bios.Call(CMD, OP_COLOR_SET, buf, 4);
        }

        public int GetBacklight() { var d = Bios.Call(CMD, OP_LIGHT_GET, new byte[] { 0 }, 128); return d.Length > 0 ? d[0] : -1; }

        public void SetBacklight(bool on, int level) {
            int v = Math.Max(0, Math.Min(100, level)) | (on ? ON_FLAG : 0);   // OGH writes 0xE4 (on) / 0x64 (off)
            Bios.Call(CMD, OP_LIGHT_SET, new byte[] { (byte)v, 0, 0, 0 }, 4);
        }
    }

    /// <summary>Simulated keyboard for the preview build.</summary>
    public sealed class DemoLighting : ILighting {
        // Zone ids are HP's (0 right, 1 middle, 2 left, 3 WASD), so written in that order this is a blue ramp
        // running light on the left to deep on the right, with the WASD cluster picked out in near-white.
        Rgb[] colors = { new Rgb(0x2E, 0x6B, 0xFF), new Rgb(0x33, 0xA5, 0xE6), new Rgb(0x5F, 0xCB, 0xE0), new Rgb(0xF2, 0xEC, 0xE6) };
        int light = 0xE4;
        public LightKind Kind { get { return LightKind.Zones; } }
        public bool Inert { get { return false; } }
        public int Zones { get { return 4; } }
        public string Describe { get { return "4 zones"; } }
        public bool Numpad { get { return false; } }
        public Rgb[] GetColors() { return (Rgb[])colors.Clone(); }
        public void SetColors(Rgb[] c) { for (int i = 0; i < 4 && i < c.Length; i++) colors[i] = c[i]; }
        public int GetBacklight() { return light; }
        public void SetBacklight(bool on, int level) { light = Math.Max(0, Math.Min(100, level)) | (on ? 0x80 : 0); }
    }

    /// <summary>Windows Dynamic Lighting owns the keyboard when its per-device "ambient" switch is on; OGH and Ohman
    /// take the keyboard by clearing that switch (the same registry value the Settings toggle writes) and give it back
    /// by setting it. Only HP's virtual lighting device (VHF) is touched, never external LampArray peripherals.</summary>
    public static class WinLighting {
        const string Root = @"Software\Microsoft\Lighting";
        static string[] DeviceKeys() {
            var r = new List<string>();
            try {
                using (var k = Registry.CurrentUser.OpenSubKey(Root + @"\Devices"))
                    if (k != null) foreach (string n in k.GetSubKeyNames()) if (n.IndexOf("HID_DEVICE_SYSTEM_VHF", StringComparison.OrdinalIgnoreCase) >= 0) r.Add(n);
            } catch { }
            return r.ToArray();
        }
        /// <summary>True when Windows has a Dynamic Lighting entry for the laptop keyboard (HP's HyperX Lighting driver present).</summary>
        public static bool Present { get { return DeviceKeys().Length > 0; } }
        public static bool HasControl {
            get {
                try {
                    using (var g = Registry.CurrentUser.OpenSubKey(Root)) if (g != null && Convert.ToInt32(g.GetValue("AmbientLightingEnabled", 1)) == 0) return false;
                    foreach (string n in DeviceKeys()) using (var k = Registry.CurrentUser.OpenSubKey(Root + @"\Devices\" + n)) if (k != null && Convert.ToInt32(k.GetValue("AmbientLightingEnabled", 1)) != 0) return true;
                } catch { }
                return false;
            }
        }
        public static void SetControl(bool windows) {
            foreach (string n in DeviceKeys()) {
                try { using (var k = Registry.CurrentUser.CreateSubKey(Root + @"\Devices\" + n)) k.SetValue("AmbientLightingEnabled", windows ? 1 : 0, RegistryValueKind.DWord); }
                catch (Exception ex) { Log.Write("dynamic lighting switch: " + ex.Message); }
            }
        }
    }
}
