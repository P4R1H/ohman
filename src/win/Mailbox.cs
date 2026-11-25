// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: how a mailbox call reaches the firmware here, through the WMI class root\wmi:hpqBIntM.
// src\linux\Mailbox.cs is the same class for Linux. Everything above this (opcodes, payloads, what a reply
// means) is shared and lives in src\Hardware.cs; nothing in here knows what a command is for.
using System;
using System.Globalization;
using System.Management;

namespace Ohman {
    static class Mailbox {
        static readonly byte[] SIGN = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"

        /// <summary>Where the calls go, for the diagnostics line.</summary>
        public static string Describe { get { return "root\\wmi hpqBIntM"; } }

        static ManagementObject cached;                       // the hpqBIntM instance; found once, dropped again on any WMI error
        static ManagementObject Interface() {
            if (cached != null) return cached;
            using (var s = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM hpqBIntM"))
                foreach (ManagementObject mo in s.Get()) { cached = mo; return mo; }
            throw new InvalidOperationException("hpqBIntM WMI class has no instance (not an HP OMEN, or not elevated)");
        }

        /// <summary>One call. outSize must be 0, 4, 128, 1024 or 4096. Returns the firmware's reply and its return
        /// code; a call that fails as a call (rather than being refused by the firmware) throws. The caller holds
        /// the lock: one call at a time.</summary>
        public static byte[] Invoke(uint command, uint commandType, byte[] data, int outSize, out uint rc) {
            try { return InvokeWmi(command, commandType, data, outSize, out rc); }
            catch (ManagementException) { try { if (cached != null) cached.Dispose(); } catch { } cached = null; throw; }
        }

        static byte[] InvokeWmi(uint command, uint commandType, byte[] data, int outSize, out uint rc) {
            ManagementObject intf = Interface();
            using (var cls = new ManagementClass("root\\wmi", "hpqBDataIn", null))
            using (ManagementBaseObject din = cls.CreateInstance()) {
                din["Sign"] = SIGN;
                din["Command"] = command;
                din["CommandType"] = commandType;
                din["Size"] = (uint)data.Length;
                din["hpqBData"] = data;
                string method = "hpqBIOSInt" + outSize.ToString(CultureInfo.InvariantCulture);
                using (ManagementBaseObject inP = intf.GetMethodParameters(method)) {
                    inP["InData"] = din;
                    using (ManagementBaseObject outP = intf.InvokeMethod(method, inP, null))
                    using (var outD = (ManagementBaseObject)outP["OutData"]) {
                        rc = Convert.ToUInt32(outD["rwReturnCode"], CultureInfo.InvariantCulture);
                        if (rc != 0 || outSize == 0) return new byte[0];
                        byte[] o = outD["Data"] as byte[];
                        return o ?? new byte[0];
                    }
                }
            }
        }
    }
}
