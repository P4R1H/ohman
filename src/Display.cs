// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: display helpers: panel refresh rate (Win32 display settings) and display off. No firmware involved.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ohman {

    public static class Display {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct DEVMODE {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, CDS_UPDATEREGISTRY = 1, DISP_CHANGE_SUCCESSFUL = 0;
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---------- which panel is the laptop's own ----------
        // EnumDisplaySettings(Panel(), ...) means "the current display device on the computer on which the calling
        // thread is running" (MSDN), which is the primary display. On a docked laptop with an external monitor set
        // primary that is the external one, so the refresh rate control read and wrote the wrong screen. Two owners
        // reported it as Ohman "detecting the external display as internal".
        //
        // The connector type is what distinguishes them, and only the DisplayConfig API reports it. Everything here
        // falls back to null on any failure, which is exactly the old behaviour, so a machine this cannot work out
        // is no worse off than before.
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID adapter; public uint id, modeIdx, statusFlags; }
        [StructLayout(LayoutKind.Sequential)] struct PathTarget {
            public LUID adapter; public uint id, modeIdx, outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLineOrdering;
            public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource source; public PathTarget target; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] struct ModeInfo {
            public uint infoType; public uint id; public LUID adapter;
            // the union that follows is 48 bytes and none of it is needed here, only its size
            public long a, b, c, d, e, f;
        }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoHeader { public uint type, size; public LUID adapter; public uint id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SourceDeviceName {
            public DeviceInfoHeader header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiDeviceName;
        }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName req);
        const uint QDC_ONLY_ACTIVE_PATHS = 2, GET_SOURCE_NAME = 1;   // GET_TARGET_NAME is 2 and wants a bigger struct: passing it here returns ERROR_INVALID_PARAMETER
        // DISPLAYCONFIG_OUTPUT_TECHNOLOGY: INTERNAL, and the two embedded kinds a modern panel reports instead.
        const uint TECH_INTERNAL = 0x80000000, TECH_DISPLAYPORT_EMBEDDED = 11, TECH_UDI_EMBEDDED = 13;

        /// <summary>GDI name of the laptop's built-in panel, or null when it cannot be identified. Not cached across
        /// calls: a dock or an undock changes the answer. Each public method below resolves it once into a local
        /// instead, because this is four Win32 calls and two allocations and it must not sit inside a loop.</summary>
        static string Panel() {
            try {
                uint nPaths, nModes;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out nPaths, out nModes) != 0) return null;
                var paths = new PathInfo[nPaths];
                var modes = new ModeInfo[nModes];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPaths, paths, ref nModes, modes, IntPtr.Zero) != 0) return null;
                for (int i = 0; i < nPaths; i++) {
                    uint tech = paths[i].target.outputTechnology;
                    if (tech != TECH_INTERNAL && tech != TECH_DISPLAYPORT_EMBEDDED && tech != TECH_UDI_EMBEDDED) continue;
                    var q = new SourceDeviceName();
                    q.header.type = GET_SOURCE_NAME;
                    q.header.size = (uint)Marshal.SizeOf(typeof(SourceDeviceName));
                    q.header.adapter = paths[i].source.adapter;
                    q.header.id = paths[i].source.id;
                    if (DisplayConfigGetDeviceInfo(ref q) != 0) continue;
                    if (!string.IsNullOrEmpty(q.gdiDeviceName)) return q.gdiDeviceName;
                }
            } catch (Exception ex) { Log.Write("internal panel: " + ex.Message); }
            return null;
        }

        static DEVMODE Fresh() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }

        /// <summary>The built-in panel's current refresh rate, 0 when unknown.</summary>
        public static int CurrentHz() {
            try { string p = Panel(); var d = Fresh(); if (EnumDisplaySettings(p, ENUM_CURRENT_SETTINGS, ref d)) return d.dmDisplayFrequency; } catch { }
            return 0;
        }

        /// <summary>Refresh rates the primary display offers at its current resolution and depth, ascending.</summary>
        public static int[] Rates() {
            var set = new SortedDictionary<int, bool>();
            try {
                string p = Panel();                            // hoisted: the loop runs once per display mode, 30-100 times on a laptop panel
                var cur = Fresh();
                if (!EnumDisplaySettings(p, ENUM_CURRENT_SETTINGS, ref cur)) return new int[0];
                for (int i = 0; ; i++) {
                    var d = Fresh();
                    if (!EnumDisplaySettings(p, i, ref d)) break;
                    if (d.dmPelsWidth == cur.dmPelsWidth && d.dmPelsHeight == cur.dmPelsHeight && d.dmBitsPerPel == cur.dmBitsPerPel && d.dmDisplayFrequency > 1) set[d.dmDisplayFrequency] = true;
                }
            } catch (Exception ex) { Log.Write("display modes: " + ex.Message); }
            var r = new List<int>(set.Keys);
            return r.ToArray();
        }

        /// <summary>The rates worth a button: 60 Hz, the highest, and the lowest at or above 48 Hz when that is neither.</summary>
        public static int[] Choices() {
            var all = Rates();
            if (all.Length < 2) return new int[0];
            var pick = new SortedDictionary<int, bool>();
            int max = all[all.Length - 1];
            pick[max] = true;
            foreach (int r in all) if (r == 60) pick[60] = true;
            foreach (int r in all) if (r >= 48) { pick[r] = true; break; }
            var l = new List<int>(pick.Keys);
            return l.ToArray();
        }
        /// <summary>The battery rate: 60 Hz when the panel has it, else the lowest rate at or above 48 Hz, else the lowest.</summary>
        public static int BatteryHz() {
            var all = Rates();
            if (all.Length == 0) return 0;
            foreach (int r in all) if (r == 60) return 60;
            foreach (int r in all) if (r >= 48) return r;
            return all[0];
        }
        /// <summary>The panel's fastest rate. Used to put things back when Ohman lowered the rate on battery and
        /// the user never chose a rate of their own, so there is nothing else to return to.</summary>
        public static int HighestHz() {
            var all = Rates();
            return all.Length == 0 ? 0 : all[all.Length - 1];
        }
        /// <summary>Switch the built-in panel's refresh rate, keeping resolution and depth; persisted like the Settings app does.</summary>
        public static bool SetHz(int hz) {
            try {
                string p = Panel();                            // the same panel has to be read and written, so resolve it once
                var d = Fresh();
                if (!EnumDisplaySettings(p, ENUM_CURRENT_SETTINGS, ref d)) return false;
                if (d.dmDisplayFrequency == hz) return true;
                int was = d.dmDisplayFrequency;
                d.dmDisplayFrequency = hz;
                d.dmFields = DM_DISPLAYFREQUENCY;
                int rc = ChangeDisplaySettingsEx(p, ref d, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                // read it back: "it says it switched but it did not" (#81) is invisible without this
                var after = Fresh();
                int now = EnumDisplaySettings(p, ENUM_CURRENT_SETTINGS, ref after) ? after.dmDisplayFrequency : 0;
                Log.Write("refresh rate " + was + " -> " + hz + " Hz: rc " + rc + ", reads back " + now + " Hz");
                return rc == DISP_CHANGE_SUCCESSFUL;
            } catch (Exception ex) { Log.Write("refresh rate: " + ex.Message); return false; }
        }

        /// <summary>Put every display to sleep (what the power button's "turn off display" does); any input wakes them.</summary>
        public static void Off() {
            const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
            var HWND_BROADCAST = new IntPtr(0xFFFF);
            try { SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2)); } catch (Exception ex) { Log.Write("display off: " + ex.Message); }
        }
    }
}
