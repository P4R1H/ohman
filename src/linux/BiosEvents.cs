// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: the firmware's own events (the OMEN key). src\win\BiosEvents.cs is the same class for
// Windows, where they arrive as the WMI event hpqBEvnt. What an event means is shared: Engine.OnBiosEvent.
//
// On Linux the kernel's hp-wmi driver owns those events and turns the keys into input events, so this reads
// that input device (HpHotkeys in HpWmi.cs) and hands the engine the same (EventID, EventData) pair Windows
// would have: 29 / 0x21A5 for the OMEN key. As root that is done here directly; as an ordinary user the helper
// does the reading and pushes the pairs down a connection of their own.
//
// Only keys arrive this way. The backlight key (event 13) and the AC notification (131073) are consumed inside
// the kernel and have no counterpart on that device; the engine already learns the power source from
// OnPowerSource, and the backlight state from the firmware when it next asks.
using System;

namespace Ohman {
    sealed class BiosEvents : IDisposable {
        IDisposable source;

        /// <summary>Start listening. Throws when the events cannot be had; the caller logs that and carries on.</summary>
        public static BiosEvents Start(Action<uint, uint> onEvent) {
            var b = new BiosEvents();
            switch (Route.Now) {
                case Route.Via.Helper: b.source = HelperClient.Subscribe(onEvent); break;
                case Route.Via.Device:
                    if (!HpWmi.Present) throw new NotSupportedException("the hp-wmi driver is not loaded, so there is no hotkey device to read");
                    b.source = new HpHotkeys(onEvent);
                    break;
                default: throw new InvalidOperationException(Route.WhyNone);
            }
            return b;
        }

        public void Dispose() { try { if (source != null) source.Dispose(); } catch { } }
    }
}
