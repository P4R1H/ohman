// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: where Ohman keeps its files. src\win\AppPaths.cs is the same class for Windows, where
// everything sits beside the exe. Here the binaries are root's (/opt/ohman) and the files are the user's, so
// they go where the XDG base directory specification says:
//   settings   $XDG_CONFIG_HOME/ohman/ohman.state     (~/.config/ohman)
//   log        $XDG_STATE_HOME/ohman/ohman.log        (~/.local/state/ohman)
//   data       $XDG_DATA_HOME/ohman/                  (~/.local/share/ohman)
// OHMAN_HOME, when set, replaces all three with one directory: tests and side-by-side runs use it.
// Directories are made on first use, private to the user.
using System;
using System.IO;

namespace Ohman {
    static class AppPaths {
        /// <summary>Simulated hardware keeps its files apart from the real ones (a "demo" directory inside each),
        /// so a preview never loads or overwrites the settings the real laptop runs on. Set it before anything
        /// is logged or loaded: the first thing in Main.</summary>
        public static bool Demo;

        static string Home {
            get {
                string h = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(h) ? "/tmp" : h;
            }
        }

        static string Dir(string xdgVariable, string fallback) {
            string dir = Environment.GetEnvironmentVariable("OHMAN_HOME");
            if (string.IsNullOrEmpty(dir)) {
                string x = Environment.GetEnvironmentVariable(xdgVariable);
                // The specification says a relative path here is invalid and must be ignored.
                dir = Path.Combine(!string.IsNullOrEmpty(x) && x[0] == '/' ? x : Path.Combine(Home, fallback), Program.FileStem);
            }
            if (Demo) dir = Path.Combine(dir, "demo");
            try {
                if (!Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                    try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
                }
            } catch { }
            return dir;
        }

        public static string LogFile { get { return Path.Combine(StateDir, Program.FileStem + ".log"); } }
        public static string StateFile { get { return Path.Combine(Dir("XDG_CONFIG_HOME", ".config"), Program.FileStem + ".state"); } }
        /// <summary>Things written while running that are not settings: the log, the EC pair file.</summary>
        public static string StateDir { get { return Dir("XDG_STATE_HOME", ".local/state"); } }
        /// <summary>What the owner made: benchmark runs.</summary>
        public static string DataDir { get { return Dir("XDG_DATA_HOME", ".local/share"); } }
        /// <summary>Where a downloaded build would wait. Nothing is staged on Linux (Host.CanSelfUpdate is false).</summary>
        public static string UpdateDir { get { return DataDir; } }
    }
}
