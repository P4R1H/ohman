// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: the update path: ask GitHub for the newest release of the project's own repository. Shared: asking,
// downloading, comparing versions. Putting a build in place of the running one is per system and lives in the
// other half of this class: src\win\Update.cs (download Ohman.exe beside the running one and swap), and
// src\linux\Update.cs (report only: the files are root's, the installer replaces them).
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

    public static partial class Update {
        public const string Repo = "P4R1H/Ohman";
        public static string ReleasesUrl { get { return "https://github.com/" + Repo + "/releases/latest"; } }
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
