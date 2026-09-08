// Ohman — platform profiles. Everything model-specific lives here so that adding a laptop means adding a profile,
// not touching the engine or the UI. A machine whose board is not listed runs read-only (no BIOS writes).
using System;
using System.Collections.Generic;
using System.Management;

namespace Ohman {

    /// <summary>What the app knows about one laptop model. Values come from the vendor app's own behaviour and logs.</summary>
    public sealed class PlatformProfile {
        public string Name;                 // human name
        public string[] Boards;             // DMI baseboard product ids this profile applies to (Win32_BaseBoard.Product)
        public int ThermalPolicy = 1;       // expected byte 3 of system-design data (1 = modes 0x30/0x31/0x50)
        public byte ModeEco = 0x30, ModeBalanced = 0x30, ModePerformance = 0x31, ModeCool = 0x50;
        public int FanCount = 2;
        public int FanLevelMax = 57;        // OGH's slider bound on this model (levels are RPM/100)
        public int TdpBase = 30, TdpGainMax = 15;   // concurrent CPU+GPU budget: base and the "Smart Performance Gain" range
        public byte[] GpuBase = { 0, 0, 1, 75 }, GpuBoost = { 0, 1, 1, 87 }, GpuMax = { 1, 1, 1, 87 };
        public uint KeyEventId = 29, KeyEventData = 8613;   // hpqBEvnt of the OMEN key
        public FanCurve Curve = new FanCurve();
        public string Notes;
    }

    /// <summary>
    /// The software fan curve the vendor app runs on this model (OGH stores it in profiles.json). Fan level = RPM/100.
    /// Auto mode drives the fans with this because the firmware's own fallback is not reliable after software control:
    /// measured 2026-09-08, after max fan expired the firmware first reapplied the last written level (0) for minutes.
    /// </summary>
    public sealed class FanCurve {
        public int[] CpuTemps = { 50, 55, 60, 65, 70, 75, 80, 85, 90 };
        public int[] CpuLevels = { 23, 23, 25, 32, 39, 46, 46, 46, 49 };
        public int[] GpuTemps = { 50, 55, 60, 65, 70, 75, 80, 85, 90 };
        public int[] GpuLevels = { 23, 25, 31, 35, 46, 46, 46, 46, 46 };
        public int[] IrTemps = { 40, 52 };           // BIOS chassis/IR sensor (0x23)
        public int[] IrLevels = { 0, 46 };
        public int Floor = 18, Ceiling = 57;         // OGH's bounds; the firmware's own table never goes below 19
        public int StepPerTick = 3;                  // OGH moves ~3 levels per update
        public int Fallback = 35;                    // level used when no temperature is available at all

        static int Interp(int[] xs, int[] ys, double x) {
            if (xs.Length == 0) return 0;
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) { double t = (x - xs[i - 1]) / (double)(xs[i] - xs[i - 1]); return (int)Math.Round(ys[i - 1] + t * (ys[i] - ys[i - 1])); }
            return ys[ys.Length - 1];
        }

        /// <summary>Target level pair for the given temperatures (NaN = sensor unavailable).</summary>
        public int[] Target(double cpu, double gpu, double ir) {
            // without a CPU or GPU reading (first seconds after start, or sensors broken) use the conservative fallback;
            // the chassis sensor alone is not enough to judge how hot the silicon is
            bool silicon = !double.IsNaN(cpu) || !double.IsNaN(gpu);
            int c = double.IsNaN(cpu) ? 0 : Interp(CpuTemps, CpuLevels, cpu);
            int g = double.IsNaN(gpu) ? 0 : Interp(GpuTemps, GpuLevels, gpu);
            int r = double.IsNaN(ir) ? 0 : Interp(IrTemps, IrLevels, ir);
            int lvl = silicon ? Math.Max(c, Math.Max(g, r)) : Math.Max(Fallback, r);
            lvl = Math.Max(Floor, Math.Min(Ceiling, lvl));
            return new int[] { lvl, lvl };
        }

        /// <summary>Move one step from the current level towards the target, like OGH's smoothing.</summary>
        public int Step(int current, int target) {
            if (current < Floor) return Math.Max(Floor, Math.Min(target, Floor + StepPerTick * 2));   // coming from off/unknown: get to the floor quickly
            int d = target - current;
            if (Math.Abs(d) <= StepPerTick) return target;
            return current + Math.Sign(d) * StepPerTick;
        }

        public int Clamp(int level) { return Math.Max(Floor, Math.Min(Ceiling, level)); }
    }

    public static class Platforms {
        public static readonly PlatformProfile[] Known = {
            new PlatformProfile {
                Name = "HP OMEN Transcend 14 (2024, 14-fb0xxx)",
                Boards = new[] { "8C58" },
                Notes = "Core Ultra 9 185H + RTX 4070. Verified 2026-09-08 against OMEN Gaming Hub 1101.2608 logs, decompiled code, hp-wmi.c and OmenMon."
            }
        };

        /// <summary>DMI baseboard product id, e.g. "8C58". Empty when unavailable.</summary>
        public static string ReadBoard() {
            try {
                using (var s = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard"))
                    foreach (ManagementObject mo in s.Get()) return (mo["Product"] as string ?? "").Trim();
            } catch { }
            return "";
        }

        public static string ReadModel() {
            try {
                using (var s = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                    foreach (ManagementObject mo in s.Get()) return (mo["Model"] as string ?? "").Trim();
            } catch { }
            return "";
        }

        public static PlatformProfile Find(string board) {
            foreach (var p in Known) foreach (var b in p.Boards) if (string.Equals(b, board, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }
}
