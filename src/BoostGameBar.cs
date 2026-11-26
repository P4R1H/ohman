// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ohman {
    static class BoostGameBar {
        const string PackageFamily = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";
        const uint Terminate = 0x0001, QueryLimitedInformation = 0x1000, Synchronize = 0x00100000;
        const uint WaitSignaled = 0, WaitTimeout = 258;
        const int NoPackage = 15700, InvalidParameter = 87;
        static readonly string[] ProcessNames = { "GameBar", "GameBarFTServer", "XboxGameBarWidgets", "GameBarElevatedFT" };

        public static bool IsOverlayProcess(int pid) {
            if (pid == 0) return false;
            IntPtr process = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
            if (process == IntPtr.Zero) return false;
            try { return MatchesOverlay(process, null); }
            catch (Win32Exception) { return false; }
            finally { CloseHandle(process); }
        }

        public static string StopInSession(int sessionId) {
            string problem = null;
            foreach (string name in ProcessNames) {
                foreach (var candidate in Process.GetProcessesByName(name)) {
                    using (candidate) {
                        try {
                            if (candidate.SessionId == sessionId && !StopIfVerified(candidate.Id, sessionId, name))
                                problem = problem ?? name + " is still exiting";
                        }
                        catch (InvalidOperationException) { /* The candidate exited during discovery. */ }
                        catch (Win32Exception ex) { problem = problem ?? name + ": " + ex.Message; }
                    }
                }
            }
            return problem;
        }

        static bool StopIfVerified(int pid, int sessionId, string name) {
            IntPtr process = OpenProcess(Terminate | QueryLimitedInformation | Synchronize, false, pid);
            if (process == IntPtr.Zero) {
                int error = Marshal.GetLastWin32Error();
                if (error == InvalidParameter) return true; // The PID no longer exists.
                throw new Win32Exception(error);
            }
            try {
                if (HasExited(process, 0)) return true;
                int actualSession;
                if (!ProcessIdToSessionId(pid, out actualSession)) {
                    int error = Marshal.GetLastWin32Error();
                    if (HasExited(process, 0)) return true;
                    throw new Win32Exception(error);
                }
                if (actualSession != sessionId) return true;

                if (!MatchesOverlay(process, name)) return true;

                // Verify identity, terminate, and observe exit through the same open handle.
                if (!TerminateProcess(process, 0)) {
                    int error = Marshal.GetLastWin32Error();
                    if (HasExited(process, 0)) return true;
                    throw new Win32Exception(error);
                }
                Log.Write("booster: Xbox Game Bar stop requested for " + name + " (PID " + pid + ")");
                if (!HasExited(process, 100)) return false;
                Log.Write("booster: verified Xbox Game Bar process exited (" + name + ", PID " + pid + ")");
                return true;
            } finally { CloseHandle(process); }
        }

        static bool MatchesOverlay(IntPtr process, string name) {
            var family = new StringBuilder(128);
            int length = family.Capacity;
            int result = GetPackageFamilyName(process, ref length, family);
            if (result == NoPackage || HasExited(process, 0)) return false;
            if (result != 0) throw new Win32Exception(result);
            if (!string.Equals(family.ToString(), PackageFamily, StringComparison.OrdinalIgnoreCase)) return false;

            var image = new StringBuilder(1024);
            length = image.Capacity;
            if (!QueryFullProcessImageName(process, 0, image, ref length)) {
                int error = Marshal.GetLastWin32Error();
                if (HasExited(process, 0)) return false;
                throw new Win32Exception(error);
            }
            string imageName = Path.GetFileNameWithoutExtension(image.ToString());
            return string.Equals(Path.GetExtension(image.ToString()), ".exe", StringComparison.OrdinalIgnoreCase)
                && (name == null ? Array.Exists(ProcessNames, entry => string.Equals(entry, imageName, StringComparison.OrdinalIgnoreCase))
                    : string.Equals(name, imageName, StringComparison.OrdinalIgnoreCase));
        }

        static bool HasExited(IntPtr process, uint timeout) {
            uint result = WaitForSingleObject(process, timeout);
            if (result == WaitSignaled) return true;
            if (result == WaitTimeout) return false;
            throw new Win32Exception();
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int GetPackageFamilyName(IntPtr process, ref int size, StringBuilder family);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
