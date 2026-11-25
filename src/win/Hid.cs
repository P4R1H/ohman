// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: raw HID through hid.dll and setupapi.dll. src\linux\Hid.cs is the same class for Linux
// (hidraw). What is said over it is shared: LampArray and PerKeyLighting in src\Hid.cs, the Primax keyboard in
// src\McuKeyboard.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ohman {

    /// <summary>Enough of hid.dll and setupapi.dll to find a device and exchange feature reports with it.</summary>
    internal static class Hid {
        [StructLayout(LayoutKind.Sequential)]
        struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }

        [StructLayout(LayoutKind.Sequential)]
        struct Caps {
            public ushort Usage, UsagePage;
            public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkCollectionNodes;
            public ushort InputButtonCaps, InputValueCaps, InputDataIndices;
            public ushort OutputButtonCaps, OutputValueCaps, OutputDataIndices;
            public ushort FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref Attributes a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, ref Caps c);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr h, byte[] buf, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int needed, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string path, int access, int share, IntPtr sec, int disp, int flags, IntPtr template);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);

        const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        const int GENERIC_READ = unchecked((int)0x80000000), GENERIC_WRITE = 0x40000000;
        const int FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        internal static readonly IntPtr Invalid = new IntPtr(-1);

        /// <summary>One HID collection: its path and what its report descriptor says it is.</summary>
        internal sealed class Info {
            public string Path, Product;
            public ushort VendorId, ProductId, UsagePage, Usage;
            public int InputLen, OutputLen, FeatureLen;
            public override string ToString() {
                return "VID_" + VendorId.ToString("X4") + " PID_" + ProductId.ToString("X4") +
                    " usage " + UsagePage.ToString("X2") + "/" + Usage.ToString("X2") +
                    " reports in/out/feat " + InputLen + "/" + OutputLen + "/" + FeatureLen +
                    (string.IsNullOrEmpty(Product) ? "" : " \"" + Product + "\"");
            }
        }

        /// <summary>Every HID collection on the machine. Read-only: each device is opened without access rights,
        /// which is enough for HidD_GetAttributes and HidP_GetCaps and never disturbs whoever else has it open.</summary>
        internal static List<Info> Enumerate() {
            var found = new List<Info>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == Invalid) return found;
            try {
                var data = new InterfaceData();
                data.Size = Marshal.SizeOf(typeof(InterfaceData));
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++) {
                    string path = DetailPath(set, ref data);
                    if (path == null) continue;
                    var info = Describe(path);
                    if (info != null) found.Add(info);
                }
            } catch (Exception ex) { Log.Write("hid enumerate: " + ex.Message); }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        static string DetailPath(IntPtr set, ref InterfaceData data) {
            int needed;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
            if (needed <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try {
                // SP_DEVICE_INTERFACE_DETAIL_DATA is { DWORD cbSize; TCHAR DevicePath[1]; } and cbSize must be the
                // size of that declaration, not of the buffer: 8 with 64-bit packing, 6 on 32-bit.
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, needed, out needed, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            } finally { Marshal.FreeHGlobal(buf); }
        }

        static Info Describe(string path) {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) return null;
            IntPtr pp = IntPtr.Zero;
            try {
                var attrs = new Attributes();
                attrs.Size = Marshal.SizeOf(typeof(Attributes));
                if (!HidD_GetAttributes(h, ref attrs)) return null;
                if (!HidD_GetPreparsedData(h, out pp)) return null;
                var caps = new Caps();
                if (HidP_GetCaps(pp, ref caps) != 0x110000) return null;      // HIDP_STATUS_SUCCESS
                var info = new Info {
                    Path = path, VendorId = attrs.VendorId, ProductId = attrs.ProductId,
                    UsagePage = caps.UsagePage, Usage = caps.Usage,
                    InputLen = caps.InputReportByteLength, OutputLen = caps.OutputReportByteLength,
                    FeatureLen = caps.FeatureReportByteLength
                };
                var name = new byte[254];
                if (HidD_GetProductString(h, name, name.Length)) {
                    string s = System.Text.Encoding.Unicode.GetString(name);
                    int nul = s.IndexOf('\0');
                    info.Product = nul >= 0 ? s.Substring(0, nul) : s;
                }
                return info;
            } catch (Exception ex) { Log.Write("hid describe: " + ex.Message); return null; }
            finally { if (pp != IntPtr.Zero) HidD_FreePreparsedData(pp); CloseHandle(h); }
        }

        /// <summary>Open for feature-report traffic. Shared, so Windows keeps whatever it is doing with the device.</summary>
        internal static IntPtr Open(string path) {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        internal static bool GetFeature(IntPtr h, byte[] buf) { return HidD_GetFeature(h, buf, buf.Length); }
        internal static bool SetFeature(IntPtr h, byte[] buf) { return HidD_SetFeature(h, buf, buf.Length); }

        [DllImport("kernel32.dll", EntryPoint = "CreateFile", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileSafe(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        /// <summary>Open for input and output reports as a stream of whole reports, report id first. Read/write,
        /// shared (OGH opens it the same way), overlapped so every wait can have a timeout. Null when it cannot
        /// be opened; error is then the Win32 code.</summary>
        internal static Stream OpenStream(string path, int reportLen, out int error) {
            error = 0;
            SafeFileHandle handle = CreateFileSafe(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); return null; }
            return new FileStream(handle, FileAccess.ReadWrite, reportLen, true);
        }
    }
}
