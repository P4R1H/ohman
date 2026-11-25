// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: how a mailbox call reaches the firmware here. src\win\Mailbox.cs is the same class for
// Windows. Everything above this (opcodes, payloads, what a reply means) is shared and lives in src\Hardware.cs.
//
// The firmware is behind /dev/ohman-wmi, the passthrough module in linux\driver\ohman-wmi.c. The stock hp-wmi
// driver has no raw interface in any kernel version, and acpi_call cuts replies off below what the 1024 and
// 4096 byte methods return. The device is root's, so there are two ways to it:
//   * directly, when this process can open it (ohman-probe under sudo, ohman-helper itself);
//   * through ohman-helper's socket, which is how the GUI gets there as an ordinary user.
// Route decides which, once, and everything else on Linux that needs root (EC bytes, MSRs, key events) follows
// the same answer.
using System;
using System.IO;

namespace Ohman {
    /// <summary>Which way this process reaches the hardware.</summary>
    static class Route {
        public enum Via { None, Device, Helper }
        static Via found = Via.None;

        /// <summary>The device when it opens, else the helper when it answers, else None. A yes is kept; a no is
        /// asked again next time (cheaply: HelperClient remembers a failure for two seconds), because both the
        /// module and the helper can appear after Ohman has started.</summary>
        public static Via Now {
            get {
                if (found != Via.None) return found;
                // An explicit helper socket wins: it is how a test points a root-less process at a simulated helper.
                bool forced = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HelperProtocol.SocketVariable));
                if (!forced && KernelModule.Usable) found = Via.Device;
                else if (HelperClient.Reachable) found = Via.Helper;
                else if (forced && KernelModule.Usable) found = Via.Device;
                return found;
            }
        }

        /// <summary>Why there is no route, as one sentence for an exception or a status row.</summary>
        public static string WhyNone {
            get {
                if (KernelModule.Present && Native.IsRoot) return "cannot open " + KernelModule.Device;
                string helper = HelperClient.LastError;
                if (!KernelModule.Present && !File.Exists(HelperProtocol.SocketPath))
                    return "no route to the firmware: the ohman-wmi kernel module is not loaded and ohman-helper is not running (" + Host.InstallHint + " says what is missing)";
                return "no route to the firmware: " + (helper.Length > 0 ? helper : KernelModule.Device + " is root's and ohman-helper is not answering");
            }
        }
    }

    static class Mailbox {
        public const string Device = KernelModule.Device;

        /// <summary>Whether the passthrough module's device node exists. Says nothing about being allowed to use it.</summary>
        public static bool Present { get { return KernelModule.Present; } }

        /// <summary>Whether a mailbox call can be made from this process right now, either way. This is what a
        /// front end asks before choosing between the real hardware and the simulated one.</summary>
        public static bool Reachable { get { return Route.Now != Route.Via.None; } }

        /// <summary>Where the calls go, for the diagnostics line.</summary>
        public static string Describe { get { Route.Via v = Route.Now; return v == Route.Via.Device ? Device : v == Route.Via.Helper ? "ohman-helper (" + HelperProtocol.SocketPath + ")" : "nothing (no route)"; } }

        /// <summary>"device", "helper" or "none", for the reports.</summary>
        public static string RouteName { get { Route.Via v = Route.Now; return v == Route.Via.Device ? "device" : v == Route.Via.Helper ? "helper" : "none"; } }

        /// <summary>One call. outSize must be 0, 4, 128, 1024 or 4096. Returns the firmware's reply and its return
        /// code; a call that fails as a call (rather than being refused by the firmware) throws. The caller holds
        /// the lock: one call at a time.</summary>
        public static byte[] Invoke(uint command, uint commandType, byte[] data, int outSize, out uint rc) {
            switch (Route.Now) {
                case Route.Via.Device: return KernelModule.Call(command, commandType, data, outSize, out rc);
                case Route.Via.Helper:
                    try { return HelperClient.Mailbox(command, commandType, data, outSize, out rc); }
                    catch (HelperException ex) { throw new IOException("the mailbox call failed as a call: " + ex.Message); }
                default: throw new InvalidOperationException(Route.WhyNone);
            }
        }
    }
}
