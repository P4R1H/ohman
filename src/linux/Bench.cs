// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: what the benchmark asks of Linux. src\win\Bench.cs holds the same classes for Windows
// (FrameCapture, Games' other half, Machine, Cues). The run itself, the numbers and the stored runs are shared:
// src\Bench.cs. Frames come from MangoHud (src\linux\MangoHud.cs says how, and from what in its source).
//
// Which game: on Windows it is the process that owns the window in front. Here a game can only be measured if
// MangoHud is loaded in it, so the game is the process with MangoHud that is drawing, and the window in front
// decides only whether it counts as being in front:
//   * X11, and XWayland windows under Wayland: the active window (_NET_ACTIVE_WINDOW) and the process behind it
//     (_NET_WM_PID). When that process is the game, or something the game runs inside (gamescope), the game
//     is in front. Any other window in front pauses the run, as on Windows.
//   * Wayland, when no X11 window has the focus: a Wayland compositor tells no program which window is in front.
//     The game with MangoHud that is drawing the most is taken to be in front, so a run of a native Wayland
//     game is not paused by switching away from it.
//
// UNTESTED on a real desktop with a real game: the X11 calls were tried only against the X server WSL provides.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ohman {

    /// <summary>The frame capture as the shared benchmark sees it: is the tool there, fetch it, start it. Here it
    /// is MangoHud, which Ohman cannot fetch: it comes from the distribution, and goes into the game by the
    /// owner's own launch option.</summary>
    static class FrameCapture {
        /// <summary>What the tool is called, for the log and the page.</summary>
        public const string Tool = "MangoHud";
        /// <summary>A run can be started: MangoHud is installed, or a game with its control socket is already
        /// running (a Flatpak game can carry a MangoHud this system knows nothing about).</summary>
        public static bool Ready { get { return MangoHud.Installed || MangoHud.Sockets().Count > 0; } }
        /// <summary>Why a run cannot start when it is not.</summary>
        public const string Missing = "MangoHud is not installed";
        /// <summary>Ohman cannot get the tool itself: it is a package of the distribution.</summary>
        public const bool CanFetch = false;
        public static void Fetch() { throw new NotSupportedException("install MangoHud with your package manager"); }
        /// <summary>Frames of one process, or of every game with a control socket when pid is 0. It starts even
        /// with no game running yet, and says what it is waiting for (IFrameSource.Note).</summary>
        public static IFrameSource Start(int pid, out string error) {
            error = null;
            try { return new MangoHudSource(pid); }
            catch (Exception ex) { error = "could not start reading MangoHud: " + ex.Message; return null; }
        }
        /// <summary>Clear up after a run that ended badly. Nothing of Ohman's outlives it here: a log it started is
        /// stopped by MangoHud itself when the game closes.</summary>
        public static void Recover() { }
        /// <summary>What the page says while it waits for a game, and under it.</summary>
        public const string WaitTitle = "Switch to your game", WaitHint = "It starts once a game with MangoHud is drawing";
        /// <summary>The line to copy into a launcher so the tool is loaded with the game.</summary>
        public const string LaunchOption = MangoHud.LaunchOption;
    }

    // ================================================================== which game ================================
    /// <summary>Finding the game, the half that asks Linux: the window in front (X11), the process behind it and
    /// the screen it is on. The deny list and the names are shared (src\Bench.cs).</summary>
    public static partial class Games {
        // Programs that hold MangoHud or a window in front and are never the game: compositors, Wine's and Steam's
        // own helpers, players and recorders. Lower case, the way the shared list is.
        static readonly HashSet<string> Extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "gamescope", "gamescope-wl", "mangoapp", "mangohud", "xwayland", "kwin_wayland", "kwin_x11", "gnome-shell", "plasmashell", "mutter", "sway", "hyprland",
            "wineserver", "winedevice", "services", "plugplay", "rpcss", "tabtip", "start", "steam-runtime-launcher-service", "pressure-vessel-wrap", "pv-bwrap", "bwrap",
            "gameoverlayui", "reaper", "proton", "lutris", "heroic", "bottles", "obs", "mpv", "vlc", "chromium", "konsole", "gnome-terminal-server", "kitty", "alacritty"
        };
        /// <summary>Programs that are never the game on this system alone.</summary>
        static bool NotAGame(string stem) { return Extra.Contains(stem); }

        // ---------- the window in front ----------
        /// <summary>The game in front, or the process of whatever else is, or 0. See the top of this file for
        /// what "in front" can mean here. root is the X11 window when there is one.</summary>
        public static int Foreground(out IntPtr root) {
            root = IntPtr.Zero;
            IntPtr win; int owner;
            bool known = X11.Active(out win, out owner);
            if (known && win != IntPtr.Zero) {
                root = win;
                if (owner <= 0) return MangoHud.Busiest();             // a window that does not say whose it is
                int[] games = MangoHud.Pids();
                foreach (int p in games) if (p == owner) return p;
                // The window belongs to something the game runs inside: gamescope draws the game into its own.
                string stem = Stem(owner, null);
                if (stem.StartsWith("gamescope", StringComparison.OrdinalIgnoreCase)) {
                    int best = 0, rows = -1;
                    foreach (int p in games) if (Inside(p, owner) && MangoHud.RateOf(p) > rows) { rows = MangoHud.RateOf(p); best = p; }
                    if (best > 0) return best;
                }
                return owner;
            }
            if (known && !X11.Wayland) return 0;                        // an X11 session with nothing in front
            return MangoHud.Busiest();                                  // nobody can say: see the top of this file
        }
        /// <summary>Whether a process has another above it, a few generations up.</summary>
        static bool Inside(int pid, int ancestor) {
            for (int i = 0; i < 12 && pid > 1; i++) {
                if (pid == ancestor) return true;
                pid = Parent(pid);
            }
            return false;
        }
        static int Parent(int pid) {
            try {
                // "pid (comm) state ppid ...": comm may hold spaces and brackets, so count from the last one
                string s = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                int close = s.LastIndexOf(')');
                string[] f = s.Substring(close + 2).Split(' ');
                return int.Parse(f[1], CultureInfo.InvariantCulture);
            } catch { return 0; }
        }
        public static string Title(IntPtr h) { return h == IntPtr.Zero ? "" : X11.Title(h); }

        // ---------- the process ----------
        static readonly Dictionary<int, string> started = new Dictionary<int, string>();
        /// <summary>When the process started, in the kernel's clock ticks: with the pid, what tells it from a later
        /// process given the same number. Null when it is gone.</summary>
        static string StartTime(int pid) {
            try {
                string s = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                string[] f = s.Substring(s.LastIndexOf(')') + 2).Split(' ');
                return f.Length > 19 ? f[19] : null;
            } catch { return null; }
        }
        /// <summary>Something to ask the path of and to watch for the end of: the pid itself, with its start time
        /// remembered. IntPtr.Zero when it is gone.</summary>
        public static IntPtr Open(int pid) {
            string t = StartTime(pid);
            if (t == null) return IntPtr.Zero;
            lock (started) started[pid] = t;
            return new IntPtr(pid);
        }
        /// <summary>Let go of what Open gave.</summary>
        public static void Close(IntPtr h) { lock (started) started.Remove(h.ToInt32()); }
        /// <summary>The process behind the handle has ended.</summary>
        public static bool Exited(IntPtr h) {
            int pid = h.ToInt32();
            string was, now = StartTime(pid);
            lock (started) started.TryGetValue(pid, out was);
            return now == null || (was != null && was != now);
        }
        /// <summary>The program a process is. For a Windows game under Wine or Proton that is the .exe it was given,
        /// not Wine's loader: the argument that names one, else the process name (which the kernel cuts at 15
        /// characters). MangoHud names its logs the same way (src/file_utils.cpp, get_wine_exe_name).</summary>
        public static string PathOf(IntPtr h) {
            if (h == IntPtr.Zero) return null;
            string dir = "/proc/" + h.ToInt32().ToString(CultureInfo.InvariantCulture);
            string exe = null;
            try { exe = new FileInfo(dir + "/exe").LinkTarget; } catch { }
            try {
                string image = exe == null ? "" : Path.GetFileName(exe);
                if (image.StartsWith("wine", StringComparison.Ordinal)) {
                    foreach (string arg in File.ReadAllText(dir + "/cmdline").Split('\0'))
                        if (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return arg.Replace('\\', '/');
                    string comm = File.ReadAllText(dir + "/comm").Trim();
                    if (comm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return comm;
                }
            } catch { }
            return exe;
        }
        /// <summary>Program name without extension for a pid, or "" (the cache keeps the per-second check cheap).</summary>
        public static string Stem(int pid, Dictionary<int, string> cache) {
            string s;
            if (cache != null && cache.TryGetValue(pid, out s)) return s;
            s = "";
            string p = PathOf(new IntPtr(pid));
            if (p != null) s = Path.GetFileNameWithoutExtension(p);
            if (cache != null) {
                if (cache.Count > 500) cache.Clear();
                if (s.Length > 0) cache[pid] = s;
            }
            return s;
        }

        // ---------- the screen it is on ----------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        static int hzSeen;
        static long hzAt;
        /// <summary>The screen a window is on, in pixels, with its refresh rate: the X screen, and the built-in
        /// panel's rate as xrandr gives it (asked at most once a minute: it is a program to run, and a run is
        /// being measured). False when there is no X11 display to ask.</summary>
        public static bool Monitor(IntPtr hwnd, out RECT rc, out int hz) {
            rc = new RECT(); hz = 0;
            int w, h;
            if (!X11.Screen(out w, out h)) return false;
            rc.Right = w; rc.Bottom = h;
            long now = Stopwatch.GetTimestamp();
            if (hzAt == 0 || now - hzAt > 60 * Stopwatch.Frequency) { hzAt = now; try { hzSeen = Display.Available ? Display.CurrentHz() : 0; } catch { hzSeen = 0; } }
            hz = hzSeen;
            return true;
        }
        /// <summary>What the game draws into: its window's size in pixels.</summary>
        public static void ClientSize(IntPtr hwnd, out int w, out int h) {
            w = h = 0;
            if (hwnd != IntPtr.Zero) X11.Size(hwnd, out w, out h);
        }
    }

    /// <summary>The few Xlib calls the benchmark needs, on a connection of its own. Every call is made under one
    /// lock, and a window that vanished between two calls must not end the program: Xlib's default answer to an
    /// error is to exit, so a handler that does nothing is put in, unless the window toolkit already has its own.</summary>
    static class X11 {
        const string Lib = "libX11.so.6";
        [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
        [DllImport(Lib)] static extern int XDefaultScreen(IntPtr dpy);
        [DllImport(Lib)] static extern int XDisplayWidth(IntPtr dpy, int screen);
        [DllImport(Lib)] static extern int XDisplayHeight(IntPtr dpy, int screen);
        [DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr dpy, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);
        [DllImport(Lib)] static extern int XGetWindowProperty(IntPtr dpy, IntPtr w, IntPtr prop, IntPtr offset, IntPtr length, int delete, IntPtr reqType,
            out IntPtr type, out int format, out IntPtr items, out IntPtr after, out IntPtr data);
        [DllImport(Lib)] static extern int XGetGeometry(IntPtr dpy, IntPtr d, out IntPtr root, out int x, out int y, out uint w, out uint h, out uint border, out uint depth);
        [DllImport(Lib)] static extern int XFree(IntPtr p);
        [DllImport(Lib)] static extern int XSync(IntPtr dpy, int discard);
        [DllImport(Lib)] static extern IntPtr XSetErrorHandler(IntPtr handler);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ErrorHandler(IntPtr dpy, IntPtr ev);
        static readonly ErrorHandler quiet = delegate { return 0; };     // kept alive here: Xlib holds only the pointer

        static readonly object sync = new object();
        static IntPtr dpy, root, aActive, aPid, aName, aUtf8;
        static bool tried;

        /// <summary>The session is a Wayland one: only XWayland's windows can be seen from here.</summary>
        public static bool Wayland {
            get {
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                    || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
            }
        }

        static bool Ready() {
            if (tried) return dpy != IntPtr.Zero;
            tried = true;
            try {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
                // Whose error handler is in place? Setting none returns the one before; setting that one back returns
                // Xlib's own. If the two are the same nobody had set one, and ours goes in.
                IntPtr before = XSetErrorHandler(IntPtr.Zero), own = XSetErrorHandler(before);
                if (before == own) XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(quiet));
                dpy = XOpenDisplay(IntPtr.Zero);
                if (dpy == IntPtr.Zero) return false;
                root = XDefaultRootWindow(dpy);
                aActive = XInternAtom(dpy, "_NET_ACTIVE_WINDOW", 0);
                aPid = XInternAtom(dpy, "_NET_WM_PID", 0);
                aName = XInternAtom(dpy, "_NET_WM_NAME", 0);
                aUtf8 = XInternAtom(dpy, "UTF8_STRING", 0);
                return true;
            } catch (Exception ex) { Log.Write("X11: " + ex.Message); dpy = IntPtr.Zero; return false; }
        }

        /// <summary>One 32-bit property value (Xlib hands those over as C longs), or 0.</summary>
        static long Card(IntPtr w, IntPtr prop) {
            IntPtr type, items, after, data; int format;
            if (XGetWindowProperty(dpy, w, prop, IntPtr.Zero, new IntPtr(1), 0, IntPtr.Zero, out type, out format, out items, out after, out data) != 0 || data == IntPtr.Zero) return 0;
            try { return format == 32 && items.ToInt64() >= 1 ? Marshal.ReadIntPtr(data).ToInt64() & 0xFFFFFFFFL : 0; }
            finally { XFree(data); }
        }

        /// <summary>The window in front and the process that says it owns it. False when there is no X11 display
        /// to ask; true with no window when nothing X11 has the focus.</summary>
        public static bool Active(out IntPtr win, out int pid) {
            win = IntPtr.Zero; pid = 0;
            lock (sync) {
                if (!Ready()) return false;
                try {
                    long w = Card(root, aActive);
                    if (w == 0) return true;
                    win = new IntPtr(w);
                    pid = (int)Card(win, aPid);
                    XSync(dpy, 0);
                    return true;
                } catch { return false; }
            }
        }
        public static string Title(IntPtr w) {
            lock (sync) {
                if (!Ready()) return "";
                try {
                    IntPtr type, items, after, data; int format;
                    if (XGetWindowProperty(dpy, w, aName, IntPtr.Zero, new IntPtr(256), 0, aUtf8, out type, out format, out items, out after, out data) != 0 || data == IntPtr.Zero) return "";
                    try {
                        int n = (int)items.ToInt64();
                        if (format != 8 || n <= 0) return "";
                        var b = new byte[n];
                        Marshal.Copy(data, b, 0, n);
                        return Encoding.UTF8.GetString(b).Trim();
                    } finally { XFree(data); }
                } catch { return ""; }
            }
        }
        public static void Size(IntPtr w, out int width, out int height) {
            width = height = 0;
            lock (sync) {
                if (!Ready()) return;
                try {
                    IntPtr r; int x, y; uint cw, ch, border, depth;
                    if (XGetGeometry(dpy, w, out r, out x, out y, out cw, out ch, out border, out depth) != 0) { width = (int)cw; height = (int)ch; }
                    XSync(dpy, 0);
                } catch { }
            }
        }
        public static bool Screen(out int width, out int height) {
            width = height = 0;
            lock (sync) {
                if (!Ready()) return false;
                try { int s = XDefaultScreen(dpy); width = XDisplayWidth(dpy, s); height = XDisplayHeight(dpy, s); return width > 0; } catch { return false; }
            }
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
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal)) { int c = line.IndexOf(':'); if (c > 0) Cpu = Tidy(line.Substring(c + 1)); break; }
            } catch { }
            try {
                // NVIDIA's driver names the card and itself; the dGPU is the one games run on.
                if (Directory.Exists("/proc/driver/nvidia/gpus")) {
                    foreach (string dir in Directory.GetDirectories("/proc/driver/nvidia/gpus")) {
                        foreach (string line in File.ReadLines(Path.Combine(dir, "information")))
                            if (line.StartsWith("Model:", StringComparison.Ordinal)) { Gpu = Tidy(line.Substring(6)); break; }
                        if (Gpu.Length > 0) break;
                    }
                    GpuDriver = Host.ReadText("/sys/module/nvidia/version");
                }
                if (Gpu.Length == 0) Gpu = Tidy(FromLspci());
            } catch (Exception ex) { Log.Write("machine: " + ex.Message); }
        }
        /// <summary>The display adapter by the name the PCI id list gives it, a Radeon RX before an APU's own
        /// Radeon, and either before Intel's. Empty without lspci.</summary>
        static string FromLspci() {
            string tool = Host.Find("lspci");
            if (tool == null) return "";
            string text;
            if (Host.Run(tool, "-mm", 4000, out text) != 0) return "";
            return PickGpu(text);
        }
        /// <summary>The parser apart from the program, so it can be checked against captured output. A line of
        /// lspci -mm is: slot "class" "vendor" "device" ...</summary>
        internal static string PickGpu(string lspci) {
            string amd = null, other = null;
            foreach (string line in (lspci ?? "").Split('\n')) {
                string[] q = line.Split('"');
                if (q.Length < 6) continue;
                string cls = q[1], vendor = q[3], device = q[5];
                if (cls.IndexOf("VGA", StringComparison.Ordinal) < 0 && cls.IndexOf("3D", StringComparison.Ordinal) < 0 && cls.IndexOf("Display", StringComparison.Ordinal) < 0) continue;
                // "Navi 33 [Radeon RX 7600M XT]": the name people know is the part in brackets
                int a = device.LastIndexOf('['), b = device.LastIndexOf(']');
                string name = a >= 0 && b > a ? device.Substring(a + 1, b - a - 1) : device;
                if (name.StartsWith("Device ", StringComparison.Ordinal)) continue;      // not in the id list: a number is not a name
                if (vendor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return name;
                if (vendor.IndexOf("Advanced Micro", StringComparison.OrdinalIgnoreCase) >= 0 || vendor.IndexOf("AMD", StringComparison.Ordinal) >= 0) {
                    if (amd == null || (amd.IndexOf("RX", StringComparison.Ordinal) < 0 && name.IndexOf("RX", StringComparison.Ordinal) >= 0)) amd = name;
                }
                else if (other == null) other = (vendor.IndexOf("Intel", StringComparison.Ordinal) >= 0 ? "Intel " : "") + name;
            }
            return amd ?? other ?? "";
        }
        static string Tidy(string s) {
            s = (s ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("NVIDIA GeForce ", "").Replace("GeForce ", "").Replace("AMD Radeon(TM) ", "Radeon ").Replace(" Processor", "").Replace(" GPU", "");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            int at = s.IndexOf(" with Radeon", StringComparison.OrdinalIgnoreCase);
            if (at > 0) s = s.Substring(0, at);
            at = s.IndexOf(" w/ Radeon", StringComparison.OrdinalIgnoreCase);
            if (at > 0) s = s.Substring(0, at);
            return s.Trim();
        }
    }

    // ================================================================== sound =====================================
    /// <summary>Two short tones (Tones, in src\Bench.cs): one when measuring starts, one when it is done. The pill
    /// cannot be seen over every game; a sound can. Played by whichever of PipeWire's, PulseAudio's or ALSA's
    /// player is installed, from a file written once; silent when none is.</summary>
    static class Cues {
        static string player;
        static bool looked;
        public static void Start() { Play("cue-start.wav", Tones.Start); }
        public static void Done() { Play("cue-done.wav", Tones.Done); }
        static void Play(string file, double[] notes) {
            try {
                if (!looked) { looked = true; player = Host.Find("pw-play") ?? Host.Find("paplay") ?? Host.Find("aplay"); }
                if (player == null) return;
                string path = Path.Combine(AppPaths.StateDir, file);
                if (!File.Exists(path)) File.WriteAllBytes(path, Tones.Wav(notes));
                var psi = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true };
                if (player.EndsWith("aplay", StringComparison.Ordinal) && !player.EndsWith("paplay", StringComparison.Ordinal)) psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add(path);
                var p = Process.Start(psi);
                if (p != null) { p.EnableRaisingEvents = true; p.Exited += delegate { try { p.Dispose(); } catch { } }; }
            } catch (Exception ex) { Log.Write("cue: " + ex.Message); }
        }
    }
}
