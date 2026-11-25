// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: Windows Dynamic Lighting, which does not exist here. src\win\WinLighting.cs is the real
// one. Nothing on Linux paints the keyboard behind Ohman's back, so nothing has to be taken or given back:
// every answer is no and every request does nothing. Host.HasDynamicLighting tells the UI to drop the choice.
namespace Ohman {
    public static class WinLighting {
        public static bool KeyboardFound { get { return false; } }
        public static void Warm() { }
        public static void Forget() { }
        public static bool Present { get { return false; } }
        public static bool HasControl { get { return false; } }
        public static void SetControl(bool windows) { }
    }
}
