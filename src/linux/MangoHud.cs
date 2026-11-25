// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: frames from MangoHud. Linux has no PresentMon and no event tracing to read presents from;
// the tool people already run in their games is MangoHud (github.com/flightlessmango/MangoHud, MIT), a Vulkan
// layer and OpenGL preload that sits inside the game and times every frame. Ohman injects nothing and loads
// nothing: it talks to a MangoHud the owner has put in the game, and reads the log that MangoHud writes.
//
// What this is built on, read from MangoHud's source (master 2bfd645, release v0.8.4):
//   * control=NAME in MangoHud's configuration opens an abstract Unix socket called NAME, and %p in NAME becomes
//     the game's process id (src/overlay_params.cpp, parse_control; src/mesa/util/os_socket.c). One client at a
//     time (src/control.cpp, control_client_check), served once a frame (src/vulkan.cpp, snapshot_swapchain_frame;
//     src/gl/gl_hud.cpp).
//   * The commands are ":name=value;" (src/control.cpp, process_char). ":logging=1;" starts a log if none is
//     running and ":logging=0;" stops it (parse_command). On connecting MangoHud says who it is
//     (":MangoHudControlVersion=1;:DeviceName=...;:MangoHudVersion=...;"), and when a log ends ":LoggingFinished".
//   * A log is OUTPUT_FOLDER/PROGRAM_YYYY-MM-DD_HH-MM-SS.csv, in $HOME when output_folder is not set
//     (src/logging.cpp, Logger::Logger and start_logging). It starts with two lines about the system, then the
//     header "fps,frametime,cpu_load,...,elapsed", then one row per frame while log_interval is 0, which is its
//     default (src/overlay.cpp, update_hud_info_with_frametime calls try_log on every present), each row flushed
//     as it is written (Logger::writeToFile). frametime is the time since the last present in milliseconds,
//     elapsed the time since the log started in nanoseconds. Stopping writes PROGRAM_..._summary.csv beside it.
//   * MANGOHUD_CONFIG replaces the configuration file unless it says read_cfg (overlay_params.cpp).
//
// So: every "mangohud*" abstract socket listed in /proc/net/unix is a game Ohman can ask. The process behind one
// is the socket's peer (SO_PEERCRED), which is right inside a Flatpak too, where the name's own number is not.
// The log is found as the .csv the game holds open (/proc/PID/fd), wherever its output_folder points, and read
// through that same entry. A log Ohman started is stopped and removed again when the run is over; one the owner
// was already recording is read from where it stands and left alone.
//
// What MangoHud does not say: when a frame reached the screen, which swap chain it was, how it was presented.
// Those columns stay empty here, so frame generation and the display-time cap check have nothing to work on.
//
// UNTESTED against a real MangoHud: no game can run where this was written. The parser is checked against a
// log in MangoHud's format and the socket handling against an emulation of its control protocol
// (linux/test/fake-mangohud.py); neither is the real thing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ohman {

    /// <summary>MangoHud on this machine: is it installed, which games have it loaded with a control socket.</summary>
    public static class MangoHud {
        /// <summary>What to put in a launcher so a game can be measured: MangoHud loaded, its control socket open
        /// under a name of its own, one row a frame, and the owner's own MangoHud.conf still read.</summary>
        public const string LaunchOption = "MANGOHUD_CONFIG=read_cfg,control=mangohud-%p,log_interval=0 mangohud %command%";
        const string Prefix = "mangohud";

        static bool? installed;
        /// <summary>The mangohud launcher is on PATH, or its Vulkan layer or Flatpak extension is where they are
        /// usually put. Looked for once: installing it means starting a game again anyway.</summary>
        public static bool Installed {
            get {
                if (installed.HasValue && installed.Value) return true;
                bool found = false;
                try {
                    if (Host.Find("mangohud") != null) found = true;
                    string home = Environment.GetEnvironmentVariable("HOME") ?? "";
                    string[] places = {
                        "/usr/share/vulkan/implicit_layer.d", "/usr/local/share/vulkan/implicit_layer.d", "/etc/vulkan/implicit_layer.d",
                        Path.Combine(home, ".local/share/vulkan/implicit_layer.d") };
                    foreach (string dir in places) {
                        if (found || !Directory.Exists(dir)) continue;
                        foreach (string f in Directory.GetFiles(dir, "*.json")) if (Path.GetFileName(f).IndexOf("MangoHud", StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                    }
                    foreach (string dir in new[] { "/var/lib/flatpak/runtime/org.freedesktop.Platform.VulkanLayer.MangoHud", Path.Combine(home, ".local/share/flatpak/runtime/org.freedesktop.Platform.VulkanLayer.MangoHud") })
                        if (!found && Directory.Exists(dir)) found = true;
                } catch { }
                installed = found;
                return found;
            }
        }

        /// <summary>The abstract sockets whose name starts with "mangohud", from the kernel's own list.</summary>
        public static List<string> Sockets() {
            try { return SocketsIn(File.ReadAllText("/proc/net/unix")); } catch { return new List<string>(); }
        }
        /// <summary>The parser apart from the file, so it can be checked against captured text. An abstract
        /// socket's path is written with an @ in front; only listening sockets have one worth connecting to, and
        /// a name seen twice is one socket.</summary>
        public static List<string> SocketsIn(string procNetUnix) {
            var names = new List<string>();
            foreach (string raw in (procNetUnix ?? "").Split('\n')) {
                int at = raw.IndexOf(" @", StringComparison.Ordinal);
                if (at < 0) continue;
                string name = raw.Substring(at + 2).TrimEnd('\r', ' ');
                if (name.StartsWith(Prefix, StringComparison.Ordinal) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }

        // ---------- what the sources know, for Games.Foreground ----------
        static readonly object sync = new object();
        static readonly Dictionary<int, int> rate = new Dictionary<int, int>();        // pid -> rows in the last whole second
        static readonly Dictionary<string, int> owner = new Dictionary<string, int>();  // socket name -> the pid behind it
        internal static void Seen(string socket, int pid) { lock (sync) owner[socket] = pid; }
        internal static int OwnerOf(string socket) { lock (sync) { int p; return owner.TryGetValue(socket, out p) ? p : 0; } }
        internal static void Forget(string socket) { lock (sync) owner.Remove(socket); }
        internal static void Rate(int pid, int rows) { lock (sync) { if (rows < 0) rate.Remove(pid); else rate[pid] = rows; } }
        /// <summary>The games a source is connected to right now.</summary>
        public static int[] Pids() { lock (sync) { var a = new int[rate.Count]; rate.Keys.CopyTo(a, 0); return a; } }
        /// <summary>Rows that process logged in the last second; 0 when it is not one of ours.</summary>
        public static int RateOf(int pid) { lock (sync) { int r; return rate.TryGetValue(pid, out r) ? r : 0; } }
        /// <summary>The connected game that is drawing the most, or 0.</summary>
        public static int Busiest() {
            lock (sync) { int best = 0, rows = 0; foreach (var kv in rate) if (kv.Value > rows) { rows = kv.Value; best = kv.Key; } return best; }
        }

        /// <summary>A process of this user that has MangoHud's library mapped, or 0: a game started with MangoHud
        /// but without the control socket. Only its presence can be told; nothing can be asked of it.</summary>
        public static int LoadedWithoutSocket(ICollection<int> connected) {
            try {
                uint me = Native.geteuid();
                foreach (string dir in Directory.GetDirectories("/proc")) {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid == Environment.ProcessId) continue;
                    if (connected != null && connected.Contains(pid)) continue;
                    try {
                        if (!OwnedBy(dir, me)) continue;
                        foreach (string line in File.ReadLines(Path.Combine(dir, "maps")))
                            if (line.IndexOf("libMangoHud", StringComparison.Ordinal) >= 0) return pid;
                    } catch { }
                }
            } catch { }
            return 0;
        }
        static bool OwnedBy(string procDir, uint uid) {
            foreach (string line in File.ReadLines(Path.Combine(procDir, "status"))) {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                string[] f = line.Substring(4).Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                uint u;
                return f.Length > 1 && uint.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out u) && u == uid;   // the effective one
            }
            return false;
        }
    }

    /// <summary>MangoHud's log, read by column name. Everything before the line that names "frametime" and
    /// "elapsed" is about the system (and, with log_versioning, a version and two rules) and is skipped.</summary>
    public sealed class MangoHudLog {
        int iFt = -1, iElapsed = -1, cols;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>The column map, or null when the line is not the frame header.</summary>
        public static MangoHudLog Header(string line) {
            if (string.IsNullOrEmpty(line)) return null;
            var c = new MangoHudLog();
            string[] h = line.Split(',');
            for (int i = 0; i < h.Length; i++) {
                string n = h[i].Trim();
                if (n == "frametime") c.iFt = i;
                else if (n == "elapsed") c.iElapsed = i;
            }
            if (c.iFt < 0 || c.iElapsed < 0) return null;
            c.cols = h.Length;
            return c;
        }
        /// <summary>One row: the frame time as written (see Unit for what it is in) and nanoseconds since the log began.</summary>
        public bool Row(string line, out double frametime, out long elapsedNs) {
            frametime = 0; elapsedNs = 0;
            if (string.IsNullOrEmpty(line)) return false;
            string[] f = line.Split(',');
            if (f.Length < cols) return false;
            if (!double.TryParse(f[iFt], NumberStyles.Float, Inv, out frametime) || !(frametime > 0) || double.IsInfinity(frametime)) return false;
            if (!long.TryParse(f[iElapsed], NumberStyles.Integer, Inv, out elapsedNs) || elapsedNs < 0) return false;
            return true;
        }

        /// <summary>What the frametime column is in, judged against the log's own clock: the frame times of a
        /// stretch of rows add up to the time the stretch took. 1 when they are milliseconds (MangoHud today),
        /// 0.001 when they are microseconds (its older logs), and 0 when they add up to far less than the time:
        /// the log is a sample every log_interval milliseconds, not a row per frame, and no frame statistics
        /// can be made from it. NaN: not enough rows to tell yet.</summary>
        public static double Unit(List<double> frametimes, List<long> elapsedNs) {
            int n = Math.Min(frametimes.Count, elapsedNs.Count);
            if (n < 20) return double.NaN;
            double sum = 0;
            for (int i = 1; i < n; i++) sum += frametimes[i];
            double took = (elapsedNs[n - 1] - elapsedNs[0]) / 1e6;
            if (!(took > 0)) return double.NaN;
            double r = sum / took;
            if (r > 0.5 && r < 2) return 1;
            if (r > 500 && r < 2000) return 0.001;
            if (r < 0.5) return 0;
            return double.NaN;
        }
    }

    /// <summary>Frames from the games that have a MangoHud control socket: all of them while Ohman is looking for
    /// the one in front (pid 0), then that one alone. Its own thread connects, asks for a log, finds the file and
    /// follows it; a row is raised for every frame logged.</summary>
    sealed class MangoHudSource : IFrameSource {
        public event Action<FrameRow> Row;
        public bool Ended { get { return ended; } }
        public string Why { get { return why; } }
        public string Note { get { return note; } }
        volatile bool ended, stopping;
        volatile string why = "", note = "";
        int stopped;
        readonly int only;                                // the one process wanted, or 0 for every one
        readonly Thread reader;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, double> refused = new Dictionary<string, double>();   // sockets not to try again before this time
        double scanAt = -10, loadedAt = -10, secondAt;
        int loadedPid;
        bool hadSession;
        static double Mono() { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }

        sealed class Session {
            public string Name;
            public Socket Sock;
            public int Pid;
            public bool WeStarted;                        // the log is one Ohman asked for: stop it and remove it afterwards
            public string LogLink, LogPath;               // /proc/PID/fd/N, and where that points
            public FileStream Log;
            public MangoHudLog Csv;
            public readonly StringBuilder Part = new StringBuilder();
            public bool Backlog;                          // the log was already running: what is in it so far is not ours to count
            public double Unit = double.NaN;
            public long Anchor;                           // Stopwatch ticks at the log's zero
            public bool Anchored;
            public readonly List<double> HeldFt = new List<double>();
            public readonly List<long> HeldNs = new List<long>();
            public int RowsThisSecond;
            public double AskedAt;
            public bool Gone;
            public bool Sampled;                          // its log is a sample every so often, not a row a frame
        }

        public MangoHudSource(int pid) {
            only = pid;
            secondAt = Mono();
            Adopt();
            reader = new Thread(Run) { IsBackground = true, Name = "mangohud" };
            reader.Start();
        }

        void Run() {
            try {
                while (!stopping) {
                    double now = Mono();
                    if (now - scanAt >= 1) { scanAt = now; Scan(now); }
                    foreach (var s in new List<Session>(sessions.Values)) {
                        if (s.Gone) continue;
                        try { Follow(s, now); } catch (Exception ex) { Log.Write("mangohud " + s.Name + ": " + ex.Message); Drop(s); }
                    }
                    if (now - secondAt >= 1) {
                        secondAt = now;
                        foreach (var s in sessions.Values) { if (!s.Gone) MangoHud.Rate(s.Pid, s.RowsThisSecond); s.RowsThisSecond = 0; }
                        Say(now);
                    }
                    // One process wanted and it is gone: the run cannot go on, and says so at once.
                    if (only > 0 && hadSession && Live() == 0 && !ended) { if (why.Length == 0) why = "the game closed its MangoHud socket"; ended = true; Log.Write("MangoHud: " + why); }
                    Thread.Sleep(40);
                }
            } catch (Exception ex) { if (!stopping) { why = "reading MangoHud: " + ex.Message; ended = true; Log.Write(why); } }
        }
        int Live() { int n = 0; foreach (var s in sessions.Values) if (!s.Gone) n++; return n; }

        /// <summary>What the page says while nothing is drawing yet: the nearest thing to what is wrong.</summary>
        void Say(double now) {
            if (only > 0) { note = ""; return; }
            int live = 0, drawing = 0; Session first = null;
            Session sampled = null;
            foreach (var s in sessions.Values) { if (s.Gone) continue; live++; if (first == null) first = s; if (s.Sampled) sampled = s; if (MangoHud.RateOf(s.Pid) > 0) drawing++; }
            if (drawing > 0) { note = ""; return; }
            if (sampled != null) { note = Games.Stem(sampled.Pid, stems) + " logs at an interval: set log_interval=0"; return; }
            if (live > 0) { note = Games.Stem(first.Pid, stems) + " has MangoHud: waiting for it to draw"; return; }
            if (now - loadedAt >= 5) { loadedAt = now; loadedPid = MangoHud.LoadedWithoutSocket(null); }
            if (loadedPid > 0) { note = Games.Stem(loadedPid, stems) + " has MangoHud, but not its control socket"; return; }
            note = MangoHud.Installed ? "No game with MangoHud is running" : "MangoHud is not installed";
        }
        readonly Dictionary<int, string> stems = new Dictionary<int, string>();

        /// <summary>Look for sockets that are new, connect, and ask each for a log.</summary>
        void Scan(double now) {
            foreach (string name in MangoHud.Sockets()) {
                Session old;
                if (sessions.TryGetValue(name, out old)) { if (!old.Gone) continue; sessions.Remove(name); }
                double until;
                if (refused.TryGetValue(name, out until) && now < until) continue;
                // A socket whose owner is known and is not the one wanted is left alone: connecting takes its one seat.
                int known = MangoHud.OwnerOf(name);
                if (only > 0 && known > 0 && known != only) continue;
                Session s = Connect(name);
                if (s == null) { refused[name] = now + 3; continue; }
                MangoHud.Seen(name, s.Pid);
                if (only > 0 && s.Pid != only) { Close(s.Sock); continue; }
                sessions[name] = s;
                hadSession = true;
                MangoHud.Rate(s.Pid, 0);
                // A log the owner already has running is theirs: read it, do not stop it, do not remove it.
                string link, path;
                if (FindLog(s.Pid, out link, out path)) { s.LogLink = link; s.LogPath = path; s.Backlog = true; Log.Write("MangoHud: pid " + s.Pid + " is already logging; reading that log"); }
                else { s.WeStarted = true; Send(s, ":logging=1;"); }
                s.AskedAt = now;
                Log.Write("MangoHud: connected to " + name + " (pid " + s.Pid + ")");
            }
        }

        static Session Connect(string name) {
            Socket sock = null;
            try {
                sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                // Abstract: a NUL in front. A socket whose one seat is taken makes connect wait; do not wait long.
                var t = sock.ConnectAsync(new UnixDomainSocketEndPoint("\0" + name));
                if (!t.Wait(300) || !sock.Connected) { Close(sock); return null; }
                var cred = new byte[12];                                  // struct ucred: pid, uid, gid
                sock.GetRawSocketOption(1, 17, cred);                     // SOL_SOCKET, SO_PEERCRED
                int pid = BitConverter.ToInt32(cred, 0);
                uint uid = BitConverter.ToUInt32(cred, 4);
                if (pid <= 0 || uid != Native.geteuid()) { Close(sock); return null; }   // somebody else's game is not ours to command
                sock.Blocking = false;
                return new Session { Name = name, Sock = sock, Pid = pid };
            } catch { Close(sock); return null; }
        }
        static void Close(Socket s) { try { if (s != null) s.Dispose(); } catch { } }
        static bool Send(Session s, string cmd) {
            try { s.Sock.Send(Encoding.ASCII.GetBytes(cmd)); return true; } catch { return false; }
        }

        /// <summary>The log a process is writing: a file it holds open whose name ends the way MangoHud ends a
        /// log's, a date and a time and .csv. The summary beside it is only open for an instant, and never has
        /// that ending.</summary>
        internal static bool FindLog(int pid, out string link, out string path) {
            link = path = null;
            try {
                foreach (string fd in Directory.GetFileSystemEntries("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/fd")) {
                    string to;
                    try { var fi = new FileInfo(fd); to = fi.LinkTarget; } catch { continue; }
                    if (to != null && IsLogName(to)) { link = fd; path = to; return true; }
                }
            } catch { }
            return false;
        }
        /// <summary>..._YYYY-MM-DD_HH-MM-SS.csv</summary>
        internal static bool IsLogName(string path) {
            if (path == null || !path.EndsWith(".csv", StringComparison.Ordinal) || path.Length < 24) return false;
            string stamp = path.Substring(path.Length - 24, 20);              // _2026-01-31_12-00-00
            const string shape = "_dddd-dd-dd_dd-dd-dd";
            for (int i = 0; i < 20; i++) {
                if (shape[i] == 'd' ? !(stamp[i] >= '0' && stamp[i] <= '9') : stamp[i] != shape[i]) return false;
            }
            return true;
        }

        /// <summary>One session's turn: see whether the game hung up, find its log once it exists, read what is new.</summary>
        void Follow(Session s, double now) {
            // What MangoHud says is not needed; reading is how a closed socket shows.
            try {
                var buf = new byte[512];
                while (s.Sock.Available > 0) if (s.Sock.Receive(buf) <= 0) break;
                if (s.Sock.Poll(0, SelectMode.SelectRead) && s.Sock.Available == 0) { Drop(s); return; }
            } catch (SocketException) { Drop(s); return; }
            if (s.Log == null) {
                if (s.LogLink == null) {
                    string link, path;
                    if (!FindLog(s.Pid, out link, out path)) {
                        // Asked, and no log yet: MangoHud reads its socket once a frame, so a game that is not drawing
                        // has not heard. Ask again now and then; a second ask is ignored while a log is running.
                        if (s.WeStarted && now - s.AskedAt > 5) { s.AskedAt = now; Send(s, ":logging=1;"); }
                        return;
                    }
                    s.LogLink = link; s.LogPath = path;
                }
                s.Log = new FileStream(s.LogLink, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            var chunk = new byte[16384];
            long stamp = Stopwatch.GetTimestamp();
            int got;
            bool any = false;
            while ((got = s.Log.Read(chunk, 0, chunk.Length)) > 0) {
                any = true;
                s.Part.Append(Encoding.ASCII.GetString(chunk, 0, got));
            }
            if (!any) return;
            string text = s.Part.ToString();
            int end = text.LastIndexOf('\n');
            if (end < 0) return;
            s.Part.Remove(0, end + 1);
            string[] lines = text.Substring(0, end).Split('\n');
            long lastNs = -1;
            var fts = new List<double>(); var nss = new List<long>();
            foreach (string raw in lines) {
                string line = raw.TrimEnd('\r');
                if (s.Csv == null) { s.Csv = MangoHudLog.Header(line); continue; }
                double ft; long ns;
                if (!s.Csv.Row(line, out ft, out ns)) continue;
                fts.Add(ft); nss.Add(ns); lastNs = ns;
            }
            if (lastNs < 0) return;
            // The log's clock against ours: the newest row was written a moment before it was read.
            if (!s.Anchored) { s.Anchor = stamp - (long)(lastNs * (Stopwatch.Frequency / 1e9)); s.Anchored = true; }
            if (s.Backlog) {
                // Everything written before Ohman came is somebody else's recording: only its unit is taken from it.
                s.Backlog = false;
                if (double.IsNaN(s.Unit)) s.Unit = MangoHudLog.Unit(fts, nss);
                if (s.Unit == 0) { Sampled(s); }
                return;
            }
            if (double.IsNaN(s.Unit)) {
                s.HeldFt.AddRange(fts); s.HeldNs.AddRange(nss);
                s.Unit = MangoHudLog.Unit(s.HeldFt, s.HeldNs);
                if (double.IsNaN(s.Unit)) { if (s.HeldFt.Count > 400) s.Unit = 1; else return; }
                if (s.Unit == 0) { Sampled(s); return; }
                fts = new List<double>(s.HeldFt); nss = new List<long>(s.HeldNs);
                s.HeldFt.Clear(); s.HeldNs.Clear();
            }
            if (s.Unit == 0) return;
            var h = Row;
            double perNs = Stopwatch.Frequency / 1e9;
            for (int i = 0; i < fts.Count; i++) {
                s.RowsThisSecond++;
                if (h != null) h(new FrameRow { Pid = s.Pid, Qpc = s.Anchor + (long)(nss[i] * perNs), Ms = fts[i] * s.Unit, Displayed = false, DisplayMs = 0, Swap = 1, Mode = 7 });
            }
        }

        /// <summary>The log is a sample every so often, not a row a frame: the owner's log_interval is not 0.</summary>
        void Sampled(Session s) {
            if (!s.Sampled) Log.Write("MangoHud: pid " + s.Pid + " logs at an interval, not every frame (log_interval is not 0)");
            s.Sampled = true;
            if (only > 0) { why = "MangoHud logs every few ms, not every frame: set log_interval=0"; ended = true; }
        }

        void Drop(Session s) {
            if (s.Gone) return;
            s.Gone = true;
            MangoHud.Rate(s.Pid, -1);
            MangoHud.Forget(s.Name);
            try { if (s.Log != null) s.Log.Dispose(); } catch { }
            s.Log = null;
            Close(s.Sock);
            // The game is gone and its log with nobody to stop it: MangoHud closed the file when it died. Ours goes.
            if (s.WeStarted && s.LogPath != null) Remove(s.Pid, s.LogPath);
            Log.Write("MangoHud: " + s.Name + " (pid " + s.Pid + ") closed");
        }

        /// <summary>Remove a log Ohman asked for, and the summary MangoHud writes beside it when it stops. Only a
        /// file with a log's name, and only through the path the game itself had open.</summary>
        static void Remove(int pid, string path) {
            if (!IsLogName(path)) return;
            string summary = path.Substring(0, path.Length - 4) + "_summary.csv";
            foreach (string p in new[] { path, summary }) {
                try {
                    if (File.Exists(p)) { File.Delete(p); continue; }
                    // A sandboxed game names its files as it sees them; its root is reachable while it lives.
                    string inside = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/root" + p;
                    if (File.Exists(inside)) File.Delete(inside);
                } catch (Exception ex) { Log.Write("MangoHud: could not remove " + Path.GetFileName(p) + ": " + ex.Message); }
            }
        }

        /// <summary>Let go of the games. Not at once: the benchmark stops the source that watched every game and
        /// starts one for the game it chose a moment later, and a log stopped and started again in between would
        /// be two files racing each other. So the sessions are parked for a moment, the next source takes the ones
        /// it wants as they are, and whatever nobody took is then stopped and tidied up, on a thread of its own
        /// (MangoHud writes a summary when a log stops, and only once the game draws its next frame).</summary>
        public void Stop() {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            stopping = true;
            try { reader.Join(1000); } catch { }
            int n = 0;
            lock (parked) {
                foreach (var s in sessions.Values) { if (s.Gone) continue; parked.Add(s); n++; }
            }
            sessions.Clear();
            if (n == 0) return;
            // Not a background thread: at exit it still gets its two seconds to stop the logs it started.
            new Thread(delegate() {
                Thread.Sleep(400);
                List<Session> mine;
                lock (parked) { mine = new List<Session>(parked); parked.Clear(); }
                Finish(mine);
            }) { IsBackground = false, Name = "mangohud-stop" }.Start();
        }
        static readonly List<Session> parked = new List<Session>();

        /// <summary>Take over the sessions the source before this one left, as far as this one wants them.</summary>
        void Adopt() {
            lock (parked) {
                for (int i = parked.Count - 1; i >= 0; i--) {
                    var s = parked[i];
                    if (only > 0 && s.Pid != only) continue;
                    parked.RemoveAt(i);
                    sessions[s.Name] = s;
                    hadSession = true;
                }
            }
        }

        static void Finish(List<Session> list) {
            var mine = new List<Session>();
            foreach (var s in list) {
                MangoHud.Rate(s.Pid, -1);
                try { if (s.Log != null) s.Log.Dispose(); } catch { }
                s.Log = null;
                s.Part.Length = 0;
                if (s.WeStarted) { Send(s, ":logging=0;"); mine.Add(s); }
                else Close(s.Sock);
            }
            if (mine.Count == 0) return;
            // ":LoggingFinished" comes once the summary is written; a game that is not drawing never answers.
            double until = Mono() + 2.0;
            var left = new List<Session>(mine);
            var buf = new byte[512];
            while (left.Count > 0 && Mono() < until) {
                for (int i = left.Count - 1; i >= 0; i--) {
                    var s = left[i];
                    try {
                        while (s.Sock.Available > 0) {
                            int n = s.Sock.Receive(buf);
                            if (n <= 0) break;
                            s.Part.Append(Encoding.ASCII.GetString(buf, 0, n));
                        }
                        if (s.Part.ToString().IndexOf("LoggingFinished", StringComparison.Ordinal) >= 0) left.RemoveAt(i);
                    } catch { left.RemoveAt(i); }
                }
                Thread.Sleep(50);
            }
            foreach (var s in mine) {
                // Still logging (it has not drawn a frame since, so it never heard): the file is in use and stays.
                // The stop is in its socket and takes effect with its next frame; that one log is left behind.
                string link, path;
                bool still = FindLog(s.Pid, out link, out path) && path == s.LogPath;
                if (!still && s.LogPath != null) Remove(s.Pid, s.LogPath);
                else if (still) Log.Write("MangoHud: pid " + s.Pid + " has not stopped its log yet; " + Path.GetFileName(s.LogPath) + " is left where it is");
                Close(s.Sock);
            }
        }
        public void Dispose() { Stop(); }
    }
}
