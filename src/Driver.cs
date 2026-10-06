// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the kernel driver, which is PawnIO (https://pawnio.eu) and not ours.
//
// The WMI mailbox is all the firmware offers from user mode. Two things live behind it that the mailbox does
// not reach: the embedded controller's own registers (ports 0x62/0x66) and the CPU's model-specific registers.
// Both need ring 0, and the only way to ring 0 that Windows still allows is a signed driver. PawnIO is that
// driver: signed, HVCI-compatible, open, and it executes nothing but signed modules, small Pawn scripts with
// an allow-list each, so what a caller can touch is decided by the module's author, not by the caller.
//
// This file knows nothing about HP. It finds the driver, installs it on request, opens it, loads a module and
// runs functions in it. Ec.cs and Cpu.cs are the two users.
//
// Wire protocol, from LibreHardwareMonitor's PawnIo.cs and PawnIOLib.h: the device is \Device\PawnIO, one
// handle per loaded module, IOCTL_PIO_LOAD_BINARY takes the module bytes, IOCTL_PIO_EXECUTE_FN takes a
// 32-byte function name followed by 64-bit cells in, and answers with 64-bit cells out. Nothing else.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ohman {

    /// <summary>One loaded module: a handle on the driver with a script in it. Thread-safe per call.</summary>
    public sealed class PawnIoModule : IDisposable {
        const uint DeviceType = 41394u << 16;
        const uint IoctlLoad = DeviceType | (0x821u << 2);
        const uint IoctlExecute = DeviceType | (0x841u << 2);
        const int NameLength = 32;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint returned, IntPtr overlapped);

        readonly SafeFileHandle handle;
        readonly object sync = new object();
        public readonly string Name;

        PawnIoModule(SafeFileHandle h, string name) { handle = h; Name = name; }

        /// <summary>Open the driver and load one module into it. Null with the reason when either step fails:
        /// the driver is not there, refuses us, or rejects the module (an unsigned one, or one for the wrong
        /// CPU vendor - main() in each module checks and answers STATUS_NOT_SUPPORTED).</summary>
        public static PawnIoModule Load(string name, byte[] blob, out string why) { bool absent; return Load(name, blob, out why, out absent); }
        /// <summary>deviceAbsent says which step failed: the device itself, or this module.</summary>
        public static PawnIoModule Load(string name, byte[] blob, out string why, out bool deviceAbsent) {
            why = null;
            deviceAbsent = false;
            SafeFileHandle h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000u /* GENERIC_READ|WRITE */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
            if (h == null || h.IsInvalid) { why = "cannot open the PawnIO device (" + Win32(Marshal.GetLastWin32Error()) + ")"; deviceAbsent = true; return null; }
            uint ret;
            if (!DeviceIoControl(h, IoctlLoad, blob, (uint)blob.Length, null, 0, out ret, IntPtr.Zero)) {
                why = "the driver rejected the " + name + " module (" + Win32(Marshal.GetLastWin32Error()) + ")";
                h.Close();
                return null;
            }
            return new PawnIoModule(h, name);
        }

        /// <summary>Run one function. Returns 0 on success, otherwise the Win32 error the driver mapped the
        /// module's NTSTATUS to (5 = access denied: the module's allow-list said no).</summary>
        public int Execute(string fn, ulong[] input, ulong[] output, out int returnedCells) {
            returnedCells = 0;
            int nin = input == null ? 0 : input.Length, nout = output == null ? 0 : output.Length;
            var inBuf = new byte[NameLength + nin * 8];
            Encoding.ASCII.GetBytes(fn, 0, Math.Min(fn.Length, NameLength - 1), inBuf, 0);
            for (int i = 0; i < nin; i++) Array.Copy(BitConverter.GetBytes(input[i]), 0, inBuf, NameLength + i * 8, 8);
            var outBuf = new byte[nout * 8];
            uint ret;
            bool ok;
            lock (sync) ok = DeviceIoControl(handle, IoctlExecute, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out ret, IntPtr.Zero);
            if (!ok) return Marshal.GetLastWin32Error();
            returnedCells = (int)(ret / 8);
            for (int i = 0; i < returnedCells && i < nout; i++) output[i] = BitConverter.ToUInt64(outBuf, i * 8);
            return 0;
        }

        /// <summary>Execute, for callers that only need yes or no.</summary>
        public bool Call(string fn, ulong[] input, ulong[] output) { int n; return Execute(fn, input, output, out n) == 0; }

        public void Dispose() { try { handle.Close(); } catch { } }

        internal static string Win32(int code) { return new System.ComponentModel.Win32Exception(code).Message + " [" + code + "]"; }
    }

    /// <summary>What Install came back with. The installer speaks in DOS error codes since 2.2.0.</summary>
    public enum DriverInstallResult { Installed, RestartNeeded, Failed }

    /// <summary>The driver on this machine: found, installed, removed. All static; there is one driver.</summary>
    public static class PawnIo {
        public const string SetupRepo = "namazso/PawnIO.Setup";
        public const string SetupAsset = "PawnIO_setup.exe";
        public const string HomeUrl = "https://pawnio.eu";
        /// <summary>Where its code is. The row links to it, so looking it up is not a web search.</summary>
        public const string SourceUrl = "https://github.com/namazso/PawnIO";
        /// <summary>The name on the installer's Authenticode certificate. The chain is verified by Windows; this
        /// says whose chain it has to be, so a valid signature by somebody else is still not the installer.</summary>
        public const string Signer = "namazso.eu";
        /// <summary>First version whose silent mode returns DOS codes and upgrades in place. Older ones answered
        /// with NTSTATUS values, which makes "did it work" a guess; anything older is offered an update.</summary>
        public static readonly Version MinVersion = new Version(2, 2, 0);
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        // ---------- what is on the machine ----------
        /// <summary>Installed version, or null. The installer writes the ordinary uninstall key, and only that.</summary>
        public static Version InstalledVersion() {
            string v = Reg("DisplayVersion");
            Version ver;
            return v != null && Version.TryParse(v, out ver) ? ver : null;
        }
        public static string InstallLocation() { return Reg("InstallLocation"); }
        /// <summary>Where the driver's own files are. The installer writes InstallLocation, but a removal takes
        /// that key away before the files, so a half-finished one has to be finishable: the installer offers no
        /// choice of folder and its own documentation names the default, which is a safe second place to look.</summary>
        public static string InstallFolder() {
            string dir = InstallLocation();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            return Directory.Exists(def) ? def : dir;
        }
        public static bool Installed { get { return InstalledVersion() != null; } }
        public static bool Outdated { get { Version v = InstalledVersion(); return v != null && v < MinVersion; } }
        static string Reg(string value) {
            try {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallKey))
                    if (k != null) { string s = k.GetValue(value) as string; if (!string.IsNullOrEmpty(s)) return s.Trim(); }
            } catch { }
            return null;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string machine, string db, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr svc, out ServiceStatus status);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

        /// <summary>"running", "stopped", "not registered" - the service control manager's word on the driver.
        /// A registered but stopped driver is what a fresh install looks like until the reboot it asked for.</summary>
        public static string ServiceState() {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try {
                scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
                if (scm == IntPtr.Zero) return "service manager unavailable";
                svc = OpenService(scm, "PawnIO", 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? "not registered" : "cannot query (" + PawnIoModule.Win32(Marshal.GetLastWin32Error()) + ")";
                ServiceStatus st;
                if (!QueryServiceStatus(svc, out st)) return "cannot query";
                switch (st.State) {
                    case 4: return "running";
                    case 1: return "stopped";
                    case 2: case 3: return "starting";
                    default: return "state " + st.State;
                }
            } catch (Exception ex) { return "cannot query (" + ex.Message + ")"; }
            finally { if (svc != IntPtr.Zero) CloseServiceHandle(svc); if (scm != IntPtr.Zero) CloseServiceHandle(scm); }
        }

        // ---------- the modules we carry ----------
        /// <summary>A signed module from third_party\PawnIO.Modules, embedded by build.cmd.</summary>
        public static byte[] Module(string name) {
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ohman.pawnio." + name + ".bin")) {
                if (st == null) throw new InvalidOperationException("embedded module " + name + " missing");
                var ms = new MemoryStream();
                st.CopyTo(ms);
                return ms.ToArray();
            }
        }
        /// <summary>Open the driver with one of our modules in it. Null, with the reason, when it cannot be done.</summary>
        public static PawnIoModule Open(string module, out string why) { bool absent; return Open(module, out why, out absent); }
        /// <summary>deviceAbsent: the driver itself could not be opened, as opposed to this one module being refused.
        /// The two mean different things to a caller with a second module to try.</summary>
        public static PawnIoModule Open(string module, out string why, out bool deviceAbsent) {
            why = null;
            deviceAbsent = false;
            if (!Installed) { why = "not installed"; deviceAbsent = true; return null; }
            try { return PawnIoModule.Load(module, Module(module), out why, out deviceAbsent); }
            catch (Exception ex) { why = ex.Message; return null; }
        }

        // ---------- installing ----------
        /// <summary>Where the installer is downloaded to. A name nothing can predict, because the folder Ohman
        /// runs from is usually the one it was downloaded to and is therefore writable by the user: a fixed name
        /// there can be waited for.</summary>
        static string NewSetupPath() {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.FileStem + "." + Guid.NewGuid().ToString("N") + ".setup.exe");
        }

        /// <summary>Fetch the signed installer from its author's releases, check the signature, run it silently.
        /// The caller is elevated (app.manifest), and CreateProcess hands that token down, so there is no second
        /// UAC prompt. progress gets one short line per step for the row in Settings.</summary>
        public static DriverInstallResult Install(Action<string> progress, out string error) {
            error = null;
            string setup = NewSetupPath();
            try {
                Say(progress, "Finding the installer…");
                Release r = Update.LatestOf(SetupRepo, SetupAsset);
                if (r == null || string.IsNullOrEmpty(r.AssetUrl)) throw new Exception("could not find " + SetupAsset + " on " + SetupRepo);
                Say(progress, "Downloading " + (r.Size > 0 ? (r.Size / 1024 / 1024.0).ToString("0.0") + " MB" : "the installer") + "…");
                Update.Download(r.AssetUrl, setup, r.Size);
                // Checked and launched through one handle. Checking a path and then running that path are two
                // different files if anything is watching for the gap, and this one runs with Ohman's token.
                // FileShare.Read still lets Windows map the image to execute it.
                using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    Say(progress, "Checking the signature…");
                    string signer;
                    if (!Signed(setup, out signer)) throw new Exception("the installer's signature is not " + Signer + (signer != null ? " (it is " + signer + ")" : ""));
                    // 2.2.0 upgrades 2.1.0 in place; anything older has to go first, which is what the other apps
                    // that bundle this installer do unconditionally.
                    Version have = InstalledVersion();
                    if (have != null && have < new Version(2, 1, 0)) {
                        Say(progress, "Removing PawnIO " + have + "…");
                        Run(setup, "-uninstall -silent");
                    }
                    Say(progress, "Installing…");
                    int rc = Run(setup, "-install -silent");
                    Log.Write("PawnIO installer exit code " + rc);
                    switch (rc) {
                        case 0: case 183: return Installed ? DriverInstallResult.Installed : Fail("the installer returned " + rc + " but left no installation behind", out error);   // 183 = ERROR_ALREADY_EXISTS
                        case 3010: case 1072: return DriverInstallResult.RestartNeeded;   // ERROR_SUCCESS_REBOOT_REQUIRED, ERROR_SERVICE_MARKED_FOR_DELETE
                        default: return Fail("the installer returned " + PawnIoModule.Win32(rc), out error);
                    }
                }
            } catch (Exception ex) { return Fail(ex.Message, out error); }
            finally { try { if (File.Exists(setup)) File.Delete(setup); } catch { } }
        }
        static DriverInstallResult Fail(string why, out string error) { error = why; Log.Write("PawnIO install failed: " + why); return DriverInstallResult.Failed; }
        static void Say(Action<string> progress, string s) { Log.Write("PawnIO: " + s); if (progress != null) { try { progress(s); } catch { } } }

        /// <summary>Remove the driver with its own installer, which lives in the install folder as uninstall.exe
        /// (the setup binary behaves as the uninstaller under that name).</summary>
        public static bool Uninstall(out string error) {
            error = null;
            string copy = null;
            try {
                string dir = InstallFolder();
                string exe = null;
                if (!string.IsNullOrEmpty(dir)) {
                    foreach (string cand in new string[] { Path.Combine(dir, "uninstall.exe"), Path.Combine(dir, SetupAsset) })
                        if (File.Exists(cand)) { exe = cand; break; }
                }
                if (exe == null) {
                    if (!Installed) return true;                    // nothing registered and nothing to run: already gone
                    throw new Exception("no uninstaller in " + (dir ?? "(unknown folder)"));
                }
                // From a copy, under the same name: Windows will not let a program delete the image it is running
                // from. That and the working directory (see Run) is why the first removal here left the folder
                // behind with a sharing violation.
                copy = Path.Combine(Path.GetTempPath(), Program.AppName + "-pawnio-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(copy);
                string runner = Path.Combine(copy, "uninstall.exe");
                File.Copy(exe, runner);
                int rc = Run(runner, "-uninstall -silent");
                Log.Write("PawnIO uninstaller exit code " + rc + " · service " + ServiceState());
                // The exit code is what the uninstaller thinks happened; the registry is what happened. An
                // unrecognised code from a driver that is plainly gone is not a failure worth reporting.
                if (!Installed) return true;
                throw new Exception(rc == 0 ? "the driver is still registered" : "the uninstaller returned " + PawnIoModule.Win32(rc));
            } catch (Exception ex) { error = ex.Message; Log.Write("PawnIO uninstall failed: " + ex.Message); return false; }
            finally { try { if (copy != null) Directory.Delete(copy, true); } catch { } }
        }

        static int Run(string exe, string args) {
            // Never the folder being installed into or removed: a directory that is a running process's working
            // directory cannot be deleted, which is half of why the first removal here failed.
            using (Process p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() })) {
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new Exception("the installer did not finish in three minutes"); }
                return p.ExitCode;
            }
        }

        // ---------- the signature ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WintrustFileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct WintrustData {
            public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>Is this file signed with a certificate Windows trusts, and is it the author's? Both, or
        /// nothing is run. A tampered download fails the first; a valid signature by anyone else fails the
        /// second. Revocation is not fetched: this runs on laptops with no guarantee of a network, and a
        /// revoked certificate of this author is not the threat the check exists for.</summary>
        public static bool Signed(string path, out string signer) { return Signed(path, Signer, out signer); }
        /// <summary>The same check for any author: PresentMon has to be Intel's.</summary>
        public static bool Signed(string path, string expected, out string signer) {
            signer = null;
            IntPtr pInfo = IntPtr.Zero, pData = IntPtr.Zero;
            try {
                var fi = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo)), pcwszFilePath = path };
                pInfo = Marshal.AllocHGlobal((int)fi.cbStruct);
                Marshal.StructureToPtr(fi, pInfo, false);
                var wd = new WintrustData {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData)), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */,
                    dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pInfo, dwStateAction = 0 /* WTD_STATEACTION_IGNORE */,
                    dwProvFlags = 0x10 | 0x1000 /* WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL */
                };
                pData = Marshal.AllocHGlobal((int)wd.cbStruct);
                Marshal.StructureToPtr(wd, pData, false);
                int hr = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData);
                if (hr != 0) { Log.Write(System.IO.Path.GetFileName(path) + " signature: WinVerifyTrust 0x" + hr.ToString("X8")); return false; }
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                signer = cert.GetNameInfo(X509NameType.SimpleName, false);
                return signer != null && signer.Equals(expected, StringComparison.OrdinalIgnoreCase);
            } catch (Exception ex) { Log.Write(System.IO.Path.GetFileName(path) + " signature: " + ex.Message); return false; }
            finally { if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData); if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo); }
        }
    }
}
