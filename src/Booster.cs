// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ohman {
    [Flags] enum BoostOptions { None = 0, Parking = 1, Efficiency = 2, Sharing = 4, GameBar = 8, GameMode = 16, WindowsKey = 32, Memory = 64, Priority = 128 }

    sealed class Booster : IDisposable {
        const string Park = "parking", Energy = "efficiency", Share = "sharing", Mode = "game-mode", Priority = "priority-";
        const string Capture = "game-capture", BackgroundCapture = "game-background-capture";
        const string ShareConfig = "sharing-config";
        readonly object gate = new object();
        readonly int sessionId, self;
        readonly BoostJournal journal;
        readonly Timer timer;
        readonly Dictionary<string, string> saved = new Dictionary<string, string>();
        static readonly long PriorityRetryDelay = Stopwatch.Frequency;
        readonly Dictionary<string, long> nextPriorityAttempt = new Dictionary<string, long>();
        readonly HashSet<string> priorityLost = new HashSet<string>();
        volatile Preferences config = new Preferences();
        volatile bool manual, closing, suppressAuto, draining, drained;
        int restoreRequested, gameBarCheckQueued;
        volatile InputFilter input = new InputFilter();
        bool active, loaded;
        int idleTicks, failed;
        int sharingChecks;
        bool sharingConfirmed, sharingLost;
        bool gameBarStopPending, captureAttempted;
        Guid? powerPlan;
        DateTime nextRestore;
        bool invalidRecovery;
        BoostOptions sessionOptions;
        public volatile string Status = "Ready";
        public bool Manual { get { return manual; } }
        public bool Draining { get { return draining; } }
        public InputFilter Input { get { return input; } }

        sealed class Preferences {
            public bool Auto;
            public BoostOptions Options;
            public string[] Games = new string[0];
        }
        public sealed class InputFilter {
            public BoostOptions Options;
            public int[] Pids = new int[0];
        }

        public Booster() : this(new RegistryBoostJournal()) { }

        internal Booster(BoostJournal journal) {
            using (var p = Process.GetCurrentProcess()) { sessionId = p.SessionId; self = p.Id; }
            this.journal = journal;
            timer = new Timer(Tick, null, 500, 1500);
        }

        public static string[] ParseGames(string value) {
            return value.Split('|').Where(path => path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(path)
                && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void Configure(bool auto, int options, string[] games) {
            if (auto && !config.Auto) suppressAuto = false;
            config = new Preferences { Auto = auto, Options = (BoostOptions)(options & 255), Games = games };
        }

        public void StartManual() { manual = true; suppressAuto = false; }
        public void RestoreNow() { manual = false; suppressAuto = true; Interlocked.Exchange(ref restoreRequested, 1); }

        public void CheckGameBarSoon() {
            if (Interlocked.Exchange(ref gameBarCheckQueued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    if (!Monitor.TryEnter(gate)) return;
                    try {
                        var current = config;
                        if (!closing && !draining && restoreRequested == 0 && !suppressAuto && (manual || current.Auto)
                            && active && input.Pids.Length > 0 && sessionOptions.HasFlag(BoostOptions.GameBar)) CheckGameBar();
                    } finally { Monitor.Exit(gate); }
                } catch (Exception ex) { Issue("Xbox Game Bar check", ex); }
                finally { Interlocked.Exchange(ref gameBarCheckQueued, 0); }
            });
        }

        public bool Drain() {
            draining = true;
            if (drained) return true;
            if (!Monitor.TryEnter(gate)) return false;
            try { return !active && loaded && saved.Count == 0; }
            finally { Monitor.Exit(gate); }
        }

        void Tick(object ignored) {
            if (closing || !Monitor.TryEnter(gate)) return;
            try { if (!closing) Check(); }
            catch (Exception ex) { Issue("Booster error", ex); Status = "Booster error · see Log"; }
            finally { Monitor.Exit(gate); }
        }

        void Check() {
            if (!loaded) LoadJournal();
            if (draining) {
                End();
                RestoreChanges();
                drained = saved.Count == 0;
                Status = drained ? "Windows settings restored" : "Restoring Windows settings · see Log";
                return;
            }
            var current = config;
            bool restore = Interlocked.Exchange(ref restoreRequested, 0) != 0;
            if (active && (restore || (!manual && !current.Auto))) End();
            if (!active && saved.Count > 0) {
                RestoreChanges();
                if (saved.Count > 0) { Status = "Waiting to restore Windows settings · see Log"; return; }
            }
            if (!manual && !current.Auto) { suppressAuto = false; Status = invalidRecovery ? "Invalid recovery records were ignored · see Log" : "Ready"; return; }
            if (!active && current.Options == BoostOptions.None) { manual = false; Status = "Select a boost option"; return; }

            var games = FindGames(current.Games);
            try {
                if (games.Count == 0) {
                    suppressAuto = false;
                    if (!manual && active && ++idleTicks >= 2) { End(); RestoreChanges(); }
                    if (!active) Status = saved.Count > 0 ? "Waiting to restore Windows settings · see Log"
                        : current.Games.Length == 0 ? "Add a game for automatic boost" : "Waiting for a selected game";
                    if (!manual) return;
                }
                else idleTicks = 0;
                bool starting = !active && !suppressAuto;
                if (starting) Begin(current.Options);
                if (!active) { Status = "Restored · waiting for the next gaming session"; return; }
                input = new InputFilter { Options = sessionOptions & (BoostOptions.GameBar | BoostOptions.WindowsKey), Pids = games.Select(game => game.Id).ToArray() };
                if (games.Count > 0 && sessionOptions.HasFlag(BoostOptions.GameBar)) {
                    if (!captureAttempted) {
                        captureAttempted = true;
                        Try("Xbox Game Bar background capture", () => DisableCapture(BackgroundCapture, true));
                        Try("Xbox Game Bar capture", () => DisableCapture(Capture, false));
                    }
                    CheckGameBar();
                }
                if (sessionOptions.HasFlag(BoostOptions.Priority)) {
                    RestoreUnselectedGames(games);
                    foreach (var game in games) RaisePriority(game);
                }
                RefreshPowerPlan();
                if (starting && sessionOptions.HasFlag(BoostOptions.Efficiency)) Try("Windows performance preference", ApplyPowerMode);
                if (sessionOptions.HasFlag(BoostOptions.Sharing)) CheckSharing();
                const BoostOptions gameOnly = BoostOptions.GameBar | BoostOptions.WindowsKey | BoostOptions.Priority;
                if (games.Count == 0 && (sessionOptions & ~gameOnly) == 0) Status = "Waiting for a selected game · no options applied";
                else if (priorityLost.Count != 0) Status = "Boost active · High priority pending; see Log";
                else if (sharingLost) Status = "Boost active · sharing stop unconfirmed; see Log";
                else if (gameBarStopPending && games.Count > 0) Status = "Boost active · Xbox Game Bar stop unconfirmed; see Log";
                else if (failed != 0) Status = "Boost active · some options did not take effect; see Log";
                else if (current.Options != sessionOptions) Status = "Boost active · option changes apply next session";
                else if (sessionOptions.HasFlag(BoostOptions.Efficiency)) Status = "Boost active · Windows may override the power preference";
                else Status = "Boost active";
            } finally { foreach (var game in games) game.Dispose(); }
        }

        void Begin(BoostOptions options) {
            active = true;
            sessionOptions = options;
            Log.Write("booster: session started · " + options);
            failed = 0;
            idleTicks = 0;
            if (options.HasFlag(BoostOptions.Sharing)) Try("File and printer sharing", SuspendSharing);
            if (options.HasFlag(BoostOptions.GameMode)) Try("Windows Game Mode", () => {
                int? before = BoostWindows.ReadGameMode();
                if (before == 0) return;
                Remember(Mode, before.HasValue ? before.Value.ToString() : "absent");
                BoostWindows.WriteGameMode(0);
            });
            if (options.HasFlag(BoostOptions.Memory)) Try("Memory Boost", () => {
                BoostWindows.TrimMemory();
                Log.Write("booster: Memory Boost completed (working sets and standby cache)");
            });
        }

        void CheckGameBar() {
            string problem = BoostGameBar.StopInSession(sessionId);
            if (problem != null && !gameBarStopPending) Log.Write("booster: Xbox Game Bar stop unconfirmed: " + problem);
            if (problem == null && gameBarStopPending) Log.Write("booster: Xbox Game Bar stop check cleared");
            gameBarStopPending = problem != null;
        }

        void DisableCapture(string key, bool background) {
            int? before = BoostWindows.ReadGameCapture(background);
            if (before != 0) {
                Remember(key, before.HasValue ? before.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent");
                BoostWindows.WriteGameCapture(background, 0);
                if (BoostWindows.ReadGameCapture(background) != 0)
                    throw new InvalidOperationException("Windows did not retain the disabled capture preference.");
            }
            Log.Write("booster: verified Xbox Game Bar " + (background ? "background capture" : "capture") + " preference is off");
        }

        void SuspendSharing() {
            uint startup = BoostWindows.SharingStartType();
            bool wasRunning = BoostWindows.SharingRunning();
            if (startup == BoostWindows.SharingDisabled && BoostWindows.SharingStopped()) return;
            if ((startup != 2 && startup != 3) || (!wasRunning && !BoostWindows.SharingStopped()))
                throw new InvalidOperationException("Windows Server is in a state that cannot be restored safely.");

            string original = startup + (wasRunning ? ",running" : ",stopped");
            Remember(ShareConfig, original);
            try {
                BoostWindows.SetSharingStartType(BoostWindows.SharingDisabled);
                if (BoostWindows.SharingStartType() != BoostWindows.SharingDisabled)
                    throw new InvalidOperationException("Windows Server startup type did not become Disabled.");
                if (!BoostWindows.SharingStopped() && BoostWindows.RequestSharingStop())
                    Log.Write("booster: disabled Windows Server startup and requested a stop");
            } catch {
                Try("Restore Windows Server after an unsuccessful stop", RestoreSharingConfiguration);
                throw;
            }
        }

        void RestoreSharingConfiguration() {
            string original;
            if (saved.TryGetValue(ShareConfig, out original) && RestoreOne(ShareConfig, original)) Forget(ShareConfig);
        }

        void CheckSharing() {
            bool stopped;
            try {
                stopped = BoostWindows.SharingStopped() && BoostWindows.SharingStartType() == BoostWindows.SharingDisabled;
            }
            catch (Exception ex) {
                if (!sharingLost) {
                    Issue("File and printer sharing readback", ex);
                    if (!sharingConfirmed) Try("Restore Windows Server after an unconfirmed stop", RestoreSharingConfiguration);
                }
                sharingLost = true;
                return;
            }
            if (stopped) {
                if (!sharingConfirmed || sharingLost) Log.Write("booster: verified Windows Server stopped with startup Disabled");
                sharingConfirmed = true;
                sharingLost = false;
            } else if (!sharingLost && (sharingConfirmed || ++sharingChecks >= 4)) {
                Log.Write("booster: Windows Server could not be verified stopped and disabled");
                sharingLost = true;
                if (!sharingConfirmed) Try("Restore Windows Server after an unconfirmed stop", RestoreSharingConfiguration);
            }
        }

        void End() {
            active = false;
            input = new InputFilter();
            nextPriorityAttempt.Clear();
            priorityLost.Clear();
            sharingChecks = 0;
            sharingConfirmed = sharingLost = false;
            gameBarStopPending = captureAttempted = false;
            idleTicks = 0;
            powerPlan = null;
        }

        void RefreshPowerPlan() {
            if ((sessionOptions & (BoostOptions.Parking | BoostOptions.Efficiency)) == 0) return;
            var scheme = BoostWindows.ActiveScheme();
            if (powerPlan == scheme) return;
            foreach (string key in new[] { Park, Energy }) {
                string old;
                if (saved.TryGetValue(key, out old) && RestoreOne(key, old)) Forget(key);
            }
            powerPlan = scheme;
            if (sessionOptions.HasFlag(BoostOptions.Parking)) Try("CPU core parking", () => ApplyPower(Park, scheme, BoostWindows.Parking, 100));
            if (sessionOptions.HasFlag(BoostOptions.Efficiency)) Try("CPU energy preference", () => ApplyPower(Energy, scheme, BoostWindows.Efficiency, 0));
        }

        void ApplyPower(string key, Guid scheme, Guid setting, uint wanted) {
            uint ac = BoostWindows.ReadPower(scheme, setting, false), dc = BoostWindows.ReadPower(scheme, setting, true);
            if (ac == wanted && dc == wanted) return;
            string before = scheme + "," + ac + "," + dc;
            Remember(key, before);
            try {
                if (ac != wanted) BoostWindows.WritePower(scheme, setting, false, wanted);
                if (dc != wanted) BoostWindows.WritePower(scheme, setting, true, wanted);
                BoostWindows.ApplyIfCurrent(scheme);
            } catch {
                if (RestoreOne(key, before)) Forget(key);
                throw;
            }
        }

        void ApplyPowerMode() {
            if (!BoostWindows.PowerModesSupported) return;
            Guid ac = BoostWindows.ReadPowerMode(false), dc = BoostWindows.ReadPowerMode(true);
            if (ac == BoostWindows.BestPerformance && dc == ac) return;
            const string key = "power-mode";
            string before = ac + "," + dc;
            Remember(key, before);
            try {
                if (ac != BoostWindows.BestPerformance) BoostWindows.WritePowerMode(false, BoostWindows.BestPerformance);
                if (dc != BoostWindows.BestPerformance) BoostWindows.WritePowerMode(true, BoostWindows.BestPerformance);
            } catch {
                if (RestoreOne(key, before)) Forget(key);
                throw;
            }
        }

        void RaisePriority(Process game) {
            string key;
            try { key = Priority + game.Id + "-" + game.StartTime.ToUniversalTime().Ticks; }
            catch (InvalidOperationException) { return; }
            catch (Win32Exception ex) { Issue("Game process identity", ex); return; }
            long now = Stopwatch.GetTimestamp(), next;
            bool first = !nextPriorityAttempt.TryGetValue(key, out next);
            if (first) nextPriorityAttempt.Add(key, 0);
            try {
                var before = game.PriorityClass;
                if (before == ProcessPriorityClass.High || before == ProcessPriorityClass.RealTime) {
                    if (first) Log.Write("booster: " + game.ProcessName + " already has " + before + " priority");
                    priorityLost.Remove(key);
                    return;
                }
                if (now < next) {
                    if (priorityLost.Add(key)) Log.Write("booster: " + game.ProcessName + " priority fell to " + before + "; retry queued");
                    return;
                }
                nextPriorityAttempt[key] = now + PriorityRetryDelay;
                if (!saved.ContainsKey(key)) Remember(key, ((int)before).ToString());
                game.PriorityClass = ProcessPriorityClass.High;
                game.Refresh();
                var after = game.PriorityClass;
                Log.Write("booster: " + game.ProcessName + " priority " + before + " → " + after);
                if (after == ProcessPriorityClass.High || after == ProcessPriorityClass.RealTime) priorityLost.Remove(key);
                else priorityLost.Add(key);
            } catch (Exception ex) {
                if (first || now >= next) Issue("Game priority", ex);
                if (now >= next) nextPriorityAttempt[key] = now + PriorityRetryDelay;
                priorityLost.Add(key);
            }
        }

        void RestoreUnselectedGames(List<Process> games) {
            var selected = new HashSet<string>(games.Select(game => {
                try { return Priority + game.Id + "-" + game.StartTime.ToUniversalTime().Ticks; }
                catch (InvalidOperationException) { return ""; }
                catch (Win32Exception) { return ""; }
            }));
            foreach (var record in saved.Where(item => item.Key.StartsWith(Priority, StringComparison.Ordinal) && !selected.Contains(item.Key)).ToArray()) {
                if (RestoreOne(record.Key, record.Value)) {
                    Forget(record.Key);
                }
            }
            foreach (string key in nextPriorityAttempt.Keys.Where(key => !selected.Contains(key) && !saved.ContainsKey(key)).ToArray()) {
                nextPriorityAttempt.Remove(key);
                priorityLost.Remove(key);
            }
        }

        List<Process> FindGames(string[] paths) {
            var games = new List<Process>();
            foreach (string path in paths) {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))) {
                    bool keep = false;
                    try {
                        if (process.Id == self || process.SessionId != sessionId) continue;
                        var image = process.MainModule;
                        keep = image != null && string.Equals(image.FileName, path, StringComparison.OrdinalIgnoreCase);
                        if (keep) games.Add(process);
                    } catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    finally { if (!keep) process.Dispose(); }
                }
            }
            return games;
        }

        void RestoreChanges() {
            if (DateTime.UtcNow < nextRestore && !draining) return;
            bool errors = false;
            foreach (var record in saved.ToArray()) {
                try { if (RestoreOne(record.Key, record.Value)) Forget(record.Key); }
                catch (Exception ex) { errors = true; if (DateTime.UtcNow >= nextRestore) Issue("Restore " + record.Key, ex); }
            }
            nextRestore = errors ? DateTime.UtcNow.AddSeconds(10) : DateTime.MinValue;
        }

        bool RestoreOne(string key, string value) {
            if (key == Share) return BoostWindows.RestoreSharing();
            if (key == ShareConfig) {
                uint original = value[0] == '2' ? 2u : 3u;
                uint current = BoostWindows.SharingStartType();
                if (current == BoostWindows.SharingDisabled) {
                    BoostWindows.SetSharingStartType(original);
                    if (BoostWindows.SharingStartType() != original) return false;
                } else if (current != original) return true;
                if (value.EndsWith(",running", StringComparison.Ordinal) && !BoostWindows.RestoreSharing()) return false;
                Log.Write("booster: restored the Windows Server sharing configuration");
                return true;
            }
            if (key == Mode) {
                if (BoostWindows.ReadGameMode() == 0) BoostWindows.WriteGameMode(value == "absent" ? (int?)null : int.Parse(value));
                return true;
            }
            if (key == Capture || key == BackgroundCapture) {
                bool background = key == BackgroundCapture;
                if (BoostWindows.ReadGameCapture(background) == 0) {
                    int? original = value == "absent" ? (int?)null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    BoostWindows.WriteGameCapture(background, original);
                    return BoostWindows.ReadGameCapture(background) == original;
                }
                return true;
            }
            if (key == "power-mode") {
                string[] modes = value.Split(',');
                for (int i = 0; i < 2; i++) {
                    Guid original = Guid.Parse(modes[i]);
                    if (original != BoostWindows.BestPerformance && BoostWindows.ReadPowerMode(i == 1) == BoostWindows.BestPerformance)
                        BoostWindows.WritePowerMode(i == 1, original);
                }
                return true;
            }
            if (key == Park || key == Energy) {
                Guid setting = key == Park ? BoostWindows.Parking : BoostWindows.Efficiency;
                uint wanted = key == Park ? 100u : 0u;
                string[] parts = value.Split(',');
                Guid scheme = Guid.Parse(parts[0]);
                try {
                    uint ac = uint.Parse(parts[1]), dc = uint.Parse(parts[2]);
                    uint currentAc = BoostWindows.ReadPower(scheme, setting, false), currentDc = BoostWindows.ReadPower(scheme, setting, true);
                    if (ac != wanted && currentAc == wanted) BoostWindows.WritePower(scheme, setting, false, ac);
                    if (dc != wanted && currentDc == wanted) BoostWindows.WritePower(scheme, setting, true, dc);
                    if ((ac != wanted && (currentAc == wanted || currentAc == ac)) || (dc != wanted && (currentDc == wanted || currentDc == dc)))
                        BoostWindows.ApplyIfCurrent(scheme);
                } catch (Win32Exception ex) { if (ex.NativeErrorCode != 2 && ex.NativeErrorCode != 1168) throw; }
                return true;
            }
            if (!key.StartsWith(Priority, StringComparison.Ordinal)) return true;
            string[] identity = key.Substring(Priority.Length).Split('-');
            int pid;
            long started;
            ProcessPriorityClass before;
            if (identity.Length != 2 || !int.TryParse(identity[0], out pid) || !long.TryParse(identity[1], out started)
                || !Enum.TryParse(value, out before) || !Enum.IsDefined(typeof(ProcessPriorityClass), before) || before == ProcessPriorityClass.RealTime) return true;
            Process game;
            try { game = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }
            using (game) {
                if (!game.HasExited && game.SessionId == sessionId && game.StartTime.ToUniversalTime().Ticks == started
                    && game.PriorityClass == ProcessPriorityClass.High) game.PriorityClass = before;
            }
            return true;
        }

        void LoadJournal() {
            foreach (string line in journal.Read()) {
                int split = line.IndexOf('=');
                if (split > 0 && ValidRecord(line.Substring(0, split), line.Substring(split + 1))) saved[line.Substring(0, split)] = line.Substring(split + 1);
                else invalidRecovery = true;
            }
            if (invalidRecovery) { Log.Write("booster: invalid recovery records ignored; valid records retained"); SaveJournal(); }
            loaded = true;
        }

        static bool ValidRecord(string key, string value) {
            if (key == Share) return value == "1";
            if (key == ShareConfig) return value == "2,running" || value == "2,stopped" || value == "3,running" || value == "3,stopped";
            if (key == Mode) return value == "absent" || value == "1";
            if (key == Capture || key == BackgroundCapture) {
                int original;
                return value == "absent" || int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out original);
            }
            string[] parts = value.Split(',');
            Guid plan, other;
            if (key == "power-mode") return parts.Length == 2 && Guid.TryParse(parts[0], out plan) && Guid.TryParse(parts[1], out other);
            uint ac, dc;
            if (key == Park || key == Energy) return parts.Length == 3 && Guid.TryParse(parts[0], out plan) && plan != Guid.Empty
                && uint.TryParse(parts[1], out ac) && uint.TryParse(parts[2], out dc) && ac <= 100 && dc <= 100;
            ProcessPriorityClass priority;
            return key.StartsWith(Priority, StringComparison.Ordinal) && Enum.TryParse(value, out priority)
                && Enum.IsDefined(typeof(ProcessPriorityClass), priority) && priority != ProcessPriorityClass.RealTime;
        }

        void Remember(string key, string value) {
            saved.Add(key, value);
            try { SaveJournal(); } catch { saved.Remove(key); throw; }
        }

        void Forget(string key) {
            string value = saved[key];
            saved.Remove(key);
            try { SaveJournal(); } catch { saved.Add(key, value); throw; }
        }

        void SaveJournal() {
            journal.Write(saved.Select(item => item.Key + "=" + item.Value).ToArray());
        }

        void Try(string action, Action work) {
            try { work(); } catch (Exception ex) { failed++; Issue(action, ex); }
        }
        static void Issue(string action, Exception ex) { Log.Write("booster: " + action + ": " + ex.Message); }

        public void Dispose() {
            closing = true;
            timer.Dispose();
            input = new InputFilter();
            var cleanup = Task.Factory.StartNew(delegate {
                lock (gate) {
                    try { if (!loaded) LoadJournal(); End(); RestoreChanges(); }
                    catch (Exception ex) { Issue("Exit recovery", ex); }
                }
            });
            cleanup.Wait(1800);
        }
    }

    interface BoostJournal { string[] Read(); void Write(string[] records); }

    sealed class RegistryBoostJournal : BoostJournal {
        const string Root = @"SOFTWARE\OhmanPortableBooster\Recovery";
        readonly string account;

        public RegistryBoostJournal() { using (var who = WindowsIdentity.GetCurrent()) account = who.User.Value; }

        public string[] Read() {
            using (var key = Registry.LocalMachine.OpenSubKey(Root)) {
                return key == null ? new string[0] : key.GetValue(account) as string[] ?? new string[0];
            }
        }

        public void Write(string[] records) {
            if (records.Length == 0) {
                using (var key = Registry.LocalMachine.OpenSubKey(Root, true)) {
                    if (key != null) { key.DeleteValue(account, false); key.Flush(); }
                }
                return;
            }
            var security = new RegistrySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            using (var key = Registry.LocalMachine.CreateSubKey(Root, RegistryKeyPermissionCheck.ReadWriteSubTree, security)) {
                key.SetValue(account, records, RegistryValueKind.MultiString);
                key.Flush();
            }
        }
    }
}
