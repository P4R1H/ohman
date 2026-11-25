// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the lock held around embedded-controller access. src\win\EcLock.cs is the same class for
// Windows, where other tools share it by name. Here it only keeps two Ohman processes apart; the kernel's own
// EC driver serialises single transactions for everybody else.
using System;
using System.Threading;

namespace Ohman {
    static class EcLock {
        public static Mutex Open(string name) {
            try { return new Mutex(false, name); } catch { return null; }
        }
    }
}
