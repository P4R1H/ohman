// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Ohman {
    static class BoostWindows {
        public static readonly Guid Parking = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        public static readonly Guid Efficiency = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid BestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        static readonly Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        const string GameModeKey = @"Software\Microsoft\GameBar", GameModeValue = "AutoGameModeEnabled";
        const string CaptureKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        const string SharingService = "LanmanServer";
        const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
        public const uint SharingDisabled = 4;

        public static Guid ActiveScheme() {
            IntPtr result;
            Check(PowerGetActiveScheme(IntPtr.Zero, out result));
            try { return (Guid)Marshal.PtrToStructure(result, typeof(Guid)); }
            finally { LocalFree(result); }
        }

        public static uint ReadPower(Guid scheme, Guid setting, bool battery) {
            var subgroup = Processor;
            uint value;
            Check(battery ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
                : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value));
            return value;
        }

        public static void WritePower(Guid scheme, Guid setting, bool battery, uint value) {
            var subgroup = Processor;
            Check(battery ? PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
                : PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value));
        }

        public static void ApplyIfCurrent(Guid scheme) {
            if (scheme == ActiveScheme()) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        }

        public static bool PowerModesSupported {
            get {
                try { ReadPowerMode(false); ReadPowerMode(true); return true; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }
        public static Guid ReadPowerMode(bool battery) {
            Guid mode;
            Check(battery ? PowerGetUserConfiguredDCPowerMode(out mode) : PowerGetUserConfiguredACPowerMode(out mode));
            return mode;
        }
        public static void WritePowerMode(bool battery, Guid mode) {
            Check(battery ? PowerSetUserConfiguredDCPowerMode(ref mode) : PowerSetUserConfiguredACPowerMode(ref mode));
        }

        public static int? ReadGameMode() {
            using (var key = Registry.CurrentUser.OpenSubKey(GameModeKey)) {
                if (key == null) return null;
                object value = key.GetValue(GameModeValue);
                if (value == null) return null;
                if (!(value is int)) throw new InvalidOperationException("Windows Game Mode preference has an unexpected registry type.");
                return (int)value;
            }
        }

        public static void WriteGameMode(int? value) {
            using (var key = Registry.CurrentUser.CreateSubKey(GameModeKey)) {
                if (value.HasValue) key.SetValue(GameModeValue, value.Value, RegistryValueKind.DWord);
                else key.DeleteValue(GameModeValue, false);
            }
        }

        public static int? ReadGameCapture(bool background) {
            using (var key = Registry.CurrentUser.OpenSubKey(CaptureKey)) {
                if (key == null) return null;
                string name = background ? "HistoricalCaptureEnabled" : "AppCaptureEnabled";
                object value = key.GetValue(name);
                if (value == null) return null;
                if (!(value is int)) throw new InvalidOperationException(name + " has an unexpected registry type.");
                return (int)value;
            }
        }

        public static void WriteGameCapture(bool background, int? value) {
            string name = background ? "HistoricalCaptureEnabled" : "AppCaptureEnabled";
            if (value.HasValue) {
                using (var key = Registry.CurrentUser.CreateSubKey(CaptureKey)) key.SetValue(name, value.Value, RegistryValueKind.DWord);
            } else {
                using (var key = Registry.CurrentUser.OpenSubKey(CaptureKey, true)) if (key != null) key.DeleteValue(name, false);
            }
        }

        public static bool SharingRunning() {
            using (var service = new ServiceHandle(0x0004)) return service.Status().State == Running;
        }

        public static bool SharingStopped() {
            using (var service = new ServiceHandle(0x0004)) return service.Status().State == Stopped;
        }

        public static uint SharingStartType() {
            // Read the persisted configuration even if the running service has not adopted it yet.
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SharingService)) {
                object value = key == null ? null : key.GetValue("Start");
                if (!(value is int)) throw new InvalidOperationException("Windows Server has no readable startup type.");
                return (uint)(int)value;
            }
        }

        public static void SetSharingStartType(uint type) {
            using (var service = new ServiceHandle(0x0002)) {
                const uint NoChange = uint.MaxValue;
                if (!ChangeServiceConfig(service.Handle, NoChange, type, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception();
            }
        }

        public static bool RequestSharingStop() {
            using (var service = new ServiceHandle(0x0004 | 0x0020)) {
                if (service.Status().State != Running) return false;
                ServiceStatus status;
                if (!ControlService(service.Handle, 1, out status)) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1062) return false;
                    throw new Win32Exception(error);
                }
                return true;
            }
        }

        public static bool RestoreSharing() {
            using (var service = new ServiceHandle(0x0004 | 0x0010)) {
                uint state = service.Status().State;
                if (state == Running) return true;
                if (state == StopPending || state == StartPending) return false;
                if (state != Stopped) return true; // another controller owns a paused service
                if (!StartService(service.Handle, 0, IntPtr.Zero)) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1056) return false;
                    throw new Win32Exception(error);
                }
                return service.Status().State == Running;
            }
        }

        // The native memory-list operations are optional and can be rejected by Windows.
        public static void TrimMemory() {
            IntPtr original, token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x000A, out original)) throw new Win32Exception();
            try {
                // Only this worker uses the enabled privilege. The main application's token stays intact.
                if (!DuplicateTokenEx(original, 0x002C, IntPtr.Zero, 2, 2, out token)) throw new Win32Exception();
            } finally { CloseHandle(original); }
            try {
                long luid;
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out luid)) throw new Win32Exception();
                var enable = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                bool ok = AdjustTokenPrivileges(token, false, ref enable, 0, IntPtr.Zero, IntPtr.Zero);
                int error = Marshal.GetLastWin32Error();
                if (!ok || error != 0) throw new Win32Exception(error);
                using (WindowsIdentity.Impersonate(token)) {
                    const int MemoryListInformation = 80;
                    int emptyWorkingSets = 2, purgeStandbyList = 4;
                    int rc = NtSetSystemInformation(MemoryListInformation, ref emptyWorkingSets, sizeof(int));
                    if (rc != 0) throw new Win32Exception((int)RtlNtStatusToDosError(rc));
                    rc = NtSetSystemInformation(MemoryListInformation, ref purgeStandbyList, sizeof(int));
                    if (rc != 0) throw new Win32Exception((int)RtlNtStatusToDosError(rc));
                }
            } finally { CloseHandle(token); }
        }

        static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }

        sealed class ServiceHandle : IDisposable {
            public readonly IntPtr Handle;
            public ServiceHandle(uint access) {
                IntPtr manager = OpenSCManager(null, null, 1);
                if (manager == IntPtr.Zero) throw new Win32Exception();
                try {
                    Handle = OpenService(manager, SharingService, access);
                    if (Handle == IntPtr.Zero) throw new Win32Exception();
                } finally { CloseServiceHandle(manager); }
            }
            public ServiceStatus Status() {
                ServiceStatus status;
                if (!QueryServiceStatus(Handle, out status)) throw new Win32Exception();
                return status;
            }
            public void Dispose() { CloseServiceHandle(Handle); }
        }

        [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type, State, Controls, Win32Exit, ServiceExit, CheckPoint, WaitHint; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr service);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig(IntPtr service,
            uint serviceType, uint startType, uint errorControl, string image, string group, IntPtr tag, string dependencies, string account, string password, string display);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool StartService(IntPtr service, uint count, IntPtr args);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr original, uint access, IntPtr attributes, int impersonation, int type, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int size, IntPtr old, IntPtr returned);
        [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int type, ref int info, int length);
        [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    }
}
