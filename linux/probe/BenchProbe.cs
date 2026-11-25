// SPDX-License-Identifier: GPL-3.0-or-later
// ohman-probe, the benchmark's part: what can be tried of it from a terminal.
//
//   ohman-probe selftest               (BenchChecks) the MangoHud parsers against text in MangoHud's own format
//   ohman-probe bench-selftest         one whole run of the shared state machine on simulated frames, as
//                                      "Ohman.exe --demo --bench-selftest" does on Windows: start, countdown,
//                                      30 s measured, the run stored and read back. Always simulated hardware.
//   ohman-probe bench-watch [SECONDS]  what the MangoHud frame source sees, once a second: which games have a
//                                      control socket, how many frames each logs, which one counts as in front
//   ohman-probe bench-run              one real run (no warm-up, 30 s) of whatever game MangoHud is loaded in.
//                                      The laptop is simulated; the frames are not.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ohman {
    public static partial class Program {
        static void BenchChecks() {
            // A log as MangoHud 0.8 writes it (src/logging.cpp, writeFileHeaders and writeToFile): two lines about the
            // system, the header, a row a frame with the frame time in milliseconds and the time since the log
            // began in nanoseconds.
            const string sys = "os,cpu,gpu,ram,kernel,driver,cpuscheduler\n"
                + "Arch Linux,AMD Ryzen 7 7840HS w/ Radeon 780M Graphics,NVIDIA GeForce RTX 4060 Laptop GPU,15601140,6.10.3-arch1-2,4.6.0 NVIDIA 555.58.02,schedutil\n";
            const string head = "fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed\n";
            var rows = new System.Text.StringBuilder();
            long ns = 0;
            double[] fts = new double[40];
            for (int i = 0; i < fts.Length; i++) {
                fts[i] = 11.2 + (i % 7) * 0.13 + (i == 23 ? 30 : 0);
                ns += (long)(fts[i] * 1e6);
                rows.Append((1000 / fts[i]).ToString("G6", CultureInfo.InvariantCulture)).Append(',').Append(fts[i].ToString("G6", CultureInfo.InvariantCulture))
                    .Append(",23.4,0,97,71,68,1890,7000,4.31,96,9.52,0,3.1,4012,").Append(ns).Append('\n');
            }
            foreach (bool versioned in new[] { false, true }) {
                string text = (versioned ? "v1\n0.8.4\n---------------------SYSTEM INFO---------------------\n" : "") + sys
                    + (versioned ? "--------------------FRAME METRICS--------------------\n" : "") + head + rows;
                MangoHudLog csv = null;
                var ft = new List<double>(); var at = new List<long>();
                foreach (string line in text.Split('\n')) {
                    if (csv == null) { csv = MangoHudLog.Header(line); continue; }
                    if (csv.Row(line, out double f, out long e)) { ft.Add(f); at.Add(e); }
                }
                string which = versioned ? "mangohud log (log_versioning)" : "mangohud log";
                Check(which + ": the header is found after the system lines, every row read", csv != null && ft.Count == 40, ft.Count + " rows");
                Check(which + ": frame time and elapsed of the hitch row", ft.Count == 40 && Math.Abs(ft[23] - (11.2 + 2 * 0.13 + 30)) < 1e-3 && at[39] == ns, ft.Count == 40 ? ft[23] + " ms, last at " + at[39] : "");
                Check(which + ": the frame times are milliseconds", MangoHudLog.Unit(ft, at) == 1, "unit " + MangoHudLog.Unit(ft, at));
                if (!versioned && ft.Count == 40) {
                    FrameStats st = FrameStats.Of(ft);
                    double sum = 0; foreach (double v in fts) sum += v;
                    Check("mangohud log: average FPS is frames over the time they took", Math.Abs(st.AvgFps - 1000.0 * 40 / sum) < 1e-3, st.AvgFps.ToString("0.000", CultureInfo.InvariantCulture));
                    var us = ft.ConvertAll(v => v * 1000);
                    Check("mangohud log: an older log in microseconds is told by its own clock", MangoHudLog.Unit(us, at) == 0.001, "unit " + MangoHudLog.Unit(us, at));
                    var sampled = new List<long>(); long t = 0; foreach (double v in ft) { t += 100000000; sampled.Add(t); }
                    Check("mangohud log: a log sampled every 100 ms (log_interval) is refused", MangoHudLog.Unit(ft, sampled) == 0, "unit " + MangoHudLog.Unit(ft, sampled));
                    Check("mangohud log: too few rows say nothing yet", double.IsNaN(MangoHudLog.Unit(ft.GetRange(0, 5), at.GetRange(0, 5))), "");
                }
            }
            Check("mangohud log: the system lines are not a header", MangoHudLog.Header("os,cpu,gpu,ram,kernel,driver,cpuscheduler") == null && MangoHudLog.Header("") == null, "");
            MangoHudLog c2 = MangoHudLog.Header(head.Trim());
            Check("mangohud log: a broken row is skipped", c2 != null && !c2.Row("87.1,abc,1,2", out _, out _) && !c2.Row("0,0,23.4,0,97,71,68,1890,7000,4.31,96,9.52,0,3.1,4012,5", out _, out _), "");

            Check("mangohud log names: a log, not its summary, not another csv",
                MangoHudSource.IsLogName("/home/u/Cyberpunk2077_2026-11-25_18-03-11.csv") && !MangoHudSource.IsLogName("/home/u/Cyberpunk2077_2026-11-25_18-03-11_summary.csv")
                && !MangoHudSource.IsLogName("/home/u/scores.csv") && !MangoHudSource.IsLogName("/home/u/Cyberpunk2077_2026-11-25_18-03-11.csv (deleted)"), "");

            // /proc/net/unix: abstract sockets carry an @; MangoHud's are the ones Ohman may talk to.
            const string unix = "Num       RefCount Protocol Flags    Type St Inode Path\n"
                + "0000000000000000: 00000002 00000000 00010000 0001 01 2247303 /run/user/1000/bus\n"
                + "0000000000000000: 00000002 00000000 00010000 0001 01 51234 @mangohud-48213\n"
                + "0000000000000000: 00000003 00000000 00000000 0001 03 51240 @mangohud-48213\n"
                + "0000000000000000: 00000002 00000000 00010000 0001 01 60111 @mangohud\n"
                + "0000000000000000: 00000002 00000000 00010000 0001 01 60112 @/tmp/.X11-unix/X0\n"
                + "0000000000000000: 00000003 00000000 00000000 0001 03 21655\n";
            List<string> socks = MangoHud.SocketsIn(unix);
            Check("mangohud sockets: the two MangoHud names, each once", socks.Count == 2 && socks[0] == "mangohud-48213" && socks[1] == "mangohud", string.Join(" ", socks));

            const string lspci = "00:02.0 \"VGA compatible controller\" \"Intel Corporation\" \"Raptor Lake-P [UHD Graphics]\" -r04 \"Hewlett-Packard Company\" \"Device 8bcd\"\n"
                + "01:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"GA107M [GeForce RTX 4050 Max-Q / Mobile]\" -ra1 \"Hewlett-Packard Company\" \"Device 8bcd\"\n";
            Check("gpu name: the NVIDIA card before the integrated one", Machine.PickGpu(lspci) == "GeForce RTX 4050 Max-Q / Mobile", Machine.PickGpu(lspci));
            const string amd = "03:00.0 \"Display controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Navi 33 [Radeon RX 7600M XT]\" -rc1 \"HP\" \"Device 8bd4\"\n"
                + "65:00.0 \"VGA compatible controller\" \"Advanced Micro Devices, Inc. [AMD/ATI]\" \"Phoenix1 [Radeon 780M]\" -rc8 \"HP\" \"Device 8bd4\"\n";
            Check("gpu name: a Radeon RX before the APU's own Radeon, in either order", Machine.PickGpu(amd) == "Radeon RX 7600M XT", Machine.PickGpu(amd));
        }

        static Engine BenchEngine() {
            AppPaths.Demo = true;
            var s = new Settings();
            s.NoPersist = true;
            var e = new Engine(new DemoHardware(), s);
            e.Init();
            return e;
        }

        /// <summary>The state machine once, on simulated frames. What Windows logs for --bench-selftest is printed
        /// here; beyond that, the phases it went through, the stored file and the run read back are checked.</summary>
        static int BenchSelfTest() {
            failures = 0;
            Engine e = BenchEngine();
            var phases = new List<BenchPhase>();
            BenchRun done = null; string ended = null;
            using (var wait = new ManualResetEvent(false))
            using (var b = new Bench(e, true)) {
                b.SetChoices(2, 0, false);
                b.Changed += () => { BenchPhase p = b.Snapshot().Phase; lock (phases) if (phases.Count == 0 || phases[phases.Count - 1] != p) { phases.Add(p); Console.WriteLine("  phase " + p); } };
                b.Finished += r => { done = r; wait.Set(); };
                b.EndedEarly += why => { ended = why; wait.Set(); };
                Console.WriteLine("Ohman probe " + Version + ": one benchmark run on simulated frames (about 40 s)");
                Check("the run starts", b.Start(), b.Error);
                bool finished = wait.WaitOne(90000);
                Check("the run finishes by itself", finished && done != null, ended ?? (finished ? "" : "timed out"));
                Thread.Sleep(300);
            }
            if (done != null) {
                Console.WriteLine("  selftest: finished " + done.Id + " frames=" + done.Frames + " avg=" + done.AvgFps.ToString("0.0", CultureInfo.InvariantCulture) + " low1=" + done.Low1.ToString("0.0", CultureInfo.InvariantCulture)
                    + " stutter=" + done.StutterPct.ToString("0.00", CultureInfo.InvariantCulture) + " samples=" + done.Samples.Count);
                string want = "Waiting Countdown Measuring Finishing Idle";
                Check("phases: waiting, countdown, measuring, finishing, idle", string.Join(" ", phases) == want, string.Join(" ", phases));
                Check("30 s measured, about 80 frames a second", Math.Abs(done.MeasuredSec - 30) < 0.01 && done.Frames > 2000 && done.Frames < 2800 && done.AvgFps > 70 && done.AvgFps < 95, done.MeasuredSec + " s, " + done.Frames + " frames");
                Check("a 1% low, no 0.1% low (under 10,000 frames)", !double.IsNaN(done.Low1) && double.IsNaN(done.Low01), "");
                Check("one sample a second", done.Samples.Count >= 29 && done.Samples.Count <= 31, done.Samples.Count + " samples");
                Check("the game is the simulated one", done.Game == "Cyberpunk 2077" && done.Exe == "Cyberpunk2077" && done.PresentMode == "Hardware: Independent Flip", done.Game + " / " + done.PresentMode);
                Check("stored under the data directory", done.SaveError.Length == 0 && done.File.StartsWith(Path.Combine(AppPaths.DataDir, "bench", "runs")) && File.Exists(done.File), done.File + " " + done.SaveError);
                BenchRun back = null;
                foreach (BenchRun r in RunStore.List()) if (r.Id == done.Id) back = RunStore.Full(r);
                Check("read back: the same numbers, histogram and samples", back != null && back.Frames == done.Frames && Math.Abs(back.AvgFps - done.AvgFps) < 0.001 && Math.Abs(back.Low1 - done.Low1) < 0.001
                    && back.Samples.Count == done.Samples.Count && back.Hist.Length == done.Hist.Length, back == null ? "not found" : back.Frames + " frames, " + back.Samples.Count + " samples");
                // The stored histogram holds every frame; the numbers recomputed from the file's own sum agree with it.
                int inHist = 0; foreach (int n in done.Hist) inHist += n;
                Check("the histogram holds every frame, and the average is frames over their time", inHist == done.Frames && Math.Abs(done.AvgFps - 1000.0 * done.Frames / done.FtSum) < 1e-9, inHist + " in the histogram");
            }
            e.Dispose();
            Console.WriteLine(failures == 0 ? "PASS" : "FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        static int BenchWatch(List<string> a) {
            int seconds = a.Count > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 10;
            Console.WriteLine("Ohman probe " + Version + ": MangoHud " + (MangoHud.Installed ? "is installed" : "is not installed") + " · frame capture " + (Host.HasFrameCapture ? "ready" : "not ready")
                + " · sockets: " + (MangoHud.Sockets().Count == 0 ? "none" : string.Join(" ", MangoHud.Sockets())));
            Console.WriteLine("  launch option: " + FrameCapture.LaunchOption);
            IFrameSource src = FrameCapture.Start(0, out string err);
            if (src == null) { Console.WriteLine("  " + err); return 1; }
            var counts = new Dictionary<int, int>();
            var sums = new Dictionary<int, double>();
            src.Row += r => { lock (counts) { counts.TryGetValue(r.Pid, out int n); counts[r.Pid] = n + 1; sums.TryGetValue(r.Pid, out double s); sums[r.Pid] = s + r.Ms; } };
            for (int i = 0; i < seconds; i++) {
                Thread.Sleep(1000);
                int front = Games.Foreground(out IntPtr root);
                var sb = new System.Text.StringBuilder();
                lock (counts) {
                    foreach (var kv in counts) sb.Append(" · " + Games.Stem(kv.Key, null) + " (pid " + kv.Key + ") " + kv.Value + " rows, " + (kv.Value > 0 ? (1000.0 * kv.Value / sums[kv.Key]).ToString("0.0", CultureInfo.InvariantCulture) : "0") + " fps");
                    counts.Clear(); sums.Clear();
                }
                Console.WriteLine("  " + (i + 1) + " s: " + (src.Note.Length > 0 ? src.Note : sb.Length > 0 ? "drawing" : "nothing yet") + sb + " · in front: " + (front > 0 ? Games.Stem(front, null) + " (pid " + front + ")" : "nothing")
                    + (root != IntPtr.Zero ? " window 0x" + root.ToInt64().ToString("x") : ""));
            }
            src.Stop();
            Thread.Sleep(3000);          // the logs it started are stopped and removed on a thread of their own
            return 0;
        }

        /// <summary>The real thing, as far as it goes without a laptop: the state machine on frames from MangoHud.</summary>
        static int BenchRun_() {
            failures = 0;
            Engine e = BenchEngine();
            BenchRun done = null; string ended = null;
            BenchPhase last = BenchPhase.Idle; string note = "";
            using (var wait = new ManualResetEvent(false))
            using (var b = new Bench(e, false)) {
                b.SetChoices(2, 0, false);
                b.Changed += () => {
                    BenchView v = b.Snapshot();
                    if (v.Phase != last) { last = v.Phase; Console.WriteLine("  phase " + v.Phase + (v.Game.Length > 0 ? " · " + v.Game + " (" + v.Exe + ")" : "")); }
                    if ((v.Note ?? "") != note) { note = v.Note ?? ""; if (note.Length > 0) Console.WriteLine("  " + note); }
                };
                b.Finished += r => { done = r; wait.Set(); };
                b.EndedEarly += why => { ended = why; wait.Set(); };
                Console.WriteLine("Ohman probe " + Version + ": one benchmark run on MangoHud's frames (no warm-up, 30 s)");
                if (!b.Start()) { Console.WriteLine("  cannot start: " + b.Error); Console.WriteLine("  launch option: " + FrameCapture.LaunchOption); return 1; }
                wait.WaitOne(120000);
                if (done == null && ended == null) b.Stop();
                Thread.Sleep(500);
            }
            e.Dispose();
            if (done == null) { Console.WriteLine("  no result: " + (ended ?? "timed out")); Thread.Sleep(3000); return 1; }
            Console.WriteLine("  finished " + done.Id + " · " + done.Game + " · frames=" + done.Frames + " avg=" + done.AvgFps.ToString("0.0", CultureInfo.InvariantCulture) + " low1=" + done.Low1.ToString("0.0", CultureInfo.InvariantCulture)
                + " p50=" + done.FtP50.ToString("0.00", CultureInfo.InvariantCulture) + " ms stutter=" + done.StutterPct.ToString("0.00", CultureInfo.InvariantCulture) + "% samples=" + done.Samples.Count
                + " · " + (done.ResW > 0 ? done.ResW + "x" + done.ResH : "size unknown") + " · " + done.Cpu + " / " + done.Gpu + (done.Ended.Length > 0 ? " · ended: " + done.Ended : ""));
            Console.WriteLine("  stored: " + done.File);
            Thread.Sleep(3000);          // the log it started is stopped and removed on a thread of its own
            return 0;
        }
    }
}
