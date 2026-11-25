// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the handful of libc calls the Linux hardware layer is made of, in one place, and the
// ioctl request numbers computed the way the kernel's _IOC macro does. Used by the classes beside it and by
// ohman-helper. Nothing here knows what any device is for.
using System;
using System.Runtime.InteropServices;

namespace Ohman {
    static class Native {
        public const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
        public const int EPERM = 1, ENOENT = 2, EIO = 5, ENXIO = 6, EAGAIN = 11, EACCES = 13, ENODEV = 19, EINVAL = 22, ENOTTY = 25;

        [DllImport("libc", SetLastError = true)] public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, UIntPtr request, byte[] arg);
        [DllImport("libc", SetLastError = true)] public static extern IntPtr read(int fd, byte[] buf, UIntPtr count);
        [DllImport("libc", SetLastError = true)] public static extern IntPtr write(int fd, byte[] buf, UIntPtr count);
        [DllImport("libc", SetLastError = true)] public static extern IntPtr pread(int fd, byte[] buf, UIntPtr count, long offset);
        [DllImport("libc", SetLastError = true)] public static extern IntPtr pwrite(int fd, byte[] buf, UIntPtr count, long offset);
        [DllImport("libc", SetLastError = true)] public static extern int poll(byte[] fds, UIntPtr count, int timeoutMs);
        [DllImport("libc")] public static extern uint geteuid();
        [DllImport("libc", SetLastError = true)] public static extern int chown([MarshalAs(UnmanagedType.LPStr)] string path, int owner, int group);
        [DllImport("libc", SetLastError = true)] public static extern int chmod([MarshalAs(UnmanagedType.LPStr)] string path, uint mode);
        [DllImport("libc", SetLastError = true)] public static extern int lchown([MarshalAs(UnmanagedType.LPStr)] string path, int owner, int group);
        [DllImport("libc")] public static extern uint umask(uint mask);
        [DllImport("libc", SetLastError = true)] static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPStr)] string path, int flags, uint mask, byte[] buf);

        /// <summary>Owner, group and mode of a path itself (a symbolic link is not followed), through statx, whose
        /// layout is the same on every architecture: stx_uid at 20, stx_gid at 24, stx_mode at 28. False when it
        /// cannot be had.</summary>
        public static bool Stat(string path, out uint uid, out uint gid, out uint mode) {
            uid = gid = mode = 0;
            try {
                var b = new byte[256];
                if (statx(-100 /* AT_FDCWD */, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x7FF /* STATX_BASIC_STATS */, b) != 0) return false;
                uid = Get32(b, 20); gid = Get32(b, 24); mode = (uint)(b[28] | (b[29] << 8));
                return true;
            } catch { return false; }
        }

        public static int Errno { get { return Marshal.GetLastWin32Error(); } }
        public static bool IsRoot { get { try { return geteuid() == 0; } catch { return false; } } }

        // _IOC(dir, type, nr, size): nr in bits 0-7, type in 8-15, size in 16-29, direction in 30-31 (x86 and arm64).
        const uint IocWrite = 1, IocRead = 2;
        public static UIntPtr Io(uint dir, char type, uint nr, uint size) { return new UIntPtr((dir << 30) | (size << 16) | ((uint)type << 8) | nr); }
        public static UIntPtr IoR(char type, uint nr, uint size) { return Io(IocRead, type, nr, size); }
        public static UIntPtr IoW(char type, uint nr, uint size) { return Io(IocWrite, type, nr, size); }
        public static UIntPtr IoWR(char type, uint nr, uint size) { return Io(IocRead | IocWrite, type, nr, size); }

        public static void Put32(byte[] b, int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        public static uint Get32(byte[] b, int at) { return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)); }
        public static void Put64(byte[] b, int at, ulong v) { Put32(b, at, (uint)v); Put32(b, at + 4, (uint)(v >> 32)); }
        public static ulong Get64(byte[] b, int at) { return Get32(b, at) | ((ulong)Get32(b, at + 4) << 32); }

        /// <summary>An errno as a few words, for the ones that mean something specific here.</summary>
        public static string Why(int errno) {
            switch (errno) {
                case EPERM: return "not permitted (errno 1)";
                case EACCES: return "permission denied (errno 13)";
                case ENOENT: return "not there (errno 2)";
                case ENODEV: case ENXIO: return "no such device (errno " + errno + ")";
                case EIO: return "input/output error (errno 5)";
                default: return "errno " + errno;
            }
        }
    }
}
