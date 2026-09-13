// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — platform profiles. Everything model-specific lives here so that adding a laptop means adding a profile,
// not touching the engine or the UI. A board that is not listed still runs: Platforms.Generic builds a profile
// from what the firmware reports about itself.
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
        public int TdpBase = 30, TdpGainMax = 15;   // concurrent CPU+GPU budget: base and the "Smart Performance Gain" range
        public byte[] GpuBase = { 0, 0, 1, 75 }, GpuBoost = { 0, 1, 1, 87 }, GpuMax = { 1, 1, 1, 87 };
        public uint KeyEventId = 29, KeyEventData = 8613;   // hpqBEvnt of the OMEN key
        // Features that not every OMEN has. A model without one keeps the row hidden and never sends the command.
        public bool HasPowerGain = true;    // 0x29 concurrent CPU+GPU budget ("Smart Performance Gain")
        public bool HasGpuPower = true;     // 0x22 cTGP / PPAB (discrete NVIDIA GPU with Dynamic Boost)
        public FanCurve Curve = FanCurve.Transcend14();
        public int RpmPerLevel = 100;       // what one fan level is worth on screen; 0 = the levels are already a percentage
        public GuardLimits Guard = new GuardLimits();
        public bool Verified = true;        // false = built at run time from the firmware's answers (generic mode)
        public string Notes;
    }

    /// <summary>
    /// The software fan curve the vendor app runs on this model (OGH stores it in profiles.json). Fan level = RPM/100.
    /// Auto mode drives the fans with this because the firmware's own fallback is not reliable after software control:
    /// measured 2026-09-08, after max fan expired the firmware first reapplied the last written level (0) for minutes.
    /// </summary>
    /// <summary>When to stop trusting the curve and force the fans, and when it is safe to stop forcing them. The
    /// chassis numbers belong to this board's 0x23 sensor, so they are model data like the curve itself.</summary>
    public sealed class GuardLimits {
        public int CpuHot = 90, ChassisHot = 56;        // engage at or above either
        public int CpuSafe = 78, ChassisSafe = 48;      // release after SafeSeconds below both
        public int SafeSeconds = 60;
        public int StallCpu = 70, StallLevelSum = 10;   // warm, but both fans reading under ~500 rpm
        public int MaxFanCoolBelow = 60, MaxFanCoolSeconds = 120;   // when a max-fan session hands itself back
        public int WarnAt = 80;                         // the amber temperature on the Home page
    }

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
        /// <summary>OGH's own curve for the Transcend 14, read out of its profiles.json. Every unverified OMEN
        /// inherits it and rescales it to whatever its own fan table tops out at, which is the best guess available.</summary>
        public static FanCurve Transcend14() { return new FanCurve(); }
        /// <summary>Stretch the level tables to a different ceiling (models whose levels are percent rather than rpm/100).</summary>
        public void Rescale(int newCeiling) {
            if (newCeiling == Ceiling || newCeiling <= 0) return;
            double f = newCeiling / (double)Ceiling;
            for (int i = 0; i < CpuLevels.Length; i++) CpuLevels[i] = (int)Math.Round(CpuLevels[i] * f);
            for (int i = 0; i < GpuLevels.Length; i++) GpuLevels[i] = (int)Math.Round(GpuLevels[i] * f);
            for (int i = 0; i < IrLevels.Length; i++) IrLevels[i] = (int)Math.Round(IrLevels[i] * f);
            Fallback = (int)Math.Round(Fallback * f); Ceiling = newCeiling;
        }
    }

    /// <summary>Board families the Linux hp-wmi driver drives through the same 0x1A mode command. Used to build a
    /// generic profile for a board that has no verified entry yet; the firmware's system-design data decides the rest.</summary>
    public static class Families {
        // Boards the Linux hp-wmi driver recognises as OMEN, from BOTH of its tables: omen_thermal_profile_boards[]
        // and the later hp_wmi_feature_boards[] DMI table, whose omen_v1 entries use the same profile set
        // (HP_OMEN_V1_THERMAL_PROFILE_COOL = 0x50). Checked against drivers/platform/x86/hp/hp-wmi.c on 2026-09-13.
        public static readonly string[] Omen = { "84DA", "84DB", "84DC", "8572", "8573", "8574", "8575", "8600", "8601", "8602", "8603", "8604", "8605", "8606", "8607", "860A",
            "8746", "8747", "8748", "8749", "874A", "8786", "8787", "8788", "878A", "878B", "878C", "87B5", "886B", "886C", "88C8", "88CB", "88D1", "88D2", "88F4", "88F5",
            "88F6", "88F7", "88FD", "88FE", "88FF", "8900", "8901", "8902", "8912", "8917", "8918", "8949", "894A", "89EB", "8A15", "8A42", "8A43", "8BAD", "8C58", "8E41",
            // hp_wmi_feature_boards[]: driven with omen_v1 / omen_v1_legacy / omen_v1_no_ec params
            "8A44", "8A4D", "8BA9", "8BAA", "8BAB", "8BB3", "8BC2", "8BCA", "8BCD", "8C76", "8C77", "8C78", "8D26", "8D41", "8D87", "8D88", "8DD6", "8E35" };
        public static readonly string[] OmenForceV0 = { "8607", "8746", "8747", "8748", "8749", "874A" };   // report v1 but want v0 bytes
        public static readonly string[] Victus = { "88F8", "8A25" };                                       // 0x00 default, 0x01 performance, 0x03 quiet
        public static readonly string[] VictusS = { "8A3D", "8B2F", "8BBE", "8BD4", "8BD5", "8C99", "8C9C" };   // 0x00 default, 0x01 performance
        public static bool In(string[] list, string board) { foreach (var b in list) if (string.Equals(b, board, StringComparison.OrdinalIgnoreCase)) return true; return false; }
    }

    public static class Platforms {
        /// <summary>A profile for a board without a verified entry. Null when the firmware generation is unknown (stay read-only).</summary>
        public static PlatformProfile Generic(string board, SystemInfo info) {
            if (info == null || !info.Valid) return null;
            var p = new PlatformProfile { Name = "Generic OMEN/Victus (board " + board + ")", Boards = new[] { board }, Verified = false, ThermalPolicy = info.ThermalPolicy };
            p.Curve = FanCurve.Transcend14();
            if (Families.In(Families.Victus, board)) { p.ModeEco = 0x03; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x03; p.Notes = "Victus family (hp-wmi victus_thermal_profile_boards)"; }
            else if (Families.In(Families.VictusS, board)) { p.ModeEco = 0x00; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x00; p.Notes = "Victus S family"; }
            else if (Families.In(Families.OmenForceV0, board) || info.ThermalPolicy == 0) { p.ModeEco = 0x00; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x02; p.ThermalPolicy = 0; p.Notes = "thermal policy v0"; }
            else if (info.ThermalPolicy == 1) {
                // 0x50 (Cool) is documented only for the boards the kernel lists. Anywhere else, leave the
                // quieter-Eco switch writing the ordinary Eco byte rather than one we cannot source.
                bool listed = Families.In(Families.Omen, board);
                if (!listed) p.ModeCool = p.ModeEco;
                p.Notes = "thermal policy v1" + (listed ? ", listed in hp-wmi" : ", not in hp-wmi: no Cool profile");
            }
            else return null;
            p.TdpBase = info.DefaultConcurrentTdp; p.HasPowerGain = info.DefaultConcurrentTdp > 0;
            p.HasGpuPower = false;                                        // the engine probes 0x21 and turns this on when the firmware answers
            return p;
        }

        // A verified laptop is one entry here. Everything a profile does not say has a default on PlatformProfile,
        // and anything the firmware can answer for itself (zone count, graphics modes, base TDP) is read at run time.
        public static readonly PlatformProfile[] Known = {
            new PlatformProfile {
                Name = "HP OMEN Transcend 14 (2024, 14-fb0xxx)",
                Boards = new[] { "8C58" },
                ThermalPolicy = 1,
                ModeEco = 0x30, ModeBalanced = 0x30, ModePerformance = 0x31, ModeCool = 0x50,
                TdpBase = 30, TdpGainMax = 15,
                GpuBase = new byte[] { 0, 0, 1, 75 }, GpuBoost = new byte[] { 0, 1, 1, 87 }, GpuMax = new byte[] { 1, 1, 1, 87 },
                KeyEventId = 29, KeyEventData = 8613,
                RpmPerLevel = 100,
                Notes = "Core Ultra 9 185H + RTX 4070. Modes, fans, power and GPU verified 2026-09-08 against OMEN Gaming Hub 1101.2608 logs and code; four-zone keyboard lighting verified on the device 2026-09-12."
            }
        };

        public static string BoardOverride;         // --board: test aid
        /// <summary>DMI baseboard product id, e.g. "8C58". Empty when unavailable.</summary>
        public static string ReadBoard() {
            if (!string.IsNullOrEmpty(BoardOverride)) return BoardOverride;
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
            foreach (var p in Known) foreach (var b in p.Boards) if (string.Equals(b, board, StringComparison.OrdinalIgnoreCase)) { Floor(p); return p; }
            return null;
        }
        /// <summary>No profile may drive a fan below the lowest level any HP firmware has been measured to keep spinning.</summary>
        public static PlatformProfile Floor(PlatformProfile p) {
            if (p != null && p.Curve != null && p.Curve.Floor < Bios.AbsoluteFloor) p.Curve.Floor = Bios.AbsoluteFloor;
            return p;
        }
    }
}
