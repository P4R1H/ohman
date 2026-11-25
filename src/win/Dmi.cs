// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: what the machine says it is (SMBIOS, read through WMI). src\linux\Dmi.cs is the same
// class for Linux. What those answers mean for a profile is shared and lives in src\Platform.cs.
using System;
using System.Collections.Generic;
using System.Management;

namespace Ohman {
    static class Dmi {
        /// <summary>Baseboard product id, e.g. "8C58". Empty when unavailable.</summary>
        public static string Board() {
            try {
                using (var s = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard"))
                    foreach (ManagementObject mo in s.Get()) return (mo["Product"] as string ?? "").Trim();
            } catch { }
            return "";
        }

        /// <summary>The model name HP gave the machine. Empty when unavailable.</summary>
        public static string Model() {
            try {
                using (var s = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                    foreach (ManagementObject mo in s.Get()) return (mo["Model"] as string ?? "").Trim();
            } catch { }
            return "";
        }

        /// <summary>SMBIOS chassis types. Empty when unavailable.</summary>
        public static int[] ChassisTypes() {
            var list = new List<int>();
            try {
                using (var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                    foreach (ManagementObject mo in s.Get()) {
                        var t = mo["ChassisTypes"] as ushort[];
                        if (t != null) foreach (ushort c in t) list.Add((int)c);
                    }
            } catch { }
            return list.ToArray();
        }
    }
}
