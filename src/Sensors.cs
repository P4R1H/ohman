// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: extra sensors that do not need the BIOS: ACPI thermal zone + CPU utilisation (perf counters)
// and NVIDIA GPU stats via nvidia-smi. All reads are best-effort and never throw.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohman {

    public sealed class SensorSnapshot {
        public double CpuTemp = double.NaN, CpuLoad = double.NaN, CpuMhz = double.NaN, CpuWatts = double.NaN;
        public double AcpiTemp = double.NaN;              // the hottest ACPI zone, kept beside CpuTemp when the driver supplies that
        public double CpuTempNow = double.NaN;            // the single reading behind CpuTemp, before the median; for the report
        public bool CpuFromDriver;                        // CpuTemp is the package sensor read through the driver
        public bool AcpiDead, AcpiMoved;                  // the zone sat on one number through real work / has changed at least once
        public int AcpiDeciKelvin;                        // the zone's raw reading in tenths of a kelvin, 0 when there is none
        public double Pl1 = double.NaN, Pl2 = double.NaN; // package power limits the CPU is holding (driver only)
        public string Throttle = "";                      // "" or why the CPU is being held back (driver only)
        public DateTime GpuRead = DateTime.MinValue;      // when the GPU numbers below were actually measured
        public double GpuTemp = double.NaN, GpuLoad = double.NaN, GpuWatts = double.NaN, GpuMhz = double.NaN;
        public ulong GpuLimits = ulong.MaxValue;          // NVML clock event reasons (what holds the GPU clock back); MaxValue = unknown
        public bool GpuAsleep;                            // the dGPU was in D3 at the last check: GpuTemp is the reading from before it slept
        public bool OnBattery;
        public int BatteryPercent = -1;
    }
}
