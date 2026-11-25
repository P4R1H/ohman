// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: the firmware's own events (the OMEN key, the backlight key, AC changes), which arrive
// here as the WMI event root\wmi:hpqBEvnt. src\linux\BiosEvents.cs is the same class for Linux. What an event
// means is shared and lives in Engine.OnBiosEvent.
using System;
using System.Management;

namespace Ohman {
    sealed class BiosEvents : IDisposable {
        ManagementEventWatcher watcher;
        Action<uint, uint> handler;

        /// <summary>Start listening. Throws when the events cannot be had; the caller logs that and carries on.</summary>
        public static BiosEvents Start(Action<uint, uint> onEvent) {
            var b = new BiosEvents { handler = onEvent };
            var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"), new WqlEventQuery("SELECT * FROM hpqBEvnt"));
            w.EventArrived += b.Arrived;
            w.Start();
            b.watcher = w;
            return b;
        }

        void Arrived(object s, EventArrivedEventArgs e) {
            uint id = 0, data = 0;
            try { id = Convert.ToUInt32(e.NewEvent["EventID"]); data = Convert.ToUInt32(e.NewEvent["EventData"]); } catch { return; }
            handler(id, data);
        }

        public void Dispose() { watcher.Stop(); watcher.Dispose(); }
    }
}
