// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the wire between the GUI (an ordinary user) and ohman-helper (root), and the client end
// of it. The server end is linux\helper\Helper.cs and uses the constants here, so the two cannot drift.
//
// A Unix stream socket, /run/ohman/helper.sock (root:ohman 0660). One request, one reply, in order, per
// connection:
//   request   u32 length (of what follows), u8 operation, payload
//   reply     u32 length (of what follows), u8 status,    payload      status 0 = done; else payload is a
//                                                                      UTF-8 sentence saying why not
// All integers little-endian. A connection that sends Subscribe gets its reply and from then on only event
// frames (status EventFrame, payload u32 id, u32 data), so key events use a connection of their own.
//
// The helper forwards hardware access and holds no policy beyond its allow-lists: which EC registers may be
// written (EcMap, src\Ec.cs) and which MSRs may be read or written (Msr, src\linux\Msr.cs). It has no engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ohman {
    static class HelperProtocol {
        public const string DefaultSocket = "/run/ohman/helper.sock";
        /// <summary>Overrides the socket path, for tests and for a helper started by hand.</summary>
        public const string SocketVariable = "OHMAN_HELPER_SOCKET";
        /// <summary>Bumped when an operation changes shape. Status reports it; the client says so when they differ.</summary>
        public const int Version = 1;
        public const int MaxFrame = 8192;                 // a 4096-byte mailbox buffer and its header fit; nothing else is near it

        // operations
        public const byte OpStatus = 1;                   // -> text, "key=value" lines
        public const byte OpMailbox = 2;                  // u32 command, u32 type, u32 outSize, u32 inLen, in[]  -> u32 rc, out[]
        public const byte OpEcRead = 3;                   // u8 register                                          -> u8 value
        public const byte OpEcWrite = 4;                  // u8 register, u8 value   (EcMap's writable list only)
        public const byte OpMsrRead = 5;                  // u32 msr                 (Msr.MayRead only)           -> u64 value
        public const byte OpMsrWrite = 6;                 // u32 msr, u64 value      (the package power limit only)
        public const byte OpSubscribe = 7;                // -> ok, then event frames for as long as the connection lives
        public const byte OpEnergy = 8;                   // -> u64 package energy in microjoules, u64 wrap value
        public const byte OpInjectKey = 9;                // u32 id, u32 data: --demo only, a test hook that raises a key event
        public const byte OpTestDelay = 10;               // u32 milliseconds: --demo only, a test hook that answers that much later

        // statuses
        public const byte Ok = 0;
        public const byte BadRequest = 1;                 // malformed, or an operation this helper does not have
        public const byte NotAllowed = 2;                 // outside an allow-list
        public const byte Unavailable = 3;                // the thing it needs is not there (module not loaded, no msr driver)
        public const byte Failed = 4;                     // the kernel or the firmware call itself failed
        public const byte Refused = 5;                    // the kernel said no on purpose: lockdown, or writes switched off
        public const byte EventFrame = 0x80;

        /// <summary>The group whose members may use the helper.</summary>
        public const string Group = "ohman";
        /// <summary>How long a client waits for a connection, and for a reply. A hardware access takes
        /// milliseconds; a helper that has not answered in three seconds is not going to.</summary>
        public const int ConnectTimeoutMs = 1500, RequestTimeoutMs = 3000;

        /// <summary>The id of a group, or -1 when there is no such group. /etc/group first; then getent, for a
        /// system whose groups come from somewhere else (LDAP, systemd-homed).</summary>
        public static int GroupId(string name) {
            try {
                foreach (string line in File.ReadLines("/etc/group")) {
                    string[] p = line.Split(':');
                    int gid;
                    if (p.Length >= 3 && p[0] == name && int.TryParse(p[2], out gid)) return gid;
                }
            } catch { }
            try {
                string getent = Host.Find("getent");
                string o;
                if (getent != null && Host.Run(getent, "group " + name, 3000, out o) == 0) {
                    string[] p = o.Trim().Split(':');
                    int gid;
                    if (p.Length >= 3 && p[0] == name && int.TryParse(p[2], out gid)) return gid;
                }
            } catch { }
            return -1;
        }

        /// <summary>Why a directory is no place for root's socket, or null. Anyone who can rename things in it
        /// can put their own socket where Ohman expects root's, so it has to be root's own and writable by nobody
        /// else (a sticky directory such as /run or /tmp also keeps others' hands off the name).</summary>
        public static string UnsafeSocketDirectory(string dir) {
            uint uid, gid, mode;
            if (!Native.Stat(dir, out uid, out gid, out mode)) return "its owner and mode cannot be read";
            if ((mode & 0xF000) != 0x4000) return "it is not a directory";
            if (uid != 0) return "it is not owned by root (uid " + uid + ")";
            if ((mode & 0x12 /* 0022 */) != 0 && (mode & 0x200 /* sticky */) == 0) return "it is writable by its group or by everyone and not sticky (mode " + Convert.ToString(mode & 0xFFF, 8) + ")";
            return null;
        }

        public static string SocketPath {
            get {
                string p = Environment.GetEnvironmentVariable(SocketVariable);
                return string.IsNullOrEmpty(p) ? DefaultSocket : p;
            }
        }

        /// <summary>Read exactly n bytes, or throw: a short read on a stream socket is the peer going away.</summary>
        public static void ReadExact(Stream s, byte[] b, int n) {
            int got = 0;
            while (got < n) {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException("the other end closed the connection");
                got += r;
            }
        }
        public static void WriteFrame(Stream s, byte code, byte[] payload) {
            int n = payload == null ? 0 : payload.Length;
            var f = new byte[5 + n];
            Native.Put32(f, 0, (uint)(1 + n));
            f[4] = code;
            if (n > 0) Buffer.BlockCopy(payload, 0, f, 5, n);
            s.Write(f, 0, f.Length);
            s.Flush();
        }
        public static byte[] ReadFrame(Stream s, out byte code) {
            var head = new byte[4];
            ReadExact(s, head, 4);
            uint len = Native.Get32(head, 0);
            if (len < 1 || len > MaxFrame) throw new InvalidDataException("a frame of " + len + " bytes is not one of ours");
            var body = new byte[len];
            ReadExact(s, body, (int)len);
            code = body[0];
            var payload = new byte[len - 1];
            Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
            return payload;
        }
    }

    /// <summary>The helper said no, or could not. Status is one of HelperProtocol's.</summary>
    sealed class HelperException : Exception {
        public readonly byte Status;
        public HelperException(byte status, string message) : base(message) { Status = status; }
    }

    /// <summary>The client end. One shared connection for requests, opened on first use and reopened once when
    /// it is found dead (the helper restarted); a second connection per key-event subscription.</summary>
    static class HelperClient {
        static readonly object Sync = new object();
        static Socket socket;
        static NetworkStream stream;
        static DateTime lastFailure = DateTime.MinValue;
        static string lastError = "";
        const double FailFastSeconds = 2;

        /// <summary>Connect, bounded. A Unix socket connects at once or not at all, except when the helper's
        /// accept queue is full, and that must not hang whoever is asking.</summary>
        static Socket Connect() {
            string path = HelperProtocol.SocketPath;
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                var pending = s.ConnectAsync(new UnixDomainSocketEndPoint(path));
                bool done;
                try { done = pending.Wait(HelperProtocol.ConnectTimeoutMs); }
                catch (AggregateException ex) { throw ex.InnerException ?? ex; }
                if (!done) throw new TimeoutException("no connection within " + HelperProtocol.ConnectTimeoutMs + " ms");
                s.SendTimeout = HelperProtocol.RequestTimeoutMs;
                s.ReceiveTimeout = HelperProtocol.RequestTimeoutMs;
                return s;
            } catch { try { s.Dispose(); } catch { } throw; }
        }

        static void Drop() {
            try { if (stream != null) stream.Dispose(); } catch { }
            try { if (socket != null) socket.Dispose(); } catch { }
            stream = null; socket = null;
        }
        /// <summary>Close the shared connection. The next request opens a new one.</summary>
        public static void Close() { lock (Sync) Drop(); }

        static SocketException SocketError_(Exception ex) {
            for (Exception e = ex; e != null; e = e.InnerException) { var se = e as SocketException; if (se != null) return se; }
            return null;
        }
        /// <summary>The reply did not come in time. Never retried: the helper may still be doing what was asked.</summary>
        static bool TimedOut(Exception ex) {
            if (ex is TimeoutException) return true;
            SocketException se = SocketError_(ex);
            return se != null && (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock);
        }
        /// <summary>The connection was already dead: the helper closed it or was restarted. The request never
        /// reached a helper that could act on it, so sending it once more on a new connection is safe.</summary>
        static bool ConnectionGone(Exception ex) {
            if (ex is EndOfStreamException) return true;
            SocketException se = SocketError_(ex);
            return se != null && (se.SocketErrorCode == SocketError.ConnectionReset || se.SocketErrorCode == SocketError.ConnectionAborted
                || se.SocketErrorCode == SocketError.Shutdown || se.SocketErrorCode == SocketError.NotConnected);
        }

        /// <summary>Why the helper cannot be reached, in words a person can act on.</summary>
        static string Explain(Exception ex) {
            string path = HelperProtocol.SocketPath;
            SocketException se = SocketError_(ex);
            if (!File.Exists(path)) return "ohman-helper is not running (" + path + " is not there)";
            if (se != null && se.SocketErrorCode == SocketError.AccessDenied) {
                if (HelperProtocol.GroupId(HelperProtocol.Group) < 0)
                    return "not allowed to talk to ohman-helper: group \"" + HelperProtocol.Group + "\" does not exist on this system, so only root can (run the installer again: " + Host.InstallHint + ")";
                return "not allowed to talk to ohman-helper: this user is not in group \"" + HelperProtocol.Group + "\" (log out and in again after installing)";
            }
            if (se != null && se.SocketErrorCode == SocketError.ConnectionRefused) return "ohman-helper is not running (" + path + " is left over from one that was)";
            if (TimedOut(ex)) return "ohman-helper at " + path + " is not answering";
            return "cannot reach ohman-helper at " + path + ": " + ex.Message;
        }

        static bool FailingFast { get { return (DateTime.UtcNow - lastFailure).TotalSeconds < FailFastSeconds; } }
        static void Failed(string why) { lastError = why; lastFailure = DateTime.UtcNow; Drop(); }

        /// <summary>Whether the helper answers. A no is remembered for two seconds, so a machine without the
        /// helper does not pay for a connection attempt on every call the engine makes.</summary>
        public static bool Reachable {
            get {
                lock (Sync) {
                    if (stream != null) return true;
                    if (FailingFast) return false;
                    try { socket = Connect(); stream = new NetworkStream(socket, false); return true; }
                    catch (Exception ex) { Failed(Explain(ex)); return false; }
                }
            }
        }
        public static string LastError { get { return lastError; } }

        /// <summary>One request and its reply. Throws HelperException when the helper answers with a status,
        /// IOException when it cannot be reached, does not answer in time, or the connection breaks. Never
        /// returns a value it did not get from the helper.
        ///
        /// Every caller in the process waits on one lock, so the waits are short and a failure is not paid for
        /// twice: after a timeout or a failed connect, requests fail at once for two seconds instead of each
        /// waiting out the same timeout in turn. A request is sent again only when the connection it went out
        /// on was already dead (the helper was restarted); never after a timeout, because a helper that is
        /// merely slow may still carry the first one out.</summary>
        public static byte[] Request(byte op, byte[] payload) {
            lock (Sync) {
                for (int attempt = 0; ; attempt++) {
                    if (stream == null) {
                        if (FailingFast) throw new IOException(lastError);
                        try { socket = Connect(); stream = new NetworkStream(socket, false); }
                        catch (Exception ex) { Failed(Explain(ex)); throw new IOException(lastError); }
                    }
                    byte status;
                    byte[] reply;
                    try {
                        HelperProtocol.WriteFrame(stream, op, payload);
                        reply = HelperProtocol.ReadFrame(stream, out status);
                    } catch (Exception ex) {
                        if (attempt == 0 && ConnectionGone(ex) && !TimedOut(ex)) { Drop(); continue; }
                        Failed(TimedOut(ex) ? "ohman-helper did not answer within " + (HelperProtocol.RequestTimeoutMs / 1000) + " s" : "the connection to ohman-helper broke: " + ex.Message);
                        throw new IOException(lastError);
                    }
                    if (status != HelperProtocol.Ok) throw new HelperException(status, Encoding.UTF8.GetString(reply));
                    return reply;
                }
            }
        }

        // ---------- the operations ----------
        /// <summary>The helper's own account of the machine, as key=value pairs: version, demo, module,
        /// module_version, lockdown, msr, hp_wmi, pwm1_enable, hotkeys.</summary>
        public static Dictionary<string, string> Status() {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Encoding.UTF8.GetString(Request(HelperProtocol.OpStatus, null)).Split('\n')) {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static byte[] Mailbox(uint command, uint commandType, byte[] data, int outSize, out uint rc) {
            if (data == null) data = new byte[0];
            var p = new byte[16 + data.Length];
            Native.Put32(p, 0, command);
            Native.Put32(p, 4, commandType);
            Native.Put32(p, 8, (uint)outSize);
            Native.Put32(p, 12, (uint)data.Length);
            Buffer.BlockCopy(data, 0, p, 16, data.Length);
            byte[] r = Request(HelperProtocol.OpMailbox, p);
            if (r.Length < 4) throw new IOException("ohman-helper answered a mailbox call with " + r.Length + " bytes");
            rc = Native.Get32(r, 0);
            if (rc != 0 || outSize == 0) return new byte[0];
            var o = new byte[outSize];
            Buffer.BlockCopy(r, 4, o, 0, Math.Min(outSize, r.Length - 4));
            return o;
        }

        /// <summary>0, or an errno-like number: EPERM for anything the helper would not do, EIO for the rest.</summary>
        public static int EcRead(byte register, out byte value) {
            value = 0;
            try {
                byte[] r = Request(HelperProtocol.OpEcRead, new byte[] { register });
                if (r.Length < 1) return Native.EIO;
                value = r[0];
                return 0;
            } catch (HelperException ex) { return Errno(ex); }
            catch { return Native.EIO; }
        }
        public static int EcWrite(byte register, byte value) {
            try { Request(HelperProtocol.OpEcWrite, new byte[] { register, value }); return 0; }
            catch (HelperException ex) { return Errno(ex); }
            catch { return Native.EIO; }
        }

        public static int MsrRead(uint msr, out ulong value) {
            value = 0;
            try {
                var p = new byte[4];
                Native.Put32(p, 0, msr);
                byte[] r = Request(HelperProtocol.OpMsrRead, p);
                if (r.Length < 8) return Native.EIO;
                value = Native.Get64(r, 0);
                return 0;
            } catch (HelperException ex) { return Errno(ex); }
            catch { return Native.EIO; }
        }
        /// <summary>why is the helper's own sentence when it did not write (a lockdown refusal says so).</summary>
        public static int MsrWrite(uint msr, ulong value, out string why) {
            why = null;
            try {
                var p = new byte[12];
                Native.Put32(p, 0, msr);
                Native.Put64(p, 4, value);
                Request(HelperProtocol.OpMsrWrite, p);
                return 0;
            } catch (HelperException ex) { why = ex.Message; return Errno(ex); }
            catch (Exception ex) { why = ex.Message; return Native.EIO; }
        }

        public static bool Energy(out ulong microjoules, out ulong max) {
            microjoules = 0; max = 0;
            try {
                byte[] r = Request(HelperProtocol.OpEnergy, null);
                if (r.Length < 16) return false;
                microjoules = Native.Get64(r, 0);
                max = Native.Get64(r, 8);
                return true;
            } catch { return false; }
        }

        /// <summary>The test hook: a helper in --demo raises this key event to its subscribers. A real helper refuses.</summary>
        public static void InjectKey(uint id, uint data) {
            var p = new byte[8];
            Native.Put32(p, 0, id);
            Native.Put32(p, 4, data);
            Request(HelperProtocol.OpInjectKey, p);
        }

        static int Errno(HelperException ex) {
            switch (ex.Status) {
                case HelperProtocol.NotAllowed: case HelperProtocol.Refused: return Native.EPERM;
                case HelperProtocol.Unavailable: return Native.ENODEV;
                case HelperProtocol.BadRequest: return Native.EINVAL;
                default: return Native.EIO;
            }
        }

        /// <summary>Key events, on a connection and a thread of their own. Throws when the helper cannot be
        /// reached now; after that a broken connection is retried quietly every few seconds, so a helper that
        /// is restarted does not cost the OMEN key until Ohman is.</summary>
        public static IDisposable Subscribe(Action<uint, uint> onEvent) { return new Subscription(onEvent); }

        sealed class Subscription : IDisposable {
            readonly Action<uint, uint> handler;
            readonly Thread thread;
            volatile bool stop;
            volatile Socket current;

            public Subscription(Action<uint, uint> onEvent) {
                handler = onEvent;
                Socket first = Open();                      // throws: the caller logs it and carries on without the key
                thread = new Thread(delegate() { Loop(first); }) { IsBackground = true, Name = "helper-events" };
                thread.Start();
            }

            static Socket Open() {
                Socket s;
                try { s = Connect(); } catch (Exception ex) { throw new IOException(Explain(ex)); }
                try {
                    var ns = new NetworkStream(s, false);
                    HelperProtocol.WriteFrame(ns, HelperProtocol.OpSubscribe, null);
                    byte status;
                    byte[] r = HelperProtocol.ReadFrame(ns, out status);
                    if (status != HelperProtocol.Ok) throw new HelperException(status, Encoding.UTF8.GetString(r));
                    s.ReceiveTimeout = 0;                   // events arrive when a key is pressed, which may be never
                    return s;
                } catch { try { s.Dispose(); } catch { } throw; }
            }

            void Loop(Socket s) {
                while (!stop) {
                    if (s == null) {
                        for (int i = 0; i < 50 && !stop; i++) Thread.Sleep(100);
                        if (stop) break;
                        try { s = Open(); } catch { continue; }
                    }
                    current = s;
                    try {
                        var ns = new NetworkStream(s, false);
                        while (!stop) {
                            byte code;
                            byte[] p = HelperProtocol.ReadFrame(ns, out code);
                            if (code != HelperProtocol.EventFrame || p.Length < 8) continue;
                            try { handler(Native.Get32(p, 0), Native.Get32(p, 4)); } catch { }
                        }
                    } catch { }
                    try { s.Dispose(); } catch { }
                    s = null; current = null;
                }
            }

            public void Dispose() {
                stop = true;
                try { var s = current; if (s != null) s.Dispose(); } catch { }
            }
        }
    }
}
