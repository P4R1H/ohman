// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — update check: asks GitHub for the newest release tag of the project's own repository.
// Nothing is sent but the request itself; no identifiers, no telemetry. Runs at most once a day, or on demand.
using System;
using System.Globalization;
using System.IO;
using System.Net;

namespace Ohman {

    public static class Update {
        public const string Repo = "P4R1H/Ohman";
        public static string ReleasesUrl { get { return "https://github.com/" + Repo + "/releases/latest"; } }

        /// <summary>The newest release tag ("1.3"), or null when the check failed.</summary>
        public static string LatestTag() {
            try {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;      // TLS 1.2: GitHub refuses anything older
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.UserAgent = Program.AppName + "/" + Program.Version;                 // GitHub rejects requests without one
                req.Accept = "application/vnd.github+json";
                req.Timeout = req.ReadWriteTimeout = 8000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) {
                    string body = sr.ReadToEnd();
                    string tag = Field(body, "tag_name");
                    if (tag == null) return null;
                    Log.Write("update check: latest release " + tag + ", running " + Program.Version);
                    return tag.TrimStart('v', 'V');
                }
            } catch (Exception ex) { Log.Write("update check failed: " + ex.Message); return null; }
        }

        /// <summary>The string value of a top-level JSON field (the response has no nested "tag_name").</summary>
        static string Field(string json, string name) {
            int i = json.IndexOf("\"" + name + "\"", StringComparison.Ordinal); if (i < 0) return null;
            i = json.IndexOf(':', i); if (i < 0) return null;
            i = json.IndexOf('"', i); if (i < 0) return null;
            int end = json.IndexOf('"', i + 1); if (end < 0) return null;
            return json.Substring(i + 1, end - i - 1);
        }

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
            int n; return i < parts.Length && int.TryParse(new string(Array.FindAll(parts[i].ToCharArray(), char.IsDigit)), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>"3 days ago", "2 hours ago", "just now" — the design's phrasing for the last check.</summary>
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
