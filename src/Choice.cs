// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the order of the multiple-choice controls. Shared: every front end lists the same choices in the same
// order, and the engine words its own messages with these names.
using System;

namespace Ohman {

    /// <summary>The order of every multiple-choice control, once. The page segment, the tray submenu and the code
    /// that ticks them all index the same arrays, so reordering a choice cannot leave one of them pointing elsewhere.</summary>
    static class Choice {
        public static readonly string[] Fan = { "Auto", "Max", "Curve", "Manual" };
        public static readonly FanMode[] FanModes = { FanMode.Auto, FanMode.Max, FanMode.Custom, FanMode.Manual };
        public static int Of(FanMode m) { for (int i = 0; i < FanModes.Length; i++) if (FanModes[i] == m) return i; return 0; }

        public static readonly string[] Key = { "Cycle", "Panel", "Max fan", "Run", "Off" };
        public static readonly KeyAction[] KeyActions = { KeyAction.Cycle, KeyAction.Show, KeyAction.MaxFan, KeyAction.Run, KeyAction.Off };
        public static int Of(KeyAction a) { for (int i = 0; i < KeyActions.Length; i++) if (KeyActions[i] == a) return i; return 0; }

        // lighting is two settings (mode 0 off / 1 ours / 2 Windows, and the effect) shown as one list
        public static readonly string[] Light = { "Off", "Static", "Breathe", "Cycle", "Wave", "Windows" };
        public static int OfLight(int mode, int effect) { return mode == 2 ? 5 : mode == 0 ? 0 : 1 + Math.Max(0, Math.Min(3, effect)); }
        public static int LightMode(int index) { return index == 5 ? 2 : index == 0 ? 0 : 1; }
        public static int LightEffect(int index) { return index >= 1 && index <= 4 ? index - 1 : 0; }
    }
}
