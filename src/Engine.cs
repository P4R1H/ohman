// Ohman — control engine: settings, apply logic, keep-alive heartbeat, OMEN key watcher, OGH suppression.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ohman {

    public enum FanMode { Auto = 0, Max = 1, Manual = 2 }
    public enum KeyAction { Cycle = 0, Show = 1, MaxFan = 2, Off = 3 }
    public enum GpuLevel { Base = 0, Boost = 1, Max = 2 }   // {cTGP,PPAB} = {0,0} / {0,1} / {1,1} — the three payloads OGH sends

    /// <summary>Fan, power-gain and GPU choices are remembered per performance mode (like tabs): switching to a mode applies its own set.</summary>
    public sealed class ModeProfile {
        public FanMode Fan = FanMode.Auto;
        public int Fan1 = 30, Fan2 = 30;
        public int TdpOffset = 0;
        public GpuLevel Gpu = GpuLevel.Boost;
        public bool GpuAuto = true;                 // follow the mode: Eco->Base, Balanced->Boost, Performance->Max (what OGH does)
        public void Apply(string k, string v) {
            int n; bool b;
            switch (k) {
                case "Fan": if (Settings.TryInt(v, out n)) Fan = (FanMode)Math.Max(0, Math.Min(2, n)); break;
                case "Fan1": if (Settings.TryInt(v, out n)) Fan1 = n; break;
                case "Fan2": if (Settings.TryInt(v, out n)) Fan2 = n; break;
                case "TdpOffset": if (Settings.TryInt(v, out n)) TdpOffset = Math.Max(0, Math.Min(30, n)); break;   // the engine clamps again to the profile's range
                case "Gpu": if (Settings.TryInt(v, out n)) Gpu = (GpuLevel)Math.Max(0, Math.Min(2, n)); break;
                case "GpuAuto": if (bool.TryParse(v, out b)) GpuAuto = b; break;
            }
        }
        public void Write(StringBuilder sb, string prefix) {
            sb.AppendLine(prefix + "Fan=" + (int)Fan); sb.AppendLine(prefix + "Fan1=" + Fan1); sb.AppendLine(prefix + "Fan2=" + Fan2);
            sb.AppendLine(prefix + "TdpOffset=" + TdpOffset); sb.AppendLine(prefix + "Gpu=" + (int)Gpu); sb.AppendLine(prefix + "GpuAuto=" + GpuAuto);
        }
    }

    public sealed class Settings {
        public int ModeIndex = 1;                   // 0 Eco, 1 Balanced, 2 Performance
        // one profile per mode; Performance defaults to OGH's +15 W gain, the others to +0
        public readonly ModeProfile[] Modes = { new ModeProfile(), new ModeProfile(), new ModeProfile { TdpOffset = 15 } };
        public ModeProfile Cur { get { return Modes[Math.Max(0, Math.Min(2, ModeIndex))]; } }
        public FanMode Fan { get { return Cur.Fan; } set { Cur.Fan = value; } }
        public int Fan1 { get { return Cur.Fan1; } set { Cur.Fan1 = value; } }
        public int Fan2 { get { return Cur.Fan2; } set { Cur.Fan2 = value; } }
        public int TdpOffset { get { return Cur.TdpOffset; } set { Cur.TdpOffset = value; } }
        public GpuLevel Gpu { get { return Cur.Gpu; } set { Cur.Gpu = value; } }
        public bool GpuAuto { get { return Cur.GpuAuto; } set { Cur.GpuAuto = value; } }
        public KeyAction Key = KeyAction.Show;      // plain OMEN key; Shift+key always cycles modes, Ctrl+key toggles max fan
        public uint KeyId = 0, KeyData = 0;         // hpqBEvnt EventID / EventData of the OMEN key; 0 = use the platform profile's values
        public bool SuppressOgh = true;
        public bool Hotkeys = true;
        public bool EcoOnBattery = false;
        public bool SyncWinPower = true;            // mirror mode into the Windows power-mode overlay
        public bool EcoCool = false;                // true: Eco uses the BIOS "cool" fan policy (0x50). Off = exactly what OGH sends for Eco (0x30).
        public int HeartbeatSec = 45;               // firmware forgets fan settings after 120 s without a call
        public int WinX = -1, WinY = -1;
        public bool StartHidden = false;
        public string Name = "";                    // display name shown in the window/tray (empty = "Ohman")
        public bool NoPersist;                      // set when --set overrides are in effect: never write them back to the file
        public int SavedModeOverride = -1;          // while battery forces Eco, the file keeps the user's own mode

        static readonly string File_ = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.FileStem + ".state");

        public static Settings Load() {
            var s = new Settings();
            try {
                if (!File.Exists(File_)) return s;
                foreach (string raw in File.ReadAllLines(File_)) {
                    string line = raw.Trim(); int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq < 1) continue;
                    s.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            } catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            return s;
        }

        /// <summary>Apply one key=value pair (used by Load and by the --set command-line override).</summary>
        public void Apply(string k, string v) {
            var s = this;
            try {
                // per-mode keys: M0.Fan=… M1.TdpOffset=… (M0 Eco, M1 Balanced, M2 Performance)
                if (k.Length > 3 && k[0] == 'M' && char.IsDigit(k[1]) && k[2] == '.') { int mi = k[1] - '0'; if (mi >= 0 && mi < 3) Modes[mi].Apply(k.Substring(3), v); return; }
                // un-prefixed keys apply to every mode (handy for hand edits and --set)
                if (k == "Fan" || k == "Fan1" || k == "Fan2" || k == "TdpOffset" || k == "Gpu" || k == "GpuAuto") { foreach (var m in Modes) m.Apply(k, v); return; }
                {
                    int n; bool b;
                    switch (k) {
                        case "ModeIndex": if (TryInt(v, out n)) s.ModeIndex = Math.Max(0, Math.Min(2, n)); break;
                        case "EcoCool": if (bool.TryParse(v, out b)) s.EcoCool = b; break;
                        case "Key": if (TryInt(v, out n)) s.Key = (KeyAction)Math.Max(0, Math.Min(3, n)); break;
                        case "KeyId": if (TryInt(v, out n)) s.KeyId = (uint)n; break;
                        case "KeyData": if (TryInt(v, out n)) s.KeyData = (uint)n; break;
                        case "SuppressOgh": if (bool.TryParse(v, out b)) s.SuppressOgh = b; break;
                        case "Hotkeys": if (bool.TryParse(v, out b)) s.Hotkeys = b; break;
                        case "EcoOnBattery": if (bool.TryParse(v, out b)) s.EcoOnBattery = b; break;
                        case "SyncWinPower": if (bool.TryParse(v, out b)) s.SyncWinPower = b; break;
                        case "HeartbeatSec": if (TryInt(v, out n)) s.HeartbeatSec = Math.Max(10, Math.Min(110, n)); break;
                        case "WinX": if (TryInt(v, out n)) s.WinX = n; break;
                        case "WinY": if (TryInt(v, out n)) s.WinY = n; break;
                        case "StartHidden": if (bool.TryParse(v, out b)) s.StartHidden = b; break;
                        case "Name": s.Name = v.Length > 24 ? v.Substring(0, 24) : v; break;
                    }
                }
            } catch (Exception ex) { Log.Write("settings apply " + k + ": " + ex.Message); }
        }

        internal static bool TryInt(string v, out int n) {
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n);
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        public void Save() {
            if (NoPersist) return;
            try {
                var sb = new StringBuilder();
                sb.AppendLine("# " + Program.AppName + " settings (edited by the app; safe to hand-edit while it is closed)");
                sb.AppendLine("ModeIndex=" + (SavedModeOverride >= 0 ? SavedModeOverride : ModeIndex)); sb.AppendLine("EcoCool=" + EcoCool);
                sb.AppendLine("# per-mode profiles: M0 = Eco, M1 = Balanced, M2 = Performance");
                for (int i = 0; i < 3; i++) Modes[i].Write(sb, "M" + i + ".");
                sb.AppendLine("Key=" + (int)Key); sb.AppendLine("KeyId=" + KeyId); sb.AppendLine("KeyData=" + KeyData);
                sb.AppendLine("SuppressOgh=" + SuppressOgh); sb.AppendLine("Hotkeys=" + Hotkeys);
                sb.AppendLine("EcoOnBattery=" + EcoOnBattery); sb.AppendLine("SyncWinPower=" + SyncWinPower);
                sb.AppendLine("HeartbeatSec=" + HeartbeatSec);
                sb.AppendLine("WinX=" + WinX); sb.AppendLine("WinY=" + WinY); sb.AppendLine("StartHidden=" + StartHidden);
                sb.AppendLine("# Name=   (optional: a different display name for the window and tray; no rebuild needed)");
                if (!string.IsNullOrEmpty(Name)) sb.AppendLine("Name=" + Name);
                File.WriteAllText(File_, sb.ToString());
            } catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }
    }

    public sealed class Engine : IDisposable {
        public readonly IHardware Hw;
        public readonly Settings S;
        public SystemInfo Info = new SystemInfo();
        public int FanCount = -1;
        public bool BiosOk;
        public string LastError = "";
        public DateTime LastHeartbeat = DateTime.MinValue;
        public volatile bool Learning;
        public uint LastEventId, LastEventData; public DateTime LastEventTime = DateTime.MinValue;

        public event Action StateChanged;                 // any setting applied (may fire on a worker thread)
        public event Action<string, bool> Toast;          // (message, isError)
        public event Action<KeyAction> KeyPressed;        // OMEN key matched
        public event Action<uint, uint> AnyKeyEvent;      // every hpqBEvnt

        public static readonly string[] ModeNames = { "Eco", "Balanced", "Performance" };
        public byte[] ModeBytes { get { return new byte[] { S.EcoCool ? P.ModeCool : P.ModeEco, P.ModeBalanced, P.ModePerformance }; } }
        public bool OnBattery;                              // mirrored into the SetMode payload like OGH does (BiosAutoFanControlInDc)

        // ---------- platform ----------
        public PlatformProfile P = Platforms.Known[0];     // the profile in use (defaults to the Transcend 14 values until detection runs)
        public string Board = "", Model = "";
        public bool Supported;                              // false = unknown board: read-only, no BIOS writes
        public bool ReadOnly { get { return !Supported && !Hw.IsDemo; } }

        ManagementEventWatcher watcher;
        System.Threading.Timer heartbeat;
        DateTime lastKey = DateTime.MinValue;
        readonly object applySync = new object();
        bool ecoForcedByBattery;
        int modeBeforeBattery = 1;

        public Engine(IHardware hw, Settings s) { Hw = hw; S = s; }

        public int ModeIndex { get { return Math.Max(0, Math.Min(2, S.ModeIndex)); } }
        public byte ModeByte { get { return ModeBytes[ModeIndex]; } }
        public string ModeName { get { return ModeNames[ModeIndex]; } }
        public int BaseTdp { get { return Info.Valid && Info.DefaultConcurrentTdp > 0 ? Info.DefaultConcurrentTdp : P.TdpBase; } }
        public int MaxOffset { get { return P.TdpGainMax; } }
        public int CurrentTdp { get { return BaseTdp + Math.Max(0, Math.Min(MaxOffset, S.TdpOffset)); } }   // clamped here too: the file is hand-editable
        public uint KeyId { get { return S.KeyId != 0 ? S.KeyId : P.KeyEventId; } }       // learned value wins, else the profile's
        public uint KeyData { get { return S.KeyId != 0 ? S.KeyData : P.KeyEventData; } }
        public GpuLevel EffectiveGpu { get { return S.GpuAuto ? GpuForMode(ModeIndex) : S.Gpu; } }
        public static GpuLevel GpuForMode(int modeIndex) { return modeIndex == 0 ? GpuLevel.Base : modeIndex == 1 ? GpuLevel.Boost : GpuLevel.Max; }

        // ---------- lifecycle ----------
        public void Init() {
            try { OnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
            Board = Platforms.ReadBoard(); Model = Platforms.ReadModel();
            var prof = Platforms.Find(Board);
            Supported = prof != null || Hw.IsDemo;
            if (prof != null) P = prof;
            Log.Write("platform: model='" + Model + "' board='" + Board + "' -> " + (prof != null ? prof.Name : "UNKNOWN (read-only, no BIOS writes)"));
            try {
                FanCount = Hw.GetFanCountPassive();     // never the 0x10 query here: it is the keep-alive trigger
                Info = Hw.GetSystemInfo();
                BiosOk = true;
                if (Supported && !Hw.IsDemo && Info.Valid && Info.ThermalPolicy != P.ThermalPolicy) {
                    Supported = false; Log.Write("thermal policy v" + Info.ThermalPolicy + " does not match the profile (v" + P.ThermalPolicy + "); switching to read-only");
                }
                Log.Write("BIOS ok: fans=" + FanCount + " policy=v" + Info.ThermalPolicy + " swFan=" + Info.SwFanControl + " defPL4=" + Info.DefaultPl4 + "W baseTdp=" + Info.DefaultConcurrentTdp + "W raw=" + Info.Hex + (Hw.IsDemo ? " (DEMO)" : ""));
            } catch (Exception ex) { BiosOk = false; LastError = ex.Message; Log.Write("BIOS self-test FAILED: " + ex.Message); }
            // take the key over only where we can also take over the fans; on an unknown board OGH stays in charge
            if (S.SuppressOgh && !Hw.IsDemo && Supported) { KillOgh(); new Thread(delegate() { SetOghTasks(true); }) { IsBackground = true }.Start(); }
            if (S.EcoOnBattery && OnBattery && ModeIndex != 0) { ecoForcedByBattery = true; modeBeforeBattery = ModeIndex; S.SavedModeOverride = modeBeforeBattery; S.ModeIndex = 0; Log.Write("on battery at start: Eco (user mode " + ModeNames[modeBeforeBattery] + " kept)"); }
            ApplyAll(false);
            StartKeyWatcher();
            heartbeat = new System.Threading.Timer(delegate { Heartbeat(); }, null, S.HeartbeatSec * 1000, S.HeartbeatSec * 1000);
            guard = new System.Threading.Timer(delegate { GuardTick(); }, null, 10000, 10000);
            fanTimer = new System.Threading.Timer(delegate { FanTick(); }, null, 5000, 5000);
        }

        System.Threading.Timer fanTimer;
        void FanTick() {
            if (!BiosOk && !Hw.IsDemo) return;
            try {
                lock (applySync) {
                    if (GuardActive) return;
                    switch (S.Fan) {
                        case FanMode.Auto: AutoTick(false); break;
                        case FanMode.Manual: if ((DateTime.Now - lastFanWrite).TotalSeconds >= 30) { WriteLevels(S.Fan1, S.Fan2, "Fan level"); lastFanWrite = DateTime.Now; } break;
                        case FanMode.Max: if ((DateTime.Now - lastFanWrite).TotalSeconds >= 30) { Try(delegate { Hw.GetFanCount(); Hw.SetMaxFan(true); }, "Max fan"); lastFanWrite = DateTime.Now; } break;
                    }
                }
            } catch (Exception ex) { Log.Write("fan tick: " + ex.Message); }
        }

        public void Dispose() {
            try { if (fanTimer != null) fanTimer.Dispose(); } catch { }
            try { if (heartbeat != null) heartbeat.Dispose(); } catch { }
            try { if (guard != null) guard.Dispose(); } catch { }
            try { if (watcher != null) { watcher.Stop(); watcher.Dispose(); } } catch { }
        }

        // ---------- apply ----------
        bool Try(Action a, string what) {
            if (ReadOnly) { Log.Write("read-only (unsupported board '" + Board + "'): skipped " + what); return false; }
            try { a(); LastError = ""; return true; }
            catch (Exception ex) { LastError = ex.Message; Log.Write("FAIL " + what + ": " + ex.Message); Fire(Toast, what + " failed: " + ex.Message, true); return false; }
        }
        void Fire(Action<string, bool> h, string m, bool err) { if (h != null) { try { h(m, err); } catch { } } }
        void Changed() { var h = StateChanged; if (h != null) { try { h(); } catch { } } }
        void Say(string m) { Log.Write(m); Fire(Toast, m, false); }

        public void ApplyAll(bool announce) {
            lock (applySync) {
                if (!BiosOk && !Hw.IsDemo) return;
                Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");
                ApplyFanCore();
                Try(delegate { Hw.SetConcurrentTdp(CurrentTdp); }, "Set power");
                ApplyGpuCore();
                if (S.SyncWinPower) SetWinPowerOverlay(ModeIndex);
                LastHeartbeat = DateTime.Now;
            }
            if (announce) Say("Applied " + ModeName + " · +" + S.TdpOffset + " W");
            Changed();
        }

        // Fan semantics, measured on this machine on 2026-09-08 (see docs/research.md):
        //  * The 0x10 fan-count query is the keep-alive trigger: it holds the firmware in user-defined thermal/fan state.
        //  * Fan level 0 switches a fan off. Written with the trigger repeated, the fans read 0 rpm indefinitely (the incident).
        //  * When user-defined state expires, the firmware first re-applies the last written level (it went back to 0 for
        //    minutes after max fan expired) before its own curve resumes. So "hand back to the firmware" is not safe either.
        //  * Therefore every mode drives the fans explicitly and never below the curve floor. Auto = the vendor app's own
        //    curve (P.Curve) stepped every 5 s; Manual = the slider levels; Max = the max-fan flag. All with the trigger.
        int curLevel1 = -1, curLevel2 = -1;                // last levels written (what the firmware currently holds)
        public int AutoLevel1 { get { return curLevel1; } }
        public int AutoLevel2 { get { return curLevel2; } }
        public double GpuTemp = double.NaN, IrTemp = double.NaN;   // GpuTemp fed by the UI sensor loop; IrTemp read here
        int fanWriteFailures;

        bool WriteLevels(int l1, int l2, string what) {
            l1 = P.Curve.Clamp(l1); l2 = P.Curve.Clamp(l2);
            if (Try(delegate { Hw.GetFanCount(); Hw.SetFanLevels(l1, l2); }, what)) { curLevel1 = l1; curLevel2 = l2; fanWriteFailures = 0; fanFailureShown = false; return true; }
            fanWriteFailures++;
            if (fanWriteFailures >= 3 && !fanFailureShown) { fanFailureShown = true; Fire(Toast, "Fan writes failing; firmware curve will take over", true); }
            return false;
        }
        bool fanFailureShown;

        /// <summary>Max-fan flag with the keep-alive trigger in front of it, the pair every fan path uses.</summary>
        void MaxFan(bool on, string what) { Try(delegate { Hw.GetFanCount(); Hw.SetMaxFan(on); }, what); }

        void ApplyFanCore() {
            if (GuardActive) { MaxFan(true, "Guard max fan"); return; }      // the guard owns the fans until it releases
            switch (S.Fan) {
                case FanMode.Max: MaxFan(true, "Max fan"); break;
                case FanMode.Manual: MaxFan(false, "Max fan off"); WriteLevels(S.Fan1, S.Fan2, "Fan level"); break;
                default: MaxFan(false, "Max fan off"); AutoTick(true); break;
            }
        }

        /// <summary>Auto mode: one step of the software curve. Called every 5 s and on every mode change.</summary>
        void AutoTick(bool immediate) {
            if (S.Fan != FanMode.Auto || GuardActive || ReadOnly) return;
            if (fanWriteFailures >= 3 && (DateTime.Now - lastFanWrite).TotalSeconds < 60) return;   // back off; retry once a minute
            try { IrTemp = Hw.GetTemperature(); } catch { IrTemp = double.NaN; }
            int[] target = P.Curve.Target(CpuTemp, GpuTemp, IrTemp);
            int n1 = immediate ? target[0] : P.Curve.Step(curLevel1, target[0]);
            int n2 = immediate ? target[1] : P.Curve.Step(curLevel2, target[1]);
            bool changed = n1 != curLevel1 || n2 != curLevel2;
            bool refresh = (DateTime.Now - lastFanWrite).TotalSeconds >= 30;     // keep-alive even when steady
            if (!changed && !refresh) return;
            bool ok = WriteLevels(n1, n2, "Fan curve");
            lastFanWrite = DateTime.Now;
            if (changed && ok) Log.Write("curve " + n1 + "/" + n2 + " (target " + target[0] + ", cpu " + Fmt(CpuTemp) + " gpu " + Fmt(GpuTemp) + " ir " + Fmt(IrTemp) + ")");
        }
        DateTime lastFanWrite = DateTime.MinValue;
        static string Fmt(double v) { return double.IsNaN(v) ? "?" : v.ToString("0"); }
        void ApplyGpuCore() {
            // payloads come from the platform profile (Transcend 14: Base {0,0,1,75}, Boost {0,1,1,87}, Max {1,1,1,87} as OGH sends them)
            GpuLevel g = EffectiveGpu;
            byte[] p = g == GpuLevel.Max ? P.GpuMax : g == GpuLevel.Boost ? P.GpuBoost : P.GpuBase;
            Try(delegate { Hw.SetGpuPower(p[0] != 0, p[1] != 0, p[3]); }, "GPU power");
        }

        public void SetMode(int index, bool announce) {
            if (ecoForcedByBattery) { ecoForcedByBattery = false; S.SavedModeOverride = -1; }   // an explicit choice ends the battery override
            SetModeCore(index, announce);
        }
        void SetModeCore(int index, bool announce) {
            index = Math.Max(0, Math.Min(2, index));
            S.ModeIndex = index; S.Save();
            lock (applySync) {
                // the mode's own profile comes with it: fans, power gain and GPU power are remembered per mode
                if (Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode")) { if (announce) Say(ModeName + " mode"); }
                if (!GuardActive) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
                Try(delegate { Hw.SetConcurrentTdp(CurrentTdp); }, "Set power");
                ApplyGpuCore();
                if (S.SyncWinPower) SetWinPowerOverlay(index);
            }
            Changed();
        }

        public void SetEcoCool(bool on) {
            S.EcoCool = on; S.Save();
            if (ModeIndex == 0) lock (applySync) Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");
            Changed();
        }
        public void SetFan(FanMode mode, int f1, int f2, bool announce) {
            S.Fan = mode; S.Fan1 = P.Curve.Clamp(f1); S.Fan2 = P.Curve.Clamp(f2); S.Save();
            if (GuardActive && mode != FanMode.Max) { Say("Thermal guard is holding max fan; " + mode + " resumes when cool"); Changed(); return; }
            lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
            if (announce) Say(mode == FanMode.Max ? "Max fan" : mode == FanMode.Manual ? "Fans " + (S.Fan1 * 100) + " / " + (S.Fan2 * 100) + " RPM" : "Fans auto");
            Changed();
        }
        public void ToggleMaxFan() { SetFan(S.Fan == FanMode.Max ? FanMode.Auto : FanMode.Max, S.Fan1, S.Fan2, true); }

        public void SetTdpOffset(int off, bool announce) {
            S.TdpOffset = Math.Max(0, Math.Min(MaxOffset, off)); S.Save();
            lock (applySync) Try(delegate { Hw.SetConcurrentTdp(CurrentTdp); }, "Set power");
            if (announce) Say("Power gain +" + S.TdpOffset + " W · " + CurrentTdp + " W budget");
            Changed();
        }

        public void SetGpu(GpuLevel lvl, bool auto, bool announce) {
            S.Gpu = lvl; S.GpuAuto = auto; S.Save();
            lock (applySync) ApplyGpuCore();
            if (announce) Say("GPU " + (auto ? "auto" : lvl.ToString()));
            Changed();
        }

        public void SetKey(KeyAction a) { S.Key = a; S.Save(); Changed(); }

        // ---------- heartbeat ----------
        void Heartbeat() {
            try {
                if (!BiosOk && !Hw.IsDemo) return;
                lock (applySync) {
                    if (GuardActive) MaxFan(true, "Guard max fan");
                    Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");   // fans are handled by FanTick; this only pins mode and power
                    Try(delegate { Hw.SetConcurrentTdp(CurrentTdp); }, "Set power");
                    LastHeartbeat = DateTime.Now;
                }
                if (S.SuppressOgh && !Hw.IsDemo && Supported) KillOgh();
            } catch (Exception ex) { Log.Write("heartbeat: " + ex.Message); }
        }

        // ---------- thermal guard ----------
        // Independent of everything above: every 10 s read the fans and the chassis sensor; with the CPU temperature the UI
        // feeds in, force max fan when the CPU is >= 90 C, the chassis sensor >= 56 C, or the fans read stalled while warm.
        // Release only after 60 s of cool readings. Would have caught the 2026-09-08 incident within a minute.
        public volatile bool GuardActive;
        public double CpuTemp = double.NaN;               // set by the UI sensor loop
        public int GuardFan1 = -1, GuardFan2 = -1, GuardChassis = -1;
        DateTime guardSafeSince = DateTime.MinValue;
        System.Threading.Timer guard;

        void GuardTick() {
            if (!BiosOk || Hw.IsDemo || ReadOnly) return;      // read-only boards: nothing to force, the firmware's own limits apply
            try {
                int[] f; int c;
                lock (applySync) { f = Hw.GetFanLevels(); c = Hw.GetTemperature(); }
                GuardFan1 = f[0]; GuardFan2 = f[1]; GuardChassis = c;
                double t = CpuTemp;
                bool cpuKnown = !double.IsNaN(t);
                bool hot = (cpuKnown && t >= 90) || c >= 56;
                bool stalled = cpuKnown && t >= 70 && f[0] >= 0 && f[1] >= 0 && (f[0] + f[1]) < 10;   // both fans under 500 rpm while warm
                if ((hot || stalled) && !GuardActive) {
                    GuardActive = true; guardSafeSince = DateTime.MinValue;
                    Log.Write("THERMAL GUARD engaged: cpu=" + (cpuKnown ? t.ToString("0") : "?") + " chassis=" + c + " fans=" + f[0] + "/" + f[1] + (stalled ? " (stalled)" : ""));
                    Fire(Toast, "Thermal guard: fans to max (CPU " + (cpuKnown ? t.ToString("0") + "°" : "?") + ", chassis " + c + "°)", true);
                    lock (applySync) MaxFan(true, "Guard max fan");
                    Changed();
                } else if (GuardActive) {
                    bool safe = (!cpuKnown || t < 78) && c < 48;
                    if (!safe) { guardSafeSince = DateTime.MinValue; lock (applySync) MaxFan(true, "Guard max fan"); }
                    else if (guardSafeSince == DateTime.MinValue) guardSafeSince = DateTime.Now;
                    else if ((DateTime.Now - guardSafeSince).TotalSeconds >= 60) {
                        GuardActive = false;
                        Log.Write("thermal guard released; fan mode back to " + S.Fan);
                        Fire(Toast, "Thermal guard released", false);
                        curLevel1 = curLevel2 = -1;         // unknown after max fan; the curve starts again from its floor
                        lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; }   // the current mode's own fan setting, nothing saved
                        Changed();
                    }
                }
            } catch (Exception ex) { Log.Write("guard: " + ex.Message); }
        }

        public void OnResume() {
            // firmware state is lost across sleep; re-apply after the WMI provider is back
            new Thread(delegate() { Thread.Sleep(4000); Log.Write("resume: re-applying"); ApplyAll(false); }) { IsBackground = true }.Start();
        }

        public void OnPowerSource(bool onBattery) {
            bool changed = OnBattery != onBattery; OnBattery = onBattery;
            if (S.EcoOnBattery) {
                // forced Eco is temporary: the file keeps the user's own mode (SavedModeOverride) and it comes back when plugged in
                if (onBattery && !ecoForcedByBattery && ModeIndex != 0) { ecoForcedByBattery = true; modeBeforeBattery = ModeIndex; S.SavedModeOverride = modeBeforeBattery; Say("On battery → Eco"); SetModeCore(0, false); return; }
                if (!onBattery && ecoForcedByBattery) { ecoForcedByBattery = false; S.SavedModeOverride = -1; SetModeCore(modeBeforeBattery, false); Say("Plugged in → " + ModeName); return; }
            }
            if (changed) lock (applySync) Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");   // re-send with the DC flag like OGH
        }

        // ---------- OGH suppression ----------
        // OGH's key handler (OmenCommandCenterBackground) is launched at logon by HP's "OmenInstallMonitor" scheduled
        // tasks (its own startup task is disabled). Taking over the key = stop both processes now and disable those
        // tasks so they do not come back at the next logon. Turning the switch off re-enables the tasks.
        public void KillOgh() {
            foreach (string name in new string[] { "OmenCommandCenterBackground", "OmenInstallMonitor" }) {
                try {
                    foreach (var p in Process.GetProcessesByName(name)) {
                        using (p) { try { p.Kill(); Log.Write("stopped " + name + " (pid " + p.Id + ")"); } catch (Exception ex) { Log.Write("kill " + name + ": " + ex.Message); } }
                    }
                } catch { }
            }
        }

        public void SetOghTasks(bool disable) {
            if (Hw.IsDemo) return;
            try {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                string csv;
                using (var q = Process.Start(psi)) { csv = q.StandardOutput.ReadToEnd(); q.WaitForExit(5000); }
                int n = 0;
                foreach (string line in csv.Split('\n')) {
                    if (!line.StartsWith("\"\\OmenInstallMonitor", StringComparison.OrdinalIgnoreCase)) continue;
                    string task = line.Split('"')[1];
                    using (var c = Process.Start(new ProcessStartInfo("schtasks.exe", "/Change /TN \"" + task + "\" " + (disable ? "/DISABLE" : "/ENABLE")) { CreateNoWindow = true, UseShellExecute = false })) { c.WaitForExit(5000); if (c.ExitCode == 0) n++; else Log.Write("schtasks " + task + " rc=" + c.ExitCode); }
                }
                Log.Write((disable ? "disabled " : "re-enabled ") + n + " OmenInstallMonitor task(s)");
            } catch (Exception ex) { Log.Write("OGH tasks: " + ex.Message); }
        }

        public void SetOghSuppression(bool on) {
            S.SuppressOgh = on; S.Save();
            if (Hw.IsDemo) { Say("Simulated hardware: OMEN Gaming Hub is left alone"); Changed(); return; }
            if (ReadOnly) { Say("Unsupported board: OMEN Gaming Hub keeps the key"); Changed(); return; }
            if (on) { KillOgh(); SetOghTasks(true); Say("Fn+F12 now belongs to " + Program.DisplayName); }
            else { SetOghTasks(false); Say("OMEN Gaming Hub's launcher restored at next logon"); }
        }

        // ---------- OMEN key ----------
        void StartKeyWatcher() {
            try {
                var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"), new WqlEventQuery("SELECT * FROM hpqBEvnt"));
                w.EventArrived += OnBiosEvent; w.Start(); watcher = w;
                Log.Write("hpqBEvnt watcher started");
            } catch (Exception ex) { Log.Write("hpqBEvnt watcher failed: " + ex.Message); }
        }

        void OnBiosEvent(object s, EventArrivedEventArgs e) {
            uint id = 0, data = 0;
            try { id = Convert.ToUInt32(e.NewEvent["EventID"]); data = Convert.ToUInt32(e.NewEvent["EventData"]); } catch { return; }
            LastEventId = id; LastEventData = data; LastEventTime = DateTime.Now;
            // the OMEN key is a firmware event, not a keystroke, so chords are read from the modifier state at arrival
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0, ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            Log.Write("hpqBEvnt id=" + id + " data=" + data + (shift ? " +shift" : "") + (ctrl ? " +ctrl" : ""));
            var any = AnyKeyEvent; if (any != null) { try { any(id, data); } catch { } }
            if (Learning) {
                if (id == 131073) return;               // power/AC notification, not a key
                Learning = false; S.KeyId = id; S.KeyData = data; S.Save();
                Say("OMEN key bound to event " + id + "/" + data); Changed(); return;
            }
            if (id != KeyId || data != KeyData) return;
            if ((DateTime.Now - lastKey).TotalMilliseconds < 400) return;   // the key fires twice per press
            lastKey = DateTime.Now;
            if (S.SuppressOgh && !Hw.IsDemo && Supported) KillOgh();
            KeyAction act = shift ? KeyAction.Cycle : ctrl ? KeyAction.MaxFan : S.Key;
            if (act == KeyAction.Off) return;
            var h = KeyPressed; if (h != null) { try { h(act); } catch { } }
        }
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

        // ---------- Windows power-mode overlay ----------
        static readonly Guid OverlayEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid OverlayBalanced = Guid.Empty;
        static readonly Guid OverlayPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(ref Guid overlay);
        public void SetWinPowerOverlay(int modeIndex) {
            try {
                Guid g = modeIndex == 0 ? OverlayEfficiency : modeIndex == 2 ? OverlayPerformance : OverlayBalanced;
                uint rc = PowerSetActiveOverlayScheme(ref g);
                if (rc != 0) Log.Write("PowerSetActiveOverlayScheme rc=" + rc);
            } catch (Exception ex) { Log.Write("power overlay: " + ex.Message); }
        }

        // ---------- diagnostics ----------
        public string Diagnostics() {
            var sb = new StringBuilder();
            sb.AppendLine(Program.AppName + " diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("hardware: " + (Hw.IsDemo ? "DEMO (simulated)" : "BIOS via root\\wmi hpqBIntM"));
            sb.AppendLine("platform: " + Model + " board " + Board + " -> " + (Supported ? P.Name : "UNSUPPORTED (read-only)"));
            sb.AppendLine("bios ok: " + BiosOk + (LastError.Length > 0 ? "  last error: " + LastError : ""));
            sb.AppendLine("fans: " + FanCount + "   system data: " + Info.Hex + "   policy v" + Info.ThermalPolicy + "  swFan=" + Info.SwFanControl + "  PL4=" + Info.DefaultPl4 + "W  baseTdp=" + Info.DefaultConcurrentTdp + "W");
            try { var f = Hw.GetFanLevels(); sb.AppendLine("fan levels: " + f[0] + " / " + f[1] + "  (x100 RPM)"); } catch (Exception ex) { sb.AppendLine("fan levels: " + ex.Message); }
            try { sb.AppendLine("bios temp sensor: " + Hw.GetTemperature() + " C"); } catch (Exception ex) { sb.AppendLine("temp: " + ex.Message); }
            try { sb.AppendLine("max fan: " + Hw.GetMaxFan()); } catch (Exception ex) { sb.AppendLine("max fan: " + ex.Message); }
            try { sb.AppendLine("gpu power: " + Hw.GetGpuPower()); } catch (Exception ex) { sb.AppendLine("gpu power: " + ex.Message); }
            sb.AppendLine("settings: mode=" + ModeName + " (BIOS 0x" + ModeByte.ToString("X2") + (OnBattery ? ", DC" : ", AC") + ") fan=" + S.Fan + " " + S.Fan1 + "/" + S.Fan2 + " tdp=" + CurrentTdp + "W gpu=" + EffectiveGpu + (S.GpuAuto ? "(auto)" : "") + " key=" + KeyId + "/" + KeyData + "→" + S.Key + " ecoCool=" + S.EcoCool);
            sb.AppendLine("fan drive: written " + curLevel1 + "/" + curLevel2 + "  cpu " + Fmt(CpuTemp) + "  gpu " + Fmt(GpuTemp) + "  ir " + Fmt(IrTemp) + "  guard=" + GuardActive + "  writeFailures=" + fanWriteFailures);
            sb.AppendLine("last heartbeat: " + (LastHeartbeat == DateTime.MinValue ? "never" : LastHeartbeat.ToString("HH:mm:ss")) + "   last key event: " + (LastEventTime == DateTime.MinValue ? "none" : LastEventId + "/" + LastEventData + " at " + LastEventTime.ToString("HH:mm:ss")));
            return sb.ToString();
        }
    }
}

