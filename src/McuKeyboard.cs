// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: per-key lighting on HP's Primax keyboards (OMEN 17 "Cybug" 0461:4E9A, OMEN 16 "Ralph" 0461:4E9B), 2021-2024.
//
// These keyboards have no HID LampArray, so Windows Dynamic Lighting cannot drive them and neither could we: the
// BIOS answers the four-zone calls (keyboard type 3) and lights nothing. What does light them is HP's own MCU
// protocol on the keyboard's vendor interface (mi_02, usage page FF13), the same one OMEN Gaming Hub speaks through
// McuSDK2 and the same one documented for the 2025 Darfon keyboard in docs/research.md 7a. Only the LED count and
// the index map differ. The wire format, as read from OGH's assemblies and confirmed on an OMEN 17-ck2013nl (board
// 8BAD, issue #20, 2026-09-22):
//
//   65-byte output report, report id 0 (HidP value caps say 0), written with WriteFile; replies come back as 65-byte
//   input reports on the same handle. Packet = [cmd][index][len lo][len hi][60 bytes]. A SET is acked with EC AC at
//   data bytes 4..5 (EC FA = refused); a GET answers with its data from byte 4. Unsolicited key events carry EC BD
//   there and are skipped. Reply bytes 2..3 are the reply length and a status byte (0x80 on an ack, 0xD0 on a GET).
//
//   0x80/01  device info: 4..7 firmware, 8 device type (1 = keyboard)          GET, sent once on open
//   0x83/00  current effect record                                               GET, logged on open
//   0x09/00  {01} lighting on                                                    before the first map
//   0x05/0x06/0x07 x page 0..2   R, G, B channel, 60 LEDs per page, length 0     the static map, nine packets
//
// What is never sent, and cannot be: 0x0A (store to flash; OGH only sends it on "save") and 0x10 (factory restore,
// firmware update mode). Guard() refuses everything outside the four rows above whatever the caller asks, so no
// later change to this file can reach the flash or the bootloader by accident. Brightness is done by scaling the
// colours (0x0C exists in the SDK but OGH never sends it to these keyboards). Nothing here is persistent: the
// static map lives in the MCU's RAM and a restart brings back whatever the keyboard had in flash.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Ohman {

    /// <summary>One Primax keyboard model: its PID, how many LED slots its map has, and every physical key in reading
    /// order (row by row, left to right) with the slots that light it. Slots not listed are the ones OGH forces to 0
    /// (NbPerKeyRgbLightingModel.SetNullBytes); they are sent as 0 here too.</summary>
    internal sealed class McuBoard {
        public readonly string Name, Model;
        public readonly ushort Pid;
        public readonly int Slots;
        public readonly bool Tested;
        /// <summary>Per key, in reading order: its name, HID usage (0 = none), LED slots, and the key whose colour it
        /// copies when the drawing has no key for it (-1 = itself).</summary>
        public readonly string[] Names;
        public readonly ushort[] Usage;
        public readonly int[][] Leds;
        public readonly int[] Leader;

        // Entry format: "name usage(hex) slots [>leader]". Slots are a comma list or a range a-b.
        // Source: HP.Omen.DraxLightingModule.JSON.PerKey.CybugKBKeysGlobalData.json (LED order "as defined in spec"),
        // null slots from NbPerKeyRgbLightingModel.SetNullBytes (Cybug branch). Verified on 8BAD, Italian keyboard:
        // the whole map lights; 36, 37, 38 = left Shift; 140 = the key right of ']' (ù on Italian, '\' on US);
        // 146, 147, 148 = Enter. The Italian board is ANSI-shaped (wide left Shift, one-row Enter): its extra '<>' key
        // sits in the bottom row between Menu and right Ctrl, where the map has "FNR" (163); not yet lit singly.
        static readonly string[] CybugKeys = {
            "P1 00 0,6 >Esc", "Esc 29 1,7", "F1 3A 2,8", "F2 3B 3,9", "F3 3C 4,10", "F4 3D 5,11", "F5 3E 60,66", "F6 3F 61,67", "F7 40 62,68", "F8 41 63,69", "F9 42 64,70", "F10 43 65,71", "F11 44 120,126", "F12 45 121,127", "Power 66 122,128 >F12", "Del 4C 123,129", "Omen 00 124,130 >Del", "NumPad 00 125,131 >Del", "PrtSc 46 137,143 >Del",
            "P2 00 12 >Tilde", "Tilde 35 13", "1 1E 14", "2 1F 15", "3 20 16", "4 21 17", "5 22 72", "6 23 73", "7 24 74", "8 25 75", "9 26 76", "0 27 77", "Hyphen 2D 132", "Equal 2E 133", "Back 2A 134-136", "Ins 49 150 >Back", "Home 4A 151 >Back", "PageUp 4B 152 >Back",
            "P3 00 18 >Tab", "Tab 2B 19,25,31", "Q 14 20", "W 1A 21", "E 08 22", "R 15 23", "T 17 78", "Y 1C 79", "U 18 80", "I 0C 81", "O 12 82", "P 13 83", "BracketL 2F 138", "BracketR 30 139", "Backslash 31 140", "Pause 48 153 >Backslash", "End 4D 154 >Backslash", "PageDn 4E 155 >Backslash",
            "P4 00 24 >Caps", "Caps 39 26,27", "A 04 28", "S 16 29", "D 07 35", "F 09 84", "G 0A 85", "H 0B 86", "J 0D 87", "K 0E 88", "L 0F 89", "Semicolon 33 144", "Quote 34 145", "Enter 28 146-148",
            "P5 00 30 >ShiftL", "ShiftL E1 36-38", "Z 1D 32", "X 1B 33", "C 06 34", "V 19 90", "B 05 91", "N 11 92", "M 10 93", "Comma 36 94", "Dot 37 95", "Slash 38 156", "ShiftR E5 157-159", "Up 52 160",
            "P6 00 42 >CtrlL", "CtrlL E0 43", "FnL 00 44", "Win E3 45", "AltL E2 46", "Space 2C 96-100", "AltR E6 101", "Menu 65 107 >AltR", "FnR 00 163 >CtrlR", "CtrlR E4 164", "Left 50 165", "Down 51 166 >Up", "Right 4F 167",
        };
        const string CybugNull = "39-41,47-59,102-106,108-119,141-142,149,161-162";

        // Source: RalphKBKeysGlobalData.json, null slots from StarmadeKBLightingModel.SetNullBytes (the branch OGH takes
        // for Ralph). Same commands, interface and handshake as Cybug in OGH's code. UNTESTED on hardware.
        static readonly string[] RalphKeys = {
            "Esc 29 0,6", "F1 3A 1,7", "F2 3B 2,8", "F3 3C 3,9", "F4 3D 4,10", "F5 3E 5,11", "F6 3F 60,66", "F7 40 61,67", "F8 41 62,68", "F9 42 63,69", "F10 43 64,70", "F11 44 65,71", "F12 45 120,126", "Power 66 121,127 >F12", "Del 4C 122,128", "Omen 00 123,129 >Del", "NumPad 00 124,130 >Del", "PrtSc 46 125,131 >Del",
            "Tilde 35 12,18", "1 1E 13", "2 1F 14", "3 20 15", "4 21 16", "5 22 17", "6 23 72", "7 24 73", "8 25 74", "9 26 75", "0 27 76", "Hyphen 2D 77", "Equal 2E 132", "Back 2A 133-135", "Ins 49 138,139 >Back", "Home 4A 140,141 >Back", "PageUp 4B 142,143 >Back",
            "Tab 2B 24,25", "Q 14 19", "W 1A 20", "E 08 21", "R 15 22", "T 17 23", "Y 1C 78", "U 18 79", "I 0C 80", "O 12 81", "P 13 82", "BracketL 2F 83", "BracketR 30 150", "Backslash 31 136,137", "Pause 48 144,145 >Backslash", "End 4D 146,147 >Backslash", "PageDn 4E 148,149 >Backslash",
            "Caps 39 30-32", "A 04 26", "S 16 27", "D 07 28", "F 09 29", "G 0A 84", "H 0B 85", "J 0D 86", "K 0E 87", "L 0F 88", "Semicolon 33 89", "Quote 34 151", "Enter 28 152-154",
            "ShiftL E1 36-38", "Z 1D 33", "X 1B 34", "C 06 35", "V 19 41", "B 05 90", "N 11 91", "M 10 92", "Comma 36 93", "Dot 37 94", "Slash 38 95", "ShiftR E5 156-158", "Up 52 159",
            "CtrlL E0 42", "FnL 00 43", "Win E3 44", "AltL E2 45", "Space 2C 96-101", "AltR E6 102", "Menu 65 103 >AltR", "FnR 00 162 >CtrlR", "CtrlR E4 163", "Left 50 164", "Down 51 165 >Up", "Right 4F 166",
        };
        const string RalphNull = "39-40,46-59,104-119,155,160-161";

        static McuBoard cybug, ralph;

        /// <summary>The board for a Primax PID, or null. A table that fails its own checks is never used.</summary>
        public static McuBoard For(ushort pid) {
            try {
                if (pid == 0x4E9A) { if (cybug == null) cybug = new McuBoard("Cybug", "OMEN 17", 0x4E9A, 168, true, CybugKeys, CybugNull); return cybug; }
                if (pid == 0x4E9B) { if (ralph == null) ralph = new McuBoard("Ralph", "OMEN 16", 0x4E9B, 167, false, RalphKeys, RalphNull); return ralph; }
            } catch (Exception ex) { Log.Write("mcu keyboard table " + pid.ToString("X4") + ": " + ex.Message); }
            return null;
        }

        McuBoard(string name, string model, ushort pid, int slots, bool tested, string[] keys, string nulls) {
            Name = name; Model = model; Pid = pid; Slots = slots; Tested = tested;
            int n = keys.Length;
            Names = new string[n]; Usage = new ushort[n]; Leds = new int[n][]; Leader = new int[n];
            var leaderName = new string[n];
            var owner = new int[slots];
            for (int i = 0; i < slots; i++) owner[i] = -1;
            foreach (int s in Ranges(nulls)) owner[s] = -2;
            for (int k = 0; k < n; k++) {
                string[] p = keys[k].Split(' ');
                if (p.Length < 3) throw new FormatException("bad entry '" + keys[k] + "'");
                Names[k] = p[0];
                Usage[k] = Convert.ToUInt16(p[1], 16);
                Leds[k] = Ranges(p[2]).ToArray();
                leaderName[k] = p.Length > 3 && p[3].StartsWith(">") ? p[3].Substring(1) : null;
                foreach (int s in Leds[k]) {
                    if (s < 0 || s >= slots) throw new FormatException(p[0] + ": slot " + s + " out of range");
                    if (owner[s] == -2) throw new FormatException(p[0] + ": slot " + s + " is one OGH blanks");
                    if (owner[s] >= 0) throw new FormatException(p[0] + ": slot " + s + " already belongs to " + Names[owner[s]]);
                    owner[s] = k;
                }
            }
            for (int k = 0; k < n; k++) {
                Leader[k] = -1;
                if (leaderName[k] == null) continue;
                Leader[k] = Array.IndexOf(Names, leaderName[k]);
                if (Leader[k] < 0) throw new FormatException(Names[k] + ": no key called " + leaderName[k]);
            }
        }

        static List<int> Ranges(string s) {
            var r = new List<int>();
            foreach (string part in s.Split(',')) {
                int dash = part.IndexOf('-');
                if (dash < 0) { r.Add(int.Parse(part)); continue; }
                int a = int.Parse(part.Substring(0, dash)), b = int.Parse(part.Substring(dash + 1));
                for (int i = a; i <= b; i++) r.Add(i);
            }
            return r;
        }

        /// <summary>The key a drawn key is: by its HID usage, and by label for Fn, which has none. -1 when this board
        /// has no such key.</summary>
        public int KeyFor(KeyDef k) {
            if (k.Usage != 0) { for (int i = 0; i < Usage.Length; i++) if (Usage[i] == k.Usage) return i; return -1; }
            if (k.Label == "Fn") return Array.IndexOf(Names, "FnL");
            return -1;
        }
    }

    /// <summary>The transport: one open handle on the keyboard's mi_02 interface, and the exchange of one packet for
    /// one reply. Every batch holds OGH's own named mutex so our packets never interleave with a running OGH's.</summary>
    internal sealed class McuKeyboard : IDisposable {
        public const ushort Vid = 0x0461;
        const int ReportLen = 65;                  // report id + 64 data bytes, both directions (HidP_GetCaps on 4E9A)
        const byte ReportId = 0;                   // HidP value caps on 4E9A mi_02: input and output report id 0
        const int WriteTimeoutMs = 1000, ReadTimeoutMs = 1000, MutexWaitMs = 1000;

        public readonly McuBoard Board;
        public readonly string Path;
        public string Info = "";                   // what the device said about itself, for the log

        SafeFileHandle handle;
        FileStream fs;
        Task<int> pending;                         // at most one read outstanding; a late reply is not left to a later read
        byte[] pendingBuf;
        Mutex mutex;
        bool mutexFailed, mutexBusyLogged;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

        McuKeyboard(string path, McuBoard board) { Path = path; Board = board; }

        /// <summary>The Primax keyboard's MCU interface, opened and answering as a keyboard, or null. OGH matches on
        /// VID, PID and "mi_02" in the path; the report lengths are checked as well so nothing else is ever written.
        /// Sends one GET (device info, what OGH sends on every open) and one more GET for the log. Changes nothing.</summary>
        public static McuKeyboard Find() {
            foreach (var info in Hid.Enumerate()) {
                if (info.VendorId != Vid) continue;
                var board = McuBoard.For(info.ProductId);
                if (board == null) continue;
                if ((info.Path ?? "").IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (info.OutputLen != ReportLen || info.InputLen != ReportLen) {
                    Log.Write("mcu keyboard: " + info + " is mi_02 but not 65/65-byte reports; not touched");
                    continue;
                }
                var k = new McuKeyboard(info.Path, board);
                try {
                    if (k.Open() && k.Handshake()) return k;
                } catch (Exception ex) { Log.Write("mcu keyboard " + info + ": " + ex.Message); }
                k.Dispose();
            }
            return null;
        }

        /// <summary>Reopen after the handle went bad (a resume or a USB reset re-enumerates the keyboard).</summary>
        public bool Reopen() {
            Close();
            try {
                foreach (var info in Hid.Enumerate())
                    if (info.VendorId == Vid && info.ProductId == Board.Pid && (info.Path ?? "").IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0
                        && info.OutputLen == ReportLen && info.InputLen == ReportLen)
                        return OpenPath(info.Path);
            } catch (Exception ex) { Log.Write("mcu keyboard reopen: " + ex.Message); }
            return false;
        }

        bool Open() { return OpenPath(Path); }
        bool OpenPath(string path) {
            // read/write, shared (OGH opens it the same way), overlapped so every wait has a timeout
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) { Log.Write("mcu keyboard: cannot open (error " + Marshal.GetLastWin32Error() + ")"); handle = null; return false; }
            fs = new FileStream(handle, FileAccess.ReadWrite, ReportLen, true);
            return true;
        }

        bool Handshake() {
            byte[] d = null;
            Batch(delegate { d = Exchange(0x80, 0x01, null, 0); });
            if (d[4] == 0xEC && d[5] == 0xFA) { Log.Write("mcu keyboard: device info refused (EC FA)"); return false; }
            if (d[8] != 0x01) { Log.Write("mcu keyboard: device type " + d[8] + ", not a keyboard; not touched"); return false; }
            Info = "0461:" + Board.Pid.ToString("X4") + " (" + Board.Name + ", " + Board.Model + (Board.Tested ? "" : ", UNTESTED") + ") firmware "
                + d[4].ToString("X2") + "." + d[5].ToString("X2") + "." + d[6].ToString("X2") + "." + d[7].ToString("X2")
                + " language 0x" + d[9].ToString("X2") + " profile " + d[10] + " effect 0x" + d[11].ToString("X2") + " brightness " + d[12];
            try {
                byte[] e = null;
                Batch(delegate { e = Exchange(0x83, 0x00, null, 0); });
                int len = Math.Min(60, (int)e[2]);
                Info += "; stored effect record (" + len + " bytes): " + BitConverter.ToString(e, 4, len).Replace("-", " ");
            } catch (Exception ex) { Info += "; effect record: " + ex.Message; }
            return true;
        }

        /// <summary>The only packets this class can send. 0x0A (write the current lighting to flash) and 0x10 (factory
        /// restore / firmware update mode) are named here so nobody has to wonder: they are refused like everything
        /// else that is not on the list, whatever index or payload comes with them.</summary>
        static void Guard(byte cmd, byte index, byte[] data, int len) {
            if (cmd == 0x0A || cmd == 0x10) throw new InvalidOperationException("MCU command 0x" + cmd.ToString("X2") + " is never sent");
            bool ok = (cmd == 0x80 && (index == 0x01 || index == 0x02) && len == 0)
                   || (cmd == 0x83 && index == 0x00 && len == 0)
                   || (cmd == 0x09 && index == 0x00 && len == 1 && data != null && data.Length == 1 && data[0] <= 1)
                   || ((cmd == 0x05 || cmd == 0x06 || cmd == 0x07) && index <= 2 && len == 0);
            if (!ok) throw new InvalidOperationException("MCU command 0x" + cmd.ToString("X2") + "/" + index.ToString("X2") + " is not on the allow-list");
        }

        /// <summary>One write, one matching reply (64 data bytes, report id stripped). Key events (EC BD) and replies
        /// to anything else are skipped. Throws on a timeout; the caller closes the handle, so nothing late can be
        /// mistaken for the next command's answer.</summary>
        byte[] Exchange(byte cmd, byte index, byte[] data, int len) {
            Guard(cmd, index, data, len);
            if (fs == null) throw new IOException("keyboard not open");
            var b = new byte[ReportLen];
            b[0] = ReportId; b[1] = cmd; b[2] = index; b[3] = (byte)(len & 0xFF); b[4] = (byte)(len >> 8);
            if (data != null) Array.Copy(data, 0, b, 5, Math.Min(60, data.Length));
            try { if (!fs.WriteAsync(b, 0, ReportLen).Wait(WriteTimeoutMs)) throw new IOException("write 0x" + cmd.ToString("X2") + " timed out"); }
            catch (AggregateException ex) { throw new IOException("write 0x" + cmd.ToString("X2") + ": " + ex.InnerException.Message); }
            var clock = Stopwatch.StartNew();
            for (; ; ) {
                if (pending == null) { pendingBuf = new byte[ReportLen]; pending = fs.ReadAsync(pendingBuf, 0, ReportLen); }
                int left = ReadTimeoutMs - (int)clock.ElapsedMilliseconds;
                try { if (left <= 0 || !pending.Wait(left)) throw new IOException("no reply to 0x" + cmd.ToString("X2") + "/" + index.ToString("X2")); }
                catch (AggregateException ex) { pending = null; throw new IOException("read: " + ex.InnerException.Message); }
                int n = pending.Result;
                var r = pendingBuf;
                pending = null;
                if (n < 7) continue;
                if (r[5] == 0xEC && r[6] == 0xBD) continue;          // a key event (OMEN key, Fn toggles), not our answer
                if (r[1] != cmd || r[2] != index) continue;          // an answer to something else
                var d = new byte[64];
                Array.Copy(r, 1, d, 0, 64);
                return d;
            }
        }

        /// <summary>A SET, which must come back acked.</summary>
        void Set(byte cmd, byte index, byte[] data, int len) {
            var d = Exchange(cmd, index, data, len);
            if (d[4] == 0xEC && d[5] == 0xAC) return;
            if (d[4] == 0xEC && d[5] == 0xFA) throw new IOException("keyboard refused 0x" + cmd.ToString("X2") + "/" + index.ToString("X2") + " (EC FA)");
            throw new IOException("no ack for 0x" + cmd.ToString("X2") + "/" + index.ToString("X2") + ": " + d[4].ToString("X2") + " " + d[5].ToString("X2"));
        }

        /// <summary>Run a group of exchanges under OGH's mutex. If the mutex cannot be had (an OGH that holds it for
        /// good, or one we are not allowed to open) we go ahead without it and say so once: Ohman closes OGH at start
        /// on supported boards, and a frame that loses a race is repainted by the next one.</summary>
        void Batch(Action body) {
            if (mutex == null && !mutexFailed) {
                try { mutex = new Mutex(false, "omen_device_vid[0461]_pid[" + Board.Pid.ToString("x4") + "]_interface_string[mi_02]"); }
                catch (Exception ex) { mutexFailed = true; Log.Write("mcu keyboard: OGH's mutex unavailable (" + ex.Message + "); going without it"); }
            }
            bool held = false;
            if (mutex != null) {
                try { held = mutex.WaitOne(MutexWaitMs); }
                catch (AbandonedMutexException) { held = true; }      // an OGH that died holding it; ours now
                if (!held && !mutexBusyLogged) { mutexBusyLogged = true; Log.Write("mcu keyboard: OGH's mutex busy for " + MutexWaitMs + " ms; sending anyway"); }
            }
            try { body(); }
            finally { if (held) mutex.ReleaseMutex(); }
        }

        /// <summary>The static map, R then G then B, three 60-LED pages each (OGH's order). Arrays are Board.Slots
        /// long; the last page is short and the rest of its payload stays 0. lightingOn sends 09 00 {01} first.</summary>
        public void WriteMap(byte[] r, byte[] g, byte[] b, bool lightingOn) {
            Batch(delegate {
                if (lightingOn) Set(0x09, 0x00, new byte[] { 1 }, 1);
                byte[][] ch = { r, g, b };
                for (int c = 0; c < 3; c++)
                    for (int page = 0; page < 3; page++) {
                        var p = new byte[60];
                        int from = page * 60, n = Math.Max(0, Math.Min(60, Board.Slots - from));
                        Array.Copy(ch[c], from, p, 0, n);
                        Set((byte)(0x05 + c), (byte)page, p, 0);
                    }
            });
        }

        void Close() {
            pending = null;
            if (fs != null) { try { fs.Dispose(); } catch { } fs = null; }
            if (handle != null) { try { handle.Dispose(); } catch { } handle = null; }
        }

        public void Dispose() {
            Close();
            if (mutex != null) { try { mutex.Dispose(); } catch { } mutex = null; }
        }
    }

    /// <summary>A Primax per-key keyboard as Ohman's lighting device. One zone per physical key, in reading order, so
    /// the effects that walk the zones (wave) walk the board the way it is read rather than in LED-chain order. Keys
    /// the drawing has no key for (P1-P6, Power, OMEN, the numpad key, PrtSc, Ins..PgDn, Menu, the right Fn or the
    /// ISO '&lt;&gt;' in its place, the down arrow) copy a neighbour that is drawn; wide keys light all their LEDs.
    /// There is no colour readback on this protocol: what we last wrote is all we know.</summary>
    public sealed class McuKeyboardLighting : ILighting, IDisposable {
        const int MinFrameMs = 100;             // at most 10 maps a second: nine acked packets each
        const int LightingOnEveryMs = 2000;     // re-send 09 {01} before a map that is not part of a running effect

        readonly object sync = new object();
        readonly McuKeyboard dev;
        readonly McuBoard board;
        readonly Rgb[] shown;
        readonly int[] leader;                  // per key: the drawn key whose colour it copies, -1 for itself
        int backlight = 0x80 | 100;
        bool open = true, lightingOn;
        readonly Stopwatch sinceSend = new Stopwatch();
        byte[] lastR, lastG, lastB;
        bool timed;

        McuKeyboardLighting(McuKeyboard k) {
            dev = k;
            board = k.Board;
            shown = new Rgb[board.Names.Length];          // black: nothing is claimed about a map we cannot read
            leader = new int[board.Names.Length];
            for (int i = 0; i < leader.Length; i++) leader[i] = -1;
            try {
                var keys = KeyboardLayouts.Build(Numpad, Zones);
                var drawn = new bool[leader.Length];
                foreach (var kd in keys) { int z = board.KeyFor(kd); if (z >= 0) drawn[z] = true; }
                for (int i = 0; i < leader.Length; i++)
                    if (!drawn[i] && board.Leader[i] >= 0 && drawn[board.Leader[i]]) leader[i] = board.Leader[i];
            } catch (Exception ex) { Log.Write("mcu keyboard followers: " + ex.Message); }
        }

        /// <summary>The Primax keyboard if this machine has one that answers, else null. Only asks (two GETs).</summary>
        public static McuKeyboardLighting Detect() {
            try {
                var k = McuKeyboard.Find();
                if (k == null) return null;
                Log.Write("mcu keyboard: " + k.Info);
                return new McuKeyboardLighting(k);
            } catch (Exception ex) { Log.Write("mcu keyboard detect: " + ex.Message); return null; }
        }

        public LightKind Kind { get { return LightKind.PerKey; } }
        public bool Inert { get { return false; } }
        public int Zones { get { return board.Names.Length; } }
        public bool Numpad { get { return false; } }                 // neither Cybug nor Ralph has one
        public string Describe { get { return board.Names.Length + " keys"; } }

        /// <summary>Point each drawn key at its key here; Zone is then this class's key index. A drawn key this board
        /// does not have (none on the current drawing) is logged and given the first key rather than an invalid zone.</summary>
        public void Bind(List<KeyDef> keys) {
            var missing = new List<string>();
            foreach (var k in keys) {
                int z = board.KeyFor(k);
                if (z < 0) { missing.Add(k.Label); z = 0; }
                k.Zone = z;
            }
            if (missing.Count > 0) Log.Write("mcu keyboard: drawn keys with no LED on " + board.Name + ": " + string.Join(" ", missing.ToArray()));
        }

        public Rgb[] GetColors() { lock (sync) return (Rgb[])shown.Clone(); }

        // The engine hands a frame over and returns. The keyboard is written on this class's own thread, because one
        // map is nine acked packets with a 1 s timeout each, and a keyboard that stops answering (a resume, a USB
        // reset, OGH holding it) would otherwise hold the engine's lock, the one every fan and thermal-guard write
        // needs, for seconds per frame. Only the newest frame is sent; frames in between are simply skipped.
        readonly object io = new object();             // one conversation with the device at a time
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread writer;
        volatile bool closing;
        bool releaseWanted;
        int failures;
        DateTime retryAfter = DateTime.MinValue;

        public void SetColors(Rgb[] c) {
            if (c == null) return;
            lock (sync) {
                for (int i = 0; i < shown.Length && i < c.Length; i++) shown[i] = c[i];
                releaseWanted = false;
            }
            Kick();
        }

        public int GetBacklight() { return backlight; }
        public void SetBacklight(bool on, int level) {
            lock (sync) {
                backlight = Math.Max(0, Math.Min(100, level)) | (on ? 0x80 : 0);
                releaseWanted = false;
            }
            Kick();
        }

        void Kick() {
            lock (sync) {
                if (closing) return;
                if (writer == null) { writer = new Thread(WriteLoop) { IsBackground = true, Name = "mcu keyboard" }; writer.Start(); }
            }
            wake.Set();
        }

        /// <summary>Stop talking to the keyboard. The colours stay (the MCU keeps rendering its current map, as it
        /// does when OGH exits) until a restart reloads what the keyboard has in flash. Putting the stored effect
        /// back is not attempted: the record 0x83 returns (24 bytes on 4E9A) is not the 36-byte 0x03 layout OGH
        /// writes, and re-sending a guess is not worth it. The next paint reopens. Done on the writer thread when
        /// there is one, so a caller holding the engine's lock never waits on the device.</summary>
        public void Release() {
            bool now;
            lock (sync) { now = writer == null; if (!now) releaseWanted = true; }
            if (now) { lock (io) CloseDevice(); }
            else wake.Set();
        }

        public void Dispose() {
            closing = true;
            wake.Set();
            Thread w; lock (sync) w = writer;
            if (w != null) w.Join(2500);
            if (Monitor.TryEnter(io, 2000)) { try { CloseDevice(); } finally { Monitor.Exit(io); } }
        }

        void CloseDevice() {
            if (!open) return;
            dev.Dispose();
            open = false;
            lightingOn = false;
            lastR = null;
        }

        void WriteLoop() {
            for (; ; ) {
                wake.WaitOne();
                if (closing) return;
                bool release;
                lock (sync) { release = releaseWanted; releaseWanted = false; }
                if (release) { lock (io) CloseDevice(); continue; }
                // after repeated failures, wait a little rather than hammer a keyboard that is not there
                TimeSpan wait = retryAfter - DateTime.Now;
                if (wait > TimeSpan.Zero) { Thread.Sleep(wait); if (closing) return; }
                byte[] r, g, b; bool on;
                lock (sync) { Build(out r, out g, out b, out on); }
                lock (io) {
                    if (closing) return;
                    try { Send(r, g, b, on); failures = 0; retryAfter = DateTime.MinValue; }
                    catch (Exception ex) {
                        failures++;
                        if (failures == 1 || failures % 20 == 0) Log.Write("mcu keyboard: " + ex.Message + (failures > 1 ? " (" + failures + " in a row)" : ""));
                        if (failures >= 3) retryAfter = DateTime.Now.AddSeconds(5);
                    }
                }
            }
        }

        void Build(out byte[] r, out byte[] g, out byte[] b, out bool on) {
            on = (backlight & 0x80) != 0;
            double f = on ? (backlight & 0x7F) / 100.0 : 0;
            r = new byte[board.Slots];
            g = new byte[board.Slots];
            b = new byte[board.Slots];
            for (int k = 0; k < shown.Length; k++) {
                var c = shown[leader[k] >= 0 ? leader[k] : k];
                if (f < 1) c = c.Scale(f);
                foreach (int sl in board.Leds[k]) { r[sl] = c.R; g[sl] = c.G; b[sl] = c.B; }
            }
        }

        /// <summary>One map, on the writer thread only (io held).</summary>
        void Send(byte[] r, byte[] g, byte[] b, bool on) {
            // the engine sets colours and then the backlight in one apply; the second map is the same one
            long since = sinceSend.IsRunning ? sinceSend.ElapsedMilliseconds : long.MaxValue;
            if (lastR != null && since < 1000 && Same(r, lastR) && Same(g, lastG) && Same(b, lastB)) return;
            if (since < MinFrameMs) Thread.Sleep((int)(MinFrameMs - since));
            bool sendOn = on && (!lightingOn || since >= LightingOnEveryMs);
            var clock = Stopwatch.StartNew();
            try {
                if (!open) { if (!dev.Reopen()) throw new IOException("keyboard not found"); open = true; sendOn = on; }
                dev.WriteMap(r, g, b, sendOn);
            } catch (IOException first) {
                // A resume or a USB reset leaves the old handle dead. One reopen and one retry, then give up; the
                // writer backs off and tries again with the next frame.
                Log.Write("mcu keyboard: " + first.Message + "; reopening");
                lightingOn = false;
                lastR = null;
                if (!dev.Reopen()) { open = false; throw new IOException("keyboard lighting: " + first.Message); }
                open = true;
                dev.WriteMap(r, g, b, on);
                sendOn = on;
            }
            if (sendOn) lightingOn = true;
            lastR = r; lastG = g; lastB = b;
            sinceSend.Restart();
            if (!timed) { timed = true; Log.Write("mcu keyboard: first map sent and acked in " + clock.ElapsedMilliseconds + " ms"); }
        }

        static bool Same(byte[] a, byte[] b) {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
