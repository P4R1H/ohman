// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: hardware layer.
// HP OMEN BIOS control through the WMI class root\wmi:hpqBIntM (requires elevation).
// Every opcode below was verified against three independent sources on 2026-09-08:
//   * OMEN Gaming Hub's own background log on this machine (payload bytes it sends),
//   * the Linux kernel driver drivers/platform/x86/hp/hp-wmi.c (enum hp_wmi_gm_commandtype),
//   * OmenMon (Hardware/BiosCtl.cs).
// Target language level: C# 5 (in-box csc.exe of .NET Framework 4.8).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;

namespace Ohman {

    public static class Log {
        static readonly object Sync = new object();
        static bool off;                        // set by Delete: the reset is leaving, nothing more should be written
        /// <summary>Remove the log and its rotated copy. The reset dialog promises the log goes with the settings,
        /// and it is the only caller. Writing is switched off first so the teardown after it cannot recreate the
        /// file we just deleted.</summary>
        public static void Delete() {
            lock (Sync) {
                off = true;
                try { if (File.Exists(Path)) File.Delete(Path); } catch { }
                try { if (File.Exists(Path + ".1")) File.Delete(Path + ".1"); } catch { }
            }
        }
        public static string Path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.FileStem + ".log");
        public static void Write(string s) {
            try {
                lock (Sync) {
                    if (off) return;
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > 1024 * 1024) {
                        string old = Path + ".1";
                        try { if (File.Exists(old)) File.Delete(old); fi.MoveTo(old); } catch { }
                    }
                    File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + s + Environment.NewLine);
                }
            } catch { }
        }
    }

    public sealed class SystemInfo {
        public bool Valid;
        public int ThermalPolicy;        // byte 3: 0 = legacy (modes 0x00/0x01/0x02), 1 = current (0x30/0x31/0x50)
        public bool SwFanControl;        // byte 4 bit 0
        public int DefaultPl4;           // byte 5 (W)
        public int DefaultConcurrentTdp; // byte 8 (W), base for the "+15 W" slider
        public int GpuModes;             // byte 7: bitmask of graphics modes the firmware offers (1 iGPU-only, 2 Hybrid, 4 Discrete, 8 Advanced Optimus)
        public byte[] Raw = new byte[0];
        public string Hex { get { return Bios.Hex(Raw, 12); } }
    }

    public sealed class GpuPowerState {
        public bool CustomTgp, Ppab;
        public int DState, PeakTemp;
        public override string ToString() { return "cTGP=" + (CustomTgp ? 1 : 0) + " PPAB=" + (Ppab ? 1 : 0) + " D" + DState + " peak=" + PeakTemp; }
    }

    public interface IHardware {
        bool IsDemo { get; }
        /// <summary>0x10. WARNING: this is also the firmware's "user-defined state" keep-alive trigger (hp-wmi.c). Only call it
        /// when you intend to hold a manual/max fan state; calling it while fans are meant to be automatic freezes them.</summary>
        int GetFanCount();
        /// <summary>Fan count from the fan table (0x2F), a plain read with no side effects.</summary>
        int GetFanCountPassive();
        int GetFanTableMax();                       // highest level in the firmware's own fan table (0x2F); -1 when unavailable
        int[] GetFanLevels();                       // {fan1, fan2} in units of 100 RPM (0..57 on this machine)
        int GetTemperature();                       // BIOS thermal sensor (0x23), degrees C
        bool GetMaxFan();
        GpuPowerState GetGpuPower();
        SystemInfo GetSystemInfo();
        void SetMode(byte mode, bool fanControlByBios);   // OGH sets fanControlByBios=1 on battery (BiosAutoFanControlInDc)
        void SetMaxFan(bool on);
        void SetFanLevels(int fan1, int fan2);
        void SetConcurrentTdp(int watts);
        /// <summary>0x29 with the PL bytes filled: CPU PL1/PL2 through the firmware, no driver. Byte order measured on
        /// 8E41 (2026-09-25): byte 0 = PL2, byte 1 = PL1 (sent 32 3C, 0x610 read PL1 60 / PL2 50; 50 41 gave 65/80).</summary>
        void SetCpuPowerLimits(int pl1, int pl2);
        void SetGpuPower(bool customTgp, bool ppab, int peakTemp);
        /// <summary>Graphics mode: 0 Hybrid, 1 Discrete, 2 Optimus, 3 iGPU only. Legacy mailbox (command 1 read / 2 write, type 0x52).</summary>
        int GetGpuMode();
        void SetGpuMode(int mode);                  // takes effect after a restart on every generation before HP's "26C1" cycle
    }

    /// <summary>Real BIOS access via WMI. Thread-safe (one call at a time).</summary>
    public sealed class Bios : IHardware {
        public const uint CMD_DEFAULT = 0x20008;                              // HPWMI_GM / OmenMon Cmd.Default
        static readonly byte[] SIGN = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
        static readonly object Sync = new object();

        // CommandType opcodes (all under CMD_DEFAULT)
        public const uint OP_FAN_COUNT     = 0x10; // in {0,0,0,0} out4  -> [0]=count. Also the firmware keep-alive trigger.
        public const uint OP_SET_MODE      = 0x1A; // in {0xFF, mode, 0, 0}
        public const uint OP_GPU_POWER_GET = 0x21; // out4 {cTGP, PPAB, DState, PeakTemp}
        public const uint OP_GPU_POWER_SET = 0x22; // in  {cTGP, PPAB, DState, PeakTemp}
        // 0x23 takes a sensor index: 0 IR, 1 Ambient, 2 PCH, 3 VR (named in OGH's own device library).
        // Ohman drives from 1 and called it "chassis" for a year; it is the ambient sensor.
        public const uint OP_TEMP          = 0x23; // in {index,0,0,0} out4 -> [0]=degC
        public const uint OP_MAX_FAN_GET   = 0x26; // out4 -> [0]=1 when max
        public const uint OP_MAX_FAN_SET   = 0x27; // in {1|0}
        public const uint OP_SYSTEM_DATA   = 0x28; // out128
        public const uint OP_CPU_POWER_SET = 0x29; // in {PL2, PL1, PL4?, LimitWithGpu}, 0xFF = leave unchanged (PL2 first: measured on 8E41)
        public const uint OP_FAN_LEVEL_GET = 0x2D; // out128 -> [0]=fan1 [1]=fan2 (x100 RPM)
        public const uint OP_FAN_LEVEL_SET = 0x2E; // in {fan1, fan2, 0...}
        public const uint OP_FAN_TABLE_GET = 0x2F; // out128 -> [0]=fan count, [1]=entries, then {fan1, fan2, noise dB} triplets
        public const uint OP_FAN_TYPE = 0x2C;      // out128 -> [0] one nibble per fan; see GetFanCountPassive

        // Thermal-policy v1 mode bytes (this machine reports policy v1 in system data byte 3).
        // OGH's own SetFanMode maps: Default->0x30, Performance->0x31, Cool->0x50, Eco->0x30 (Eco is software-side: Windows power mode + GPU/TDP).

        public bool IsDemo { get { return false; } }

        public static string Hex(byte[] b, int max) {
            if (b == null) return "(null)";
            var sb = new StringBuilder();
            int n = Math.Min(b.Length, max);
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
            if (b.Length > n) sb.Append(" ..");
            return sb.ToString();
        }

        static ManagementObject cached;                       // the hpqBIntM instance; found once, dropped again on any WMI error
        static ManagementObject Interface() {
            if (cached != null) return cached;
            using (var s = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM hpqBIntM"))
                foreach (ManagementObject mo in s.Get()) { cached = mo; return mo; }
            throw new InvalidOperationException("hpqBIntM WMI class has no instance (not an HP OMEN, or not elevated)");
        }

        /// <summary>Raw call. outSize must be 0, 4, 128, 1024 or 4096. Throws on rwReturnCode != 0.</summary>
        public static byte[] Call(uint commandType, byte[] data, int outSize) {
            return Call(CMD_DEFAULT, commandType, data, outSize);
        }
        public static byte[] Call(uint command, uint commandType, byte[] data, int outSize) {
            if (data == null) data = new byte[0];
            lock (Sync) {
                try { return CallLocked(command, commandType, data, outSize); }
                catch (ManagementException) { try { if (cached != null) cached.Dispose(); } catch { } cached = null; throw; }
            }
        }
        static byte[] CallLocked(uint command, uint commandType, byte[] data, int outSize) {
            {
                ManagementObject intf = Interface();
                using (var cls = new ManagementClass("root\\wmi", "hpqBDataIn", null))
                using (ManagementBaseObject din = cls.CreateInstance()) {
                    din["Sign"] = SIGN;
                    din["Command"] = command;
                    din["CommandType"] = commandType;
                    din["Size"] = (uint)data.Length;
                    din["hpqBData"] = data;
                    string method = "hpqBIOSInt" + outSize.ToString(CultureInfo.InvariantCulture);
                    using (ManagementBaseObject inP = intf.GetMethodParameters(method)) {
                        inP["InData"] = din;
                        using (ManagementBaseObject outP = intf.InvokeMethod(method, inP, null))
                        using (var outD = (ManagementBaseObject)outP["OutData"]) {
                            uint rc = Convert.ToUInt32(outD["rwReturnCode"], CultureInfo.InvariantCulture);
                            if (rc != 0) throw new BiosException(commandType, rc);
                            if (outSize == 0) return new byte[0];
                            byte[] o = outD["Data"] as byte[];
                            return o ?? new byte[0];
                        }
                    }
                }
            }
        }

        static readonly byte[] Z4 = new byte[] { 0, 0, 0, 0 };

        public int GetFanCount() { var d = Call(OP_FAN_COUNT, Z4, 4); return d.Length > 0 ? d[0] : -1; }
        /// <summary>How many fans this machine has. 0x2C answers with one nibble per fan (OmenMon's FanType:
        /// 0 none, 1 CPU, 2 GPU, 3 exhaust, 4 pump, 5 intake), which is the reliable source -- byte 0 of the fan
        /// table says 1 on a two-fan Victus 16-d1xxx. Falls back to the fan table when 0x2C will not answer.</summary>
        public int GetFanCountPassive() {
            int n = 0;
            try {
                var t = Call(OP_FAN_TYPE, Z4, 128);
                if (t.Length > 0) {
                    // Two nibbles in a byte, and only 1..5 are fan types. Firmware answering 0xFF instead of
                    // refusing would otherwise read as two fans and skip the fan table that knows better.
                    for (int i = 0; i < 2; i++) { int nib = (t[0] >> (i * 4)) & 0xF; if (nib >= 1 && nib <= 5) n++; }
                }
            } catch { }
            bool asked = false;
            if (n == 0) { try { var d = Call(OP_FAN_TABLE_GET, Z4, 128); if (d.Length > 0) { n = d[0]; asked = true; } } catch { } }
            // A Victus 16-d1xxx (board 8A26) declares one fan in 0x2C and one in the fan table, then reports two
            // live speeds. It has two fans, so the declaration is the part that is wrong. Believe the speeds, but
            // only ever upwards: 0x2D reads 0 for a fan that is stopped, so a pair of zeroes proves nothing.
            if (n == 1) {
                try {
                    var lv = Call(OP_FAN_LEVEL_GET, Z4, 128);
                    if (lv.Length > 1 && lv[0] > 0 && lv[1] > 0) n = 2;
                } catch { }
            }
            // asked distinguishes a firmware that answered zero from one that would not answer at all, which is
            // what this returned before the two-fan check was added in front of it.
            return n > 0 ? n : (asked ? 0 : -1);
        }
        public int GetFanTableMax() {
            var d = Call(OP_FAN_TABLE_GET, Z4, 128);
            if (d.Length < 2) return -1;
            int n = Math.Min((int)d[1], 40), top = -1;
            for (int i = 0; i < n; i++) { int o = 2 + 3 * i; if (o + 1 >= d.Length) break; top = Math.Max(top, Math.Max(d[o], d[o + 1])); }
            return top;
        }

        public int[] GetFanLevels() {
            var d = Call(OP_FAN_LEVEL_GET, Z4, 128);
            return new int[] { d.Length > 0 ? d[0] : -1, d.Length > 1 ? d[1] : -1 };
        }

        public int GetTemperature() { var d = Call(OP_TEMP, new byte[] { 1, 0, 0, 0 }, 4); return d.Length > 0 ? d[0] : -1; }

        public bool GetMaxFan() { var d = Call(OP_MAX_FAN_GET, Z4, 4); return d.Length > 0 && (d[0] & 1) != 0; }

        public GpuPowerState GetGpuPower() {
            var d = Call(OP_GPU_POWER_GET, Z4, 4);
            var g = new GpuPowerState();
            if (d.Length >= 4) { g.CustomTgp = d[0] != 0; g.Ppab = d[1] != 0; g.DState = d[2]; g.PeakTemp = d[3]; }
            return g;
        }

        public SystemInfo GetSystemInfo() {
            var d = Call(OP_SYSTEM_DATA, Z4, 128);
            var s = new SystemInfo { Raw = d };
            if (d.Length >= 9) {
                s.Valid = true;
                s.ThermalPolicy = d[3];
                s.SwFanControl = (d[4] & 1) != 0;
                s.DefaultPl4 = d[5];
                s.DefaultConcurrentTdp = d[8];
                s.GpuModes = d[7];
            }
            return s;
        }

        // Decompiled from OGH PerformanceControlHelper.SetFanMode: {0xFF, mode, Convert.ToByte(fanControlByBios), 0}
        public void SetMode(byte mode, bool fanControlByBios) { Call(OP_SET_MODE, new byte[] { 0xFF, mode, (byte)(fanControlByBios ? 1 : 0), 0 }, 0); }


        public void SetMaxFan(bool on) { Call(OP_MAX_FAN_SET, new byte[] { (byte)(on ? 1 : 0) }, 0); }
        // 18 was described here as the lowest level any HP firmware keeps a fan spinning. It is not: it is the
        // lower bound of OGH's custom-curve editor, and OGH's own default tables for these machines start at 0.
        // The Transcend 14's is [0, 23, 25, 27, ...]; an OMEN Transcend 14 (2025) runs its fans off entirely
        // until 74 C. So 0 is a level HP itself writes through this same command, and refusing to pass it on was
        // why four owners could silence their fans with OGH and not with Ohman.
        //
        // What stays refused is 1..17. Nothing writes those deliberately, and a fan asked for a speed it cannot
        // hold is worse than one told to stop: it is off, but nothing above knows it is.
        public const int AbsoluteFloor = 18;
        // Exactly 0 is off. Anything negative is a sentinel that escaped from somewhere above, and the safe
        // reading of "I do not know" is the floor, never stopped.
        static int Level(int v) { return v == 0 ? 0 : Math.Max(AbsoluteFloor, Math.Min(255, v)); }
        public void SetFanLevels(int fan1, int fan2) {
            // OGH on this machine sends a 128-byte buffer with the two levels in front; mirror it exactly.
            var d = new byte[128];
            d[0] = (byte)Level(fan1);
            d[1] = (byte)Level(fan2);
            Call(OP_FAN_LEVEL_SET, d, 0);
        }

        public void SetConcurrentTdp(int watts) {
            Call(OP_CPU_POWER_SET, new byte[] { 0xFF, 0xFF, 0xFF, (byte)Math.Max(0, Math.Min(255, watts)) }, 0);
        }

        public void SetCpuPowerLimits(int pl1, int pl2) {
            Call(OP_CPU_POWER_SET, new byte[] { (byte)Math.Max(1, Math.Min(254, pl2)), (byte)Math.Max(1, Math.Min(254, pl1)), 0xFF, 0xFF }, 0);
        }

        public void SetGpuPower(bool customTgp, bool ppab, int peakTemp) {
            Call(OP_GPU_POWER_SET, new byte[] { (byte)(customTgp ? 1 : 0), (byte)(ppab ? 1 : 0), 1, (byte)peakTemp }, 0);
        }

        // Graphics switching lives in the legacy mailbox: command 1 = read BIOS config, 2 = write; type 0x52.
        // OGH: mode = data[0] & 0x7F; on write, bit 7 set means "no reboot" and is only used on platforms from cycle 26C1 on.
        public const uint CMD_BIOS_READ = 1, CMD_BIOS_WRITE = 2, OP_GPU_MODE = 0x52;
        // Four zero bytes, not an empty buffer. hpqBDataIn sizes the input from Size, so an empty one reaches
        // the firmware as a 16 byte buffer; ACPI methods that build fields at fixed offsets then fault. Mainline
        // hp-wmi carries a fix for exactly this - "Resolve WMI query failures on some devices", found on an
        // OMEN 15-ek0xxx - which pads every input instead. This was the only read Ohman made with an empty
        // buffer, so it is worth padding on its own merits.
        //
        // It was NOT the board 8BC2 problem, and this comment used to say it was. The evidence for that came
        // from the support report, which asked for 0x52 on the wrong mailbox and therefore answered "refused"
        // on every laptop, working ones included. 8BC2 reported a graphics mode on 1.0.5 and on 1.0.6 alike:
        // the read was never the broken half. Only the write is, and rc 6 below is what that turned out to be.
        public int GetGpuMode() { var d = Call(CMD_BIOS_READ, OP_GPU_MODE, Z4, 4); return d.Length > 0 ? (d[0] & 0x7F) : -1; }
        public void SetGpuMode(int mode) {
            try { Call(CMD_BIOS_WRITE, OP_GPU_MODE, new byte[] { (byte)(mode & 0x7F), 0, 0, 0 }, 0); }
            catch (BiosException ex) {
                // 6 is not a refusal here. OGH sends this same payload on 8BC2, gets the same 6, writes it to its
                // log as an error, carries on to its restart prompt - and the mode has changed by the next boot.
                // It is not in hp-wmi's return codes; HP's other BIOS interface uses 6 for access denied, and on
                // this mailbox it reads as "accepted, restart to apply". Reporting it as a failure told an owner
                // his board could not switch graphics when it had just agreed to.
                if (ex.Code != 6) throw;
                Log.Write("graphics mode: rc 6, taken as accepted pending restart");
            }
        }
    }

    public sealed class BiosException : Exception {
        public readonly uint Op, Code;
        public BiosException(uint op, uint code) : base("BIOS returned " + code + " for command 0x" + op.ToString("X2")) { Op = op; Code = code; }
    }

    /// <summary>Simulated hardware for UI preview / non-elevated runs.</summary>
    public sealed class DemoHardware : IHardware {
        readonly Random rnd = new Random();
        int f1 = 27, f2 = 25, temp = 41;
        bool max;
        byte mode = 0x30;
        int tdp = 30;
        bool ppab;
        int m1 = -1, m2 = -1;
        public bool IsDemo { get { return true; } }
        public int GetFanCount() { return 2; }
        public int GetFanCountPassive() { return 2; }
        public int GetFanTableMax() { return 46; }
        public int[] GetFanLevels() {
            int t1 = max ? 57 : (m1 >= 0 ? m1 : (mode == 0x31 ? 36 : mode == 0x30 ? 28 : 22));
            int t2 = max ? 57 : (m2 >= 0 ? m2 : (mode == 0x31 ? 34 : mode == 0x30 ? 26 : 20));
            f1 += Math.Sign(t1 - f1) * Math.Min(3, Math.Abs(t1 - f1));
            f2 += Math.Sign(t2 - f2) * Math.Min(3, Math.Abs(t2 - f2));
            return new int[] { f1, f2 };
        }
        public int GetTemperature() { temp += rnd.Next(-1, 2); temp = Math.Max(34, Math.Min(52, temp)); return temp; }
        public bool GetMaxFan() { return max; }
        public GpuPowerState GetGpuPower() { return new GpuPowerState { CustomTgp = true, Ppab = ppab, DState = 1, PeakTemp = 87 }; }
        public SystemInfo GetSystemInfo() {
            return new SystemInfo { Valid = true, ThermalPolicy = 1, SwFanControl = true, DefaultPl4 = 159, DefaultConcurrentTdp = 30, GpuModes = 3,
                Raw = new byte[] { 0x8C, 0, 0x35, 1, 1, 0x9F, 0, 3, 0x1E } };
        }
        public void SetMode(byte m, bool byBios) { mode = m; }
        public void SetMaxFan(bool on) { max = on; }
        // 0 is a level, not the absence of one: the firmware stops the fan on it and owners can now ask for it.
        // Treating it as "no override" here made the simulated machine ignore the one setting hardest to check
        // on a real one, so a preview of a stopped fan showed it spinning at the mode's own speed.
        public void SetFanLevels(int a, int b) { m1 = a >= 0 ? a : -1; m2 = b >= 0 ? b : -1; }
        public void SetConcurrentTdp(int w) { tdp = w; }
        public void SetCpuPowerLimits(int pl1, int pl2) { }
        public void SetGpuPower(bool c, bool p, int t) { ppab = p; }
        int gpuMode = 0;
        public int GetGpuMode() { return gpuMode; }
        public void SetGpuMode(int m) { gpuMode = m; }
    }
}

