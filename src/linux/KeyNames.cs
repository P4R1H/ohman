// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the names of keys a hotkey can be written with. src\win\KeyNames.cs is the same class for
// Windows. A Hotkey is stored as a Windows virtual key on every platform (src\Hotkeys.cs) so one settings file
// reads the same everywhere; letters, digits, function keys and the common named keys are handled there, and
// only the rest reach this class.
using System;

namespace Ohman {
    static class KeyNames {
        /// <summary>The name of a virtual key that has no friendly name of ours. Throws when there is none,
        /// which Hotkey.KeyName turns into the key's number.</summary>
        public static string FromVk(uint vk) { throw new NotSupportedException(); }

        /// <summary>The reverse. 0 when the name is not a key.</summary>
        public static uint ToVk(string name) { return 0; }

        /// <summary>Whether this layout types a character with the key pressed through AltGr. Windows delivers
        /// AltGr as Ctrl+Alt, which is why it asks; X11 and Wayland do not (AltGr is its own modifier there), so
        /// a Ctrl+Alt binding takes no letter away and the answer is always no.</summary>
        public static bool TypesWithAltGr(uint mods, uint vk, IntPtr layout) { return false; }
    }
}
