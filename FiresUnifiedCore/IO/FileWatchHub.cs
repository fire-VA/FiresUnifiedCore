using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace FiresCore.IO
{
    // Unity's Mono has no native FileSystemWatcher on Windows. Every watcher is served by System.IO.DefaultWatcher: one
    // thread that wakes every 750 ms and re-lists each watcher's folder, and with IncludeSubdirectories every folder
    // under it, allocating paths for every entry. A dozen mods watching BepInEx\config that way kept a thread busy and
    // fed the GC all session. The hub takes every watcher off DefaultWatcher at StartDispatching / StopDispatching and
    // serves them from Windows change notifications: one handle per watched folder, nothing polled. Changes to a file
    // are collapsed the way a poll would have seen them (added, removed or modified) and delivered through Mono's own
    // DefaultWatcher.DispatchEvents, one check interval after the first change.
    public static class FileWatchHub
    {
        private const string DefaultWatcherTypeName = "System.IO.DefaultWatcher";
        private const string FileActionTypeName = "System.IO.FileAction";
        private const int ActionNone = 0;
        private const int ActionAdded = 1;
        private const int ActionRemoved = 2;
        private const int ActionModified = 3;
        private const int ActionRenamedOld = 4;
        private const int ActionRenamedNew = 5;
        private const int MillisecondsPerSecond = 1000;
        private const float MinimumIntervalSeconds = 0.1f;
        private const int HandlerErrorsLogged = 3;
        private const long MaxFingerprintBytes = 4L * 1024 * 1024;
        private const int FingerprintBufferBytes = 64 * 1024;
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;
        private const string StartedBeforeCore = "started before Core";
        private const string UnknownOwner = "unknown";

        private static readonly string[] FrameworkAssemblyPrefixes = { "mscorlib", "System", "0Harmony", "MonoMod", "Mono." };

        private sealed class Registration
        {
            public FileSystemWatcher Watcher;
            public string Directory;
            public string ExactName;
            public bool Recursive;
            public Func<string, bool> IsMatch;
            public string Owner;
            public bool Overflowed;
            public int HandlerErrors;
            public readonly Dictionary<string, bool> Exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Change> Pending = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Order = new List<string>();
            public readonly Dictionary<string, ContentFingerprint> DeliveredContent = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        }

        private struct Change
        {
            public bool ExistedBefore;
            public bool ExistsAfter;
        }

        private struct ContentFingerprint
        {
            public long Length;
            public ulong Hash;

            public bool SameAs(ContentFingerprint other) => Length == other.Length && Hash == other.Hash;
        }

        private struct Delivery
        {
            public Registration Registration;
            public int Action;
            public string Name;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<FileSystemWatcher, Registration> Registrations = new Dictionary<FileSystemWatcher, Registration>();
        private static readonly Dictionary<string, List<Registration>> ByDirectory = new Dictionary<string, List<Registration>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DirectoryChangeWatch> Watches = new Dictionary<string, DirectoryChangeWatch>(StringComparer.OrdinalIgnoreCase);

        // A folder inside a recursively watched folder needs no handle of its own: the ancestor's notification already
        // carries its events. This maps each folder that DOES hold a handle to the watched folders underneath it that
        // ride on it, so OnChange can re-base a path and hand it to those registrations as if their own watch saw it.
        // Without this the stack opened one handle - and one thread - per folder even when one already covered twenty.
        private static readonly Dictionary<string, List<string>> CoveredByRoot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly AutoResetEvent ChangeSignal = new AutoResetEvent(false);
        private static readonly byte[] FingerprintBuffer = new byte[FingerprintBufferBytes];

        private static MethodInfo _dispatch;
        private static MethodInfo _dispatchError;
        private static MethodInfo _fullPath;
        private static MethodInfo _pattern;
        private static MethodInfo _mangledFilter;
        private static MethodInfo _isMatch;
        private static MethodInfo _hasWildcard;
        private static Type _fileAction;
        private static Action<string> _log = _ => { };
        private static Action<string> _warn = _ => { };
        private static bool _installed;
        private static bool _enabled = true;
        private static bool _foldIntoConfigRoot;
        private static float _intervalSeconds = 2f;
        private static long _delivered;
        private static long _unchangedRewrites;

        public static bool Installed => _installed;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                lock (Gate)
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    if (!value) ClearPending();
                    RebuildWatches();
                }
            }
        }

        public static float IntervalSeconds
        {
            get => _intervalSeconds;
            set => _intervalSeconds = Math.Max(MinimumIntervalSeconds, value);
        }

        // Watches the whole config folder from one recursive handle instead of one per watched subfolder.
        // At rest this is free either way - a notification handle is event-driven, so an idle 4 GB tree costs
        // exactly what an idle 12 MB one does. It only differs while something is WRITING a lot inside the
        // tree: every write anywhere under config then has to be decoded and matched, and a burst big enough
        // to fill the 64 KB notification buffer overflows it, which forces a refresh on every watcher riding
        // that handle rather than only the ones near the churn.
        public static bool FoldIntoConfigRoot
        {
            get => _foldIntoConfigRoot;
            set
            {
                lock (Gate)
                {
                    if (_foldIntoConfigRoot == value) return;
                    _foldIntoConfigRoot = value;
                    RebuildWatches();
                }
            }
        }

        public static bool Install(Harmony harmony, Action<string> log, Action<string> warn)
        {
            if (_installed) return true;
            if (log != null) _log = log;
            if (warn != null) _warn = warn;
            Assembly system = typeof(FileSystemWatcher).Assembly;
            Type defaultWatcher = system.GetType(DefaultWatcherTypeName);
            _fileAction = system.GetType(FileActionTypeName);
            MethodInfo start = defaultWatcher == null ? null : AccessTools.Method(defaultWatcher, "StartDispatching");
            MethodInfo stop = defaultWatcher == null ? null : AccessTools.Method(defaultWatcher, "StopDispatching");
            FieldInfo watches = defaultWatcher == null ? null : AccessTools.Field(defaultWatcher, "watches");
            _dispatch = defaultWatcher == null ? null : AccessTools.Method(defaultWatcher, "DispatchEvents");
            _dispatchError = AccessTools.Method(typeof(FileSystemWatcher), "DispatchErrorEvents");
            _fullPath = AccessTools.PropertyGetter(typeof(FileSystemWatcher), "FullPath");
            _pattern = AccessTools.PropertyGetter(typeof(FileSystemWatcher), "Pattern");
            _mangledFilter = AccessTools.PropertyGetter(typeof(FileSystemWatcher), "MangledFilter");
            _isMatch = _pattern == null ? null : AccessTools.Method(_pattern.ReturnType, "IsMatch", new[] { typeof(string) });
            _hasWildcard = _pattern == null ? null : AccessTools.PropertyGetter(_pattern.ReturnType, "HasWildcard");
            if (start == null || stop == null || watches == null || _dispatch == null || _dispatchError == null || _fileAction == null ||
                _fullPath == null || _pattern == null || _mangledFilter == null || _isMatch == null || _hasWildcard == null)
            {
                _warn("file watching left to Mono: this runtime's FileSystemWatcher internals are not the ones the hub expects");
                return false;
            }

            try
            {
                harmony.Patch(start, prefix: new HarmonyMethod(AccessTools.Method(typeof(FileWatchHub), nameof(StartDispatchingPrefix))));
                harmony.Patch(stop, prefix: new HarmonyMethod(AccessTools.Method(typeof(FileWatchHub), nameof(StopDispatchingPrefix))));
            }
            catch (Exception ex)
            {
                _warn($"file watching left to Mono: patching DefaultWatcher failed: {ex.Message}");
                return false;
            }
            if (!HasOurPrefix(start) || !HasOurPrefix(stop))
            {
                _warn("file watching left to Mono: the DefaultWatcher patches did not attach");
                return false;
            }
            _installed = true;
            new Thread(DeliverLoop) { Name = "Fires file-watch hub", IsBackground = true }.Start();
            int takenOver = TakeOverRunningWatchers(watches);
            _log($"serving every FileSystemWatcher from change notifications ({takenOver} taken over from Mono's poller)");
            return true;
        }

        public static string StatusLine()
        {
            lock (Gate)
            {
                if (!_installed) return null;
                if (!_enabled) return $"off: {Registrations.Count} watchers get no changes";
                int recursive = Watches.Values.Count(watch => watch.Recursive);
                int covered = CoveredByRoot.Values.Sum(list => list.Count);
                long unchanged = Interlocked.Read(ref _unchangedRewrites);
                return $"{Registrations.Count} watchers on {Watches.Count} folders ({recursive} with subfolders" +
                       (covered > 0 ? $", {covered} folded into a parent" : string.Empty) + "), " +
                       $"{Interlocked.Read(ref _delivered)} changes delivered" +
                       (unchanged > 0 ? $", {unchanged} unchanged {(unchanged == 1 ? "rewrite" : "rewrites")} held back" : string.Empty) +
                       $", every {_intervalSeconds:0.##} s";
            }
        }

        public static List<string> Describe()
        {
            lock (Gate)
            {
                return Registrations.Values
                    .OrderBy(registration => registration.Directory, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(registration => registration.Owner, StringComparer.OrdinalIgnoreCase)
                    .Select(registration => $"{registration.Owner}: {Path.Combine(registration.Directory, registration.ExactName ?? registration.Watcher.Filter)}" +
                                            (registration.Recursive ? " (and subfolders)" : string.Empty))
                    .ToList();
            }
        }

        private static bool HasOurPrefix(MethodBase original) =>
            Harmony.GetPatchInfo(original)?.Prefixes.Any(patch => patch.PatchMethod.DeclaringType == typeof(FileWatchHub)) == true;

        // A registration failure hands the watcher back to Mono rather than leaving it deaf.
        private static bool StartDispatchingPrefix(object handle)
        {
            if (!(handle is FileSystemWatcher watcher)) return true;
            try
            {
                Register(watcher, OwnerOf(new StackTrace(false)));
                return false;
            }
            catch (Exception ex)
            {
                _warn($"left a watcher on {watcher.Path} to Mono: {ex.Message}");
                return true;
            }
        }

        private static bool StopDispatchingPrefix(object handle)
        {
            if (!(handle is FileSystemWatcher watcher)) return true;
            lock (Gate)
            {
                if (!RemoveLocked(watcher)) return true;
                RebuildWatches();
                return false;
            }
        }

        // Watchers a plugin started before Core loaded are already on Mono's poller. Each leaves Mono's table only once the
        // hub holds it, and the poller's thread ends by itself when the table is empty.
        private static int TakeOverRunningWatchers(FieldInfo watchesField)
        {
            if (!(watchesField.GetValue(null) is Hashtable watches)) return 0;
            List<FileSystemWatcher> running;
            lock (watches)
            {
                running = watches.Keys.OfType<FileSystemWatcher>().ToList();
            }
            int takenOver = 0;
            foreach (FileSystemWatcher watcher in running)
            {
                try
                {
                    Register(watcher, StartedBeforeCore);
                }
                catch (Exception ex)
                {
                    _warn($"left a watcher on {watcher.Path} to Mono: {ex.Message}");
                    continue;
                }
                lock (watches)
                {
                    watches.Remove(watcher);
                }
                takenOver++;
            }
            return takenOver;
        }

        // As in Mono, a filter without wildcards names one file directly in the watched folder, even with IncludeSubdirectories.
        private static void Register(FileSystemWatcher watcher, string owner)
        {
            object pattern = _pattern.Invoke(watcher, null);
            bool wildcard = (bool)_hasWildcard.Invoke(pattern, null);
            string directory = Normalize((string)_fullPath.Invoke(watcher, null));
            var registration = new Registration
            {
                Watcher = watcher,
                Directory = directory,
                ExactName = wildcard ? null : (string)_mangledFilter.Invoke(watcher, null),
                Recursive = wildcard && watcher.IncludeSubdirectories,
                IsMatch = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), pattern, _isMatch),
                Owner = owner,
            };
            if (registration.ExactName != null) registration.Exists[registration.ExactName] = ExistsOnDisk(directory, registration.ExactName);

            lock (Gate)
            {
                RemoveLocked(watcher);
                Registrations[watcher] = registration;
                if (!ByDirectory.TryGetValue(directory, out List<Registration> list)) ByDirectory[directory] = list = new List<Registration>();
                list.Add(registration);
                RebuildWatches();
            }
        }

        private static bool RemoveLocked(FileSystemWatcher watcher)
        {
            if (!Registrations.TryGetValue(watcher, out Registration registration)) return false;
            Registrations.Remove(watcher);
            if (ByDirectory.TryGetValue(registration.Directory, out List<Registration> list))
            {
                list.Remove(registration);
                if (list.Count == 0) ByDirectory.Remove(registration.Directory);
            }
            return true;
        }

        private static void RebuildWatches()
        {
            var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (_enabled)
                foreach (KeyValuePair<string, List<Registration>> folder in ByDirectory)
                    wanted[folder.Key] = folder.Value.Any(registration => registration.Recursive);

            if (_enabled) AddConfigRootFold(wanted);

            // Anything inside a recursively watched folder rides on that folder's handle instead of opening one.
            var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            CoveredByRoot.Clear();
            foreach (KeyValuePair<string, bool> folder in wanted)
            {
                string root = RecursiveAncestorOf(folder.Key, wanted);
                if (root == null) { roots[folder.Key] = folder.Value; continue; }
                if (!CoveredByRoot.TryGetValue(root, out List<string> covered)) CoveredByRoot[root] = covered = new List<string>();
                covered.Add(folder.Key);
            }

            foreach (string directory in Watches.Keys.ToList())
            {
                if (roots.TryGetValue(directory, out bool recursive) && recursive == Watches[directory].Recursive) continue;
                Watches[directory].Stop();
                Watches.Remove(directory);
            }
            foreach (KeyValuePair<string, bool> folder in roots)
            {
                if (Watches.ContainsKey(folder.Key)) continue;
                DirectoryChangeWatch watch = DirectoryChangeWatch.Start(folder.Key, folder.Value, OnChange, OnOverflow,
                    message => WarnOnce(folder.Key, message));
                if (watch != null) Watches[folder.Key] = watch;
            }
        }

        // Marks the config folder itself as a recursive watch so the subsumption below folds every watched
        // folder underneath it into that one handle. Only does anything when something under config is already
        // watched - it never opens a handle nobody wanted. The root is usually in `wanted` already, because
        // most mods watch it with an exact filename filter for their own .cfg; those registrations are
        // unaffected, since Matches() rejects a subfolder path for a non-recursive registration.
        private static void AddConfigRootFold(Dictionary<string, bool> wanted)
        {
            if (!_foldIntoConfigRoot) return;

            string root;
            try { root = Normalize(BepInEx.Paths.ConfigPath); }
            catch { return; }
            if (string.IsNullOrEmpty(root)) return;

            if (!wanted.ContainsKey(root))
            {
                bool anythingUnder = false;
                foreach (string directory in wanted.Keys)
                    if (IsUnder(directory, root)) { anythingUnder = true; break; }
                if (!anythingUnder) return;
            }

            wanted[root] = true;
        }

        // The nearest watched ancestor that is recursive, or null when nothing above this folder covers it. Walking up
        // rather than comparing every pair keeps this linear in path depth, and taking the NEAREST one means a covered
        // folder is always re-based against a handle that really exists.
        private static string RecursiveAncestorOf(string directory, Dictionary<string, bool> wanted)
        {
            for (string parent = ParentOf(directory); parent != null; parent = ParentOf(parent))
                if (wanted.TryGetValue(parent, out bool recursive) && recursive)
                    return parent;
            return null;
        }

        private static string ParentOf(string directory)
        {
            try
            {
                string parent = Path.GetDirectoryName(directory);
                return string.IsNullOrEmpty(parent) || parent.Length >= directory.Length ? null : Normalize(parent);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUnder(string child, string parent)
        {
            if (child.Length <= parent.Length || !child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
            return parent[parent.Length - 1] == Path.DirectorySeparatorChar || child[parent.Length] == Path.DirectorySeparatorChar;
        }

        // The part of an absolute child path that follows its parent, with no leading separator.
        private static string RelativeSegment(string child, string parent) =>
            child.Substring(parent[parent.Length - 1] == Path.DirectorySeparatorChar ? parent.Length : parent.Length + 1);

        private static void OnChange(DirectoryChangeWatch watch, int action, string relativePath)
        {
            lock (Gate)
            {
                if (!IsCurrent(watch)) return;
                bool noted = DispatchTo(watch.Directory, relativePath, action);

                // The same notification also belongs to any watched folder underneath this one, re-based so each of
                // those registrations sees the path relative to its OWN folder - which is what everything downstream,
                // from Matches to the Pending keys to the fingerprint's Path.Combine, already assumes.
                if (CoveredByRoot.TryGetValue(watch.Directory, out List<string> covered))
                {
                    string absolute = Path.Combine(watch.Directory, relativePath);
                    foreach (string directory in covered)
                        if (IsUnder(absolute, directory))
                            noted |= DispatchTo(directory, RelativeSegment(absolute, directory), action);
                }
                if (noted) ChangeSignal.Set();
            }
        }

        private static bool DispatchTo(string directory, string relativePath, int action)
        {
            if (!ByDirectory.TryGetValue(directory, out List<Registration> list)) return false;
            bool noted = false;
            foreach (Registration registration in list)
            {
                if (!Matches(registration, relativePath)) continue;
                Note(registration, relativePath, action);
                noted = true;
            }
            return noted;
        }

        private static void OnOverflow(DirectoryChangeWatch watch)
        {
            lock (Gate)
            {
                if (!IsCurrent(watch)) return;
                // A lost batch on this handle is lost for everything riding on it, so every covered folder refreshes too.
                bool any = MarkOverflowed(watch.Directory);
                if (CoveredByRoot.TryGetValue(watch.Directory, out List<string> covered))
                    foreach (string directory in covered) any |= MarkOverflowed(directory);
                if (any) ChangeSignal.Set();
            }
            WarnOnce("overflow " + watch.Directory, $"more changes in {watch.Directory} at once than one notification holds; its watchers get a refresh");
        }

        private static bool MarkOverflowed(string directory)
        {
            if (!ByDirectory.TryGetValue(directory, out List<Registration> list) || list.Count == 0) return false;
            foreach (Registration registration in list) registration.Overflowed = true;
            return true;
        }

        private static bool IsCurrent(DirectoryChangeWatch watch) =>
            _enabled && Watches.TryGetValue(watch.Directory, out DirectoryChangeWatch current) && current == watch;

        private static bool Matches(Registration registration, string relativePath)
        {
            if (registration.ExactName != null)
                return string.Equals(relativePath, registration.ExactName, StringComparison.OrdinalIgnoreCase);
            int separator = relativePath.LastIndexOf(Path.DirectorySeparatorChar);
            if (separator >= 0 && !registration.Recursive) return false;
            return registration.IsMatch(separator >= 0 ? relativePath.Substring(separator + 1) : relativePath);
        }

        // The first notification in a batch fixes whether the file existed before it; the last one whether it exists after.
        private static void Note(Registration registration, string name, int action)
        {
            bool existsAfter = action == ActionAdded || action == ActionModified || action == ActionRenamedNew;
            if (!registration.Pending.TryGetValue(name, out Change change))
            {
                change.ExistedBefore = registration.Exists.TryGetValue(name, out bool known)
                    ? known
                    : action == ActionModified || action == ActionRemoved || action == ActionRenamedOld;
                registration.Order.Add(name);
            }
            change.ExistsAfter = existsAfter;
            registration.Pending[name] = change;
        }

        private static void DeliverLoop()
        {
            var batch = new List<Delivery>();
            var fingerprints = new Dictionary<string, ContentFingerprint?>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                ChangeSignal.WaitOne();
                Thread.Sleep((int)(_intervalSeconds * MillisecondsPerSecond));
                batch.Clear();
                lock (Gate)
                {
                    if (_enabled) TakePending(batch);
                    else ClearPending();
                }
                fingerprints.Clear();
                foreach (Delivery delivery in batch)
                    if (!IsUnchangedRewrite(delivery, fingerprints)) Deliver(delivery);
            }
        }

        // A file rewritten with the bytes a watcher was last told about is held back from that watcher. Mods that save their
        // config after every reload (Azumatt's template) would otherwise hear their own save one interval later and reload
        // forever; Mono's 750 ms poll landed that echo inside their 1 s reload guard, the hub's interval does not.
        private static bool IsUnchangedRewrite(Delivery delivery, Dictionary<string, ContentFingerprint?> fingerprints)
        {
            if (delivery.Name == null) return false;
            Dictionary<string, ContentFingerprint> delivered = delivery.Registration.DeliveredContent;
            if (delivery.Action == ActionRemoved)
            {
                delivered.Remove(delivery.Name);
                return false;
            }
            string path = Path.Combine(delivery.Registration.Directory, delivery.Name);
            if (!fingerprints.TryGetValue(path, out ContentFingerprint? current))
                fingerprints[path] = current = FingerprintOf(path);
            if (current == null)
            {
                delivered.Remove(delivery.Name);
                return false;
            }
            bool unchanged = delivery.Action == ActionModified &&
                             delivered.TryGetValue(delivery.Name, out ContentFingerprint previous) && previous.SameAs(current.Value);
            delivered[delivery.Name] = current.Value;
            if (unchanged) Interlocked.Increment(ref _unchangedRewrites);
            return unchanged;
        }

        private static ContentFingerprint? FingerprintOf(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxFingerprintBytes) return null;
                    ulong hash = FnvOffsetBasis;
                    int read;
                    while ((read = stream.Read(FingerprintBuffer, 0, FingerprintBuffer.Length)) > 0)
                        for (int i = 0; i < read; i++) hash = (hash ^ FingerprintBuffer[i]) * FnvPrime;
                    return new ContentFingerprint { Length = stream.Length, Hash = hash };
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TakePending(List<Delivery> batch)
        {
            foreach (Registration registration in Registrations.Values)
            {
                if (registration.Overflowed) Refresh(registration, batch);
                foreach (string name in registration.Order)
                {
                    Change change = registration.Pending[name];
                    registration.Exists[name] = change.ExistsAfter;
                    int action = change.ExistedBefore
                        ? (change.ExistsAfter ? ActionModified : ActionRemoved)
                        : (change.ExistsAfter ? ActionAdded : ActionNone);
                    if (action != ActionNone) batch.Add(new Delivery { Registration = registration, Action = action, Name = name });
                }
                registration.Order.Clear();
                registration.Pending.Clear();
            }
        }

        // After an overflow a one-file watcher gets its file's state checked on disk; a wildcard watcher gets the error event
        // FileSystemWatcher raises for a lost batch.
        private static void Refresh(Registration registration, List<Delivery> batch)
        {
            registration.Overflowed = false;
            if (registration.ExactName == null)
            {
                batch.Add(new Delivery { Registration = registration, Action = ActionNone, Name = null });
                return;
            }
            if (registration.Pending.ContainsKey(registration.ExactName)) return;
            bool existed = registration.Exists.TryGetValue(registration.ExactName, out bool known) && known;
            bool exists = ExistsOnDisk(registration.Directory, registration.ExactName);
            registration.Exists[registration.ExactName] = exists;
            int action = existed ? (exists ? ActionModified : ActionRemoved) : (exists ? ActionAdded : ActionNone);
            if (action != ActionNone) batch.Add(new Delivery { Registration = registration, Action = action, Name = registration.ExactName });
        }

        private static void Deliver(Delivery delivery)
        {
            FileSystemWatcher watcher = delivery.Registration.Watcher;
            try
            {
                if (delivery.Name == null)
                    _dispatchError.Invoke(watcher, new object[] { new ErrorEventArgs(new InternalBufferOverflowException()) });
                else
                    _dispatch.Invoke(null, new object[] { watcher, Enum.ToObject(_fileAction, delivery.Action), delivery.Name });
                Interlocked.Increment(ref _delivered);
            }
            catch (TargetInvocationException ex)
            {
                if (delivery.Registration.HandlerErrors++ < HandlerErrorsLogged)
                    _warn($"a file-change handler from {delivery.Registration.Owner} threw: {ex.InnerException}");
            }
        }

        private static void ClearPending()
        {
            foreach (Registration registration in Registrations.Values)
            {
                registration.Order.Clear();
                registration.Pending.Clear();
                registration.Overflowed = false;
            }
        }

        private static void WarnOnce(string key, string message)
        {
            lock (Warned)
            {
                if (!Warned.Add(key)) return;
            }
            _warn(message);
        }

        private static bool ExistsOnDisk(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            return File.Exists(path) || System.IO.Directory.Exists(path);
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        private static string OwnerOf(StackTrace trace)
        {
            foreach (StackFrame frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
            {
                Type type = frame.GetMethod()?.DeclaringType;
                if (type == null || type == typeof(FileWatchHub)) continue;
                string assembly = type.Assembly.GetName().Name;
                if (FrameworkAssemblyPrefixes.Any(prefix => assembly.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                return $"{assembly} ({type.Name})";
            }
            return UnknownOwner;
        }
    }
}
