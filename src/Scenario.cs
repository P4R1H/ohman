// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the scripted run. One engine, a fixed script, simulated hardware, and every call the engine makes to
// the hardware written down in order. Windows prints it with "Ohman.exe --demo --scenario" and Linux with
// "ohman-probe --scenario", and the two texts must be the same text: linux\test\parity.sh runs both and
// compares them. That is the guard against the two builds drifting apart, because whatever else differs
// between them, the engine deciding to send a different byte to the firmware shows up here as a changed line.
//
// For the comparison to mean anything the run must not depend on the machine or the moment:
//   * the hardware is DemoHardware behind a recorder, and the one random reading it has (the chassis sensor)
//     is supplied by the script instead;
//   * the engine is Scripted (see Engine.Scripted): no timers, no wall clock, no battery state read from the
//     machine, no key watcher. The script turns the timers by hand and moves the clock itself;
//   * the settings are the defaults, never the file on disk, and nothing is saved;
//   * the board is named, never read from the machine.
// Nothing here sleeps, so the whole run takes well under a second.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ohman {

    /// <summary>DemoHardware with every call written down: the name, what went in, what came back.</summary>
    sealed class RecordingHardware : IHardware {
        readonly DemoHardware inner = new DemoHardware();
        readonly List<string> lines;
        /// <summary>What the chassis sensor (0x23) reads. The simulated one wanders at random; the script says.</summary>
        public int Chassis = 40;
        public RecordingHardware(List<string> transcript) { lines = transcript; }

        void Note(string s) { lines.Add("    hw " + s); }
        static string B(bool b) { return b ? "1" : "0"; }

        public bool IsDemo { get { return true; } }
        public int GetFanCount() { int r = inner.GetFanCount(); Note("GetFanCount -> " + r); return r; }
        public int GetFanCountPassive() { int r = inner.GetFanCountPassive(); Note("GetFanCountPassive -> " + r); return r; }
        public int GetFanTableMax() { int r = inner.GetFanTableMax(); Note("GetFanTableMax -> " + r); return r; }
        public int[] GetFanLevels() { int[] r = inner.GetFanLevels(); Note("GetFanLevels -> " + r[0] + "/" + r[1]); return r; }
        public int GetTemperature() { Note("GetTemperature -> " + Chassis); return Chassis; }
        public bool GetMaxFan() { bool r = inner.GetMaxFan(); Note("GetMaxFan -> " + B(r)); return r; }
        public GpuPowerState GetGpuPower() { GpuPowerState r = inner.GetGpuPower(); Note("GetGpuPower -> " + r); return r; }
        public SystemInfo GetSystemInfo() { SystemInfo r = inner.GetSystemInfo(); Note("GetSystemInfo -> " + r.Hex); return r; }
        public void SetMode(byte mode, bool fanControlByBios) { Note("SetMode 0x" + mode.ToString("X2") + " byBios=" + B(fanControlByBios)); inner.SetMode(mode, fanControlByBios); }
        public void SetMaxFan(bool on) { Note("SetMaxFan " + B(on)); inner.SetMaxFan(on); }
        public void SetFanLevels(int fan1, int fan2) { Note("SetFanLevels " + fan1 + "/" + fan2); inner.SetFanLevels(fan1, fan2); }
        public void SetConcurrentTdp(int watts) { Note("SetConcurrentTdp " + watts); inner.SetConcurrentTdp(watts); }
        public void SetCpuPowerLimits(int pl1, int pl2) { Note("SetCpuPowerLimits " + pl1 + "/" + pl2); inner.SetCpuPowerLimits(pl1, pl2); }
        public void SetGpuPower(bool customTgp, bool ppab, int peakTemp) { Note("SetGpuPower cTGP=" + B(customTgp) + " PPAB=" + B(ppab) + " peak=" + peakTemp); inner.SetGpuPower(customTgp, ppab, peakTemp); }
        public int GetGpuMode() { int r = inner.GetGpuMode(); Note("GetGpuMode -> " + r); return r; }
        public void SetGpuMode(int mode) { Note("SetGpuMode " + mode); inner.SetGpuMode(mode); }
    }

    /// <summary>The engine's keyboard with every write written down.</summary>
    sealed class RecordingLighting : ILighting {
        readonly ILighting inner;
        readonly List<string> lines;
        public RecordingLighting(ILighting light, List<string> transcript) { inner = light; lines = transcript; }
        public LightKind Kind { get { return inner.Kind; } }
        public int Zones { get { return inner.Zones; } }
        public bool Inert { get { return inner.Inert; } }
        public bool Numpad { get { return inner.Numpad; } }
        public string Describe { get { return inner.Describe; } }
        public Rgb[] GetColors() { return inner.GetColors(); }
        public void SetColors(Rgb[] zones) {
            var sb = new StringBuilder("    kb SetColors");
            foreach (Rgb c in zones) sb.Append(' ').Append(c.Hex);
            lines.Add(sb.ToString());
            inner.SetColors(zones);
        }
        public int GetBacklight() { return inner.GetBacklight(); }
        public void SetBacklight(bool on, int level) { lines.Add("    kb SetBacklight " + (on ? "on" : "off") + " " + level); inner.SetBacklight(on, level); }
    }

    public static class Scenario {
        /// <summary>A verified profile, a generic one, one whose firmware refuses fan levels, a current-generation
        /// OMEN and a thermal-policy v0 board: between them every branch the profile table can take.</summary>
        public static readonly string[] Boards = { "8C58", "8A25", "878A", "8BCD", "8607" };

        /// <summary>The whole transcript, every board, as text with \n line ends.</summary>
        public static string Run() {
            var sb = new StringBuilder();
            sb.Append("Ohman scenario ").Append(Program.Version).Append('\n');
            string saved = Platforms.BoardOverride;
            try {
                foreach (string board in Boards) {
                    var lines = new List<string>();
                    try { RunBoard(board, lines); }
                    catch (Exception ex) { lines.Add("  FAILED: " + ex.GetType().Name + ": " + ex.Message); }
                    foreach (string l in lines) sb.Append(l).Append('\n');
                }
            } finally { Platforms.BoardOverride = saved; }
            var bench = new List<string>();
            try { RunBench(bench); }
            catch (Exception ex) { bench.Add("  FAILED: " + ex.GetType().Name + ": " + ex.Message); }
            foreach (string l in bench) sb.Append(l).Append('\n');
            return sb.ToString();
        }

        /// <summary>The benchmark's numbers for frame series that are the same on every machine: what the result
        /// page would show for them must be the same text on both systems. The frames come from a small generator
        /// written out here, not from System.Random, so nothing rests on two runtimes agreeing about that.</summary>
        static void RunBench(List<string> lines) {
            lines.Add("benchmark");
            uint seed = 20260101;
            Func<double> next = delegate { seed = unchecked(seed * 1664525u + 1013904223u); return (seed >> 8) / 16777216.0; };
            // a game at about 87 fps with the odd hitch, the shape the simulated source draws
            var game = new List<double>();
            for (int i = 0; i < 12000; i++) game.Add(11.5 + (next() + next() + next() - 1.5) * 1.6 + (next() < 0.004 ? 30 : 0));
            Stats(lines, "12000 frames", FrameStats.Of(game));
            Stats(lines, "the first 2500", FrameStats.Of(game.GetRange(0, 2500)));
            Stats(lines, "the first 500", FrameStats.Of(game.GetRange(0, 500)));
            Stats(lines, "none", FrameStats.Of(new List<double>()));
            // frame generation: present times take turns, short and long
            var turns = new List<double>();
            for (int i = 0; i < 1200; i++) turns.Add(i % 2 == 0 ? 2.0 + next() * 0.4 : 12.0 + next() * 0.8);
            Stats(lines, "taking turns", FrameStats.Of(turns));
            lines.Add("  alternates: the game " + YesNo(Bench.Alternates(game)) + " · taking turns " + YesNo(Bench.Alternates(turns)));
            // a cap: display intervals tight on 1000/141 ms, and the game again, which has none
            var capped = new List<FrameRow>();
            for (int i = 0; i < 3000; i++) { double ms = 1000.0 / 141 + (next() - 0.5) * 0.05; capped.Add(new FrameRow { Ms = ms + (next() - 0.5) * 2, DisplayMs = ms, Displayed = true }); }
            var free = new List<FrameRow>();
            var bare = new List<FrameRow>();
            foreach (double ms in game) { free.Add(new FrameRow { Ms = ms, DisplayMs = ms, Displayed = true }); bare.Add(new FrameRow { Ms = ms }); }
            lines.Add("  cap at 144 Hz: capped " + Bench.Cap(capped, 144) + " · the game " + Bench.Cap(free, 144) + " · the game without display times " + Bench.Cap(bare, 144));
            var sorted = game.ToArray(); Array.Sort(sorted);
            lines.Add("  quantiles: p1 " + Num(FrameStats.Quantile(sorted, 0.01)) + " · p50 " + Num(FrameStats.Quantile(sorted, 0.5)) + " · p99.9 " + Num(FrameStats.Quantile(sorted, 0.999)) + " · of one value " + Num(FrameStats.Quantile(new[] { 7.0 }, 0.99)));
            lines.Add("  names: Cyberpunk2077 -> " + Games.Name("Cyberpunk2077", null, IntPtr.Zero) + " · eldenring -> " + Games.Name("eldenring", null, IntPtr.Zero));
            lines.Add("  not games: steam " + YesNo(Games.Denied("steam")) + " · PresentMon-2.6.0-x64 " + YesNo(Games.Denied("PresentMon-2.6.0-x64")) + " · Cyberpunk2077 " + YesNo(Games.Denied("Cyberpunk2077")) + " · nothing " + YesNo(Games.Denied("")));
            lines.Add("  tones: " + Tones.Wav(Tones.Start).Length + " and " + Tones.Wav(Tones.Done).Length + " bytes");
        }
        static string YesNo(bool b) { return b ? "yes" : "no"; }
        static string Num(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.000000", CultureInfo.InvariantCulture); }
        static void Stats(List<string> lines, string what, FrameStats s) {
            lines.Add("  " + what + ": n " + s.N + " · avg " + Num(s.AvgFps) + " · 1% low " + Num(s.Low1) + " · 0.1% low " + Num(s.Low01) + " · p50 " + Num(s.P50) + " · p99 " + Num(s.P99) + " · stutter " + Num(s.StutterPct) + " · sum " + Num(s.Sum));
        }

        sealed class Run1 {
            public Engine E;
            public RecordingHardware Hw;
            public List<string> Lines;
            public void Step(string what) { Lines.Add("  " + what); }
            public void Clock(int seconds) { E.ScriptedNow = E.ScriptedNow.AddSeconds(seconds); }
            /// <summary>What the engine believes after a step: the part of its state a front end would show.</summary>
            public void State() {
                Lines.Add("    = mode " + E.ModeName + " · fan " + E.S.Fan + " · written " + E.AutoLevel1 + "/" + E.AutoLevel2
                    + " · guard " + (E.GuardActive ? "on" : "off") + " · route " + E.Route + (E.FirmwareHolding ? " · held by firmware" : ""));
            }
            public void Temps(double cpu, double gpu, int chassis) {
                E.CpuTemp = cpu; E.CpuTempNow = cpu; E.GpuTemp = gpu; Hw.Chassis = chassis;
                Lines.Add("  temperatures: cpu " + cpu.ToString("0", CultureInfo.InvariantCulture) + " gpu " + gpu.ToString("0", CultureInfo.InvariantCulture) + " chassis " + chassis);
            }
            /// <summary>Time passes and a timer fires: 'f' the 5 s fan tick, 'g' the 10 s guard, 'h' the heartbeat.</summary>
            public void Tick(char which, int seconds) {
                Clock(seconds);
                Lines.Add("  +" + seconds + " s, " + (which == 'f' ? "fan tick" : which == 'g' ? "guard tick" : "heartbeat"));
                E.ScriptedTick(which);
            }
        }

        static void RunBoard(string board, List<string> lines) {
            Platforms.BoardOverride = board;
            lines.Add("board " + board);
            var s = new Settings();
            s.NoPersist = true;
            s.SyncWinPower = false;             // the system's own power mode is not the firmware, and not simulated
            s.Light = 1;                        // chosen, so nothing asks the machine who owns its keyboard
            var hw = new RecordingHardware(lines);
            var e = new Engine(hw, s);
            e.Scripted = true;
            var r = new Run1 { E = e, Hw = hw, Lines = lines };
            e.Toast += delegate(string message, bool error) { lines.Add("    " + (error ? "toast! " : "toast  ") + message); };
            e.CpuTemp = 50; e.CpuTempNow = 50; e.GpuTemp = 45;

            r.Step("start");
            e.Init(true);
            lines.Add("    profile " + (e.Supported ? e.P.Name + (e.Generic ? " [generic]" : " [verified]") : "unsupported")
                + " · fans " + e.FanCount + " · ceiling " + (e.P.Curve != null ? e.P.Curve.Ceiling : -1) + " · lighting " + (e.Light == null ? "none" : e.Light.Describe));
            r.State();
            if (e.Light != null) e.Light = new RecordingLighting(e.Light, lines);

            r.Step("mode: eco"); r.Clock(5); e.SetMode(0, false); r.State();
            r.Step("mode: performance"); r.Clock(5); e.SetMode(2, false); r.State();
            r.Step("mode: balanced"); r.Clock(5); e.SetMode(1, false); r.State();

            r.Step("fan: max"); r.Clock(5); e.SetFan(FanMode.Max, e.S.Fan1, e.S.Fan2, false); r.State();
            r.Tick('f', 5); r.Tick('g', 5); r.Tick('f', 5); r.Tick('g', 5); r.Tick('f', 5); r.Tick('g', 5);
            r.Tick('f', 30);
            r.State();
            r.Step("fan: auto"); r.Clock(5); e.SetFan(FanMode.Auto, e.S.Fan1, e.S.Fan2, false); r.State();

            r.Step("fan: manual 30/32"); r.Clock(5); e.SetFan(FanMode.Manual, 30, 32, false); r.State();
            r.Tick('f', 5); r.Tick('f', 30);
            r.Step("fan: manual, stopped"); r.Clock(5); e.SetFan(FanMode.Manual, 0, 0, false); r.State();

            r.Step("fan: auto, the profile's curve"); r.Clock(5); e.SetFan(FanMode.Auto, 30, 32, false); r.State();
            int[][] climb = { new[] { 50, 45, 40 }, new[] { 62, 50, 42 }, new[] { 75, 68, 45 }, new[] { 88, 80, 50 }, new[] { 88, 80, 50 }, new[] { 70, 60, 46 }, new[] { 55, 48, 42 }, new[] { 55, 48, 42 } };
            foreach (int[] t in climb) { r.Temps(t[0], t[1], t[2]); r.Tick('f', 5); }
            r.Temps(double.NaN, 48, 42); r.Tick('f', 5); r.Tick('f', 5); r.Tick('f', 5);      // the CPU reading goes away
            r.State();

            r.Step("fan: the owner's curve, seeded from the profile's"); r.Clock(5); r.Temps(60, 50, 42); e.SeedCurveFromVendor(); r.State();
            r.Temps(80, 70, 46); r.Tick('f', 5); r.Tick('f', 5);
            r.Step("curve floor 30"); e.SetCurveFloor(30); r.State();

            r.Step("thermal guard");
            r.Temps(97, 85, 50); r.Tick('g', 10); r.Tick('g', 10); r.State();
            r.Tick('f', 5); r.Tick('h', 5);
            r.Step("fan: manual while the guard holds"); e.SetFan(FanMode.Manual, 28, 28, false); r.State();
            r.Temps(60, 55, 44);
            for (int i = 0; i < 9; i++) r.Tick('g', 10);
            r.State();

            r.Step("guard holding a level instead of max"); e.SetGuardLimits(e.GuardCpuHot, e.GuardChassisHot, 40, e.GuardHoldSeconds);
            r.Temps(97, 85, 50); r.Tick('g', 10); r.Tick('g', 10); r.State();
            r.Step("guard switched off while engaged"); e.SetGuard(false); r.State();
            e.SetGuard(true);

            r.Step("fan: auto"); r.Clock(5); r.Temps(55, 48, 42); e.SetFan(FanMode.Auto, 30, 32, false);
            r.Step("on battery"); r.Clock(5); e.OnPowerSource(true); r.Tick('f', 5); r.Tick('h', 5); r.State();
            r.Step("plugged in"); r.Clock(5); e.OnPowerSource(false); r.Tick('f', 5); r.State();
            r.Step("display off"); r.Clock(5); e.OnDisplay(false); r.Tick('f', 5); r.Tick('h', 5); r.State();
            r.Step("display on"); r.Clock(5); e.OnDisplay(true); r.State();

            r.Step("power gain +10 W"); e.SetTdpOffset(10, false);
            r.Step("gpu: max"); e.SetGpu(GpuLevel.Max, false, false);
            r.Step("gpu: follows the mode"); e.SetGpu(GpuLevel.Base, true, false);
            r.Step("eco stays cool"); e.SetEcoCool(true); e.SetMode(0, false); e.SetEcoCool(false); e.SetMode(1, false);

            r.Step("keyboard: static");
            e.SetLight(1, 0, false);
            e.SetLightColor(null, new Rgb(255, 0, 0));
            e.SetLightColor(new int[] { 0, 3 }, new Rgb(0, 0, 255));
            e.SetLightLevel(50);
            r.Step("keyboard: off");
            e.SetLight(0, 0, false);

            r.Step("heartbeat"); r.Tick('h', 45); r.State();
            r.Step("exit"); e.Park(); r.State();
            try { e.Dispose(); } catch { }
        }
    }
}
