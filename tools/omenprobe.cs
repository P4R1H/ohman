// omenprobe â€” tiny CLI to exercise the HP hpqBIntM WMI BIOS interface. Must run elevated.
// Usage:
//   omenprobe read                          -> run every known read command, print raw bytes
//   omenprobe call <typeHex> <outSize> [bytes...]   e.g. call 1A 4 FF 31 00 00
//   omenprobe watch <seconds>               -> print hpqBEvnt events
//   omenprobe classes                       -> dump hpqBIntM instance + method names
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Text;
using System.Threading;

static class P {
    const uint CMD = 0x20008;
    static readonly byte[] SIGN = { 0x53, 0x45, 0x43, 0x55 };

    static string Hex(byte[] b, int max) {
        if (b == null) return "(null)";
        var sb = new StringBuilder();
        int n = Math.Min(b.Length, max);
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        if (b.Length > n) sb.Append(" ...(" + b.Length + " total)");
        return sb.ToString();
    }

    static ManagementObject Intf() {
        foreach (ManagementObject mo in new ManagementObjectSearcher("root\\wmi", "SELECT * FROM hpqBIntM").Get()) return mo;
        throw new Exception("hpqBIntM instance not found");
    }

    // returns rc; fills data/sign
    static uint CmdOverride = CMD;
    static uint Call(uint type, byte[] data, int outSize, out byte[] outData, out string outSign, out long ms) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var intf = Intf()) {
            var cls = new ManagementClass("root\\wmi", "hpqBDataIn", null);
            var din = cls.CreateInstance();
            din["Sign"] = SIGN; din["Command"] = CmdOverride; din["CommandType"] = type;
            din["Size"] = (uint)data.Length; din["hpqBData"] = data;
            string m = "hpqBIOSInt" + outSize;
            var inP = intf.GetMethodParameters(m);
            inP["InData"] = din;
            var outP = intf.InvokeMethod(m, inP, null);
            var od = (ManagementBaseObject)outP["OutData"];
            uint rc = Convert.ToUInt32(od["rwReturnCode"]);
            outData = outSize == 0 ? new byte[0] : (od["Data"] as byte[]);
            var s = od["Sign"] as byte[];
            outSign = s == null ? "(null)" : Encoding.ASCII.GetString(s);
            ms = sw.ElapsedMilliseconds;
            return rc;
        }
    }

    static void Try(string name, uint type, byte[] data, int outSize) {
        try {
            byte[] o; string sg; long ms;
            uint rc = Call(type, data, outSize, out o, out sg, out ms);
            Console.WriteLine("{0,-28} type=0x{1:X2} in=[{2}] out{3} -> rc={4} sign={5} data=[{6}] ({7}ms)",
                name, type, Hex(data, 8), outSize, rc, sg, Hex(o, 24), ms);
        } catch (Exception ex) {
            Console.WriteLine("{0,-28} type=0x{1:X2} EXC {2}", name, type, ex.Message);
        }
    }

    static int Main(string[] a) {
        try {
            if (a.Length == 0 || a[0] == "read") {
                byte[] z4 = { 0, 0, 0, 0 };
                Try("GetFanCount(0x10)", 0x10, z4, 4);
                Try("GetFanLevel(0x2D)", 0x2D, z4, 128);
                Try("GetFanTable(0x2F)", 0x2F, z4, 128);
                Try("GetFanMode?(0x0F)", 0x0F, z4, 4);      // hp-wmi: HPWMI_FAN_SPEED_GET_QUERY? cross-check
                Try("GetMaxFan(0x26)", 0x26, z4, 4);
                Try("GetTemp(0x23)", 0x23, z4, 4);
                Try("GetGpuPower(0x21)", 0x21, z4, 4);
                Try("GetSystemDesign(0x28)", 0x28, z4, 128);
                Try("GetSystemDesign(0x28)/1024", 0x28, z4, 1024);
                Try("GetTemp0x23/128", 0x23, z4, 128);
                Try("GetFanRpm?(0x2C)", 0x2C, z4, 128);
                Try("GetPerfMode?(0x1B)", 0x1B, z4, 4);
                Try("GetKbdType(0x2B)", 0x2B, z4, 4);
                Try("GetBiosStatus?(0x30)", 0x30, z4, 4);
                Try("GetCpuPower?(0x2A)", 0x2A, z4, 4);
                Try("Get0x11", 0x11, z4, 4);
                Try("Get0x12", 0x12, z4, 4);
                Try("Get0x13", 0x13, z4, 4);
                Try("Get0x24", 0x24, z4, 4);
                Try("Get0x25", 0x25, z4, 4);
                Try("GetThermalPolicyVer?(0x35)", 0x35, z4, 4);
                Try("GetBorn(0x36)", 0x36, z4, 128);
                return 0;
            }
            if (a[0] == "call") {
                uint type = uint.Parse(a[1], NumberStyles.HexNumber);
                int outSize = int.Parse(a[2]);
                var d = new List<byte>();
                for (int i = 3; i < a.Length; i++) d.Add(byte.Parse(a[i], NumberStyles.HexNumber));
                byte[] data = d.ToArray();
                if (a.Length > 3 && a[3].StartsWith("pad")) { } // no-op
                Try("call", type, data, outSize);
                return 0;
            }
            if (a[0] == "callpad") { // callpad <typeHex> <outSize> <padLen> [bytes...] -> pads payload with zeros to padLen
                uint type = uint.Parse(a[1], NumberStyles.HexNumber);
                int outSize = int.Parse(a[2]);
                int pad = int.Parse(a[3]);
                byte[] data = new byte[pad];
                for (int i = 4; i < a.Length && i - 4 < pad; i++) data[i - 4] = byte.Parse(a[i], NumberStyles.HexNumber);
                Try("callpad", type, data, outSize);
                return 0;
            }
            if (a[0] == "watch") {
                int secs = int.Parse(a[1]);
                var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"), new WqlEventQuery("SELECT * FROM hpqBEvnt"));
                w.EventArrived += (s, e) => {
                    try { Console.WriteLine("{0:HH:mm:ss.fff} hpqBEvnt EventID={1} EventData={2}", DateTime.Now, e.NewEvent["EventID"], e.NewEvent["EventData"]); } catch (Exception ex) { Console.WriteLine("evt err " + ex.Message); }
                };
                w.Start();
                Console.WriteLine("watching hpqBEvnt for " + secs + "s ...");
                Thread.Sleep(secs * 1000);
                w.Stop();
                return 0;
            }
            if (a[0] == "classes") {
                foreach (ManagementObject mo in new ManagementObjectSearcher("root\\wmi", "SELECT * FROM hpqBIntM").Get()) {
                    Console.WriteLine("instance: " + mo["InstanceName"] + " active=" + mo["Active"]);
                    /* methods listed via Get-CimClass */
                }
                return 0;
            }
            Console.WriteLine("unknown command");
            return 2;
        } catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 1; }
    }
}


