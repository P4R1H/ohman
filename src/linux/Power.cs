// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: whether the laptop is running on its battery, from /sys/class/power_supply.
// src\win\Power.cs is the same class for Windows.
using System;
using System.IO;

namespace Ohman {
    static class Power {
        const string Root = "/sys/class/power_supply";

        /// <summary>True on battery. Throws when the kernel lists nothing to tell by, and the caller keeps what
        /// it had. The barrel jack is a "Mains" supply; a laptop charged over USB-C alone lists only "USB"
        /// supplies, whose online flag also covers a phone plugged in to charge, so there the battery's own
        /// status decides: it is discharging, or it is not.</summary>
        public static bool OnBattery() {
            bool sawMains = false, online = false, sawBattery = false, discharging = false;
            foreach (string dir in Directory.GetDirectories(Root)) {
                string type;
                try { type = File.ReadAllText(Path.Combine(dir, "type")).Trim(); } catch { continue; }
                if (type == "Mains") {
                    sawMains = true;
                    try { if (File.ReadAllText(Path.Combine(dir, "online")).Trim() == "1") online = true; } catch { }
                } else if (type == "Battery") {
                    // A wireless mouse reports itself as a battery too; the laptop's own has scope System (or none).
                    try { if (File.ReadAllText(Path.Combine(dir, "scope")).Trim() == "Device") continue; } catch { }
                    sawBattery = true;
                    try { if (File.ReadAllText(Path.Combine(dir, "status")).Trim() == "Discharging") discharging = true; } catch { }
                }
            }
            if (sawMains) return !online;
            if (sawBattery) return discharging;
            throw new InvalidOperationException("no mains supply and no battery under " + Root);
        }
    }
}
