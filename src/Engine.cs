// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: control engine: settings, apply logic, keep-alive heartbeat, OMEN key watcher, OGH suppression.
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
    public enum GpuLevel { Base = 0, Boost = 1, Max = 2 }   // {cTGP,PPAB} = {0,0} / {0,1} / {1,1}, the three payloads OGH sends

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
            int n;
            bool b;
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
            var parts = v.Split(',');
            if (parts.Length != Engine.CurveTemps.Length) return null;
            var lv = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (!Settings.TryInt(parts[i].Trim(), out lv[i])) return null;
            return lv;
        }
        static string JoinCurve(int[] lv) { return string.Join(",", Array.ConvertAll(lv, delegate(int x) { return x.ToString(); })); }
        public void Write(StringBuilder sb, string prefix) {
            sb.AppendLine(prefix + "Fan=" + (int)Fan);
            sb.AppendLine(prefix + "Fan1=" + Fan1);
            sb.AppendLine(prefix + "Fan2=" + Fan2);
            sb.AppendLine(prefix + "TdpOffset=" + TdpOffset);
            sb.AppendLine(prefix + "Gpu=" + (int)Gpu);
            sb.AppendLine(prefix + "GpuAuto=" + GpuAuto);
            if (CurveLevels != null) sb.AppendLine(prefix + "Curve=" + JoinCurve(CurveLevels));
            if (GpuCurveLevels != null) sb.AppendLine(prefix + "GpuCurve=" + JoinCurve(GpuCurveLevels));
            sb.AppendLine(prefix + "CurveLink=" + CurveLinked);
            sb.AppendLine(prefix + "CurveFloor=" + CurveFloor);
            sb.AppendLine(prefix + "CurveRamp=" + CurveRamp);
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
        public string CheckedFrom = "";             // the build that did that check; a different one means the cache is not ours
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
        public int PollMs = 2000;                   // sensor refresh while the window is open
        public bool TookWinLighting = false;        // we switched Windows Dynamic Lighting off and owe it back
        public bool PerKeyReset = false;            // the one-time clear of the all-white per-key array
        public int FanBeforeMax = 0;                // FanMode that Max interrupted; survives a restart
        public bool InfoDismissed = false;          // the first-on-this-board note, closed by the user
        public bool Guard = true;                   // thermal guard: force max fan when the machine runs away
        public int GuardCpu, GuardChassis;          // the guard's own limits, 0 = the profile's
        public int GuardLevel;                      // what it forces: 0 = max fan, else a fan level
        public int GuardHold;                       // seconds below the limits before it lets go, 0 = the profile's
        public bool UpdateOnLaunch = true;          // ask GitHub for the latest release when Ohman starts (once a day)
        public string KeyCommand = "";              // KeyAction.Run: command line the OMEN key starts
        public string[] HotkeyText = new string[HotkeyTable.Count];   // per action: null = default, "" = none, else "Ctrl+Alt+E"
        public bool DriverUse = true;               // use the PawnIO driver when it is installed
        public int FanCeilingSeen;                  // top fan level measured on this machine, 0 = not learned yet
        public bool DriverInstalledByOhman = false; // we put it there, so Uninstall may offer to take it away
        public bool DriverRestartPending = false;   // the installer asked for a restart and has not had one
        public string DriverNudgeDismissed = "";    // the Ohman version whose Home-page nudge was closed
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
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
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
                if (k.StartsWith("Hotkey.", StringComparison.Ordinal)) {
                    int hi = Array.IndexOf(HotkeyTable.Keys, k.Substring(7));
                    if (hi >= 0) s.HotkeyText[hi] = v.Trim();
                    return;
                }
                // un-prefixed keys apply to every mode (handy for hand edits and --set)
                if (k == "Fan" || k == "Fan1" || k == "Fan2" || k == "TdpOffset" || k == "Gpu" || k == "GpuAuto" || k == "Curve") { foreach (var m in Modes) m.Apply(k, v); return; }
                {
                    int n;
                    bool b;
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
                        case "CheckedFrom": s.CheckedFrom = v; break;
                        case "PollMs": { int pm; if (int.TryParse(v, out pm) && pm >= 500 && pm <= 5000) s.PollMs = pm; break; }
                        case "TookWinLighting": if (bool.TryParse(v, out b)) s.TookWinLighting = b; break;
                        case "PerKeyReset": if (bool.TryParse(v, out b)) s.PerKeyReset = b; break;
                        case "FanBeforeMax": if (TryInt(v, out n)) s.FanBeforeMax = Math.Max(0, Math.Min(3, n)); break;
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
                        case "GuardCpu": if (int.TryParse(v, out n) && n >= 70 && n <= 105) s.GuardCpu = n; break;
                        case "GuardChassis": if (int.TryParse(v, out n) && n >= 40 && n <= 80) s.GuardChassis = n; break;
                        case "GuardLevel": if (int.TryParse(v, out n) && n >= 0 && n <= 255) s.GuardLevel = n; break;
                        case "GuardHold": if (int.TryParse(v, out n) && n >= 0 && n <= 900) s.GuardHold = n; break;
                        case "UpdateOnLaunch": if (bool.TryParse(v, out b)) s.UpdateOnLaunch = b; break;
                        case "KeyCommand": s.KeyCommand = v; break;
                        case "DriverUse": if (bool.TryParse(v, out b)) s.DriverUse = b; break;
                        case "DriverInstalledByOhman": if (bool.TryParse(v, out b)) s.DriverInstalledByOhman = b; break;
                        case "DriverRestartPending": if (bool.TryParse(v, out b)) s.DriverRestartPending = b; break;
                        case "DriverNudgeDismissed": s.DriverNudgeDismissed = v.Length > 24 ? v.Substring(0, 24) : v; break;
                        case "FanCeilingSeen": { int fc; if (int.TryParse(v, out fc) && fc >= 0 && fc <= 255) s.FanCeilingSeen = fc; break; }
                    }
                }
            } catch (Exception ex) { Log.Write("settings apply " + k + ": " + ex.Message); }
        }

        internal static bool TryInt(string v, out int n) {
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n);
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        static readonly object saveSync = new object();
        /// <summary>Remove the settings file. Only the reset uses this: it has just undone everything the file
        /// described, and leaving it would put all of it back the moment anything ran again.</summary>
        public void Delete() {
            NoPersist = true;
            try { if (System.IO.File.Exists(File_)) System.IO.File.Delete(File_); } catch { }
        }

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
                sb.AppendLine("ModeIndex=" + (SavedModeOverride >= 0 ? SavedModeOverride : ModeIndex));
                sb.AppendLine("EcoCool=" + EcoCool);
                sb.AppendLine("# per-mode profiles: M0 = Eco, M1 = Balanced, M2 = Performance");
                for (int i = 0; i < 3; i++) Modes[i].Write(sb, "M" + i + ".");
                sb.AppendLine("Key=" + (int)Key);
                sb.AppendLine("KeyId=" + KeyId);
                sb.AppendLine("KeyData=" + KeyData);
                sb.AppendLine("SuppressOgh=" + SuppressOgh);
                sb.AppendLine("Hotkeys=" + Hotkeys);
                sb.AppendLine("EcoOnBattery=" + EcoOnBattery);
                sb.AppendLine("SyncWinPower=" + SyncWinPower);
                sb.AppendLine("HeartbeatSec=" + HeartbeatSec);
                sb.AppendLine("Light=" + Light);
                sb.AppendLine("LightColors=" + LightColors);
                sb.AppendLine("LightLevel=" + LightLevel);
                sb.AppendLine("LightEffect=" + LightEffect);
                sb.AppendLine("LightSpeed=" + LightSpeed);
                sb.AppendLine("RefreshHz=" + RefreshHz);
                sb.AppendLine("LowHzOnBattery=" + LowHzOnBattery);
                sb.AppendLine("TrayTemp=" + TrayTemp);
                sb.AppendLine("KeyCommand=" + KeyCommand);
                for (int i = 0; i < HotkeyTable.Count; i++) if (HotkeyText[i] != null) sb.AppendLine("Hotkey." + HotkeyTable.Keys[i] + "=" + HotkeyText[i]);   // only what differs from the defaults
                sb.AppendLine("Guard=" + Guard);
                if (GuardCpu > 0) sb.AppendLine("GuardCpu=" + GuardCpu);
                if (GuardChassis > 0) sb.AppendLine("GuardChassis=" + GuardChassis);
                if (GuardLevel > 0) sb.AppendLine("GuardLevel=" + GuardLevel);
                if (GuardHold > 0) sb.AppendLine("GuardHold=" + GuardHold);
                sb.AppendLine("UpdateOnLaunch=" + UpdateOnLaunch);
                sb.AppendLine("DriverUse=" + DriverUse);
                sb.AppendLine("DriverInstalledByOhman=" + DriverInstalledByOhman);
                sb.AppendLine("DriverRestartPending=" + DriverRestartPending);
                sb.AppendLine("DriverNudgeDismissed=" + DriverNudgeDismissed);
                sb.AppendLine("FanCeilingSeen=" + FanCeilingSeen);
                sb.AppendLine("MaxBackWhenCool=" + MaxBackWhenCool);
                sb.AppendLine("MaxStopAfterMin=" + MaxStopAfterMin);
                sb.AppendLine("ManualLinked=" + ManualLinked);
                sb.AppendLine("UpdateChecked=" + UpdateChecked);
                sb.AppendLine("LatestVersion=" + LatestVersion);
                sb.AppendLine("CheckedFrom=" + CheckedFrom);
                sb.AppendLine("PollMs=" + PollMs);
                sb.AppendLine("TookWinLighting=" + TookWinLighting);
                sb.AppendLine("PerKeyReset=" + PerKeyReset);
                sb.AppendLine("FanBeforeMax=" + FanBeforeMax);
                sb.AppendLine("WinX=" + WinX);
                sb.AppendLine("WinY=" + WinY);
                sb.AppendLine("StartHidden=" + StartHidden);
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
        public uint LastEventId, LastEventData;
        public DateTime LastEventTime = DateTime.MinValue;

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
        bool graphicsReadable;
        public bool GpuModeOffered(int mode) {
            if (!graphicsReadable && !Hw.IsDemo) return false;
            int bit = mode == 3 ? 1 : mode == 0 ? 2 : mode == 1 ? 4 : 8;
            return (Info.GpuModes & bit) != 0;
        }
        public ILighting Light;                             // null = this keyboard has no controllable lighting (or read-only board)
        public Rgb[] LightColors = new Rgb[0];              // what the app believes the zones show
        public bool ReadOnly { get { return !Supported && !Hw.IsDemo; } }

        // ---------- the driver ----------
        // Optional, and only ever additive: with it the CPU's own temperature and power limits are read, and on
        // a board whose mailbox refuses fan levels the EC is written instead. Without it Ohman is what it was.
        public CpuRegisters Cpu;                            // null = no driver, switched off, or the CPU is not one we have a module for
        public EmbeddedController Ec;                       // null = no driver, or this board has no EC map (never guessed)
        public string DriverWhy = "";                       // why Cpu and Ec are null, for the row in Settings
        public volatile bool DriverBusy;                    // an install or removal is running
        public volatile string DriverProgress = "";         // its current step, for the row
        public enum FanRoute { Mailbox, Ec }
        public FanRoute Route = FanRoute.Mailbox;           // where fan levels go
        bool ecVerified;                                    // the EC recognised its own registers on this machine
        public string EcProof = "";                         // what it made of them, for the report
        public bool EcVerified { get { return ecVerified; } }
        public bool DriverReady { get { return Cpu != null || (Ec != null && ecVerified); } }
        /// <summary>The installed driver's version, read when the driver state changes rather than on every
        /// repaint: the row asking the registry four times a second for two values that change twice in the
        /// life of the process is exactly the kind of cost this app measures and removes.</summary>
        public Version DriverVersion;
        public bool DriverInstalled { get { return Hw.IsDemo ? DriverReady : DriverVersion != null; } }
        public bool DriverOutdated { get { return DriverVersion != null && DriverVersion < PawnIo.MinVersion; } }

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
            // A cached answer belongs to the build that fetched it. Version numbers do not only go up: this one
            // went 1.2 -> 1.0 when it was renumbered for release, so a tag cached by an older build compares as
            // newer than anything we could actually offer and the update badge never clears. If the running build
            // is not the one that did the check, the cache is stale by definition and the day's wait is skipped.
            bool otherBuild = S.CheckedFrom != Program.Version;
            if (!force && !otherBuild && S.UpdateChecked != 0 && (DateTime.Now - LastUpdateCheck).TotalHours < 24) return;
            Release rel = Update.Latest();
            string tag = rel == null ? null : rel.Tag;
            S.UpdateChecked = DateTime.Now.Ticks;
            // Only a check that actually reached GitHub may claim the cache for this build. Stamping it on a
            // failed fetch spends the new-build recheck on nothing and leaves a stale tag for another day.
            if (tag != null) { S.LatestVersion = tag; S.CheckedFrom = Program.Version; }
            S.Save();
            if (force) Say(tag == null ? "Update check failed" : Update.Newer(tag, Program.Version) ? "Version " + tag + " is available" : "Ohman is up to date");
            Changed();
            // Fetch it now rather than when the owner asks. This runs on a background thread already, and the
            // point of doing it here is that "restart to update" is offered only once there is something on disk
            // to restart into: a button that begins a download after it is pressed can fail after the promise.
            // Compare against what is already staged, not merely whether something is. Testing for presence meant
            // that once 1.0.6 was waiting, 1.0.7 was never fetched and the row went on offering the older build.
            // Gated on the switch as well: it promises a background download, so turning it off has to stop one,
            // and a forced check from an owner who opted out should still only tell them.
            string have = Staged ?? Program.Version;
            if (rel != null && S.UpdateOnLaunch && Update.Newer(rel.Tag, have) && Update.Stage(rel)) { RefreshStaged(); Changed(); }
        }
        volatile string staged;
        /// <summary>The version downloaded and waiting to go in, or null. Read from the field: the UI asks on
        /// every refresh and the answer costs a file open.</summary>
        public string Staged { get { return staged; } }
        public void RefreshStaged() { staged = Update.Staged(); }
        /// <summary>Put the staged build in place and hand over to it. The window exits straight after.</summary>
        public bool StartUpdate(out string error) { return Update.Swap(out error); }

        public void Init() { Init(true); }

        /// <summary>apply=false detects the board and the keyboard and stops there. Used by the support report,
        /// which must not kill the vendor app, move the refresh rate, write a mode byte or start a fan timer --
        /// and which can run while another Ohman is already doing all four.</summary>
        public void Init(bool apply) {
            RefreshStaged();        // a build downloaded in an earlier session is still there to be offered
            try { OnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
            Board = Platforms.ReadBoard();
            Model = Platforms.ReadModel();
            var prof = Platforms.Find(Board);
            Supported = prof != null || Hw.IsDemo;
            if (prof != null) P = prof;
            Log.Write("platform: model='" + Model + "' board='" + Board + "' -> " + (prof != null ? prof.Name : "no verified profile"));
            try {
                // The fan table (0x2F) is not in every firmware's command set: a 2018 OMEN answers 0x28 happily and
                // returns rc 3 for this one. It used to be the first call in this block, so a board that simply had
                // fewer opcodes threw before BiosOk was ever set and the whole app went read-only, including the
                // mode switching that board can do perfectly well. Nothing here is allowed to decide that any more:
                // the mailbox works if 0x28 answers, and every other capability is probed on its own below.
                try { FanCount = Hw.GetFanCountPassive(); }   // never the 0x10 query here: it is the keep-alive trigger
                catch (Exception ex) { FanCount = -1; Log.Write("no fan table (" + ex.Message + "): fan readout unavailable"); }
                // Some firmware refuses 0x28 outright (rc 3). That is an answer, not a dead mailbox, so it must not
                // abort the rest of the check: a board can still have its mode bytes from a contributed readback.
                try { Info = Hw.GetSystemInfo(); }
                catch (Exception ex) { Info = new SystemInfo(); Log.Write("system data (0x28) unavailable: " + ex.Message); }
                BiosOk = true;                                 // the mailbox answers; what it will answer is decided below
                if (!Info.Valid) Log.Write("no system data: power gain and graphics cannot be offered on this board");
                if (Supported && !Hw.IsDemo && Info.Valid && Info.ThermalPolicy != P.ThermalPolicy) {
                    Supported = false;
                    Log.Write("thermal policy v" + Info.ThermalPolicy + " does not match the profile (v" + P.ThermalPolicy + "); switching to read-only");
                }
                if (prof == null && (!Hw.IsDemo || Platforms.BoardOverride != null)) {
                    // no verified profile: build one from what the firmware says about itself, the way the Linux driver does
                    var g = Platforms.Generic(Board, Info);
                    if (g != null) {
                        try { Hw.GetGpuPower(); g.HasGpuPower = true; } catch (Exception ex) { Log.Write("generic: no GPU power control (" + ex.Message + ")"); }
                        try { int top = Hw.GetFanTableMax(); if (top > g.Curve.Ceiling) g.Curve.Rescale(top); } catch { }
                        P = g;
                        Supported = true;
                        Generic = true;
                        Log.Write("generic profile: " + g.Notes + " · modes " + g.ModeEco.ToString("X2") + "/" + g.ModeBalanced.ToString("X2") + "/" + g.ModePerformance.ToString("X2") + " · powerGain=" + g.HasPowerGain + " (base " + g.TdpBase + " W) · gpuPower=" + g.HasGpuPower + " · fan ceiling " + g.Curve.Ceiling);
                    } else Log.Write("generic profile not possible (thermal policy v" + Info.ThermalPolicy + "); read-only");
                }
                ApplyMeasuredCeiling();
                // Byte 7 advertises which graphics modes exist, but 8BC2 advertises them and then refuses 0x52
                // both ways: rc 3 reading, rc 6 writing. Its owner could pick a mode and nothing happened, so a
                // firmware that will not say which mode it is in does not get to be asked to change it.
                try { GpuMode = Hw.GetGpuMode(); graphicsReadable = true; }
                catch (Exception ex) { Log.Write("graphics mode read: " + ex.Message + " - graphics switching not offered"); }
                Log.Write("BIOS ok: fans=" + FanCount + " policy=v" + Info.ThermalPolicy + " swFan=" + Info.SwFanControl + " defPL4=" + Info.DefaultPl4 + "W baseTdp=" + Info.DefaultConcurrentTdp + "W raw=" + Info.Hex + (Hw.IsDemo ? " (DEMO)" : ""));
            } catch (Exception ex) { BiosOk = false; LastError = ex.Message; Log.Write("BIOS self-test FAILED: " + ex.Message); }
            InitDriver();           // reads only; the fan route it may pick is not written until ApplyAll below
            // Everything above only looked. Everything below changes the machine, and the support report wants the
            // first half without the second: it must describe this laptop, not reconfigure it.
            if (!apply) { InitLight(); Log.Write("probe only: nothing was applied"); return; }
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
        DateTime maxSince = DateTime.MinValue, maxCoolSince = DateTime.MinValue;
        string maxStopReason = "";
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
            maxStopReason = "below " + P.Guard.MaxFanCoolBelow + "° for " + (P.Guard.MaxFanCoolSeconds / 60) + " minutes";
            return true;
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
            if (leaveMax) { Say("Max fan off · " + maxStopReason); SetFan(fanBeforeMax, S.Fan1, S.Fan2, false); }
        }

        // Work the UI hands over runs on one thread in the order it was posted. The thread pool does not promise that,
        // and applySync only serialises: two quick clicks could otherwise leave the firmware holding the first one.
        readonly Queue<Action> work = new Queue<Action>();
        readonly AutoResetEvent workReady = new AutoResetEvent(false);
        Thread worker;
        volatile bool stopping;
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
        /// <summary>Put the machine back the way it was, then let the caller quit. Everything Ohman changes
        /// outside its own folder is undone here, vendor software first so the laptop has its own tools back.
        ///
        /// This exists because an owner panicked. He could not get his keyboard back, uninstalling did not help,
        /// and he had no route to the software he already knew. Anything that writes to firmware and switches off
        /// the vendor's own tools owes people a way out that does not depend on the author shipping a fix in time.
        ///
        /// The graphics mode is deliberately not reverted: changing it needs a restart and it is an explicit
        /// choice somebody made, so the dialog names it rather than silently undoing it.</summary>
        public string FactoryReset() {
            var done = new List<string>();
            try { SetOghTasks(false); done.Add("re-enabled OMEN Gaming Hub's tasks"); }
            catch (Exception ex) { Log.Write("reset ogh tasks: " + ex.Message); }
            try {
                if (!Hw.IsDemo && S.TookWinLighting && WinLighting.Present) {
                    WinLighting.SetControl(true);
                    S.TookWinLighting = false;
                    done.Add("gave the keyboard back to Windows Dynamic Lighting");
                }
            } catch (Exception ex) { Log.Write("reset lighting: " + ex.Message); }
            try {
                int top = Display.HighestHz();
                if (top > 0 && (S.RefreshHz > 0 || S.LowHzOnBattery) && Display.CurrentHz() != top) {
                    Display.SetHz(top);
                    done.Add("put the refresh rate back to " + top + " Hz");
                }
            } catch (Exception ex) { Log.Write("reset refresh: " + ex.Message); }
            if (BiosOk && !Hw.IsDemo && !ReadOnly) {
                try {
                    lock (applySync) {
                        Try(delegate { Hw.SetMode(P.ModeBalanced, OnBattery); }, "Reset mode");
                        MaxFan(false, "Reset max fan");
                        if (Route == FanRoute.Ec) ReleaseEcFans("reset");
                        else WriteLevels(P.Curve.Fallback, P.Curve.Fallback, "Reset fan level");
                    }
                    done.Add("set the mode back to balanced and handed the fans back");
                } catch (Exception ex) { Log.Write("reset firmware: " + ex.Message); }
            }
            try {
                if (Light != null && !Hw.IsDemo) {
                    TryLight(delegate { Light.SetBacklight(true, 100); }, "Reset backlight");
                    done.Add("turned the keyboard backlight back on");
                }
            } catch (Exception ex) { Log.Write("reset backlight: " + ex.Message); }
            var sb = new StringBuilder();
            foreach (string d in done) sb.AppendLine("  - " + d);
            Log.Write("factory reset: " + string.Join("; ", done.ToArray()));
            return sb.ToString();
        }

        /// <summary>Leave the fans somewhere they can survive being left. We hold them by refreshing the
        /// firmware's user-defined state; stop refreshing and the firmware keeps the last level we wrote for
        /// about 120 s before its own curve resumes (research.md §4). Quitting at an idle level and then
        /// starting a game would therefore leave the fans idle while the chips climb. Fallback is the level
        /// the curve uses when it cannot see a temperature at all, which is exactly the situation we are about
        /// to be in, so it is the right number to leave behind. Never writes lower than what is already set.</summary>
        public void Park() { Park(false); }
        /// <param name="quiet">Nothing is going to need the parting level raised: Windows is logging off or
        /// restarting, or a replacement build is starting this second. The level we leave behind is replayed by
        /// the firmware for about two minutes, which on a restart is most of the next boot: an owner who quit at
        /// idle got Fallback back at the login screen and read it as the fans maxing out.</param>
        public void Park(bool quiet) {
            // Give the keyboard back before anything else. To paint it at all we switch Windows Dynamic Lighting
            // off, and we were never switching it back: quitting left the keyboard frozen on the last thing we
            // wrote, with Windows told to keep out of it. One owner uninstalled Ohman, rebooted, and still had
            // our colours, because nothing left on the machine was allowed to change them.
            try {
                if (!Hw.IsDemo && S.TookWinLighting && WinLighting.Present) {
                    WinLighting.SetControl(true);
                    S.TookWinLighting = false;
                    S.Save();
                    Log.Write("handed the keyboard back to Windows Dynamic Lighting");
                }
            } catch (Exception ex) { Log.Write("release lighting: " + ex.Message); }
            if (!BiosOk || Hw.IsDemo || ReadOnly) return;
            try {
                // Max fan is a flag the firmware holds until something clears it, and once we have exited nothing
                // will. This used to return early when Max was on or the guard was engaged - "already at maximum,
                // leave it there" - which on the way out means leaving it there for good. Two owners reported the
                // fans running flat out until they rebooted or slept the machine, one after using Max, one after
                // the guard fired. Whatever we were doing, the fans go back to the firmware before we go.
                MaxFan(false, "Max fan off on exit");
                // The level is still written high on purpose. The firmware replays the last pair for about two
                // minutes before its own curve resumes, and that is the handover: a hot machine keeps its cooling
                // across it rather than dropping to a middling level the moment we quit.
                // curLevel is -1 until a level has actually been written, and in Max fan mode nothing ever writes
                // one, so quiet + -1 used to come out as Math.Max(0, -1) = 0: a machine that was on max fan
                // because it was hot, restarted, and left with its fans stopped for the two minutes the EC
                // replays the last pair. Quiet only means "do not raise a level we know"; not knowing one is
                // exactly the case Fallback exists for.
                int cur = Math.Max(curLevel1, curLevel2);
                int want = quiet && cur >= 0 ? cur : Math.Max(P.Curve.Fallback, cur);
                // On the EC route the fans are handed back rather than parked: the EC resumes its own curve on
                // its own clock once manual mode is cleared, and there is no replay of the last level to survive.
                if (Route == FanRoute.Ec) { lock (applySync) ReleaseEcFans("exit"); }
                else {
                    lock (applySync) WriteLevels(want, want, "Fan level on exit");
                    Log.Write("parked fans at " + want + " and cleared max fan; the firmware resumes its own curve within ~120 s");
                }
            } catch (Exception ex) { Log.Write("park fans: " + ex.Message); }
        }

        public void Dispose() {
            stopping = true; try { workReady.Set(); } catch { }
            try { if (fx != null) fx.Dispose(); } catch { }
            CloseDriver();
            try { if (fanTimer != null) fanTimer.Dispose(); } catch { }
            try { if (heartbeat != null) heartbeat.Dispose(); } catch { }
            try { if (guard != null) guard.Dispose(); } catch { }
            try { if (watcher != null) { watcher.Stop(); watcher.Dispose(); } } catch { }
        }

        // ---------- apply ----------
        /// <summary>Lighting lives behind a different command id (0x20009) from everything else (0x20008), and the
        /// two are not supported together. Board 8574 answers every lighting call and returns rc 3 for every
        /// performance one, so gating the backlight on whether we understand the board's fan and mode bytes hid a
        /// keyboard that works. Nothing here writes a performance byte, so ReadOnly has no say over it.</summary>
        bool TryLight(Action a, string what) {
            try { a(); return true; }
            catch (Exception ex) { Log.Write("FAIL " + what + ": " + ex.Message); Fire(Toast, what + " failed: " + ex.Message, true); return false; }
        }

        bool Try(Action a, string what) {
            if (ReadOnly) { Log.Write("read-only (unsupported board '" + Board + "'): skipped " + what); return false; }
            try { a(); LastError = ""; return true; }
            catch (Exception ex) { LastError = ex.Message; Log.Write("FAIL " + what + ": " + ex.Message); Fire(Toast, what + " failed: " + ex.Message, true); return false; }
        }
        void Fire(Action<string, bool> h, string m, bool err) { if (h != null) { try { h(m, err); } catch { } } }
        void Changed() { var h = StateChanged; if (h != null) { try { h(); } catch { } } }
        /// <summary>A line for the user. Only for things they did not just ask for: the guard, a battery switch, an
        /// update result. Echoing a change back at the person who made it is noise, so every "announce" from a
        /// button, menu item or hotkey is false, the control they used already shows the new state.</summary>
        void Say(string m) { Log.Write(m); Fire(Toast, m, false); }

        public void ApplyAll(bool announce) {
            lock (applySync) {
                if (!BiosOk && !Hw.IsDemo) return;
                Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode");
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

        // The documented place to find a board's top fan level is its own fan table on 0x2F, and on some boards
        // that table is not populated: 8A26 answers with one fan and one row out of twelve. Ohman then keeps the
        // Transcend 14's 57, the curve asks for a speed these fans cannot reach, and the readout divides a real
        // 4300 rpm by an imaginary 5700 and shows 75% at full tilt, with Max looking dead because the curve was
        // already pinned at the cap. So measure it instead. 0x2D reports the speed the fans are turning, not the
        // level asked for, which is why it has to settle: a fan still spooling up reads low and means nothing.
        /// <summary>Use what this machine was measured doing, whichever way it points.
        ///
        /// A verified profile's ceiling is no more measured than a generic one's: 8C58's 57 came out of OGH's
        /// profiles.json, and asking that machine for maximum fan gets 59 back. So this applies everywhere. What
        /// differs is the curve underneath. A generic board is running the Transcend 14's curve on loan and wants
        /// it stretched to its own range; a verified board's curve is already its own, and stretching it would
        /// walk it away from the table it was checked against, so there only the ceiling moves.</summary>
        int pristineCeiling;
        void ApplyMeasuredCeiling() {
            if (P == null || P.Curve == null) return;
            // The verified profiles are one shared static instance, so a ceiling written straight onto it
            // outlives the setting that asked for it: Reset clears FanCeilingSeen and the next Init would
            // otherwise keep the old number with nothing on disk saying so. Remember the profile's own value
            // and always derive from it, so the measurement can be taken back as well as applied.
            if (pristineCeiling == 0) pristineCeiling = P.Curve.Ceiling;
            int want = S.FanCeilingSeen > P.Curve.Floor + CeilingUsableRange ? S.FanCeilingSeen : pristineCeiling;
            int was = P.Curve.Ceiling;
            if (want == was) return;
            if (Generic) P.Curve.Rescale(want); else P.Curve.Ceiling = want;
            Log.Write("fan ceiling " + was + " -> " + P.Curve.Ceiling
                + (want == pristineCeiling ? ", back to the profile's own" : ", measured on this machine")
                + (Generic ? " (curve rescaled with it)" : ""));
        }

        int ceilingTicks, ceilingHighWater;
        const int CeilingSettleTicks = 8;          // readings spent asking for the top before the answer is believed
        // The high-water mark is the fastest reading seen, so it is a lower bound and the two directions are not
        // the same claim. Seeing the fans go past the ceiling proves the ceiling is too low, whatever the margin.
        // Falling short of it only means they did not get there this time, which a stuck fan or a cold room also
        // explains, so that direction has to clear a gap noise cannot.
        const int CeilingOver = 2, CeilingShort = 10;
        // Rescale stretches the level tables but never moves Floor, so a ceiling close to it would leave the
        // curve, the sliders and the whole fan page with a handful of levels between them. Below this it is a
        // misread unit or a stuck fan, not a ceiling.
        const int CeilingUsableRange = 15;
        readonly object ceilingSync = new object();
        /// <summary>One fan reading, from wherever Ohman happened to take it. Applied at the next start rather
        /// than now: the sliders take their range once, before anything is wired to them, because changing a
        /// Maximum coerces the Value under it and that would read as the owner moving the slider.</summary>
        public void NoteFanLevels(int[] f) {
            if (f == null || f.Length < 2 || P == null || P.Curve == null) return;
            // The UI sensor poll and the guard timer both land here, and the counters below are a sequence, not
            // one value: interleaved they lose increments and reset each other mid-test.
            lock (ceilingSync) NoteFanLevelsCore(f);
        }
        void NoteFanLevelsCore(int[] f) {
            int seen = Math.Max(f[0], f[1]);
            if (seen <= 0 || seen > 255) return;
            // Only while asking for everything. Any lower and a low reading says nothing about the limit.
            if (!(GuardActive || S.Fan == FanMode.Max || Math.Max(curLevel1, curLevel2) >= P.Curve.Ceiling)) {
                ceilingTicks = 0; ceilingHighWater = 0; return;
            }
            if (seen > ceilingHighWater) ceilingHighWater = seen;
            if (++ceilingTicks < CeilingSettleTicks) return;
            ceilingTicks = 0;
            int real = ceilingHighWater;
            int gap = real - P.Curve.Ceiling;
            if (gap < CeilingOver && gap > -CeilingShort) return;
            if (real <= P.Curve.Floor + CeilingUsableRange || real == S.FanCeilingSeen) return;
            S.FanCeilingSeen = real;
            S.Save();
            Log.Write("fan ceiling learned: asked for " + P.Curve.Ceiling + " and these fans never went past " + real
                + "; using " + real + " from the next start");
        }
        public int AutoLevel1 { get { return curLevel1; } }
        public int AutoLevel2 { get { return curLevel2; } }
        public double GpuTemp = double.NaN, IrTemp = double.NaN;   // GpuTemp fed by the UI sensor loop; IrTemp read here
        int fanWriteFailures;

        /// <summary>Whether this firmware still takes fan levels. Driven by what it does, not by what it says.
        ///
        /// HP's "software fan control supported" flag - system-design byte 4 bit 0 - looked like the answer for
        /// 878A, which clears it and refuses 0x2E with rc 46 once a minute forever. It is not the answer: 8A26
        /// clears the same bit and takes fan levels perfectly, on a board whose owner verified it as a full OGH
        /// replacement. Identical byte, opposite behaviour, so the claim does not predict anything and gating on
        /// it would have broken working fan control.
        ///
        /// So count the refusals instead and stop asking once the firmware has made itself clear. Max fan and the
        /// performance modes go through their own commands and are unaffected.</summary>
        public bool CanSetFanLevels { get { return !fanLevelsRefused || Route == FanRoute.Ec; } }
        const int FanWriteGiveUp = 8;
        bool fanLevelsRefused;

        bool WriteLevels(int l1, int l2, string what) {
            // ClampOrOff, not Clamp. Clamp floors at the profile's own Floor, which is 18 on every profile we
            // ship, so it turned every 0 back into 18 on the way out - the last step of the path, after the
            // editor, the sliders and the settings had all been taught to carry one. The whole of "let the fans
            // stop" was inert and the only place it showed was the fans not stopping.
            l1 = P.Curve.ClampOrOff(l1);
            l2 = P.Curve.ClampOrOff(l2);
            if (Route == FanRoute.Ec) return WriteLevelsEc(l1, l2, what);
            // A refusing board whose EC route was dropped (three failures, or a rest after timeouts) gets it back
            // the moment the controller is usable again. Without this the give-up branch below is the only other
            // caller of ChooseRoute, and it sits behind the refusal check, so a rest was permanent until restart.
            if (fanLevelsRefused && Ec != null && ChooseRoute() == FanRoute.Ec) return WriteLevelsEc(l1, l2, what);
            if (fanLevelsRefused) return false;
            if (Try(delegate { Hw.GetFanCount(); Hw.SetFanLevels(l1, l2); }, what)) { curLevel1 = l1; curLevel2 = l2; fanWriteFailures = 0; fanFailureShown = false; return true; }
            fanWriteFailures++;
            if (fanWriteFailures >= FanWriteGiveUp) {
                fanLevelsRefused = true;
                Log.Write("giving up on fan levels after " + fanWriteFailures + " refusals; this firmware will not take them. Max fan and the modes are unaffected.");
                // The refusal is exactly what the EC route exists for. With the driver and a map, switch and
                // carry on; without, tell the owner, and the Home page will point at the driver if a map exists.
                if (ChooseRoute() == FanRoute.Ec) { Say("Fan levels now go through the driver"); return WriteLevelsEc(l1, l2, what); }
                Fire(Toast, "This firmware will not take fan levels; its own curve stays in charge", true);
                Changed();
            } else if (fanWriteFailures >= 3 && !fanFailureShown) {
                fanFailureShown = true;
                Fire(Toast, "Fan writes failing; firmware curve will take over", true);
            }
            return false;
        }

        /// <summary>Fan levels through the EC. The mailbox's max-fan flag is left alone on purpose: it works on
        /// these boards, and the guard still uses it. Three failures in a row and the route goes back to the
        /// mailbox, which on a refusing board means the firmware's own curve - never nothing.</summary>
        int ecFailures;
        bool WriteLevelsEc(int l1, int l2, string what) {
            if (Ec == null) { Route = FanRoute.Mailbox; return false; }
            if (Ec.HoldFans(l1, l2, P.Curve.Ceiling)) { curLevel1 = l1; curLevel2 = l2; ecFailures = 0; LastError = ""; return true; }
            ecFailures++;
            LastError = Ec.LastError;
            Log.Write("FAIL " + what + " via EC: " + Ec.LastError);
            if (ecFailures >= 3) {
                Route = FanRoute.Mailbox;
                Log.Write("EC fan route abandoned after " + ecFailures + " failures; back to the mailbox");
                Fire(Toast, "The driver could not hold the fans (" + Ec.LastError + "); firmware curve in charge", true);
                Changed();
            }
            return false;
        }
        void ReleaseEcFans(string why) {
            if (Ec == null) return;
            if (Ec.ReleaseFans()) Log.Write("EC fans released on " + why + "; the controller resumes its own curve");
            else Log.Write("EC fans NOT released on " + why + ": " + Ec.LastError);
            curLevel1 = curLevel2 = -1;
        }

        // ---------- the driver ----------
        /// <summary>Open what the driver offers on this machine: the CPU's registers always, the EC only where
        /// the profile carries a map. Nothing here writes. Called at Init and again after an install.</summary>
        void InitDriver() {
            CloseDriver();
            DriverWhy = "";
            EcProof = "";
            DriverVersion = Hw.IsDemo ? null : PawnIo.InstalledVersion();
            if (Hw.IsDemo) {
                // A simulated board that asks for the driver simulates not having it, so the preview shows the
                // row its owner would see rather than the one this machine happens to be in. Nothing here needs
                // to know how the board was chosen; the profile already says whether it wants a driver.
                if (P.DriverFor != DriverFor.None) { DriverWhy = "not installed"; return; }
                Cpu = new DemoCpu();
                Ec = new EmbeddedController(new DemoEcPorts(), EcMap.Legacy());
                ecVerified = true;
                EcProof = "simulated";
                Route = ChooseRoute();
                return;
            }
            if (!S.DriverUse) { DriverWhy = "switched off"; return; }
            if (DriverVersion == null) { DriverWhy = "not installed"; return; }
            if (DriverOutdated) { DriverWhy = "PawnIO " + DriverVersion + " is older than " + PawnIo.MinVersion + "; update it"; return; }
            string why;
            bool deviceAbsent;
            Cpu = CpuRegisters.Open(out why, out deviceAbsent);
            if (Cpu == null) {
                string svc = PawnIo.ServiceState();
                DriverWhy = why ?? "unavailable";
                Log.Write("driver: CPU registers unavailable: " + DriverWhy + " · service " + svc);
                // "Restart to finish" is only true while the service is registered and not yet started. Once it is
                // running the restart has happened and something else is wrong; if it is not registered at all the
                // install never took. Either way the row must offer Troubleshoot, not another restart.
                if (S.DriverRestartPending && svc != "stopped" && svc != "starting") { S.DriverRestartPending = false; S.Save(); }
                if (!S.DriverRestartPending && svc != "running") DriverWhy += " · the PawnIO service is " + svc;
                // Only a device that cannot be opened means the driver itself is not there. A module the driver
                // rejected (a CPU it has no support for) says nothing about the EC, which is its own module and
                // on the boards that need it the whole reason the driver was installed.
                if (deviceAbsent) return;
            }
            else if (S.DriverRestartPending) { S.DriverRestartPending = false; S.Save(); }
            if (P.Ec != null) {
                PawnIoModule m = PawnIo.Open("LpcACPIEC", out why);
                if (m == null) Log.Write("driver: EC module unavailable: " + why);
                else {
                    // Nothing is written until the controller has recognised its own registers here, checked
                    // against two readings of this machine we already have: the firmware's fan speeds and the
                    // CPU's own temperature.
                    var ec = new EmbeddedController(new PawnIoEcPorts(m), P.Ec);
                    // Which register pair drives the fans is only knowable by driving them, which drivertest's fan
                    // test does in its own process. It leaves the answer in a file of its own rather than in the
                    // settings, which the running instance owns and would write over on exit.
                    try {
                        string pair = System.IO.File.Exists(EcPairPath) ? System.IO.File.ReadAllText(EcPairPath).Trim() : "";
                        if (pair == "percent") { P.Ec.UsePercent = true; Log.Write("driver: fan levels go to the percent pair (measured by the fan test)"); }
                        else if (pair == "rpm") P.Ec.UsePercent = false;
                    } catch { }
                    int[] rpm = null;
                    double die = double.NaN;
                    try { rpm = Hw.GetFanLevels(); } catch { }
                    try { if (Cpu != null) die = Cpu.Poll().DieTemp; } catch { }
                    ecVerified = ec.Verify(rpm, die, out EcProof);
                    Log.Write("driver: the EC map " + (ecVerified ? "fits this board: " : "does NOT fit this board: ") + EcProof);
                    Ec = ec;
                }
            }
            Route = ChooseRoute();
            Log.Write("driver: PawnIO " + DriverVersion + " · cpu=" + (Cpu != null ? Cpu.Describe : "unavailable") + " · ec=" + (Ec == null ? "no map for this board" : ecVerified ? P.Ec.Name : "map rejected") + " · fan route " + Route);
        }
        /// <summary>Where the fan test records which register pair moved the fans: "rpm" or "percent".</summary>
        public static string EcPairPath { get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Path), "ecpair.txt"); } }
        /// <summary>Set by the fan test in the report process: 1 = the rpm pair moved the fans, 2 = the percent pair.</summary>
        public int EcPairFound;
        void CloseDriver() {
            try { if (Cpu != null) Cpu.Dispose(); } catch { }
            try { if (Ec != null) Ec.Dispose(); } catch { }
            Cpu = null; Ec = null;
            ecVerified = false;
            Route = FanRoute.Mailbox;
        }
        /// <summary>Where fan levels should go: the EC only when this board's mailbox cannot take them, whether
        /// the profile said so or the firmware has just shown it, and only once the EC has proved it is the one
        /// the map describes.</summary>
        /// <summary>Would a fan level ever go through the EC on this board? False wherever the firmware's own
        /// channel works, and there nothing may write the EC's fan registers: the firmware reads them too, and
        /// two writers with no arbitration between them is the whole of the 1.1 stopped-fans bug.</summary>
        public bool EcFanRouteWanted { get { return (P.DriverFor & DriverFor.FanLevels) != 0 || fanLevelsRefused; } }

        FanRoute ChooseRoute() {
            bool need = EcFanRouteWanted;
            FanRoute was = Route;
            Route = (Ec != null && ecVerified && need && !Ec.Resting) ? FanRoute.Ec : FanRoute.Mailbox;
            // A route taking over starts clean: fanWriteFailures counts mailbox refusals, and three of them back
            // AutoTick off to once a minute, which on the one board this route rescues is permanent.
            if (Route == FanRoute.Ec && was != FanRoute.Ec) { fanWriteFailures = 0; fanFailureShown = false; ecFailures = 0; Log.Write("fan route: EC"); }
            return Route;
        }

        /// <summary>The line the Home page shows, or null when there is nothing to ask for: the driver is in and
        /// working, the owner closed the note on this version, or this board has nothing to gain from it.</summary>
        public string DriverNudge {
            get {
                if (DriverReady || !S.DriverUse || DriverBusy) return null;
                if (S.DriverNudgeDismissed == Program.Version) return null;
                if (S.DriverRestartPending) return "Restart Windows to finish installing the driver";
                bool fans = (P.DriverFor & DriverFor.FanLevels) != 0 || (fanLevelsRefused && P.Ec != null);
                return fans ? "Fan levels on this board need a driver" : null;
            }
        }
        public void DismissDriverNudge() { S.DriverNudgeDismissed = Program.Version; S.Save(); Changed(); }

        /// <summary>Download, verify and install the driver, then open it. Runs on the caller's thread and takes
        /// as long as the download takes; the row in Settings follows DriverProgress.</summary>
        public void InstallDriver() {
            if (DriverBusy) return;
            DriverBusy = true;
            DriverProgress = "Starting…";
            if (!S.DriverUse) { S.DriverUse = true; S.Save(); }      // asking for it is switching it on
            Changed();
            try {
                string error;
                DriverInstallResult r = PawnIo.Install(delegate(string step) { DriverProgress = step; Changed(); }, out error);
                switch (r) {
                    case DriverInstallResult.Installed:
                        S.DriverInstalledByOhman = true;
                        S.DriverRestartPending = false;
                        S.Save();
                        lock (applySync) {
                            FanRoute before = Route;
                            InitDriver();
                            if (Route != before) ApplyFanCore();
                        }
                        Say(DriverReady ? "Driver installed" : "Driver installed, but it could not be opened: " + DriverWhy);
                        break;
                    case DriverInstallResult.RestartNeeded:
                        S.DriverInstalledByOhman = true;
                        S.DriverRestartPending = true;
                        S.Save();
                        InitDriver();       // the registry key is there even though the device is not; the row reads that
                        DriverWhy = "installed · restart Windows to finish";
                        Say("Driver installed · restart Windows to finish");
                        break;
                    default:
                        DriverWhy = error ?? "install failed";
                        Fire(Toast, "Driver install failed: " + DriverWhy, true);
                        break;
                }
            } finally { DriverBusy = false; DriverProgress = ""; Changed(); }
        }

        /// <summary>Remove the driver again. Ours to offer only when we put it there; another app may still be
        /// using it, which the dialog says before this is called.</summary>
        public bool RemoveDriver() { return RemoveDriver(true); }
        /// <summary>reapply false: called from a factory reset that has just handed the fans back, which must stay handed back.</summary>
        public bool RemoveDriver(bool reapply) {
            if (DriverBusy) return false;
            DriverBusy = true;
            DriverProgress = "Removing…";
            Changed();
            try {
                lock (applySync) {
                    if (Route == FanRoute.Ec) ReleaseEcFans("driver removal");
                    CloseDriver();
                }
                string error;
                bool ok = PawnIo.Uninstall(out error);
                if (ok) { S.DriverInstalledByOhman = false; S.DriverRestartPending = false; S.Save(); }
                // Either way, re-read the machine. InitDriver refreshes the version the row reads, and skipping it
                // on the path that succeeded left the row offering to troubleshoot a removal that had worked.
                // Under the lock, like the other two callers: a failed removal re-opens the modules and may pick
                // the EC route, and the fan tick must not see that happen halfway through a write.
                lock (applySync) InitDriver();
                if (ok) Say("Driver removed");
                else { DriverWhy = error; Fire(Toast, "Could not remove the driver: " + error, true); }
                if (reapply) lock (applySync) ApplyFanCore();
                return ok;
            } finally { DriverBusy = false; DriverProgress = ""; Changed(); }
        }

        public void SetDriverUse(bool on) {
            S.DriverUse = on;
            S.Save();
            lock (applySync) {
                if (Route == FanRoute.Ec && !on) ReleaseEcFans("driver switched off");
                InitDriver();
                ApplyFanCore();
            }
            Changed();
        }
        bool fanFailureShown;
        FanMode fanBeforeMax { get { return (FanMode)S.FanBeforeMax; } set { S.FanBeforeMax = (int)value; } }

        /// <summary>Max-fan flag with the keep-alive trigger in front of it, the pair every fan path uses.</summary>
        void MaxFan(bool on, string what) { Try(delegate { Hw.GetFanCount(); Hw.SetMaxFan(on); }, what); }

        void ApplyFanCore() {
            if (GuardActive) { GuardFans(); return; }      // the guard owns the fans until it releases
            switch (S.Fan) {
                case FanMode.Max: MaxFan(true, "Max fan"); break;
                case FanMode.Manual: MaxFan(false, "Max fan off"); WriteLevels(S.Fan1, S.Fan2, "Fan level"); break;
                default: MaxFan(false, "Max fan off"); AutoTick(true); break;
            }
        }

        // A CPU temperature at idle swings several degrees every few seconds. Feeding that straight into the curve
        // makes the fans hunt audibly and writes the firmware every tick, so the curve follows a smoothed reading and
        // ignores a target that has not moved far enough to be worth hearing.
        double smoothCpu = double.NaN, smoothGpu = double.NaN;
        int cpuGone, gpuGone;
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
            var lv = S.Cur.CurveLevels;
            var gl = S.Cur.CurveLinked ? lv : S.Cur.GpuCurveLevels;
            // the floor slider raises the lowest level the curve may drive; the ramp is how many levels a 5 s tick may move (5 s per step = the vendor's 3)
            int floor = Math.Max(0, Math.Min(P.Curve.Ceiling, S.Cur.CurveFloor));
            int step = Math.Max(1, Math.Min(P.Curve.Ceiling, (int)Math.Round(P.Curve.StepPerTick * 5.0 / Math.Max(1, S.Cur.CurveRamp))));
            // UseChassis has to come across too. It is false on a board nobody has measured, and leaving it to
            // the field default meant Custom mode quietly went back to letting an uncalibrated chassis sensor
            // raise the fans -- on the one board whose owner reported that exact problem, in the one mode he uses.
            return new FanCurve { CpuTemps = CurveTemps, CpuLevels = lv, GpuTemps = CurveTemps, GpuLevels = gl, IrTemps = P.Curve.IrTemps, IrLevels = P.Curve.IrLevels,
                Floor = floor, Ceiling = P.Curve.Ceiling, StepPerTick = step, Fallback = Math.Max(floor, P.Curve.Fallback),
                UseChassis = P.Curve.UseChassis, Linked = S.Cur.CurveLinked };
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
            var lv = new int[levels.Length];
            for (int i = 0; i < lv.Length; i++) lv[i] = P.Curve.ClampOrOff(levels[i]);
            if (gpu) S.Cur.GpuCurveLevels = lv;
            else S.Cur.CurveLevels = lv;
            S.Save();
            if (S.Fan == FanMode.Custom && !GuardActive) lock (applySync) { AutoTick(true); lastFanWrite = DateTime.Now; }
            Changed();
        }
        public void SetCurveFloor(int level) {
            S.Cur.CurveFloor = level <= P.Curve.Floor ? 0 : P.Curve.Clamp(level);
            S.Save();
            if (S.Fan == FanMode.Custom && !GuardActive) lock (applySync) { AutoTick(true); lastFanWrite = DateTime.Now; }
            Changed();
        }
        public void SetCurveRamp(int seconds) { S.Cur.CurveRamp = Math.Max(1, Math.Min(10, seconds)); S.Save(); Changed(); }
        public void SetMaxBackWhenCool(bool on) { S.MaxBackWhenCool = on; if (!on) maxCoolSince = DateTime.MinValue; S.Save(); Changed(); }
        public void SetMaxStopAfter(int minutes) { S.MaxStopAfterMin = Math.Max(0, minutes); S.Save(); Changed(); }
        public void SetManualLinked(bool on) { S.ManualLinked = on; S.Save(); Changed(); }
        /// <summary>Start a custom curve from this model's own points (the "Edit as curve" link on the Auto page).</summary>
        public void SeedCurveFromVendor() {
            S.Cur.CurveLevels = VendorCurveAt(false);
            S.Cur.GpuCurveLevels = VendorCurveAt(true);
            S.Save();
            SetFan(FanMode.Custom, S.Fan1, S.Fan2, false);
        }
        public void SetCurveLinked(bool linked) {
            if (S.Cur.CurveLinked == linked) return;
            if (!linked) S.Cur.GpuCurveLevels = (int[])S.Cur.CurveLevels.Clone();     // unlinking starts the GPU curve where the shared one is
            S.Cur.CurveLinked = linked;
            S.Save();
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
            S.ModeIndex = index;
            S.Save();
            NoteFanMode(S.Fan);                                      // the new mode brings its own fan setting with it
            lock (applySync) {
                // the mode's own profile comes with it: fans, power gain and GPU power are remembered per mode
                if (Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode")) { if (announce) Say(ModeName + " mode"); }
                if (!GuardActive) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
                ApplyPowerCore();
                ApplyGpuCore();
                if (S.SyncWinPower) SetWinPowerOverlay(index);
            }
            Changed();
        }

        public void SetEcoCool(bool on) {
            S.EcoCool = on;
            S.Save();
            if (ModeIndex == 0) lock (applySync) Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode");
            Changed();
        }
        public void SetFan(FanMode mode, int f1, int f2, bool announce) {
            // Max fan is a detour, not a destination: remember what it interrupted so leaving it puts the fans
            // back under the curve the owner drew rather than handing them to the firmware's own.
            if (mode == FanMode.Max && S.Fan != FanMode.Max) { maxSince = DateTime.MinValue; fanBeforeMax = S.Fan; }   // saved with S below
            NoteFanMode(mode);
            S.Fan = mode;
            S.Fan1 = P.Curve.ClampOrOff(f1);
            S.Fan2 = P.Curve.ClampOrOff(f2);
            S.Save();
            if (GuardActive && mode != FanMode.Max) { Say("Thermal guard is holding max fan; " + Choice.Fan[Choice.Of(mode)] + " resumes when cool"); Changed(); return; }
            // On battery the mode command carries who drives the fans (FansByBios), and that follows the fan
            // mode: leaving Auto has to take control back before the first level is written.
            if (OnBattery) lock (applySync) Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode");
            lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; }
            if (announce) Say(mode == FanMode.Max ? "Max fan" : mode == FanMode.Manual ? "Fans " + Rpm(S.Fan1) + " / " + Rpm(S.Fan2) : mode == FanMode.Custom ? "Fans on your curve" : "Fans auto");
            Changed();
        }
        public void ToggleMaxFan() { SetFan(S.Fan == FanMode.Max ? fanBeforeMax : FanMode.Max, S.Fan1, S.Fan2, false); }

        public void SetTdpOffset(int off, bool announce) {
            S.TdpOffset = Math.Max(0, Math.Min(MaxOffset, off));
            S.Save();
            lock (applySync) ApplyPowerCore();
            if (announce) Say("Power gain +" + S.TdpOffset + " W · " + CurrentTdp + " W budget");
            Changed();
        }

        public void SetGpu(GpuLevel lvl, bool auto, bool announce) {
            S.Gpu = lvl;
            S.GpuAuto = auto;
            S.Save();
            lock (applySync) ApplyGpuCore();
            if (announce) Say("GPU " + (auto ? "auto" : lvl.ToString()));
            Changed();
        }

        public void SetKey(KeyAction a) { S.Key = a; S.Save(); Changed(); }
        public void SetKeyCommand(string cmd) { S.KeyCommand = (cmd ?? "").Trim(); S.Save(); Changed(); }
        public void SetHotkeys(bool on) { S.Hotkeys = on; S.Save(); Changed(); }

        // ---------- which keys ----------
        public Hotkey GetHotkey(HotkeyAction a) {
            string t = S.HotkeyText[(int)a];
            if (t == null) return HotkeyTable.Defaults[(int)a];
            Hotkey h;
            return Hotkey.TryParse(t, out h) ? h : HotkeyTable.Defaults[(int)a];
        }
        public Hotkey[] GetHotkeys() { var r = new Hotkey[HotkeyTable.Count]; for (int i = 0; i < r.Length; i++) r[i] = GetHotkey((HotkeyAction)i); return r; }
        public bool HotkeysCustomised { get { foreach (string t in S.HotkeyText) if (t != null) return true; return false; } }
        /// <summary>Bind one action. A key already bound to another action is taken from it, since both are on
        /// screen at the time and a shortcut cannot do two things. Back to default is stored as default.</summary>
        public void SetHotkey(HotkeyAction a, Hotkey h) {
            for (int i = 0; i < HotkeyTable.Count; i++)
                if (i != (int)a && !h.IsEmpty && GetHotkey((HotkeyAction)i).Same(h)) S.HotkeyText[i] = HotkeyTable.Defaults[i].Same(Hotkey.None) ? null : "";
            S.HotkeyText[(int)a] = h.Same(HotkeyTable.Defaults[(int)a]) ? null : h.ToString();
            S.Save();
            Changed();
        }
        public void ResetHotkeys() { for (int i = 0; i < HotkeyTable.Count; i++) S.HotkeyText[i] = null; S.Save(); Changed(); }
        /// <summary>Put every binding back as it was, for a rebind Windows refused: SetHotkey may have taken the
        /// key from another action on the way, and that one has to come back too.</summary>
        public string[] SnapshotHotkeys() { return (string[])S.HotkeyText.Clone(); }
        public void RestoreHotkeys(string[] snapshot) { Array.Copy(snapshot, S.HotkeyText, HotkeyTable.Count); S.Save(); Changed(); }
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
                    if (GuardActive) GuardFans();
                    Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode");   // fans are handled by FanTick; this only pins mode and power
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
        public double CpuTemp = double.NaN;               // set by the UI sensor loop; the median of three readings
        public double CpuTempNow = double.NaN;            // the single reading behind it, for the report when somebody says it jumps
        public int GuardChassis = -1;
        DateTime guardSafeSince = DateTime.MinValue;
        int guardHotTicks;                 // consecutive ten-second ticks that have judged the machine hot
        int guardWarmTicks;
        int guardIgnoredTicks;             // and, once engaged, consecutive ticks where max fan plainly did not arrive                // and, once engaged, consecutive ticks that are not yet cool
        bool chassisScaleKnown;            // the 0x23 sensor has read below the release threshold at least once, so it is on the scale the profile assumes
        System.Threading.Timer guard;

        /// <summary>Turning the guard off releases it at once; turning it on lets the next tick judge the machine.</summary>
        // ---------- the guard's limits: the owner's where set, the profile's otherwise ----------
        public int GuardCpuHot { get { return S.GuardCpu > 0 ? S.GuardCpu : P.Guard.CpuHot; } }
        public int GuardChassisHot { get { return S.GuardChassis > 0 ? S.GuardChassis : P.Guard.ChassisHot; } }
        // Release sits the same distance under the limit that the profile's does, so moving a limit moves both.
        public int GuardCpuSafe { get { return GuardCpuHot - (P.Guard.CpuHot - P.Guard.CpuSafe); } }
        public int GuardChassisSafe { get { return GuardChassisHot - (P.Guard.ChassisHot - P.Guard.ChassisSafe); } }
        public int GuardHoldSeconds { get { return S.GuardHold > 0 ? S.GuardHold : P.Guard.SafeSeconds; } }
        public int GuardLevel { get { return S.GuardLevel; } }
        public void SetGuardLimits(int cpu, int chassis, int level, int hold) {
            S.GuardCpu = cpu == P.Guard.CpuHot ? 0 : cpu;
            S.GuardChassis = chassis == P.Guard.ChassisHot ? 0 : chassis;
            S.GuardLevel = Math.Max(0, level);
            S.GuardHold = hold == P.Guard.SafeSeconds ? 0 : hold;
            S.Save();
            if (GuardActive) lock (applySync) GuardFans();
            Changed();
        }
        bool guardStalled;
        /// <summary>What the guard holds the fans at. A stalled fan gets max whatever the setting says: a level
        /// the fan is not taking is not a level, and max is the one command with its own path.</summary>
        void GuardFans() {
            // Max when: no level is set; the fans are not answering; this board cannot take a level at all
            // (turning max off and then failing to write one is less cooling than before the guard fired); or
            // the owner is already on Max, which the guard must never undercut.
            // FansByBios: on battery in Auto the firmware owns the levels and drops ours, so only max reaches the fans.
            if (GuardLevel <= 0 || guardStalled || !CanSetFanLevels || FansByBios || S.Fan == FanMode.Max) { MaxFan(true, "Guard max fan"); return; }
            // Never below what the fans are already doing: a guard that slows them at the hottest moment is not one.
            int level = Math.Max(GuardLevel, Math.Max(curLevel1, curLevel2));
            MaxFan(false, "Guard level");
            WriteLevels(level, level, "Guard level");
        }
        public void SetGuard(bool on) {
            S.Guard = on;
            S.Save();
            if (!on && GuardActive) { GuardActive = false; guardSafeSince = DateTime.MinValue; guardStalled = false; guardIgnoredTicks = guardHotTicks = guardWarmTicks = 0; curLevel1 = curLevel2 = -1; lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; } Log.Write("thermal guard switched off while engaged; fans back to " + S.Fan); }
            Changed();
        }
        void GuardTick() {
            if (!BiosOk || Hw.IsDemo || ReadOnly) return;      // read-only boards: nothing to force, the firmware's own limits apply
            if (!S.Guard) {
                if (GuardActive) { GuardActive = false; guardSafeSince = DateTime.MinValue; guardStalled = false; guardIgnoredTicks = guardHotTicks = guardWarmTicks = 0; curLevel1 = curLevel2 = -1; lock (applySync) { ApplyFanCore(); lastFanWrite = DateTime.Now; } Changed(); }
                // Still read the fans. This is the only tick that runs with the window closed, and somebody
                // running Ohman in the tray with the guard switched off has to be able to learn a ceiling too.
                try { int[] idle; lock (applySync) idle = Hw.GetFanLevels(); NoteFanLevels(idle); } catch { }
                return;
            }
            try {
                int[] f;
                int c;
                lock (applySync) { f = Hw.GetFanLevels(); c = Hw.GetTemperature(); }
                NoteFanLevels(f);
                GuardChassis = c;
                double t = CpuTemp;
                bool cpuKnown = !double.IsNaN(t);
                // On a board nobody has verified, the chassis sensor may not mean what the profile's thresholds assume.
                // Wait until it has read cool once; until then the CPU and the stall test carry the guard on their own.
                if (c >= 0 && c < GuardChassisSafe) chassisScaleKnown = true;
                bool chassisUsable = P.Verified || chassisScaleKnown;
                bool hot = (cpuKnown && t >= GuardCpuHot) || (chassisUsable && c >= GuardChassisHot);
                bool stalled = cpuKnown && t >= P.Guard.StallCpu && f[0] >= 0 && f[1] >= 0 && (f[0] + f[1]) < P.Guard.StallLevelSum;
                // Two ticks, not one: a single sample landing inside a spike used to force maximum fan on an idle
                // laptop, three times in one evening. Sensors hands over a median now, so this is the second
                // layer rather than the only one. Twenty seconds costs nothing, since the chips throttle
                // themselves long before they come to harm and the chassis this guards does not heat up in ten.
                if (hot || stalled) guardHotTicks++; else guardHotTicks = 0;
                if ((hot || stalled) && !GuardActive && guardHotTicks >= 2) {
                    GuardActive = true;
                    guardStalled = stalled;
                    guardSafeSince = DateTime.MinValue;
                    Log.Write("THERMAL GUARD engaged: cpu=" + (cpuKnown ? t.ToString("0") : "?") + " ambient=" + c + " fans=" + f[0] + "/" + f[1] + (stalled ? " (stalled)" : ""));
                    // "ambient", matching the Home page. Same 0x23 index 1 either way; HP's own device library calls it
                    // Ambient and Ohman called it chassis for a year, so the two lines disagreed on screen.
                    Fire(Toast, "Thermal guard: fans to max (CPU " + (cpuKnown ? t.ToString("0") + "°" : "?") + ", ambient " + c + "°)", true);
                    lock (applySync) GuardFans();
                    Changed();
                } else if (GuardActive) {
                    // Max fan has been commanded on every tick since this engaged, so the firmware's own level
                    // readback should be nowhere near zero. When it is, the command is not arriving, and a guard
                    // that keeps reporting "fans to max" while the machine sits at 98 C is worse than no guard.
                    // 1.1 did exactly that for minutes. It cannot fix this from here, but it must not be quiet.
                    if (f[0] >= 0 && f[1] >= 0 && (f[0] + f[1]) < P.Guard.StallLevelSum) guardIgnoredTicks++; else guardIgnoredTicks = 0;
                    if (guardIgnoredTicks == 3) {
                        Log.Write("THERMAL GUARD is being ignored: commanded every tick and the firmware still reports "
                            + f[0] + "/" + f[1] + ". Fan commands are not reaching the fans.");
                        Fire(Toast, "The fans are not answering the thermal guard. Save your work and restart the machine.", true);
                        // A level the fans are not taking is not a level. Max has its own command path; try it.
                        guardStalled = true;
                        lock (applySync) GuardFans();
                    }
                    bool safe = (!cpuKnown || t < GuardCpuSafe) && (!chassisUsable || c < GuardChassisSafe);
                    // One warm sample no longer restarts the minute. A sensor sitting a degree under its own
                    // threshold crosses it now and then, and requiring sixty unbroken seconds meant the guard
                    // could hold maximum fan on a machine that had already cooled, indefinitely.
                    if (safe) guardWarmTicks = 0; else guardWarmTicks++;
                    if (guardWarmTicks >= 2) { guardSafeSince = DateTime.MinValue; lock (applySync) GuardFans(); }
                    else if (!safe) { lock (applySync) GuardFans(); }
                    else if (guardSafeSince == DateTime.MinValue) guardSafeSince = DateTime.Now;
                    else if ((DateTime.Now - guardSafeSince).TotalSeconds >= GuardHoldSeconds) {
                        GuardActive = false;
                        guardStalled = false;
                        guardIgnoredTicks = 0;
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

        /// <summary>The third byte of the mode command, "fan control by BIOS". OGH sends 1 on battery, and so did
        /// Ohman, copied from it. But OGH has no software curve to lose and Ohman does: with the byte set the
        /// firmware ignores every level written, and on a 2025 Transcend 14 its own curve keeps the fans off
        /// until 74 C, which one owner reported as "the fans only work plugged in". So the hand-off happens only
        /// in Auto, where the quiet firmware curve is a fair reading of what the owner asked for; Manual, Curve
        /// and Max are explicit choices and stay Ohman's on either power source.</summary>
        bool FansByBios { get { return OnBattery && S.Fan == FanMode.Auto; } }
        public void OnPowerSource(bool onBattery) {
            bool changed = OnBattery != onBattery;
            OnBattery = onBattery;
            ApplyRefreshRate(onBattery);
            if (S.EcoOnBattery) {
                // forced Eco is temporary: the file keeps the user's own mode (SavedModeOverride) and it comes back when plugged in
                if (onBattery && !ecoForcedByBattery && ModeIndex != 0) { ecoForcedByBattery = true; modeBeforeBattery = ModeIndex; S.SavedModeOverride = modeBeforeBattery; Say("On battery → Eco"); SetModeCore(0, false); return; }
                if (!onBattery && ecoForcedByBattery) { ecoForcedByBattery = false; S.SavedModeOverride = -1; SetModeCore(modeBeforeBattery, false); Say("Plugged in → " + ModeName); return; }
            }
            if (changed) lock (applySync) Try(delegate { Hw.SetMode(ModeByte, FansByBios); }, "Set mode");   // re-send with the DC flag like OGH
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
                string csv = "";
                var outLines = new System.Text.StringBuilder();
                using (var q = new Process()) {
                    q.StartInfo = psi;
                    q.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outLines) outLines.Append(e.Data).Append("\n"); };
                    q.Start();
                    q.BeginOutputReadLine();
                    if (q.WaitForExit(5000)) { q.WaitForExit(); csv = outLines.ToString(); }
                    else { try { q.Kill(); } catch { } q.WaitForExit(2000); if (!q.HasExited) Log.Write("could not stop schtasks query"); Log.Write("schtasks query timed out"); }
                }
                int n = 0;
                foreach (string line in csv.Split('\n')) {
                    if (!line.StartsWith("\"\\OmenInstallMonitor", StringComparison.OrdinalIgnoreCase)) continue;
                    string task = line.Split('"')[1];
                    using (var c = Process.Start(new ProcessStartInfo("schtasks.exe", "/Change /TN \"" + task + "\" " + (disable ? "/DISABLE" : "/ENABLE")) { CreateNoWindow = true, UseShellExecute = false })) {
                        if (c.WaitForExit(5000)) { if (c.ExitCode == 0) n++; else Log.Write("schtasks " + task + " rc=" + c.ExitCode); }
                        else { try { c.Kill(); } catch { } c.WaitForExit(2000); if (!c.HasExited) Log.Write("could not stop " + task); Log.Write("schtasks " + task + " timed out"); }
                    }
                }
                Log.Write((disable ? "disabled " : "re-enabled ") + n + " OmenInstallMonitor task(s)");
            } catch (Exception ex) { Log.Write("OGH tasks: " + ex.Message); }
        }

        public void SetOghSuppression(bool on) {
            S.SuppressOgh = on;
            S.Save();
            if (Hw.IsDemo) { Say("Simulated hardware: OMEN Gaming Hub is left alone"); Changed(); return; }
            if (ReadOnly) { Say("Unsupported board: OMEN Gaming Hub keeps the key"); Changed(); return; }
            if (on) { KillOgh(); SetOghTasks(true); Say("The OMEN key now belongs to " + Program.DisplayName); }
            else { SetOghTasks(false); Say("OMEN Gaming Hub's launcher restored at next logon"); }
        }

        // ---------- OMEN key ----------
        void StartKeyWatcher() {
            try {
                var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"), new WqlEventQuery("SELECT * FROM hpqBEvnt"));
                w.EventArrived += OnBiosEvent;
                w.Start();
                watcher = w;
                Log.Write("hpqBEvnt watcher started");
            } catch (Exception ex) { Log.Write("hpqBEvnt watcher failed: " + ex.Message); }
        }

        void OnBiosEvent(object s, EventArrivedEventArgs e) {
            uint id = 0, data = 0;
            try { id = Convert.ToUInt32(e.NewEvent["EventID"]); data = Convert.ToUInt32(e.NewEvent["EventData"]); } catch { return; }
            LastEventId = id;
            LastEventData = data;
            LastEventTime = DateTime.Now;
            Log.Write("hpqBEvnt id=" + id + " data=" + data);
            var any = AnyKeyEvent; if (any != null) { try { any(id, data); } catch { } }
            if (Learning) {
                if (id == 131073) return;               // power/AC notification, not a key
                Learning = false;
                S.KeyId = id;
                S.KeyData = data;
                S.Save();
                Say("OMEN key bound to event " + id + "/" + data);
                Changed();
                return;
            }
            if (id == 13 && Light != null && S.Light != 2) {      // Fn backlight key: the firmware already toggled, mirror it (OGH: EventID 13)
                S.Light = data == 0 ? 0 : 1;
                S.Save();
                Changed();
                return;
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
                int[] rates = Display.Rates();
                if (rates.Length < 2) return;
                int want = S.LowHzOnBattery && onBattery ? Display.BatteryHz()
                         : S.RefreshHz > 0 ? S.RefreshHz
                         : S.LowHzOnBattery ? Display.HighestHz()   // we lowered it, so we put it back
                         : 0;
                if (want > 0 && Display.CurrentHz() != want) Display.SetHz(want);
            } catch (Exception ex) { Log.Write("refresh rate: " + ex.Message); }
        }
        public void SetRefreshRate(int hz) {
            S.RefreshHz = hz;
            S.Save();
            if (Display.SetHz(hz)) Say(hz + " Hz");
            else Fire(Toast, "Could not switch to " + hz + " Hz", true);
            Changed();
        }

        // ---------- keyboard lighting ----------
        void InitLight() {
            try {
                Light = Hw.IsDemo ? (ILighting)new DemoLighting() : (BiosOk ? BiosLighting.Detect() : null);   // not !ReadOnly: see TryLight
                // A per-key board answers the firmware calls and lights nothing by them. Its colours live on the
                // keyboard's own HID lighting interface, so look for that before giving up on it.
                if (!Hw.IsDemo && Light != null && Light.Inert) {
                    var la = LampArray.FindKeyboard();
                    if (la != null) Light = new PerKeyLighting(la);
                    else Log.Write("per-key board with no HID lighting interface we can drive; colours left to Windows");
                }
                if (Light == null) return;
                // One-time repair. Before this build a per-key keyboard was initialised to white for every lamp,
                // and InitLight then saved that array as the owner's own colours. It is the right length, so it
                // parses cleanly and would be painted straight back. Drop it once and let the new default stand.
                if (Light.Kind == LightKind.PerKey && !S.PerKeyReset) {
                    S.LightColors = "";
                    S.PerKeyReset = true;
                    S.Save();
                    Log.Write("cleared the saved per-key colours once; they were the old all-white default");
                }
                var fw = Light.GetColors();
                LightColors = ParseColors(S.LightColors, Light.Zones);
                if (LightColors == null) { LightColors = fw; S.LightColors = JoinColors(fw); }    // first run: keep what the keyboard shows now
                if (S.Light < 0) S.Light = (WinLighting.Present && WinLighting.HasControl) ? 2 : ((Light.GetBacklight() & BiosLighting.ON_FLAG) != 0 ? 1 : 0);
                Log.Write("lighting: " + Light.Describe + ", mode " + S.Light + ", effect " + S.LightEffect + ", level " + S.LightLevel + (WinLighting.Present ? ", Windows Dynamic Lighting present" + (WinLighting.HasControl ? " (in control)" : "") : ""));
            } catch (Exception ex) { Log.Write("lighting init: " + ex.Message); Light = null; }
        }
        static Rgb[] ParseColors(string s, int n) {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            var r = new Rgb[n];
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
            // Record that we took it. Plenty of people switch Dynamic Lighting off themselves because it fights
            // vendor software, and handing it back on exit to someone who never had it on would be us turning a
            // Windows feature on behind their back.
            if (WinLighting.Present && WinLighting.HasControl) { S.TookWinLighting = true; S.Save(); WinLighting.SetControl(false); }   // take the keyboard first or Windows overwrites us
            TryLight(delegate {
                if (S.Light == 1) Light.SetColors(Scaled(LightColors));
                Light.SetBacklight(S.Light == 1, 100);                                              // the level byte OGH writes; brightness is in the colours
            }, "Keyboard lighting");
            if (S.Light == 1 && S.LightEffect != 0) StartEffect();
        }
        public void SetLight(int mode, int effect, bool announce) {
            S.Light = Math.Max(0, Math.Min(2, mode));
            S.LightEffect = Math.Max(0, Math.Min(3, effect));
            S.Save();
            lock (applySync) ApplyLightCore();
            if (announce) Say(S.Light == 2 ? "Keyboard: Windows Dynamic Lighting" : S.Light == 0 ? "Keyboard off" : new[] { "Keyboard static", "Keyboard breathe", "Keyboard cycle", "Keyboard wave" }[S.LightEffect]);
            Changed();
        }
        /// <summary>zones = null: every zone.</summary>
        public void SetLightColor(int[] zones, Rgb c) {
            if (Light == null) return;
            for (int i = 0; i < LightColors.Length; i++) if (zones == null || Array.IndexOf(zones, i) >= 0) LightColors[i] = c;
            S.LightColors = JoinColors(LightColors);
            if (S.Light != 1) S.Light = 1;
            S.Save();
            lock (applySync) ApplyLightCore();
            Changed();
        }
        public static double SpeedFactor(int speed) { return new[] { 0.35, 0.6, 1.0, 1.6, 2.4 }[Math.Max(0, Math.Min(4, speed - 1))]; }
        public void SetLightSpeed(int speed) { S.LightSpeed = Math.Max(1, Math.Min(5, speed)); S.Save(); Changed(); }
        public void SetLightLevel(int level) {
            S.LightLevel = Math.Max(0, Math.Min(100, level));
            S.Save();
            lock (applySync) { if (S.Light == 1 && Light != null && S.LightEffect == 0) TryLight(delegate { Light.SetColors(Scaled(LightColors)); }, "Keyboard brightness"); }
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
        System.Threading.Timer fx;
        double fxPhase;
        int fxFailures;
        void StartEffect() { fxFailures = 0; if (fx == null) fx = new System.Threading.Timer(delegate { EffectTick(); }, null, 120, 120); else fx.Change(120, 120); }
        void StopEffect() { if (fx != null) fx.Change(Timeout.Infinite, Timeout.Infinite); }
        void EffectTick() {
            if (Light == null || S.Light != 1 || S.LightEffect == 0) return;
            if (!Monitor.TryEnter(applySync, 50)) return;
            try {
                fxPhase += 0.12 * SpeedFactor(S.LightSpeed);
                var frame = new Rgb[LightColors.Length];
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
            sb.AppendLine("fan drive: written " + curLevel1 + "/" + curLevel2 + "  cpu " + Fmt(CpuTemp) + " (last single reading " + Fmt(CpuTempNow) + ")  gpu " + Fmt(GpuTemp) + "  ir " + Fmt(IrTemp) + "  guard=" + GuardActive + "  writeFailures=" + fanWriteFailures + "  route=" + Route);
            sb.AppendLine("driver: " + (DriverReady ? "PawnIO " + (Hw.IsDemo ? "simulated" : "" + DriverVersion) + " · cpu " + (Cpu != null ? Cpu.Describe : "none")
                + " · ec " + (Ec == null ? "none" : (ecVerified ? Ec.Map.Name : "map rejected") + (Ec.Resting ? " (resting)" : "") + " u{00B7} " + EcProof) : "none (" + DriverWhy + ")"));
            sb.AppendLine("last heartbeat: " + (LastHeartbeat == DateTime.MinValue ? "never" : LastHeartbeat.ToString("HH:mm:ss")) + "   last key event: " + (LastEventTime == DateTime.MinValue ? "none" : LastEventId + "/" + LastEventData + " at " + LastEventTime.ToString("HH:mm:ss")));
            return sb.ToString();
        }
    }
}

