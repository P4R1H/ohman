// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the update path: ask GitHub for the newest release of the project's own repository, download its
// Ohman.exe beside the running one, and on request put it in place of the running one and hand over to it.
// Nothing is sent but the requests themselves; no identifiers, no telemetry. Checked at most once a day.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;

namespace Ohman {

    /// <summary>The newest published release: the version, and where its executable is.</summary>
    public sealed class Release {
        public string Tag;              // "1.0.6", never with the leading v
        public string AssetUrl;         // browser_download_url of the bare exe
        public long Size;               // its exact length, the one thing the download can be measured against
    }

    public static class Update {
        public const string Repo = "P4R1H/Ohman";
        public static string ReleasesUrl { get { return "https://github.com/" + Repo + "/releases/latest"; } }
        const string AssetName = "Ohman.exe";

        // ---------- where the pieces live ----------
        static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
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

        // ---------- asking ----------
        /// <summary>The newest release, or null when the check failed.</summary>
        public static Release Latest() { return LatestOf(Repo, AssetName); }

        /// <summary>The newest release of any GitHub repository, and where one named asset on it is. The driver
        /// installer comes from its author's releases through exactly this path.</summary>
        public static Release LatestOf(string repo, string assetName) {
            try {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;      // TLS 1.2: GitHub refuses anything older
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + repo + "/releases/latest");
                req.UserAgent = Program.AppName + "/" + Program.Version;                 // GitHub rejects requests without one
                req.Accept = "application/vnd.github+json";
                req.Timeout = req.ReadWriteTimeout = 8000;
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd();
                string tag = Field(body, "tag_name", 0);
                if (tag == null) return null;
                var r = new Release { Tag = tag.TrimStart('v', 'V') };
                // The assets are a list of objects. Find ours by name and read the rest of that object forward:
                // size and browser_download_url both follow name inside it, and nothing nested in between carries
                // either key.
                int at = body.IndexOf("\"name\":\"" + assetName + "\"", StringComparison.OrdinalIgnoreCase);
                if (at >= 0) {
                    r.AssetUrl = Field(body, "browser_download_url", at);
                    long n;
                    if (long.TryParse(Number(body, "size", at) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) r.Size = n;
                }
                Log.Write(repo + ": latest release " + r.Tag + (repo == Repo ? ", running " + Program.Version : "")
                    + (r.AssetUrl == null ? " (no " + assetName + " asset on it)" : ""));
                return r;
            } catch (Exception ex) { Log.Write(repo + " release check failed: " + ex.Message); return null; }
        }

        /// <summary>The newest release tag ("1.0.6"), or null when the check failed.</summary>
        public static string LatestTag() { var r = Latest(); return r == null ? null : r.Tag; }

        /// <summary>The string value of a JSON field at or after <paramref name="from"/>.</summary>
        static string Field(string json, string name, int from) {
            int i = json.IndexOf("\"" + name + "\"", from, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            i = json.IndexOf('"', i);
            if (i < 0) return null;
            int end = json.IndexOf('"', i + 1);
            if (end < 0) return null;
            return json.Substring(i + 1, end - i - 1);
        }
        /// <summary>The digits of an unquoted JSON number at or after <paramref name="from"/>.</summary>
        static string Number(string json, string name, int from) {
            int i = json.IndexOf("\"" + name + "\"", from, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            int j = i + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t' || json[j] == '\r' || json[j] == '\n')) j++;
            int s = j;
            while (j < json.Length && char.IsDigit(json[j])) j++;
            return j > s ? json.Substring(s, j - s) : null;
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

        /// <summary>Fetch one GitHub release asset to a file. Only GitHub's own hosts may answer, the length must
        /// be the one the release declared, and a failure leaves nothing behind. Used for our own builds and for
        /// the driver installer alike.
        ///
        /// A network hiccup gets one more try, the whole fetch over again (#70): the first attempt timed out after
        /// 15 s at boot, and a stall halfway through the body is the same kind of failure. A wrong host or a
        /// wrong length is not a hiccup and is never retried.</summary>
        public static void Download(string url, string dest, long expectedSize) {
            for (int attempt = 1; ; attempt++) {
                try { DownloadOnce(url, dest, expectedSize, attempt == 1 ? 15000 : 30000); return; }
                catch (Exception ex) {
                    if (attempt >= 2 || !Transient(ex)) throw;
                    Log.Write("download: " + ex.Message + "; trying once more in 5 s");
                    Thread.Sleep(5000);
                }
            }
        }

        static void DownloadOnce(string url, string dest, long expectedSize, int timeoutMs) {
            try {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = Program.AppName + "/" + Program.Version;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = 60000;
                using (var resp = (HttpWebResponse)req.GetResponse()) {
                    // GitHub hands release assets off to its own object store, so the URL we end on is not the
                    // one we asked for. Anywhere outside those two names and we are no longer talking to GitHub.
                    Uri u = resp.ResponseUri;
                    string host = u.Host;
                    bool ok = u.Scheme == "https" && (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
                    if (!ok) throw new Exception("redirected to " + u.Scheme + "://" + host);
                    using (var src = resp.GetResponseStream())
                    using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None)) {
                        var buf = new byte[64 * 1024];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    }
                }
                long len = new FileInfo(dest).Length;
                if (expectedSize > 0 && len != expectedSize) throw new Exception(len + " bytes arrived, the release says " + expectedSize);
            } catch {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                throw;
            }
        }

        /// <summary>Worth one more try: a timeout, a dropped or refused connection, a name that did not resolve, a
        /// read that stalled mid-body (an IOException around the socket), or a server-side 5xx / 408 / 429.</summary>
        static bool Transient(Exception ex) {
            var we = ex as WebException ?? ex.InnerException as WebException;
            if (we == null) return ex is IOException;
            var hr = we.Response as HttpWebResponse;
            if (hr != null) {
                int code = (int)hr.StatusCode;
                return code >= 500 || code == 408 || code == 429;
            }
            return we.Status == WebExceptionStatus.Timeout || we.Status == WebExceptionStatus.ConnectFailure
                || we.Status == WebExceptionStatus.ReceiveFailure || we.Status == WebExceptionStatus.RequestCanceled
                || we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ProxyNameResolutionFailure
                || we.Status == WebExceptionStatus.ConnectionClosed || we.Status == WebExceptionStatus.KeepAliveFailure;
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

        // ---------- comparing ----------
        /// <summary>True when a is a newer version than b ("1.10" &gt; "1.9"). Unparsable values are never newer.</summary>
        public static bool Newer(string a, string b) {
            if (string.IsNullOrEmpty(a)) return false;
            string[] pa = a.TrimStart('v', 'V').Split('.'), pb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++) {
                int na = Part(pa, i), nb = Part(pb, i);
                if (na != nb) return na > nb;
            }
            return false;
        }
        static int Part(string[] parts, int i) {
            int n;
            return i < parts.Length && int.TryParse(new string(Array.FindAll(parts[i].ToCharArray(), char.IsDigit)), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>"3 days ago", "2 hours ago", "just now", the design's phrasing for the last check.</summary>
        public static string Ago(DateTime when) {
            if (when == DateTime.MinValue) return "never checked";
            var d = DateTime.Now - when;
            if (d.TotalMinutes < 2) return "checked just now";
            if (d.TotalHours < 1) return "checked " + (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return "checked " + (int)d.TotalHours + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            int days = (int)d.TotalDays;
            return "checked " + days + (days == 1 ? " day ago" : " days ago");
        }
    }
}
