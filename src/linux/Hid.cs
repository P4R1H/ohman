// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: raw HID through the kernel's hidraw driver. src\win\Hid.cs is the same class for Windows
// (hid.dll). What is said over it is shared: LampArray and PerKeyLighting in src\Hid.cs, the Primax keyboard in
// src\McuKeyboard.cs.
//
// Windows hands out one device per top-level collection and has already parsed the report descriptor; hidraw
// hands out one node per USB interface and the raw descriptor. So Enumerate walks /sys/class/hidraw, parses
// each descriptor (ParseDescriptor) and returns one entry per top-level collection, with the report lengths
// Windows would report for it (the longest report of each kind, plus the report-id byte), so the shared code
// sees the same thing on both systems. A path here is "/dev/hidrawN#mi_XX#colYY": the node, the USB interface
// number and the collection, because the shared code looks for "mi_02" in the path the way OGH does.
//
// hidraw nodes are root's by default. The installer's udev rule gives the laptop's own keyboards to group
// "ohman"; without it Enumerate still lists them (sysfs is readable) and Open fails, which is logged.
//
// UNTESTED on real hardware: the descriptor parser is checked against hand-made descriptors only, and no
// feature report has been exchanged with a real keyboard from Linux.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ohman {

    internal static class Hid {
        internal static readonly IntPtr Invalid = new IntPtr(-1);

        /// <summary>One HID collection: its path and what its report descriptor says it is.</summary>
        internal sealed class Info {
            public string Path, Product;
            public ushort VendorId, ProductId, UsagePage, Usage;
            public int InputLen, OutputLen, FeatureLen;
            public override string ToString() {
                return "VID_" + VendorId.ToString("X4") + " PID_" + ProductId.ToString("X4") +
                    " usage " + UsagePage.ToString("X2") + "/" + Usage.ToString("X2") +
                    " reports in/out/feat " + InputLen + "/" + OutputLen + "/" + FeatureLen +
                    (string.IsNullOrEmpty(Product) ? "" : " \"" + Product + "\"");
            }
        }

        /// <summary>What a descriptor says about one node: whether its reports carry ids, and how long each
        /// feature report is. hidraw wants a feature report's own length, not the collection's longest.</summary>
        sealed class Node {
            public bool Numbered;
            public readonly Dictionary<byte, int> FeatureBytes = new Dictionary<byte, int>();   // report id -> bytes, id excluded
        }
        static readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>();      // by /dev/hidrawN
        static readonly Dictionary<int, Node> open = new Dictionary<int, Node>();              // by file descriptor

        static string Device(string path) { int h = path.IndexOf('#'); return h < 0 ? path : path.Substring(0, h); }

        /// <summary>Every HID collection on the machine. Read-only: nothing is opened, only sysfs is read.</summary>
        internal static List<Info> Enumerate() {
            var found = new List<Info>();
            try {
                const string root = "/sys/class/hidraw";
                if (!Directory.Exists(root)) return found;
                string[] dirs = Directory.GetDirectories(root);
                Array.Sort(dirs, StringComparer.Ordinal);
                foreach (string dir in dirs) {
                    try { Describe(dir, found); } catch (Exception ex) { Log.Write("hid describe " + System.IO.Path.GetFileName(dir) + ": " + ex.Message); }
                }
            } catch (Exception ex) { Log.Write("hid enumerate: " + ex.Message); }
            return found;
        }

        static void Describe(string sysDir, List<Info> found) {
            string dev = "/dev/" + System.IO.Path.GetFileName(sysDir);
            string hid = System.IO.Path.Combine(sysDir, "device");
            ushort vid = 0, pid = 0;
            string name = "";
            foreach (string line in File.ReadAllLines(System.IO.Path.Combine(hid, "uevent"))) {
                if (line.StartsWith("HID_ID=", StringComparison.Ordinal)) {
                    string[] p = line.Substring(7).Split(':');          // bus:vendor:product, hex, zero-padded to 8
                    if (p.Length == 3) {
                        vid = (ushort)Convert.ToUInt32(p[1], 16);
                        pid = (ushort)Convert.ToUInt32(p[2], 16);
                    }
                } else if (line.StartsWith("HID_NAME=", StringComparison.Ordinal)) name = line.Substring(9);
            }
            // The HID device's parent is the USB interface ("1-5:1.2"), whose bInterfaceNumber is the mi_ in a
            // Windows path. Not a USB device (I2C, Bluetooth): no interface number, and no "mi_" in the path.
            string mi = "";
            try {
                string n = Host.ReadText(System.IO.Path.Combine(hid, "..", "bInterfaceNumber"));
                if (n.Length > 0) mi = "#mi_" + Convert.ToInt32(n, 16).ToString("x2");
            } catch { }
            byte[] desc = File.ReadAllBytes(System.IO.Path.Combine(hid, "report_descriptor"));
            var node = new Node();
            List<Info> cols = ParseDescriptor(desc, node);
            lock (nodes) nodes[dev] = node;
            for (int i = 0; i < cols.Count; i++) {
                Info c = cols[i];
                c.Path = dev + mi + "#col" + (i + 1).ToString("d2");
                c.VendorId = vid; c.ProductId = pid; c.Product = name;
                found.Add(c);
            }
        }

        /// <summary>A report descriptor as its top-level collections: the usage each declares and the longest
        /// input, output and feature report inside it, in bytes with one added for the report id, which is how
        /// HidP_GetCaps counts them. (USB HID 1.11, section 6.2.2.) node, when given, collects what hidraw
        /// needs to know about the whole interface.</summary>
        internal static List<Info> ParseDescriptor(byte[] d, object nodeObject) {
            var node = nodeObject as Node;
            var cols = new List<Info>();
            var bits = new List<Dictionary<int, int>[]>();          // per collection: [input, output, feature] -> report id -> bits
            uint usagePage = 0, reportSize = 0, reportCount = 0, reportId = 0;
            var stack = new Stack<uint[]>();
            var usages = new List<uint>();
            int depth = 0, current = -1;
            int i = 0;
            while (i < d.Length) {
                byte prefix = d[i++];
                if (prefix == 0xFE) {                                // a long item: size, tag, data. Nothing uses them; step over.
                    if (i + 1 >= d.Length) break;
                    i += 2 + d[i];
                    continue;
                }
                int size = prefix & 3; if (size == 3) size = 4;
                int type = (prefix >> 2) & 3, tag = prefix >> 4;
                if (i + size > d.Length) break;
                uint v = 0;
                for (int k = 0; k < size; k++) v |= (uint)d[i + k] << (8 * k);
                i += size;
                if (type == 1) {                                     // global
                    switch (tag) {
                        case 0: usagePage = v; break;
                        case 7: reportSize = v; break;
                        case 8: reportId = v; if (node != null) node.Numbered = true; break;
                        case 9: reportCount = v; break;
                        case 10: stack.Push(new uint[] { usagePage, reportSize, reportCount, reportId }); break;
                        case 11: if (stack.Count > 0) { uint[] g = stack.Pop(); usagePage = g[0]; reportSize = g[1]; reportCount = g[2]; reportId = g[3]; } break;
                    }
                } else if (type == 2) {                              // local
                    if (tag == 0) usages.Add(size == 4 ? v : (usagePage << 16) | v);
                } else if (type == 0) {                              // main
                    if (tag == 10) {                                 // collection
                        if (depth == 0) {
                            uint u = usages.Count > 0 ? usages[0] : usagePage << 16;
                            cols.Add(new Info { UsagePage = (ushort)(u >> 16), Usage = (ushort)u });
                            bits.Add(new Dictionary<int, int>[] { new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, int>() });
                            current = cols.Count - 1;
                        }
                        depth++;
                    } else if (tag == 12) {                          // end collection
                        if (depth > 0) depth--;
                        if (depth == 0) current = -1;
                    } else if ((tag == 8 || tag == 9 || tag == 11) && current >= 0) {
                        int kind = tag == 8 ? 0 : tag == 9 ? 1 : 2;
                        int had;
                        bits[current][kind].TryGetValue((int)reportId, out had);
                        bits[current][kind][(int)reportId] = had + (int)(reportSize * reportCount);
                    }
                    usages.Clear();
                }
            }
            for (int c = 0; c < cols.Count; c++) {
                int[] len = new int[3];
                for (int kind = 0; kind < 3; kind++)
                    foreach (var kv in bits[c][kind]) {
                        int bytes = (kv.Value + 7) / 8;
                        if (bytes + 1 > len[kind]) len[kind] = bytes + 1;
                        if (kind == 2 && node != null) node.FeatureBytes[(byte)kv.Key] = bytes;
                    }
                cols[c].InputLen = len[0]; cols[c].OutputLen = len[1]; cols[c].FeatureLen = len[2];
            }
            return cols;
        }

        /// <summary>Open for feature-report traffic. The handle is the file descriptor.</summary>
        internal static IntPtr Open(string path) {
            string dev = Device(path);
            int fd = Native.open(dev, Native.O_RDWR | Native.O_CLOEXEC);
            if (fd < 0) return Invalid;
            Node n;
            lock (nodes) nodes.TryGetValue(dev, out n);
            lock (open) open[fd] = n;
            return new IntPtr(fd);
        }
        internal static bool CloseHandle(IntPtr h) {
            int fd = h.ToInt32();
            lock (open) open.Remove(fd);
            return Native.close(fd) == 0;
        }

        /// <summary>The length to exchange for the report whose id is buf[0]: that report's own, id included.
        /// The caller's buffer is as long as the collection's longest report, which is what Windows wants and
        /// more than a shorter report's transfer carries.</summary>
        static int FeatureLength(int fd, byte[] buf) {
            Node n;
            lock (open) open.TryGetValue(fd, out n);
            int bytes;
            if (n != null && n.FeatureBytes.TryGetValue(buf[0], out bytes)) return Math.Min(buf.Length, bytes + 1);
            return buf.Length;
        }

        // HIDIOCSFEATURE(len) = _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x06, len); HIDIOCGFEATURE(len) the same with 0x07
        // (linux/hidraw.h). The first byte of the buffer is the report id, 0 when the device does not number them.
        internal static bool GetFeature(IntPtr h, byte[] buf) {
            int fd = h.ToInt32();
            int len = FeatureLength(fd, buf);
            var b = new byte[len];
            b[0] = buf[0];
            int rc = Native.ioctl(fd, Native.IoWR('H', 0x07, (uint)len), b);
            if (rc < 0) return false;
            Buffer.BlockCopy(b, 0, buf, 0, Math.Min(len, buf.Length));
            return true;
        }
        internal static bool SetFeature(IntPtr h, byte[] buf) {
            int fd = h.ToInt32();
            int len = FeatureLength(fd, buf);
            var b = new byte[len];
            Buffer.BlockCopy(buf, 0, b, 0, len);
            return Native.ioctl(fd, Native.IoWR('H', 0x06, (uint)len), b) >= 0;
        }

        /// <summary>Open for input and output reports as a stream of whole reports, report id first, which is
        /// what the Windows stream gives. Null when it cannot be opened; error is then the errno.</summary>
        internal static Stream OpenStream(string path, int reportLen, out int error) {
            error = 0;
            string dev = Device(path);
            int fd = Native.open(dev, Native.O_RDWR | Native.O_NONBLOCK | Native.O_CLOEXEC);
            if (fd < 0) { error = Native.Errno; return null; }
            Node n;
            lock (nodes) nodes.TryGetValue(dev, out n);
            return new HidrawStream(fd, n != null && n.Numbered);
        }

        /// <summary>Whole reports over a hidraw node. hidraw leaves the report id off an input report when the
        /// device does not number its reports, and Windows always hands back a leading zero for it; reads here
        /// put it back so the shared code finds every byte where it expects it. Writes already agree: both
        /// systems want the id (or a zero) first.</summary>
        sealed class HidrawStream : Stream {
            readonly int fd;
            readonly bool numbered;
            volatile bool closed;
            public HidrawStream(int fd, bool numbered) { this.fd = fd; this.numbered = numbered; }

            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) {
                var pfd = new byte[8];
                var tmp = new byte[Math.Max(1, count)];
                while (!closed) {
                    Native.Put32(pfd, 0, (uint)fd);
                    pfd[4] = 1; pfd[5] = 0; pfd[6] = 0; pfd[7] = 0;       // POLLIN
                    int r = Native.poll(pfd, new UIntPtr(1), 200);
                    if (r == 0) continue;
                    if (r < 0) { if (Native.Errno == 4) continue; throw new IOException("hidraw poll: " + Native.Why(Native.Errno)); }
                    if ((pfd[6] & 0x18) != 0) throw new IOException("the keyboard went away");
                    int room = numbered ? count : count - 1;
                    long n = Native.read(fd, tmp, new UIntPtr((uint)Math.Max(1, room))).ToInt64();
                    if (n < 0) { if (Native.Errno == Native.EAGAIN) continue; throw new IOException("hidraw read: " + Native.Why(Native.Errno)); }
                    if (numbered) { Buffer.BlockCopy(tmp, 0, buffer, offset, (int)n); return (int)n; }
                    buffer[offset] = 0;
                    Buffer.BlockCopy(tmp, 0, buffer, offset + 1, (int)n);
                    return (int)n + 1;
                }
                throw new ObjectDisposedException("hidraw");
            }

            public override void Write(byte[] buffer, int offset, int count) {
                var b = buffer;
                if (offset != 0) { b = new byte[count]; Buffer.BlockCopy(buffer, offset, b, 0, count); }
                long n = Native.write(fd, b, new UIntPtr((uint)count)).ToInt64();
                if (n != count) throw new IOException("hidraw write: " + Native.Why(Native.Errno));
            }

            // Not the base class's: Stream serialises its asynchronous operations, so a read left waiting for a
            // reply would hold the next write back. A read and a write on a hidraw node are independent.
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) {
                return Task.Run(delegate() { return Read(buffer, offset, count); });
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) {
                return Task.Run(delegate() { Write(buffer, offset, count); });
            }

            protected override void Dispose(bool disposing) {
                if (!closed) { closed = true; Native.close(fd); }
                base.Dispose(disposing);
            }
        }
    }
}
