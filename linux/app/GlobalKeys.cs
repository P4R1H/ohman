// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: global hotkeys. The Windows window uses RegisterHotKey and WM_HOTKEY (src\Ui.cs); there is no
// one call for that here, so this is what works where it works:
//
//   X11        XGrabKey on the root window, through a connection of our own. A key another program has already
//              grabbed is refused (BadAccess), which is how Register says "busy", as RegisterHotKey does.
//   Wayland    nothing. A Wayland compositor does not let a client see keys while another window has the
//              keyboard, and XGrabKey through XWayland only fires while an X11 window is focused, which is worse
//              than nothing: a shortcut that works sometimes. Available is false and Why says so. (The portal for
//              this, org.freedesktop.portal.GlobalShortcuts, asks the user to bind each shortcut in the
//              compositor's own dialog and is not on every desktop yet; it is the way to add this later.)
//
// The OMEN key is not a hotkey: it arrives from the firmware through the core's BiosEvents, on any session.
//
// A Hotkey is a Windows virtual key and RegisterHotKey's modifier bits on every system (src\Hotkeys.cs), so this
// file is where those become an X keysym and X modifier masks.
//
// One thread owns the connection: it sleeps in poll() on the X socket and on a pipe, and Register / Unregister
// hand it their work through the pipe and wait for the answer. Xlib is never entered from two threads.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohman {
    static class GlobalKeys {
        /// <summary>Raised on the X thread with the id given to Register. The window posts it to its dispatcher.</summary>
        public static event Action<int> Pressed;

        /// <summary>Whether a hotkey can be registered in this session at all.</summary>
        public static bool Available { get { Probe(); return available; } }
        /// <summary>Why not, in words fit for the Settings row and the log. Empty when Available.</summary>
        public static string Why { get { Probe(); return why; } }

        static bool probed, available;
        static string why = "";
        static void Probe() {
            if (probed) return;
            probed = true;
            string wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "";
            string session = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "").ToLowerInvariant();
            if (wayland.Length > 0 || session == "wayland") { why = "a Wayland session does not offer global shortcuts to applications"; return; }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) { why = "there is no X display"; return; }
            available = true;
        }

        // ---------- Xlib ----------
        const string X = "libX11.so.6";
        [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(X)] static extern int XCloseDisplay(IntPtr dpy);
        [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
        [DllImport(X)] static extern int XConnectionNumber(IntPtr dpy);
        [DllImport(X)] static extern int XPending(IntPtr dpy);
        [DllImport(X)] static extern int XNextEvent(IntPtr dpy, IntPtr ev);
        [DllImport(X)] static extern int XSync(IntPtr dpy, int discard);
        [DllImport(X)] static extern byte XKeysymToKeycode(IntPtr dpy, IntPtr keysym);
        [DllImport(X)] static extern int XGrabKey(IntPtr dpy, int keycode, uint modifiers, IntPtr window, int ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(X)] static extern int XUngrabKey(IntPtr dpy, int keycode, uint modifiers, IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int XErrorHandler(IntPtr dpy, IntPtr ev);
        [DllImport(X)] static extern IntPtr XSetErrorHandler(XErrorHandler handler);
        [DllImport(X, EntryPoint = "XSetErrorHandler")] static extern IntPtr XRestoreErrorHandler(IntPtr handler);

        [StructLayout(LayoutKind.Sequential)] struct PollFd { public int fd; public short events, revents; }
        [DllImport("libc", SetLastError = true)] static extern int poll([In, Out] PollFd[] fds, uint n, int timeout);
        [DllImport("libc", SetLastError = true)] static extern int pipe(int[] fds);
        [DllImport("libc", SetLastError = true)] static extern IntPtr read(int fd, byte[] buf, IntPtr n);
        [DllImport("libc", SetLastError = true)] static extern IntPtr write(int fd, byte[] buf, IntPtr n);
        [DllImport("libc")] static extern int close(int fd);

        const int KeyPress = 2, KeyRelease = 3, GrabModeAsync = 1;
        const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 0x10, Mod4Mask = 0x40;
        // Caps Lock and Num Lock are modifiers to the X server, so a grab for Ctrl+Alt+E does not fire with Num Lock
        // on. Each binding is grabbed four times, once per combination of the two.
        static readonly uint[] Locks = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };
        const uint Ours = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;

        sealed class Grab { public int Keycode; public uint Mods; }
        static readonly Dictionary<int, Grab> grabs = new Dictionary<int, Grab>();      // by id; touched on the X thread only
        static readonly Queue<Action> work = new Queue<Action>();
        static IntPtr dpy, rootWindow;
        static Thread thread;
        static int pipeRead = -1, pipeWrite = -1;
        static volatile bool closing, running;
        static bool grabRefused;
        static XErrorHandler onError;                  // kept alive: the X library holds the pointer

        /// <summary>Grab one binding. False when the session has no global hotkeys, the key has no X counterpart, or
        /// another program already holds the combination.</summary>
        public static bool Register(int id, uint mods, uint vk) {
            if (!Available || !Start() || !running) return false;
            bool ok = false;
            Do(delegate {
                Release(id);
                IntPtr sym = Keysym(vk);
                int code = sym == IntPtr.Zero ? 0 : XKeysymToKeycode(dpy, sym);
                if (code == 0) return;
                uint m = 0;
                if ((mods & Hotkey.Ctrl) != 0) m |= ControlMask;
                if ((mods & Hotkey.Alt) != 0) m |= Mod1Mask;
                if ((mods & Hotkey.Shift) != 0) m |= ShiftMask;
                if ((mods & Hotkey.Win) != 0) m |= Mod4Mask;
                // A refused grab comes back as an X error, later, on whatever handler the process has. Ours is in
                // place only for the grab and the XSync that flushes its answer out, then the previous one returns.
                grabRefused = false;
                IntPtr before = XSetErrorHandler(onError);
                foreach (uint l in Locks) XGrabKey(dpy, code, m | l, rootWindow, 0, GrabModeAsync, GrabModeAsync);
                XSync(dpy, 0);
                XRestoreErrorHandler(before);
                if (grabRefused) {
                    before = XSetErrorHandler(onError);
                    foreach (uint l in Locks) XUngrabKey(dpy, code, m | l, rootWindow);
                    XSync(dpy, 0);
                    XRestoreErrorHandler(before);
                    return;
                }
                grabs[id] = new Grab { Keycode = code, Mods = m };
                ok = true;
            });
            return ok;
        }

        public static void Unregister(int id) {
            if (thread == null) return;
            Do(delegate { Release(id); });
        }

        /// <summary>Let every grab go and close the connection. The X server would do the same when the process ends.</summary>
        public static void Close() {
            if (thread == null) return;
            Do(delegate { foreach (int id in new List<int>(grabs.Keys)) Release(id); });
            closing = true;
            Wake();
            try { thread.Join(1000); } catch { }
            thread = null;
        }

        static void Release(int id) {
            Grab g;
            if (!grabs.TryGetValue(id, out g)) return;
            grabs.Remove(id);
            foreach (uint l in Locks) XUngrabKey(dpy, g.Keycode, g.Mods | l, rootWindow);
            XSync(dpy, 0);
        }

        /// <summary>The key a Windows virtual key stands for, as an X keysym. Zero when there is none we know.</summary>
        static IntPtr Keysym(uint vk) {
            if (vk >= 'A' && vk <= 'Z') return new IntPtr(vk + 32);                 // the lowercase letter is the unshifted keysym
            if (vk >= '0' && vk <= '9') return new IntPtr(vk);
            if (vk >= 0x70 && vk <= 0x87) return new IntPtr(0xFFBE + (vk - 0x70));  // F1..F24
            switch (vk) {
                case 0x20: return new IntPtr(0x20);     // Space
                case 0x1B: return new IntPtr(0xFF1B);   // Esc
                case 0x0D: return new IntPtr(0xFF0D);   // Enter
                case 0x09: return new IntPtr(0xFF09);   // Tab
                case 0x21: return new IntPtr(0xFF55);   // PageUp
                case 0x22: return new IntPtr(0xFF56);   // PageDown
                case 0x23: return new IntPtr(0xFF57);   // End
                case 0x24: return new IntPtr(0xFF50);   // Home
                case 0x25: return new IntPtr(0xFF51);   // Left
                case 0x26: return new IntPtr(0xFF52);   // Up
                case 0x27: return new IntPtr(0xFF53);   // Right
                case 0x28: return new IntPtr(0xFF54);   // Down
                case 0x2D: return new IntPtr(0xFF63);   // Insert
                case 0x2E: return new IntPtr(0xFFFF);   // Delete
                case 0x2C: return new IntPtr(0xFF61);   // PrintScreen
                case 0x91: return new IntPtr(0xFF14);   // ScrollLock
                case 0x13: return new IntPtr(0xFF13);   // Pause
            }
            return IntPtr.Zero;
        }

        // ---------- the thread ----------
        static readonly object startLock = new object();
        static bool Start() {
            lock (startLock) {
                if (thread != null) return true;
                try {
                    dpy = XOpenDisplay(IntPtr.Zero);
                    if (dpy == IntPtr.Zero) { available = false; why = "the X display could not be opened"; return false; }
                    rootWindow = XDefaultRootWindow(dpy);
                    var p = new int[2];
                    if (pipe(p) != 0) { XCloseDisplay(dpy); dpy = IntPtr.Zero; available = false; why = "no pipe for the hotkey thread"; return false; }
                    pipeRead = p[0];
                    pipeWrite = p[1];
                    onError = delegate(IntPtr d, IntPtr ev) { grabRefused = true; return 0; };
                    closing = false;
                    running = true;
                    thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
                    thread.Start();
                    return true;
                } catch (Exception ex) {
                    // libX11 missing, or anything else: no hotkeys, and the row says why
                    available = false;
                    why = "Xlib is not usable here (" + ex.Message + ")";
                    Log.Write("hotkeys: " + ex.Message);
                    return false;
                }
            }
        }

        static void Wake() { try { write(pipeWrite, new byte[] { 1 }, new IntPtr(1)); } catch { } }

        /// <summary>Run a piece of work on the X thread and wait for it.</summary>
        static void Do(Action a) {
            if (Thread.CurrentThread == thread) { a(); return; }
            if (!running) return;                          // the connection has gone: nobody would answer
            using (var done = new ManualResetEventSlim(false)) {
                lock (work) work.Enqueue(delegate { try { a(); } catch (Exception ex) { Log.Write("hotkeys: " + ex.Message); } finally { done.Set(); } });
                Wake();
                done.Wait(2000);
            }
        }

        static void Loop() {
            IntPtr ev = Marshal.AllocHGlobal(256);         // an XEvent is 24 longs
            var fds = new PollFd[2];
            var one = new byte[16];
            int lastReleaseCode = 0;
            long lastReleaseTime = -1;
            try {
                int xfd = XConnectionNumber(dpy);
                while (!closing) {
                    while (XPending(dpy) > 0) {
                        XNextEvent(dpy, ev);
                        int type = Marshal.ReadInt32(ev, 0);
                        if (type != KeyPress && type != KeyRelease) continue;
                        // XKeyEvent on a 64-bit system: time at 56, state at 80, keycode at 84
                        long time = Marshal.ReadInt64(ev, 56);
                        uint state = (uint)Marshal.ReadInt32(ev, 80) & Ours;
                        int code = Marshal.ReadInt32(ev, 84);
                        if (type == KeyRelease) { lastReleaseCode = code; lastReleaseTime = time; continue; }
                        // A held key repeats as release and press with the same timestamp. RegisterHotKey is asked
                        // for MOD_NOREPEAT on Windows for the same reason: a toggle sent twice is a toggle not sent.
                        if (code == lastReleaseCode && time == lastReleaseTime) continue;
                        foreach (var kv in grabs) {
                            if (kv.Value.Keycode != code || kv.Value.Mods != state) continue;
                            var h = Pressed;
                            if (h != null) { try { h(kv.Key); } catch (Exception ex) { Log.Write("hotkey: " + ex.Message); } }
                            break;
                        }
                    }
                    while (true) {
                        Action a = null;
                        lock (work) if (work.Count > 0) a = work.Dequeue();
                        if (a == null) break;
                        a();
                    }
                    if (closing) break;
                    if (XPending(dpy) > 0) continue;
                    fds[0].fd = xfd; fds[0].events = 1; fds[0].revents = 0;       // POLLIN
                    fds[1].fd = pipeRead; fds[1].events = 1; fds[1].revents = 0;
                    poll(fds, 2, -1);
                    if ((fds[1].revents & 1) != 0) read(pipeRead, one, new IntPtr(one.Length));
                    if ((fds[0].revents & (8 | 16)) != 0) { Log.Write("hotkeys: the X connection closed"); break; }   // POLLERR, POLLHUP
                }
            } catch (Exception ex) { Log.Write("hotkeys: " + ex); }
            finally {
                running = false;
                Marshal.FreeHGlobal(ev);
                try { XCloseDisplay(dpy); } catch { }
                try { close(pipeRead); close(pipeWrite); } catch { }
                dpy = IntPtr.Zero;
                // anything still waiting must not wait for a thread that has gone
                while (true) { Action a = null; lock (work) if (work.Count > 0) a = work.Dequeue(); if (a == null) break; }
            }
        }
    }
}
