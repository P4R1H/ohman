// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the sensors that do not need the firmware. src\win\Sensors.cs is the same class for
// Windows, and this one keeps its shape on purpose: one worker thread, one snapshot per tick, the same medians,
// the same care about not waking a sleeping GPU, the same rule for an ACPI zone that never moves. Only where the
// numbers come from differs:
//   CPU temperature   the CPU's own register through the driver when there is one (CpuSource), else the kernel's
//                     reading of the same sensor (hwmon: coretemp "Package id 0", k10temp Tctl/Tdie, zenpower),
//                     else the hottest ACPI thermal zone
//   CPU load          /proc/stat
//   CPU clock         the driver's APERF/MPERF ratio, else cpufreq's scaling_cur_freq averaged over the cores
//   package power     the driver's energy counter, else powercap RAPL (root-only since 5.10: through the helper)
//   NVIDIA            NVML in process (libnvidia-ml.so.1), the same calls Windows makes on nvml.dll
//   AMD graphics      the amdgpu hwmon node and gpu_busy_percent
//   battery, AC       /sys/class/power_supply
// All reads are best-effort and never throw.
//
// UNTESTED on a real laptop. Checked in WSL only, where most of these files do not exist: the paths and units
// are from the kernel's documentation (hwmon sysfs-interface, powercap, cpufreq, power_supply class).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohman {

    public sealed class Sensors : IDisposable {
        // Asking NVML anything wakes the discrete GPU. On a hybrid laptop, asking every few seconds stops it ever
        // reaching its deepest idle state. So: ask at the normal rate while the GPU is doing something, and back
        // off hard once it goes quiet. (The same schedule as Windows; see src\win\Sensors.cs for the history.)
        int gpuQuiet;
        DateTime lastNv = DateTime.MinValue;
        public volatile bool SkipGpu;                 // set by the UI when the machine is running on the iGPU alone
        public volatile bool Bench;                   // a benchmark is running: the GPU is read on every tick
        const int GpuIdleMs = 30000, GpuStaleMs = 45000;
        const int GpuTrayMs = 15000;
        readonly object sync = new object();
        SensorSnapshot last = new SensorSnapshot();
        Thread worker;
        volatile bool stop;
        bool disposed;
        volatile int intervalMs = 2000;
        readonly ManualResetEvent wake = new ManualResetEvent(false);
        public event Action<SensorSnapshot> Updated;
        /// <summary>Where the CPU's own registers are, when there is a driver. Asked on every tick rather than
        /// held, because the engine opens and closes them.</summary>
        public Func<CpuRegisters> CpuSource;
        int cpuPollFailures;
        bool disagreementLogged;
        // An ACPI zone that is not the CPU: the same rule as Windows. Called dead only on its own evidence: it has
        // not changed by a tenth of a degree since this process started, through a minute's worth of ticks with
        // the CPU at 40 % or more, and the number it sits on is under 40 C.
        public int DeadZoneHint;
        const double ZoneBusyLoad = 40, ZoneBusySeconds = 60;
        const int ZoneColdBelow = 3132;                   // 40 C in tenths of a kelvin
        int zoneFirst;
        bool zoneMoved;
        double zoneBusy;
        DateTime zoneTick = DateTime.MinValue;

        // what was found at start
        string packageTemp;                               // hwmon file of the CPU's own sensor, or null
        string[] zones = new string[0];                   // ACPI thermal zone temp files
        string[] freqFiles = new string[0];
        string raplFile;                                  // energy_uj when this process may read it
        ulong raplMax;
        bool raplViaHelper;
        string nvidiaPower;                               // the dGPU's runtime_status file
        string amdHwmon, amdBusy;                         // an AMD GPU's hwmon directory and its gpu_busy_percent
        string battery;
        ulong statBusy, statTotal;
        bool statKnown;
        ulong energyLast;
        DateTime energyAt = DateTime.MinValue;

        public Sensors() { }

        static string Text(string path) { try { return File.ReadAllText(path).Trim(); } catch { return ""; } }
        static bool Number(string path, out double v) { return double.TryParse(Text(path), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        /// <summary>The ACPI thermal zones' temperature files. "acpitz" is the firmware's zones; the others
        /// (x86_pkg_temp, iwlwifi, nvme...) are other drivers' and are either covered by hwmon or not the CPU.</summary>
        static string[] AcpiZones() {
            var list = new List<string>();
            try {
                const string root = "/sys/class/thermal";
                if (Directory.Exists(root)) {
                    string[] dirs = Directory.GetDirectories(root, "thermal_zone*");
                    Array.Sort(dirs, StringComparer.Ordinal);
                    foreach (string d in dirs) if (Text(Path.Combine(d, "type")) == "acpitz") list.Add(Path.Combine(d, "temp"));
                }
            } catch { }
            return list.ToArray();
        }

        /// <summary>The hottest zone in kelvin, or NaN, through the same 283..398 K window Windows uses.</summary>
        static double HottestZone(string[] files) {
            double hot = double.NaN;
            foreach (string f in files) {
                double milli;
                if (!Number(f, out milli)) continue;
                double k = milli / 1000.0 + 273.15;
                if (k >= 283 && k <= 398 && (double.IsNaN(hot) || k > hot)) hot = k;
            }
            return hot;
        }

        /// <summary>The hwmon file that is the CPU package's own sensor, or null. Intel's coretemp labels it
        /// "Package id 0"; AMD's k10temp calls it Tctl (and Tdie where the two differ, which is the real one);
        /// zenpower calls it Tdie.</summary>
        static string FindPackageTemp(out string what) {
            what = null;
            try {
                const string root = "/sys/class/hwmon";
                if (!Directory.Exists(root)) return null;
                string[] dirs = Directory.GetDirectories(root);
                Array.Sort(dirs, StringComparer.Ordinal);
                foreach (string d in dirs) {
                    string name = Text(Path.Combine(d, "name"));
                    if (name != "coretemp" && name != "k10temp" && name != "zenpower") continue;
                    string best = null, bestLabel = null;
                    foreach (string f in Directory.GetFiles(d, "temp*_label")) {
                        string label = Text(f);
                        bool pkg = label.StartsWith("Package id", StringComparison.Ordinal) || label == "Tdie" || label == "Tctl";
                        if (!pkg) continue;
                        if (best == null || label == "Tdie") { best = f.Substring(0, f.Length - "_label".Length) + "_input"; bestLabel = label; }
                    }
                    if (best == null && File.Exists(Path.Combine(d, "temp1_input"))) { best = Path.Combine(d, "temp1_input"); bestLabel = "temp1"; }
                    if (best != null) { what = name + " " + bestLabel; return best; }
                }
            } catch { }
            return null;
        }

        void Init() {
            string what;
            packageTemp = FindPackageTemp(out what);
            zones = AcpiZones();
            Log.Write("cpu temperature sources: " + (packageTemp != null ? what : "no hwmon package sensor") + ", " + zones.Length + " ACPI zone(s)");
            try {
                var f = new List<string>();
                const string root = "/sys/devices/system/cpu";
                foreach (string d in Directory.GetDirectories(root, "cpu*")) {
                    string p = Path.Combine(d, "cpufreq", "scaling_cur_freq");
                    if (File.Exists(p)) f.Add(p);
                }
                freqFiles = f.ToArray();
            } catch { }
            // Package power: the file itself when we may read it (root), else the helper reads it for us.
            try {
                const string root = "/sys/class/powercap";
                if (Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root)) {
                        if (!Text(Path.Combine(d, "name")).StartsWith("package", StringComparison.OrdinalIgnoreCase)) continue;
                        string e = Path.Combine(d, "energy_uj");
                        ulong v;
                        if (ulong.TryParse(Text(e), out v)) { raplFile = e; ulong.TryParse(Text(Path.Combine(d, "max_energy_range_uj")), out raplMax); }
                        else raplViaHelper = true;
                        break;
                    }
            } catch { }
            try {
                const string root = "/sys/bus/pci/devices";
                if (Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root)) {
                        if (!Text(Path.Combine(d, "class")).StartsWith("0x03", StringComparison.Ordinal)) continue;       // display controllers
                        string vendor = Text(Path.Combine(d, "vendor"));
                        if (vendor == "0x10de" && nvidiaPower == null) nvidiaPower = Path.Combine(d, "power", "runtime_status");
                        if (vendor == "0x1002") {
                            // With two AMD GPUs the discrete one is the one the firmware did not boot the screen on.
                            bool boot = Text(Path.Combine(d, "boot_vga")) == "1";
                            string hw = null;
                            try { string h = Path.Combine(d, "hwmon"); if (Directory.Exists(h)) foreach (string x in Directory.GetDirectories(h)) { hw = x; break; } } catch { }
                            if (hw != null && (amdHwmon == null || !boot)) { amdHwmon = hw; amdBusy = Path.Combine(d, "gpu_busy_percent"); }
                        }
                    }
            } catch { }
            // NVML is tried whether or not an NVIDIA device was seen on the PCI bus: the library being there is the
            // better evidence, and it is the only evidence under WSL. The PCI device is what says "asleep".
            if (amdHwmon != null) Log.Write("gpu stats: amdgpu (" + amdHwmon + ")" + (nvidiaPower != null ? ", after NVML" : ""));
            try {
                const string root = "/sys/class/power_supply";
                if (Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root))
                        // scope "Device" is a mouse's or a headset's battery, not the laptop's
                        if (Text(Path.Combine(d, "type")) == "Battery" && Text(Path.Combine(d, "scope")) != "Device" && File.Exists(Path.Combine(d, "capacity"))) { battery = d; break; }
            } catch { }
        }

        public void SetInterval(int ms) {
            int now = Math.Max(500, ms);
            if (now == intervalMs) return;
            intervalMs = now;
            if (now < 3000) wake.Set();        // going faster: take the next reading now rather than after the old wait
        }

        public void Start() {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "sensors" };
            worker.Start();
        }

        double CpuLoad() {
            try {
                string line;
                using (var r = new StreamReader("/proc/stat")) line = r.ReadLine();
                if (line == null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return double.NaN;
                string[] p = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                // cpu user nice system idle iowait irq softirq steal ...; idle and iowait are the time not working
                ulong total = 0, idle = 0;
                for (int i = 1; i < p.Length && i <= 8; i++) {
                    ulong v;
                    if (!ulong.TryParse(p[i], out v)) return double.NaN;
                    total += v;
                    if (i == 4 || i == 5) idle += v;
                }
                ulong busy = total - idle;
                double load = double.NaN;
                if (statKnown && total > statTotal) load = Math.Max(0, Math.Min(100, 100.0 * (busy - statBusy) / (total - statTotal)));
                statBusy = busy; statTotal = total; statKnown = true;
                return load;
            } catch { return double.NaN; }
        }

        double CpuMhz() {
            double sum = 0; int n = 0;
            foreach (string f in freqFiles) { double khz; if (Number(f, out khz) && khz > 0) { sum += khz; n++; } }
            return n > 0 ? sum / n / 1000.0 : double.NaN;
        }

        /// <summary>Package watts from the powercap energy counter: the difference over the interval.</summary>
        double RaplWatts() {
            ulong now, max = raplMax;
            if (raplFile != null) { if (!ulong.TryParse(Text(raplFile), out now)) return double.NaN; }
            else if (raplViaHelper) { if (!HelperClient.Energy(out now, out max)) { raplViaHelper = Route.Now == Route.Via.None; return double.NaN; } }
            else return double.NaN;
            DateTime t = DateTime.UtcNow;
            double w = double.NaN;
            if (energyAt != DateTime.MinValue) {
                double secs = (t - energyAt).TotalSeconds;
                ulong delta = now >= energyLast ? now - energyLast : (max > 0 ? max - energyLast + now : 0);
                if (secs > 0.2 && secs < 120 && delta > 0) w = delta / 1e6 / secs;
            }
            energyLast = now; energyAt = t;
            return w;
        }

        void Loop() {
            Init();
            // The first read of a delta has no window to divide by; run the first few passes fast whatever rate is
            // configured, so a window that opens straight into the tray rate still has real numbers in it.
            int warm = 3;
            while (!stop) {
                wake.Reset();
                var s = new SensorSnapshot();
                double driverWatts = double.NaN, die = double.NaN, driverMhz = double.NaN;
                CpuRegisters cpu = CpuSource == null ? null : CpuSource();
                if (cpu != null) {
                    try {
                        CpuTelemetry ct = cpu.Poll();
                        die = ct.DieTemp;
                        if (ct.Pl1On) s.Pl1 = ct.Pl1;
                        if (ct.Pl2On) s.Pl2 = ct.Pl2;
                        s.Throttle = ct.Throttle ?? "";
                        driverWatts = ct.Watts;
                        driverMhz = ct.Mhz;
                    } catch (Exception ex) { if (cpuPollFailures++ == 0) Log.Write("driver cpu poll: " + ex.Message); }
                }
                // The kernel's reading of the package sensor is the same register the driver reads on Intel, and
                // the only reading of it on AMD, where the register is not an MSR.
                if (double.IsNaN(die) && packageTemp != null) {
                    double milli;
                    if (Number(packageTemp, out milli) && milli > 0 && milli < 130000) die = Math.Round(milli / 1000.0);
                }
                double hot = HottestZone(zones);
                if (!double.IsNaN(hot)) {
                    s.AcpiTemp = Math.Round(hot - 273.15, 1);
                    s.AcpiDeciKelvin = (int)Math.Round(hot * 10);
                    if (zoneFirst == 0) zoneFirst = s.AcpiDeciKelvin;
                    else if (s.AcpiDeciKelvin != zoneFirst) zoneMoved = true;
                    s.AcpiMoved = zoneMoved;
                    s.AcpiDead = !zoneMoved && s.AcpiDeciKelvin < ZoneColdBelow && (zoneBusy >= ZoneBusySeconds || s.AcpiDeciKelvin == DeadZoneHint);
                }
                s.CpuFromDriver = !double.IsNaN(die);
                s.CpuTempNow = s.CpuFromDriver ? die : s.AcpiDead ? double.NaN : s.AcpiTemp;
                if (s.CpuFromDriver != tempFromDriver) { tempFromDriver = s.CpuFromDriver; recentCount = 0; }
                s.CpuTemp = Steady(s.CpuTempNow);
                if (!disagreementLogged && s.CpuFromDriver && !double.IsNaN(s.AcpiTemp) && Math.Abs(s.CpuTemp - s.AcpiTemp) > 15) {
                    disagreementLogged = true;
                    Log.Write("cpu temperature: the CPU's own sensor reads " + s.CpuTemp.ToString("0") + ", the ACPI zone " + s.AcpiTemp.ToString("0") + "; the CPU's number is used");
                }
                s.CpuLoad = CpuLoad();
                DateTime tickAt = DateTime.UtcNow;
                if (!zoneMoved && zoneFirst != 0 && zoneTick != DateTime.MinValue && s.CpuLoad >= ZoneBusyLoad)
                    zoneBusy += Math.Min(10, (tickAt - zoneTick).TotalSeconds);
                zoneTick = tickAt;
                s.CpuMhz = !double.IsNaN(driverMhz) ? driverMhz : CpuMhz();
                try {
                    bool fromDriver = !double.IsNaN(driverWatts);
                    if (fromDriver != wattsFromDriver) { wattsFromDriver = fromDriver; recentWattsCount = 0; }
                    if (fromDriver) s.CpuWatts = Watts(driverWatts);
                    else s.CpuWatts = Watts(RaplWatts());
                } catch { }
                try { s.OnBattery = Power.OnBattery(); } catch { }
                if (battery != null) { double pct; if (Number(Path.Combine(battery, "capacity"), out pct)) s.BatteryPercent = (int)Math.Round(pct); }

                bool haveGpu = (!nvmlGone && nvFail < 5) || amdHwmon != null;
                if (haveGpu && !SkipGpu && DueForGpu(s)) {
                    lastNv = DateTime.Now; ReadGpu(s); NoteGpuActivity(s);
                    if (gpuQuiet >= 3) CloseNvml();             // quiet: let it sleep, nothing of ours held open
                    if (!double.IsNaN(s.GpuTemp)) s.GpuRead = DateTime.Now;
                }
                else lock (sync) { s.GpuTemp = last.GpuTemp; s.GpuLoad = last.GpuLoad; s.GpuWatts = last.GpuWatts; s.GpuMhz = last.GpuMhz; s.GpuLimits = last.GpuLimits; s.GpuRead = last.GpuRead; s.GpuAsleep = last.GpuAsleep; }
                // carrying the last number forward is fine for a display; it is not fine for the fan curve
                if (s.GpuRead != DateTime.MinValue && (DateTime.Now - s.GpuRead).TotalMilliseconds > GpuStaleMs) { s.GpuTemp = double.NaN; s.GpuLoad = double.NaN; s.GpuWatts = double.NaN; s.GpuMhz = double.NaN; s.GpuLimits = ulong.MaxValue; }
                // Publication and shutdown are one decision: Dispose cannot begin between the check and the callback.
                lock (sync) {
                    if (!stop) {
                        last = s;
                        var h = Updated; if (h != null) { try { h(s); } catch { } }
                    }
                }
                if (stop) break;
                wake.WaitOne(warm > 0 ? Math.Min(1000, intervalMs) : intervalMs);
                if (warm > 0) warm--;
            }
        }

        /// <summary>Three readings, middle one wins: a temperature is worth acting on only if it is still there a
        /// moment later. The same filter as Windows, for the same reason.</summary>
        readonly double[] recent = new double[3];
        int recentCount;
        bool tempFromDriver;
        double Steady(double now) {
            if (double.IsNaN(now)) { recentCount = 0; return now; }
            recent[2] = recent[1];
            recent[1] = recent[0];
            recent[0] = now;
            if (recentCount < 3) { recentCount++; if (recentCount < 3) return now; }
            double a = recent[0], b = recent[1], c = recent[2];
            return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        /// <summary>The hottest ACPI zone right now, in degrees C, or NaN: for the reports, so that the number
        /// they print is the number the panel would show.</summary>
        public static double AcpiZoneOnce() {
            double hot = HottestZone(AcpiZones());
            return double.IsNaN(hot) ? hot : Math.Round(hot - 273.15);
        }

        /// <summary>The CPU package's own sensor as the kernel publishes it, in degrees C, or NaN; and which
        /// driver and label it came from. For the reports.</summary>
        public static double PackageTempOnce(out string source) {
            string f = FindPackageTemp(out source);
            double milli;
            if (f != null && Number(f, out milli)) return Math.Round(milli / 1000.0);
            return double.NaN;
        }

        /// <summary>Every other tick while the GPU is busy; every 30 s once it has been idle three times running.
        /// A busy CPU brings the rate back at once. The same schedule as Windows.</summary>
        bool DueForGpu(SensorSnapshot s) {
            bool cpuBusy = !double.IsNaN(s.CpuLoad) && s.CpuLoad > 25;
            double want = gpuQuiet >= 3 && !cpuBusy ? GpuIdleMs : Math.Max(intervalMs >= 5000 ? GpuTrayMs : 4000, intervalMs * 2);
            if (Bench) {
                if ((nvmlLoaded && !nvmlGone) || amdHwmon != null) { if (gpuQuiet < 3) want = Math.Min(want, intervalMs - 100); }
                else want = Math.Max(want, GpuTrayMs);
            }
            if (last != null && last.GpuAsleep) want = Math.Min(want, 4000);
            if ((DateTime.Now - lastNv).TotalMilliseconds < want) return false;
            // Asking NVML wakes the GPU to answer, and a hybrid laptop's dGPU spends most of its day asleep. Asleep
            // is also the answer: it is cool. Its temperature stays good for the display while it sleeps; its
            // load, power, clock and limits do not, and it is flagged so the fan curve does not hold on to it.
            if (GpuAsleep()) { CloseNvml(); lastNv = DateTime.Now; lock (sync) { if (last != null) { last.GpuAsleep = true; if (last.GpuRead != DateTime.MinValue) { last.GpuRead = DateTime.Now; last.GpuLoad = 0; last.GpuWatts = 0; last.GpuMhz = double.NaN; last.GpuLimits = ulong.MaxValue; } } } return false; }
            return true;
        }

        /// <summary>True only when the kernel says the NVIDIA adapter is runtime-suspended (D3), read from sysfs,
        /// which does not wake it. Anything unreadable is "awake".</summary>
        bool GpuAsleep() {
            if (nvidiaPower == null) return false;
            return Text(nvidiaPower) == "suspended";
        }
        void NoteGpuActivity(SensorSnapshot s) {
            bool busy = (!double.IsNaN(s.GpuLoad) && s.GpuLoad > 1) || (!double.IsNaN(s.GpuWatts) && s.GpuWatts >= 12);
            if (busy) gpuQuiet = 0;
            else if (gpuQuiet < 100) gpuQuiet++;
        }

        // Median of three, dropping what the package cannot physically draw: the same filter as Windows.
        double wattsAvg = double.NaN;
        static readonly double WattsCeiling = 200;
        readonly double[] recentWatts = new double[3];
        int recentWattsCount;
        bool wattsFromDriver;
        double Watts(double w) {
            if (double.IsNaN(w) || w <= 0 || w > WattsCeiling) return wattsAvg;    // nothing believable this time; keep what we had
            recentWatts[2] = recentWatts[1]; recentWatts[1] = recentWatts[0]; recentWatts[0] = w;
            if (recentWattsCount < 3) { recentWattsCount++; if (recentWattsCount < 3) { wattsAvg = w; return w; } }
            double x = recentWatts[0], y = recentWatts[1], z = recentWatts[2];
            wattsAvg = Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z));
            return wattsAvg;
        }

        // ---- NVML in process. The same functions src\win\Sensors.cs declares on nvml.dll; the library here is the
        // one the NVIDIA driver installs. The session is held only while the GPU is busy, when it is awake anyway,
        // and shut as soon as it goes quiet or to sleep, so it never keeps an idle dGPU out of D3.
        const string Nvml = "libnvidia-ml.so.1";
        [DllImport(Nvml)] static extern int nvmlInit_v2();
        [DllImport(Nvml)] static extern int nvmlShutdown();
        [DllImport(Nvml)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Nvml)] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [StructLayout(LayoutKind.Sequential)] struct NvmlUtil { public uint Gpu, Memory; }
        [DllImport(Nvml)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtil util);
        [DllImport(Nvml)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Nvml)] static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Nvml, EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")] static extern int nvmlClocksEventReasons(IntPtr device, out ulong reasons);
        [DllImport(Nvml, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] static extern int nvmlClocksThrottleReasons(IntPtr device, out ulong reasons);
        int reasonsCall;                              // 0 = try the new name, 1 = the old one, 2 = neither exists
        double lastGoodWatts = double.NaN;
        bool nvmlLoaded, nvmlGone;
        volatile bool nvmlOpen;
        int nvmlFail, nvFail;
        IntPtr nvmlDev;

        void ReadGpu(SensorSnapshot s) {
            if (!nvmlGone && nvFail < 5) {
                if (ReadNvml(s)) return;
                nvFail++;
            }
            ReadAmd(s);
        }

        bool ReadNvml(SensorSnapshot s) {
            try {
                if (!nvmlOpen) {
                    int rc;
                    try { rc = nvmlInit_v2(); }
                    catch (DllNotFoundException) { nvmlGone = true; Log.Write("no libnvidia-ml.so.1 (the NVIDIA driver's library)" + (amdHwmon == null ? "; GPU stats disabled" : "")); return false; }
                    if (rc != 0) throw new Exception("init rc " + rc);
                    if (!nvmlLoaded) { nvmlLoaded = true; Log.Write("gpu stats: NVML in process (" + Nvml + ")"); }
                    nvmlOpen = true;
                    rc = nvmlDeviceGetHandleByIndex_v2(0, out nvmlDev);
                    if (rc != 0) throw new Exception("device rc " + rc);
                }
                uint t;
                int r = nvmlDeviceGetTemperature(nvmlDev, 0, out t);          // 0 = NVML_TEMPERATURE_GPU
                if (r != 0) throw new Exception("temperature rc " + r);
                if (t > 0 && t < 130) s.GpuTemp = t;
                NvmlUtil u; if (nvmlDeviceGetUtilizationRates(nvmlDev, out u) == 0 && u.Gpu <= 100) s.GpuLoad = u.Gpu;
                // The driver hands back the odd impossible figure; those keep the last good number.
                uint mw;
                if (nvmlDeviceGetPowerUsage(nvmlDev, out mw) == 0) {
                    bool busy = !double.IsNaN(s.GpuLoad) && s.GpuLoad > 30;
                    if (mw < 250000 && !(busy && mw < 3000)) { s.GpuWatts = mw / 1000.0; lastGoodWatts = s.GpuWatts; }
                    else s.GpuWatts = lastGoodWatts;
                }
                if (reasonsCall < 2) {
                    ulong why = 0; int rr = -1;
                    try { rr = reasonsCall == 0 ? nvmlClocksEventReasons(nvmlDev, out why) : nvmlClocksThrottleReasons(nvmlDev, out why); }
                    catch (EntryPointNotFoundException) { reasonsCall++; }
                    if (rr == 0) s.GpuLimits = why;
                }
                uint mhz; if (nvmlDeviceGetClockInfo(nvmlDev, 0, out mhz) == 0 && mhz >= 100) s.GpuMhz = mhz;   // 0 = graphics clock
                nvmlFail = 0; nvFail = 0;
                return true;
            } catch (Exception ex) {
                CloseNvml();
                if (++nvmlFail >= 3) { nvmlGone = true; Log.Write("nvml off after " + nvmlFail + " failures (" + ex.Message + "); GPU stats disabled"); }
                return false;
            }
        }
        void CloseNvml() {
            lastGoodWatts = double.NaN;                   // a figure from before the GPU went quiet is no stand-in for one after
            if (!nvmlOpen) return;
            nvmlOpen = false;
            try { nvmlShutdown(); } catch { }
        }

        /// <summary>An AMD GPU through its hwmon node: temp1_input in millidegrees, power1_average in microwatts
        /// (power1_input on newer kernels), freq1_input in hertz, and gpu_busy_percent beside the device.</summary>
        void ReadAmd(SensorSnapshot s) {
            if (amdHwmon == null) return;
            double v;
            if (Number(Path.Combine(amdHwmon, "temp1_input"), out v) && v > 0 && v < 130000) s.GpuTemp = Math.Round(v / 1000.0);
            if (Number(Path.Combine(amdHwmon, "power1_average"), out v) || Number(Path.Combine(amdHwmon, "power1_input"), out v)) { if (v >= 0 && v < 250e6) s.GpuWatts = v / 1e6; }
            if (Number(Path.Combine(amdHwmon, "freq1_input"), out v) && v >= 100e6) s.GpuMhz = v / 1e6;
            if (amdBusy != null && Number(amdBusy, out v) && v >= 0 && v <= 100) s.GpuLoad = v;
        }

        public void Dispose() {
            lock (sync) {
                if (disposed) return;
                disposed = true;
                stop = true;
                wake.Set();
            }
            // Bounded: a read that hangs must not hold up quitting. Past the cap the thread is a background one
            // and goes with the process. A callback that disposes us runs on the worker; do not join ourselves.
            if (worker != null && worker != Thread.CurrentThread) worker.Join(3000);
            CloseNvml();
        }
    }
}
