// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Windows only: where Ohman keeps its files. Everything sits beside the exe: Ohman is one file somebody
// downloaded into a folder, and its log, its settings and a staged update live and die with that folder.
// src\linux\AppPaths.cs is the same class for Linux, where they go to the XDG directories instead.
using System;
using System.IO;

namespace Ohman {
    static class AppPaths {
        static string Base { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>Simulated hardware keeps its files apart from the real ones. On Windows the preview build is
        /// its own exe in its own folder, so there is nothing to do; the switch exists for Linux.</summary>
        public static bool Demo;

        public static string LogFile { get { return Path.Combine(Base, Program.FileStem + ".log"); } }
        public static string StateFile { get { return Path.Combine(Base, Program.FileStem + ".state"); } }
        /// <summary>Things written while running that are not settings: the EC pair file.</summary>
        public static string StateDir { get { return Base; } }
        /// <summary>What the owner made: benchmark runs.</summary>
        public static string DataDir { get { return Base; } }
        /// <summary>Where a downloaded build waits. Beside the exe, so putting it in place is a rename.</summary>
        public static string UpdateDir { get { return Base; } }
    }
}
