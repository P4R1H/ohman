// Vane — extra sensors that do not need the BIOS: ACPI thermal zone + CPU utilisation (perf counters)
// and NVIDIA GPU stats via nvidia-smi. All reads are best-effort and never throw.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ohman {

    public sealed class SensorSnapshot {
        public double CpuTemp = double.NaN, CpuLoad = double.NaN, CpuMhz = double.NaN;
        public double GpuTemp = double.NaN, GpuLoad = double.NaN, GpuWatts = double.NaN, GpuMhz = double.NaN;
        public bool OnBattery; public int BatteryPercent = -1;
    }

    public sealed class Sensors : IDisposable {
        PerformanceCounter thermal, cpuUtil, cpuFreq;
        string nvsmi; int nvFail;
        readonly object sync = new object();
        SensorSnapshot last = new SensorSnapshot();
        Thread worker; volatile bool stop; volatile int intervalMs = 2000;
        public event Action<SensorSnapshot> Updated;

        public Sensors() {
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
            foreach (string p in new string[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
                if (File.Exists(p)) { nvsmi = p; break; }
            if (nvsmi == null) Log.Write("nvidia-smi not found; GPU stats disabled");
        }

        public SensorSnapshot Last { get { lock (sync) return last; } }
        public void SetInterval(int ms) { intervalMs = Math.Max(500, ms); }

        public void Start() {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "sensors" };
            worker.Start();
        }

        void Loop() {
            while (!stop) {
                var s = new SensorSnapshot();
                try { if (thermal != null) { double k = thermal.NextValue(); if (k > 200) s.CpuTemp = Math.Round(k - 273.15, 1); } } catch { }
                try { if (cpuUtil != null) s.CpuLoad = Math.Min(100, cpuUtil.NextValue()); } catch { }
                try { if (cpuFreq != null) s.CpuMhz = cpuFreq.NextValue(); } catch { }
                try {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    s.OnBattery = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
                    s.BatteryPercent = (int)Math.Round(ps.BatteryLifePercent * 100);
                } catch { }
                if (nvsmi != null && nvFail < 5) ReadNvidia(s);
                lock (sync) last = s;
                var h = Updated; if (h != null) { try { h(s); } catch { } }
                int waited = 0; while (!stop && waited < intervalMs) { Thread.Sleep(100); waited += 100; }
            }
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
            try { if (thermal != null) thermal.Dispose(); if (cpuUtil != null) cpuUtil.Dispose(); if (cpuFreq != null) cpuFreq.Dispose(); } catch { }
        }
    }
}

