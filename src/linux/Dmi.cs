// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: what the machine says it is (SMBIOS, as the kernel publishes it under /sys/class/dmi/id).
// src\win\Dmi.cs is the same class for Windows. What those answers mean for a profile is shared and lives in
// src\Platform.cs. These files are world-readable, so none of this needs root.
using System;
using System.IO;

namespace Ohman {
    static class Dmi {
        const string Root = "/sys/class/dmi/id/";

        static string Read(string name) {
            try { return File.ReadAllText(Root + name).Trim(); } catch { }
            return "";
        }

        /// <summary>Baseboard product id, e.g. "8C58". Empty when unavailable.</summary>
        public static string Board() { return Read("board_name"); }

        /// <summary>The model name HP gave the machine. Empty when unavailable.</summary>
        public static string Model() { return Read("product_name"); }

        /// <summary>SMBIOS chassis types. The kernel publishes the one type of the first enclosure.</summary>
        public static int[] ChassisTypes() {
            int t;
            if (int.TryParse(Read("chassis_type"), out t)) return new int[] { t };
            return new int[0];
        }
    }
}
