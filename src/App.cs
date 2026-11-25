// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: what the app is called, on every platform. The entry point beside it is per platform: src\Program.cs
// is the Windows one (WPF), and the Linux front ends have their own.
namespace Ohman {
    public static partial class Program {
        // To rename the app: change AppName here and the /out: names in build.cmd. Everything else follows
        // (window title, tray, scheduled task, single-instance names, log/state file names).
        public const string AppName = "Ohman";                 // internal id: file names, mutex, scheduled task
        public static string DisplayName = AppName;           // what the UI shows; override with Name=... in ohman.state
        public const string Version = Meta.Version;           // bump it in src\Meta.cs, not here
        public static string FileStem { get { return AppName.ToLowerInvariant(); } }
        public static bool FanProbe;                          // --fantest with --driver: also find which EC register drives the fans
    }
}
