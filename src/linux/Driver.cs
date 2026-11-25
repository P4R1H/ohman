// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the optional driver as the shared engine sees it. src\win\Driver.cs is the same class for
// Windows, where it is PawnIO. Here "the driver" is the ohman-wmi kernel module (which the mailbox needs anyway)
// plus what the root helper adds: the embedded controller's bytes through the module's ec_read/ec_write, and
// the CPU's registers through the kernel's msr driver. What the engine does with them (verify the EC before a
// single write, choose the fan route, set the package limit OGH sets) is shared and unchanged.
//
// It cannot be installed from the GUI: a kernel module wants DKMS and root, a service wants systemd. The
// installer does that. Host.CanInstallDriver is false, and Install below says where to go instead of pretending.
//
// UNTESTED on real hardware. In particular: that ec_read/ec_write reach the same registers the port handshake
// does on Windows (they should: both are the ACPI EC protocol), and that MSR 0x610 can be written on a laptop
// with Secure Boot off.
using System;
using System.Collections.Generic;
using System.IO;

namespace Ohman {
    /// <summary>What an install came back with. On Linux only Failed ever happens.</summary>
    public enum DriverInstallResult { Installed, RestartNeeded, Failed }

    /// <summary>The names the Windows UI reads off the driver class. Kept so a front end ported line by line
    /// compiles; nothing on Linux is PawnIO.</summary>
    public static class PawnIo {
        public const string HomeUrl = "https://github.com/" + Update.Repo;
        public const string SourceUrl = "https://github.com/" + Update.Repo + "/tree/main/linux/driver";
        public static Version MinVersion { get { return Driver.MinVersion; } }
        public static bool Signed(string path, out string signer) { signer = null; return false; }
        public static bool Signed(string path, string expected, out string signer) { signer = null; return false; }
    }

    /// <summary>The EC a register at a time: the module's ioctls directly, or the same through the helper.</summary>
    sealed class LinuxEc : IEcRegisters {
        public int Read(byte register, out byte value) {
            switch (Route.Now) {
                case Route.Via.Device: return KernelModule.EcRead(register, out value);
                case Route.Via.Helper: return HelperClient.EcRead(register, out value);
                default: value = 0; return Native.ENODEV;
            }
        }
        public int Write(byte register, byte value) {
            switch (Route.Now) {
                case Route.Via.Device: return KernelModule.EcWrite(register, value);
                case Route.Via.Helper: return HelperClient.EcWrite(register, value);
                default: return Native.ENODEV;
            }
        }
    }

    /// <summary>The CPU's registers: /dev/cpu/0/msr directly, or the same through the helper.</summary>
    sealed class LinuxCpuPort : CpuPort {
        string refusal = "the kernel refused the write";

        public override bool ReadMsr(uint msr, out ulong value) {
            switch (Route.Now) {
                case Route.Via.Device: return Msr.Read(msr, out value) == 0;
                case Route.Via.Helper: return HelperClient.MsrRead(msr, out value) == 0;
                default: value = 0; return false;
            }
        }

        public override bool WriteMsr(uint msr, ulong value) {
            int rc;
            string why = null;
            switch (Route.Now) {
                case Route.Via.Device: rc = Msr.Write(msr, value); break;
                case Route.Via.Helper: rc = HelperClient.MsrWrite(msr, value, out why); break;
                default: rc = Native.ENODEV; break;
            }
            if (rc == 0) return true;
            refusal = why ?? Driver.MsrRefusal(rc);
            return false;
        }

        /// <summary>Both from core 0, because that is the only core's file ever opened (Msr.Device).</summary>
        public override bool ReadPair(uint first, uint second, out ulong a, out ulong b) {
            b = 0;
            return ReadMsr(first, out a) && ReadMsr(second, out b);
        }

        // The AMD die temperature is on the SMN bus, behind PCI configuration space. The kernel's k10temp driver
        // reads exactly that register and publishes it, so Sensors takes it from hwmon and nothing here needs root.
        public override string WriteRefusal { get { return refusal; } }
    }

    public static class Driver {
        public const string Name = "ohman-wmi";

        /// <summary>The module's interface version as a version number: 0.1 the mailbox only, 0.2 with the EC
        /// bytes. Null when there is no way to the module at all.</summary>
        public static Version InstalledVersion() {
            try {
                switch (Route.Now) {
                    case Route.Via.Device: return new Version(0, KernelModule.Version());
                    case Route.Via.Helper:
                        Dictionary<string, string> st = HelperClient.Status();
                        string v; int n;
                        if (st.TryGetValue("module_version", out v) && int.TryParse(v, out n) && n > 0) return new Version(0, n);
                        return null;
                }
            } catch (Exception ex) { Log.Write("driver version: " + ex.Message); }
            return null;
        }
        /// <summary>0.2 is the first module with the EC bytes; an older one still does the mailbox.</summary>
        public static Version MinVersion { get { return new Version(0, KernelModule.WantVersion); } }

        /// <summary>"running" when the hardware can be reached, else what is missing.</summary>
        public static string ServiceState() {
            switch (Route.Now) {
                case Route.Via.Device: return "running";
                case Route.Via.Helper: return "running";
                default: return File.Exists(HelperProtocol.SocketPath) ? "not answering" : "not running";
            }
        }

        internal static string MsrRefusal(int errno) {
            if (errno == Native.EPERM || errno == Native.EACCES) {
                string lockdown = Msr.Lockdown();
                if (lockdown.Length > 0 && lockdown != "none") return "the kernel refused the write: lockdown is on (" + lockdown + ", usually Secure Boot)";
                return "the kernel refused the write (msr.allow_writes is off, or not root)";
            }
            if (errno == Native.ENOENT || errno == Native.ENODEV) return "the msr kernel module is not loaded";
            return "the write failed: " + Native.Why(errno);
        }

        /// <summary>The CPU's registers. module is the PawnIO module name the shared code picks by vendor; it
        /// means nothing here. deviceAbsent is never set: the EC does not depend on the msr driver.</summary>
        public static CpuPort OpenCpu(string module, out string why, out bool deviceAbsent) {
            why = null;
            deviceAbsent = false;
            Route.Via via = Route.Now;
            if (via == Route.Via.None) { why = Route.WhyNone; deviceAbsent = true; return null; }
            var port = new LinuxCpuPort();
            // One register every supported CPU has, to tell "there" from "not there" before the engine relies on it.
            bool intel = CpuVendor().IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;
            ulong v;
            if (port.ReadMsr(intel ? 0x1A2u : 0xC0010299u, out v)) return port;
            if (via == Route.Via.Device) why = Msr.Present ? "cannot read " + Msr.Device : "the msr kernel module is not loaded (modprobe msr)";
            else why = "ohman-helper cannot read the CPU's registers (is the msr kernel module loaded?)";
            return null;
        }

        /// <summary>The embedded controller, a register at a time behind the port conversation the shared
        /// EmbeddedController speaks (EcRegisterPorts). Null with the reason when the module is too old.</summary>
        public static IEcPorts OpenEc(out string why) {
            why = null;
            if (Route.Now == Route.Via.None) { why = Route.WhyNone; return null; }
            Version v = InstalledVersion();
            if (v == null || v < MinVersion) { why = "the ohman-wmi module is " + (v == null ? "not answering" : "version " + v + ", which has no EC access") + "; update it"; return null; }
            return new EcRegisterPorts(new LinuxEc());
        }

        public static DriverInstallResult Install(Action<string> progress, out string error) {
            error = "the kernel module and the helper are installed by the installer, not from here: " + Host.InstallHint;
            Log.Write("driver install: " + error);
            return DriverInstallResult.Failed;
        }
        public static bool Uninstall(out string error) {
            error = "the kernel module and the helper are removed by the uninstaller, not from here";
            return false;
        }

        static string cpuinfoVendor;
        /// <summary>"GenuineIntel" or "AuthenticAMD", from /proc/cpuinfo.</summary>
        public static string CpuVendor() {
            if (cpuinfoVendor != null) return cpuinfoVendor;
            string found = "";
            try {
                foreach (string line in File.ReadLines("/proc/cpuinfo")) {
                    if (!line.StartsWith("vendor_id", StringComparison.Ordinal)) continue;
                    int c = line.IndexOf(':');
                    if (c > 0) found = line.Substring(c + 1).Trim();
                    break;
                }
            } catch { }
            cpuinfoVendor = found;
            return found;
        }

        /// <summary>The base (non-turbo) clock in MHz, 0 when the kernel does not say. intel_pstate publishes it;
        /// under acpi-cpufreq the advertised maximum is the base clock, because boost is a separate switch. The
        /// other drivers (amd-pstate) advertise the boosted maximum, which is not it, so they answer 0 and the
        /// clock comes from cpufreq's own reading instead (Sensors).</summary>
        public static double NominalMhz() {
            const string cpu0 = "/sys/devices/system/cpu/cpu0/cpufreq/";
            double khz;
            if (double.TryParse(Host.ReadText(cpu0 + "base_frequency"), out khz) && khz > 0) return khz / 1000.0;
            if (Host.ReadText(cpu0 + "scaling_driver") == "acpi-cpufreq" && double.TryParse(Host.ReadText(cpu0 + "cpuinfo_max_freq"), out khz) && khz > 0) return khz / 1000.0;
            return 0;
        }
    }
}
