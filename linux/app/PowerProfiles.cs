// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux app only: what moves the system's own power profile while Ohman runs, told as it happens.
//
// The engine pushes its mode into tuned or power-profiles-daemon (src/linux/Host.cs, SetPowerMode); this is the
// way back. The desktop's own quick settings, tuned-adm in a terminal, a daemon restarting into another
// profile -- each of those is an event, and the engine decides whether the mode follows (Engine.OnSystemProfile,
// behind the "Sync the system power profile" row). Events only, no timer anywhere: the same sources Victus Hub
// listens to,
//   * the daemons' own D-Bus signals: tuned's profile_changed, power-profiles-daemon's PropertiesChanged under
//     both its bus names, and NameOwnerChanged, so a daemon (re)starting counts as one too;
//   * tuned's own file /etc/tuned/active_profile, watched through inotify.
// On every event the profile is read back as it stands *now* -- the file first, the bus second, exactly
// Victus Hub's order -- never the value the signal happened to carry, so nothing ever pushes a half-written
// state. Ohman itself writing the profile is covered too: the event it raises reads back as the mode Ohman
// already holds, and Engine.OnSystemProfile does nothing when the mode did not change. Nothing is read at
// start: there Ohman is the writer, applying its saved mode through the same sync.
// The bus library is the one Avalonia already carries (Tmds.DBus.Protocol, transitively through
// Avalonia.FreeDesktop), which is why this lives beside the window and not in src/linux: the helper and the
// probe link the shared core without it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Ohman {
    sealed class PowerProfileWatch : IDisposable {
        const string PpdNew = "org.freedesktop.UPower.PowerProfiles", PpdOld = "net.hadess.PowerProfiles";
        const string Tuned = "com.redhat.tuned";
        const string TunedControl = "com.redhat.tuned.control";
        const string TunedFile = "/etc/tuned/active_profile";

        readonly Engine engine;
        readonly List<IDisposable> matches = new List<IDisposable>();
        Connection bus;
        FileSystemWatcher file;
        string lastSeen;                    // only to say a profile that is not ours once, not once per event

        PowerProfileWatch(Engine e) { engine = e; }

        /// <summary>Nothing to watch on a machine with neither daemon, so null is fine to keep and dispose.</summary>
        public static PowerProfileWatch Start(Engine engine) {
            if (!Host.HasPowerModeSync) return null;
            var w = new PowerProfileWatch(engine);
            w.Go();
            return w;
        }

        async void Go() {
            bool dbus = false, disk = false;
            try {
                bus = Connection.System;                    // the cached shared connection; never closed here
                await bus.ConnectAsync();
                // tuned: one signal of its own. The payload is not trusted; the read in Refresh is.
                await Add(new MatchRule { Type = MessageType.Signal, Sender = Tuned, Path = "/Tuned", Interface = TunedControl, Member = "profile_changed" }, null);
                // power-profiles-daemon: PropertiesChanged under both its bus names, old and new. Arg0 is the
                // interface the properties belong to, which is the service's own name.
                await Add(new MatchRule { Type = MessageType.Signal, Sender = PpdNew, Path = "/org/freedesktop/UPower/PowerProfiles", Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged", Arg0 = PpdNew }, null);
                await Add(new MatchRule { Type = MessageType.Signal, Sender = PpdOld, Path = "/net/hadess/PowerProfiles", Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged", Arg0 = PpdOld }, null);
                // A daemon (re)starting is an event of its own: it comes back with whatever profile it held. The
                // rule cannot be narrowed to three names, so the handler is.
                await Add(new MatchRule { Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged" },
                    delegate(string name) { return name == Tuned || name == PpdNew || name == PpdOld; });
                dbus = true;
            } catch (Exception ex) { Log.Write("power profile watch: " + ex.Message); }
            try { disk = WatchTunedFile(); } catch (Exception ex) { Log.Write("power profile watch: " + ex.Message); }
            if (dbus || disk) Log.Write("power profile watch: on (" + (dbus && disk ? "D-Bus signals and tuned's file" : dbus ? "D-Bus signals" : "tuned's file") + ")");
        }

        /// <summary>One subscription. The handler runs wherever the bus delivers it (emitOnCapturedContext
        /// false); Refresh has no thread of its own to be on.</summary>
        async Task Add(MatchRule rule, Predicate<string> nameFilter) {
            matches.Add(await bus.AddMatchAsync<string>(rule,
                delegate(Message m, object s) { try { return m.GetBodyReader().ReadString(); } catch { return null; } },
                delegate(Exception ex, string arg, object rs, object hs) {
                    if (ex != null) { Log.Write("power profile watch: " + ex.Message); return; }
                    if (nameFilter == null || nameFilter(arg)) Refresh();
                }, null, null, false));
        }

        bool WatchTunedFile() {
            if (!Directory.Exists(Path.GetDirectoryName(TunedFile))) return false;    // no tuned here: the bus alone, or nothing
            file = new FileSystemWatcher(Path.GetDirectoryName(TunedFile), Path.GetFileName(TunedFile)) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            file.Changed += delegate(object s, FileSystemEventArgs e) { Refresh(); };
            file.Created += delegate(object s, FileSystemEventArgs e) { Refresh(); };
            file.Deleted += delegate(object s, FileSystemEventArgs e) { Refresh(); };
            file.Renamed += delegate(object s, RenamedEventArgs e) { Refresh(); };
            file.Error += delegate(object s, ErrorEventArgs e) { Log.Write("power profile watch: " + e.GetException().Message); };
            file.EnableRaisingEvents = true;
            return true;
        }

        /// <summary>One event of any kind: read the profile as it stands, then hand it to the engine through the
        /// same queue the battery event takes. The read blocks (a bus call can wait out its bound), so it never
        /// happens on the thread that arrived.</summary>
        void Refresh() {
            Task.Run(delegate {
                string profile = ReadProfile();
                if (string.IsNullOrEmpty(profile)) return;
                int index = Host.ProfileIndexFor(profile);
                if (index < 0) {
                    if (profile != lastSeen) { lastSeen = profile; Log.Write("the system power profile is now " + profile + ", not one of ours; the mode stays"); }
                    return;
                }
                engine.Post(delegate { engine.OnSystemProfile(index, profile); });
            });
        }

        /// <summary>The profile as it stands now: tuned's own file first (no bus involved, and it is what
        /// tuned-adm wrote), then power-profiles-daemon's property under both its names, then tuned itself.
        /// The same order Victus Hub reads in, with the bus calls bounded so a hung daemon cannot hold one.</summary>
        string ReadProfile() {
            string p = Host.ReadText(TunedFile);
            if (p.Length > 0) return p;
            string v = Bound(BusGet(PpdNew, "/org/freedesktop/UPower/PowerProfiles", PpdNew));
            if (v != null) return v;
            v = Bound(BusGet(PpdOld, "/net/hadess/PowerProfiles", PpdOld));
            if (v != null) return v;
            return Bound(TunedActive());
        }

        /// <summary>A bus call given up on after two seconds: a daemon that is not there answers with an error
        /// at once, and one that hangs is no reason to wait forever.</summary>
        static string Bound(Task<string> call) {
            try { return call.Wait(2000) ? call.Result : null; }
            catch { return null; }
        }

        /// <summary>org.freedesktop.DBus.Properties.Get, read as the string inside the variant it returns.</summary>
        Task<string> BusGet(string service, string path, string interfaceName) {
            MessageWriter w = bus.GetMessageWriter();
            w.WriteMethodCallHeader(service, path, "org.freedesktop.DBus.Properties", "Get", "ss");
            w.WriteString(interfaceName);
            w.WriteString("ActiveProfile");
            return bus.CallMethodAsync<string>(w.CreateMessage(), delegate(Message m, object s) { return m.GetBodyReader().ReadVariantValue().GetString(); });
        }

        /// <summary>tuned's own answer, for a machine whose file is somewhere else but whose daemon is there.</summary>
        Task<string> TunedActive() {
            MessageWriter w = bus.GetMessageWriter();
            w.WriteMethodCallHeader(Tuned, "/Tuned", TunedControl, "active_profile", null);
            return bus.CallMethodAsync<string>(w.CreateMessage(), delegate(Message m, object s) { return m.GetBodyReader().ReadString(); });
        }

        public void Dispose() {
            foreach (IDisposable m in matches) { try { m.Dispose(); } catch { } }
            matches.Clear();
            if (file != null) { try { file.EnableRaisingEvents = false; file.Dispose(); } catch { } file = null; }
            // The bus connection is the shared static one (Connection.System) and stays: it is not ours to close.
        }
    }
}
