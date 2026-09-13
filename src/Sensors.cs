// Ohman — extra sensors that do not need the BIOS: ACPI thermal zone + CPU utilisation (perf counters)
// and NVIDIA GPU stats via nvidia-smi. All reads are best-effort and never throw.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ohman {

    public sealed class SensorSnapshot {
        public double CpuTemp = double.NaN, CpuLoad = double.NaN, CpuMhz = double.NaN, CpuWatts = double.NaN;
        public DateTime GpuRead = DateTime.MinValue;      // when the GPU numbers below were actually measured
        public double GpuTemp = double.NaN, GpuLoad = double.NaN, GpuWatts = double.NaN, GpuMhz = double.NaN;
        public bool OnBattery; public int BatteryPercent = -1;
    }

    public sealed class Sensors : IDisposable {
        PerformanceCounter thermal, cpuUtil, cpuFreq, cpuPower;
        string nvsmi; int nvFail;
        // Asking nvidia-smi anything wakes the discrete GPU. On a hybrid laptop, asking every few seconds stops it
        // ever reaching its deepest idle state, which costs several watts and shows up as a warmer chassis and busier
        // fans. So: ask at the normal rate while the GPU is doing something, and back off hard once it goes quiet.
        int gpuQuiet; DateTime lastNv = DateTime.MinValue;
        public volatile bool SkipGpu;                 // set by the UI when the machine is running on the iGPU alone
        const int GpuIdleMs = 30000, GpuStaleMs = 45000;
        readonly object sync = new object();
        SensorSnapshot last = new SensorSnapshot();
        Thread worker; volatile bool stop; volatile int intervalMs = 2000;
        public event Action<SensorSnapshot> Updated;

        public Sensors() { }

        /// <summary>Counter setup takes seconds the first time; it runs on the worker so the window is not held back.</summary>
        void InitCounters() {
            try {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                string[] inst = cat.GetInstanceNames();
                if (inst.Length > 0) {
                    Array.Sort(inst);
                    thermal = new PerformanceCounter("Thermal Zone Information", "Temperature", inst[0], true);
                    thermal.NextValue();
                    Log.Write("thermal zone counter: " + inst[0]);
                }
            } catch (Exception ex) { Log.Write("no thermal zone counter: " + ex.Message); }
            try { cpuUtil = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true); cpuUtil.NextValue(); } catch (Exception ex) { Log.Write("no cpu util counter: " + ex.Message); }
            try { cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true); } catch { }
            // CPU package power: the Energy Meter counters (Intel RAPL through Windows' energy metering interface, no driver needed)
            try {
                var cat = new PerformanceCounterCategory("Energy Meter");
                foreach (string inst in cat.GetInstanceNames())
                    if (inst.IndexOf("PKG", StringComparison.OrdinalIgnoreCase) >= 0) { cpuPower = new PerformanceCounter("Energy Meter", "Power", inst, true); cpuPower.NextValue(); Log.Write("cpu power counter: " + inst); break; }
            } catch (Exception ex) { Log.Write("no cpu power counter: " + ex.Message); }
            foreach (string p in new string[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
                if (File.Exists(p)) { nvsmi = p; break; }
            if (nvsmi == null) Log.Write("nvidia-smi not found; GPU stats disabled");
        }

        public void SetInterval(int ms) { intervalMs = Math.Max(500, ms); }

        public void Start() {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "sensors" };
            worker.Start();
        }

        void Loop() {
            InitCounters();
            int tick = 0;
            while (!stop) {
                var s = new SensorSnapshot();
                try { if (thermal != null) { double k = thermal.NextValue(); if (k > 200) s.CpuTemp = Math.Round(k - 273.15, 1); } } catch { }
                try { if (cpuUtil != null) s.CpuLoad = Math.Min(100, cpuUtil.NextValue()); } catch { }
                try { if (cpuFreq != null) s.CpuMhz = cpuFreq.NextValue(); } catch { }
                try { if (cpuPower != null) { double mw = cpuPower.NextValue(); if (mw > 0 && mw < 400000) s.CpuWatts = mw / 1000.0; } } catch { }
                try {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    s.OnBattery = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
                    s.BatteryPercent = (int)Math.Round(ps.BatteryLifePercent * 100);
                } catch { }
                // nvidia-smi wakes the discrete GPU; ask it every other tick and keep the previous numbers in between
                if (nvsmi != null && nvFail < 5 && !SkipGpu && DueForGpu(s)) { lastNv = DateTime.Now; ReadNvidia(s); NoteGpuActivity(s); if (!double.IsNaN(s.GpuTemp)) s.GpuRead = DateTime.Now; }
                else lock (sync) { s.GpuTemp = last.GpuTemp; s.GpuLoad = last.GpuLoad; s.GpuWatts = last.GpuWatts; s.GpuMhz = last.GpuMhz; s.GpuRead = last.GpuRead; }
                // carrying the last number forward is fine for a display; it is not fine for the fan curve, which would
                // believe an idle GPU while a job heats it up between two backed-off reads
                if (s.GpuRead != DateTime.MinValue && (DateTime.Now - s.GpuRead).TotalMilliseconds > GpuStaleMs) { s.GpuTemp = double.NaN; s.GpuLoad = double.NaN; s.GpuWatts = double.NaN; s.GpuMhz = double.NaN; }
                lock (sync) last = s;
                var h = Updated; if (h != null) { try { h(s); } catch { } }
                int waited = 0; while (!stop && waited < intervalMs) { Thread.Sleep(100); waited += 100; }
            }
        }

        /// <summary>Every other tick while the GPU is busy; every 30 s once it has been idle three times running. Work
        /// that could be heating the GPU almost always shows on the CPU first, so a busy CPU brings the rate back at once.</summary>
        bool DueForGpu(SensorSnapshot s) {
            bool cpuBusy = !double.IsNaN(s.CpuLoad) && s.CpuLoad > 25;
            double want = gpuQuiet >= 3 && !cpuBusy ? GpuIdleMs : Math.Max(4000, intervalMs * 2);
            return (DateTime.Now - lastNv).TotalMilliseconds >= want;
        }
        void NoteGpuActivity(SensorSnapshot s) {
            bool busy = (!double.IsNaN(s.GpuLoad) && s.GpuLoad > 1) || (!double.IsNaN(s.GpuWatts) && s.GpuWatts >= 12);
            if (busy) gpuQuiet = 0; else if (gpuQuiet < 100) gpuQuiet++;
        }
        void ReadNvidia(SensorSnapshot s) {
            try {
                var psi = new ProcessStartInfo(nvsmi, "--query-gpu=temperature.gpu,utilization.gpu,power.draw,clocks.gr --format=csv,noheader,nounits") {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi)) {
                    string line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(2500)) { try { p.Kill(); } catch { } nvFail++; return; }
                    if (string.IsNullOrEmpty(line)) { nvFail++; return; }
                    string[] parts = line.Split(',');
                    if (parts.Length != 4) { nvFail++; return; }             // a dropped field would shift the columns
                    double v;
                    if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v < 130) s.GpuTemp = v;
                    if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 100) s.GpuLoad = v;
                    if (double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v < 250) s.GpuWatts = v;
                    if (double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 100) s.GpuMhz = v;
                    nvFail = 0;
                }
            } catch (Exception ex) { nvFail++; if (nvFail == 5) Log.Write("nvidia-smi disabled: " + ex.Message); }
        }

        public void Dispose() {
            stop = true;
            try { if (thermal != null) thermal.Dispose(); if (cpuUtil != null) cpuUtil.Dispose(); if (cpuFreq != null) cpuFreq.Dispose(); if (cpuPower != null) cpuPower.Dispose(); } catch { }
        }
    }
}

