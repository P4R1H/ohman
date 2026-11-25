// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: whether the laptop is running on its battery. src\linux\Power.cs is the same class for Linux.
namespace Ohman {
    static class Power {
        /// <summary>True on battery. Throws when Windows cannot say, and the caller keeps what it had.</summary>
        public static bool OnBattery() {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        }
    }
}
