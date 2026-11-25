// SPDX-License-Identifier: GPL-3.0-or-later
// ohman-helper: the root end of Ohman on Linux.
//
//   ohman-helper                      serve /run/ohman/helper.sock (what the systemd unit runs)
//   ohman-helper --socket PATH        serve somewhere else
//   ohman-helper --demo [--board ID]  answer from simulated hardware: no root, no firmware, for tests;
//                                     --board makes it judge EC writes as it would on that board
//
// The GUI runs as an ordinary user and hosts the whole engine. The few things that need root reach the
// hardware through this process: a mailbox call (/dev/ohman-wmi), an EC byte (the same module), an MSR
// (/dev/cpu/0/msr), the OMEN key (the stock driver's input device). That is all it does. It holds no policy
// and no engine; what it will not do is fixed by two lists it shares with the core: the EC registers a map
// names as writable (EcMap, src/Ec.cs) and the MSRs the core reads, with the one it writes (Msr,
// src/linux/Msr.cs). The wire format is in src/linux/Helper.cs.
//
// Who may connect: the socket is root:ohman 0660, and each connection's peer is checked again (SO_PEERCRED):
// root, a member of group "ohman", or the user the helper itself runs as.
//
// The stock hp-wmi driver's fan control is touched once at most: when the first program subscribes to key
// events, which is Ohman starting to drive the laptop, and only on a kernel where that driver keeps a fan mode
// of its own and was left in its max or manual one by something else (it re-sends that every 90 seconds over
// whatever Ohman sets). Never periodically, never on a kernel where pwm1_enable is the firmware's live max-fan
// flag, never for a program that only reads. The reasoning is in src/linux/HpWmi.cs.
//
// What it refuses, beyond the two lists: any EC write at all on a board no EC map applies to (the 2025 OMEN
// MAX, or a board id that cannot be read); a write of the power-limit register that would set its lock bit,
// move any bit Ohman never moves, or set a limit outside the range Ohman sets; more than sixteen connections;
// a client that stops in the middle of a request.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ohman {

    /// <summary>What the helper forwards to. Real hardware, or the core's simulated hardware.</summary>
    interface IBackend {
        bool IsDemo { get; }
        string Status();
        byte[] Mailbox(uint command, uint type, byte[] data, int outSize, out uint rc);
        int EcRead(byte register, out byte value);
        int EcWrite(byte register, byte value);
        int MsrRead(uint msr, out ulong value);
        int MsrWrite(uint msr, ulong value);
        bool Energy(out ulong microjoules, out ulong max);
        void StartHotkeys(Action<uint, uint> onEvent);
        /// <summary>Once, when Ohman takes the fans: see HpWmi.TakeOverFrom. Returns what to log, or "".</summary>
        string TakeOverStockFans();
    }

    sealed class RealBackend : IBackend {
        HpHotkeys hotkeys;
        public bool IsDemo => false;

        public string Status() {
            var sb = new StringBuilder();
            sb.Append("helper=").Append(Program.Version).Append('\n');
            sb.Append("protocol=").Append(HelperProtocol.Version).Append('\n');
            sb.Append("demo=0\n");
            sb.Append("module=").Append(KernelModule.Present ? (KernelModule.Usable ? "ready" : "present, cannot open") : "absent").Append('\n');
            int v = 0;
            try { if (KernelModule.Usable) v = KernelModule.Version(); } catch { }
            sb.Append("module_version=").Append(v).Append('\n');
            sb.Append("lockdown=").Append(Msr.Lockdown()).Append('\n');
            sb.Append("msr=").Append(Msr.Present ? "present" : "absent").Append('\n');
            sb.Append("hp_wmi=").Append(HpWmi.Present ? "loaded" : "not loaded").Append('\n');
            sb.Append("pwm1_enable=").Append(HpWmi.FanMode()).Append('\n');
            sb.Append("hotkeys=").Append(hotkeys == null ? "not started" : hotkeys.State).Append('\n');
            return sb.ToString();
        }

        public byte[] Mailbox(uint command, uint type, byte[] data, int outSize, out uint rc) => KernelModule.Call(command, type, data, outSize, out rc);
        public int EcRead(byte register, out byte value) => KernelModule.EcRead(register, out value);
        public int EcWrite(byte register, byte value) => KernelModule.EcWrite(register, value);
        public int MsrRead(uint msr, out ulong value) => Msr.Read(msr, out value);
        public int MsrWrite(uint msr, ulong value) => Msr.Write(msr, value);
        public bool Energy(out ulong microjoules, out ulong max) => Msr.RaplEnergy(out microjoules, out max);
        public void StartHotkeys(Action<uint, uint> onEvent) { hotkeys = new HpHotkeys(onEvent); }
        public string TakeOverStockFans() {
            if (!HpWmi.Present) return "";
            string note;
            HpWmi.TakeOverFrom(HpWmi.Hwmon(), out note);
            return note;
        }
    }

    /// <summary>The core's simulated laptop, answered at the level the socket speaks: raw mailbox calls, EC
    /// registers, MSRs. The mailbox half is Bios (src/Hardware.cs) read backwards onto DemoHardware, so a client
    /// on the far side of the socket sees the same machine the preview build simulates in process.</summary>
    sealed class DemoBackend : IBackend {
        readonly DemoHardware hw = new DemoHardware();
        readonly DemoEcPorts ec = new DemoEcPorts();
        readonly DemoCpu cpu = new DemoCpu();
        readonly byte[] colours = new byte[128];
        byte backlight = 0xE4;
        ulong powerLimit = (45ul * 8) | (1ul << 15) | ((80ul * 8) << 32) | (1ul << 47);    // PL1 45 W, PL2 80 W, in eighths
        readonly DateTime started = DateTime.UtcNow;
        public bool IsDemo => true;

        public DemoBackend() {
            colours[0] = 3;
            byte[] rgb = { 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
            Buffer.BlockCopy(rgb, 0, colours, BiosLighting.COLOR_OFFSET, rgb.Length);
        }

        public string Status() =>
            "helper=" + Program.Version + "\nprotocol=" + HelperProtocol.Version + "\ndemo=1\nmodule=simulated\nmodule_version=" + KernelModule.WantVersion
            + "\nlockdown=none\nmsr=simulated\nhp_wmi=simulated\npwm1_enable=2\nhotkeys=simulated\n";

        static byte[] Out(int outSize, params byte[] front) {
            var o = new byte[outSize];
            Buffer.BlockCopy(front, 0, o, 0, Math.Min(front.Length, outSize));
            return o;
        }
        static byte At(byte[] d, int i) => i < d.Length ? d[i] : (byte)0;

        public byte[] Mailbox(uint command, uint type, byte[] d, int outSize, out uint rc) {
            rc = 0;
            if (command == Bios.CMD_DEFAULT) {
                switch (type) {
                    case Bios.OP_FAN_COUNT: return Out(outSize, (byte)hw.GetFanCount());
                    case Bios.OP_SET_MODE: hw.SetMode(At(d, 1), At(d, 2) != 0); return Out(outSize);
                    case Bios.OP_GPU_POWER_GET: { var g = hw.GetGpuPower(); return Out(outSize, (byte)(g.CustomTgp ? 1 : 0), (byte)(g.Ppab ? 1 : 0), (byte)g.DState, (byte)g.PeakTemp); }
                    case Bios.OP_GPU_POWER_SET: hw.SetGpuPower(At(d, 0) != 0, At(d, 1) != 0, At(d, 3)); return Out(outSize);
                    case Bios.OP_TEMP: return Out(outSize, At(d, 0) == 1 ? (byte)hw.GetTemperature() : (byte)(38 + At(d, 0)));
                    case Bios.OP_MAX_FAN_GET: return Out(outSize, (byte)(hw.GetMaxFan() ? 1 : 0));
                    case Bios.OP_MAX_FAN_SET: hw.SetMaxFan(At(d, 0) != 0); return Out(outSize);
                    case Bios.OP_SYSTEM_DATA: return Out(outSize, hw.GetSystemInfo().Raw);
                    case Bios.OP_CPU_POWER_SET:
                        if (At(d, 3) != 0xFF) hw.SetConcurrentTdp(At(d, 3));
                        else if (At(d, 0) != 0xFF) hw.SetCpuPowerLimits(At(d, 1), At(d, 0));
                        return Out(outSize);
                    case Bios.OP_FAN_TYPE: return Out(outSize, 0x21);                      // a CPU fan and a GPU fan
                    case Bios.OP_FAN_LEVEL_GET: { int[] f = hw.GetFanLevels(); return Out(outSize, (byte)f[0], (byte)f[1]); }
                    case Bios.OP_FAN_LEVEL_SET: hw.SetFanLevels(At(d, 0), At(d, 1)); return Out(outSize);
                    case Bios.OP_FAN_TABLE_GET: {
                        byte top = (byte)hw.GetFanTableMax();
                        return Out(outSize, 2, 4, 0, 0, 20, 22, 20, 24, 34, 32, 30, top, top, 40);
                    }
                    case BiosLighting.OP_KBD_TYPE: return Out(outSize, 1);                 // four zones, with a numpad
                }
            } else if (type == Bios.OP_GPU_MODE && command == Bios.CMD_BIOS_READ) return Out(outSize, (byte)hw.GetGpuMode());
            else if (type == Bios.OP_GPU_MODE && command == Bios.CMD_BIOS_WRITE) { hw.SetGpuMode(At(d, 0) & 0x7F); return Out(outSize); }
            else if (command == BiosLighting.CMD) {
                switch (type) {
                    case BiosLighting.OP_PLATFORM_INFO: return Out(outSize, 0x03, 0x3D);
                    case BiosLighting.OP_COLOR_GET: return Out(outSize, colours);
                    case BiosLighting.OP_COLOR_SET: Buffer.BlockCopy(d, 0, colours, 0, Math.Min(d.Length, colours.Length)); return Out(outSize);
                    case BiosLighting.OP_LIGHT_GET: return Out(outSize, backlight);
                    case BiosLighting.OP_LIGHT_SET: backlight = At(d, 0); return Out(outSize);
                }
            }
            rc = 4;                                           // what HP firmware answers for a command type it does not have
            return new byte[0];
        }

        // DemoEcPorts speaks ports; a register read or write is the handshake EmbeddedController performs on it.
        public int EcRead(byte register, out byte value) {
            ec.Out(0x66, 0x80); ec.Out(0x62, register);
            return ec.In(0x62, out value);
        }
        public int EcWrite(byte register, byte value) {
            ec.Out(0x66, 0x81); ec.Out(0x62, register);
            return ec.Out(0x62, value);
        }

        /// <summary>An Intel register set that reads as DemoCpu polls: TjMax 100, the die temperature under it,
        /// counters that advance with the clock.</summary>
        public int MsrRead(uint msr, out ulong value) {
            double secs = (DateTime.UtcNow - started).TotalSeconds;
            CpuTelemetry t = cpu.Poll();
            switch (msr) {
                case 0x1A2: value = 100ul << 16; return 0;
                case 0x1B1: value = 0x80000000ul | ((ulong)Math.Max(0, 100 - (int)t.DieTemp) << 16); return 0;
                case 0x19C: value = 0; return 0;
                case 0x606: value = 3 | (14ul << 8); return 0;                      // power in 1/8 W, energy in 1/16384 J
                case 0x610: value = powerLimit; return 0;
                case 0x611: value = (ulong)(secs * 15 * 16384) & 0xFFFFFFFFul; return 0;    // 15 W
                case 0xCE: value = 23ul << 8; return 0;                             // 2300 MHz base
                case 0xE7: value = (ulong)(secs * 2.3e9); return 0;                 // MPERF
                case 0xE8: value = (ulong)(secs * 3.1e9); return 0;                 // APERF: running at 3100 MHz
                default: value = 0; return Native.EIO;                              // an AMD register on this pretend Intel
            }
        }
        public int MsrWrite(uint msr, ulong value) { powerLimit = value; return 0; }
        public bool Energy(out ulong microjoules, out ulong max) {
            microjoules = (ulong)((DateTime.UtcNow - started).TotalSeconds * 15e6);
            max = 262143328850;
            return true;
        }
        public void StartHotkeys(Action<uint, uint> onEvent) { }
        public string TakeOverStockFans() => "";
    }

    public static partial class Program {
        static IBackend backend;
        static readonly object HardwareLock = new object();       // one hardware access at a time, whoever asks
        static bool[] ecWritable;
        static string ecWrites = "allowed";                        // or why none are, for the status and the refusal
        static string board = "";
        static string socketPath;
        static bool demoMode;

        // What the server will put up with. Each connection is a thread; sixteen is far more than Ohman opens
        // (two per running program) and few enough that a flood of connections costs nothing.
        const int MaxConnections = 16;
        const int SendTimeoutMs = 2000;          // a client that will not take its reply does not hold the helper
        const int FrameTimeoutMs = 10000;        // from the first byte of a request to its last; between requests: for ever
        static int connections;

        sealed class Subscriber { public Socket Socket; public NetworkStream Stream; }
        static readonly List<Subscriber> subscribers = new List<Subscriber>();

        // The package power limit as it was before the first write through this helper: putting it back is
        // always allowed (Msr.LimitWriteRefusal).
        static bool haveOriginalLimit;
        static ulong originalLimit;
        static int testDelays;                   // how many OpTestDelay requests arrived (--demo), for the client's own test

        static void Say(string s) { Console.Error.WriteLine("ohman-helper: " + s); }

        public static int Main(string[] args) {
            socketPath = null;
            string boardArg = null;
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--demo": demoMode = true; break;
                    case "--socket" when i + 1 < args.Length: socketPath = args[++i]; break;
                    case "--board" when i + 1 < args.Length: boardArg = args[++i]; break;
                    case "--version": Console.WriteLine("ohman-helper " + Version + " (protocol " + HelperProtocol.Version + ")"); return 0;
                    case "--help": case "-h":
                        Console.WriteLine("ohman-helper [--socket PATH] [--demo [--board ID]]\n  Serves hardware access to Ohman over a Unix socket (default " + HelperProtocol.DefaultSocket + ").\n  --demo answers from simulated hardware and needs no root; --board makes it behave as on that board.");
                        return 0;
                    default: Say("unknown argument: " + args[i]); return 2;
                }
            }
            socketPath ??= HelperProtocol.SocketPath;
            Log.Path = "/dev/null";                 // the core's log is the GUI's; this process speaks on stderr, to the journal

            if (!demoMode && !Native.IsRoot) Say("not running as root: the kernel module and the CPU's registers will refuse (use --demo for simulated hardware)");
            if (!demoMode && boardArg != null) { Say("--board is for --demo only: a real helper reads the board from the machine"); return 2; }
            backend = demoMode ? new DemoBackend() : new RealBackend();

            // The registers any map allows writing, as one table; nothing outside it is ever written. And on a
            // board where the maps do not apply at all, nothing is: the 2025 OMEN MAX keeps other things at these
            // addresses, and writing them corrupts its controller (Families.EcCandidate, src/Platform.cs). The
            // engine never asks there, but this process does not take the engine's word for anything.
            ecWritable = new bool[256];
            foreach (EcMap map in EcMap.Known())
                for (int r = 0; r < 256; r++) if (map.MayWrite((byte)r)) ecWritable[r] = true;
            board = boardArg ?? (demoMode ? "" : Dmi.Board());
            bool judged = !demoMode || boardArg != null;        // a plain --demo has no board and keeps every map's registers
            if (judged && !Families.EcCandidate(board)) {
                ecWritable = new bool[256];
                ecWrites = board.Length == 0 ? "none: the board id could not be read, so no EC map is trusted"
                                             : "none: no EC map applies to board " + board + " (its controller is laid out differently)";
            }

            Socket listener;
            try { listener = Listen(socketPath); }
            catch (Exception ex) { Say("cannot listen on " + socketPath + ": " + ex.Message); return 1; }

            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => { Cleanup(); Environment.Exit(0); });
            Console.CancelKeyPress += (_, _) => { Cleanup(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

            backend.StartHotkeys(Broadcast);
            Say("listening on " + socketPath + (demoMode ? " (SIMULATED HARDWARE)" : "") + " · " + Status().Replace("\n", " ").Trim());

            while (true) {
                Socket s;
                try { s = listener.Accept(); } catch (Exception ex) { Say("accept: " + ex.Message); Thread.Sleep(200); continue; }
                // Nothing in here may end the loop: a connection that cannot be served is closed and forgotten.
                try {
                    s.SendTimeout = SendTimeoutMs;
                    if (Volatile.Read(ref connections) >= MaxConnections) { Refuse(s, "too many connections (" + MaxConnections + " are open)"); continue; }
                    Interlocked.Increment(ref connections);
                    try { new Thread(() => Serve(s)) { IsBackground = true, Name = "client" }.Start(); }
                    catch (Exception ex) { Interlocked.Decrement(ref connections); Say("could not start a thread for a client: " + ex.Message); try { s.Dispose(); } catch { } }
                } catch (Exception ex) { Say("accept: " + ex.Message); try { s.Dispose(); } catch { } }
            }
        }

        static void Refuse(Socket s, string why) {
            try {
                using var st = new NetworkStream(s, false);
                HelperProtocol.WriteFrame(st, HelperProtocol.Unavailable, Encoding.UTF8.GetBytes(why));
                // Closing with the client's request still unread would reach it as a reset instead of the
                // sentence above. Say we are done, take what it sent for a moment, then close.
                s.Shutdown(SocketShutdown.Send);
                s.ReceiveTimeout = 100;
                var junk = new byte[256];
                while (s.Receive(junk) > 0) { }
            } catch { }
            try { s.Dispose(); } catch { }
        }

        static void Cleanup() { try { if (socketPath != null && File.Exists(socketPath)) File.Delete(socketPath); } catch { } }

        /// <summary>The backend's account of the machine, and this process's own two lines.</summary>
        static string Status() {
            return backend.Status() + "board=" + board + "\nec_writes=" + ecWrites + "\nconnections=" + Volatile.Read(ref connections) + "\n"
                + (demoMode ? "test_delays=" + Volatile.Read(ref testDelays) + "\n" : "");
        }

        static Socket Listen(string path) {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
                try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute); } catch { }
            }
            if (!demoMode) {
                string unsafeWhy = HelperProtocol.UnsafeSocketDirectory(dir);
                if (unsafeWhy != null) throw new InvalidOperationException(dir + " is no place for root's socket: " + unsafeWhy);
            }
            if (File.Exists(path)) {
                // Left over from a helper that died, or one that is alive. Only the first may be cleared away.
                try {
                    using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    probe.Connect(new UnixDomainSocketEndPoint(path));
                    throw new InvalidOperationException("another ohman-helper is already serving it");
                } catch (SocketException) { File.Delete(path); }
            }
            var l = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // Born 0660: the mode comes from the umask at bind, so there is no moment at which the node exists
            // with wider permissions than it ends up with. (The systemd unit sets the same umask.)
            uint old = Native.umask(0x4F /* 0117 */);
            try { l.Bind(new UnixDomainSocketEndPoint(path)); }
            finally { Native.umask(old); }
            // The group is given with lchown: it acts on the name itself, so a symbolic link slipped in under
            // that name gets its own group changed and nothing it points to does.
            int gid = OhmanGroup();
            if (Native.IsRoot) {
                if (gid >= 0) { if (Native.lchown(path, 0, gid) != 0) Say("could not give " + path + " to group " + HelperProtocol.Group + ": " + Native.Why(Native.Errno)); }
                else Say("group \"" + HelperProtocol.Group + "\" does not exist (not in /etc/group, not known to getent): only root can connect until the installer has created it");
            }
            l.Listen(MaxConnections);
            return l;
        }

        // ---------- who is asking ----------
        static int groupId = -1;
        static DateTime groupAsked = DateTime.MinValue;
        /// <summary>The group's id, looked up once it is found and at most every half minute while it is not
        /// (the installer may create it after the helper has started).</summary>
        static int OhmanGroup() {
            if (groupId >= 0 || (DateTime.UtcNow - groupAsked).TotalSeconds < 30) return groupId;
            groupAsked = DateTime.UtcNow;
            groupId = HelperProtocol.GroupId(HelperProtocol.Group);
            return groupId;
        }

        /// <summary>The peer's pid, uid and gid as the kernel recorded them at connect (SO_PEERCRED).</summary>
        static bool PeerCredentials(Socket s, out int pid, out int uid, out int gid) {
            pid = uid = gid = -1;
            try {
                Span<byte> cred = stackalloc byte[12];             // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                if (s.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, cred) != 12) return false;
                pid = BitConverter.ToInt32(cred.Slice(0, 4));
                uid = BitConverter.ToInt32(cred.Slice(4, 4));
                gid = BitConverter.ToInt32(cred.Slice(8, 4));
                return true;
            } catch { return false; }
        }

        static bool Allowed(Socket s, out string who) {
            who = "unknown peer";
            if (!PeerCredentials(s, out int pid, out int uid, out int gid)) return false;
            who = "pid " + pid + " uid " + uid;
            if (uid == 0 || (uint)uid == Native.geteuid()) return true;
            int ohman = OhmanGroup();
            if (ohman < 0) { who += " (group \"" + HelperProtocol.Group + "\" does not exist)"; return false; }
            if (gid == ohman) return true;
            // The groups the process actually holds, which is what the socket's own permission check went by.
            try {
                foreach (string line in File.ReadLines("/proc/" + pid + "/status")) {
                    if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
                    foreach (string g in line.Substring(7).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(g, out int n) && n == ohman) return true;
                }
            } catch { }
            return false;
        }

        // ---------- key events, and the moment Ohman takes the fans ----------
        static void Drop(Subscriber sub) {
            lock (subscribers) subscribers.Remove(sub);
            try { sub.Socket.Dispose(); } catch { }               // which also ends its Serve thread's read
        }

        static void Broadcast(uint id, uint data) {
            var p = new byte[8];
            Native.Put32(p, 0, id);
            Native.Put32(p, 4, data);
            Subscriber[] all;
            lock (subscribers) all = subscribers.ToArray();
            foreach (Subscriber sub in all) {
                try {
                    // A subscriber that has stopped reading has a full buffer. It is dropped, not waited for: one
                    // stuck program must not delay the key for the others or hold the thread that reads the keys.
                    if (!sub.Socket.Poll(0, SelectMode.SelectWrite)) { Say("dropped a key-event subscriber that is not reading"); Drop(sub); continue; }
                    lock (sub.Stream) HelperProtocol.WriteFrame(sub.Stream, HelperProtocol.EventFrame, p);
                } catch { Drop(sub); }
            }
        }

        /// <summary>A program subscribes to key events when it starts driving the laptop (Engine.Init with apply;
        /// a report or a probe never does). The first one to arrive is the moment Ohman takes the fans, before
        /// it has sent anything, and the only moment the stock driver's leftover fan mode is corrected: see
        /// HpWmi.TakeOverFrom for why it is once, and why never on a kernel without a cached mode.</summary>
        static void Subscribed(Subscriber sub) {
            bool first;
            lock (subscribers) { first = subscribers.Count == 0; subscribers.Add(sub); }
            if (!first) return;
            string note;
            lock (HardwareLock) note = backend.TakeOverStockFans();
            if (!string.IsNullOrEmpty(note)) Say(note);
        }

        // ---------- one connection ----------
        /// <summary>One request frame. Waiting for a request to begin may take for ever; once its first byte is
        /// here the rest has ten seconds, so a client that sends half a frame and stops is let go.</summary>
        static byte[] ReadRequest(Socket s, NetworkStream st, out byte op) {
            var head = new byte[4];
            s.ReceiveTimeout = 0;
            if (st.Read(head, 0, 1) <= 0) throw new EndOfStreamException();
            s.ReceiveTimeout = FrameTimeoutMs;
            int got = 1;
            while (got < 4) { int r = st.Read(head, got, 4 - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
            uint len = Native.Get32(head, 0);
            if (len < 1 || len > HelperProtocol.MaxFrame) throw new InvalidDataException("a frame of " + len + " bytes is not one of ours");
            var body = new byte[len];
            HelperProtocol.ReadExact(st, body, (int)len);
            s.ReceiveTimeout = 0;
            op = body[0];
            var payload = new byte[len - 1];
            Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
            return payload;
        }

        static void Serve(Socket s) {
            NetworkStream st = null;
            Subscriber sub = null;
            try {
                if (!Allowed(s, out string who)) { Say("refused " + who + ": not root and not in group " + HelperProtocol.Group); return; }
                st = new NetworkStream(s, false);
                while (true) {
                    byte[] payload = ReadRequest(s, st, out byte op);
                    if (op == HelperProtocol.OpSubscribe) {
                        lock (st) HelperProtocol.WriteFrame(st, HelperProtocol.Ok, null);
                        sub = new Subscriber { Socket = s, Stream = st };
                        Subscribed(sub);
                        // From here the connection only carries events out. Reading is how its closing is noticed.
                        var one = new byte[1];
                        while (st.Read(one, 0, 1) > 0) { }
                        return;
                    }
                    byte status;
                    byte[] reply;
                    try {
                        if (op == HelperProtocol.OpTestDelay) reply = TestDelay(payload, out status);     // not under the lock: it must not hold up the others
                        else lock (HardwareLock) reply = Handle(op, payload, out status);
                    } catch (Exception ex) { status = HelperProtocol.Failed; reply = Encoding.UTF8.GetBytes(ex.Message); }
                    lock (st) HelperProtocol.WriteFrame(st, status, reply);
                }
            } catch (EndOfStreamException) { }
            catch (InvalidDataException ex) { Say("client: " + ex.Message); }
            catch (IOException ex) when (ex.InnerException is SocketException se && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock)) {
                Say("dropped a client that stopped in the middle of a request or would not take its reply");
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Say("client: " + ex.Message); }
            finally {
                if (sub != null) lock (subscribers) subscribers.Remove(sub);
                Interlocked.Decrement(ref connections);
                try { s.Dispose(); } catch { }
            }
        }

        static byte[] No(byte status, string why, out byte s) { s = status; return Encoding.UTF8.GetBytes(why); }

        // An errno means different things from different doors, and each gets its own sentence.
        static byte[] EcError(int e, string what, out byte s) {
            if (e == Native.ENOENT || e == Native.ENODEV || e == Native.ENXIO) return No(HelperProtocol.Unavailable, what + ": the ohman-wmi kernel module is not loaded, or this machine has no ACPI embedded controller", out s);
            if (e == Native.ENOTTY) return No(HelperProtocol.Unavailable, what + ": the loaded ohman-wmi module is too old to reach the EC; update it", out s);
            if (e == Native.EPERM || e == Native.EACCES) return No(HelperProtocol.Refused, what + ": the kernel module refused; ohman-helper is not root or has lost CAP_SYS_RAWIO", out s);
            return No(HelperProtocol.Failed, what + ": the embedded controller did not answer (" + Native.Why(e) + ")", out s);
        }
        static byte[] MsrError(int e, string what, bool write, out byte s) {
            if (e == Native.ENOENT || e == Native.ENODEV || e == Native.ENXIO) return No(HelperProtocol.Unavailable, what + ": the msr kernel module is not loaded (modprobe msr)", out s);
            if (e == Native.EPERM || e == Native.EACCES)
                return No(HelperProtocol.Refused, what + ": " + (write ? Driver.MsrRefusal(e) : "the kernel refused the read; ohman-helper is not root or has lost CAP_SYS_RAWIO"), out s);
            if (e == Native.EIO) return No(HelperProtocol.Failed, what + ": this CPU does not have that register", out s);
            return No(HelperProtocol.Failed, what + ": " + Native.Why(e), out s);
        }

        /// <summary>--demo only: answer after the asked delay. It is how a client's own timeout handling is
        /// tested: the count in the status shows whether a request that timed out was sent a second time.</summary>
        static byte[] TestDelay(byte[] p, out byte status) {
            if (!backend.IsDemo) return No(HelperProtocol.NotAllowed, "the delay hook exists only in a --demo helper", out status);
            if (p.Length != 4) return No(HelperProtocol.BadRequest, "a delay is four bytes: milliseconds", out status);
            Interlocked.Increment(ref testDelays);
            Thread.Sleep((int)Math.Min(30000, Native.Get32(p, 0)));
            status = HelperProtocol.Ok;
            return new byte[0];
        }

        static byte[] Handle(byte op, byte[] p, out byte status) {
            status = HelperProtocol.Ok;
            switch (op) {
                case HelperProtocol.OpStatus:
                    return Encoding.UTF8.GetBytes(Status());

                case HelperProtocol.OpMailbox: {
                    if (p.Length < 16) return No(HelperProtocol.BadRequest, "a mailbox request is at least 16 bytes", out status);
                    uint command = Native.Get32(p, 0), type = Native.Get32(p, 4), outSize = Native.Get32(p, 8), inLen = Native.Get32(p, 12);
                    if (inLen != p.Length - 16 || inLen > 4096) return No(HelperProtocol.BadRequest, "the input length does not match the request", out status);
                    if (outSize != 0 && outSize != 4 && outSize != 128 && outSize != 1024 && outSize != 4096) return No(HelperProtocol.BadRequest, "the reply size must be 0, 4, 128, 1024 or 4096", out status);
                    var data = new byte[inLen];
                    Buffer.BlockCopy(p, 16, data, 0, (int)inLen);
                    byte[] o;
                    uint rc;
                    try { o = backend.Mailbox(command, type, data, (int)outSize, out rc); }
                    catch (UnauthorizedAccessException ex) { return No(HelperProtocol.Unavailable, "mailbox: " + ex.Message, out status); }
                    catch (InvalidOperationException ex) { return No(HelperProtocol.Unavailable, "mailbox: " + ex.Message, out status); }
                    catch (IOException ex) { return No(HelperProtocol.Failed, "mailbox: " + ex.Message, out status); }
                    var r = new byte[4 + o.Length];
                    Native.Put32(r, 0, rc);
                    Buffer.BlockCopy(o, 0, r, 4, o.Length);
                    return r;
                }

                case HelperProtocol.OpEcRead: {
                    if (p.Length != 1) return No(HelperProtocol.BadRequest, "an EC read is one byte: the register", out status);
                    int e = backend.EcRead(p[0], out byte v);
                    return e == 0 ? new[] { v } : EcError(e, "ec read 0x" + p[0].ToString("X2"), out status);
                }
                case HelperProtocol.OpEcWrite: {
                    if (p.Length != 2) return No(HelperProtocol.BadRequest, "an EC write is two bytes: the register and the value", out status);
                    if (!ecWritable[p[0]])
                        return No(HelperProtocol.NotAllowed, ecWrites == "allowed" ? "0x" + p[0].ToString("X2") + " is not a register any EC map allows writing"
                                                                                  : "EC writes are switched off on this machine (" + ecWrites + ")", out status);
                    int e = backend.EcWrite(p[0], p[1]);
                    return e == 0 ? new byte[0] : EcError(e, "ec write 0x" + p[0].ToString("X2"), out status);
                }

                case HelperProtocol.OpMsrRead: {
                    if (p.Length != 4) return No(HelperProtocol.BadRequest, "an MSR read is four bytes: the register", out status);
                    uint msr = Native.Get32(p, 0);
                    if (!Msr.MayRead(msr)) return No(HelperProtocol.NotAllowed, "MSR 0x" + msr.ToString("X") + " is not one Ohman reads", out status);
                    int e = backend.MsrRead(msr, out ulong v);
                    if (e != 0) return MsrError(e, "msr read 0x" + msr.ToString("X"), false, out status);
                    var r = new byte[8];
                    Native.Put64(r, 0, v);
                    return r;
                }
                case HelperProtocol.OpMsrWrite: {
                    if (p.Length != 12) return No(HelperProtocol.BadRequest, "an MSR write is twelve bytes: the register and the value", out status);
                    uint msr = Native.Get32(p, 0);
                    if (!Msr.MayWrite(msr)) return No(HelperProtocol.NotAllowed, "MSR 0x" + msr.ToString("X") + " is not written: only the package power limit (0x610) is", out status);
                    ulong wanted = Native.Get64(p, 4);
                    // The register is the right one; now the value has to be one Ohman would write. Read what is
                    // there and the unit it is in, and let Msr decide: only the limit and enable bits may move,
                    // never the lock, and the limits stay in the range the client keeps them in.
                    int e = backend.MsrRead(msr, out ulong current);
                    if (e != 0) return MsrError(e, "msr write 0x610: reading it first", false, out status);
                    e = backend.MsrRead(0x606, out ulong unit);
                    if (e != 0) return MsrError(e, "msr write 0x610: reading the power unit (0x606)", false, out status);
                    string why = Msr.LimitWriteRefusal(current, wanted, unit, haveOriginalLimit, originalLimit);
                    if (why != null) return No(HelperProtocol.NotAllowed, "msr write 0x610: " + why, out status);
                    if (!haveOriginalLimit) { originalLimit = current; haveOriginalLimit = true; }
                    e = backend.MsrWrite(msr, wanted);
                    return e == 0 ? new byte[0] : MsrError(e, "msr write 0x" + msr.ToString("X"), true, out status);
                }

                case HelperProtocol.OpEnergy: {
                    if (!backend.Energy(out ulong uj, out ulong max)) return No(HelperProtocol.Unavailable, "no package energy counter under /sys/class/powercap", out status);
                    var r = new byte[16];
                    Native.Put64(r, 0, uj);
                    Native.Put64(r, 8, max);
                    return r;
                }

                case HelperProtocol.OpInjectKey: {
                    if (!backend.IsDemo) return No(HelperProtocol.NotAllowed, "key events can only be injected into a --demo helper", out status);
                    if (p.Length != 8) return No(HelperProtocol.BadRequest, "an injected key is eight bytes: id and data", out status);
                    Broadcast(Native.Get32(p, 0), Native.Get32(p, 4));
                    return new byte[0];
                }

                default:
                    return No(HelperProtocol.BadRequest, "operation " + op + " is not one this helper has", out status);
            }
        }
    }
}
