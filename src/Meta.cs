// SPDX-License-Identifier: GPL-3.0-or-later
// The version number and the publisher strings that every Ohman binary carries.
//
// Windows reads these for the Properties > Details tab, for the name on the UAC prompt, and for Task Manager.
// Reputation services read them too: a binary with a blank product name and 0.0.0.0 for a version is the exact
// shape SmartScreen and Smart App Control treat as unknown software, and signing services require them to be set.
//
// Compiled into Ohman.exe, preview\Ohman.exe and tools\omenprobe.exe alike, so the release checklist is to bump
// Version here and nothing else. The title and description differ per binary and sit next to each entry point.
using System.Reflection;

[assembly: AssemblyProduct("Ohman")]
[assembly: AssemblyCompany("Ohman")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Ohman contributors - GPL-3.0-or-later")]
[assembly: AssemblyVersion(Ohman.Meta.Version)]
[assembly: AssemblyFileVersion(Ohman.Meta.Version)]
[assembly: AssemblyInformationalVersion(Ohman.Meta.Version)]

namespace Ohman {
    /// <summary>The one place the version lives. Program.Version reads from here.</summary>
    public static class Meta {
        public const string Version = "1.2.2";
    }
}
