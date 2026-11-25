// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: /dev/ohman-wmi, the passthrough kernel module in linux\driver\ohman-wmi.c, as its ioctls.
// The device is root's (mode 0600, CAP_SYS_RAWIO checked on every call), so this class is used directly by
// whatever runs as root (ohman-helper, ohman-probe under sudo) and by nothing else: the GUI goes through the
// helper (HelperClient), and Mailbox / Driver choose between the two.
//
// The layouts and request numbers here are written out by hand and MUST match the C side:
//   struct ohman_wmi_call { u32 command, commandtype, insize, outsize, rc, outlen; u8 data[4096]; }   4120 bytes
//   struct ohman_ec_byte  { u8 reg, value; }                                                             2 bytes
//   OHMAN_WMI_IOC_CALL     = _IOWR('O', 1, struct ohman_wmi_call)
//   OHMAN_WMI_IOC_EC_READ  = _IOWR('O', 2, struct ohman_ec_byte)
//   OHMAN_WMI_IOC_EC_WRITE = _IOW ('O', 3, struct ohman_ec_byte)
//   OHMAN_WMI_IOC_VERSION  = _IOR ('O', 4, u32)
// Change one side and change the other.
using System;
using System.IO;

namespace Ohman {
    static class KernelModule {
        public const string Device = "/dev/ohman-wmi";
        /// <summary>The interface version this build was written against (OHMAN_WMI_VERSION in the C file).
        /// 1 had the mailbox call only; 2 added the EC bytes and the version call itself.</summary>
        public const int WantVersion = 2;

        const int Header = 24, MaxData = 4096, CallSize = Header + MaxData;
        static readonly UIntPtr IocCall = Native.IoWR('O', 1, CallSize);
        static readonly UIntPtr IocEcRead = Native.IoWR('O', 2, 2);
        static readonly UIntPtr IocEcWrite = Native.IoW('O', 3, 2);
        static readonly UIntPtr IocVersion = Native.IoR('O', 4, 4);

        static readonly object Sync = new object();
        static int fd = -1;

        /// <summary>Whether the module is loaded. Says nothing about being allowed to use it.</summary>
        public static bool Present { get { return File.Exists(Device); } }

        /// <summary>Whether this process can use the device itself: it is there and opens. Asked once per
        /// answer that is yes; a no is asked again, because the module can be loaded later.</summary>
        public static bool Usable {
            get {
                if (fd >= 0) return true;
                if (!Present) return false;
                try { Handle(); return true; } catch { return false; }
            }
        }

        static int Handle() {
            lock (Sync) {
                if (fd >= 0) return fd;
                if (!Present) throw new InvalidOperationException(Device + " is not there: the ohman-wmi kernel module is not loaded (or this is not an HP OMEN/Victus)");
                int h = Native.open(Device, Native.O_RDWR | Native.O_CLOEXEC);
                if (h < 0) {
                    int e = Native.Errno;
                    throw new UnauthorizedAccessException("cannot open " + Device + ": " + Native.Why(e) + (e == Native.EACCES || e == Native.EPERM ? "; it is root's" : ""));
                }
                fd = h;
                return h;
            }
        }

        /// <summary>The module's interface version: 1 for a module from before the version call existed.</summary>
        public static int Version() {
            var b = new byte[4];
            if (Native.ioctl(Handle(), IocVersion, b) != 0) {
                int e = Native.Errno;
                if (e == Native.ENOTTY) return 1;
                throw new IOException("the version call failed: " + Native.Why(e));
            }
            return (int)Native.Get32(b, 0);
        }

        /// <summary>One mailbox call. outSize must be 0, 4, 128, 1024 or 4096. Returns the reply, the whole reply
        /// size, zero-filled past what the firmware returned (which is what Windows hands back too), and the
        /// firmware's return code. A call that fails as a call throws.</summary>
        public static byte[] Call(uint command, uint commandType, byte[] data, int outSize, out uint rc) {
            if (data == null) data = new byte[0];
            if (data.Length > MaxData) throw new ArgumentException("mailbox input is limited to " + MaxData + " bytes");
            var c = new byte[CallSize];
            Native.Put32(c, 0, command);
            Native.Put32(c, 4, commandType);
            Native.Put32(c, 8, (uint)data.Length);
            Native.Put32(c, 12, (uint)outSize);
            Buffer.BlockCopy(data, 0, c, Header, data.Length);
            if (Native.ioctl(Handle(), IocCall, c) != 0) throw new IOException("the mailbox call failed as a call: " + Native.Why(Native.Errno));
            rc = Native.Get32(c, 16);
            if (rc != 0 || outSize == 0) return new byte[0];
            var o = new byte[outSize];
            Buffer.BlockCopy(c, Header, o, 0, outSize);
            return o;
        }

        /// <summary>One EC register, through the kernel's ec_read. 0, or the errno.</summary>
        public static int EcRead(byte register, out byte value) {
            value = 0;
            var b = new byte[] { register, 0 };
            int h;
            try { h = Handle(); } catch { return Native.ENODEV; }
            if (Native.ioctl(h, IocEcRead, b) != 0) { int e = Native.Errno; return e == 0 ? Native.EIO : e; }
            value = b[1];
            return 0;
        }

        /// <summary>One EC register, through the kernel's ec_write. 0, or the errno. No policy here: the caller
        /// (EmbeddedController, and ohman-helper before it) decides which registers may be written.</summary>
        public static int EcWrite(byte register, byte value) {
            var b = new byte[] { register, value };
            int h;
            try { h = Handle(); } catch { return Native.ENODEV; }
            if (Native.ioctl(h, IocEcWrite, b) != 0) { int e = Native.Errno; return e == 0 ? Native.EIO : e; }
            return 0;
        }
    }
}
