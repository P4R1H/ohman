// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the CPU's own registers, through the driver. Shared: what the registers mean. How they are reached
// is a CpuPort, which the Driver class of each operating system hands out (src\win\Driver.cs: PawnIO;
// src\linux\Driver.cs: /dev/cpu/N/msr through the root helper).
//
// The ACPI zone Windows exposes is whatever the firmware put there: sometimes the package sensor, sometimes a
// skin sensor, on a few boards a constant 28. The CPU itself knows, and says so in a register only ring 0 can
// read, along with the power limits it is holding and why it is slowing down.
//
// Intel (SDM vol. 4, cross-checked against LibreHardwareMonitor's IntelCpu.cs): TjMax in IA32_TEMPERATURE_TARGET
// 0x1A2 bits 23:16; the distance below it and the throttle flags in IA32_PACKAGE_THERM_STATUS 0x1B1; PL1/PL2 in
// MSR_PKG_POWER_LIMIT 0x610, scaled by MSR_RAPL_POWER_UNIT 0x606; energy in MSR_PKG_ENERGY_STATUS 0x611.
// AMD (Amd17Cpu.cs): the die temperature is not an MSR but SMN register THM_TCON_CUR_TMP 0x59800; energy is
// MSR_PKG_ENERGY_STAT 0xC001029B.
//
// Reads, and on Intel one write: MSR_PKG_POWER_LIMIT, which the stock signed IntelMSR module allow-lists
// (is_allowed_msr_write: MSR_PKG_POWER_LIMIT among six), to set the limit OGH itself sets per mode.
using System;
using System.Threading;

namespace Ohman {

    /// <summary>What one poll said. NaN where this CPU or this driver cannot say.</summary>
    public sealed class CpuTelemetry {
        public double DieTemp = double.NaN;         // the package sensor, degrees C
        public int TjMax;                           // where the CPU starts to throttle; 0 = unknown
        public double Pl1 = double.NaN, Pl2 = double.NaN;   // watts allowed, long and short term
        public bool Pl1On, Pl2On, PlLocked;
        public double Watts = double.NaN;           // package power over the last poll interval
        public double Mhz = double.NaN;             // what the cores actually ran at over that interval
        public string Throttle = "";                // "" or why the CPU is being held back right now
    }

    /// <summary>How the registers are reached on this operating system. True means the read or write happened.</summary>
    public abstract class CpuPort : IDisposable {
        public abstract bool ReadMsr(uint msr, out ulong value);
        /// <summary>Only ever asked for the package power limit; each implementation refuses anything else.</summary>
        public abstract bool WriteMsr(uint msr, ulong value);
        /// <summary>AMD's SMN bus, where the die temperature lives. False where it cannot be reached.</summary>
        public virtual bool ReadSmn(uint address, out uint value) { value = 0; return false; }
        /// <summary>Two per-core counters read from the same core, one straight after the other. False when the
        /// pair could not be had from one core: an unpinned pair can difference one core's counter against
        /// another's for a ratio that looks entirely plausible, and no reading beats a wrong one.</summary>
        public abstract bool ReadPair(uint first, uint second, out ulong a, out ulong b);
        /// <summary>Why the last write was refused, in the words the settings row shows.</summary>
        public virtual string WriteRefusal { get { return "the driver refused the write"; } }
        public virtual void Dispose() { }
    }

    /// <summary>The CPU's registers behind an interface, so the preview build can pretend to have them.</summary>
    public abstract class CpuRegisters : IDisposable {
        readonly object sync = new object();
        protected readonly CpuPort Port;
        ulong lastEnergy;
        DateTime lastEnergyAt = DateTime.MinValue;
        protected double EnergyUnit;                // joules per count, read once from the CPU

        protected CpuRegisters(CpuPort port) { Port = port; }

        public abstract string Describe { get; }
        protected abstract CpuTelemetry PollCore();

        /// <summary>One reading. Serialised: the sensor loop polls every couple of seconds and the driver check
        /// polls eighty times in two, and the energy counter is a difference against the last call, so two
        /// callers sharing an instance would take each other's measurement window.</summary>
        public CpuTelemetry Poll() { return Poll(true); }
        /// <summary>full false is a light poll: the temperature and limits only. The driver report samples this
        /// eighty times in two seconds to characterise the die sensor, and a full poll would spoil two other
        /// things each time: the clock pins the caller to core 0, so the spread would be one core measured
        /// through an affinity change rather than the sensor free-running; and the energy counter is a
        /// difference against the last call, so eighty calls leave the sensor thread a window too short to
        /// divide by and its wattage drops out for a tick.</summary>
        public CpuTelemetry Poll(bool full) {
            lock (sync) {
                light = !full;
                try { return PollCore(); } finally { light = false; }
            }
        }
        bool light;

        public virtual void Dispose() { if (Port != null) Port.Dispose(); }

        // Architectural on both vendors. APERF counts cycles the core actually ran, MPERF counts them at the
        // base clock, so their ratio over an interval is the average multiplier for that interval. Windows'
        // "% Processor Performance" counter is a sampled estimate of the same ratio, which is why it was the
        // number people kept reporting as wrong.
        const uint IA32_MPERF = 0xE7, IA32_APERF = 0xE8;
        ulong lastAperf, lastMperf;
        bool haveClock;
        protected double BaseMhz;                   // max non-turbo, set by the subclass

        /// <summary>The average clock since the last call. Both counters are per core, so a thread that moved
        /// cores between calls would difference one core's counter against another's and produce nonsense; the
        /// reads are pinned to core 0 for that reason (CpuPort.ReadPair), and the result is that core's clock
        /// rather than a package average, which is the same thing the Windows counter approximates.</summary>
        protected double Clock() {
            if (BaseMhz <= 0 || light) return double.NaN;
            ulong a, m;
            if (!Port.ReadPair(IA32_APERF, IA32_MPERF, out a, out m)) return double.NaN;
            double mhz = double.NaN;
            if (haveClock) {
                ulong da = a - lastAperf, dm = m - lastMperf;
                // A core that was parked reports no cycles, and a migration that slipped past the pin reports a
                // ratio no chip can hold. Both mean "no reading this time", never a number.
                if (dm > 0 && da > 0 && da < dm * 12) mhz = BaseMhz * da / (double)dm;
            }
            lastAperf = a; lastMperf = m; haveClock = true;
            return mhz > 200 && mhz < 12000 ? mhz : double.NaN;
        }

        /// <summary>The nominal clock the kernel recorded at boot, which is the base clock on every machine we
        /// have seen. Only a fallback: Intel states it exactly in a register.</summary>
        protected static double NominalMhz() { return Driver.NominalMhz(); }

        /// <summary>Set the package limits OGH sets for this mode. Null when they are in place, else why not.
        /// The first value read is kept and put back by RestorePackageLimits.</summary>
        public virtual string SetPackageLimits(int pl1, int pl2) { return "not on this CPU"; }
        public virtual void RestorePackageLimits() { }
        protected bool WriteMsr(uint msr, ulong v) { return Port != null && Port.WriteMsr(msr, v); }

        protected bool Msr(uint msr, out ulong v) {
            bool ok = Port.ReadMsr(msr, out v);
            if (!ok) v = 0;
            return ok;
        }

        /// <summary>Energy counters are 32-bit and wrap; the difference over the interval is the power.</summary>
        protected double Power(ulong now) {
            if (light) return double.NaN;              // and leave the window for the caller that owns it
            DateTime t = DateTime.UtcNow;
            double w = double.NaN;
            if (lastEnergyAt != DateTime.MinValue) {
                double secs = (t - lastEnergyAt).TotalSeconds;
                ulong delta = (now - lastEnergy) & 0xFFFFFFFFu;
                if (secs > 0.2 && secs < 120) w = delta * EnergyUnit / secs;
            }
            lastEnergy = now;
            lastEnergyAt = t;
            return w;
        }

        /// <summary>The vendor the kernel recorded at boot; no CPUID instruction needed from here.</summary>
        public static string Vendor() { return Driver.CpuVendor(); }
        /// <summary>Cached: the settings row asks on every refresh and a CPU does not change under us.</summary>
        static int isIntel;
        public static bool IsIntel {
            get {
                if (isIntel == 0) isIntel = Vendor().IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : -1;
                return isIntel == 1;
            }
        }

        /// <summary>The register set for this machine's CPU, or null with the reason.</summary>
        public static CpuRegisters Open(out string why) { bool absent; return Open(out why, out absent); }
        public static CpuRegisters Open(out string why, out bool deviceAbsent) {
            string vendor = Vendor();
            deviceAbsent = false;
            if (IsIntel) {
                CpuPort m = Driver.OpenCpu("IntelMSR", out why, out deviceAbsent);
                return m == null ? null : new IntelCpu(m);
            }
            if (vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0) {
                CpuPort m = Driver.OpenCpu("AMDFamily17", out why, out deviceAbsent);
                return m == null ? null : new AmdCpu(m);
            }
            why = "unknown CPU vendor '" + vendor + "'";
            return null;
        }
    }

    sealed class IntelCpu : CpuRegisters {
        const uint IA32_THERM_STATUS = 0x19C, IA32_TEMPERATURE_TARGET = 0x1A2, IA32_PACKAGE_THERM_STATUS = 0x1B1;
        const uint MSR_RAPL_POWER_UNIT = 0x606, MSR_PKG_POWER_LIMIT = 0x610, MSR_PKG_ENERGY_STATUS = 0x611;
        const uint MSR_PLATFORM_INFO = 0xCE;
        int tjMax;
        bool baseTried;
        double powerUnit;
        public IntelCpu(CpuPort port) : base(port) { }
        ulong firstLimit;
        bool firstKnown;
        readonly object limitSync = new object();
        const ulong LimitBits = 0x7FFFul | (0x7FFFul << 32);

        public override string SetPackageLimits(int pl1, int pl2) {
            lock (limitSync) {
                ulong v;
                if (powerUnit == 0) { if (!Msr(MSR_RAPL_POWER_UNIT, out v)) return "power unit unreadable"; powerUnit = 1.0 / (1 << (int)(v & 0xF)); }
                if (!Msr(MSR_PKG_POWER_LIMIT, out v)) return "0x610 unreadable";
                if ((v & (1ul << 63)) != 0) return "0x610 is locked by the firmware";
                if (!firstKnown) {
                    firstLimit = v; firstKnown = true;
                    Log.Write("cpu limits before Ohman: PL1 " + ((v & 0x7FFF) * powerUnit).ToString("0") + " W, PL2 " + (((v >> 32) & 0x7FFF) * powerUnit).ToString("0") + " W");
                }
                // The value is OGH's own for this mode (Platforms.CpuLimits); 15..125 W is a sanity bound, PL2 never under PL1.
                double w1 = Math.Max(15, Math.Min(125, pl1)), w2 = Math.Max(w1, Math.Min(125, pl2));
                ulong u1 = (ulong)Math.Round(w1 / powerUnit) & 0x7FFF, u2 = (ulong)Math.Round(w2 / powerUnit) & 0x7FFF;
                // Time windows and clamp bits stay as the firmware set them; only the two limits and their enables move.
                ulong nv = (v & ~LimitBits) | u1 | (1ul << 15) | (u2 << 32) | (1ul << 47);
                if (nv == v) return null;
                if (!WriteMsr(MSR_PKG_POWER_LIMIT, nv)) return Port.WriteRefusal;
                ulong back;
                if (!Msr(MSR_PKG_POWER_LIMIT, out back) || (back & LimitBits) != (nv & LimitBits)) return "written, but 0x610 did not keep it";
                return null;
            }
        }
        public override void RestorePackageLimits() {
            lock (limitSync) {
                ulong v;
                if (!firstKnown || !Msr(MSR_PKG_POWER_LIMIT, out v) || v == firstLimit || (v & (1ul << 63)) != 0) return;
                if (WriteMsr(MSR_PKG_POWER_LIMIT, firstLimit)) Log.Write("cpu limits put back as they were before Ohman");
            }
        }
        public override string Describe { get { return "Intel MSR" + (tjMax > 0 ? " · TjMax " + tjMax : ""); } }

        protected override CpuTelemetry PollCore() {
            var t = new CpuTelemetry();
            ulong v;
            if (tjMax == 0 && Msr(IA32_TEMPERATURE_TARGET, out v)) { tjMax = (int)((v >> 16) & 0xFF); if (tjMax == 0) tjMax = 100; }
            t.TjMax = tjMax;
            if (tjMax > 0 && Msr(IA32_PACKAGE_THERM_STATUS, out v) && (v & 0x80000000u) != 0) {
                t.DieTemp = tjMax - (int)((v >> 16) & 0x7F);
                // Each status bit has a sticky log bit above it, which is history and not wanted here.
                if ((v & (1u << 0)) != 0) t.Throttle = "thermal";
                else if ((v & (1u << 2)) != 0) t.Throttle = "PROCHOT";
                else if ((v & (1u << 10)) != 0) t.Throttle = "power limit";
            }
            // 0x19C is per core, but the current limit it reports is a package-level regulator decision, so
            // whichever core this thread landed on is a fair sample.
            if (t.Throttle.Length == 0 && Msr(IA32_THERM_STATUS, out v) && (v & (1u << 12)) != 0) t.Throttle = "current limit";
            if (powerUnit == 0 && Msr(MSR_RAPL_POWER_UNIT, out v)) {
                powerUnit = 1.0 / (1 << (int)(v & 0xF));
                EnergyUnit = 1.0 / (1 << (int)((v >> 8) & 0x1F));
            }
            if (powerUnit > 0 && Msr(MSR_PKG_POWER_LIMIT, out v)) {
                t.Pl1 = (v & 0x7FFF) * powerUnit; t.Pl1On = (v & (1ul << 15)) != 0;
                t.Pl2 = ((v >> 32) & 0x7FFF) * powerUnit; t.Pl2On = (v & (1ul << 47)) != 0;
                t.PlLocked = (v & (1ul << 63)) != 0;
            }
            if (EnergyUnit > 0 && Msr(MSR_PKG_ENERGY_STATUS, out v)) t.Watts = Power(v & 0xFFFFFFFFu);
            // Bits 15:8 are the max non-turbo ratio, times the 100 MHz bus clock every part since Sandy Bridge
            // uses. Read once; it does not change.
            if (!baseTried) {
                baseTried = true;
                if (Msr(MSR_PLATFORM_INFO, out v)) BaseMhz = ((v >> 8) & 0xFF) * 100.0;
                if (BaseMhz <= 0) BaseMhz = NominalMhz();
            }
            t.Mhz = Clock();
            return t;
        }
    }

    /// <summary>AMD Ryzen, family 17h to 1Ah. No power limits or throttle reasons: those live in the SMU's
    /// power-management table, which is a different module and a much larger job.</summary>
    sealed class AmdCpu : CpuRegisters {
        const uint THM_TCON_CUR_TMP = 0x00059800;
        const uint MSR_PWR_UNIT = 0xC0010299, MSR_PKG_ENERGY_STAT = 0xC001029B;
        bool baseTried;
        public AmdCpu(CpuPort port) : base(port) { }
        public override string Describe { get { return "AMD SMN + MSR"; } }

        bool Smn(uint addr, out uint v) { return Port.ReadSmn(addr, out v); }

        protected override CpuTelemetry PollCore() {
            var t = new CpuTelemetry();
            uint r;
            if (Smn(THM_TCON_CUR_TMP, out r)) {
                // bits 31:21 in eighths of a degree; bit 19 shifts the range down by 49 (Zen 2 and later).
                double tctl = ((r >> 21) & 0x7FF) * 0.125;
                if ((r & (1u << 19)) != 0) tctl -= 49;
                if (tctl > 0 && tctl < 130) t.DieTemp = tctl;
            }
            ulong v;
            if (EnergyUnit == 0 && Msr(MSR_PWR_UNIT, out v)) EnergyUnit = 1.0 / (1 << (int)((v >> 8) & 0x1F));
            if (EnergyUnit > 0 && Msr(MSR_PKG_ENERGY_STAT, out v)) t.Watts = Power(v & 0xFFFFFFFFu);
            // No equivalent of Intel's platform-info register here, so the kernel's nominal figure it is.
            if (!baseTried) { baseTried = true; BaseMhz = NominalMhz(); }
            t.Mhz = Clock();
            return t;
        }
    }

    /// <summary>Simulated registers for the preview build.</summary>
    public sealed class DemoCpu : CpuRegisters {
        readonly Random rnd = new Random();
        double temp = 52;
        public DemoCpu() : base(null) { }
        public override string Describe { get { return "simulated"; } }
        protected override CpuTelemetry PollCore() {
            temp = Math.Max(38, Math.Min(88, temp + rnd.Next(-2, 3)));
            return new CpuTelemetry { DieTemp = temp, TjMax = 110, Pl1 = 45, Pl2 = 80, Pl1On = true, Pl2On = true, Watts = 12 + rnd.Next(0, 9), Mhz = 2300 + rnd.Next(0, 1800), Throttle = temp > 84 ? "power limit" : "" };
        }
    }
}
