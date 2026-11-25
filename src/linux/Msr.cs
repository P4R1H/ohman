// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the CPU's model-specific registers through the kernel's msr driver (/dev/cpu/0/msr), and
// the two lists that say which of them Ohman touches at all. Root only, so this runs inside ohman-helper (and
// in ohman-probe under sudo); the GUI asks the helper, which checks the same lists again before it reads or
// writes anything. What the registers mean is shared and lives in src\Cpu.cs.
//
// Always core 0. The per-core counters Cpu.cs differences against each other (APERF, MPERF) have to come from
// one core, and reading a fixed core's file is how that is guaranteed here: no thread pinning needed.
//
// Kernel lockdown (Secure Boot on most distributions) allows reads and refuses writes with EPERM; so does
// msr.allow_writes=off. Both are reported as a refusal, never as an error to retry.
//
// UNTESTED on real hardware.
using System;
using System.IO;

namespace Ohman {
    static class Msr {
        public const string Device = "/dev/cpu/0/msr";
        /// <summary>MSR_PKG_POWER_LIMIT: the one register that is ever written (Cpu.cs, SetPackageLimits).</summary>
        public const uint PackagePowerLimit = 0x610;

        /// <summary>Every register src\Cpu.cs reads: Intel thermal status and target, RAPL units, limit and energy,
        /// platform info, MPERF/APERF; AMD's RAPL unit and package energy. Add one there, add it here.</summary>
        static readonly uint[] Readable = {
            0x19C, 0x1A2, 0x1B1, 0x606, 0x610, 0x611, 0xCE, 0xE7, 0xE8,
            0xC0010299, 0xC001029B
        };
        public static bool MayRead(uint msr) { return Array.IndexOf(Readable, msr) >= 0; }
        public static bool MayWrite(uint msr) { return msr == PackagePowerLimit; }

        static readonly object Sync = new object();
        static int rfd = -1, wfd = -1;

        /// <summary>Whether the msr driver is loaded (modprobe msr; the installer arranges it).</summary>
        public static bool Present { get { return File.Exists(Device); } }

        /// <summary>0, or the errno. EPERM/EACCES: not root. ENOENT: the msr module is not loaded. EIO: this
        /// CPU does not have the register.</summary>
        public static int Read(uint msr, out ulong value) {
            value = 0;
            if (!MayRead(msr)) return Native.EPERM;
            lock (Sync) {
                if (rfd < 0) {
                    if (!Present) return Native.ENOENT;
                    rfd = Native.open(Device, Native.O_RDONLY | Native.O_CLOEXEC);
                    if (rfd < 0) return Native.Errno;
                }
                var b = new byte[8];
                long n = Native.pread(rfd, b, new UIntPtr(8), msr).ToInt64();
                if (n != 8) { int e = Native.Errno; return e == 0 ? Native.EIO : e; }
                value = Native.Get64(b, 0);
                return 0;
            }
        }

        /// <summary>0, or the errno. EPERM is the kernel saying no: lockdown, or msr.allow_writes=off.</summary>
        public static int Write(uint msr, ulong value) {
            if (!MayWrite(msr)) return Native.EPERM;
            lock (Sync) {
                if (wfd < 0) {
                    if (!Present) return Native.ENOENT;
                    wfd = Native.open(Device, Native.O_WRONLY | Native.O_CLOEXEC);
                    if (wfd < 0) return Native.Errno;
                }
                var b = new byte[8];
                Native.Put64(b, 0, value);
                long n = Native.pwrite(wfd, b, new UIntPtr(8), msr).ToInt64();
                if (n != 8) { int e = Native.Errno; return e == 0 ? Native.EIO : e; }
                return 0;
            }
        }

        // ---------- the one write ----------
        /// <summary>The bits of MSR_PKG_POWER_LIMIT that IntelCpu.SetPackageLimits (src\Cpu.cs) can change: the two
        /// limits (14:0 and 46:32) and their enable bits (15 and 47). It leaves the clamp bits, the time windows
        /// and the lock bit as the firmware set them, so nothing else may differ in a write.</summary>
        public const ulong LimitWritable = 0x7FFFul | (1ul << 15) | (0x7FFFul << 32) | (1ul << 47);
        /// <summary>The watt range the client keeps both limits in (SetPackageLimits: 15..125 W).</summary>
        public const double LimitMinWatts = 15, LimitMaxWatts = 125;

        /// <summary>Whether a write of the package power limit is one Ohman makes. Null when it is; else why
        /// not. current is the register now, unitRegister is MSR_RAPL_POWER_UNIT (0x606), original is the value
        /// the register held before the first write through this process: putting exactly that back is always
        /// allowed, whatever watts the firmware had chosen, because it is how the limits are restored on exit.</summary>
        public static string LimitWriteRefusal(ulong current, ulong wanted, ulong unitRegister, bool haveOriginal, ulong original) {
            if ((current & (1ul << 63)) != 0) return "0x610 is locked by the firmware";
            if ((wanted & (1ul << 63)) != 0) return "a write that sets the lock bit of 0x610 is never made";
            if (((current ^ wanted) & ~LimitWritable) != 0) return "only the two power limits and their enable bits of 0x610 may change; this write changes other bits";
            if (haveOriginal && wanted == original) return null;
            if (!haveOriginal && wanted == current) return null;
            double unit = 1.0 / (1 << (int)(unitRegister & 0xF));
            double pl1 = (wanted & 0x7FFF) * unit, pl2 = ((wanted >> 32) & 0x7FFF) * unit;
            // Half a unit of slack: the client rounds watts to the register's unit.
            if (pl1 < LimitMinWatts - unit || pl1 > LimitMaxWatts + unit || pl2 < LimitMinWatts - unit || pl2 > LimitMaxWatts + unit)
                return "power limits of " + pl1.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " and " + pl2.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                    + " W are outside the " + LimitMinWatts + " to " + LimitMaxWatts + " W Ohman sets";
            return null;
        }

        /// <summary>What the kernel says about lockdown: "none", "integrity", "confidentiality", or "" when the
        /// file is not there (a kernel without the lockdown LSM).</summary>
        public static string Lockdown() {
            string s = Host.ReadText("/sys/kernel/security/lockdown");     // "none [integrity] confidentiality"
            int a = s.IndexOf('['), z = s.IndexOf(']');
            return a >= 0 && z > a ? s.Substring(a + 1, z - a - 1) : s;
        }

        /// <summary>The package energy counter the powercap driver keeps, in microjoules, and its wrap value.
        /// Root only since 5.10. False when there is no package domain.</summary>
        public static bool RaplEnergy(out ulong microjoules, out ulong max) {
            microjoules = 0; max = 0;
            try {
                const string root = "/sys/class/powercap";
                if (!Directory.Exists(root)) return false;
                foreach (string dir in Directory.GetDirectories(root)) {
                    // intel-rapl:0 is package 0 (the name file says "package-0"); intel-rapl:0:0 are its subdomains.
                    // AMD uses the same driver and the same names.
                    string name = Host.ReadText(Path.Combine(dir, "name"));
                    if (!name.StartsWith("package", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ulong.TryParse(Host.ReadText(Path.Combine(dir, "energy_uj")), out microjoules)) continue;
                    ulong.TryParse(Host.ReadText(Path.Combine(dir, "max_energy_range_uj")), out max);
                    return true;
                }
            } catch { }
            return false;
        }
    }
}
