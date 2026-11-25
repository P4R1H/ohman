// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: what the benchmark asks of Windows. Frames come from Intel PresentMon's console exe (MIT),
// fetched on first use and checked before every start; Ohman only reads what it prints. Beside it: the window in
// front and the process behind it, the screen it is on, what the run was made on, and the two sounds.
// src\linux\Bench.cs holds the same classes for Linux (FrameCapture, Games' other half, Machine, Cues), where the
// frames come from MangoHud instead. The run itself, the numbers and the stored runs are shared: src\Bench.cs.
//
// Frame time is MsBetweenPresents, dropped frames included, which is CapFrameX's default.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ohman {

    /// <summary>The frame capture as the shared benchmark sees it: is the tool there, fetch it, start it. Here it
    /// is PresentMon.</summary>
    static class FrameCapture {
        /// <summary>What the tool is called, for the log and the page.</summary>
        public const string Tool = "PresentMon";
        /// <summary>The tool can be started: PresentMon is downloaded.</summary>
        public static bool Ready { get { return PresentMon.Downloaded; } }
        /// <summary>Why a run cannot start when it is not.</summary>
        public const string Missing = "PresentMon is not downloaded";
        /// <summary>Ohman can get the tool itself. Throws with a short reason on any failure.</summary>
        public const bool CanFetch = true;
        public static void Fetch() { PresentMon.Fetch(); }
        /// <summary>Frames of one process, or of every process when pid is 0. Null, and why, when it cannot start.</summary>
        public static IFrameSource Start(int pid, out string error) { return PresentMonSource.Start(pid, out error); }
        /// <summary>Clear up after a run that ended badly. A session left behind by a crash keeps tracing every
        /// present on the machine until a reboot. Ours has a name of its own, so ending it is safe and costs one call.</summary>
        public static void Recover() { Etw.Stop(PresentMon.Session, true); }
        /// <summary>What the page says while it waits for a game, and under it.</summary>
        public const string WaitTitle = "Switch to your game", WaitHint = "It starts once a game in front is drawing";
        /// <summary>The line to copy into a launcher so the tool is loaded with the game. Empty: nothing is needed.</summary>
        public const string LaunchOption = "";
    }


    // ================================================================== the tool ==================================
    public static class PresentMon {
        public const string Version = "2.6.0";
        const string Url = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        public const long Size = 980320;
        const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
        const string Signer = "Intel Corporation";
        /// <summary>Our ETW session's name. --stop_existing_session takes back one a crash left running.</summary>
        public const string Session = "OhmanBench";

        public static string Dir { get { return RunStore.Root; } }
        public static string Exe { get { return Path.Combine(Dir, "PresentMon-" + Version + "-x64.exe"); } }
        /// <summary>The file is there at the pinned length. The full check runs before every start.</summary>
        public static bool Downloaded { get { try { var fi = new FileInfo(Exe); return fi.Exists && fi.Length == Size; } catch { return false; } } }

        /// <summary>Download the pinned build next to Ohman. Throws with a short reason on any failure and leaves
        /// nothing behind; the file only takes its real name once it has passed every check.</summary>
        public static void Fetch() {
            Directory.CreateDirectory(Dir);
            string part = Exe + ".part";
            try {
                Update.Download(Url, part, Size);
                string why;
                using (var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read)) why = Check(fs, part);
                if (why != null) throw new Exception(why);
                if (File.Exists(Exe)) File.Delete(Exe);
                File.Move(part, Exe);
                Log.Write("PresentMon " + Version + " downloaded and verified");
            } finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
        }

        /// <summary>Open the exe and check it through that handle: length, SHA-256 and Intel's signature. The handle
        /// stays open while PresentMon runs, and its share mode lets nobody write, rename or delete the file in
        /// the meantime, so the file that was checked is the file that runs with Ohman's token.</summary>
        public static FileStream OpenChecked(out string error) {
            error = null;
            FileStream fs = null;
            try {
                fs = new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.Read);
                error = Check(fs, Exe);
                if (error == null) return fs;
                fs.Dispose(); fs = null;
                // Not the pinned build (patched by an antivirus, damaged on disk): remove it, so the page goes back
                // to offering the download instead of a Start that can never work.
                try { File.Delete(Exe); } catch { }
            } catch (Exception ex) { error = ex.Message; }
            if (fs != null) fs.Dispose();
            Log.Write("PresentMon check failed: " + error);
            return null;
        }
        static string Check(FileStream fs, string path) {
            if (fs.Length != Size) return "PresentMon is " + fs.Length + " bytes, expected " + Size;
            fs.Position = 0;
            string hex;
            using (var sha = SHA256.Create()) hex = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (hex != Sha256) return "PresentMon does not match the pinned build";
            string signer;
            if (!PawnIo.Signed(path, Signer, out signer)) return "PresentMon is not signed by " + Signer + (signer != null ? " (" + signer + ")" : "");
            return null;
        }
    }

    /// <summary>Our ETW session by name. Stopping it is what PresentMon's own --terminate_existing_session does
    /// (ControlTrace, EVENT_TRACE_CONTROL_STOP), done here without starting another process: the PresentMon reading
    /// it then exits by itself.</summary>
    static class Etw {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        const int PropsSize = 120, NameChars = 1024;                     // sizeof(EVENT_TRACE_PROPERTIES) on x64, then room for the name
        /// <summary>True when a session of that name was running and is now stopped.</summary>
        public static bool Stop(string name, bool quiet) {
            int size = PropsSize + NameChars * 2;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, size);                            // Wnode.BufferSize
                Marshal.WriteInt32(buf, 116, PropsSize);                     // LoggerNameOffset
                int rc = ControlTraceW(0, name, buf, 1);                     // EVENT_TRACE_CONTROL_STOP
                if (rc == 0) { Log.Write("ETW session " + name + " stopped"); return true; }
                if (rc != 4201 && !quiet) Log.Write("ETW session " + name + ": stop returned " + rc);   // 4201 = not running
                return false;
            } catch (Exception ex) { if (!quiet) Log.Write("ETW stop: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>A job that ends its processes when Ohman ends, however it ends: a crash or End task must not leave an
    /// elevated PresentMon tracing behind it.</summary>
    static class KillOnExit {
        [StructLayout(LayoutKind.Sequential)] struct Basic {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] struct Io { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic BasicLimitInformation; public Io IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref Extended info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        static IntPtr job;
        static readonly object sync = new object();
        public static void Add(Process p) {
            try {
                lock (sync) {
                    if (job == IntPtr.Zero) {
                        IntPtr j = CreateJobObject(IntPtr.Zero, null);
                        var info = new Extended();
                        info.BasicLimitInformation.LimitFlags = 0x2000;          // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        if (j != IntPtr.Zero && SetInformationJobObject(j, 9, ref info, Marshal.SizeOf(typeof(Extended)))) job = j;   // JobObjectExtendedLimitInformation
                        else { Log.Write("job object: " + Marshal.GetLastWin32Error()); if (j != IntPtr.Zero) CloseHandle(j); }
                    }
                    if (job != IntPtr.Zero && !AssignProcessToJobObject(job, p.Handle)) Log.Write("job object assign: " + Marshal.GetLastWin32Error());
                }
            } catch (Exception ex) { Log.Write("job object: " + ex.Message); }
        }
    }

    /// <summary>PresentMon's CSV, read by column name: the column set changes with the flags, and the header is the
    /// only thing that says which is which. Without --v1/--v2 and with --qpc_time the columns used are ProcessID,
    /// SwapChainAddress, PresentMode, MsBetweenPresents, MsBetweenDisplayChange ("NA" when not displayed) and TimeInQPC.</summary>
    public sealed class PresentMonCsv {
        int iPid = -1, iSwap = -1, iMode = -1, iMs = -1, iDisp = -1, iQpc = -1, cols;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>The column map, or null when the line is not a header with what we need in it.</summary>
        public static PresentMonCsv Header(string line) {
            if (string.IsNullOrEmpty(line)) return null;
            var c = new PresentMonCsv();
            string[] h = line.Split(',');
            for (int i = 0; i < h.Length; i++) {
                string n = h[i].Trim();
                if (n == "ProcessID") c.iPid = i;
                else if (n == "SwapChainAddress") c.iSwap = i;
                else if (n == "PresentMode") c.iMode = i;
                else if (n == "MsBetweenPresents") c.iMs = i;
                else if (n == "MsBetweenDisplayChange") c.iDisp = i;
                else if (n == "TimeInQPC") c.iQpc = i;
                else if (n == "CPUStartQPC" && c.iQpc < 0) c.iQpc = i;
            }
            if (c.iPid < 0 || c.iMs < 0 || c.iQpc < 0) return null;
            c.cols = h.Length;
            return c;
        }
        public bool Row(string line, out FrameRow r) {
            r = new FrameRow();
            if (string.IsNullOrEmpty(line)) return false;
            string[] f = line.Split(',');
            // The first column is the process name and nothing stops it holding a comma; everything after it is a
            // number or a fixed string, so any extra fields belong to the name.
            int extra = f.Length - cols;
            if (extra < 0) return false;
            if (!int.TryParse(f[iPid + extra], NumberStyles.Integer, Inv, out r.Pid)) return false;
            if (!double.TryParse(f[iMs + extra], NumberStyles.Float, Inv, out r.Ms) || !(r.Ms > 0) || double.IsInfinity(r.Ms)) return false;
            if (!long.TryParse(f[iQpc + extra], NumberStyles.Integer, Inv, out r.Qpc) || r.Qpc <= 0) return false;
            double d = 0;
            r.Displayed = iDisp >= 0 && double.TryParse(f[iDisp + extra], NumberStyles.Float, Inv, out d) && d > 0 && !double.IsInfinity(d);
            if (r.Displayed) r.DisplayMs = d;
            if (iSwap >= 0) {
                string sw = f[iSwap + extra];
                if (sw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) sw = sw.Substring(2);
                ulong u; if (ulong.TryParse(sw, NumberStyles.HexNumber, Inv, out u)) r.Swap = u;
            }
            r.Mode = 7;
            if (iMode >= 0) { int m = Array.IndexOf(FrameRow.Modes, f[iMode + extra]); if (m >= 0) r.Mode = (byte)m; }
            return true;
        }
    }

    /// <summary>A PresentMon process streaming CSV into Ohman. Rows are read by column name: the column set
    /// changes with the flags, and the header is the only thing that says which is which.</summary>
    sealed class PresentMonSource : IFrameSource {
        public event Action<FrameRow> Row;
        public bool Ended { get { return ended; } }
        public string Why { get { return why; } }
        public string Note { get { return ""; } }
        volatile bool ended, stopping;
        int stopped;
        string why = "";
        Process proc;
        FileStream hold;
        Thread reader;
        readonly StringBuilder err = new StringBuilder();

        public static PresentMonSource Start(int pid, out string error) {
            var s = new PresentMonSource();
            s.hold = PresentMon.OpenChecked(out error);
            if (s.hold == null) return null;
            // No --v1/--v2: only the default set has both MsBetweenPresents and MsBetweenDisplayChange. --qpc_time
            // puts QPC ticks in TimeInQPC, the same clock as our own. No --track_frame_type: it is a beta option
            // that adds rows of its own. Input tracking is the one thing we never read.
            string args = "--output_stdout --no_console_stats --no_track_input --qpc_time --stop_existing_session --session_name " + PresentMon.Session;
            if (pid > 0) args += " --process_id " + pid + " --terminate_on_proc_exit";
            try {
                var psi = new ProcessStartInfo(PresentMon.Exe, args) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                s.proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                // stderr is drained on its own: left unread it fills the pipe and PresentMon stops writing rows
                s.proc.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (s.err) { if (s.err.Length < 2000) s.err.Append(e.Data).Append(' '); } };
                s.proc.Start();
                KillOnExit.Add(s.proc);
                s.proc.BeginErrorReadLine();
                s.reader = new Thread(s.Read) { IsBackground = true, Name = "presentmon" };
                s.reader.Start();
                Log.Write("PresentMon started" + (pid > 0 ? " for pid " + pid : " for every process"));
                return s;
            } catch (Exception ex) {
                error = "could not start PresentMon: " + ex.Message;
                s.Dispose();
                return null;
            }
        }

        void Read() {
            try {
                var so = proc.StandardOutput;
                PresentMonCsv csv = null;
                int skipped = 0;
                string line;
                while ((line = so.ReadLine()) != null) {
                    if (csv == null) {
                        csv = PresentMonCsv.Header(line);
                        if (csv == null && ++skipped > 20) { why = "PresentMon printed no CSV header"; Log.Write(why + ": " + line); break; }
                        continue;
                    }
                    FrameRow r;
                    if (!csv.Row(line, out r)) continue;
                    var h = Row; if (h != null) h(r);
                }
            } catch (Exception ex) { if (!stopping) why = "reading PresentMon: " + ex.Message; }
            if (!stopping) {
                int code = -1;
                try { if (proc.WaitForExit(2000)) code = proc.ExitCode; } catch { }
                string e2; lock (err) e2 = err.ToString().Trim();
                if (why.Length == 0) why = "PresentMon exited (" + code + ")" + (e2.Length > 0 ? ": " + e2 : "");
                Log.Write(why);
            }
            ended = true;
        }

        /// <summary>Stop the session the way PresentMon's own tooling does, by name, and PresentMon ends with it. A kill
        /// is the fallback, and the session is stopped again after it, since a killed PresentMon leaves it running.
        /// Bounded: at most a few seconds, at exit included.</summary>
        public void Stop() {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            stopping = true;
            try {
                if (proc != null && !proc.HasExited) {
                    Etw.Stop(PresentMon.Session, false);
                    if (!proc.WaitForExit(2500)) { try { proc.Kill(); } catch { } proc.WaitForExit(1500); Etw.Stop(PresentMon.Session, true); Log.Write("PresentMon did not stop on request; killed"); }
                }
                else Etw.Stop(PresentMon.Session, true);   // it ended by itself (a crash, an antivirus, End task): its session may still be tracing
            } catch { }
            if (reader != null) reader.Join(2000);
            Dispose();
        }
        public void Dispose() {
            try { if (proc != null) proc.Dispose(); } catch { }
            proc = null;
            if (hold != null) { hold.Dispose(); hold = null; }
        }
    }

    // ================================================================== which game ================================
    /// <summary>Finding the game, the half that asks Windows: the window in front, the process behind it and the
    /// screen it is on. The deny list and the names are shared (src\Bench.cs).</summary>
    public static partial class Games {
        /// <summary>Programs that are never the game on this system alone. Windows has none beyond the shared list.</summary>
        static bool NotAGame(string stem) { return false; }

        // ---------- the window in front ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

        /// <summary>The process behind the window in front, or 0. A store app's frame belongs to
        /// ApplicationFrameHost; the game is the process of the CoreWindow inside it (OCAT does the same).</summary>
        public static int Foreground(out IntPtr root) {
            root = IntPtr.Zero;
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            root = GetAncestor(h, 2);                                   // GA_ROOT
            if (root == IntPtr.Zero) root = h;
            int pid;
            GetWindowThreadProcessId(root, out pid);
            if (Class(root) == "ApplicationFrameWindow") {
                int inner = 0;
                EnumChildWindows(root, delegate(IntPtr c, IntPtr l) {
                    if (Class(c) == "Windows.UI.Core.CoreWindow") { GetWindowThreadProcessId(c, out inner); return false; }
                    return true;
                }, IntPtr.Zero);
                if (inner > 0) pid = inner;
            }
            return pid;
        }
        static string Class(IntPtr h) { var sb = new StringBuilder(64); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        public static string Title(IntPtr h) { if (h == IntPtr.Zero) return ""; var sb = new StringBuilder(256); GetWindowText(h, sb, sb.Capacity); return sb.ToString().Trim(); }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        /// <summary>Let go of a handle Open gave.</summary>
        public static void Close(IntPtr h) { CloseHandle(h); }
        /// <summary>The process behind the handle has ended.</summary>
        public static bool Exited(IntPtr h) { return WaitForSingleObject(h, 0) == 0; }
        /// <summary>A handle we can wait on (SYNCHRONIZE) and ask the path of; IntPtr.Zero when it is gone or not ours to open.</summary>
        public static IntPtr Open(int pid) { return OpenProcess(0x00100000 | 0x1000, false, pid); }
        public static string PathOf(IntPtr h) {
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        /// <summary>Exe name without extension for a pid, or "" (the cache keeps the per-second check cheap).</summary>
        public static string Stem(int pid, Dictionary<int, string> cache) {
            string s;
            if (cache.TryGetValue(pid, out s)) return s;
            s = "";
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero) { try { string p = PathOf(h); if (p != null) s = Path.GetFileNameWithoutExtension(p); } finally { CloseHandle(h); } }
            if (cache.Count > 500) cache.Clear();
            if (s.Length > 0) cache[pid] = s;
            return s;
        }

        // ---------- the screen it is on ----------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);

        /// <summary>The monitor a window is on, in physical pixels (Ohman is per-monitor DPI aware), with its refresh rate.</summary>
        public static bool Monitor(IntPtr hwnd, out RECT rc, out int hz) {
            rc = new RECT(); hz = 0;
            try {
                IntPtr m = MonitorFromWindow(hwnd, 2);                  // MONITOR_DEFAULTTONEAREST
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (m == IntPtr.Zero || !GetMonitorInfo(m, ref mi)) return false;
                rc = mi.rcMonitor;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettings(mi.szDevice, -1, ref dm)) hz = dm.dmDisplayFrequency;     // ENUM_CURRENT_SETTINGS
                return true;
            } catch { return false; }
        }
        /// <summary>What the game draws into: its client area in physical pixels.</summary>
        public static void ClientSize(IntPtr hwnd, out int w, out int h) {
            w = h = 0;
            RECT r;
            if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out r)) { w = r.Right - r.Left; h = r.Bottom - r.Top; }
        }
    }

    // ================================================================== the machine ===============================
    /// <summary>What the share card and the run file say the run was made on. Read once, off the UI thread; never
    /// the user or the PC name.</summary>
    public static class Machine {
        public static string Cpu = "", Gpu = "", GpuDriver = "";
        static bool read;
        public static void Read() {
            if (read) return;
            read = true;
            try {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) Cpu = Tidy("" + k.GetValue("ProcessorNameString"));
            } catch { }
            try {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController")) {
                    string amd = null, amdDrv = null, other = null, otherDrv = null;
                    foreach (System.Management.ManagementObject o in q.Get()) {
                        string id = "" + o["PNPDeviceID"], name = ("" + o["Name"]).Trim(), drv = "" + o["DriverVersion"];
                        if (id.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) { Gpu = Tidy(name); GpuDriver = NvidiaDriver(drv); break; }
                        if (id.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0) {
                            // an APU's own Radeon and a Radeon dGPU: the dGPU (an RX) is the one games run on
                            if (amd == null || (amd.IndexOf("RX", StringComparison.Ordinal) < 0 && name.IndexOf("RX", StringComparison.Ordinal) >= 0)) { amd = name; amdDrv = drv; }
                        }
                        else if (other == null && name.Length > 0 && name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0) { other = name; otherDrv = drv; }
                    }
                    string pick = amd ?? other;
                    if (Gpu.Length == 0 && pick != null) { Gpu = Tidy(pick); GpuDriver = (amd != null ? amdDrv : otherDrv) ?? ""; }
                }
            } catch (Exception ex) { Log.Write("machine: " + ex.Message); }
        }
        static string Tidy(string s) {
            s = (s ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("NVIDIA GeForce ", "").Replace("AMD Radeon(TM) ", "Radeon ").Replace(" Processor", "").Replace(" GPU", "");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            int at = s.IndexOf(" with Radeon", StringComparison.OrdinalIgnoreCase);
            if (at > 0) s = s.Substring(0, at);
            return s.Trim();
        }
        /// <summary>NVIDIA's own driver number from the Windows one: 32.0.16.1088 is 610.88.</summary>
        static string NvidiaDriver(string v) {
            string digits = (v ?? "").Replace(".", "");
            if (digits.Length < 5) return v ?? "";
            string last = digits.Substring(digits.Length - 5);
            return last.Substring(0, 3).TrimStart('0') + "." + last.Substring(3);
        }
    }

    // ================================================================== sound =====================================
    /// <summary>Two short tones (Tones, in src\Bench.cs): one when measuring starts, one when it is done.
    /// The pill cannot be seen over a game in exclusive fullscreen; a sound can. Played as the app's own audio,
    /// not as a system sound, so the sound scheme cannot mute it.</summary>
    static class Cues {
        static System.Media.SoundPlayer start, done;
        public static void Start() { Play(ref start, Tones.Start); }
        public static void Done() { Play(ref done, Tones.Done); }
        static void Play(ref System.Media.SoundPlayer p, double[] notes) {
            try {
                if (p == null) { p = new System.Media.SoundPlayer(new MemoryStream(Tones.Wav(notes))); p.Load(); }
                p.Play();
            } catch (Exception ex) { Log.Write("cue: " + ex.Message); }
        }
    }
}
