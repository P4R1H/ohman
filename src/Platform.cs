// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: platform profiles. Everything model-specific lives here so that adding a laptop means adding a profile,
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
        /// <summary>OGH's Unleashed mode (L8), or 0 where the board has none: no fourth mode is shown or sent.
        /// Set per board from an OGH capture (see Platforms.UnleashedBoards), never guessed from the policy.</summary>
        public byte ModeUnleashed = 0;
        /// <summary>CPU package limits in watts per mode (Eco, Balanced, Performance, Unleashed); null = this board's
        /// CPU limits are never touched. OghPl1/OghPl2 are what OMEN Gaming Hub itself writes per mode: the fallback
        /// when the CPU does not take ours. The ranges are the sliders: Eco PL1 (PL2 follows it), and the two
        /// experimental Unleashed sliders, each {min, max} in watts, 5 W steps.</summary>
        public int[] CpuPl1, CpuPl2, OghPl1, OghPl2;
        public int[] EcoPl1Range, UnlPl1Range, UnlPl2Range;
        /// <summary>The firmware takes PL1/PL2 on 0x29 (byte 0 PL2, byte 1 PL1), so no driver is needed for them; the
        /// driver, when present, only checks the result and is the fallback. BootPl1/BootPl2 = what the BIOS itself
        /// sets, put back on exit when there is no driver to read the original.</summary>
        public bool CpuLimitsViaBios;
        public int BootPl1, BootPl2;
        /// <summary>Eco drops the built-in display to its lowest refresh rate, as OMEN Gaming Hub does (default of the
        /// Settings switch; the owner can turn it off).</summary>
        public bool EcoLowHz;
        public int TdpBase = 30, TdpGainMax = 15;   // concurrent CPU+GPU budget: base and the "Smart Performance Gain" range
        public byte[] GpuBase = { 0, 0, 1, 75 }, GpuBaseCtgp = { 1, 0, 1, 87 }, GpuBoost = { 1, 1, 1, 87 };
        public uint KeyEventId = 29, KeyEventData = 8613;   // hpqBEvnt of the OMEN key
        // Features that not every OMEN has. A model without one keeps the row hidden and never sends the command.
        public bool HasPowerGain = true;    // 0x29 concurrent CPU+GPU budget ("Smart Performance Gain")
        public bool HasGpuPower = true;     // 0x22 cTGP / PPAB (discrete NVIDIA GPU with Dynamic Boost)
        public FanCurve Curve = FanCurve.Transcend14();
        public int RpmPerLevel = 100;       // what one fan level is worth on screen; 0 = the levels are already a percentage
        public GuardLimits Guard = new GuardLimits();
        public bool Verified = true;        // false = built at run time from the firmware's answers (generic mode)
        /// <summary>What the mailbox cannot do on this board and the driver can. This is the reason the Home
        /// page asks the owner to install it; nothing here changes what the mailbox is asked for.</summary>
        public DriverFor DriverFor = DriverFor.None;
        /// <summary>This generation's EC layout, or null: no map, no EC access, ever (see Ec.cs for why).</summary>
        public EcMap Ec;
        public string Notes;
    }

    /// <summary>The controls a board's firmware refuses through the mailbox. Set from evidence, one board at a time.</summary>
    [Flags]
    public enum DriverFor { None = 0, FanLevels = 1, MaxFan = 2 }

    /// <summary>
    /// The software fan curve the vendor app runs on this model (OGH stores it in profiles.json). Fan level = RPM/100.
    /// Auto mode drives the fans with this because the firmware's own fallback is not reliable after software control:
    /// measured 2026-09-08, after max fan expired the firmware first reapplied the last written level (0) for minutes.
    /// </summary>
    /// <summary>When to stop trusting the curve and force the fans, and when it is safe to stop forcing them. The
    /// chassis numbers belong to this board's 0x23 sensor, so they are model data like the curve itself.</summary>
    public sealed class GuardLimits {
        // Raised from 90/56 engage and 78/48 release after owners reported it firing on machines that were
        // merely working. These are a backstop for a fan that has stopped or a chassis with nowhere to put its
        // heat, not a second opinion on the firmware's own curve, which already holds the silicon well below
        // anything dangerous. 95 is still fifteen under the TjMax every part we have seen reports.
        public int CpuHot = 95, ChassisHot = 62;        // engage at or above either
        public int CpuSafe = 85, ChassisSafe = 54;      // release after SafeSeconds below both
        public int SafeSeconds = 60;
        public int StallCpu = 75, StallLevelSum = 10;   // warm, but both fans reading under ~500 rpm
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
        /// <summary>Whether the chassis sensor is allowed to raise the fans. Its reading is not on a scale we
        /// know on a board nobody has measured: one owner's 8BCD sits at 54 C idle, which against these
        /// thresholds pins the fans near the ceiling while the CPU is at 45. The thermal guard already refuses
        /// to trust this sensor until it has read cool once; the curve had no such caution.</summary>
        public bool UseChassis = true;
        /// <summary>False when the two fans follow their own curves. The profile curves are always linked: they
        /// describe one machine's cooling, not two independent fans. Only the user's custom curve unlinks.</summary>
        public bool Linked = true;
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
            int r = (double.IsNaN(ir) || !UseChassis) ? 0 : Interp(IrTemps, IrLevels, ir);
            // Unlinked: fan 1 follows the CPU curve and fan 2 the GPU curve (0x2C reports them in that order).
            // Both were previously given the maximum of the two, so drawing a separate GPU curve changed nothing
            // and the switch appeared to do nothing at all.
            //
            // It takes both readings to honour. With one sensor missing there is no second curve to follow, and
            // leaving that fan on the chassis level alone would idle it while the other chip climbs, so the
            // linked behaviour is the safe answer there.
            if (!Linked && !double.IsNaN(cpu) && !double.IsNaN(gpu))
                return new int[] { Clamp(Math.Max(c, r)), Clamp(Math.Max(g, r)) };
            int lvl = silicon ? Math.Max(c, Math.Max(g, r)) : Math.Max(Fallback, r);
            return new int[] { Clamp(lvl), Clamp(lvl) };
        }

        /// <summary>Move one step from the current level towards the target, like OGH's smoothing.</summary>
        public int Step(int current, int target) {
            if (target <= 0) return 0;                          // off is a destination, not a level to ramp towards
            if (current < Floor) return Math.Max(Floor, Math.Min(target, Floor + StepPerTick * 2));   // coming from off/unknown: get to the floor quickly
            int d = target - current;
            if (Math.Abs(d) <= StepPerTick) return target;
            return current + Math.Sign(d) * StepPerTick;
        }

        public int Clamp(int level) { return Math.Max(Floor, Math.Min(Ceiling, level)); }
        /// <summary>Clamp, except that off stays off. The floor is the lowest speed a fan will hold, so 1..17
        /// still comes up to it, but 0 is a level the owner is allowed to ask for and HP's own tables use.</summary>
        /// Exactly 0, not "0 or less". -1 is this engine's "we have not written a level yet" sentinel and it
        /// travels: treating it as off would stop the fans on the strength of a value that means nothing is known.
        public int ClampOrOff(int level) { return level == 0 ? 0 : Clamp(level); }
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
            Fallback = (int)Math.Round(Fallback * f);
            Ceiling = newCeiling;
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

        /// <summary>The 2025 OMEN MAX. Its EC is laid out differently from every generation before it, and the
        /// classic addresses written there corrupt its state (OmenCore issue #60: Caps Lock panic blink).</summary>
        public static readonly string[] OmenMax = { "8D41", "8D42", "8D87", "8D88" };

        /// <summary>Whether the legacy EC map is worth *trying* on this board.
        ///
        /// Being a candidate earns a board nothing on its own. EmbeddedController.Verify has to recognise its own
        /// registers on the actual machine - the control register holding a fan-control state and nothing else,
        /// the temperature register agreeing with the CPU's own sensor, the tachometers agreeing with the
        /// firmware - before a single byte is written anywhere. That proof is worth more than any list we could
        /// keep: it is taken on the laptop in front of us rather than inferred from a board id, and it is the
        /// reason this can be generous where an earlier version guessed from the first two hex digits of the id
        /// and handed the map to ninety boards nobody had touched.
        ///
        /// The one hard exclusion is the 2025 OMEN MAX, where the registers are somewhere else entirely and
        /// writing these addresses corrupts EC state until the Caps Lock light blinks (OmenCore issue #60).
        /// There is nothing to prove there and no reason to go looking.</summary>
        public static bool EcCandidate(string board) {
            return !string.IsNullOrEmpty(board) && !In(OmenMax, board);
        }
    }

    public static class Platforms {
        /// <summary>A profile for a board without a verified entry. Null when the firmware generation is unknown (stay read-only).</summary>
        /// <summary>Thermal-policy version for boards whose firmware refuses the system-data query. Some older
        /// firmware answers 0x1A perfectly well and returns rc 3 for 0x28, which leaves nothing to say whether the
        /// mode bytes are the v0 set or the v1 set, the Linux driver gives up in the same place and returns
        /// -EOPNOTSUPP. Anything in here came from a readback on the machine itself: an OMEN Gaming Hub log, or
        /// `omenprobe call 1A 4 FF 30 00 00` against `... FF 00 ...`. Never from a guess about the model's age.</summary>
        static readonly Dictionary<string, int> PolicyWhenFirmwareWontSay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            // { "8574", 0 },   // OMEN 15-dc1xxx: 0x28 returns rc 3. Waiting on a readback, see issue tracker.
        };

        /// <summary>The thermal-policy version, from the best source available: the kernel's own force-v0 list,
        /// then the firmware's answer, then a readback somebody contributed. -1 when nothing can say.</summary>
        static int PolicyVersion(string board, SystemInfo info) {
            if (Families.In(Families.OmenForceV0, board)) return 0;      // the kernel decides these without asking either
            if (info != null && info.Valid) return info.ThermalPolicy;
            int v;
            if (PolicyWhenFirmwareWontSay.TryGetValue(board ?? "", out v)) return v;
            return -1;
        }

        public static PlatformProfile Generic(string board, SystemInfo info) {
            bool haveInfo = info != null && info.Valid;
            int policy = PolicyVersion(board, info);
            if (policy < 0) return null;                                 // no source for the mode bytes: stay read-only
            // "Generic" read to owners as "your laptop is not supported", when it means the opposite: the profile
            // was built from what this firmware itself reported. An owner whose board is on the verified list was
            // shown those same words, which is worse again, so the name says which of the two it is.
            bool known = Reported(board);
            var p = new PlatformProfile {
                Name = "OMEN/Victus " + board + (known ? " (verified by its owner)" : " (from its own firmware)"),
                Boards = new[] { board }, Verified = known, ThermalPolicy = policy
            };
            p.Curve = FanCurve.Transcend14();
            p.Curve.UseChassis = false;     // thresholds measured on one chassis; see FanCurve.UseChassis
            if (Families.In(Families.Victus, board)) { p.ModeEco = 0x03; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x03; p.Notes = "Victus family (hp-wmi victus_thermal_profile_boards)"; }
            else if (Families.In(Families.VictusS, board)) { p.ModeEco = 0x00; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x00; p.Notes = "Victus S family"; }
            // A Victus the kernel has not listed still speaks Victus. 0x03 is HP_VICTUS_THERMAL_PROFILE_QUIET in
            // hp-wmi.c, and OGH sends 255,3,0,0 on a Victus 15 (8DCD) that is in no kernel table. Without this a
            // generic v0 Victus gets Eco 0x00, the same byte as Balanced, so its quiet mode does nothing at all.
            else if (policy == 0 && ReadModel().IndexOf("Victus", StringComparison.OrdinalIgnoreCase) >= 0) {
                p.ModeEco = 0x03; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x03;
                p.Notes = "thermal policy v0, Victus quiet byte";
            }
            else if (policy == 0) { p.ModeEco = 0x00; p.ModeBalanced = 0x00; p.ModePerformance = 0x01; p.ModeCool = 0x02; p.Notes = "thermal policy v0"; }
            else if (policy == 1) {
                // 0x50 (Cool) is documented only for the boards the kernel lists. Anywhere else, leave the
                // quieter-Eco switch writing the ordinary Eco byte rather than one we cannot source.
                bool listed = Families.In(Families.Omen, board);
                if (!listed) p.ModeCool = p.ModeEco;
                p.Notes = "thermal policy v1" + (listed ? ", listed in hp-wmi" : ", not in hp-wmi: no Cool profile");
            }
            else return null;
            if (!haveInfo) p.Notes += ", from a contributed readback (0x28 unavailable)";
            // Everything below is read from the machine. Without the system-data reply there is nothing to read,
            // so the features it would have described are not offered rather than guessed at.
            p.TdpBase = haveInfo ? info.DefaultConcurrentTdp : 0;
            p.HasPowerGain = p.TdpBase > 0;
            p.HasGpuPower = false;                                        // the engine probes 0x21 and turns this on when the firmware answers
            return Equip(p, board);
        }

        /// <summary>The two facts that are about the driver rather than about the firmware, applied to every
        /// profile however it was built. They used to be set only on generic profiles, so the moment a board was
        /// verified by its owner and promoted into Known it silently lost its EC map and its nudge - on 878A,
        /// the one board the EC route exists for, being verified would have taken its fan control away.</summary>
        static PlatformProfile Equip(PlatformProfile p, string board) {
            if (p == null) return null;
            if (p.Ec == null && Families.EcCandidate(board)) p.Ec = EcMap.Legacy();
            DriverFor need;
            if (p.DriverFor == DriverFor.None && DriverNeeds.TryGetValue(board ?? "", out need)) p.DriverFor = need;
            byte unleashed;
            if (p.ModeUnleashed == 0 && UnleashedBoards.TryGetValue(board ?? "", out unleashed)) p.ModeUnleashed = unleashed;
            CpuLimitSpec lim;
            if (p.CpuPl1 == null && CpuLimitBoards.TryGetValue(board ?? "", out lim)) {
                p.CpuPl1 = lim.Pl1; p.CpuPl2 = lim.Pl2; p.OghPl1 = lim.OghPl1; p.OghPl2 = lim.OghPl2;
                p.EcoPl1Range = lim.EcoPl1; p.UnlPl1Range = lim.UnlPl1; p.UnlPl2Range = lim.UnlPl2;
                p.CpuLimitsViaBios = lim.ViaBios; p.BootPl1 = lim.BootPl1; p.BootPl2 = lim.BootPl2;
                p.EcoLowHz = lim.EcoLowHz;
            }
            return p;
        }

        /// <summary>Boards where OMEN Gaming Hub offers Unleashed mode, with the byte it sends for it. Each entry is
        /// an OGH capture from that board. What OGH does in Unleashed, and what Ohman does with this byte:
        ///   0x1A  set mode {FF, 04, fansByBios, 00}            (Performance is 0x31, Eco/Balanced 0x30)
        ///   0x22  GPU {1,1,1,87} with Smart Performance Gain, {1,0,1,87} with it off   (GPU boost / GPU base)
        ///   0x29  concurrent budget 45 W = base 30 + UnleashedModeTppOffset 15; none with the gain off
        /// OGH also writes PL1/PL2 (default 65/77) through the CPU's MSR, not the mailbox, and clips PL1 on the
        /// chassis sensor; neither is part of this mode here.</summary>
        /// <summary>One board's CPU limits and Eco display rule. Modes in Eco, Balanced, Performance, Unleashed order.</summary>
        sealed class CpuLimitSpec {
            public int[] Pl1, Pl2, OghPl1, OghPl2, EcoPl1, UnlPl1, UnlPl2;
            public bool ViaBios, EcoLowHz;
            public int BootPl1, BootPl2;
        }
        /// <summary>Per-mode CPU limits, one board at a time, each from that board's own evidence.</summary>
        static readonly Dictionary<string, CpuLimitSpec> CpuLimitBoards = new Dictionary<string, CpuLimitSpec>(StringComparer.OrdinalIgnoreCase) {
            // OMEN Transcend 14-fb1xxx (2025), Core Ultra 9 285H.
            //  * Eco/Balanced/Performance: OGH's own values (HPOMENBG log 2026-09-24: SetPL1DefaultValue 45/77, 45/77,
            //    65/77). Eco's PL1 is a slider (25-45 W, PL2 follows it; 25 W measured to hold under Cinebench).
            //  * Unleashed 65/80: the BIOS's boot value read from 0x610 on 2026-09-25, identical in every mode. OGH's
            //    own Unleashed is 65/77 (UnleashedModePowerLimit1/2), the fallback. The experimental sliders use OGH's
            //    own Unleashed ranges: PL1 25-90 W, PL2 65-115 W.
            //  * 0x29 carries PL2/PL1 without a driver (measured 2026-09-25: 32 3C read back PL1 60 / PL2 50; 50 41 gave
            //    65/80; held across mode switches; PL2 65 respected). HWiNFO shows a second, dynamic copy (PL1 65,
            //    PL2 80) that neither route reaches: PL2 100 in the register still bursts to ~80-83 W.
            //  * OGH drops the built-in panel to 60 Hz in Eco and restores it on leaving (SetDisplayRefreshRate).
            { "8E41", new CpuLimitSpec {
                Pl1 = new[] { 45, 45, 65, 65 }, Pl2 = new[] { 45, 77, 77, 80 },
                OghPl1 = new[] { 45, 45, 65, 65 }, OghPl2 = new[] { 77, 77, 77, 77 },
                EcoPl1 = new[] { 25, 45 }, UnlPl1 = new[] { 25, 90 }, UnlPl2 = new[] { 65, 115 },
                ViaBios = true, BootPl1 = 65, BootPl2 = 80, EcoLowHz = true } },
        };

        static readonly Dictionary<string, byte> UnleashedBoards = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) {
            { "8E41", 0x04 },    // OMEN Transcend 14-fb1xxx (2025): HPOMENBG log 2026-09-24, "255,4,0,0" on every Unleashed apply
        };

        /// <summary>Boards whose mailbox is known to refuse a control the EC can do. Each entry is a field report.</summary>
        static readonly Dictionary<string, DriverFor> DriverNeeds = new Dictionary<string, DriverFor>(StringComparer.OrdinalIgnoreCase) {
            { "878A", DriverFor.FanLevels },     // OMEN 15 (2020): 0x2E answers rc 46 on every write, once a minute, forever; mode and max fan work
            { "8786", DriverFor.FanLevels },     // OMEN 15-en0 (2020): the same rc 46 on 0x2E, reported on Discord
        };

        // A verified laptop is one entry here. Everything a profile does not say has a default on PlatformProfile,
        // and anything the firmware can answer for itself (zone count, graphics modes, base TDP) is read at run time.
        public static readonly PlatformProfile[] Known = {
            new PlatformProfile {
                Name = "HP OMEN Transcend 14 (2024, 14-fb0xxx)",
                Boards = new[] { "8C58" },
                ThermalPolicy = 1,
                ModeEco = 0x30, ModeBalanced = 0x30, ModePerformance = 0x31, ModeCool = 0x50,
                TdpBase = 30, TdpGainMax = 15,
                GpuBase = new byte[] { 0, 0, 1, 75 }, GpuBaseCtgp = new byte[] { 1, 0, 1, 87 }, GpuBoost = new byte[] { 1, 1, 1, 87 },
                KeyEventId = 29, KeyEventData = 8613,
                RpmPerLevel = 100,
                Notes = "Core Ultra 9 185H + RTX 4070. Modes, fans, power and GPU verified 2026-09-08 against OMEN Gaming Hub 1101.2608 logs and code; four-zone keyboard lighting verified on the device 2026-09-12."
            }
        };

        /// <summary>Boards an owner has run and reported working. The profile stays the one built from the
        /// firmware's own answers, which is the right one for them; this only records that a human confirmed it,
        /// which marks it Verified and stops asking the next owner to be the first to try it. Verified only ever
        /// widens the thermal guard: it lets the chassis sensor arm a trigger before the sensor has read cool
        /// once, and makes release stricter, so an unexpected sensor scale costs a noisy fan, never less cooling.</summary>
        static readonly string[] OwnerReported = { "8748", "8EEC", "8DCF", "88D2", "88EE", "8BAB", "8A26",
                                                   "8BCD", "8BAD", "8787", "8E10", "8BBE", "8A4C", "8BB3", "8BCA", "8BD5", "8BC2", "8E35", "8C76", "8A25", "8D87" };
        public static bool Reported(string board) { return Families.In(OwnerReported, board); }

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
            foreach (var p in Known) foreach (var b in p.Boards) if (string.Equals(b, board, StringComparison.OrdinalIgnoreCase)) { Floor(p); return Equip(p, board); }
            return null;
        }
        /// <summary>Keep a profile's own curve off the 1..17 band, which is a speed no fan holds. 0 is left alone: it
        /// is off, which is a thing HP's own tables ask for.</summary>
        public static PlatformProfile Floor(PlatformProfile p) {
            if (p != null && p.Curve != null && p.Curve.Floor > 0 && p.Curve.Floor < Bios.AbsoluteFloor) p.Curve.Floor = Bios.AbsoluteFloor;
            return p;
        }
    }
}
