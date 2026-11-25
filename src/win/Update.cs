// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: the half of the update path that touches the installed files: download the release's
// Ohman.exe beside the running one, and on request put it in place of the running one and hand over to it.
// The other half (asking GitHub, downloading, comparing) is shared: src\Update.cs. src\linux\Update.cs is the
// same half for Linux.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Ohman {

    public static partial class Update {
                const string AssetName = "Ohman.exe";

        // ---------- where the pieces live ----------
        static string Dir { get { return AppPaths.UpdateDir; } }
        /// <summary>The running executable. Its name is what the autostart task and every shortcut already point
        /// at, so the new build has to end up here rather than beside it.</summary>
        public static string ExePath {
            get {
                try { string p = Assembly.GetEntryAssembly().Location; if (!string.IsNullOrEmpty(p)) return p; } catch { }
                try { return Process.GetCurrentProcess().MainModule.FileName; } catch { return null; }
            }
        }
        /// <summary>The downloaded build, kept beside the running one rather than in %TEMP%: the same volume makes
        /// putting it in place a rename instead of a copy, and writing it here is the only proof worth having that
        /// the folder can be written to at all.</summary>
        public static string StagePath { get { return Path.Combine(Dir, Program.FileStem + ".update.exe"); } }
        /// <summary>The build we replaced, kept until the new one is running.</summary>
        public static string OldPath { get { return Path.Combine(Dir, Program.FileStem + ".old.exe"); } }

        /// <summary>False when this build must never replace itself. The development tree is the case that
        /// matters: Ohman.exe builds to the repository root, so a working copy would otherwise overwrite the
        /// binary being worked on the first time it saw a published release.</summary>
        public static bool CanReplace {
            get {
                try {
                    if (Directory.Exists(Path.Combine(Dir, "src"))) return false;
                    string exe = ExePath;
                    return !string.IsNullOrEmpty(exe) && File.Exists(exe);
                } catch { return false; }
            }
        }

        // ---------- downloading ----------
        /// <summary>Download the release's executable beside ours and check it is what it says it is. True when
        /// there is now a staged build ready to go in. Failure is quiet on purpose: nothing has been offered to
        /// the owner yet, and the next check will try again.</summary>
        public static bool Stage(Release r) {
            if (r == null || string.IsNullOrEmpty(r.AssetUrl) || !CanReplace) return false;
            if (!Newer(r.Tag, Program.Version)) return false;
            string part = StagePath + ".part";
            try {
                Download(r.AssetUrl, part, r.Size);
                if (!Verify(part, r)) throw new Exception("what arrived is not " + AssetName + " " + r.Tag);
                try { if (File.Exists(StagePath)) File.Delete(StagePath); } catch { }
                File.Move(part, StagePath);
                Log.Write("update " + r.Tag + " staged");
                return true;
            } catch (Exception ex) {
                Log.Write("update download failed: " + ex.Message);
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return false;
            }
        }

        /// <summary>Is the file on disk the build the release said it would be? The length catches a download cut
        /// short, and the version resource catches the wrong asset entirely - which is the one that would
        /// otherwise get as far as being offered to the owner.</summary>
        static bool Verify(string path, Release r) {
            var fi = new FileInfo(path);
            if (r.Size > 0 && fi.Length != r.Size) { Log.Write("update: " + fi.Length + " bytes, the release says " + r.Size); return false; }
            string v = VersionOf(path);
            if (string.IsNullOrEmpty(v)) { Log.Write("update: the download has no version resource"); return false; }
            if (v != r.Tag && !v.StartsWith(r.Tag + ".", StringComparison.Ordinal)) {
                Log.Write("update: the download says " + v + ", the release says " + r.Tag);
                return false;
            }
            return true;
        }
        static string VersionOf(string path) {
            try { string v = FileVersionInfo.GetVersionInfo(path).ProductVersion; return v == null ? null : v.Trim(); } catch { return null; }
        }

        /// <summary>The version staged and ready to go in, or null when there is nothing worth offering.</summary>
        public static string Staged() {
            try {
                if (!CanReplace || !File.Exists(StagePath)) return null;
                string v = VersionOf(StagePath);
                return Newer(v, Program.Version) ? v : null;
            } catch { return null; }
        }

        // ---------- putting it in place ----------
        /// <summary>Swap the staged build in and start it. The caller exits straight after; until it does, the new
        /// process is sitting on the single-instance mutex waiting for it.</summary>
        public static bool Swap(out string error) {
            error = null;
            string exe = ExePath, stage = StagePath, old = OldPath;
            bool renamed = false, moved = false;
            try {
                if (string.IsNullOrEmpty(exe)) throw new Exception("cannot find the running executable");
                if (!File.Exists(stage)) throw new Exception("nothing staged");
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                // Windows will not delete a running image but it will rename one, and that is the whole trick:
                // the new build takes the name the autostart task and every shortcut already point at, and the
                // one it replaced is still on disk to go back to.
                File.Move(exe, old); renamed = true;
                File.Move(stage, exe); moved = true;
                // UseShellExecute = false is CreateProcess: the child inherits our elevated token, so its
                // requireAdministrator manifest costs no second UAC prompt, and the shell's attachment check -
                // the thing that raises SmartScreen - never runs. The file carries no mark of the web either;
                // that is stamped by browsers, not by writing a file.
                Process child = Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Dir });
                // Starting is not running. A build that cannot load - blocked at image load, or simply broken -
                // can come back from CreateProcess and be gone a moment later, and the catch below would never
                // see it. There is no race to lose by waiting: the child spends its first seconds parked on the
                // single-instance mutex waiting for this process, so it is alive unless something killed it.
                if (child != null && child.WaitForExit(2000))
                    throw new Exception("the new build exited immediately (code " + child.ExitCode + ")");
                Log.Write("update: replaced " + Path.GetFileName(exe) + ", started the new build");
                return true;
            } catch (Exception ex) {
                // Back to the build we know runs. Smart App Control refusing an unsigned image it has not seen
                // before would land here, and there is nothing to be done about that from inside the process.
                error = ex.Message;
                Log.Write("update swap failed: " + ex.Message);
                try { if (moved) File.Move(exe, stage); } catch { }
                try { if (renamed) File.Move(old, exe); } catch { }
                return false;
            }
        }

        /// <summary>Remove the build we replaced. Runs on a background thread at startup: the process that put it
        /// there is a moment from exiting and still holds the file until it does.</summary>
        public static void CleanOld() {
            try {
                string old = OldPath;
                for (int i = 0; i < 20 && File.Exists(old); i++) {
                    try { File.Delete(old); } catch { Thread.Sleep(250); }
                }
                if (File.Exists(old)) Log.Write("update: " + Path.GetFileName(old) + " is still in use, leaving it");
                else Log.Write("update: removed the build we replaced");
            } catch { }
        }
    }
}
