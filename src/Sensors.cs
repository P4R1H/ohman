// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: extra sensors that do not need the BIOS: ACPI thermal zone + CPU utilisation (perf counters)
// and NVIDIA GPU stats via nvidia-smi. All reads are best-effort and never throw.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ohman {

    public sealed class SensorSnapshot {
        public double CpuTemp = double.NaN, CpuLoad = double.NaN, CpuMhz = double.NaN, CpuWatts = double.NaN;
        public bool MhzFromDriver;
        public double AcpiTemp = double.NaN;              // the hottest ACPI zone, kept beside CpuTemp when the driver supplies that
        public double CpuTempNow = double.NaN;            // the single reading behind CpuTemp, before the median; for the report
        public bool CpuFromDriver;                        // CpuTemp is the package sensor read through the driver
        public double Pl1 = double.NaN, Pl2 = double.NaN; // package power limits the CPU is holding (driver only)
        public string Throttle = "";                      // "" or why the CPU is being held back (driver only)
        public DateTime GpuRead = DateTime.MinValue;      // when the GPU numbers below were actually measured
        public double GpuTemp = double.NaN, GpuLoad = double.NaN, GpuWatts = double.NaN, GpuMhz = double.NaN;
        public bool OnBattery;
        public int BatteryPercent = -1;
    }

    public sealed class Sensors : IDisposable {
        PerformanceCounter[] thermals = new PerformanceCounter[0];
        PerformanceCounter cpuUtil, cpuFreq, cpuPerf, cpuPower;
        string nvsmi;
        int nvFail;
        // Asking nvidia-smi anything wakes the discrete GPU. On a hybrid laptop, asking every few seconds stops it
        // ever reaching its deepest idle state, which costs several watts and shows up as a warmer chassis and busier
        // fans. So: ask at the normal rate while the GPU is doing something, and back off hard once it goes quiet.
        int gpuQuiet;
        DateTime lastNv = DateTime.MinValue;
        public volatile bool SkipGpu;                 // set by the UI when the machine is running on the iGPU alone
        const int GpuIdleMs = 30000, GpuStaleMs = 45000;
        readonly object sync = new object();
        SensorSnapshot last = new SensorSnapshot();
        Thread worker;
        volatile bool stop;
        volatile int intervalMs = 2000;
        // Sleeping in 100 ms steps to stay responsive to Stop() costs ten timer interrupts a second for the life of
        // the process, which is enough to keep the package out of its deeper idle states. One wait for the whole
        // interval costs one, and the event still ends it immediately.
        readonly ManualResetEvent wake = new ManualResetEvent(false);
        public event Action<SensorSnapshot> Updated;
        /// <summary>Where the CPU's own registers are, when there is a driver. Asked on every tick rather than
        /// held, because the engine opens and closes them as the driver is installed and removed.</summary>
        public Func<CpuRegisters> CpuSource;
        int cpuPollFailures;
        bool disagreementLogged;

        public Sensors() { }

        /// <summary>Counter setup takes seconds the first time; it runs on the worker so the window is not held back.</summary>
        void InitCounters() {
            try {
                // Machines expose several ACPI thermal zones and the order means nothing: one is as likely to be
                // a skin sensor, or a constant, as the processor. Keep every zone that reads like a temperature
                // and take the hottest on each poll; a wrong choice blinds the curve and the thermal guard alike.
                //
                // This did NOT fix "CPU stuck at 28 degrees". Choosing a zone once was a real bug, but the owners
                // still reporting it have exactly one zone and it reads a constant, so there is nothing here to
                // choose between. That needs a temperature source other than the ACPI zones.
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                string[] inst = cat.GetInstanceNames();
                Array.Sort(inst);
                var live = new List<PerformanceCounter>();
                foreach (string name in inst) {
                    PerformanceCounter c = null;
                    try {
                        c = new PerformanceCounter("Thermal Zone Information", "Temperature", name, true);
                        double k = c.NextValue();
                        if (k < 283 || k > 398) { c.Dispose(); continue; }     // 10 C to 125 C; anything else is not a temperature
                        live.Add(c);
                    } catch { if (c != null) try { c.Dispose(); } catch { } }
                }
                thermals = live.ToArray();
                if (thermals.Length > 0) Log.Write("thermal zones: " + thermals.Length + " usable of " + inst.Length + ", hottest wins each poll");
                else Log.Write("no usable thermal zone among " + inst.Length + "; CPU temperature unavailable");
            } catch (Exception ex) { Log.Write("no thermal zone counter: " + ex.Message); }
            try { cpuUtil = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true); cpuUtil.NextValue(); } catch (Exception ex) { Log.Write("no cpu util counter: " + ex.Message); }
            // "Processor Frequency" is the *base* clock on most machines and never moves, which is what a Ryzen
            // AI 7 350 owner saw: the number sat at base while the chip boosted. "% Processor Performance" is the
            // one that tracks the real clock, as a percentage of base, so the two together give the actual MHz.
            try { cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true); cpuFreq.NextValue(); } catch { }
            try { cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true); cpuPerf.NextValue(); } catch (Exception ex) { Log.Write("no cpu performance counter: " + ex.Message); }
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

        void Loop() {
            InitCounters();
            // The first read of a delta counter has no window to divide by, so it is thrown away and the panel shows
            // "--" until the second one. Run the first few passes fast whatever rate is configured, so a window that
            // opens straight into the tray-rate or battery-rate schedule still has real numbers in it.
            int warm = 3;
            while (!stop) {
                wake.Reset();
                var s = new SensorSnapshot();
                // The die is read first, a whole interval after this thread last did anything. It answers in under
                // a millisecond and takes a few hundred to settle, so read after the performance counters below,
                // which are a perflib round trip and not free, it reports the transient our own measurement just
                // caused: measured, a median of 68 that way against a floor of 55 free-running.
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
                // The same 283..398 K window InitCounters uses, applied on every read and not only the first.
                // Taking the maximum across zones means one zone reporting nonsense decides the answer for all of
                // them, and a high enough number would hold the thermal guard on and the fans at maximum.
                double hot = double.NaN;
                for (int i = 0; i < thermals.Length; i++) {
                    try {
                        double k = thermals[i].NextValue();
                        if (k >= 283 && k <= 398 && (double.IsNaN(hot) || k > hot)) hot = k;
                    } catch { }
                }
                if (!double.IsNaN(hot)) s.AcpiTemp = Math.Round(hot - 273.15, 1);
                // The driver's number wins where there is one: it is the package sensor itself, not whichever
                // zone the firmware chose to expose. The zone stays in the snapshot for the report to compare.
                s.CpuFromDriver = !double.IsNaN(die);
                s.CpuTempNow = s.CpuFromDriver ? die : s.AcpiTemp;
                s.CpuTemp = Steady(s.CpuTempNow);
                if (!disagreementLogged && s.CpuFromDriver && !double.IsNaN(s.AcpiTemp) && Math.Abs(s.CpuTemp - s.AcpiTemp) > 15) {
                    disagreementLogged = true;
                    Log.Write("cpu temperature: the driver reads " + s.CpuTemp.ToString("0") + ", the ACPI zone " + s.AcpiTemp.ToString("0") + "; the driver's number is used");
                }
                try { if (cpuUtil != null) s.CpuLoad = Math.Min(100, cpuUtil.NextValue()); } catch { }
                try {
                    if (cpuFreq != null) {
                        double base_ = cpuFreq.NextValue();
                        double pct = double.NaN;
                        try { if (cpuPerf != null) pct = cpuPerf.NextValue(); } catch { }
                        // A boosting chip reads over 100 %; a parked one reads well under. Ignore obvious nonsense.
                        // Without the percentage there is no way to know the real clock, and the base one dressed
                        // up as the current one is exactly the thing that got reported, so show nothing instead.
                        s.CpuMhz = (!double.IsNaN(pct) && pct > 1 && pct < 500) ? base_ * pct / 100.0 : double.NaN;
                    }
                    // The CPU's own answer wins. The counter above is a sampled estimate of the same ratio and
                    // was the number owners kept reporting as wrong.
                    if (!double.IsNaN(driverMhz)) { s.CpuMhz = driverMhz; s.MhzFromDriver = true;
                    }
                } catch { }
                // Package power from the energy counter the driver reads, when there is one: the same RAPL
                // figure the Energy Meter counter reports, without the counter's missed-window spikes.
                try { if (!double.IsNaN(driverWatts)) s.CpuWatts = Watts(driverWatts); else if (cpuPower != null) s.CpuWatts = Watts(cpuPower.NextValue() / 1000.0); } catch { }
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
                wake.WaitOne(warm > 0 ? Math.Min(1000, intervalMs) : intervalMs);
                if (warm > 0) warm--;
            }
        }

        /// <summary>Three readings, middle one wins. A temperature is worth acting on only if it is still there a
        /// moment later: about one reading a minute lands inside somebody else's burst of work (measured: 3 of 64
        /// samples at or above 90 while the floor was 55), and one of those was enough to drive the fans up and
        /// latch the thermal guard. Costs no extra reads and no extra wakeups, and a real climb is in all three
        /// within two ticks. Applied to the ACPI zone too, where it changes nothing.</summary>
        readonly double[] recent = new double[3];
        int recentCount;
        double Steady(double now) {
            if (double.IsNaN(now)) { recentCount = 0; return now; }
            recent[2] = recent[1];
            recent[1] = recent[0];
            recent[0] = now;
            if (recentCount < 3) { recentCount++; if (recentCount < 3) return now; }
            double a = recent[0], b = recent[1], c = recent[2];
            return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
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
            if (busy) gpuQuiet = 0;
            else if (gpuQuiet < 100) gpuQuiet++;
        }
        // The Energy Meter counter is an energy delta divided by the sampling window, so a short or missed window
        // reports a number the package cannot physically draw. Drop those, then average a few readings: a watt figure
        // that jumps 40 W between two glances reads as broken even when each sample is honest.
        double wattsAvg = double.NaN;
        static readonly double WattsCeiling = 200;
        readonly double[] recentWatts = new double[3];
        int recentWattsCount;
        // Median of three, the same thing the die temperature does, instead of the running average this used to
        // keep. The average only rejected spikes above 60 W, so at idle a 25 W burst went straight in and then
        // decayed slowly, and the figure on screen sat well above what the machine was really drawing. A median
        // drops the burst outright and carries no lag from it.
        double Watts(double w) {
            if (w <= 0 || w > WattsCeiling) return wattsAvg;                       // nothing believable this time; keep what we had
            recentWatts[2] = recentWatts[1]; recentWatts[1] = recentWatts[0]; recentWatts[0] = w;
            if (recentWattsCount < 3) { recentWattsCount++; if (recentWattsCount < 3) { wattsAvg = w; return w; } }
            double x = recentWatts[0], y = recentWatts[1], z = recentWatts[2];
            wattsAvg = Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z));
            return wattsAvg;
        }
        void ReadNvidia(SensorSnapshot s) {
            try {
                var psi = new ProcessStartInfo(nvsmi, "--query-gpu=temperature.gpu,utilization.gpu,power.draw,clocks.gr --format=csv,noheader,nounits") {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi)) {
                    string line = null;
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (line == null && e.Data != null) line = e.Data; };
                    p.ErrorDataReceived += delegate { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(2500)) { try { p.Kill(); } catch { } p.WaitForExit(2000); Log.Write("nvidia-smi query timed out"); nvFail++; return; }
                    p.WaitForExit();
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
            wake.Set();
            try { for (int i = 0; i < thermals.Length; i++) { try { thermals[i].Dispose(); } catch { } } if (cpuUtil != null) cpuUtil.Dispose(); if (cpuFreq != null) cpuFreq.Dispose(); if (cpuPower != null) cpuPower.Dispose(); } catch { }
        }
    }
}

