// Ohman — hardware layer.
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
        public static string Path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.FileStem + ".log");
        public static void Write(string s) {
            try {
                lock (Sync) {
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
        public int DefaultConcurrentTdp; // byte 8 (W) — base for the "+15 W" slider
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
        /// <summary>Fan count from the fan table (0x2F) — a plain read with no side effects.</summary>
        int GetFanCountPassive();
        int[] GetFanLevels();                       // {fan1, fan2} in units of 100 RPM (0..57 on this machine)
        int GetTemperature();                       // BIOS thermal sensor (0x23), degrees C
        bool GetMaxFan();
        GpuPowerState GetGpuPower();
        SystemInfo GetSystemInfo();
        void SetMode(byte mode, bool fanControlByBios);   // OGH sets fanControlByBios=1 on battery (BiosAutoFanControlInDc)
        void SetMaxFan(bool on);
        void SetFanLevels(int fan1, int fan2);
        void SetConcurrentTdp(int watts);
        void SetGpuPower(bool customTgp, bool ppab, int peakTemp);
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
        public const uint OP_TEMP          = 0x23; // in {1,0,0,0} out4 -> [0]=degC
        public const uint OP_MAX_FAN_GET   = 0x26; // out4 -> [0]=1 when max
        public const uint OP_MAX_FAN_SET   = 0x27; // in {1|0}
        public const uint OP_SYSTEM_DATA   = 0x28; // out128
        public const uint OP_CPU_POWER_SET = 0x29; // in {PL1, PL2, PL4, LimitWithGpu}, 0xFF = leave unchanged
        public const uint OP_FAN_LEVEL_GET = 0x2D; // out128 -> [0]=fan1 [1]=fan2 (x100 RPM)
        public const uint OP_FAN_LEVEL_SET = 0x2E; // in {fan1, fan2, 0...}
        public const uint OP_FAN_TABLE_GET = 0x2F; // out128 -> [0]=fan count, [1]=entries, then {fan1, fan2, temp} triplets

        // Thermal-policy v1 mode bytes (this machine reports policy v1 in system data byte 3).
        // OGH's own SetFanMode maps: Default->0x30, Performance->0x31, Cool->0x50, Eco->0x30 (Eco is software-side: Windows power mode + GPU/TDP).
        public const byte MODE_DEFAULT = 0x30, MODE_PERFORMANCE = 0x31, MODE_COOL = 0x50;

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
                    din["Sign"] = SIGN; din["Command"] = command; din["CommandType"] = commandType;
                    din["Size"] = (uint)data.Length; din["hpqBData"] = data;
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
        public int GetFanCountPassive() { var d = Call(OP_FAN_TABLE_GET, Z4, 128); return d.Length > 0 ? d[0] : -1; }

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
                s.Valid = true; s.ThermalPolicy = d[3]; s.SwFanControl = (d[4] & 1) != 0;
                s.DefaultPl4 = d[5]; s.DefaultConcurrentTdp = d[8];
            }
            return s;
        }

        // Decompiled from OGH PerformanceControlHelper.SetFanMode: {0xFF, mode, Convert.ToByte(fanControlByBios), 0}
        public void SetMode(byte mode, bool fanControlByBios) { Call(OP_SET_MODE, new byte[] { 0xFF, mode, (byte)(fanControlByBios ? 1 : 0), 0 }, 0); }

        public void SetMaxFan(bool on) { Call(OP_MAX_FAN_SET, new byte[] { (byte)(on ? 1 : 0) }, 0); }

        public const int MinFanLevel = 18;                    // 1800 rpm, the lowest level OGH itself ever writes
        public void SetFanLevels(int fan1, int fan2) {
            // OGH on this machine sends a 128-byte buffer with the two levels in front; mirror it exactly.
            var d = new byte[128];
            // Level 0 switches a fan off on this firmware (measured); the hardware layer refuses anything below the floor.
            d[0] = (byte)Math.Max(MinFanLevel, Math.Min(255, fan1)); d[1] = (byte)Math.Max(MinFanLevel, Math.Min(255, fan2));
            Call(OP_FAN_LEVEL_SET, d, 0);
        }

        public void SetConcurrentTdp(int watts) {
            Call(OP_CPU_POWER_SET, new byte[] { 0xFF, 0xFF, 0xFF, (byte)Math.Max(0, Math.Min(255, watts)) }, 0);
        }

        public void SetGpuPower(bool customTgp, bool ppab, int peakTemp) {
            Call(OP_GPU_POWER_SET, new byte[] { (byte)(customTgp ? 1 : 0), (byte)(ppab ? 1 : 0), 1, (byte)peakTemp }, 0);
        }
    }

    public sealed class BiosException : Exception {
        public readonly uint Op, Code;
        public BiosException(uint op, uint code) : base("BIOS returned " + code + " for command 0x" + op.ToString("X2")) { Op = op; Code = code; }
    }

    /// <summary>Simulated hardware for UI preview / non-elevated runs.</summary>
    public sealed class DemoHardware : IHardware {
        readonly Random rnd = new Random();
        int f1 = 27, f2 = 25, temp = 41; bool max; byte mode = 0x30; int tdp = 30; bool ppab;
        int m1 = -1, m2 = -1;
        public bool IsDemo { get { return true; } }
        public int GetFanCount() { return 2; }
        public int GetFanCountPassive() { return 2; }
        public int[] GetFanLevels() {
            int t1 = max ? 57 : (m1 >= 0 ? m1 : (mode == 0x31 ? 36 : mode == 0x30 ? 28 : 22));
            int t2 = max ? 57 : (m2 >= 0 ? m2 : (mode == 0x31 ? 34 : mode == 0x30 ? 26 : 20));
            f1 += Math.Sign(t1 - f1) * Math.Min(3, Math.Abs(t1 - f1)); f2 += Math.Sign(t2 - f2) * Math.Min(3, Math.Abs(t2 - f2));
            return new int[] { f1, f2 };
        }
        public int GetTemperature() { temp += rnd.Next(-1, 2); temp = Math.Max(34, Math.Min(52, temp)); return temp; }
        public bool GetMaxFan() { return max; }
        public GpuPowerState GetGpuPower() { return new GpuPowerState { CustomTgp = true, Ppab = ppab, DState = 1, PeakTemp = 87 }; }
        public SystemInfo GetSystemInfo() {
            return new SystemInfo { Valid = true, ThermalPolicy = 1, SwFanControl = true, DefaultPl4 = 159, DefaultConcurrentTdp = 30,
                Raw = new byte[] { 0x8C, 0, 0x35, 1, 1, 0x9F, 0, 3, 0x1E } };
        }
        public void SetMode(byte m, bool byBios) { mode = m; }
        public void SetMaxFan(bool on) { max = on; }
        public void SetFanLevels(int a, int b) { m1 = a > 0 ? a : -1; m2 = b > 0 ? b : -1; }
        public void SetConcurrentTdp(int w) { tdp = w; }
        public void SetGpuPower(bool c, bool p, int t) { ppab = p; }
    }
}

