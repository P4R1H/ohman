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

    public enum FanMode { Auto = 0, Max = 1, Manual = 2, Custom = 3 }   // Custom = the user's own curve, per mode
    public enum KeyAction { Cycle = 0, Show = 1, MaxFan = 2, Off = 3, Run = 4 }
    public enum GpuLevel { Base = 0, Boost = 1, Max = 2 }   // {cTGP,PPAB} = {0,0} / {0,1} / {1,1} — the three payloads OGH sends

    /// <summary>Fan, power-gain and GPU choices are remembered per performance mode (like tabs): switching to a mode applies its own set.</summary>
    public sealed class ModeProfile {
        public FanMode Fan = FanMode.Auto;
        public int Fan1 = 30, Fan2 = 30;
        public int[] CurveLevels;                   // fan level at Engine.CurveTemps, the CPU fan (both when linked); null until seeded from the profile
        public int[] GpuCurveLevels;                // the GPU fan's own curve, used when CurveLinked is off
        public bool CurveLinked = true;             // one curve for both fans (the hotter chip wins)
        public int CurveFloor = 0;                  // lowest level the curve may drive (0 = the profile's own floor)
        public int CurveRamp = 5;                   // seconds per 300 rpm step when following the curve (1 = at once, 10 = very gentle)
        public int TdpOffset = 0;
        public GpuLevel Gpu = GpuLevel.Boost;
        public bool GpuAuto = true;                 // follow the mode: Eco->Base, Balanced->Boost, Performance->Max (what OGH does)
        public void Apply(string k, string v) {
            int n; bool b;
            switch (k) {
                case "Fan": if (Settings.TryInt(v, out n)) Fan = (FanMode)Math.Max(0, Math.Min(3, n)); break;
                case "Curve": { var lv = ParseCurve(v); if (lv != null) CurveLevels = lv; break; }
                case "GpuCurve": { var lv = ParseCurve(v); if (lv != null) GpuCurveLevels = lv; break; }
                case "CurveLink": if (bool.TryParse(v, out b)) CurveLinked = b; break;
                case "CurveFloor": if (Settings.TryInt(v, out n)) CurveFloor = Math.Max(0, Math.Min(99, n)); break;
                case "CurveRamp": if (Settings.TryInt(v, out n)) CurveRamp = Math.Max(1, Math.Min(10, n)); break;
                case "Fan1": if (Settings.TryInt(v, out n)) Fan1 = n; break;
                case "Fan2": if (Settings.TryInt(v, out n)) Fan2 = n; break;
                case "TdpOffset": if (Settings.TryInt(v, out n)) TdpOffset = Math.Max(0, Math.Min(30, n)); break;   // the engine clamps again to the profile's range
                case "Gpu": if (Settings.TryInt(v, out n)) Gpu = (GpuLevel)Math.Max(0, Math.Min(2, n)); break;
                case "GpuAuto": if (bool.TryParse(v, out b)) GpuAuto = b; break;
            }
        }
        static int[] ParseCurve(string v) {
            var parts = v.Split(','); if (parts.Length != Engine.CurveTemps.Length) return null;
            var lv = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (!Settings.TryInt(parts[i].Trim(), out lv[i])) return null;
            return lv;
        }
        static string JoinCurve(int[] lv) { return string.Join(",", Array.ConvertAll(lv, delegate(int x) { return x.ToString(); })); }
        public void Write(StringBuilder sb, string prefix) {
            sb.AppendLine(prefix + "Fan=" + (int)Fan); sb.AppendLine(prefix + "Fan1=" + Fan1); sb.AppendLine(prefix + "Fan2=" + Fan2);
            sb.AppendLine(prefix + "TdpOffset=" + TdpOffset); sb.AppendLine(prefix + "Gpu=" + (int)Gpu); sb.AppendLine(prefix + "GpuAuto=" + GpuAuto);
            if (CurveLevels != null) sb.AppendLine(prefix + "Curve=" + JoinCurve(CurveLevels));
            if (GpuCurveLevels != null) sb.AppendLine(prefix + "GpuCurve=" + JoinCurve(GpuCurveLevels));
            sb.AppendLine(prefix + "CurveLink=" + CurveLinked);
            sb.AppendLine(prefix + "CurveFloor=" + CurveFloor); sb.AppendLine(prefix + "CurveRamp=" + CurveRamp);
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
        public KeyAction Key = KeyAction.Show;      // what the OMEN key does; Shift+F11 (a normal hotkey) cycles modes
        public uint KeyId = 0, KeyData = 0;         // hpqBEvnt EventID / EventData of the OMEN key; 0 = use the platform profile's values
        public bool SuppressOgh = true;
        public bool Hotkeys = true;
        public bool EcoOnBattery = false;
        public bool SyncWinPower = true;            // mirror mode into the Windows power-mode overlay
        public bool EcoCool = false;                // true: Eco uses the BIOS "cool" fan policy (0x50). Off = exactly what OGH sends for Eco (0x30).
        public int HeartbeatSec = 45;               // firmware forgets fan settings after 120 s without a call
        public bool MaxBackWhenCool = true;         // leave max fan once the CPU has been below 60 C for two minutes
        public int MaxStopAfterMin = 30;            // leave max fan after this many minutes (0 = never)
        public bool ManualLinked = true;            // the two manual sliders move together
        public long UpdateChecked = 0;              // ticks of the last successful update check
        public string LatestVersion = "";           // newest release tag GitHub reported
        public int WinX = -1, WinY = -1;
        public bool StartHidden = false;
        public string Name = "";                    // display name shown in the window/tray (empty = "Ohman")
        public int Light = -1;                      // keyboard: 0 off, 1 Ohman's colours, 2 Windows Dynamic Lighting; -1 = not chosen yet
        public string LightColors = "";             // zone colours RRGGBB,RRGGBB,... (seeded from the firmware on first run)
        public int LightLevel = 100;                // brightness 0..100, applied by scaling the colours (firmware level byte is unverified)
        public int LightEffect = 0;                 // 0 static, 1 breathe, 2 cycle, 3 wave (software effects, ~8 frames/s)
        public int LightSpeed = 3;                  // 1..5
        public int RefreshHz = 0;                   // chosen panel refresh rate (0 = leave Windows alone)
        public bool LowHzOnBattery = false;         // lowest refresh rate on battery, back to RefreshHz (or the highest) on AC
        public bool TrayTemp = true;                // CPU temperature drawn on the tray icon
        public bool Guard = true;                   // thermal guard: force max fan when the machine runs away
        public bool UpdateOnLaunch = true;          // ask GitHub for the latest release when Ohman starts (once a day)
        public string KeyCommand = "";              // KeyAction.Run: command line the OMEN key starts
        public bool NoPersist;                      // set when --set overrides are in effect: never write them back to the file
        public int SavedModeOverride = -1;          // while battery forces Eco, the file keeps the user's own mode

        static readonly string File_ = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.FileStem + ".state");

        public bool FirstRun;                       // no state file yet: first launch on this machine
        public int Ver;                             // version of the file this was read from; 0 = written before versioning
        public const int FileVer = 2;
        public static Settings Load() {
            var s = new Settings();
            try {
                if (!File.Exists(File_)) { s.FirstRun = true; return s; }
                foreach (string raw in File.ReadAllLines(File_)) {
                    string line = raw.Trim(); int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq < 1) continue;
                    s.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
                s.Migrate();
            } catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            return s;
        }

        /// <summary>Carry an older file forward. A default that changes is invisible to anyone who already has a file:
        /// the old default is sitting in it as if they had chosen it. Each step here undoes exactly one such change.</summary>
        void Migrate() {
            // v1: the OMEN key defaulted to Cycle. It now opens the window (Shift+F11 cycles), so a file that never
            // saw the new default and still says Cycle is the old default, not a choice.
            if (Ver < 2 && Key == KeyAction.Cycle) { Key = KeyAction.Show; Log.Write("settings: OMEN key moved to the new default (open the window)"); }
            if (Ver != FileVer) { Ver = FileVer; Save(); }
        }

        /// <summary>Apply one key=value pair (used by Load and by the --set command-line override).</summary>
        public void Apply(string k, string v) {
            var s = this;
            try {
                // per-mode keys: M0.Fan=… M1.TdpOffset=… (M0 Eco, M1 Balanced, M2 Performance)
                if (k.Length > 3 && k[0] == 'M' && char.IsDigit(k[1]) && k[2] == '.') { int mi = k[1] - '0'; if (mi >= 0 && mi < 3) Modes[mi].Apply(k.Substring(3), v); return; }
                // un-prefixed keys apply to every mode (handy for hand edits and --set)
                if (k == "Fan" || k == "Fan1" || k == "Fan2" || k == "TdpOffset" || k == "Gpu" || k == "GpuAuto" || k == "Curve") { foreach (var m in Modes) m.Apply(k, v); return; }
                {
                    int n; bool b;
                    switch (k) {
                        case "Ver": if (TryInt(v, out n)) s.Ver = n; break;
                        case "ModeIndex": if (TryInt(v, out n)) s.ModeIndex = Math.Max(0, Math.Min(2, n)); break;
                        case "EcoCool": if (bool.TryParse(v, out b)) s.EcoCool = b; break;
                        case "Key": if (TryInt(v, out n)) s.Key = (KeyAction)Math.Max(0, Math.Min(4, n)); break;
                        case "KeyId": if (TryInt(v, out n)) s.KeyId = (uint)n; break;
                        case "KeyData": if (TryInt(v, out n)) s.KeyData = (uint)n; break;
                        case "SuppressOgh": if (bool.TryParse(v, out b)) s.SuppressOgh = b; break;
                        case "Hotkeys": if (bool.TryParse(v, out b)) s.Hotkeys = b; break;
                        case "EcoOnBattery": if (bool.TryParse(v, out b)) s.EcoOnBattery = b; break;
                        case "SyncWinPower": if (bool.TryParse(v, out b)) s.SyncWinPower = b; break;
                        case "HeartbeatSec": if (TryInt(v, out n)) s.HeartbeatSec = Math.Max(10, Math.Min(110, n)); break;
                        case "MaxBackWhenCool": if (bool.TryParse(v, out b)) s.MaxBackWhenCool = b; break;
                        case "MaxStopAfterMin": if (TryInt(v, out n)) s.MaxStopAfterMin = Math.Max(0, Math.Min(240, n)); break;
                        case "ManualLinked": if (bool.TryParse(v, out b)) s.ManualLinked = b; break;
                        case "UpdateChecked": { long l; if (long.TryParse(v, out l)) s.UpdateChecked = l; break; }
                        case "LatestVersion": s.LatestVersion = v.Length > 24 ? v.Substring(0, 24) : v; break;
                        case "WinX": if (TryInt(v, out n)) s.WinX = n; break;
                        case "WinY": if (TryInt(v, out n)) s.WinY = n; break;
                        case "StartHidden": if (bool.TryParse(v, out b)) s.StartHidden = b; break;
                        case "Name": s.Name = v.Length > 24 ? v.Substring(0, 24) : v; break;
                        case "Light": if (TryInt(v, out n)) s.Light = Math.Max(-1, Math.Min(2, n)); break;
                        case "LightColors": s.LightColors = v; break;
                        case "LightLevel": if (TryInt(v, out n)) s.LightLevel = Math.Max(0, Math.Min(100, n)); break;
                        case "LightEffect": if (TryInt(v, out n)) s.LightEffect = Math.Max(0, Math.Min(3, n)); break;
                        case "LightSpeed": if (TryInt(v, out n)) s.LightSpeed = Math.Max(1, Math.Min(5, n)); break;
                        case "RefreshHz": if (TryInt(v, out n)) s.RefreshHz = Math.Max(0, Math.Min(500, n)); break;
                        case "LowHzOnBattery": if (bool.TryParse(v, out b)) s.LowHzOnBattery = b; break;
                        case "TrayTemp": if (bool.TryParse(v, out b)) s.TrayTemp = b; break;
                        case "Guard": if (bool.TryParse(v, out b)) s.Guard = b; break;
                        case "UpdateOnLaunch": if (bool.TryParse(v, out b)) s.UpdateOnLaunch = b; break;
                        case "KeyCommand": s.KeyCommand = v; break;
                    }
                }
            } catch (Exception ex) { Log.Write("settings apply " + k + ": " + ex.Message); }
        }

        internal static bool TryInt(string v, out int n) {
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n);
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        static readonly object saveSync = new object();
        public void Save() {
            if (NoPersist) return;
            try {
                lock (saveSync) { WriteFile(); }
            } catch (Exception ex) { Log.Write("save settings: " + ex.Message); }
        }
        void WriteFile() {
            {
                var sb = new StringBuilder();
                sb.AppendLine("# " + Program.AppName + " settings (edited by the app; safe to hand-edit while it is closed)");
                sb.AppendLine("Ver=" + FileVer);
                sb.AppendLine("ModeIndex=" + (SavedModeOverride >= 0 ? SavedModeOverride : ModeIndex)); sb.AppendLine("EcoCool=" + EcoCool);
                sb.AppendLine("# per-mode profiles: M0 = Eco, M1 = Balanced, M2 = Performance");
                for (int i = 0; i < 3; i++) Modes[i].Write(sb, "M" + i + ".");
                sb.AppendLine("Key=" + (int)Key); sb.AppendLine("KeyId=" + KeyId); sb.AppendLine("KeyData=" + KeyData);
                sb.AppendLine("SuppressOgh=" + SuppressOgh); sb.AppendLine("Hotkeys=" + Hotkeys);
                sb.AppendLine("EcoOnBattery=" + EcoOnBattery); sb.AppendLine("SyncWinPower=" + SyncWinPower);
                sb.AppendLine("HeartbeatSec=" + HeartbeatSec);
                sb.AppendLine("Light=" + Light); sb.AppendLine("LightColors=" + LightColors); sb.AppendLine("LightLevel=" + LightLevel); sb.AppendLine("LightEffect=" + LightEffect); sb.AppendLine("LightSpeed=" + LightSpeed);
                sb.AppendLine("RefreshHz=" + RefreshHz); sb.AppendLine("LowHzOnBattery=" + LowHzOnBattery); sb.AppendLine("TrayTemp=" + TrayTemp); sb.AppendLine("KeyCommand=" + KeyCommand);
                sb.AppendLine("Guard=" + Guard); sb.AppendLine("UpdateOnLaunch=" + UpdateOnLaunch);
                sb.AppendLine("MaxBackWhenCool=" + MaxBackWhenCool); sb.AppendLine("MaxStopAfterMin=" + MaxStopAfterMin); sb.AppendLine("ManualLinked=" + ManualLinked);
                sb.AppendLine("UpdateChecked=" + UpdateChecked); sb.AppendLine("LatestVersion=" + LatestVersion);
                sb.AppendLine("WinX=" + WinX); sb.AppendLine("WinY=" + WinY); sb.AppendLine("StartHidden=" + StartHidden);
                sb.AppendLine("# Name=   (optional: a different display name for the window and tray; no rebuild needed)");
                if (!string.IsNullOrEmpty(Name)) sb.AppendLine("Name=" + Name);
                File.WriteAllText(File_, sb.ToString());
            }
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
        public event Action<Rgb[]> FrameChanged;           // an effect frame was written to the keyboard (worker thread)

        public static readonly string[] ModeNames = { "Eco", "Balanced", "Performance" };
        public byte[] ModeBytes { get { return new byte[] { S.EcoCool ? P.ModeCool : P.ModeEco, P.ModeBalanced, P.ModePerformance }; } }
        public bool OnBattery;                              // mirrored into the SetMode payload like OGH does (BiosAutoFanControlInDc)

        // ---------- platform ----------
        public PlatformProfile P = new PlatformProfile { Name = "(detecting)", Boards = new string[0] };   // replaced by Init
        public string Board = "", Model = "";
        public bool Supported;                              // false = unknown board: read-only, no BIOS writes
        public bool Generic;                                // true = profile built at run time from the firmware (unverified model)
        public int GpuMode = -1;                            // graphics mode the firmware reports (0 Hybrid, 1 Discrete, 2 Optimus, 3 iGPU only), -1 unknown
        public int GpuModePending = -1;                     // mode written this session, live after a restart
        public static readonly string[] GpuModeNames = { "Hybrid", "Discrete", "Optimus", "iGPU only" };
        /// <summary>Modes the firmware offers (system-design byte 7): 1 iGPU only, 2 Hybrid, 4 Discrete, 8 Advanced Optimus.</summary>
        public bool GpuModeOffered(int mode) { int bit = mode == 3 ? 1 : mode == 0 ? 2 : mode == 1 ? 4 : 8; return (Info.GpuModes & bit) != 0; }
        public ILighting Light;                             // null = this keyboard has no controllable lighting (or read-only board)
        public Rgb[] LightColors = new Rgb[0];              // what the app believes the zones show
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
        public DateTime LastUpdateCheck { get { return S.UpdateChecked == 0 ? DateTime.MinValue : new DateTime(S.UpdateChecked); } }
        public string LatestVersion { get { return S.LatestVersion; } }
        public bool UpdateAvailable { get { return Update.Newer(S.LatestVersion, Program.Version); } }
        /// <summary>Ask GitHub for the newest release tag. force = the user pressed the button; otherwise at most once a day.</summary>
        public void CheckForUpdate(bool force) {
            if (!force && S.UpdateChecked != 0 && (DateTime.Now - LastUpdateCheck).TotalHours < 24) return;
            string tag = Update.LatestTag();
            S.UpdateChecked = DateTime.Now.Ticks;
            if (tag != null) S.LatestVersion = tag;
            S.Save();
            if (force) Say(tag == null ? "Update check failed" : Update.Newer(tag, Program.Version) ? "Version " + tag + " is available" : "Ohman is up to date");
            Changed();
        }

        public void Init() {
            try { OnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
            Board = Platforms.ReadBoard(); Model = Platforms.ReadModel();
            var prof = Platforms.Find(Board);
            Supported = prof != null || Hw.IsDemo;
            if (prof != null) P = prof;
            Log.Write("platform: model='" + Model + "' board='" + Board + "' -> " + (prof != null ? prof.Name : "no verified profile"));
            try {
                FanCount = Hw.GetFanCountPassive();     // never the 0x10 query here: it is the keep-alive trigger
                Info = Hw.GetSystemInfo();
                BiosOk = true;
                if (Supported && !Hw.IsDemo && Info.Valid && Info.ThermalPolicy != P.ThermalPolicy) {
                    Supported = false; Log.Write("thermal policy v" + Info.ThermalPolicy + " does not match the profile (v" + P.ThermalPolicy + "); switching to read-only");
                }
                if (prof == null && (!Hw.IsDemo || Platforms.BoardOverride != null)) {
                    // no verified profile: build one from what the firmware says about itself, the way the Linux driver does
                    var g = Platforms.Generic(Board, Info);
                    if (g != null) {
                        try { Hw.GetGpuPower(); g.HasGpuPower = true; } catch (Exception ex) { Log.Write("generic: no GPU power control (" + ex.Message + ")"); }
                        try { int top = Hw.GetFanTableMax(); if (top > g.Curve.Ceiling) g.Curve.Rescale(top); } catch { }
                        P = g; Supported = true; Generic = true;
                        Log.Write("generic profile: " + g.Notes + " · modes " + g.ModeEco.ToString("X2") + "/" + g.ModeBalanced.ToString("X2") + "/" + g.ModePerformance.ToString("X2") + " · powerGain=" + g.HasPowerGain + " (base " + g.TdpBase + " W) · gpuPower=" + g.HasGpuPower + " · fan ceiling " + g.Curve.Ceiling);
                    } else Log.Write("generic profile not possible (thermal policy v" + Info.ThermalPolicy + "); read-only");
                }
                try { GpuMode = Hw.GetGpuMode(); } catch (Exception ex) { Log.Write("graphics mode read: " + ex.Message); }
                Log.Write("BIOS ok: fans=" + FanCount + " policy=v" + Info.ThermalPolicy + " swFan=" + Info.SwFanControl + " defPL4=" + Info.DefaultPl4 + "W baseTdp=" + Info.DefaultConcurrentTdp + "W raw=" + Info.Hex + (Hw.IsDemo ? " (DEMO)" : ""));
            } catch (Exception ex) { BiosOk = false; LastError = ex.Message; Log.Write("BIOS self-test FAILED: " + ex.Message); }
            // take the key over only where we can also take over the fans; on an unknown board OGH stays in charge
            if (S.SuppressOgh && !Hw.IsDemo && Supported) { KillOgh(); new Thread(delegate() { SetOghTasks(true); }) { IsBackground = true }.Start(); }
            if (S.EcoOnBattery && OnBattery && ModeIndex != 0) { ecoForcedByBattery = true; modeBeforeBattery = ModeIndex; S.SavedModeOverride = modeBeforeBattery; S.ModeIndex = 0; Log.Write("on battery at start: Eco (user mode " + ModeNames[modeBeforeBattery] + " kept)"); }
            // a mode's custom curve starts as this model's own curve; before the profile is known there is nothing to copy
            foreach (var m in S.Modes) {
                if (m.CurveLevels == null) m.CurveLevels = VendorCurveAt(false);
                if (m.GpuCurveLevels == null) m.GpuCurveLevels = VendorCurveAt(true);
            }
            InitLight();
            ApplyRefreshRate(OnBattery);
            NoteFanMode(S.Fan);
            ApplyAll(false);
            StartKeyWatcher();
            heartbeat = new System.Threading.Timer(delegate { Heartbeat(); }, null, S.HeartbeatSec * 1000, S.HeartbeatSec * 1000);
            guard = new System.Threading.Timer(delegate { GuardTick(); }, null, 10000, 10000);
            fanTimer = new System.Threading.Timer(delegate { FanTick(); }, null, 5000, 5000);
        }

        System.Threading.Timer fanTimer;
        // ---------- max fan: a session with a length, and two ways to end by itself ----------
        DateTime maxSince = DateTime.MinValue, maxCoolSince = DateTime.MinValue; string maxStopReason = "";
        public int MaxMinutes { get { return maxSince == DateTime.MinValue ? 0 : (int)(DateTime.Now - maxSince).TotalMinutes; } }
        public TimeSpan MaxLeft {
            get {
                if (S.MaxStopAfterMin <= 0 || maxSince == DateTime.MinValue) return TimeSpan.Zero;
                var left = TimeSpan.FromMinutes(S.MaxStopAfterMin) - (DateTime.Now - maxSince);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
        /// <summary>The max-fan session starts wherever the fans actually go to maximum: an explicit choice, a mode whose
        /// remembered setting is Max, or a restored setting at startup. Otherwise "stop after" would never fire for those.</summary>
        void NoteFanMode(FanMode mode) {
            if (mode == FanMode.Max) { if (maxSince == DateTime.MinValue) { maxSince = DateTime.Now; maxCoolSince = DateTime.MinValue; } }
            else { maxSince = DateTime.MinValue; maxCoolSince = DateTime.MinValue; }
        }
        /// <summary>True when the max-fan session has run its course: the chips are cool again, or the timer expired.</summary>
        bool MaxShouldStop() {
            if (maxSince == DateTime.MinValue) return false;
            if (S.MaxStopAfterMin > 0 && (DateTime.Now - maxSince).TotalMinutes >= S.MaxStopAfterMin) { maxStopReason = S.MaxStopAfterMin + " min elapsed"; return true; }
            if (!S.MaxBackWhenCool) { maxCoolSince = DateTime.MinValue; return false; }
            double t = double.IsNaN(CpuTemp) ? GpuTemp : double.IsNaN(GpuTemp) ? CpuTemp : Math.Max(CpuTemp, GpuTemp);
            if (double.IsNaN(t) || t >= P.Guard.MaxFanCoolBelow) { maxCoolSince = DateTime.MinValue; return false; }
            if (maxCoolSince == DateTime.MinValue) { maxCoolSince = DateTime.Now; return false; }
            if ((DateTime.Now - maxCoolSince).TotalSeconds < P.Guard.MaxFanCoolSeconds) return false;
            maxStopReason = "below " + P.Guard.MaxFanCoolBelow + "° for " + (P.Guard.MaxFanCoolSeconds / 60) + " minutes"; return true;
        }
        void FanTick() {
            if (!BiosOk && !Hw.IsDemo) return;
            bool leaveMax = false;
            try {
                lock (applySync) {
                    if (GuardActive) return;
                    switch (S.Fan) {
                        case FanMode.Auto: case FanMode.Custom: AutoTick(false); break;
                        case FanMode.Manual: if ((DateTime.Now - lastFanWrite).TotalSeconds >= 30) { WriteLevels(S.Fan1, S.Fan2, "Fan level"); lastFanWrite = DateTime.Now; } break;
                        case FanMode.Max:
                            if ((DateTime.Now - lastFanWrite).TotalSeconds >= 30) { Try(delegate { Hw.GetFanCount(); Hw.SetMaxFan(true); }, "Max fan"); lastFanWrite = DateTime.Now; }
                            if (MaxShouldStop()) { Log.Write("max fan: " + maxStopReason + ", back to auto"); leaveMax = true; }
                            break;
                    }
                }
            } catch (Exception ex) { Log.Write("fan tick: " + ex.Message); }
            if (leaveMax) { Say("Max fan off · " + maxStopReason); SetFan(FanMode.Auto, S.Fan1, S.Fan2, false); }
        }

        // Work the UI hands over runs on one thread in the order it was posted. The thread pool does not promise that,
        // and applySync only serialises: two quick clicks could otherwise leave the firmware holding the first one.
        readonly Queue<Action> work = new Queue<Action>();
        readonly AutoResetEvent workReady = new AutoResetEvent(false);
        Thread worker; volatile bool stopping;
        public void Post(Action a) {
            if (a == null) return;
            Thread start = null;
            lock (work) {
                work.Enqueue(a);
                if (worker == null) { worker = new Thread(WorkLoop) { IsBackground = true, Name = "ohman-work" }; start = worker; }
            }
            if (start != null) start.Start();
            workReady.Set();
        }
        void WorkLoop() {
            while (!stopping) {
                workReady.WaitOne(500);
                for (; ; ) {
                    Action a;
                    lock (work) { if (work.Count == 0) break; a = work.Dequeue(); }
                    try { a(); } catch (Exception ex) { Log.Write("work: " + ex); }
                }
            }
        }
        public void Dispose() {
            stopping = true; try { workReady.Set(); } catch { }
            try { if (fx != null) fx.Dispose(); } catch { }
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
        /// <summary>A line for the user. Only for things they did not just ask for: the guard, a battery switch, an
        /// update result. Echoing a change back at the person who made it is noise, so every "announce" from a
        /// button, menu item or hotkey is false — the control they used already shows the new state.</summary>
        void Say(string m) { Log.Write(m); Fire(Toast, m, false); }

        public void ApplyAll(bool announce) {
            lock (applySync) {
                if (!BiosOk && !Hw.IsDemo) return;
                Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");
                ApplyFanCore();
                ApplyPowerCore();
                ApplyGpuCore();
                if (S.SyncWinPower) SetWinPowerOverlay(ModeIndex);
                LastHeartbeat = DateTime.Now;
                ApplyLightCore();
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

        // A CPU temperature at idle swings several degrees every few seconds. Feeding that straight into the curve
        // makes the fans hunt audibly and writes the firmware every tick, so the curve follows a smoothed reading and
        // ignores a target that has not moved far enough to be worth hearing.
        double smoothCpu = double.NaN, smoothGpu = double.NaN; int cpuGone, gpuGone;
        const double Smoothing = 0.4;       // per 5 s tick: ~4 ticks to travel 85 % of a step
        const int Deadband = 2;             // 200 rpm
        /// <summary>gone counts consecutive ticks with no reading: a couple are a hiccup and the last value still stands,
        /// but a sensor that has really stopped must go back to NaN so Target falls back instead of trusting old numbers.</summary>
        static double Smooth(double now, double was, ref int gone) {
            if (double.IsNaN(now)) { gone++; return gone >= 3 ? double.NaN : was; }
            gone = 0;
            if (double.IsNaN(was)) return now;
            if (now > was) return now;                                  // rising: follow at once, cooling can wait
            return was + (now - was) * Smoothing;
        }
        /// <summary>Auto mode: one step of the software curve. Called every 5 s and on every mode change.</summary>
        void AutoTick(bool immediate) {
            if ((S.Fan != FanMode.Auto && S.Fan != FanMode.Custom) || GuardActive || ReadOnly) return;
            if (fanWriteFailures >= 3 && (DateTime.Now - lastFanWrite).TotalSeconds < 60) return;   // back off; retry once a minute
            try { IrTemp = Hw.GetTemperature(); } catch { IrTemp = double.NaN; }
            if (immediate) { smoothCpu = CpuTemp; smoothGpu = GpuTemp; cpuGone = gpuGone = 0; }
            else { smoothCpu = Smooth(CpuTemp, smoothCpu, ref cpuGone); smoothGpu = Smooth(GpuTemp, smoothGpu, ref gpuGone); }
            FanCurve curve = S.Fan == FanMode.Custom ? CustomCurve() : P.Curve;
            int[] target = curve.Target(smoothCpu, smoothGpu, IrTemp);
            int n1 = immediate ? target[0] : curve.Step(curLevel1, target[0]);
            int n2 = immediate ? target[1] : curve.Step(curLevel2, target[1]);
            bool changed = n1 != curLevel1 || n2 != curLevel2;
            bool refresh = (DateTime.Now - lastFanWrite).TotalSeconds >= 30;     // keep-alive even when steady
            // a change smaller than the deadband is not worth a write, unless it is on its way up or we are refreshing
            if (changed && !immediate && !refresh && curLevel1 > 0 && n1 <= curLevel1 && n2 <= curLevel2
                && Math.Abs(n1 - curLevel1) < Deadband && Math.Abs(n2 - curLevel2) < Deadband) changed = false;
            if (!changed && !refresh) return;
            bool ok = WriteLevels(n1, n2, "Fan curve");
            lastFanWrite = DateTime.Now;
            if (changed && ok) Log.Write("curve " + n1 + "/" + n2 + " (target " + target[0] + ", cpu " + Fmt(smoothCpu) + " gpu " + Fmt(smoothGpu) + " ir " + Fmt(IrTemp) + ")");
        }
        DateTime lastFanWrite = DateTime.MinValue;
        public static readonly int[] CurveTemps = { 30, 40, 50, 60, 70, 80, 90 };
        /// <summary>The mode's own curve as a FanCurve: the same six points for CPU and GPU, the vendor's chassis table and bounds.</summary>
        FanCurve CustomCurve() {
            var lv = S.Cur.CurveLevels; var gl = S.Cur.CurveLinked ? lv : S.Cur.GpuCurveLevels;
            // the floor slider raises the lowest level the curve may drive; the ramp is how many levels a 5 s tick may move (5 s per step = the vendor's 3)
            int floor = Math.Max(P.Curve.Floor, Math.Min(P.Curve.Ceiling, S.Cur.CurveFloor));
            int step = Math.Max(1, Math.Min(P.Curve.Ceiling, (int)Math.Round(P.Curve.StepPerTick * 5.0 / Math.Max(1, S.Cur.CurveRamp))));
            return new FanCurve { CpuTemps = CurveTemps, CpuLevels = lv, GpuTemps = CurveTemps, GpuLevels = gl, IrTemps = P.Curve.IrTemps, IrLevels = P.Curve.IrLevels,
                Floor = floor, Ceiling = P.Curve.Ceiling, StepPerTick = step, Fallback = Math.Max(floor, P.Curve.Fallback) };
        }
        /// <summary>The vendor curve sampled at the editor's temperatures, for the dashed reference line.</summary>
        public int[] VendorCurveAt(bool gpu) {
            var r = new int[CurveTemps.Length];
            for (int i = 0; i < r.Length; i++) r[i] = gpu ? P.Curve.Target(double.NaN, CurveTemps[i], double.NaN)[0] : P.Curve.Target(CurveTemps[i], double.NaN, double.NaN)[0];
            return r;
        }
        /// <summary>The mode's own six points for the CPU fan, or for the GPU fan when the curves are unlinked.</summary>
        public void SetCurve(int[] levels, bool gpu) {
            if (levels == null || levels.Length != CurveTemps.Length) return;
            var lv = new int[levels.Length]; for (int i = 0; i < lv.Length; i++) lv[i] = P.Curve.Clamp(levels[i]);
            if (gpu) S.Cur.GpuCurveLevels = lv; else S.Cur.CurveLevels = lv;
            S.Save();
            if (S.Fan == FanMode.Custom && !GuardActive) lock (applySync) { AutoTick(true); lastFanWrite = DateTime.Now; }
            Changed();
        }
        public void SetCurveFloor(int level) {
            S.Cur.CurveFloor = level <= P.Curve.Floor ? 0 : P.Curve.Clamp(level); S.Save();
            if (S.Fan == FanMode.Custom && !GuardActive) lock (applySync) { AutoTick(true); lastFanWrite = DateTime.Now; }
            Changed();
        }
        public void SetCurveRamp(int seconds) { S.Cur.CurveRamp = Math.Max(1, Math.Min(10, seconds)); S.Save(); Changed(); }
        public void SetMaxBackWhenCool(bool on) { S.MaxBackWhenCool = on; if (!on) maxCoolSince = DateTime.MinValue; S.Save(); Changed(); }
        public void SetMaxStopAfter(int minutes) { S.MaxStopAfterMin = Math.Max(0, minutes); S.Save(); Changed(); }
        public void SetManualLinked(bool on) { S.ManualLinked = on; S.Save(); Changed(); }
        /// <summary>Start a custom curve from this model's own points (the "Edit as curve" link on the Auto page).</summary>
        public void SeedCurveFromVendor() {
            S.Cur.CurveLevels = VendorCurveAt(false); S.Cur.GpuCurveLevels = VendorCurveAt(true); S.Save();
            SetFan(FanMode.Custom, S.Fan1, S.Fan2, false);
        }
        public void SetCurveLinked(bool linked) {
            if (S.Cur.CurveLinked == linked) return;
            if (!linked) S.Cur.GpuCurveLevels = (int[])S.Cur.CurveLevels.Clone();     // unlinking starts the GPU curve where the shared one is
            S.Cur.CurveLinked = linked; S.Save();
            if (S.Fan == FanMode.Custom && !GuardActive) lock (applySync) { AutoTick(true); lastFanWrite = DateTime.Now; }
            Changed();
        }
        static string Fmt(double v) { return double.IsNaN(v) ? "?" : v.ToString("0"); }
        /// <summary>A fan level as the user should read it: this model's rpm, or a percentage when levels are already one.</summary>
        public string Rpm(int level) {
            if (level < 0) return "--";
            return P.RpmPerLevel > 0 ? (level * P.RpmPerLevel).ToString(CultureInfo.InvariantCulture) + " rpm" : Percent(level) + " of top speed";
        }
        public string Percent(int level) { return (int)Math.Round(100.0 * level / Math.Max(1, P.Curve.Ceiling)) + "%"; }
        void ApplyPowerCore() { if (P.HasPowerGain) Try(delegate { Hw.SetConcurrentTdp(CurrentTdp); }, "Set power"); }
        void ApplyGpuCore() {
            if (!P.HasGpuPower) return;
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
            NoteFanMode(S.Fan);                                      // the new mode brings its own fan setting with it
            lock (applySync) {
                // the mode's own profile comes with it: fans, power gain and GPU power are remembered per mode
                if (Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode")) { if (announce) Say(ModeName + " mode"); }
                if (!GuardActive) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
                ApplyPowerCore();
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
            if (mode == FanMode.Max && S.Fan != FanMode.Max) maxSince = DateTime.MinValue;   // asking for Max again starts a fresh session
            NoteFanMode(mode);
            S.Fan = mode; S.Fan1 = P.Curve.Clamp(f1); S.Fan2 = P.Curve.Clamp(f2); S.Save();
            if (GuardActive && mode != FanMode.Max) { Say("Thermal guard is holding max fan; " + Choice.Fan[Choice.Of(mode)] + " resumes when cool"); Changed(); return; }
            lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
            if (announce) Say(mode == FanMode.Max ? "Max fan" : mode == FanMode.Manual ? "Fans " + Rpm(S.Fan1) + " / " + Rpm(S.Fan2) : mode == FanMode.Custom ? "Fans on your curve" : "Fans auto");
            Changed();
        }
        public void ToggleMaxFan() { SetFan(S.Fan == FanMode.Max ? FanMode.Auto : FanMode.Max, S.Fan1, S.Fan2, false); }

        public void SetTdpOffset(int off, bool announce) {
            S.TdpOffset = Math.Max(0, Math.Min(MaxOffset, off)); S.Save();
            lock (applySync) ApplyPowerCore();
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
        public void SetKeyCommand(string cmd) { S.KeyCommand = (cmd ?? "").Trim(); S.Save(); Changed(); }
        public void SetHotkeys(bool on) { S.Hotkeys = on; S.Save(); Changed(); }
        public void SetEcoOnBattery(bool on) { S.EcoOnBattery = on; S.Save(); Changed(); }
        public void SetSyncWinPower(bool on) { S.SyncWinPower = on; S.Save(); if (on) SetWinPowerOverlay(ModeIndex); Changed(); }
        public void SetLowHzOnBattery(bool on) { S.LowHzOnBattery = on; S.Save(); Changed(); }
        public void SetTrayTemp(bool on) { S.TrayTemp = on; S.Save(); Changed(); }
        public void SetUpdateOnLaunch(bool on) { S.UpdateOnLaunch = on; S.Save(); Changed(); }

        // ---------- heartbeat ----------
        void Heartbeat() {
            try {
                if (!BiosOk && !Hw.IsDemo) return;
                lock (applySync) {
                    if (GuardActive) MaxFan(true, "Guard max fan");
                    Try(delegate { Hw.SetMode(ModeByte, OnBattery); }, "Set mode");   // fans are handled by FanTick; this only pins mode and power
                    ApplyPowerCore();
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
        public int GuardChassis = -1;
        DateTime guardSafeSince = DateTime.MinValue;
        bool chassisScaleKnown;            // the 0x23 sensor has read below the release threshold at least once, so it is on the scale the profile assumes
        System.Threading.Timer guard;

        /// <summary>Turning the guard off releases it at once; turning it on lets the next tick judge the machine.</summary>
        public void SetGuard(bool on) {
            S.Guard = on; S.Save();
            if (!on && GuardActive) { GuardActive = false; guardSafeSince = DateTime.MinValue; curLevel1 = curLevel2 = -1; lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; } Log.Write("thermal guard switched off while engaged; fans back to " + S.Fan); }
            Changed();
        }
        void GuardTick() {
            if (!BiosOk || Hw.IsDemo || ReadOnly) return;      // read-only boards: nothing to force, the firmware's own limits apply
            if (!S.Guard) {
                if (GuardActive) { GuardActive = false; guardSafeSince = DateTime.MinValue; curLevel1 = curLevel2 = -1; lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; } Changed(); }
                return;
            }
            try {
                int[] f; int c;
                lock (applySync) { f = Hw.GetFanLevels(); c = Hw.GetTemperature(); }
                GuardChassis = c;
                double t = CpuTemp;
                bool cpuKnown = !double.IsNaN(t);
                // On a board nobody has verified, the chassis sensor may not mean what the profile's thresholds assume.
                // Wait until it has read cool once; until then the CPU and the stall test carry the guard on their own.
                if (c >= 0 && c < P.Guard.ChassisSafe) chassisScaleKnown = true;
                bool chassisUsable = P.Verified || chassisScaleKnown;
                bool hot = (cpuKnown && t >= P.Guard.CpuHot) || (chassisUsable && c >= P.Guard.ChassisHot);
                bool stalled = cpuKnown && t >= P.Guard.StallCpu && f[0] >= 0 && f[1] >= 0 && (f[0] + f[1]) < P.Guard.StallLevelSum;
                if ((hot || stalled) && !GuardActive) {
                    GuardActive = true; guardSafeSince = DateTime.MinValue;
                    Log.Write("THERMAL GUARD engaged: cpu=" + (cpuKnown ? t.ToString("0") : "?") + " chassis=" + c + " fans=" + f[0] + "/" + f[1] + (stalled ? " (stalled)" : ""));
                    Fire(Toast, "Thermal guard: fans to max (CPU " + (cpuKnown ? t.ToString("0") + "°" : "?") + ", chassis " + c + "°)", true);
                    lock (applySync) MaxFan(true, "Guard max fan");
                    Changed();
                } else if (GuardActive) {
                    bool safe = (!cpuKnown || t < P.Guard.CpuSafe) && (!chassisUsable || c < P.Guard.ChassisSafe);
                    if (!safe) { guardSafeSince = DateTime.MinValue; lock (applySync) MaxFan(true, "Guard max fan"); }
                    else if (guardSafeSince == DateTime.MinValue) guardSafeSince = DateTime.Now;
                    else if ((DateTime.Now - guardSafeSince).TotalSeconds >= P.Guard.SafeSeconds) {
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
            ApplyRefreshRate(onBattery);
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
            if (on) { KillOgh(); SetOghTasks(true); Say("The OMEN key now belongs to " + Program.DisplayName); }
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
            Log.Write("hpqBEvnt id=" + id + " data=" + data);
            var any = AnyKeyEvent; if (any != null) { try { any(id, data); } catch { } }
            if (Learning) {
                if (id == 131073) return;               // power/AC notification, not a key
                Learning = false; S.KeyId = id; S.KeyData = data; S.Save();
                Say("OMEN key bound to event " + id + "/" + data); Changed(); return;
            }
            if (id == 13 && Light != null && S.Light != 2) {      // Fn backlight key: the firmware already toggled, mirror it (OGH: EventID 13)
                S.Light = data == 0 ? 0 : 1; S.Save(); Changed(); return;
            }
            if (id != KeyId || data != KeyData) return;
            if ((DateTime.Now - lastKey).TotalMilliseconds < 400) return;   // the key fires twice per press
            lastKey = DateTime.Now;
            if (S.SuppressOgh && !Hw.IsDemo && Supported) KillOgh();
            if (S.Key == KeyAction.Off) return;
            var h = KeyPressed; if (h != null) { try { h(S.Key); } catch { } }
        }

        // ---------- graphics mode ----------
        /// <summary>Writes the graphics mode the way OGH does on this generation; the firmware applies it at the next restart.</summary>
        public bool SetGpuMode(int mode) {
            if (mode < 0 || mode > 3 || !GpuModeOffered(mode)) return false;
            bool ok; lock (applySync) ok = Try(delegate { Hw.SetGpuMode(mode); }, "Graphics mode");
            if (ok) { GpuModePending = mode; Log.Write("graphics mode " + GpuModeNames[mode] + " written; live after a restart"); }
            Changed();
            return ok;
        }

        // ---------- display refresh rate ----------
        void ApplyRefreshRate(bool onBattery) {
            try {
                int[] rates = Display.Rates(); if (rates.Length < 2) return;
                int want = S.LowHzOnBattery && onBattery ? Display.BatteryHz() : (S.RefreshHz > 0 ? S.RefreshHz : 0);
                if (want > 0 && Display.CurrentHz() != want) Display.SetHz(want);
            } catch (Exception ex) { Log.Write("refresh rate: " + ex.Message); }
        }
        public void SetRefreshRate(int hz) {
            S.RefreshHz = hz; S.Save();
            if (Display.SetHz(hz)) Say(hz + " Hz"); else Fire(Toast, "Could not switch to " + hz + " Hz", true);
            Changed();
        }

        // ---------- keyboard lighting ----------
        void InitLight() {
            try {
                Light = Hw.IsDemo ? (ILighting)new DemoLighting() : (BiosOk && !ReadOnly ? BiosLighting.Detect() : null);
                // A per-key board answers the firmware calls and lights nothing by them. Its colours live on the
                // keyboard's own HID lighting interface, so look for that before giving up on it.
                if (!Hw.IsDemo && Light != null && Light.Inert) {
                    var la = LampArray.FindKeyboard();
                    if (la != null) Light = new PerKeyLighting(la);
                    else Log.Write("per-key board with no HID lighting interface we can drive; colours left to Windows");
                }
                if (Light == null) return;
                var fw = Light.GetColors();
                LightColors = ParseColors(S.LightColors, Light.Zones);
                if (LightColors == null) { LightColors = fw; S.LightColors = JoinColors(fw); }    // first run: keep what the keyboard shows now
                if (S.Light < 0) S.Light = (WinLighting.Present && WinLighting.HasControl) ? 2 : ((Light.GetBacklight() & BiosLighting.ON_FLAG) != 0 ? 1 : 0);
                Log.Write("lighting: " + Light.Describe + ", mode " + S.Light + ", effect " + S.LightEffect + ", level " + S.LightLevel + (WinLighting.Present ? ", Windows Dynamic Lighting present" + (WinLighting.HasControl ? " (in control)" : "") : ""));
            } catch (Exception ex) { Log.Write("lighting init: " + ex.Message); Light = null; }
        }
        static Rgb[] ParseColors(string s, int n) {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(','); var r = new Rgb[n];
            for (int i = 0; i < n; i++) { Rgb c; if (i < parts.Length && Rgb.TryParse(parts[i].Trim(), out c)) r[i] = c; else return null; }
            return r;
        }
        static string JoinColors(Rgb[] c) { var s = new List<string>(); foreach (var x in c) s.Add(x.Hex); return string.Join(",", s.ToArray()); }
        Rgb[] Scaled(Rgb[] c) { var r = new Rgb[c.Length]; double f = Math.Max(0.05, S.LightLevel / 100.0); for (int i = 0; i < c.Length; i++) r[i] = c[i].Scale(f); return r; }

        /// <summary>Push the chosen lighting state to the keyboard. Caller holds applySync.</summary>
        void ApplyLightCore() {
            if (Light == null) return;
            StopEffect();
            if (S.Light == 2) { if (WinLighting.Present) WinLighting.SetControl(true); return; }      // Windows paints; we stay out of it
            if (WinLighting.Present && WinLighting.HasControl) WinLighting.SetControl(false);        // take the keyboard first or Windows overwrites us
            Try(delegate {
                if (S.Light == 1) Light.SetColors(Scaled(LightColors));
                Light.SetBacklight(S.Light == 1, 100);                                              // the level byte OGH writes; brightness is in the colours
            }, "Keyboard lighting");
            if (S.Light == 1 && S.LightEffect != 0) StartEffect();
        }
        public void SetLight(int mode, int effect, bool announce) {
            S.Light = Math.Max(0, Math.Min(2, mode)); S.LightEffect = Math.Max(0, Math.Min(3, effect)); S.Save();
            lock (applySync) ApplyLightCore();
            if (announce) Say(S.Light == 2 ? "Keyboard: Windows Dynamic Lighting" : S.Light == 0 ? "Keyboard off" : new[] { "Keyboard static", "Keyboard breathe", "Keyboard cycle", "Keyboard wave" }[S.LightEffect]);
            Changed();
        }
        /// <summary>zones = null: every zone.</summary>
        public void SetLightColor(int[] zones, Rgb c) {
            if (Light == null) return;
            for (int i = 0; i < LightColors.Length; i++) if (zones == null || Array.IndexOf(zones, i) >= 0) LightColors[i] = c;
            S.LightColors = JoinColors(LightColors); if (S.Light != 1) S.Light = 1; S.Save();
            lock (applySync) ApplyLightCore();
            Changed();
        }
        public static double SpeedFactor(int speed) { return new[] { 0.35, 0.6, 1.0, 1.6, 2.4 }[Math.Max(0, Math.Min(4, speed - 1))]; }
        public void SetLightSpeed(int speed) { S.LightSpeed = Math.Max(1, Math.Min(5, speed)); S.Save(); Changed(); }
        public void SetLightLevel(int level) {
            S.LightLevel = Math.Max(0, Math.Min(100, level)); S.Save();
            lock (applySync) { if (S.Light == 1 && Light != null && S.LightEffect == 0) Try(delegate { Light.SetColors(Scaled(LightColors)); }, "Keyboard brightness"); }
            Changed();
        }

        /// <summary>Colour of zone i at a given phase for an effect. Shared with the editor's preview.</summary>
        public static Rgb EffectFrame(int effect, double phase, Rgb[] baseColors, int i) {
            switch (effect) {
                case 1: return baseColors[i].Scale(0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(phase * 1.6)));                 // breathe
                case 2: return Rgb.FromHue(phase * 25);                                                                  // cycle: every zone through the spectrum
                case 3: return Rgb.FromHue(phase * 25 + i * (360.0 / Math.Max(1, baseColors.Length)));                   // wave: zones offset
                default: return baseColors[i];
            }
        }
        // software effects: a frame every 120 ms through the same colour-table write (OGH animates the same way, ~15 fps)
        System.Threading.Timer fx; double fxPhase; int fxFailures;
        void StartEffect() { fxFailures = 0; if (fx == null) fx = new System.Threading.Timer(delegate { EffectTick(); }, null, 120, 120); else fx.Change(120, 120); }
        void StopEffect() { if (fx != null) fx.Change(Timeout.Infinite, Timeout.Infinite); }
        void EffectTick() {
            if (Light == null || S.Light != 1 || S.LightEffect == 0) return;
            if (!Monitor.TryEnter(applySync, 50)) return;
            try {
                fxPhase += 0.12 * SpeedFactor(S.LightSpeed); var frame = new Rgb[LightColors.Length];
                for (int i = 0; i < frame.Length; i++) frame[i] = EffectFrame(S.LightEffect, fxPhase, LightColors, i);
                try { var scaled = Scaled(frame); Light.SetColors(scaled); fxFailures = 0; var fh = FrameChanged; if (fh != null) fh(scaled); }
                catch (Exception ex) { if (++fxFailures >= 5) { Log.Write("effect stopped: " + ex.Message); StopEffect(); } }
            } finally { Monitor.Exit(applySync); }
        }

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
            sb.AppendLine("platform: " + Model + " board " + Board + " -> " + (Supported ? P.Name + (Generic ? " [generic, unverified]" : "") : "UNSUPPORTED (read-only)"));
            sb.AppendLine("bios ok: " + BiosOk + (LastError.Length > 0 ? "  last error: " + LastError : ""));
            sb.AppendLine("fans: " + FanCount + "   system data: " + Info.Hex + "   policy v" + Info.ThermalPolicy + "  swFan=" + Info.SwFanControl + "  PL4=" + Info.DefaultPl4 + "W  baseTdp=" + Info.DefaultConcurrentTdp + "W");
            try { var f = Hw.GetFanLevels(); sb.AppendLine("fan levels: " + f[0] + " / " + f[1] + "  (x100 RPM)"); } catch (Exception ex) { sb.AppendLine("fan levels: " + ex.Message); }
            try { sb.AppendLine("bios temp sensor: " + Hw.GetTemperature() + " C"); } catch (Exception ex) { sb.AppendLine("temp: " + ex.Message); }
            try { sb.AppendLine("max fan: " + Hw.GetMaxFan()); } catch (Exception ex) { sb.AppendLine("max fan: " + ex.Message); }
            try { sb.AppendLine("gpu power: " + Hw.GetGpuPower()); } catch (Exception ex) { sb.AppendLine("gpu power: " + ex.Message); }
            sb.AppendLine("settings: mode=" + ModeName + " (BIOS 0x" + ModeByte.ToString("X2") + (OnBattery ? ", DC" : ", AC") + ") fan=" + S.Fan + " " + S.Fan1 + "/" + S.Fan2 + " tdp=" + CurrentTdp + "W gpu=" + EffectiveGpu + (S.GpuAuto ? "(auto)" : "") + " key=" + KeyId + "/" + KeyData + "→" + S.Key + " ecoCool=" + S.EcoCool);
            sb.AppendLine("graphics: " + (GpuMode >= 0 && GpuMode < 4 ? GpuModeNames[GpuMode] : "unknown") + " (offered mask 0x" + Info.GpuModes.ToString("X2") + ")" + (GpuModePending >= 0 ? " -> " + GpuModeNames[GpuModePending] + " after restart" : ""));
            sb.AppendLine("lighting: " + (Light == null ? "none" : Light.Describe + " mode=" + S.Light + " level=" + S.LightLevel + " colours=" + S.LightColors + " windowsControl=" + WinLighting.HasControl));
            sb.AppendLine("fan drive: written " + curLevel1 + "/" + curLevel2 + "  cpu " + Fmt(CpuTemp) + "  gpu " + Fmt(GpuTemp) + "  ir " + Fmt(IrTemp) + "  guard=" + GuardActive + "  writeFailures=" + fanWriteFailures);
            sb.AppendLine("last heartbeat: " + (LastHeartbeat == DateTime.MinValue ? "never" : LastHeartbeat.ToString("HH:mm:ss")) + "   last key event: " + (LastEventTime == DateTime.MinValue ? "none" : LastEventId + "/" + LastEventData + " at " + LastEventTime.ToString("HH:mm:ss")));
            return sb.ToString();
        }
    }
}

