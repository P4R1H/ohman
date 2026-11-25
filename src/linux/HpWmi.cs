// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the kernel's own HP driver, hp-wmi, as far as Ohman has to know about it. Two things.
//
// Its fan control, which is two different things depending on the kernel, and they must not be confused.
//
// Before hp-wmi learned manual fan control (the rewrite that landed for 7.0) there is no pwm1 file, and
// pwm1_enable is not a setting at all: reading it asks the firmware for its max-fan flag there and then
// (mailbox 0x26; "0" means max fan is on), and writing "2" sends 0x27 {0}, max fan OFF. The driver keeps no
// state and re-sends nothing. On such a kernel Ohman must never write that file: "putting it on automatic"
// would switch off the max fan Ohman's own guard or Max mode has just switched on.
//
// From the rewrite on there is a pwm1 file, the mode is the driver's own cached choice, and while it is 0 (its
// max) or 1 (manual) the driver re-sends that state to the firmware about every 90 s, over whatever Ohman has
// set. That is worth correcting, but only once and only when it is somebody else's leftover: at the moment
// Ohman takes over, before it has sent anything of its own. Writing 2 makes the driver send 0x27 {0} and a
// zero fan level once, so doing it later, or periodically, would cancel what Ohman is doing. Ohman's own
// commands go through the mailbox and never move the driver's cached mode, so a 0 or 1 found at that moment
// cannot be Ohman's doing.
//
// Its hotkeys. hp-wmi owns the firmware's event GUID, so the events Windows delivers as hpqBEvnt never reach
// user space here; the driver turns the keys into input events on a device named "HP WMI hotkeys" instead.
// It uses a sparse keymap, which sends the firmware's own key code as MSC_SCAN just before the key: for the
// OMEN key that is 0x21A5 = 8613, the very number Windows reports as the event data of event 29. So reading
// that device gives back the pair the shared engine already matches on (Engine.OnBiosEvent).
//
// Needs root for the write and for /dev/input (or membership of group "input"), so it runs inside ohman-helper.
// UNTESTED on real hardware: written from hp-wmi.c and the input layer's documentation.
using System;
using System.IO;
using System.Threading;

namespace Ohman {
    static class HpWmi {
        public const string Dir = "/sys/devices/platform/hp-wmi";
        public const string HotkeyName = "HP WMI hotkeys";

        public static bool Present { get { return Directory.Exists(Dir); } }

        /// <summary>The driver's hwmon directory, or null: only kernels whose hp-wmi knows this board's fans have one.</summary>
        public static string Hwmon() {
            try {
                string h = Path.Combine(Dir, "hwmon");
                if (Directory.Exists(h)) foreach (string d in Directory.GetDirectories(h)) return d;
            } catch { }
            return null;
        }

        /// <summary>pwm1_enable as text: "0", "1", "2", or "" when the driver has no fan control here.</summary>
        public static string FanMode() {
            string h = Hwmon();
            return h == null ? "" : Host.ReadText(Path.Combine(h, "pwm1_enable"));
        }

        /// <summary>Whether this kernel's hp-wmi keeps a fan mode of its own and re-sends it: true exactly when
        /// its hwmon directory has a pwm1 file. False on older kernels, where pwm1_enable is the firmware's live
        /// max-fan flag and is never to be written.</summary>
        public static bool CachesFanMode(string hwmon) { return hwmon != null && File.Exists(Path.Combine(hwmon, "pwm1")); }

        /// <summary>Called once, when Ohman takes the fans over: if the stock driver was left on its own max or
        /// manual mode by something else, put it on automatic so its 90 s keep-alive stops. Writes nothing on a
        /// kernel without a cached mode, and nothing when the mode is already automatic. True when a write was
        /// made; note says what was found and done, for the log. hwmon is a parameter so this can be checked
        /// against a directory of ordinary files.</summary>
        public static bool TakeOverFrom(string hwmon, out string note) {
            if (hwmon == null) { note = "hp-wmi has no fan control on this machine; nothing to take over"; return false; }
            string file = Path.Combine(hwmon, "pwm1_enable");
            if (!CachesFanMode(hwmon)) {
                note = "hp-wmi here has no pwm1: its pwm1_enable is the firmware's live max-fan flag, not a mode, and is left alone";
                return false;
            }
            string was = Host.ReadText(file);
            if (was != "0" && was != "1") { note = "hp-wmi fan mode is " + (was.Length == 0 ? "unreadable" : was) + "; left alone"; return false; }
            try {
                File.WriteAllText(file, "2");
                note = "hp-wmi was left in its own " + (was == "0" ? "max" : "manual") + " fan mode (pwm1_enable=" + was + "), which it re-sends every 90 s; set to automatic (2) once, before Ohman drives the fans";
                return true;
            } catch (Exception ex) { note = "hp-wmi fan mode is " + was + " and could not be set to automatic: " + ex.Message; return false; }
        }

        /// <summary>/dev/input/eventN of the hotkey device, or null while it is not there (hp-wmi can load late).</summary>
        public static string HotkeyDevice() {
            try {
                const string root = "/sys/class/input";
                if (!Directory.Exists(root)) return null;
                foreach (string dir in Directory.GetDirectories(root, "input*")) {
                    if (Host.ReadText(Path.Combine(dir, "name")) != HotkeyName) continue;
                    foreach (string ev in Directory.GetDirectories(dir, "event*")) return "/dev/input/" + Path.GetFileName(ev);
                }
            } catch { }
            return null;
        }
    }

    /// <summary>The hotkey device, read on a thread of its own. Calls back with the (event id, event data) pair
    /// Windows would have delivered for the same key press.</summary>
    sealed class HpHotkeys : IDisposable {
        // linux/input-event-codes.h (checked against the 5.15 and 6.8 headers): EV_KEY 1, EV_MSC 4, MSC_SCAN 4,
        // KEY_PROG2 149. hp-wmi.c maps the OMEN key (0x21A5) to KEY_PROG2.
        const ushort EV_KEY = 1, EV_MSC = 4, MSC_SCAN = 4, KEY_PROG2 = 149;
        /// <summary>hpqBEvnt's id for this family of keys (HPWMI_OMEN_KEY, 0x1D), and the OMEN key's own code.</summary>
        public const uint OmenEventId = 29, OmenKeyData = 0x21A5;
        const int EventSize = 24;                 // struct input_event on 64-bit: timeval (16), type (2), code (2), value (4)

        readonly Action<uint, uint> handler;
        readonly Thread thread;
        volatile bool stop;
        public volatile string State = "not started";

        public HpHotkeys(Action<uint, uint> onEvent) {
            handler = onEvent;
            thread = new Thread(Loop) { IsBackground = true, Name = "hp-hotkeys" };
            thread.Start();
        }

        void Loop() {
            while (!stop) {
                string dev = HpWmi.HotkeyDevice();
                if (dev == null) { State = "no \"" + HpWmi.HotkeyName + "\" input device (is hp-wmi loaded?)"; Wait(5000); continue; }
                int fd = Native.open(dev, Native.O_RDONLY | Native.O_NONBLOCK | Native.O_CLOEXEC);
                if (fd < 0) { State = "cannot open " + dev + ": " + Native.Why(Native.Errno); Wait(5000); continue; }
                State = "reading " + dev;
                try { Read(fd); } catch (Exception ex) { State = "reading " + dev + " failed: " + ex.Message; }
                Native.close(fd);
                if (!stop) Wait(1000);            // the device went away (module reloaded, resume): look for it again
            }
        }

        void Wait(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

        void Read(int fd) {
            var pfd = new byte[8];                // struct pollfd { int fd; short events; short revents; }
            var ev = new byte[EventSize];
            uint scan = 0;
            while (!stop) {
                Native.Put32(pfd, 0, (uint)fd);
                pfd[4] = 1; pfd[5] = 0; pfd[6] = 0; pfd[7] = 0;           // POLLIN
                int r = Native.poll(pfd, new UIntPtr(1), 500);
                if (r == 0) continue;
                if (r < 0) { if (Native.Errno == 4) continue; return; }   // EINTR
                if ((pfd[6] & 0x18) != 0) return;                           // POLLERR | POLLHUP: gone
                long n = Native.read(fd, ev, new UIntPtr((uint)EventSize)).ToInt64();
                if (n < 0) { if (Native.Errno == Native.EAGAIN) continue; return; }
                if (n != EventSize) continue;
                ushort type = (ushort)(ev[16] | (ev[17] << 8)), code = (ushort)(ev[18] | (ev[19] << 8));
                uint value = Native.Get32(ev, 20);
                if (type == EV_MSC && code == MSC_SCAN) { scan = value; continue; }
                if (type != EV_KEY) continue;
                if (value == 1) Pressed(code, scan);                        // 1 = press; 0 release, 2 autorepeat
                if (value == 0) scan = 0;
            }
        }

        /// <summary>From a key press to the pair Windows would have given. The OMEN key always goes through, with
        /// the firmware's own code when the driver passed it along. The family's other keys (0x21xx: Fn+Esc, the
        /// touchpad toggle) arrive on Windows as the same event with their own data, so they are passed the same
        /// way, which is also what lets "learn the key" bind a board whose OMEN key has a different code.</summary>
        internal static bool Translate(ushort key, uint scan, out uint id, out uint data) {
            id = OmenEventId; data = 0;
            if (key == KEY_PROG2) { data = scan != 0 ? scan : OmenKeyData; return true; }
            if ((scan & 0xFF00) == 0x2100) { data = scan; return true; }
            return false;
        }

        void Pressed(ushort key, uint scan) {
            uint id, data;
            if (!Translate(key, scan, out id, out data)) return;
            try { handler(id, data); } catch { }
        }

        public void Dispose() { stop = true; try { thread.Join(1500); } catch { } }
    }
}
