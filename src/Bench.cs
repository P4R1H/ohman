// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the benchmark. Frames come from Intel PresentMon's console exe (MIT), fetched on first use and checked
// before every start; Ohman only reads what it prints. One run: wait for a game to be in front and drawing,
// warm up until the temperatures settle, measure, keep the numbers in a small file.
//
// The metric definitions are CapFrameX's defaults, so a run can be checked against CapFrameX on the same
// frames: average FPS is frames over time, the lows are R-8 percentiles of the frame times (CapFrameX "P1"
// and "P0.1", not its "1% low average"), stutter is its moving-average rule. Frame time is MsBetweenPresents,
// dropped frames included, which is CapFrameX's default.
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

    /// <summary>One CSV row of PresentMon output: one present of the process being captured.</summary>
    public struct FrameRow {
        public int Pid;
        public long Qpc;              // when the present started, QPC ticks: the clock Stopwatch.GetTimestamp reads
        public double Ms;             // MsBetweenPresents, the frame time every metric uses
        public bool Displayed;        // MsBetweenDisplayChange was a number: the frame reached the screen
        public double DisplayMs;      // MsBetweenDisplayChange, the time between this frame and the last on screen; 0 when not displayed
        public ulong Swap;            // SwapChainAddress; a run keeps only the swap chain with the most frames
        public byte Mode;             // index into PresentMon.Modes
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
        /// <summary>The PresentMode column's values, in PresentMon's own order; anything else is "Other".</summary>
        public static readonly string[] Modes = {
            "Hardware: Legacy Flip", "Hardware: Legacy Copy to front buffer", "Hardware: Independent Flip", "Composed: Flip",
            "Composed: Copy with GPU GDI", "Composed: Copy with CPU GDI", "Hardware Composed: Independent Flip", "Other" };

        public static string Dir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bench"); } }
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

    // ================================================================== frame sources =============================
    public interface IFrameSource : IDisposable {
        /// <summary>Raised on the source's own thread for every row.</summary>
        event Action<FrameRow> Row;
        /// <summary>True once the source has stopped by itself (PresentMon exited); Why says how.</summary>
        bool Ended { get; }
        string Why { get; }
        void Stop();
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
            if (iMode >= 0) { int m = Array.IndexOf(PresentMon.Modes, f[iMode + extra]); if (m >= 0) r.Mode = (byte)m; }
            return true;
        }
    }

    /// <summary>A PresentMon process streaming CSV into Ohman. Rows are read by column name: the column set
    /// changes with the flags, and the header is the only thing that says which is which.</summary>
    sealed class PresentMonSource : IFrameSource {
        public event Action<FrameRow> Row;
        public bool Ended { get { return ended; } }
        public string Why { get { return why; } }
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

    /// <summary>Frames without PresentMon, for the simulated build: a game at about 87 fps with the odd hitch,
    /// so every screen can be seen and checked without administrator rights or a game.</summary>
    sealed class SimSource : IFrameSource {
        public event Action<FrameRow> Row;
        public bool Ended { get { return false; } }
        public string Why { get { return ""; } }
        volatile bool stop;
        readonly int pid;
        public SimSource(int pid) {
            this.pid = pid;
            new Thread(Run) { IsBackground = true, Name = "sim-frames" }.Start();
        }
        void Run() {
            var rnd = new Random(7);
            while (!stop) {
                double ms = 11.5 + (rnd.NextDouble() - 0.5) * 3 + (rnd.NextDouble() < 0.004 ? 30 : 0);
                Thread.Sleep((int)ms);
                var h = Row;
                if (h != null) h(new FrameRow { Pid = pid, Qpc = Stopwatch.GetTimestamp(), Ms = ms, Displayed = true, DisplayMs = ms, Swap = 1, Mode = 2 });
            }
        }
        public void Stop() { stop = true; }
        public void Dispose() { stop = true; }
    }

    // ================================================================== the numbers ===============================
    public sealed class FrameStats {
        public int N;
        public double Sum, AvgFps, Low1 = double.NaN, Low01 = double.NaN, P50, P99, StutterPct;
        public const int MinFor1 = 1000, MinFor01 = 10000;

        /// <summary>The numbers for one run's frame times (ms, in order). Lows only with enough frames behind them:
        /// a 1% low from fewer than 1000 frames rests on fewer than ten, and CapFrameX and MangoHud both quietly
        /// report the slowest frame instead.</summary>
        public static FrameStats Of(List<double> ft) {
            var s = new FrameStats { N = ft.Count };
            if (ft.Count == 0) return s;
            foreach (double v in ft) s.Sum += v;
            s.AvgFps = 1000.0 * s.N / s.Sum;                                       // time-weighted, never the mean of per-frame fps
            var x = ft.ToArray();
            Array.Sort(x);
            s.P50 = Quantile(x, 0.50);
            s.P99 = Quantile(x, 0.99);
            if (s.N >= MinFor1) s.Low1 = 1000.0 / s.P99;
            if (s.N >= MinFor01) s.Low01 = 1000.0 / Quantile(x, 0.999);
            s.StutterPct = Stutter(ft, s.Sum);
            return s;
        }

        /// <summary>R-8, MathNet's default and so CapFrameX's: h = (N + 1/3) p + 1/3 on the sorted values.</summary>
        public static double Quantile(double[] x, double p) {
            int n = x.Length;
            if (n == 0) return double.NaN;
            double h = (n + 1.0 / 3) * p + 1.0 / 3;
            int k = (int)Math.Floor(h);
            if (k <= 0) return x[0];
            if (k >= n) return x[n - 1];
            return x[k - 1] + (h - k) * (x[k] - x[k - 1]);
        }

        /// <summary>CapFrameX's stutter time: the share of time spent in frames slower than 2.5 times the moving
        /// average of the frames before them. The window is sqrt(mean frame time) * 10 frames, and inside the
        /// average a frame more than three times its predecessor counts as its predecessor, so one hitch does not
        /// hide the next.</summary>
        public static double Stutter(List<double> ft, double sum) {
            int n = ft.Count;
            if (n == 0 || sum <= 0) return 0;
            int w = Math.Max(1, Convert.ToInt32(Math.Sqrt(sum / n) * 10));
            double run = 0, slow = 0;
            var used = new double[n];
            for (int i = 0; i < n; i++) {
                used[i] = i > 0 && ft[i] > 3 * ft[i - 1] ? ft[i - 1] : ft[i];
                run += used[i];
                if (i >= w) run -= used[i - w];
                double ma = run / Math.Min(i + 1, w);
                if (ft[i] > 2.5 * ma) slow += ft[i];
            }
            return 100.0 * slow / sum;
        }
    }

    // ================================================================== one stored run ============================
    public sealed class BenchSample {
        public double T, Fps, FtMax, CpuT, GpuT, CpuW, GpuW, CpuMhz, GpuLoad, GpuMhz;
        public int Fan1 = -1, Fan2 = -1;
        public bool PowerLimited, LimitsKnown;
        public long Q0, Q1;                     // the QPC window it covers, while the run is being built; not stored
        public BenchSample() { Fps = FtMax = CpuT = GpuT = CpuW = GpuW = CpuMhz = GpuLoad = GpuMhz = double.NaN; }
        public BenchSample Copy() { return (BenchSample)MemberwiseClone(); }
    }

    public sealed class BenchRun {
        public string Id = "", Exe = "", Game = "", App = "", Board = "", Model = "", Cpu = "", Gpu = "", GpuDriver = "", Mode = "", Settings = "", PresentMode = "", Ended = "";
        public DateTime Started;                 // UTC
        public int ModeIndex = 1, ResW, ResH, Hz, CapFps;
        public bool OnAc = true, GuardFired, FrameGen;
        public string SaveError = "";            // set when the run could not be written: it exists only in memory
        public string File = "";                 // where it was read from; not stored
        public double WarmSec, MeasureSec, MeasuredSec;
        public int Frames;
        public double FtSum, AvgFps, Low1 = double.NaN, Low01 = double.NaN, FtP50, FtP99, StutterPct;
        public double CpuTAvg = double.NaN, CpuTMax = double.NaN, GpuTAvg = double.NaN, GpuTMax = double.NaN, CpuWAvg = double.NaN, CpuWMax = double.NaN,
            GpuWAvg = double.NaN, GpuWMax = double.NaN, CpuMhzAvg = double.NaN, CpuMhzMax = double.NaN, GpuLoadAvg = double.NaN, GpuMhzAvg = double.NaN,
            Fan1Avg = double.NaN, Fan2Avg = double.NaN, FanMax = double.NaN, PowerLimitedPct = double.NaN, CpuTEnd = double.NaN, GpuTEnd = double.NaN;
        public int RpmPerLevel, FanCeiling;      // how the fan columns read: levels times this, out of this ceiling
        /// <summary>Frame-time histogram, 0.1 ms bins (bin = floor(ms * 10)), the last bin catching 100 ms and over.</summary>
        public int[] Hist = new int[0];
        public List<BenchSample> Samples = new List<BenchSample>();
        public bool HasSamples;

        public double TotalW { get { return double.IsNaN(CpuWAvg) ? GpuWAvg : double.IsNaN(GpuWAvg) ? CpuWAvg : CpuWAvg + GpuWAvg; } }
        public double FpsPerWatt { get { double w = TotalW; return w > 1 ? AvgFps / w : double.NaN; } }
        public string Res { get { return ResH > 0 ? ResH + "p" : ""; } }
        public string ResLong { get { return ResW > 0 ? ResW + "×" + ResH : ""; } }
        public DateTime Local { get { return Started.ToLocalTime(); } }
        public string Day { get { return Local.ToString("d MMM", CultureInfo.InvariantCulture); } }
        /// <summary>Things a reader needs to know before trusting the number, in the order they matter.</summary>
        public List<string> Flags {
            get {
                var f = new List<string>();
                if (Ended.Length > 0) f.Add("ended early: " + Ended);
                if (GuardFired) f.Add("thermal guard stepped in");
                if (FrameGen) f.Add("frame generation: timed on screen");
                if (CapFps > 0) f.Add("capped at " + CapFps);
                return f;
            }
        }
        /// <summary>The first of those in a word or two, for a list row.</summary>
        public string Flag { get { return Ended.Length > 0 ? "ended early" : GuardFired ? "thermal guard" : FrameGen ? "frame gen" : CapFps > 0 ? "capped " + CapFps : ""; } }
        /// <summary>Runs worth comparing: the same game at the same resolution in the same mode, both on the charger or
        /// both on battery.</summary>
        public bool SameSetup(BenchRun o) {
            return o != null && string.Equals(o.Exe, Exe, StringComparison.OrdinalIgnoreCase) && o.ModeIndex == ModeIndex && o.ResW == ResW && o.ResH == ResH && o.OnAc == OnAc;
        }
    }

    /// <summary>Runs on disk: bench\runs\yyyyMMdd-HHmmss-exe.run, one small text file each. Summary keys first, so the
    /// history list reads only up to [hist]; then the frame-time histogram and one row a second.</summary>
    public static class RunStore {
        public static string Dir { get { return Path.Combine(PresentMon.Dir, "runs"); } }
        const int Keep = 200;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", Inv); }
        static double D(string s) { double v; return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN; }
        static int I(string s) { int v; return int.TryParse(s, NumberStyles.Integer, Inv, out v) ? v : 0; }
        static string Clean(string s) { return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        public static void Save(BenchRun r) {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            Action<string, string> kv = delegate(string k, string v) { sb.Append(k).Append('=').Append(Clean(v)).Append('\n'); };
            kv("v", "1"); kv("id", r.Id); kv("exe", r.Exe); kv("game", r.Game);
            kv("started", r.Started.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv));
            kv("app", r.App); kv("board", r.Board); kv("model", r.Model); kv("cpu", r.Cpu); kv("gpu", r.Gpu); kv("gpudriver", r.GpuDriver);
            kv("mode", r.Mode); kv("modeindex", r.ModeIndex.ToString(Inv)); kv("ac", r.OnAc ? "1" : "0");
            kv("res", r.ResW + "x" + r.ResH); kv("hz", r.Hz.ToString(Inv)); kv("present", r.PresentMode); kv("settings", r.Settings);
            kv("warm_s", N(r.WarmSec)); kv("measure_s", N(r.MeasureSec)); kv("measured_s", N(r.MeasuredSec)); kv("ended", r.Ended);
            kv("frames", r.Frames.ToString(Inv)); kv("ft_sum", N(r.FtSum)); kv("avg_fps", N(r.AvgFps)); kv("low1", N(r.Low1)); kv("low01", N(r.Low01));
            kv("ft_p50", N(r.FtP50)); kv("ft_p99", N(r.FtP99)); kv("stutter_pct", N(r.StutterPct)); kv("cap", r.CapFps.ToString(Inv)); kv("guard", r.GuardFired ? "1" : "0"); kv("fg", r.FrameGen ? "1" : "0");
            kv("cpu_t_avg", N(r.CpuTAvg)); kv("cpu_t_max", N(r.CpuTMax)); kv("cpu_t_end", N(r.CpuTEnd)); kv("gpu_t_avg", N(r.GpuTAvg)); kv("gpu_t_max", N(r.GpuTMax)); kv("gpu_t_end", N(r.GpuTEnd));
            kv("cpu_w_avg", N(r.CpuWAvg)); kv("cpu_w_max", N(r.CpuWMax)); kv("gpu_w_avg", N(r.GpuWAvg)); kv("gpu_w_max", N(r.GpuWMax));
            kv("cpu_mhz_avg", N(r.CpuMhzAvg)); kv("cpu_mhz_max", N(r.CpuMhzMax)); kv("gpu_load_avg", N(r.GpuLoadAvg)); kv("gpu_mhz_avg", N(r.GpuMhzAvg));
            kv("fan1_avg", N(r.Fan1Avg)); kv("fan2_avg", N(r.Fan2Avg)); kv("fan_max", N(r.FanMax)); kv("rpm_per_level", r.RpmPerLevel.ToString(Inv)); kv("fan_ceiling", r.FanCeiling.ToString(Inv));
            kv("power_limited_pct", N(r.PowerLimitedPct));
            sb.Append("[hist]\n");
            bool first = true;
            for (int i = 0; i < r.Hist.Length; i++) if (r.Hist[i] > 0) { if (!first) sb.Append(','); sb.Append(i).Append(':').Append(r.Hist[i]); first = false; }
            sb.Append("\n[samples]\nt,fps,ftmax,cput,gput,cpuw,gpuw,cpumhz,gpuload,gpumhz,fan1,fan2,plim\n");
            foreach (var s in r.Samples)
                sb.Append(N(s.T)).Append(',').Append(N(s.Fps)).Append(',').Append(N(s.FtMax)).Append(',').Append(N(s.CpuT)).Append(',').Append(N(s.GpuT)).Append(',')
                  .Append(N(s.CpuW)).Append(',').Append(N(s.GpuW)).Append(',').Append(N(s.CpuMhz)).Append(',').Append(N(s.GpuLoad)).Append(',').Append(N(s.GpuMhz)).Append(',')
                  .Append(s.Fan1 < 0 ? "" : s.Fan1.ToString(Inv)).Append(',').Append(s.Fan2 < 0 ? "" : s.Fan2.ToString(Inv)).Append(',').Append(!s.LimitsKnown ? "" : s.PowerLimited ? "1" : "0").Append('\n');
            string path = Path.Combine(Dir, Safe(r.Id) + ".run"), tmp = path + ".tmp";
            try {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
                catch (IOException) { File.Copy(tmp, path, true); }
            } finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            r.File = path;
            Log.Write("benchmark saved: " + r.Id + " " + r.Frames + " frames, " + N(r.AvgFps) + " fps avg");
            Prune();
        }

        static string Safe(string id) { var sb = new StringBuilder(); foreach (char c in id ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'); return sb.ToString(); }

        static void Prune() {
            try {
                foreach (string t in Directory.GetFiles(Dir, "*.run.tmp")) try { File.Delete(t); } catch { }   // left by a crash mid-save
                var files = Directory.GetFiles(Dir, "*.run");
                if (files.Length <= Keep) return;
                Array.Sort(files, StringComparer.Ordinal);                          // names are sortable UTC stamps
                for (int i = 0; i < files.Length - Keep; i++) try { File.Delete(files[i]); } catch { }
            } catch { }
        }

        /// <summary>Every run, newest first, summaries only.</summary>
        public static List<BenchRun> List() {
            var list = new List<BenchRun>();
            try {
                if (!Directory.Exists(Dir)) return list;
                var files = Directory.GetFiles(Dir, "*.run");
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = files.Length - 1; i >= 0; i--) { var r = Load(files[i], false); if (r != null) list.Add(r); }
            } catch (Exception ex) { Log.Write("runs: " + ex.Message); }
            return list;
        }
        public static BenchRun Full(BenchRun summary) {
            if (summary == null || summary.HasSamples || summary.File.Length == 0) return summary;
            return Load(summary.File, true) ?? summary;
        }

        static BenchRun Load(string path, bool full) {
            try {
                var r = new BenchRun();
                string section = "";
                bool header = false;
                foreach (string raw in File.ReadLines(path)) {
                    string line = raw.TrimEnd('\r');
                    if (line.StartsWith("[", StringComparison.Ordinal)) { section = line; header = false; if (!full) break; continue; }
                    if (section == "[hist]") {
                        var bins = new List<int>();
                        foreach (string p in line.Split(',')) {
                            int c = p.IndexOf(':');
                            if (c < 1) continue;
                            int b = I(p.Substring(0, c)), n = I(p.Substring(c + 1));
                            if (b < 0 || b > 1000 || n <= 0) continue;
                            while (bins.Count <= b) bins.Add(0);
                            bins[b] += n;
                        }
                        r.Hist = bins.ToArray();
                        continue;
                    }
                    if (section == "[samples]") {
                        if (!header) { header = true; continue; }
                        var f = line.Split(',');
                        if (f.Length < 12) continue;
                        r.Samples.Add(new BenchSample {
                            T = D(f[0]), Fps = D(f[1]), FtMax = D(f[2]), CpuT = D(f[3]), GpuT = D(f[4]), CpuW = D(f[5]), GpuW = D(f[6]), CpuMhz = D(f[7]), GpuLoad = D(f[8]), GpuMhz = D(f[9]),
                            Fan1 = f[10].Length > 0 ? I(f[10]) : -1, Fan2 = f[11].Length > 0 ? I(f[11]) : -1, PowerLimited = f.Length > 12 && f[12] == "1", LimitsKnown = f.Length > 12 && f[12].Length > 0
                        });
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    switch (k) {
                        case "id": r.Id = v; break;
                        case "exe": r.Exe = v; break;
                        case "game": r.Game = v; break;
                        case "started": { DateTime t; if (DateTime.TryParse(v, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) r.Started = t; break; }
                        case "app": r.App = v; break;
                        case "board": r.Board = v; break;
                        case "model": r.Model = v; break;
                        case "cpu": r.Cpu = v; break;
                        case "gpu": r.Gpu = v; break;
                        case "gpudriver": r.GpuDriver = v; break;
                        case "mode": r.Mode = v; break;
                        case "modeindex": r.ModeIndex = Math.Max(0, Math.Min(2, I(v))); break;
                        case "ac": r.OnAc = v != "0"; break;
                        case "res": { int x = v.IndexOf('x'); if (x > 0) { r.ResW = I(v.Substring(0, x)); r.ResH = I(v.Substring(x + 1)); } break; }
                        case "hz": r.Hz = I(v); break;
                        case "present": r.PresentMode = v; break;
                        case "settings": r.Settings = v; break;
                        case "warm_s": r.WarmSec = D(v); break;
                        case "measure_s": r.MeasureSec = D(v); break;
                        case "measured_s": r.MeasuredSec = D(v); break;
                        case "ended": r.Ended = v; break;
                        case "frames": r.Frames = I(v); break;
                        case "ft_sum": r.FtSum = D(v); break;
                        case "avg_fps": r.AvgFps = D(v); break;
                        case "low1": r.Low1 = D(v); break;
                        case "low01": r.Low01 = D(v); break;
                        case "ft_p50": r.FtP50 = D(v); break;
                        case "ft_p99": r.FtP99 = D(v); break;
                        case "stutter_pct": r.StutterPct = D(v); break;
                        case "cap": r.CapFps = I(v); break;
                        case "guard": r.GuardFired = v == "1"; break;
                        case "fg": r.FrameGen = v == "1"; break;
                        case "cpu_t_avg": r.CpuTAvg = D(v); break;
                        case "cpu_t_max": r.CpuTMax = D(v); break;
                        case "cpu_t_end": r.CpuTEnd = D(v); break;
                        case "gpu_t_avg": r.GpuTAvg = D(v); break;
                        case "gpu_t_max": r.GpuTMax = D(v); break;
                        case "gpu_t_end": r.GpuTEnd = D(v); break;
                        case "cpu_w_avg": r.CpuWAvg = D(v); break;
                        case "cpu_w_max": r.CpuWMax = D(v); break;
                        case "gpu_w_avg": r.GpuWAvg = D(v); break;
                        case "gpu_w_max": r.GpuWMax = D(v); break;
                        case "cpu_mhz_avg": r.CpuMhzAvg = D(v); break;
                        case "cpu_mhz_max": r.CpuMhzMax = D(v); break;
                        case "gpu_load_avg": r.GpuLoadAvg = D(v); break;
                        case "gpu_mhz_avg": r.GpuMhzAvg = D(v); break;
                        case "fan1_avg": r.Fan1Avg = D(v); break;
                        case "fan2_avg": r.Fan2Avg = D(v); break;
                        case "fan_max": r.FanMax = D(v); break;
                        case "rpm_per_level": r.RpmPerLevel = I(v); break;
                        case "fan_ceiling": r.FanCeiling = I(v); break;
                        case "power_limited_pct": r.PowerLimitedPct = D(v); break;
                    }
                }
                // The file's own name is its id: an id line that says otherwise is ignored, so nothing read from a run
                // can point a later save anywhere else.
                r.Id = Path.GetFileNameWithoutExtension(path);
                r.File = path;
                if (r.Frames <= 0 || !(r.AvgFps > 0) || double.IsInfinity(r.AvgFps)) return null;
                r.HasSamples = full;
                return r;
            } catch (Exception ex) { Log.Write("run " + Path.GetFileName(path) + ": " + ex.Message); return null; }
        }

        /// <summary>A fan's average level in the unit the page shows it in: rpm, or percent of top speed on boards
        /// that report levels without a speed.</summary>
        public static double FanValue(BenchRun r, double level) {
            if (double.IsNaN(level) || level < 0) return double.NaN;
            return r.RpmPerLevel > 0 ? level * r.RpmPerLevel : 100.0 * level / Math.Max(1, r.FanCeiling);
        }

        /// <summary>Every run as one CSV row, for a spreadsheet. Where the list separator is ';' (most of Europe) the
        /// file uses it and the local decimal comma, the way Excel opens a CSV there; elsewhere commas and points.</summary>
        public static string Csv(List<BenchRun> runs) {
            CultureInfo cur = CultureInfo.CurrentCulture;
            bool local = cur.TextInfo.ListSeparator == ";" && cur.NumberFormat.NumberDecimalSeparator == ",";
            char sep = local ? ';' : ',';
            CultureInfo nc = local ? cur : Inv;
            Func<double, string> n = delegate(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", nc); };
            Func<string, string> q = delegate(string s) { s = s ?? ""; return s.IndexOfAny(new[] { sep, '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; };
            string[] head = { "date", "game", "exe", "mode", "ac", "resolution", "hz", "settings", "measured_s", "frames", "avg_fps", "low1_fps", "low01_fps", "ft_p50_ms", "ft_p99_ms", "stutter_pct",
                "cpu_t_avg", "cpu_t_max", "gpu_t_avg", "gpu_t_max", "cpu_w", "gpu_w", "fan1", "fan2", "fan_unit", "flags" };
            var sb = new StringBuilder(string.Join(sep.ToString(), head)).Append('\n');
            foreach (var r in runs) {
                var c = new List<string> {
                    r.Local.ToString("yyyy-MM-dd HH:mm", Inv), q(r.Game), q(r.Exe), r.Mode, r.OnAc ? "AC" : "battery", r.ResLong.Replace('×', 'x'), r.Hz.ToString(Inv), q(r.Settings),
                    n(r.MeasuredSec), r.Frames.ToString(Inv), n(r.AvgFps), n(r.Low1), n(r.Low01), n(r.FtP50), n(r.FtP99), n(r.StutterPct),
                    n(r.CpuTAvg), n(r.CpuTMax), n(r.GpuTAvg), n(r.GpuTMax), n(r.CpuWAvg), n(r.GpuWAvg),
                    n(Math.Round(FanValue(r, r.Fan1Avg))), n(Math.Round(FanValue(r, r.Fan2Avg))), r.RpmPerLevel > 0 ? "rpm" : "%", q(string.Join("; ", r.Flags.ToArray()))
                };
                sb.Append(string.Join(sep.ToString(), c.ToArray())).Append('\n');
            }
            return sb.ToString();
        }
    }

    // ================================================================== which game ================================
    /// <summary>Finding the game: the process that owns the window in front and is drawing frames. Launchers,
    /// browsers, overlays and the shell draw frames too, so they are never candidates.</summary>
    public static class Games {
        // A curated union of OCAT's deny list, PresentMon's TargetBlockList.txt and CapFrameX's ignore list:
        // exe names, lower case, no extension. Real games that ended up on those lists (Minecraft's javaw,
        // VRChat, Roblox) are left off on purpose.
        static readonly HashSet<string> Deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "dwm", "explorer", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "searchui", "minisearchhost",
            "textinputhost", "lockapp", "logonui", "csrss", "svchost", "dllhost", "conhost", "taskhostw", "backgroundtaskhost", "taskmgr", "systemsettings",
            "screenclippinghost", "screensketch", "snippingtool", "credentialuibroker", "gamebar", "gamebar_widget", "gamebarftserver", "desktopoverlayhost",
            "windowsterminal", "openconsole", "cmd", "powershell", "pwsh", "notepad", "mspaint", "photos", "video.ui",
            "chrome", "msedge", "msedgewebview2", "microsoftedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc", "comet",
            "steam", "steamwebhelper", "steamtours", "epicgameslauncher", "epicwebhelper", "epiconlineservicesuihelper", "eadesktop", "eabackgroundservice", "origin",
            "battle.net", ".battle.net", "galaxyclient", "galaxyclient helper", "gog galaxy notifications renderer", "upc", "uplay", "uplaywebcore", "ubisoftgamelauncher", "ubisoftconnect",
            "riot client", "riotclientux", "riotclientuxrender", "leagueclientux", "leagueclientuxrender", "bethesdanetlauncher", "rsi launcher", "starcitizen_launcher",
            "paradox launcher", "xboxapp", "xboxpcapp", "gamingservices", "playnite.desktopapp", "playnite.fullscreenapp", "launchbox", "skif", "launcher", "setup", "updater",
            "nvidia overlay", "nvidia share", "nvidia app", "nvidia geforce experience", "nvcplui", "radeonsoftware", "radeonsettings", "amdrsserv", "intelgraphicssoftware", "arccontrol",
            "rtss", "msiafterburner", "obs64", "obs32", "streamlabs obs", "medal", "medalencoder", "discord", "wgc_renderer", "wgc_renderer_host", "losslessscaling", "magpie",
            "wallpaper32", "wallpaper64", "livelywpf", "slack", "teams", "ms-teams", "telegram", "whatsapp", "spotify", "code", "devenv",
            "armourycrate", "icue", "lghub", "steelseriesgg", "nzxt cam", "fancontrol", "hwinfo64", "omen command center", "omencommandcenterbackground", "hp.omen.omencommandcenter",
            "presentmon", "capframex", "ocat", "ohman", "vrcompositor", "vrmonitor", "oculusclient"
        };
        public static bool Denied(string stem) {
            if (string.IsNullOrEmpty(stem)) return true;
            if (Deny.Contains(stem)) return true;
            return stem.StartsWith("presentmon", StringComparison.OrdinalIgnoreCase);
        }

        static Dictionary<string, string> names;
        /// <summary>The name people know a game by, from CapFrameX's list (third_party\CapFrameX), else the exe's
        /// own description, else its window title, else the exe name.</summary>
        public static string Name(string stem, string exePath, IntPtr hwnd) {
            if (names == null) names = LoadNames();
            string n;
            if (names.TryGetValue(stem.ToLowerInvariant(), out n)) return n;
            try {
                string d = exePath == null ? null : FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                d = d == null ? "" : d.Trim();
                if (d.Length > 1 && !d.Equals(stem, StringComparison.OrdinalIgnoreCase) && d.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("bootstrap", StringComparison.OrdinalIgnoreCase) < 0) return Cut(d);
            } catch { }
            string title = Title(hwnd);
            if (title.Length > 1) return Cut(title);
            return stem;
        }
        static string Cut(string s) { s = s.Trim(); return s.Length > 40 ? s.Substring(0, 40).TrimEnd() : s; }
        static Dictionary<string, string> LoadNames() {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                using (var st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Ohman.games.txt"))
                using (var sr = new StreamReader(st, Encoding.UTF8)) {
                    string line;
                    while ((line = sr.ReadLine()) != null) {
                        if (line.Length == 0 || line[0] == '#') continue;
                        int t = line.IndexOf('\t');
                        if (t > 0 && !d.ContainsKey(line.Substring(0, t))) d[line.Substring(0, t)] = line.Substring(t + 1).Trim();
                    }
                }
            } catch (Exception ex) { Log.Write("game names: " + ex.Message); }
            return d;
        }

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
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
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
    /// <summary>Two short tones, made here rather than shipped: one when measuring starts, one when it is done.
    /// The pill cannot be seen over a game in exclusive fullscreen; a sound can. Played as the app's own audio,
    /// not as a system sound, so the sound scheme cannot mute it.</summary>
    static class Cues {
        static System.Media.SoundPlayer start, done;
        public static void Start() { Play(ref start, new[] { 660.0, 880.0 }); }
        public static void Done() { Play(ref done, new[] { 523.25, 659.25, 783.99 }); }
        static void Play(ref System.Media.SoundPlayer p, double[] notes) {
            try {
                if (p == null) { p = new System.Media.SoundPlayer(new MemoryStream(Wav(notes))); p.Load(); }
                p.Play();
            } catch (Exception ex) { Log.Write("cue: " + ex.Message); }
        }
        static byte[] Wav(double[] notes) {
            const int rate = 22050;
            int per = rate * 110 / 1000, gap = rate * 25 / 1000, n = notes.Length * (per + gap);
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            foreach (double f in notes) {
                for (int i = 0; i < per; i++) {
                    double t = i / (double)rate, env = Math.Min(1, i / (rate * 0.006)) * Math.Exp(-t * 14);
                    w.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * 0.32 * short.MaxValue));
                }
                for (int i = 0; i < gap; i++) w.Write((short)0);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    // ================================================================== the run ===================================
    public enum BenchPhase { Idle, Fetching, Waiting, Warming, Countdown, Measuring, Finishing }

    /// <summary>One benchmark at a time. Its own thread ticks four times a second and moves it on; frames arrive on
    /// the source's thread. Everything the UI reads is set under the lock and copied out by Snapshot. Only the bench
    /// thread starts or stops the frame source, except Dispose, which first stops the bench thread.</summary>
    public sealed class Bench : IDisposable {
        public static readonly string[] WarmNames = { "auto", "2:00", "skip" }, MeasureNames = { "0:30", "1:00", "3:00" };
        public static readonly int[] MeasureSecs = { 30, 60, 180 };
        const int WarmCap = 300, WarmFixed = 120, WarmMin = 60, CountdownSec = 4, LockSec = 3;   // the pill leaves 2 s before the count ends
        const int MinRows = 10;                         // frames a second the game in front must draw, three seconds running, to be locked on
        const double SettledSlope = 1.0;                // degrees a minute, either way
        const int WaitLimitSec = 600, PausedLimitSec = 300;
        const double FinishTail = 2.5;                  // PresentMon prints a row once the frame is on screen; late rows arrive up to ~2 s after
        const double ResumeSettle = 1.0;                // seconds after coming back to the game before frames count again

        readonly Engine E;
        readonly bool sim;
        readonly object sync = new object();
        Thread worker;
        readonly ManualResetEvent wake = new ManualResetEvent(false);
        volatile bool disposed;
        public event Action Changed;                    // something visible moved; raised on the bench thread
        public event Action<BenchRun> Finished;         // a run was kept; raised on the bench thread
        public event Action<string> EndedEarly;         // a run ended without a result, and why

        // ---- read by the UI (under sync) ----
        public BenchPhase Phase = BenchPhase.Idle;
        public string Error = "";                       // why the last Start or download could not go ahead
        public bool Paused;                             // the game is not in front
        public string Game = "", Exe = "";
        public int Pid;
        public IntPtr Window;
        public double WarmSec, MeasuredSec, CountLeft, WarmSlope = double.NaN, LiveFps = double.NaN;
        public int WarmChoice, MeasureChoice;
        public int MeasureTarget { get { return MeasureSecs[Math.Max(0, Math.Min(2, MeasureChoice))]; } }
        public readonly List<double> WarmTemps = new List<double>();        // CPU temperature once a second while warming, for the graph
        public readonly List<BenchSample> Live = new List<BenchSample>();    // the samples measured so far
        public bool GuardFired;

        // ---- inputs ----
        SensorSnapshot sensors;                         // the latest, set by the UI from Sensors.Updated
        public void Feed(SensorSnapshot s) { lock (sync) sensors = s; }

        // ---- run state, bench thread only unless noted ----
        IFrameSource source;
        IntPtr proc = IntPtr.Zero;
        int startMode;
        DateTime startedUtc;
        static readonly int OwnPid = Process.GetCurrentProcess().Id;
        bool startOnAc;
        double phaseSince, lastTick;                                            // seconds on the monotonic clock (Mono), never the wall clock
        readonly Dictionary<int, int> rowsByPid = new Dictionary<int, int>();   // waiting: rows each process drew this second (under sync)
        readonly Dictionary<int, string> stems = new Dictionary<int, string>(); // pid -> exe name, for one wait only (pids are reused)
        int candidate, streak;
        double secondStart;
        readonly List<FrameRow> frames = new List<FrameRow>();                  // the game's rows from the countdown on (under sync)
        int framesThisSecond;                                                   // under sync
        readonly List<long[]> spans = new List<long[]>();                       // measured spans, QPC [start, end) (under sync)
        long openAt;                                                            // start of the span in progress, 0 = none (written under sync)
        long resumeAt;                                                          // the game came back; counting starts here
        double closedSec;                                                       // seconds in closed spans
        double pausedSince = -1, finishAt;                                     // -1 = not paused
        int fan1 = -1, fan2 = -1;
        double fanRead = -10;
        long sampleFrom;                                                        // QPC where the next sample's window starts
        double sampleT;
        string keepReason;
        string waitLog = "";
        double waitLogAt;
        volatile bool quiet;                                                    // finishing at exit: no sound
        int resW, resH, hz;                                                     // what the game draws at, the last good reading while it was in front
        /// <summary>Seconds on QPC: a clock change (daylight saving, a time sync) must not stretch or skip a phase.</summary>
        static double Mono() { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }

        public Bench(Engine e, bool simulated) {
            E = e;
            sim = simulated;
            WarmChoice = Math.Max(0, Math.Min(2, e.S.BenchWarm));
            MeasureChoice = Math.Max(0, Math.Min(2, e.S.BenchMeasure));
            // A session left behind by a crash keeps tracing every present on the machine until a reboot. Ours has a
            // name of its own, so ending it at start is safe and costs one call.
            if (!sim) ThreadPool.QueueUserWorkItem(delegate { Etw.Stop(PresentMon.Session, true); });
        }

        /// <summary>A run is under way. Not a download: the hotkey and the sensor rate only care about runs.</summary>
        public bool Running { get { lock (sync) return Phase != BenchPhase.Idle && Phase != BenchPhase.Fetching; } }
        /// <summary>Reading the sensors: warming up (CPU temperature) to the end of measuring. Waiting reads nothing.</summary>
        public bool Sampling { get { lock (sync) return Phase == BenchPhase.Warming || Phase == BenchPhase.Countdown || Phase == BenchPhase.Measuring || Phase == BenchPhase.Finishing; } }
        public bool ToolReady { get { return (sim && !poseNoTool) || PresentMon.Downloaded; } }
        bool poseNoTool;

        /// <summary>Screenshot aid for the simulated build (--page bench --bench-state x): put the run in one state
        /// with made-up numbers, without waiting for it. Never on real hardware.</summary>
        public void Pose(string state) {
            if (!sim) return;
            var rnd = new Random(11);
            lock (sync) {
                Reset();
                poseNoTool = state == "first";
                if (state == "first" || state == "ready" || state.StartsWith("result") || state == "runs" || state.StartsWith("share")) return;
                Game = "Cyberpunk 2077"; Exe = "Cyberpunk2077"; Pid = OwnPid; MeasureChoice = 2;
                if (state == "waiting") { Phase = BenchPhase.Waiting; Game = ""; return; }
                for (int i = 0; i < 138; i++) WarmTemps.Add(48 + 36 * (1 - Math.Exp(-i / 40.0)) + (rnd.NextDouble() - 0.5) * 1.4);
                WarmSec = 138; LiveFps = 87;
                if (state == "warming") { Phase = BenchPhase.Warming; WarmSlope = 0.6; return; }
                WarmSec = 220; Phase = BenchPhase.Measuring; Paused = state == "paused";
                long q = Stopwatch.GetTimestamp();
                spans.Add(new[] { q - 1, q + 1 });
                for (int i = 0; i < 72; i++) {
                    var b = new BenchSample { T = i, Fps = 87 + (rnd.NextDouble() - 0.5) * 11 - (rnd.NextDouble() < 0.04 ? 20 : 0), CpuT = 84 + (rnd.NextDouble() - 0.5) * 5, GpuT = 76 + rnd.NextDouble() * 2,
                        CpuW = 38 + (rnd.NextDouble() - 0.5) * 6, GpuW = 105 + (rnd.NextDouble() - 0.5) * 6, CpuMhz = 4200 + (rnd.NextDouble() - 0.5) * 200, GpuLoad = 96 + (rnd.NextDouble() - 0.5) * 6, GpuMhz = 2100, Fan1 = 49, Fan2 = 51 };
                    Live.Add(b);
                    for (int k = 0; k < 87; k++) frames.Add(new FrameRow { Pid = OwnPid, Qpc = q, Ms = 11.5 + (rnd.NextDouble() - 0.5) * 3 + (rnd.NextDouble() < 0.004 ? 30 : 0), DisplayMs = 11.5, Displayed = true, Swap = 1, Mode = 2 });
                }
                MeasuredSec = 72;
            }
        }

        public void SetChoices(int warm, int measure) { SetChoices(warm, measure, true); }
        /// <summary>persist false: this run only (the self-test), the owner's saved choices untouched.</summary>
        public void SetChoices(int warm, int measure, bool persist) {
            lock (sync) { WarmChoice = warm; MeasureChoice = measure; }
            if (persist) {
                E.S.BenchWarm = warm; E.S.BenchMeasure = measure;
                try { E.S.Save(); } catch { }
            }
            Raise();
        }

        /// <summary>Download PresentMon on a thread of its own. The UI shows Fetching, then Idle with Error on failure.</summary>
        public void Fetch() {
            lock (sync) { if (Phase != BenchPhase.Idle) return; Phase = BenchPhase.Fetching; Error = ""; }
            Raise();
            new Thread(delegate() {
                string err = "";
                try { PresentMon.Fetch(); } catch (Exception ex) { err = ex.Message; Log.Write("PresentMon download failed: " + ex.Message); }
                lock (sync) { if (Phase == BenchPhase.Fetching) { Phase = BenchPhase.Idle; Error = err.Length > 0 ? "Download failed: " + err : ""; } }
                Raise();
            }) { IsBackground = true, Name = "presentmon-fetch" }.Start();
        }

        /// <summary>Start waiting for a game. False (and Error set) when it cannot.</summary>
        public bool Start() {
            lock (sync) {
                if (Phase != BenchPhase.Idle || disposed) return false;
                Error = "";
                if (!sim && !PresentMon.Downloaded) { Error = "PresentMon is not downloaded"; return false; }
                Reset();
                Phase = BenchPhase.Waiting;
                phaseSince = Mono();
            }
            EnsureWorker();
            // The source is started on the bench thread like every later change to it, so no two threads ever
            // hold it at once.
            Post(delegate {
                string err;
                if (!Open(0, out err)) { lock (sync) { if (Phase == BenchPhase.Waiting) { Phase = BenchPhase.Idle; Error = err ?? ""; } } var h = EndedEarly; if (h != null) try { h("capture failed"); } catch { } Raise(); return; }
                Log.Write("benchmark: waiting for a game (warm up " + WarmNames[WarmChoice] + ", measure " + MeasureNames[MeasureChoice] + ")");
            });
            new Thread(Machine.Read) { IsBackground = true, Name = "machine-info" }.Start();
            Raise();
            return true;
        }

        /// <summary>The Stop button and the hotkey: a run past half its measuring time is kept, anything earlier is dropped.</summary>
        public void Stop() { Post(delegate { End("stopped", true); }); }
        /// <summary>Warm-up is long enough: go to the countdown now.</summary>
        public void SkipWarm() { Post(delegate { lock (sync) { if (Phase == BenchPhase.Warming) { Phase = BenchPhase.Countdown; CountLeft = CountdownSec; phaseSince = Mono(); } } Raise(); }); }

        readonly Queue<Action> posted = new Queue<Action>();
        void Post(Action a) { lock (posted) posted.Enqueue(a); EnsureWorker(); wake.Set(); }

        void EnsureWorker() {
            lock (posted) {
                if (worker != null || disposed) return;
                worker = new Thread(Loop) { IsBackground = true, Name = "bench" };
                worker.Start();
            }
        }

        void Reset() {
            Pid = 0; Game = Exe = ""; Window = IntPtr.Zero; Paused = false;
            WarmSec = MeasuredSec = 0; CountLeft = 0; WarmSlope = LiveFps = double.NaN;
            WarmTemps.Clear(); Live.Clear(); frames.Clear(); spans.Clear(); rowsByPid.Clear(); stems.Clear();
            openAt = resumeAt = 0; closedSec = 0; candidate = streak = 0; framesThisSecond = 0;
            GuardFired = false; pausedSince = -1; sampleT = 0; keepReason = null; resW = resH = hz = 0; sampleFrom = 0;
            secondStart = lastTick = Mono();
        }

        bool Open(int pid, out string err) {
            err = null;
            CloseSource();
            if (disposed) return false;
            IFrameSource s = sim ? (IFrameSource)new SimSource(SimPid) : PresentMonSource.Start(pid, out err);
            if (s == null) return false;
            s.Row += OnRow;
            source = s;
            return true;
        }
        void CloseSource() {
            var s = Interlocked.Exchange(ref source, null);
            if (s == null) return;
            s.Row -= OnRow;
            try { s.Stop(); } catch { }
        }
        void CloseProc() {
            IntPtr h = Interlocked.Exchange(ref proc, IntPtr.Zero);
            if (h != IntPtr.Zero) Games.CloseHandle(h);
        }
        static int SimPid { get { return OwnPid; } }
        void SetOpen(long v) { lock (sync) openAt = v; }

        void OnRow(FrameRow r) {
            lock (sync) {
                if (Phase == BenchPhase.Waiting) { int n; rowsByPid.TryGetValue(r.Pid, out n); rowsByPid[r.Pid] = n + 1; return; }
                if (Pid == 0 || r.Pid != Pid) return;
                if (Phase == BenchPhase.Warming || Phase == BenchPhase.Countdown || Phase == BenchPhase.Measuring || Phase == BenchPhase.Finishing) {
                    framesThisSecond++;
                    // Only what can fall in a measured span: an open one, or the one just closed (rows arrive late).
                    // Paused minutes of a game still rendering behind the desktop would otherwise pile up unused.
                    if (Phase != BenchPhase.Warming && Phase != BenchPhase.Countdown && (openAt != 0 || (resumeAt != 0 && r.Qpc >= resumeAt) || (spans.Count > 0 && r.Qpc < spans[spans.Count - 1][1]))) frames.Add(r);
                }
            }
        }

        void Loop() {
            while (!disposed) {
                wake.Reset();
                for (;;) {
                    Action a = null;
                    lock (posted) if (posted.Count > 0) a = posted.Dequeue();
                    if (a == null || disposed) break;
                    try { a(); } catch (Exception ex) { Log.Write("bench: " + ex); }
                }
                if (disposed) break;
                try { Tick(); } catch (Exception ex) { Log.Write("bench tick: " + ex); End("error: " + ex.Message, false); }
                BenchPhase ph;
                lock (sync) ph = Phase;
                // Idle: nothing to tick, so no wakeups at all until Start, Stop or Dispose posts or sets the event
                wake.WaitOne(ph == BenchPhase.Idle || ph == BenchPhase.Fetching ? Timeout.Infinite : 250);
            }
            // Measuring was complete and only its tail was running out: that run is whole, so keep it
            bool finishing;
            lock (sync) finishing = Phase == BenchPhase.Finishing;
            if (finishing) { quiet = true; try { Finish(keepReason); } catch (Exception ex) { Log.Write("benchmark at exit: " + ex.Message); } }
        }

        void Tick() {
            BenchPhase ph;
            lock (sync) ph = Phase;
            if (ph == BenchPhase.Idle || ph == BenchPhase.Fetching) return;
            double now = Mono();
            bool second = now - secondStart >= 1.0;
            double dt = Math.Max(0, Math.Min(2, now - lastTick));
            lastTick = now;
            var src = source;

            if (ph == BenchPhase.Waiting) {
                if (src == null) return;                                         // the start is still on its way through the queue
                // A capture that died says why now, instead of ten minutes of "waiting for a game".
                if (src.Ended) { End("capture failed: " + src.Why, false); return; }
                if (now - phaseSince > WaitLimitSec) { End("no game found", false); return; }
                if (!second) return;
                secondStart = now;
                Dictionary<int, int> counts;
                lock (sync) { counts = new Dictionary<int, int>(rowsByPid); rowsByPid.Clear(); }
                IntPtr root = IntPtr.Zero;
                int fg = sim ? SimPid : Games.Foreground(out root);
                int rows;
                bool drawing = fg > 0 && counts.TryGetValue(fg, out rows) && rows >= MinRows;
                bool eligible = sim || (fg > 0 && fg != OwnPid && !Games.Denied(Games.Stem(fg, stems)));
                if (drawing && eligible) { streak = fg == candidate ? streak + 1 : 1; candidate = fg; }
                else { streak = 0; candidate = 0; }
                // What the wait sees, when it changes: the window in front and whatever is drawing. Without this, "it
                // never started" leaves nothing to go on.
                if (now - waitLogAt >= 5) {
                    var top = new List<KeyValuePair<int, int>>(counts);
                    top.Sort(delegate(KeyValuePair<int, int> x, KeyValuePair<int, int> y) { return y.Value.CompareTo(x.Value); });
                    var sb = new StringBuilder();
                    for (int i = 0; i < top.Count && i < 4; i++) sb.Append(i == 0 ? "" : ", ").Append(sim ? "sim" : Games.Stem(top[i].Key, stems)).Append(' ').Append(top[i].Value);
                    int fr; counts.TryGetValue(fg, out fr);
                    string line = "front " + (fg > 0 ? (sim ? "sim" : Games.Stem(fg, stems)) + " " + fr + "/s" + (eligible ? "" : " (not a game)") : "none") + "; drawing: " + (sb.Length > 0 ? sb.ToString() : "nothing");
                    if (line != waitLog) { Log.Write("benchmark waiting: " + line); waitLog = line; }
                    waitLogAt = now;
                }
                if (streak >= LockSec) Lock(fg, root);
                return;
            }

            // ---- a game is locked from here on ----
            if (ph == BenchPhase.Finishing) {
                if (now >= finishAt) Finish(keepReason);
                return;
            }
            if (!sim && proc != IntPtr.Zero && Games.WaitForSingleObject(proc, 0) == 0) { End("game closed", true); return; }
            if (src != null && src.Ended) { End("capture stopped", true); return; }
            if (E.ModeIndex != startMode) { End("mode changed", false); return; }
            SensorSnapshot snap;
            lock (sync) snap = sensors;
            if (OnAc() != startOnAc) { End(startOnAc ? "charger unplugged" : "charger plugged in", false); return; }

            IntPtr r2 = IntPtr.Zero;
            int fgNow = sim ? Pid : Games.Foreground(out r2);
            bool front = fgNow == Pid;
            long q = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
            if (front && second && !sim) ReadDisplay();

            // pause and resume
            if (!front) {
                if (pausedSince < 0) {
                    pausedSince = now;
                    if (ph == BenchPhase.Measuring && openAt != 0) {
                        // never past the target: a pause that lands on the last tick does not add frames beyond it
                        long end = Math.Min(q, openAt + (long)((MeasureTarget - closedSec) * f));
                        if (end - sampleFrom >= 0.5 * f) Sample(snap, sampleFrom, end);
                        lock (sync) { spans.Add(new[] { openAt, end }); }
                        closedSec += (end - openAt) / (double)f;
                        SetOpen(0);
                    }
                    resumeAt = 0;
                    lock (sync) Paused = true;
                    Raise();
                } else if (now - pausedSince > PausedLimitSec) { End("paused too long", true); return; }
            } else if (pausedSince >= 0) {
                pausedSince = -1;
                resumeAt = q + (long)(ResumeSettle * f);
                // Coming back into the countdown gets the same settle as coming back into the measuring: the switch
                // back (and an exclusive-fullscreen mode change) must not be the first thing measured.
                if (ph == BenchPhase.Countdown) lock (sync) CountLeft = Math.Max(CountLeft, ResumeSettle);
                lock (sync) Paused = false;
                Raise();
            }

            if (ph == BenchPhase.Warming) {
                if (front) { lock (sync) WarmSec += dt; }
                if (second) {
                    secondStart = now;
                    double fps;
                    lock (sync) { fps = framesThisSecond; framesThisSecond = 0; LiveFps = fps; }
                    if (front && snap != null) lock (sync) { WarmTemps.Add(snap.CpuTemp); if (WarmTemps.Count > 600) WarmTemps.RemoveAt(0); }
                    double slope = Slope();
                    lock (sync) WarmSlope = slope;
                    Raise();
                }
                bool done;
                lock (sync) {
                    // Settled means still, either way: a CPU cooling fast after a loading screen is not settled either.
                    done = WarmChoice == 2 || (WarmChoice == 1 && WarmSec >= WarmFixed)
                        || (WarmChoice == 0 && (WarmSec >= WarmCap || (WarmSec >= WarmMin && !double.IsNaN(WarmSlope) && Math.Abs(WarmSlope) < SettledSlope) || (WarmSec >= 2 * WarmMin && double.IsNaN(WarmSlope))));
                    if (done) { Phase = BenchPhase.Countdown; CountLeft = CountdownSec; }
                }
                if (done) { Log.Write("benchmark: warmed up after " + WarmSec.ToString("0") + " s" + (double.IsNaN(WarmSlope) ? "" : ", CPU " + WarmSlope.ToString("+0.0;-0.0") + " deg/min")); Raise(); }
                return;
            }

            if (ph == BenchPhase.Countdown) {
                if (front) lock (sync) CountLeft -= dt;
                if (second) { secondStart = now; lock (sync) { LiveFps = framesThisSecond; framesThisSecond = 0; } Raise(); }
                bool go;
                lock (sync) go = CountLeft <= 0 && front;
                if (go) {
                    if (!sim) ReadDisplay();
                    lock (sync) { Phase = BenchPhase.Measuring; openAt = q; frames.Clear(); framesThisSecond = 0; }
                    secondStart = now; sampleT = 0; sampleFrom = q;
                    Cues.Start();
                    Log.Write("benchmark: measuring " + MeasureNames[MeasureChoice] + " of " + Game + (resW > 0 ? " at " + resW + "x" + resH : "") + (hz > 0 ? " " + hz + " Hz" : ""));
                    Raise();
                }
                return;
            }

            // ---- measuring ----
            if (front && openAt == 0 && resumeAt != 0 && q >= resumeAt) { SetOpen(resumeAt); sampleFrom = resumeAt; resumeAt = 0; lock (sync) framesThisSecond = 0; secondStart = now; second = false; }
            if (E.GuardActive && openAt != 0) lock (sync) GuardFired = true;
            double measured = closedSec + (openAt != 0 ? (q - openAt) / (double)f : 0);
            lock (sync) MeasuredSec = Math.Min(measured, MeasureTarget);
            if (second) {
                secondStart = now;
                long until = openAt != 0 ? Math.Min(q, openAt + (long)((MeasureTarget - closedSec) * f)) : 0;
                if (openAt != 0 && until - sampleFrom >= 0.5 * f) { Sample(snap, sampleFrom, until); Raise(); }
            }
            if (measured >= MeasureTarget - 1e-6) {
                if (openAt != 0) {
                    long end = openAt + (long)((MeasureTarget - closedSec) * f);
                    if (end - sampleFrom >= 0.5 * f) Sample(snap, sampleFrom, end);
                    lock (sync) spans.Add(new[] { openAt, end });
                    SetOpen(0);
                }
                closedSec = MeasureTarget;
                lock (sync) { MeasuredSec = MeasureTarget; Phase = BenchPhase.Finishing; }
                finishAt = now + FinishTail;
                keepReason = null;
                Raise();
            }
        }

        /// <summary>The power line now, read directly: a run can start before the first sensor snapshot exists.</summary>
        static bool OnAc() {
            try { return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline; } catch { return true; }
        }

        /// <summary>The game's size and its screen's refresh rate, while it is in front: read later, the window may be
        /// gone or minimised, and an exclusive-fullscreen game's own mode with it.</summary>
        void ReadDisplay() {
            IntPtr w;
            lock (sync) w = Window;
            if (w == IntPtr.Zero) return;
            int cw, ch; Games.ClientSize(w, out cw, out ch);
            if (cw > 0 && ch > 0) { resW = cw; resH = ch; }
            Games.RECT mon; int r;
            if (Games.Monitor(w, out mon, out r) && r > 0) hz = r;
        }

        /// <summary>One sample for the QPC window [q0, q1): the sensors as they are now, the fans every two seconds, and
        /// a live FPS from rows that arrived meanwhile (Build replaces it with the frames drawn inside the window).</summary>
        void Sample(SensorSnapshot s, long q0, long q1) {
            double span = (q1 - q0) / (double)Stopwatch.Frequency;
            // Every BIOS call runs firmware code (see the #72 note in Engine), and it would run inside the measured time.
            // The guard tick reads the fans every 10 s anyway; a read of our own only when nothing recent is there.
            int[] seen = E.RecentFanLevels(12);
            if (seen != null) { fan1 = seen[0]; fan2 = seen[1]; }
            else if (Mono() - fanRead >= 10) {
                fanRead = Mono();
                try { int[] lv = E.Hw.GetFanLevels(); if (lv != null && lv.Length >= 2) { fan1 = lv[0]; fan2 = lv[1]; E.NoteFanLevels(lv); } } catch { }
            }
            var b = new BenchSample { Q0 = q0, Q1 = q1 };
            sampleFrom = q1;
            lock (sync) {
                b.T = sampleT;
                b.Fps = framesThisSecond / span;
                framesThisSecond = 0;
                LiveFps = b.Fps;
            }
            sampleT += span;
            if (s != null) {
                b.CpuT = s.CpuTemp; b.GpuT = s.GpuTemp; b.CpuW = s.CpuWatts; b.GpuW = s.GpuWatts; b.CpuMhz = s.CpuMhz; b.GpuLoad = s.GpuLoad; b.GpuMhz = s.GpuMhz;
                b.LimitsKnown = s.GpuLimits != ulong.MaxValue;
                b.PowerLimited = b.LimitsKnown && (s.GpuLimits & 0x84) != 0;      // SwPowerCap | HwPowerBrakeSlowdown
            }
            b.Fan1 = fan1; b.Fan2 = fan2;
            lock (sync) Live.Add(b);
        }

        /// <summary>CPU temperature trend over the last minute of warm-up, degrees a minute, by least squares.
        /// NaN until there is a minute of it, or without a temperature to read.</summary>
        double Slope() {
            double[] y;
            lock (sync) { if (WarmTemps.Count < 60) return double.NaN; y = WarmTemps.GetRange(WarmTemps.Count - 60, 60).ToArray(); }
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int i = 0; i < y.Length; i++) { if (double.IsNaN(y[i])) continue; sx += i; sy += y[i]; sxx += i * (double)i; sxy += i * y[i]; n++; }
            if (n < 30) return double.NaN;
            double den = n * sxx - sx * sx;
            return den <= 0 ? double.NaN : (n * sxy - sx * sy) / den * 60;
        }

        void Lock(int pid, IntPtr root) {
            IntPtr h = sim ? IntPtr.Zero : Games.Open(pid);
            if (!sim && h == IntPtr.Zero) { Log.Write("benchmark: could not open pid " + pid + "; still waiting"); streak = 0; return; }
            // The name comes from the handle just opened, not from the wait's cache: that is this process for certain.
            string path = sim ? null : Games.PathOf(h);
            string stem = sim ? "Cyberpunk2077" : path != null ? Path.GetFileNameWithoutExtension(path) : Games.Stem(pid, stems);
            if (!sim && Games.Denied(stem)) { Games.CloseHandle(h); streak = 0; return; }
            string name = sim ? "Cyberpunk 2077" : Games.Name(stem, path, root);
            proc = h;
            lock (sync) {
                Pid = pid; Exe = stem; Game = name; Window = root; Phase = WarmChoice == 2 ? BenchPhase.Countdown : BenchPhase.Warming; CountLeft = CountdownSec; phaseSince = Mono(); frames.Clear();
                // The run is what happens from here: a mode or a charger changed while waiting is the setup it runs in.
                startMode = E.ModeIndex; startOnAc = OnAc(); startedUtc = DateTime.UtcNow;
            }
            Log.Write("benchmark: locked on " + stem + " (pid " + pid + "), \"" + name + "\"");
            string err;
            // PresentMon again, filtered to this process in the kernel: cheaper, and it ends when the game does.
            if (!Open(pid, out err)) { End("capture failed: " + err, false); return; }
            // The restart took a moment the warm-up and the countdown must not count.
            lastTick = secondStart = Mono();
            if (!sim) ReadDisplay();
            Raise();
        }

        /// <summary>End the run. keep: a run that measured at least half its time is kept with the reason on it.</summary>
        void End(string reason, bool keep) {
            BenchPhase ph;
            double measured;
            lock (sync) { ph = Phase; measured = MeasuredSec; }
            if (ph == BenchPhase.Idle || ph == BenchPhase.Fetching) return;
            if (ph == BenchPhase.Finishing) return;                                      // measuring is complete; its tail runs out on its own
            if (ph == BenchPhase.Measuring && openAt != 0) {
                long q = Stopwatch.GetTimestamp(), end = Math.Min(q, openAt + (long)((MeasureTarget - closedSec) * Stopwatch.Frequency));
                lock (sync) spans.Add(new[] { openAt, end });
                closedSec += (end - openAt) / (double)Stopwatch.Frequency;
                lock (sync) { MeasuredSec = closedSec; measured = closedSec; }
                SetOpen(0);
            }
            Log.Write("benchmark: " + reason + " (" + ph + ", " + measured.ToString("0") + " s measured)");
            if (keep && ph == BenchPhase.Measuring && measured >= MeasureTarget / 2.0) {
                // the frames already on their way still count; PresentMon is stopped by Finish
                lock (sync) Phase = BenchPhase.Finishing;
                finishAt = Mono() + (reason == "game closed" ? 0.5 : FinishTail);
                // A run that already has its full time is a whole run, whatever ended it on the last tick.
                keepReason = measured >= MeasureTarget - 0.25 ? null : reason;
                Raise();
                return;
            }
            CloseSource();
            CloseProc();
            // a capture that failed keeps its reason for the page (the hash, the signature, the start); others need none
            lock (sync) { Phase = BenchPhase.Idle; Error = reason.StartsWith("capture failed: ", StringComparison.Ordinal) ? reason.Substring(16) : ""; frames.Clear(); frames.TrimExcess(); }
            int colon = reason.IndexOf(':');
            var h = EndedEarly; if (h != null) try { h(colon > 0 ? reason.Substring(0, colon) : reason); } catch { }
            Raise();
        }

        void Finish(string endedEarly) {
            CloseSource();
            CloseProc();
            BenchRun run = null;
            try { run = Build(endedEarly ?? ""); } catch (Exception ex) { Log.Write("benchmark result: " + ex); }
            lock (sync) Phase = BenchPhase.Idle;
            lock (sync) { frames.Clear(); frames.TrimExcess(); }          // a long run's frames are megabytes; the result has what it needs
            if (run == null) { var h0 = EndedEarly; if (h0 != null) try { h0("too few frames"); } catch { } Raise(); return; }
            try { RunStore.Save(run); } catch (Exception ex) { run.SaveError = ex.Message; Log.Write("benchmark save: " + ex.Message); }
            if (!quiet) Cues.Done();
            var h = Finished; if (h != null) try { h(run); } catch { }
            Raise();
        }

        BenchRun Build(string ended) {
            List<FrameRow> all;
            List<long[]> sp;
            List<BenchSample> samples;
            lock (sync) { all = new List<FrameRow>(frames); sp = new List<long[]>(spans); samples = Live.ConvertAll(x => x.Copy()); }
            sp.RemoveAll(delegate(long[] s) { return s[1] <= s[0]; });
            // only the measured spans, only the swap chain with the most frames (an overlay or a second API's swap
            // chain would otherwise interleave its own frame times), in time order
            var inSpan = new List<FrameRow>();
            foreach (var r in all) foreach (var s in sp) if (r.Qpc >= s[0] && r.Qpc < s[1]) { inSpan.Add(r); break; }
            var bySwap = new Dictionary<ulong, int>();
            foreach (var r in inSpan) { int n; bySwap.TryGetValue(r.Swap, out n); bySwap[r.Swap] = n + 1; }
            ulong main = 0; int best = -1;
            foreach (var kv in bySwap) if (kv.Value > best) { best = kv.Value; main = kv.Key; }
            var kept = new List<FrameRow>();
            var modes = new int[PresentMon.Modes.Length];
            inSpan.Sort(delegate(FrameRow a, FrameRow b) { return a.Qpc.CompareTo(b.Qpc); });
            foreach (var r in inSpan) if (r.Swap == main) { kept.Add(r); modes[r.Mode]++; }
            if (kept.Count < 30) { Log.Write("benchmark: only " + kept.Count + " frames in the measured time; nothing kept"); return null; }
            // Frame generation: the generated frame and the real one are presented back to back and spaced out on the
            // display, so the present-to-present times alternate short and long while the screen sees an even
            // cadence. Lows and stutter from the presents would be artefacts of that, so those runs are measured on
            // when frames reached the screen instead, and say so.
            var ft = new List<double>();
            foreach (var r in kept) ft.Add(r.Ms);
            bool fg = Alternates(ft);
            if (fg) {
                var disp = new List<double>();
                foreach (var r in kept) if (r.DisplayMs > 0) disp.Add(r.DisplayMs);
                if (disp.Count >= 30 && !Alternates(disp)) { ft = disp; Log.Write("benchmark: present times alternate (frame generation); measured on display times"); }
                else fg = false;
            }
            var st = FrameStats.Of(ft);
            var run = new BenchRun {
                Exe = Exe, Game = Game, Started = startedUtc, App = Program.Version, Board = E.Board, Model = E.Model,
                Cpu = Machine.Cpu, Gpu = Machine.Gpu, GpuDriver = Machine.GpuDriver, Mode = Engine.ModeNames[startMode], ModeIndex = startMode, OnAc = startOnAc,
                WarmSec = WarmSec, MeasureSec = MeasureTarget, MeasuredSec = MeasuredSec, Ended = ended, FrameGen = fg,
                Frames = st.N, FtSum = st.Sum, AvgFps = st.AvgFps, Low1 = st.Low1, Low01 = st.Low01, FtP50 = st.P50, FtP99 = st.P99, StutterPct = st.StutterPct,
                GuardFired = GuardFired, Samples = samples, HasSamples = true, RpmPerLevel = E.P.RpmPerLevel, FanCeiling = E.P.Curve.Ceiling,
                ResW = resW, ResH = resH, Hz = hz
            };
            run.Id = run.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Safe(Exe);
            int mi = 0; for (int i = 1; i < modes.Length; i++) if (modes[i] > modes[mi]) mi = i;
            run.PresentMode = PresentMon.Modes[mi];
            if (sim) { run.ResW = 2560; run.ResH = 1600; run.Hz = 165; }
            string set;
            if ((set = E.S.GameLine(Exe)) != null) run.Settings = set;
            // histogram, 0.1 ms bins
            var hist = new int[1001];
            foreach (double v in ft) hist[Math.Min(1000, (int)(v * 10))]++;
            int last = 1000; while (last > 0 && hist[last] == 0) last--;
            run.Hist = new int[last + 1]; Array.Copy(hist, run.Hist, last + 1);
            run.CapFps = Cap(kept, run.Hz);
            PerSample(run, kept);
            Summarise(run);
            return run;
        }
        static string Safe(string s) { var sb = new StringBuilder(); foreach (char c in s ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'); return sb.Length == 0 ? "game" : sb.ToString(); }

        /// <summary>Short and long frames taking turns: a third or more of neighbouring pairs more than 2.5x apart.</summary>
        static bool Alternates(List<double> ft) {
            if (ft.Count < 200) return false;
            int odd = 0;
            for (int i = 1; i < ft.Count; i++) { double a = ft[i - 1], b = ft[i]; if (Math.Max(a, b) > 2.5 * Math.Min(a, b)) odd++; }
            return odd > (ft.Count - 1) / 3;
        }

        /// <summary>Each sample's FPS and slowest frame, counted from the kept frames drawn inside its own window, so the
        /// chart lines up with the temperatures and watts taken at the same moment, and with the stored average.</summary>
        static void PerSample(BenchRun run, List<FrameRow> kept) {
            double f = Stopwatch.Frequency;
            foreach (var b in run.Samples) {
                if (b.Q1 <= b.Q0) { b.Fps = double.NaN; b.FtMax = double.NaN; continue; }
                int lo = 0, hi = kept.Count;
                while (lo < hi) { int mid = (lo + hi) / 2; if (kept[mid].Qpc < b.Q0) lo = mid + 1; else hi = mid; }
                int n = 0; double m = 0;
                for (int j = lo; j < kept.Count && kept[j].Qpc < b.Q1; j++) { n++; if (kept[j].Ms > m) m = kept[j].Ms; }
                b.Fps = n / ((b.Q1 - b.Q0) / f);
                b.FtMax = n > 0 ? m : double.NaN;
            }
        }

        /// <summary>A frame cap or vsync, judged on when frames reached the screen (a vsynced game's presents jitter;
        /// its display intervals do not): the median sits on a usual limit, the middle half is tight around it,
        /// and almost nothing is faster.</summary>
        static int Cap(List<FrameRow> kept, int hz) {
            var d = new List<double>();
            foreach (var r in kept) if (r.DisplayMs > 0) d.Add(r.DisplayMs);
            if (d.Count < 200) { d.Clear(); foreach (var r in kept) d.Add(r.Ms); }
            var x = d.ToArray(); Array.Sort(x);
            double med = FrameStats.Quantile(x, 0.5), q1 = FrameStats.Quantile(x, 0.25), q3 = FrameStats.Quantile(x, 0.75);
            if (!(med > 0)) return 0;
            double fps = 1000.0 / med;
            var targets = new List<int> { 30, 40, 45, 48, 50, 60, 72, 75, 90, 100, 120, 144, 165, 180, 200, 240, 360 };
            if (hz > 0 && !targets.Contains(hz)) targets.Add(hz);
            // Under a refresh rate: 3 below it (the usual G-Sync advice, 141 on 144 Hz) and the cap NVIDIA Reflex sets
            // with V-Sync and G-Sync on, hz - hz*hz/3600 (157 on 165 Hz).
            foreach (int b0 in new[] { hz, 60, 120, 144, 165, 240 }) {
                if (b0 <= 30) continue;
                foreach (int t in new[] { b0 - 3, (int)Math.Round(b0 - b0 * b0 / 3600.0) }) if (!targets.Contains(t)) targets.Add(t);
            }
            int best = 0; double gap = double.MaxValue;
            foreach (int t in targets) { double g = Math.Abs(fps - t); if (g < gap) { gap = g; best = t; } }
            if (gap > best * 0.005) return 0;
            if ((q3 - q1) > med * 0.03) return 0;
            double faster = 1000.0 / (best * 1.02);
            int over = 0; foreach (double v in x) if (v < faster) over++;
            return over < x.Length * 0.01 ? best : 0;
        }

        static void Summarise(BenchRun r) {
            Func<Func<BenchSample, double>, double> avg = delegate(Func<BenchSample, double> g) { double s = 0; int n = 0; foreach (var b in r.Samples) { double v = g(b); if (!double.IsNaN(v)) { s += v; n++; } } return n == 0 ? double.NaN : s / n; };
            Func<Func<BenchSample, double>, double> max = delegate(Func<BenchSample, double> g) { double m = double.NaN; foreach (var b in r.Samples) { double v = g(b); if (!double.IsNaN(v) && (double.IsNaN(m) || v > m)) m = v; } return m; };
            Func<Func<BenchSample, double>, double> end = delegate(Func<BenchSample, double> g) { for (int i = r.Samples.Count - 1; i >= 0; i--) { double v = g(r.Samples[i]); if (!double.IsNaN(v)) return v; } return double.NaN; };
            r.CpuTAvg = avg(b => b.CpuT); r.CpuTMax = max(b => b.CpuT); r.CpuTEnd = end(b => b.CpuT);
            r.GpuTAvg = avg(b => b.GpuT); r.GpuTMax = max(b => b.GpuT); r.GpuTEnd = end(b => b.GpuT);
            r.CpuWAvg = avg(b => b.CpuW); r.CpuWMax = max(b => b.CpuW);
            r.GpuWAvg = avg(b => b.GpuW); r.GpuWMax = max(b => b.GpuW);
            r.CpuMhzAvg = avg(b => b.CpuMhz); r.CpuMhzMax = max(b => b.CpuMhz);
            r.GpuLoadAvg = avg(b => b.GpuLoad); r.GpuMhzAvg = avg(b => b.GpuMhz);
            r.Fan1Avg = avg(b => b.Fan1 < 0 ? double.NaN : (double)b.Fan1); r.Fan2Avg = avg(b => b.Fan2 < 0 ? double.NaN : (double)b.Fan2);
            r.FanMax = max(b => Math.Max(b.Fan1, b.Fan2) < 0 ? double.NaN : (double)Math.Max(b.Fan1, b.Fan2));
            // only samples where the driver said what limits the clock count: unknown is not "never limited"
            int lim = 0, known = 0; foreach (var b in r.Samples) if (b.LimitsKnown) { known++; if (b.PowerLimited) lim++; }
            r.PowerLimitedPct = known > 0 ? 100.0 * lim / known : double.NaN;
        }

        /// <summary>The frame times measured so far, inside the measured spans, for the live view.</summary>
        public List<double> FrameTimes() {
            var ft = new List<double>();
            lock (sync) {
                foreach (var r in frames) {
                    bool inside = openAt != 0 && r.Qpc >= openAt;
                    if (!inside) foreach (var s in spans) if (r.Qpc >= s[0] && r.Qpc < s[1]) { inside = true; break; }
                    if (inside) ft.Add(r.Ms);
                }
            }
            return ft;
        }

        /// <summary>Copy of the visible state, taken under the lock.</summary>
        public BenchView Snapshot() {
            lock (sync) {
                return new BenchView {
                    Phase = Phase, Error = Error, Paused = Paused, Game = Game, Exe = Exe, Window = Window, WarmSec = WarmSec, MeasuredSec = MeasuredSec, CountLeft = CountLeft,
                    WarmSlope = WarmSlope, LiveFps = LiveFps, WarmChoice = WarmChoice, MeasureChoice = MeasureChoice, MeasureTarget = MeasureTarget,
                    WarmTemps = WarmTemps.ToArray(), Live = Live.ToArray(), Frames = frames.Count, GuardFired = GuardFired
                };
            }
        }

        void Raise() { var h = Changed; if (h != null) try { h(); } catch (Exception ex) { Log.Write("bench changed: " + ex.Message); } }

        /// <summary>Stop the bench thread first, so nothing starts a capture behind this, then stop what it had.</summary>
        public void Dispose() {
            if (disposed) return;
            try { lock (sync) { if (Phase != BenchPhase.Idle && Phase != BenchPhase.Fetching && Phase != BenchPhase.Finishing) { Log.Write("benchmark: Ohman is closing; run dropped"); Phase = BenchPhase.Idle; } } } catch { }
            disposed = true;
            wake.Set();
            Thread w;
            lock (posted) w = worker;
            if (w != null && w != Thread.CurrentThread) w.Join(4000);
            CloseSource();
            CloseProc();
        }
    }

    public sealed class BenchView {
        public BenchPhase Phase;
        public string Error, Game, Exe;
        public bool Paused, GuardFired;
        public IntPtr Window;
        public double WarmSec, MeasuredSec, CountLeft, WarmSlope, LiveFps;
        public int WarmChoice, MeasureChoice, MeasureTarget, Frames;
        public double[] WarmTemps;
        public BenchSample[] Live;
    }
}
