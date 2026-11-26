// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ohman {
    sealed class BoostKeys : IDisposable {
        const int LeftWin = 0x5B, RightWin = 0x5C, G = 0x47, Alt = 0x12;
        readonly Booster booster;
        readonly KeyboardProc callback;
        readonly ForegroundProc foregroundChanged;
        readonly bool[] swallowed = new bool[256];
        IntPtr hook, foregroundHook, overlayWindow;
        int overlayPid, previousGamePid;
        bool attempted, foregroundAttempted;
        bool leftWinHeld, rightWinHeld;
        BoostOptions observed, reported;
        public bool Failed { get { return attempted && hook == IntPtr.Zero; } }

        public BoostKeys(Booster booster) {
            this.booster = booster;
            callback = OnKey;
            foregroundChanged = OnForegroundChanged;
        }

        public void Update() {
            var filter = booster.Input;
            bool wanted = filter.Options != BoostOptions.None && filter.Pids.Length != 0;
            if (wanted && hook == IntPtr.Zero && !attempted) {
                attempted = true;
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) Log.Write("booster: key filter: " + new Win32Exception().Message);
            }
            if (wanted && (filter.Options & BoostOptions.GameBar) != 0) {
                if (!foregroundAttempted) {
                    foregroundAttempted = true;
                    const uint ForegroundEvent = 3;
                    foregroundHook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, foregroundChanged, 0, 0, 0);
                    if (foregroundHook == IntPtr.Zero) Log.Write("booster: Game Bar foreground tracking unavailable");
                    else OnForegroundChanged(foregroundHook, ForegroundEvent, GetForegroundWindow(), 0, 0, 0, 0);
                }
            } else ResetForegroundTracking();
            var newlyObserved = observed & ~reported;
            if ((newlyObserved & BoostOptions.WindowsKey) != 0) Log.Write("booster: suppressed the Windows key during a selected game boost");
            if ((newlyObserved & BoostOptions.GameBar) != 0) Log.Write("booster: suppressed a Game Bar shortcut in a selected game");
            reported |= newlyObserved;
            if (!wanted && Array.IndexOf(swallowed, true) < 0) Dispose();
        }

        void OnForegroundChanged(IntPtr eventHook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint eventTime) {
            var filter = booster.Input;
            if (eventHook != foregroundHook || (filter.Options & BoostOptions.GameBar) == 0) return;
            int pid;
            GetWindowThreadProcessId(window, out pid);
            if (Array.IndexOf(filter.Pids, pid) >= 0) {
                previousGamePid = pid;
                overlayWindow = IntPtr.Zero;
            } else if (Array.IndexOf(filter.Pids, previousGamePid) >= 0 && BoostGameBar.IsOverlayProcess(pid)) {
                overlayWindow = window;
                overlayPid = pid;
                booster.CheckGameBarSoon();
            } else {
                previousGamePid = 0;
                overlayWindow = IntPtr.Zero;
            }
        }

        IntPtr OnKey(int code, IntPtr message, IntPtr data) {
            if (code == 0) {
                int key = Marshal.ReadInt32(data);
                int msg = message.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                if (key >= 0 && key < swallowed.Length && (down || up)) {
                    // A Windows key swallowed by our hook never reaches the system key state. Track its
                    // transitions here so Game Bar detection also works with Windows-key blocking enabled.
                    if (key == LeftWin) leftWinHeld = down;
                    if (key == RightWin) rightWinHeld = down;
                    if (swallowed[key]) { if (up) swallowed[key] = false; return new IntPtr(1); }
                    if (down) {
                        var filter = booster.Input;
                        bool win = key == LeftWin || key == RightWin;
                        bool gameBar = key == G || (key == 0x52 || key == 0x42 || key == 0x4D || key == 0x54 || key == 0x2C) && IsDown(Alt);
                        if ((win && (filter.Options & BoostOptions.WindowsKey) != 0) || (gameBar && (filter.Options & BoostOptions.GameBar) != 0
                            && (leftWinHeld || rightWinHeld || IsDown(LeftWin) || IsDown(RightWin)))) {
                            int foreground;
                            IntPtr window = GetForegroundWindow();
                            GetWindowThreadProcessId(window, out foreground);
                            bool overlayOverGame = win && window != IntPtr.Zero && window == overlayWindow && foreground == overlayPid
                                && (filter.Options & BoostOptions.GameBar) != 0 && Array.IndexOf(filter.Pids, previousGamePid) >= 0;
                            if (Array.IndexOf(filter.Pids, foreground) >= 0 || overlayOverGame) {
                                observed |= win ? BoostOptions.WindowsKey : BoostOptions.GameBar;
                                swallowed[key] = true;
                                return new IntPtr(1);
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

        public void Dispose() {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            ResetForegroundTracking();
            attempted = false;
            leftWinHeld = rightWinHeld = false;
            observed = reported = BoostOptions.None;
            Array.Clear(swallowed, 0, swallowed.Length);
        }

        void ResetForegroundTracking() {
            if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
            foregroundAttempted = false;
            overlayWindow = IntPtr.Zero;
            previousGamePid = overlayPid = 0;
        }

        delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
        delegate void ForegroundProc(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, ForegroundProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, KeyboardProc handler, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    }
}
