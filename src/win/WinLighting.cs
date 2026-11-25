// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: Windows Dynamic Lighting, which paints the keyboard unless told to keep out.
// src\linux\WinLighting.cs is the same class for Linux, where there is no such thing and every answer is no.
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Ohman {

    /// <summary>Windows Dynamic Lighting owns the keyboard when its per-device "ambient" switch is on; OGH and Ohman
    /// take the keyboard by clearing that switch (the same registry value the Settings toggle writes) and give it back
    /// by setting it. Only HP's virtual lighting device (VHF) is touched, never external LampArray peripherals.</summary>
    public static class WinLighting {
        const string Root = @"Software\Microsoft\Lighting";

        /// <summary>Device interface paths of the LampArray devices that are keyboards, cached: enumerating HID opens
        /// every lighting device and is not something HasControl should do on every call.
        ///
        /// The registry subkey name is the device interface path without its \\?\ prefix, so a LampArray path maps
        /// straight onto one.</summary>
        static string[] keyboards;
        static string[] Keyboards() {
            if (keyboards != null) return keyboards;
            var r = new List<string>();
            try {
                foreach (var la in LampArray.All()) {
                    try {
                        if (la.Kind == LampArray.KindKeyboard && la.Internal && !string.IsNullOrEmpty(la.Path))
                            r.Add(la.Path.StartsWith(@"\\?\") ? la.Path.Substring(4) : la.Path);
                    } finally { try { la.Dispose(); } catch { } }
                }
            } catch (Exception ex) { Log.Write("keyboard lighting devices: " + ex.Message); }
            keyboards = r.ToArray();
            return keyboards;
        }

        /// <summary>True when the laptop's own keyboard has a lighting interface Windows can drive. Reads the cache
        /// only: the keyboard page asks this while it draws, and the enumeration belongs on the engine's thread
        /// (Warm). Unknown reads as no.</summary>
        public static bool KeyboardFound { get { var k = keyboards; return k != null && k.Length > 0; } }
        public static void Warm() { Keyboards(); }

        /// <summary>Drop the cached HID enumeration. The device set is not fixed for the life of the process and
        /// Ohman sits in the tray for days: a dock, an undock or an external RGB keyboard being plugged in changes
        /// it, and the failure is silent, we do not take a keyboard that has appeared and Windows repaints it
        /// against us with nothing to say why. There is no WM_DEVICECHANGE hook in the app, so this hangs off the
        /// power-mode events, which is where a dock or a resume already shows up.</summary>
        public static void Forget() { keyboards = null; }

        /// <summary>Every Dynamic Lighting entry Windows holds for a VHF device, unfiltered. Registry only: this is
        /// all Present needs, and Present is read while building the keyboard page, where a HID enumeration would
        /// run inline on the UI thread.</summary>
        static List<string> AllVhfKeys() {
            var all = new List<string>();
            try {
                using (var k = Registry.CurrentUser.OpenSubKey(Root + @"\Devices"))
                    if (k != null) foreach (string n in k.GetSubKeyNames()) if (n.IndexOf("HID_DEVICE_SYSTEM_VHF", StringComparison.OrdinalIgnoreCase) >= 0) all.Add(n);
            } catch { }
            return all;
        }

        /// <summary>The Dynamic Lighting entries this application should take, which is the laptop keyboard and
        /// nothing else.
        ///
        /// Every VHF entry used to be taken. On a machine with one that is the same thing, which is why it went
        /// unnoticed: the Transcend's VHF device is the keyboard. An OMEN MAX 16 has more than one, and the front
        /// light bar is among them, so taking the keyboard also took the bar away from Windows and dropped it onto
        /// its own firmware effect. That is the shape of the light bar reports.
        ///
        /// Falling back to every VHF entry when no keyboard can be identified keeps the old behaviour on any
        /// machine this cannot work out, rather than quietly letting Windows fight us for the keyboard.
        ///
        /// The keyboard is matched against every Dynamic Lighting entry, not only the VHF ones: a MAX 16's keyboard
        /// is a Darfon USB device and its only VHF entry is the light bar, so with one VHF entry the old shortcut
        /// took the bar and left the keyboard to Windows.</summary>
        static string[] DeviceKeys() {
            var vhf = AllVhfKeys();
            var kb = Keyboards();
            var mine = new List<string>();
            int entries = 0;
            try {
                using (var d = Registry.CurrentUser.OpenSubKey(Root + @"\Devices"))
                    if (d != null) foreach (string n in d.GetSubKeyNames()) {
                        entries++;
                        foreach (string k in kb)
                            if (string.Equals(n, k, StringComparison.OrdinalIgnoreCase)) { mine.Add(n); break; }
                    }
            } catch { }
            if (mine.Count == 0) return vhf.ToArray();
            if (mine.Count != entries) Log.Write("dynamic lighting: taking " + mine.Count + " of " + entries + " devices (the keyboard, not the rest)");
            return mine.ToArray();
        }
        /// <summary>True when Windows has a Dynamic Lighting entry for a VHF device (HP's HyperX Lighting driver
        /// present). Deliberately unfiltered: which entry is the keyboard only matters when we go to take one.</summary>
        public static bool Present { get { return AllVhfKeys().Count > 0; } }
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
