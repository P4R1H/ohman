// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: the names of keys a hotkey can be written with, and a WPF key event as a hotkey.
// A Hotkey is stored as a Windows virtual key on every platform (src\Hotkeys.cs); src\linux\KeyNames.cs is the
// same class for Linux.
using System;
using System.Windows.Input;

namespace Ohman {
    static class KeyNames {
        /// <summary>The name of a virtual key that has no friendly name of ours. Throws when there is none.</summary>
        public static string FromVk(uint vk) { return KeyInterop.KeyFromVirtualKey((int)vk).ToString(); }

        /// <summary>The reverse. 0 when the name is not a key.</summary>
        public static uint ToVk(string name) {
            Key k;
            if (Enum.TryParse<Key>(name, true, out k)) return (uint)KeyInterop.VirtualKeyFromKey(k);
            return 0;
        }

        /// <summary>From a key event: the modifiers held and the key under them. False while only a modifier
        /// is down, which is the state between pressing Ctrl and pressing the letter.</summary>
        public static bool FromKey(Key key, ModifierKeys held, out Hotkey h) {
            h = Hotkey.None;
            switch (key) {
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
                case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin: case Key.None:
                    return false;
            }
            uint mods = 0;
            if ((held & ModifierKeys.Control) != 0) mods |= Hotkey.Ctrl;
            if ((held & ModifierKeys.Alt) != 0) mods |= Hotkey.Alt;
            if ((held & ModifierKeys.Shift) != 0) mods |= Hotkey.Shift;
            if ((held & ModifierKeys.Windows) != 0) mods |= Hotkey.Win;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return false;
            h = new Hotkey(mods, (uint)vk);
            return true;
        }

        /// <summary>True when this layout types a character with the key pressed through AltGr, with or without
        /// Shift. mods are Hotkey's modifier bits. Asked by Hotkey.TypesCharacter, which has already checked that
        /// the binding is a Ctrl+Alt one.</summary>
        public static bool TypesWithAltGr(uint mods, uint vk, IntPtr layout) {
            try {
                var state = new byte[256];
                state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 0x80;       // AltGr: Ctrl, left Ctrl, Alt, right Alt
                if ((mods & Hotkey.Shift) != 0) state[0x10] = state[0xA0] = 0x80;
                var buf = new System.Text.StringBuilder(8);
                // Flag 4: leave the keyboard's dead-key state alone, so asking does not change what gets typed next.
                int r = ToUnicodeEx(vk, MapVirtualKeyEx(vk, 0, layout), state, buf, buf.Capacity, 4, layout);
                return r < 0 || (r > 0 && buf.Length > 0 && buf[0] >= 0x20);         // r < 0 is a dead key: AltGr starts a character
            } catch { return false; }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [System.Runtime.InteropServices.Out] System.Text.StringBuilder buf, int size, uint flags, IntPtr hkl);
    }
}
