// Ohman — display helpers: panel refresh rate (Win32 display settings) and display off. No firmware involved.
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

        static DEVMODE Fresh() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }

        /// <summary>The primary display's current refresh rate, 0 when unknown.</summary>
        public static int CurrentHz() {
            try { var d = Fresh(); if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref d)) return d.dmDisplayFrequency; } catch { }
            return 0;
        }

        /// <summary>Refresh rates the primary display offers at its current resolution and depth, ascending.</summary>
        public static int[] Rates() {
            var set = new SortedDictionary<int, bool>();
            try {
                var cur = Fresh(); if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref cur)) return new int[0];
                for (int i = 0; ; i++) {
                    var d = Fresh(); if (!EnumDisplaySettings(null, i, ref d)) break;
                    if (d.dmPelsWidth == cur.dmPelsWidth && d.dmPelsHeight == cur.dmPelsHeight && d.dmBitsPerPel == cur.dmBitsPerPel && d.dmDisplayFrequency > 1) set[d.dmDisplayFrequency] = true;
                }
            } catch (Exception ex) { Log.Write("display modes: " + ex.Message); }
            var r = new List<int>(set.Keys); return r.ToArray();
        }

        /// <summary>The rates worth a button: 60 Hz, the highest, and the lowest at or above 48 Hz when that is neither.</summary>
        public static int[] Choices() {
            var all = Rates(); if (all.Length < 2) return new int[0];
            var pick = new SortedDictionary<int, bool>();
            int max = all[all.Length - 1]; pick[max] = true;
            foreach (int r in all) if (r == 60) pick[60] = true;
            foreach (int r in all) if (r >= 48) { pick[r] = true; break; }
            var l = new List<int>(pick.Keys); return l.ToArray();
        }
        /// <summary>The battery rate: 60 Hz when the panel has it, else the lowest rate at or above 48 Hz, else the lowest.</summary>
        public static int BatteryHz() {
            var all = Rates(); if (all.Length == 0) return 0;
            foreach (int r in all) if (r == 60) return 60;
            foreach (int r in all) if (r >= 48) return r;
            return all[0];
        }
        /// <summary>Switch the primary display's refresh rate, keeping resolution and depth; persisted like the Settings app does.</summary>
        public static bool SetHz(int hz) {
            try {
                var d = Fresh(); if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref d)) return false;
                if (d.dmDisplayFrequency == hz) return true;
                d.dmDisplayFrequency = hz; d.dmFields = DM_DISPLAYFREQUENCY;
                int rc = ChangeDisplaySettingsEx(null, ref d, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                Log.Write("refresh rate " + hz + " Hz -> rc " + rc);
                return rc == DISP_CHANGE_SUCCESSFUL;
            } catch (Exception ex) { Log.Write("refresh rate: " + ex.Message); return false; }
        }

        /// <summary>Put every display to sleep (what the power button's "turn off display" does); any input wakes them.</summary>
        public static void Off() {
            const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170; var HWND_BROADCAST = new IntPtr(0xFFFF);
            try { SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2)); } catch (Exception ex) { Log.Write("display off: " + ex.Message); }
        }
    }
}
