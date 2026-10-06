// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the global hotkeys as data. Which actions there are, what each is bound to unless the owner says
// otherwise, and how a binding reads and writes as text ("Ctrl+Alt+E"), so the settings file can carry the
// owner's own and the panel can draw them.
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace Ohman {

    public enum HotkeyAction { Eco = 0, Balanced = 1, Performance = 2, MaxFan = 3, Cycle = 4, Benchmark = 5 }

    /// <summary>One binding: RegisterHotKey's modifier bits and a virtual key. Vk 0 is "none".</summary>
    public struct Hotkey {
        public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
        public uint Mods, Vk;
        public Hotkey(uint mods, uint vk) { Mods = mods; Vk = vk; }
        public static readonly Hotkey None = new Hotkey();
        public bool IsEmpty { get { return Vk == 0; } }
        public bool Same(Hotkey o) { return Mods == o.Mods && Vk == o.Vk; }
        /// <summary>Fit to be global: a modifier held, or a function key on its own. A bare letter would take
        /// that letter away from every program on the machine.</summary>
        public bool Valid { get { return Vk != 0 && (Mods != 0 || IsFunctionKey(Vk)); } }
        static bool IsFunctionKey(uint vk) { return vk >= 0x70 && vk <= 0x87; }

        /// <summary>Windows sends AltGr as Ctrl+Alt, so a Ctrl+Alt binding on a key AltGr types a letter with takes
        /// that letter away: Ctrl+Alt+E ate every Polish "ę" and switched to Eco instead. True when this layout
        /// types a character with the combination pressed through AltGr.</summary>
        public bool TypesCharacter(IntPtr layout) {
            if ((Mods & (Ctrl | Alt)) != (Ctrl | Alt) || (Mods & Win) != 0 || Vk == 0) return false;
            try {
                var state = new byte[256];
                state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 0x80;       // AltGr: Ctrl, left Ctrl, Alt, right Alt
                if ((Mods & Shift) != 0) state[0x10] = state[0xA0] = 0x80;
                var buf = new StringBuilder(8);
                // Flag 4: leave the keyboard's dead-key state alone, so asking does not change what gets typed next.
                int r = ToUnicodeEx(Vk, MapVirtualKeyEx(Vk, 0, layout), state, buf, buf.Capacity, 4, layout);
                return r < 0 || (r > 0 && buf.Length > 0 && buf[0] >= 0x20);         // r < 0 is a dead key: AltGr starts a character
            } catch { return false; }
        }
        [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] StringBuilder buf, int size, uint flags, IntPtr hkl);

        public override string ToString() {
            if (IsEmpty) return "";
            var sb = new StringBuilder();
            if ((Mods & Ctrl) != 0) sb.Append("Ctrl+");
            if ((Mods & Alt) != 0) sb.Append("Alt+");
            if ((Mods & Shift) != 0) sb.Append("Shift+");
            if ((Mods & Win) != 0) sb.Append("Win+");
            sb.Append(KeyName(Vk));
            return sb.ToString();
        }
        /// <summary>The parts, for drawing as key caps: {"Ctrl", "Alt", "E"}.</summary>
        public string[] Parts() { return IsEmpty ? new string[0] : ToString().Split('+'); }

        public static string KeyName(uint vk) {
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
            if (IsFunctionKey(vk)) return "F" + (vk - 0x6F);
            switch (vk) {
                case 0x20: return "Space"; case 0x1B: return "Esc"; case 0x0D: return "Enter"; case 0x09: return "Tab";
                case 0x21: return "PageUp"; case 0x22: return "PageDown"; case 0x23: return "End"; case 0x24: return "Home";
                case 0x25: return "Left"; case 0x26: return "Up"; case 0x27: return "Right"; case 0x28: return "Down";
                case 0x2D: return "Insert"; case 0x2E: return "Delete"; case 0x2C: return "PrintScreen"; case 0x91: return "ScrollLock"; case 0x13: return "Pause";
            }
            try { return KeyInterop.KeyFromVirtualKey((int)vk).ToString(); } catch { return "0x" + vk.ToString("X2"); }
        }

        public static bool TryParse(string s, out Hotkey h) {
            h = None;
            if (string.IsNullOrEmpty(s)) return true;
            uint mods = 0, vk = 0;
            foreach (string raw in s.Split('+')) {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string u = t.ToUpperInvariant();
                if (u == "CTRL" || u == "CONTROL") mods |= Ctrl;
                else if (u == "ALT") mods |= Alt;
                else if (u == "SHIFT") mods |= Shift;
                else if (u == "WIN" || u == "WINDOWS") mods |= Win;
                else if (u.Length == 1 && (char.IsLetterOrDigit(u[0]))) vk = u[0];
                else if (u.Length >= 2 && u[0] == 'F' && char.IsDigit(u[1])) { int n; if (int.TryParse(u.Substring(1), out n) && n >= 1 && n <= 24) vk = (uint)(0x6F + n); }
                else {
                    // The reverse of KeyName: a friendly name first, then WPF's own key names.
                    for (uint c = 0; c < 256; c++) if (KeyName(c).Equals(t, StringComparison.OrdinalIgnoreCase)) { vk = c; break; }
                    if (vk == 0) { Key k; if (Enum.TryParse<Key>(t, true, out k)) vk = (uint)KeyInterop.VirtualKeyFromKey(k); }
                }
            }
            if (vk == 0) return false;
            h = new Hotkey(mods, vk);
            return true;
        }

        /// <summary>From a key event: the modifiers held and the key under them. False while only a modifier
        /// is down, which is the state between pressing Ctrl and pressing the letter.</summary>
        public static bool FromKey(Key key, ModifierKeys held, out Hotkey h) {
            h = None;
            switch (key) {
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
                case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin: case Key.None:
                    return false;
            }
            uint mods = 0;
            if ((held & ModifierKeys.Control) != 0) mods |= Ctrl;
            if ((held & ModifierKeys.Alt) != 0) mods |= Alt;
            if ((held & ModifierKeys.Shift) != 0) mods |= Shift;
            if ((held & ModifierKeys.Windows) != 0) mods |= Win;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return false;
            h = new Hotkey(mods, (uint)vk);
            return true;
        }
    }

    /// <summary>The six actions, their labels, their settings keys and their defaults, in HotkeyAction order.
    /// Opening the panel is the OMEN key's job and the tray's, not a shortcut's.</summary>
    public static class HotkeyTable {
        public const int Count = 6;
        public static readonly string[] Names = { "Eco", "Balanced", "Performance", "Max fan", "Cycle modes", "Benchmark" };
        public static readonly string[] Keys = { "Eco", "Balanced", "Performance", "MaxFan", "Cycle", "Benchmark" };
        public static readonly Hotkey[] Defaults = {
            CtrlAlt('E'), CtrlAlt('B'), CtrlAlt('P'), CtrlAlt('M'),
            new Hotkey(Hotkey.Shift, 0x7A),        // Shift+F11, next to the OMEN key. Not F12: Windows reserves it for the debugger
            CtrlAlt('R'),                          // start a benchmark from inside the game, and stop one; R for run
        };
        static Hotkey CtrlAlt(char c) { return new Hotkey(Hotkey.Ctrl | Hotkey.Alt, (uint)c); }

        /// <summary>The line under the Hotkeys row, grouped by modifier set: "Ctrl+Alt: E/B/P modes, M max fan ·
        /// Shift+F11 cycles". Four shortcuts that share Ctrl+Alt are one entry that way rather than a line each.</summary>
        /// <param name="omenKey">What the OMEN key (Fn+F12) does, worded for this line, or null. It is set on the row
        /// above and is not one of the bindings here, but it belongs in the list of keys that do things.</param>
        public static string Summary(Hotkey[] b, string omenKey) {
            var order = new System.Collections.Generic.List<uint>();
            var items = new System.Collections.Generic.Dictionary<uint, System.Collections.Generic.List<string>>();
            Action<uint, string> add = delegate(uint mods, string text) {
                if (!items.ContainsKey(mods)) { items[mods] = new System.Collections.Generic.List<string>(); order.Add(mods); }
                items[mods].Add(text);
            };
            Hotkey e = b[0], m = b[1], p = b[2];
            bool folded = !e.IsEmpty && !m.IsEmpty && !p.IsEmpty && e.Mods == m.Mods && m.Mods == p.Mods
                && Hotkey.KeyName(e.Vk).Length == 1 && Hotkey.KeyName(m.Vk).Length == 1 && Hotkey.KeyName(p.Vk).Length == 1;
            if (folded) add(e.Mods, Hotkey.KeyName(e.Vk) + "/" + Hotkey.KeyName(m.Vk) + "/" + Hotkey.KeyName(p.Vk) + " modes");
            else {
                if (!e.IsEmpty) add(e.Mods, Hotkey.KeyName(e.Vk) + " eco");
                if (!m.IsEmpty) add(m.Mods, Hotkey.KeyName(m.Vk) + " balanced");
                if (!p.IsEmpty) add(p.Mods, Hotkey.KeyName(p.Vk) + " performance");
            }
            if (!b[3].IsEmpty) add(b[3].Mods, Hotkey.KeyName(b[3].Vk) + " max fan");
            if (!b[4].IsEmpty) add(b[4].Mods, Hotkey.KeyName(b[4].Vk) + " cycles");
            if (!b[5].IsEmpty) add(b[5].Mods, Hotkey.KeyName(b[5].Vk) + " benchmark");
            if (omenKey != null) add(uint.MaxValue, omenKey);
            if (order.Count == 0) return "No shortcuts set";
            var parts = new System.Collections.Generic.List<string>();
            foreach (uint mods in order) {
                if (mods == uint.MaxValue) { parts.Add("Fn+F12 " + items[mods][0]); continue; }
                string prefix = new Hotkey(mods, 'X').ToString();
                prefix = prefix.Substring(0, prefix.Length - 1);          // "Ctrl+Alt+" or ""
                string list = string.Join(", ", items[mods].ToArray());
                // One item keeps its modifiers attached ("Shift+F11 cycles"); several share them once ("Ctrl+Alt: E/B/P modes, ...").
                parts.Add(prefix.Length == 0 ? list : items[mods].Count == 1 ? prefix + list : prefix.Substring(0, prefix.Length - 1) + ": " + list);
            }
            return string.Join(" · ", parts.ToArray());
        }
    }
}
