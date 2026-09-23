// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the embedded controller, through the driver.
//
// The EC is the chip that actually runs the fans; the WMI mailbox is the firmware relaying requests to it. On
// most boards the relay works and this file is only ever read from. On a few it refuses fan levels outright
// (878A answers rc 46 forever), and there the only way to the fans is the EC's own registers, reached through
// ports 0x62 and 0x66 with the handshake the ACPI specification describes (section 12, "Embedded Controller").
//
// Two things this file is careful about, both from other people's bug reports:
//  * Writing the classic addresses on a 2025 OMEN MAX corrupts EC state until the Caps Lock LED blinks in panic
//    (OmenCore issue #60), so those boards get no map at all and writes are limited to the map's own list.
//  * The EC also answers the battery, the lid and the keyboard. Flood it and its ACPI transactions time out
//    (Event 13), Windows loses the battery reading and runs the critical-battery action, which is a shutdown on
//    a plugged-in laptop (OmenCore 2.8.6). So every wait is bounded and a controller that times out is left be.
//
// The map is OmenMon's (Hardware/EcData.cs), agreeing with omen-fan's probes and OmenCore's code: 0x34/0x35 set
// each fan in rpm/100, 0x62 hands manual control over with 0x06 and back with 0x00, and 0x63 is the countdown
// after which the EC takes the fans back, which is the 120 s expiry the mailbox path keeps alive with 0x10.
using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Ohman {

    /// <summary>The two EC ports, however they are reached. Return 0 on success, else a Win32 error.</summary>
    public interface IEcPorts {
        int In(byte port, out byte value);
        int Out(byte port, byte value);
    }

    /// <summary>The ports through PawnIO's LpcACPIEC module, which allows exactly 0x62 and 0x66.</summary>
    public sealed class PawnIoEcPorts : IEcPorts, IDisposable {
        readonly PawnIoModule m;
        public PawnIoEcPorts(PawnIoModule module) { m = module; }
        public int In(byte port, out byte value) {
            var o = new ulong[1]; int n;
            int rc = m.Execute("ioctl_pio_read", new ulong[] { port }, o, out n);
            value = rc == 0 ? (byte)o[0] : (byte)0;
            return rc;
        }
        public int Out(byte port, byte value) { int n; return m.Execute("ioctl_pio_write", new ulong[] { port, value }, null, out n); }
        public void Dispose() { m.Dispose(); }
    }

    /// <summary>Where things are in this generation's EC. Every address is a claim with a source behind it.</summary>
    public sealed class EcMap {
        public string Name;
        public byte CpuTemp = 0x57, GpuTemp = 0xB7;                 // CPUT, GPTM: degrees C
        public byte Rpm1 = 0xB0, Rpm2 = 0xB2;                       // RPM1/2, RPM3/4: 16-bit little-endian rpm per fan
        public byte FanSet1 = 0x34, FanSet2 = 0x35;                 // SRP1, SRP2: rpm/100, the mailbox's own unit
        public byte FanSetPct1 = 0x2C, FanSetPct2 = 0x2D;           // XSS1, XSS2: the same thing as a percentage
        /// <summary>Which pair actually drives the fans here. Both exist on every HP board anyone has looked at,
        /// only one is wired to anything, and which one is a per-model fact: NBFC's community configs have HP
        /// laptops of the same year using each. Only ProbeFanWrite can answer it; its answer is kept in the
        /// file Engine.EcPairPath names and read back when the EC is opened.</summary>
        public bool UsePercent;
        public byte Manual = 0x62, ManualOn = 0x06, ManualOff = 0x00;   // OMCC
        // XFCD counts down and hands the fans back when it reaches zero, so zero itself is "no timeout" and is
        // what omen-fan writes to take control. Ohman refreshes every tick instead and uses the timeout as the
        // backstop: if Ohman dies, the fans are the controller's again within CountdownHold seconds.
        public byte Countdown = 0x63, CountdownRelease = 0x01, CountdownHold = 0xFF;
        public byte Mode = 0x95, Charge = 0x96;                     // HPCM, XBCH: read for the report only
        byte[] writable;
        /// <summary>Every register a write may touch. Anything else is refused in code, whatever the caller.</summary>
        public bool MayWrite(byte reg) {
            if (writable == null) writable = new byte[] { FanSet1, FanSet2, FanSetPct1, FanSetPct2, Manual, Countdown };
            foreach (byte w in writable) if (w == reg) return true;
            return false;
        }
        /// <summary>A fan level in the mailbox's unit (rpm/100) as this board's registers want it.</summary>
        public byte Encode(int level, int ceiling) {
            if (level <= 0) return 0;
            if (!UsePercent) return (byte)Math.Min(255, level);
            return (byte)Math.Max(0, Math.Min(100, (int)Math.Round(100.0 * level / Math.Max(1, ceiling))));
        }

        /// <summary>The 2018–2022 OMEN layout: OmenMon (developed on 8A14), omen-fan (16-c0140AX), OmenCore
        /// (8574 fan control confirmed in the field). Not the 2025 OMEN MAX, whose layout differs.</summary>
        public static EcMap Legacy() { return new EcMap { Name = "OMEN 2018-2022 (OmenMon / omen-fan map)" }; }
    }

    /// <summary>One read of everything the map names. -1 where the read failed.</summary>
    public sealed class EcReading {
        public int Cpu = -1, Gpu = -1, Rpm1 = -1, Rpm2 = -1, Manual = -1, Countdown = -1, Mode = -1, Charge = -1;
        public bool Any { get { return Cpu >= 0 || Rpm1 >= 0 || Manual >= 0; } }
    }

    public sealed class EmbeddedController : IDisposable {
        const byte DataPort = 0x62, CommandPort = 0x66;
        const byte CmdRead = 0x80, CmdWrite = 0x81;
        const byte Obf = 0x01, Ibf = 0x02;              // status bits: output buffer full, input buffer full
        const int WaitPolls = 400;                     // bounded: ~20 ms of polling, then 1 ms sleeps, never more than ~400 ms
        const int MutexWaitMs = 250;
        const int TimeoutsBeforeRest = 5;
        static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

        public readonly EcMap Map;
        readonly IEcPorts ports;
        readonly Mutex mutex;                          // Global\Access_EC: OmenMon, LibreHardwareMonitor, OmenCore and HWiNFO all take it
        readonly object sync = new object();
        int timeouts;
        DateTime restUntil = DateTime.MinValue;
        public int Timeouts { get { return timeouts; } }
        public string LastError = "";
        readonly Dictionary<byte, byte> lastWritten = new Dictionary<byte, byte>();
        readonly Dictionary<byte, DateTime> lastWrittenAt = new Dictionary<byte, DateTime>();

        public EmbeddedController(IEcPorts ports, EcMap map) {
            this.ports = ports;
            Map = map;
            mutex = OpenMutex(@"Global\Access_EC");
        }

        /// <summary>The shared lock, created so that anyone can open it afterwards. OmenMon does the same; an
        /// admin-only mutex would make the next tool fail to open it and go ahead without it, which is worse.</summary>
        static Mutex OpenMutex(string name) {
            try {
                var sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                return new Mutex(false, name, out created, sec);
            } catch (UnauthorizedAccessException) {
                try { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); } catch { return null; }
            } catch { return null; }
        }

        /// <summary>Resting after repeated timeouts. Callers on the fan path fall back to the mailbox meanwhile.</summary>
        public bool Resting { get { return DateTime.Now < restUntil; } }

        // ---------- the handshake ----------
        bool Status(out byte s) { return ports.In(CommandPort, out s) == 0; }
        bool WaitFor(byte bit, bool set) {
            byte s;
            for (int i = 0; i < WaitPolls; i++) {
                if (!Status(out s)) return false;
                if (((s & bit) != 0) == set) return true;
                if (i >= 20) Thread.Sleep(1);
            }
            return false;
        }
        /// <summary>A byte left in the output buffer by an earlier, interrupted transaction would be handed to us
        /// as the answer to this one. Take it out first.</summary>
        void Drain() {
            byte s, junk;
            if (Status(out s) && (s & Obf) != 0) ports.In(DataPort, out junk);
        }
        bool ReadRaw(byte reg, out byte value) {
            value = 0;
            Drain();
            if (!WaitFor(Ibf, false) || ports.Out(CommandPort, CmdRead) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, reg) != 0) return false;
            if (!WaitFor(Obf, true)) return false;
            return ports.In(DataPort, out value) == 0;
        }
        bool WriteRaw(byte reg, byte value) {
            if (!WaitFor(Ibf, false) || ports.Out(CommandPort, CmdWrite) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, reg) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, value) != 0) return false;
            return WaitFor(Ibf, false);
        }

        /// <summary>Run one or more transactions under the lock. False when the lock is busy, the controller is
        /// resting, or the body reported a failure; a timeout counts towards the rest.</summary>
        bool Locked(Func<bool> body, string what) {
            if (Resting) { LastError = "EC resting after " + timeouts + " timeouts"; return false; }
            bool held = false;
            lock (sync) {
                try {
                    if (mutex != null) {
                        try { held = mutex.WaitOne(MutexWaitMs); } catch (AbandonedMutexException) { held = true; }
                        if (!held) { LastError = "EC busy (another program holds it)"; return false; }
                    }
                    bool ok = body();
                    if (ok) { timeouts = 0; LastError = ""; }
                    else {
                        timeouts++;
                        if (LastError.Length == 0) LastError = "EC did not answer (" + what + ")";
                        if (timeouts >= TimeoutsBeforeRest) { restUntil = DateTime.Now + Rest; Log.Write("EC: " + timeouts + " timeouts in a row; leaving it alone for " + Rest.TotalMinutes + " minutes"); }
                    }
                    return ok;
                } catch (Exception ex) { LastError = ex.Message; return false; }
                finally { if (held) { try { mutex.ReleaseMutex(); } catch { } } }
            }
        }

        // ---------- what callers use ----------
        public bool ReadByte(byte reg, out byte value) { byte v = 0; bool ok = Locked(delegate { return ReadRaw(reg, out v); }, "read 0x" + reg.ToString("X2")); value = v; return ok; }

        /// <summary>Forget what was written, so the next write is sent whatever it is.</summary>
        void Forget() { lastWritten.Clear(); }

        /// <summary>A write, to a register on the map's own list only. Repeating a value the register already
        /// holds is skipped for five seconds: the fan path calls every tick and the EC does not need telling twice.</summary>
        public bool WriteByte(byte reg, byte value) {
            if (!Map.MayWrite(reg)) { LastError = "0x" + reg.ToString("X2") + " is not a register this map allows writing"; Log.Write("EC: refused write to " + LastError); return false; }
            byte had; DateTime at;
            // Never for a register the controller changes by itself. The countdown is decremented in hardware, so
            // "we already wrote 255 to it" is no reason to believe it still says 255 - and with the fan tick at
            // five seconds and this window at five seconds, whether the refresh was sent came down to jitter.
            if (reg != Map.Countdown
                && lastWritten.TryGetValue(reg, out had) && had == value && lastWrittenAt.TryGetValue(reg, out at) && (DateTime.Now - at).TotalSeconds < 5) return true;
            bool ok = Locked(delegate { return WriteRaw(reg, value); }, "write 0x" + reg.ToString("X2"));
            if (ok) { lastWritten[reg] = value; lastWrittenAt[reg] = DateTime.Now; }
            return ok;
        }

        /// <summary>Everything on the map in one hold of the lock, for the panel and the report.</summary>
        public EcReading Read() {
            var r = new EcReading();
            Locked(delegate {
                byte lo, hi, v;
                if (ReadRaw(Map.CpuTemp, out v)) r.Cpu = v;
                if (ReadRaw(Map.GpuTemp, out v)) r.Gpu = v;
                if (ReadRaw(Map.Rpm1, out lo) && ReadRaw((byte)(Map.Rpm1 + 1), out hi)) r.Rpm1 = lo | (hi << 8);
                if (ReadRaw(Map.Rpm2, out lo) && ReadRaw((byte)(Map.Rpm2 + 1), out hi)) r.Rpm2 = lo | (hi << 8);
                if (ReadRaw(Map.Manual, out v)) r.Manual = v;
                if (ReadRaw(Map.Countdown, out v)) r.Countdown = v;
                if (ReadRaw(Map.Mode, out v)) r.Mode = v;
                if (ReadRaw(Map.Charge, out v)) r.Charge = v;
                return r.Any;
            }, "snapshot");
            return r;
        }

        /// <summary>Take the fans and set both levels, in the mailbox's unit (rpm/100). Manual mode is asserted
        /// first and read back after: a controller that does not keep 0x06 in the manual register is not one this
        /// map fits, and the caller must stop using it. The countdown is set to its maximum so the hold outlives
        /// the caller's own 5 s tick many times over; the caller still calls every tick, and a repeat is free.</summary>
        public bool HoldFans(int level1, int level2, int ceiling) {
            Claim();
            byte l1 = Map.Encode(level1, ceiling), l2 = Map.Encode(level2, ceiling);
            byte r1 = Map.UsePercent ? Map.FanSetPct1 : Map.FanSet1, r2 = Map.UsePercent ? Map.FanSetPct2 : Map.FanSet2;
            if (!WriteByte(Map.Manual, Map.ManualOn)) return false;
            if (!WriteByte(r1, l1) || !WriteByte(r2, l2)) return false;
            if (!WriteByte(Map.Countdown, Map.CountdownHold)) return false;
            byte check;
            if (!ReadByte(Map.Manual, out check)) return false;
            if (check != Map.ManualOn) { LastError = "manual register reads 0x" + check.ToString("X2") + " after writing 0x" + Map.ManualOn.ToString("X2") + "; this EC does not follow the map"; return false; }
            return true;
        }

        /// <summary>What the controller looked like before Ohman touched it, taken once on the way in.
        ///
        /// 0x62 is not Ohman's register. The firmware's own mailbox relay reads it to decide whether a fan level
        /// arriving on 0x2E gets passed to the fans, so whatever is in it belongs to the firmware and handing the
        /// fans back means putting that value back, not writing the zero that looks like "off" from this side.</summary>
        int entryManual = -1, entryCountdown = -1;
        void Claim() {
            if (entryManual >= 0) return;
            byte v;
            if (ReadByte(Map.Manual, out v)) entryManual = v;
            if (ReadByte(Map.Countdown, out v)) entryCountdown = v;
        }

        /// <summary>Hand the fans back: the control registers restored to the values they held before Ohman
        /// wrote them, and nothing set to zero.
        ///
        /// 1.1 did the opposite of both and shipped a laptop with two stopped fans and no way back. It wrote
        /// 0x62 = 0x00 because that is "BIOS control" in omen-fan, where it is the right answer because omen-fan
        /// is the only writer. Here it is not: the firmware relay behind the WMI mailbox reads the same byte, and
        /// with it clear the relay silently dropped every level Ohman sent afterwards. The log shows it exactly,
        /// with 0x2E given 49/49 and 0x2D reading back 0/0 ten seconds later on the same machine. Max fan went
        /// the same way, which is why the thermal guard could do nothing at 98 C. Clearing the level registers on
        /// top of that is what made the state stopped rather than merely stale.
        ///
        /// So: put back what was found, and leave the last level alone. It is a speed the machine was happy to
        /// run at, and whoever takes over replaces it on their next write. A stale high level is survivable and a
        /// stale zero is not.</summary>
        public bool ReleaseFans() {
            Forget();
            // Restore only what was actually taken. When the snapshot failed there is nothing to give back, and
            // writing a guessed 0x00 here is precisely the bug: it is the value that tells the firmware relay to
            // stop passing fan levels on, and picking it as a fallback would reintroduce it on the one path where
            // the EC is least well understood.
            bool ok = true;
            if (entryManual >= 0) ok &= WriteByte(Map.Manual, (byte)entryManual);
            if (entryCountdown >= 0) ok &= WriteByte(Map.Countdown, (byte)entryCountdown);
            entryManual = entryCountdown = -1;
            return ok;
        }

        /// <summary>Did the fans actually come back? Called after a release on the way out of manual control,
        /// because a release that silently did not take is the one failure here that can hurt somebody. Writes a
        /// survivable level through the controller if they are still stopped.</summary>
        public bool ConfirmFansRunning(int safeLevel, int ceiling) {
            Thread.Sleep(3000);
            EcReading r = Read();
            if (r.Rpm1 > 300 || r.Rpm2 > 300) return true;                   // turning
            // A tachometer that did not answer has confirmed nothing, and this is the one place a guess is not
            // allowed: the fans were just stopped on purpose, so no answer is treated the same as no movement.
            Log.Write("EC: fans " + (r.Rpm1 < 0 ? "could not be read" : "still read " + r.Rpm1 + "/" + r.Rpm2 + " rpm") + " after handing control back; forcing " + safeLevel);
            Forget();
            HoldFans(safeLevel, safeLevel, ceiling);
            return false;
        }

        /// <summary>Which register pair drives the fans here, found by driving them. Everything else about this
        /// controller can be checked by reading it; this cannot, because both pairs accept a write and only one
        /// is connected. Only ever asks for more air than is already moving, and hands the fans back in a
        /// finally whatever happens. Fifteen seconds.</summary>
        /// <param name="pair">0 nothing moved the fans, 1 the rpm pair did, 2 the percent pair did.</param>
        public string ProbeFanWrite(int safeLevel, int ceiling, out int pair) {
            pair = 0;
            var sb = new StringBuilder();
            Claim();
            int rest = AverageRpm();
            if (rest < 0) return "  the tachometers did not answer, so there is nothing to measure a change against\n";
            sb.AppendLine("  fans at rest:    " + rest + " rpm");
            if (rest > 4200) sb.AppendLine("  NOTE: the fans are already fast, so a rise may not be visible. Run this on an idle machine.");
            try {
                for (int pass = 0; pass < 2; pass++) {
                    bool pct = pass == 1;
                    byte r1 = pct ? Map.FanSetPct1 : Map.FanSet1, r2 = pct ? Map.FanSetPct2 : Map.FanSet2;
                    byte v = pct ? (byte)80 : (byte)50;
                    string what = "0x" + r1.ToString("X2") + "/0x" + r2.ToString("X2") + " = " + v + (pct ? "%" : " (rpm/100)");
                    Forget();
                    // Per pass, because the release at the end of the previous one gave the snapshot back and
                    // cleared it. Without this the second pass takes control without recording what it took, and
                    // the release in the finally has nothing to restore and leaves the machine in manual.
                    Claim();
                    if (!(WriteByte(Map.Manual, Map.ManualOn) && WriteByte(Map.Countdown, Map.CountdownHold)
                        && WriteByte(r1, v) && WriteByte(r2, v))) {
                        sb.AppendLine("  " + what.PadRight(27) + "the write was refused (" + LastError + ")");
                        continue;
                    }
                    Thread.Sleep(5000);
                    int now = AverageRpm();
                    int rise = now - rest;
                    sb.AppendLine("  " + what.PadRight(27) + now + " rpm, " + (rise >= 0 ? "+" : "") + rise
                        + (rise > 400 ? "   <-- this pair drives the fans on this board" : "   no change"));
                    if (rise > 400 && pair == 0) pair = pct ? 2 : 1;
                    ReleaseFans();      // not zero-with-manual-on, which is a stopped fan
                    Thread.Sleep(2500);
                }
            } finally {
                bool released = ReleaseFans();
                // Not "we wrote the register", but "the fans are turning". This test is the one thing in Ohman
                // that deliberately stops somebody's fans for a few seconds, so it does not get to walk away on
                // the strength of a write that returned true.
                bool spinning = ConfirmFansRunning(safeLevel, ceiling);
                if (released && spinning) sb.AppendLine("  fans handed back to the controller and turning again.");
                else if (spinning) sb.AppendLine("  handover reported an error (" + LastError + ") but the fans are turning.");
                else sb.AppendLine("  fans did not restart on their own, so they are being held at " + safeLevel + " instead. Please say so in the issue.");
            }
            return sb.ToString();
        }
        int AverageRpm() {
            int sum = 0, n = 0;
            for (int i = 0; i < 3; i++) {
                EcReading r = Read();
                if (r.Rpm1 >= 0) { sum += r.Rpm1; n++; }
                Thread.Sleep(400);
            }
            return n > 0 ? sum / n : -1;
        }

        /// <summary>Does this controller actually follow the map? Asked once, on the machine, before anything is
        /// written - which is worth more than any list of board ids, because it is evidence rather than a guess
        /// about what a number near another number means.
        ///
        /// Four questions, cheapest first. The control register is a two-valued thing: if it holds anything but
        /// the two fan-control states then this address is not that register here, and that is the one that takes
        /// the fans away from the firmware. The temperature register has to read like a temperature, and has to
        /// agree with the CPU's own sensor when the driver can supply one. The tachometers have to read like fan
        /// speeds, and have to agree with the firmware's own answer when it will give one.</summary>
        public bool Verify(int[] mailboxRpm, double dieTemp, out string why) {
            return VerifyReading(Read(), Map, mailboxRpm, dieTemp, LastError, out why);
        }

        public static bool VerifyReading(EcReading r, EcMap map, int[] mailboxRpm, double dieTemp, string lastError, out string why) {
            if (r == null || !r.Any) { why = !string.IsNullOrEmpty(lastError) ? lastError : "the EC did not answer"; return false; }
            if (map != null && r.Manual != map.ManualOff && r.Manual != map.ManualOn) {
                why = "0x" + map.Manual.ToString("X2") + " reads 0x" + (r.Manual < 0 ? "??" : r.Manual.ToString("X2")) + ", which is not a fan-control state";
                return false;
            }
            // A temperature register that reads exactly zero is one this board does not populate, not one that
            // is lying: 878A, the board this route exists for, has its fans at the map's addresses and nothing at
            // 0x57. Rejecting the map over it left both 878A owners with no fan control. An absent temperature
            // takes nothing away from the fan registers; it only means the tachometers have to carry the proof
            // on their own, so that check stops being optional below.
            byte cpuReg = map != null ? map.CpuTemp : (byte)0x57;
            bool tempAbsent = r.Cpu == 0;
            if (!tempAbsent) {
                if (r.Cpu < 20 || r.Cpu > 110) { why = "0x" + cpuReg.ToString("X2") + " reads " + r.Cpu + ", which is not a temperature"; return false; }
                if (!double.IsNaN(dieTemp) && Math.Abs(r.Cpu - dieTemp) > 25) {
                    why = "it reads " + r.Cpu + " where the CPU itself reads " + dieTemp.ToString("0");
                    return false;
                }
            }
            if (r.Rpm1 < 0 || r.Rpm1 > 9000 || r.Rpm2 < 0 || r.Rpm2 > 9000) { why = "fan speeds of " + r.Rpm1 + " and " + r.Rpm2 + " are not rpm"; return false; }

            bool hasF1 = mailboxRpm != null && mailboxRpm.Length > 0 && mailboxRpm[0] >= 0;
            bool hasF2 = mailboxRpm != null && mailboxRpm.Length > 1 && mailboxRpm[1] >= 0;

            if (tempAbsent) {
                if (!hasF1 && !hasF2) {
                    why = "no temperature at 0x" + cpuReg.ToString("X2") + " and no firmware fan speed to check the tachometers against";
                    return false;
                }
                // Stopped fans agree at zero, but zero bytes exist across arbitrary unmapped EC memory. An absent
                // temperature sensor cannot accept stopped fans as proof of the map; at least one spinning fan is
                // required so the tachometer registers are verified against active physical motion.
                bool anySpinning = (hasF1 && mailboxRpm[0] > 0) || (hasF2 && mailboxRpm[1] > 0);
                if (!anySpinning) {
                    why = "no temperature at 0x" + cpuReg.ToString("X2") + " and stopped fans cannot prove tachometer registers";
                    return false;
                }
            }

            if (hasF1) {
                int want1 = mailboxRpm[0] * 100, slack1 = Math.Max(500, want1 / 4);
                if (Math.Abs(r.Rpm1 - want1) > slack1) { why = "fan 1 reads " + r.Rpm1 + " rpm where the firmware reads " + want1; return false; }
            }
            if (hasF2) {
                int want2 = mailboxRpm[1] * 100, slack2 = Math.Max(500, want2 / 4);
                if (Math.Abs(r.Rpm2 - want2) > slack2) { why = "fan 2 reads " + r.Rpm2 + " rpm where the firmware reads " + want2; return false; }
            }

            why = (tempAbsent ? "no temperature register" : "CPU " + r.Cpu + " C") + ", fans " + r.Rpm1 + "/" + r.Rpm2 + " rpm, control 0x" + r.Manual.ToString("X2");
            return true;
        }

        public void Dispose() {
            try { var d = ports as IDisposable; if (d != null) d.Dispose(); } catch { }
            try { if (mutex != null) mutex.Close(); } catch { }
        }
    }

    /// <summary>A pretend EC for the preview build: registers in an array, the handshake honoured.</summary>
    public sealed class DemoEcPorts : IEcPorts {
        readonly byte[] ram = new byte[256];
        readonly Random rnd = new Random();
        byte status;                  // OBF/IBF as a real controller would show them
        int phase;                    // 0 idle, 1 got read cmd, 2 got write cmd, 3 write cmd + address
        byte address, output;
        DateTime lastTick = DateTime.Now;
        public DemoEcPorts() {
            ram[0x57] = 48; ram[0xB7] = 41;
            SetRpm(0xB0, 2650); SetRpm(0xB2, 2480);
            ram[0x63] = 0x78; ram[0x95] = 0x30; ram[0x96] = 100;
        }
        void SetRpm(int at, int rpm) { ram[at] = (byte)(rpm & 0xFF); ram[at + 1] = (byte)(rpm >> 8); }
        void Tick() {
            if ((DateTime.Now - lastTick).TotalSeconds < 1) return;
            lastTick = DateTime.Now;
            ram[0x57] = (byte)Math.Max(36, Math.Min(90, ram[0x57] + rnd.Next(-1, 2)));
            if (ram[0x63] > 0) ram[0x63]--;
            if (ram[0x62] == 0x06) { SetRpm(0xB0, ram[0x34] * 100); SetRpm(0xB2, ram[0x35] * 100); }
        }
        public int In(byte port, out byte value) {
            Tick();
            if (port == 0x66) { value = status; return 0; }
            value = output; status &= unchecked((byte)~0x01);
            return 0;
        }
        public int Out(byte port, byte value) {
            if (port == 0x66) { phase = value == 0x80 ? 1 : value == 0x81 ? 2 : 0; return 0; }
            switch (phase) {
                case 1: output = ram[value]; status |= 0x01; phase = 0; break;
                case 2: address = value; phase = 3; break;
                case 3: ram[address] = value; phase = 0; break;
            }
            return 0;
        }
    }
}
