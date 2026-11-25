// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: the lock every tool that talks to the embedded controller is expected to hold while it
// does. src\linux\EcLock.cs is the same class for Linux. Who takes it and when is shared: src\Ec.cs.
using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Ohman {
    static class EcLock {
        /// <summary>The shared lock, created so that anyone can open it afterwards. OmenMon does the same; an
        /// admin-only mutex would make the next tool fail to open it and go ahead without it, which is worse.</summary>
        public static Mutex Open(string name) {
            try {
                var sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                return new Mutex(false, name, out created, sec);
            } catch (UnauthorizedAccessException) {
                try { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); } catch { return null; }
            } catch { return null; }
        }
    }
}
