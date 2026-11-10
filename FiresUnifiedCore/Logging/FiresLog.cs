using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FiresCore.Logging
{
    // Per-mod tagged logger engine. Each consuming mod constructs one instance with its own
    // bracket tag and a verbose-enabled accessor, then exposes it through its own thin static
    // wrapper. Verbose lines gate on the accessor so diagnostic spam stays opt-in. Console
    // coloring and rate-limited suppression are handled centrally by FiresLogColorPatch and the
    // suppression patches, which key off the bracket tag this writes.
    // Every Info / Verbose line also passes FiresLogLevel ([Logging] Log Level); warnings and errors always print.
    public sealed class FiresLog
    {
        private readonly string _prefix;
        private readonly Func<bool> _verboseEnabled;

        public FiresLog(string modTag, Func<bool> verboseEnabled)
        {
            _prefix = $"[{modTag}]";
            _verboseEnabled = verboseEnabled ?? (() => false);
        }

        /// <summary>Verbose lines print: the level is Verbose or above, or it is Info and this mod's own verbose switch is on.</summary>
        public bool VerboseEnabled => FiresLogLevel.InfoOn && (FiresLogLevel.VerboseOn || ReadVerboseSafely());

        public void Info(string message)
        {
            if (!FiresLogLevel.InfoOn || FiresLogLevel.InfoRepeated(message)) return;
            UnityEngine.Debug.Log(Compose(message));
        }

        public void Verbose(string message)
        {
            if (!VerboseEnabled) return;
            UnityEngine.Debug.Log(Compose(message));
        }

        /// <summary>
        /// A per-action evidence line. It prints only when Core's [Logging] Log Level is Debug or Verbose, or its tag is in DebugTags. The rule (Fire 11-03: "we want to
        /// use the debug logs, but by default players do not need that they need warning/errors"): a default install shows warnings and errors; recurring lines belong here.
        /// 0.2.1122: written at BepInEx Debug level, no longer through Unity's log, which BepInEx records at Info. Same text and tags. So that Log Level = Debug keeps working:
        /// a stock BepInEx.cfg leaves Debug out of the disk log, so the line is written there directly (other mods' debug stays out), and tests on
        /// Application.logMessageReceived still hear it. The console shows Debug only if BepInEx.cfg's console LogLevels has it.
        /// </summary>
        public void Debug(string message)
        {
            // At Info, a tag named in [Logging] DebugTags (this mod's own tag or the message's leading tag) still prints.
            if (!FiresLogLevel.DebugOn && !FiresLogLevel.DebugForcedFor(_prefix, message)) return;
            string line = Compose(DebugMarker + message); string dropped = null;
            TellUnityListeners(line);   // first, and never limited: a test hears every line
            if (!FiresLogLevel.VerboseOn && !FiresLogLevel.IsForcedDebugTag(LeadingTag(message)) && (LeadingTag(message) ?? "").Trim('[', ']') != "SnowFlash" /* 0.2.1130: the probe needs every frame */ && !PassesFloodLimit(message, out dropped))
                return;
            if (_source == null) _source = BepInEx.Logging.Logger.CreateLogSource(_prefix.Trim('[', ']'));
            _source.LogDebug(line);
            foreach (var listener in BepInEx.Logging.Logger.Listeners)
                if (listener is DiskLogListener disk && (disk.DisplayedLogLevel & LogLevel.Debug) == 0)
                    disk.LogWriter?.WriteLine($"[Debug  :{_source.SourceName,10}] {line}");
            if (dropped != null) _source.LogDebug(Compose(DebugMarker + dropped));
        }

        // 0.2.1123 (Fire: "we should still limit flooding at debug level since we dont need the flood to read the log"): at most FloodLines lines of one shape (the tag + the line's
        // start, digits ignored) per FloodSeconds reach the log; the next one to pass says how many were dropped. Log Level = Verbose and a tag named in DebugTags see all.
        private const int FloodLines = 20, ShapeChars = 40, MaxShapes = 4096;
        private const float FloodSeconds = 10f;
        private sealed class Shape { public int Window, Count, Dropped; }
        private static readonly System.Collections.Generic.Dictionary<string, Shape> s_shapes = new System.Collections.Generic.Dictionary<string, Shape>(StringComparer.Ordinal);
        private static int s_floodDropped;
        /// <summary>Debug lines the flood limit kept out of the log since the game started (Core's status box shows it).</summary>
        public static int FloodDropped => s_floodDropped;

        private bool PassesFloodLimit(string message, out string dropped)
        {
            dropped = null;
            var sb = new System.Text.StringBuilder(ShapeChars + _prefix.Length).Append(_prefix);
            for (int i = 0, n = 0; i < message.Length && n < ShapeChars; i++) if (!char.IsDigit(message[i])) { sb.Append(message[i]); n++; }
            string key = sb.ToString();
            int window = (int)(Environment.TickCount / (int)(FloodSeconds * 1000f));
            lock (s_shapes)
            {
                if (!s_shapes.TryGetValue(key, out Shape shape))
                {
                    if (s_shapes.Count >= MaxShapes) s_shapes.Clear();
                    s_shapes[key] = shape = new Shape { Window = window };
                }
                if (shape.Window != window) { shape.Window = window; shape.Count = 0; }
                if (++shape.Count > FloodLines) { shape.Dropped++; s_floodDropped++; return false; }
                if (shape.Dropped > 0 && shape.Count == 1) { dropped = $"[LogFlood] {shape.Dropped} more line(s) like '{key}' dropped (limit {FloodLines} per {FloodSeconds:0} s; Log Level = Verbose or [Logging] DebugTags shows them)"; shape.Dropped = 0; }
            }
            return true;
        }

        private static string LeadingTag(string message)
        {
            if (string.IsNullOrEmpty(message) || message[0] != '[') return null;
            int end = message.IndexOf(']');
            return end > 1 ? message.Substring(1, end - 1) : null;
        }

        private ManualLogSource _source;
        private const string DebugMarker = "[Debug] ";   // 0.2.963: marked, so filters and [LogCost] can tell Debug from Info

        // The delegates behind Application.logMessageReceived, minus BepInEx's own (it would write the line again at Info). Main thread only, as Unity itself calls them.
        private static readonly System.Reflection.FieldInfo s_unityListeners = typeof(Application).GetField("s_LogCallbackHandler", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        private static readonly int s_mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private static void TellUnityListeners(string line)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThread || !(s_unityListeners?.GetValue(null) is Delegate all)) return;
            foreach (Delegate d in all.GetInvocationList())
            {
                if (d.Method.DeclaringType?.Namespace?.StartsWith("BepInEx", StringComparison.Ordinal) == true) continue;
                try { ((Application.LogCallback)d)(line, "", LogType.Log); } catch { }
            }
        }

        public void Warning(string message) => UnityEngine.Debug.LogWarning(Compose(message));

        public void Error(string message) => UnityEngine.Debug.LogError(Compose(message));

        // Folded to the console's width here rather than at each call site, so every mod on this logger gets it and
        // no summary line has to be written short by hand. The tag stays on the first row for the colour patch, the
        // suppression patches and grep.
        private string Compose(string message)
            => _prefix + " " + ConsoleWrap.Fit(message, ConsoleWrap.UnityLogPrefixColumns + _prefix.Length + 1);

        private bool ReadVerboseSafely()
        {
            try { return _verboseEnabled(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// The Fires family's log levels, lowest to highest. Errors and warnings always print, whatever the level. Debug
    /// (every per-action evidence line) sits below Verbose (also each mod's verbose detail), so the rig can run at Debug without every
    /// mod's verbose flood. The config stores the name, so the renumbering changes no saved setting.
    /// </summary>
    public enum FiresLogVerbosity
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3,
        Verbose = 4,
    }

    /// <summary>
    /// One local key, [Logging] Log Level (default Warning),
    /// for every Fires-family mod. Three gates read it: the shared FiresLog engine (Core and the mods on it), Core's Unity log handler for
    /// raw Debug.Log lines from Fires-family assemblies (RateLimitedLogHandler), and BepInEx's own sources named for the family
    /// (BepInExLogSuppressionPatch). Below Info, info-level lines from the family are hidden; warnings, errors and F5 test lines always
    /// print. The finer switches (each mod's VerboseLogging, CompanionDebugLogging's switches) still choose WHAT prints above that; a
    /// BepInEx source named in PerModLogLevel keeps the level given there.
    /// </summary>
    public static class FiresLogLevel
    {
        private const string Section = "Logging", Key = "Log Level";
        private static ConfigEntry<FiresLogVerbosity> s_level;
        private static ManualLogSource s_announceTo;

        /// <summary>The level in force. Info until Core binds its config, so nothing is hidden before the player's choice is known.</summary>
        public static FiresLogVerbosity Current => s_level != null ? s_level.Value : FiresLogVerbosity.Info;

        /// <summary>Info-level lines from the family print.</summary>
        public static bool InfoOn => Current >= FiresLogVerbosity.Info;

        // 0.2.1170 (Fire: "our normal info logging would have a lot of info yes, but not be so verbose, debug logs remain for debugging only"): at Info, a line keeps printing until its shape (text with digits removed) has shown InfoRepeats times this session; further repeats are Debug only. One-time events and state changes stay; timer and per-frame lines stop on their own.
        private const int InfoRepeats = 3, InfoShapeChars = 48, InfoMaxShapes = 8192;
        private static readonly System.Collections.Generic.Dictionary<string, int> s_infoShapes = new System.Collections.Generic.Dictionary<string, int>();
        internal static bool InfoRepeated(string message)
        {
            if (DebugOn || Announcing || string.IsNullOrEmpty(message)) return false;
            var sb = new System.Text.StringBuilder(InfoShapeChars);
            foreach (char c in message) { if (char.IsDigit(c) || c == '.' || c == '-') continue; sb.Append(c); if (sb.Length >= InfoShapeChars) break; }
            string key = sb.ToString();
            lock (s_infoShapes)
            {
                if (s_infoShapes.Count >= InfoMaxShapes) s_infoShapes.Clear();
                s_infoShapes.TryGetValue(key, out int n);
                s_infoShapes[key] = n + 1;
                return n >= InfoRepeats;
            }
        }

        /// <summary>Per-action evidence lines print (FiresLog.Debug, and Core's chatty tags: see <see cref="IsDebugTag"/>).</summary>
        public static bool DebugOn => Current >= FiresLogVerbosity.Debug;

        /// <summary>Every verbose line prints, whatever each mod's own verbose switch says.</summary>
        public static bool VerboseOn => Current >= FiresLogVerbosity.Verbose;

        /// <summary>
        /// Core's per-action tags. A plain Debug.Log line from a Fires
        /// assembly that starts with one prints only at Debug (RateLimitedLogHandler), the same as FiresLog.Debug. Info keeps the
        /// one-time load summaries and rare events ([FiresUnifiedCore], [ShaderRebind], [Water], [Characters], [QuestProfileManager] ...).
        /// F5 test lines ("[XTest] ...") always print.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> DebugTags = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        { 
            "[Chore]", "[CompanionIdleBehavior]", "[CompanionOwnership]", "[PathWalker]", "[Climb]", "[Climbing]", "[CompanionRandomLoadout]",
            "[Sectors]", "[CompanionChatHelper]", "[CompanionKennel]", "[LootPickup]", "[CompanionController]", "[CompanionAI]",
            "[CombatAdvisor]", "[Fishing]", "[FishingBrain]", "[Speech]", "[CompanionConsumables]", "[GroupTactics]", "[NpcVisEquipment]",
            "[CompanionSkills]", "[ArcheryBrain]", "[PlayerFollowingRegistry]", "[NpcSkeleton]", "[Archetype]", "[CompanionTeleportService]",
            "[CompanionInteractionBehavior]", "[Farming]", "[SmelterOperator]", "[ResourceGathering]", "[FireTending]", "[Swimming]",
            "[Party]", "[HomeBase]", "[EnemyMemory]", "[Danger]", "[ChoreBrain]", "[MovementAuthority]", "[WeaponSwapManager]",
            "[TauntShockwave]", "[BowTraining]", "[GroupHud]",
        };

        internal static bool IsDebugTag(string tag) => tag != null && DebugTags.Contains(tag);

        // [Logging] DebugTags. At Info, debug lines carrying a listed tag still print, so a
        // diagnosis (or a test that reads debug lines) raises one tag instead of the whole family. Default empty.
        private static ConfigEntry<string> s_forcedTagsEntry;
        private static volatile System.Collections.Generic.HashSet<string> s_forcedTags =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when a tag ("Orbitals" or "[Orbitals]") is named in [Logging] DebugTags.</summary>
        public static bool IsForcedDebugTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            var set = s_forcedTags;
            return set.Count > 0 && set.Contains(tag.Trim().Trim('[', ']'));
        }

        /// <summary>At Info or above: the mod's own tag or the message's leading [Tag] is named in DebugTags.</summary>
        public static bool DebugForcedFor(string modPrefix, string message)
        {
            if (s_forcedTags.Count == 0 || !InfoOn) return false;
            if (IsForcedDebugTag(modPrefix)) return true;
            if (string.IsNullOrEmpty(message) || message[0] != '[') return false;
            int end = message.IndexOf(']');
            return end > 1 && IsForcedDebugTag(message.Substring(1, end - 1));
        }

        private static void ParseForcedTags()
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string raw = s_forcedTagsEntry?.Value ?? "";
            foreach (string part in raw.Split(','))
            {
                string tag = part.Trim().Trim('[', ']').Trim();
                if (tag.Length > 0) set.Add(tag);
            }
            s_forcedTags = set;
        }

        /// <summary>The per-action tags, for the load line and the docs.</summary>
        public static System.Collections.Generic.IEnumerable<string> DebugTagList => DebugTags;

        /// <summary>True only while the load line below is written, so the gates let it through.</summary>
        internal static bool Announcing { get; private set; }

        // Core binds this from its own config file at setup; nothing else should.
        internal static void Bind(ConfigFile config)
        {
            if (config == null || s_level != null) return;
            s_level = config.Bind(Section, Key, FiresLogVerbosity.Warning, new ConfigDescription(
                "How much the Fires mods write to the log. Error and Warning: only warnings and errors. Info: also load summaries "
                + "and rare events. Debug: also every per-action line (what companions and bots did, step by step). Verbose: also "
                + "each mod's verbose detail. Errors and warnings always print. A local setting: the server does not set it for you."));
            s_level.SettingChanged += (_, __) => Announce(s_announceTo);
            s_forcedTagsEntry = config.Bind(Section, "DebugTags", "", new ConfigDescription(
                "Comma list of log tags (for example Orbitals, Mounts, CompanionAI) whose debug lines still print when Log Level is "
                + "Info. Turn on one tag to diagnose it instead of setting the whole family to Debug. Empty: none. A local setting."));
            ParseForcedTags();
            s_forcedTagsEntry.SettingChanged += (_, __) => { ParseForcedTags(); Announce(s_announceTo); };
        }

        /// <summary>The once-per-load line (and again when the level changes) saying the level and how to turn it up; it always prints.</summary>
        internal static void Announce(ManualLogSource log)
        {
            if (log == null) return;
            s_announceTo = log;
            string what = VerboseOn ? "info, debug and verbose lines from Fires mods shown"
                : DebugOn ? "info and debug lines from Fires mods shown; Verbose adds each mod's verbose detail"
                : InfoOn ? "info lines from Fires mods shown; Debug adds the per-action lines"
                : $"info lines from Fires mods hidden; set [{Section}] {Key} = Info to see them";
            Announcing = true;
            if (!DebugOn && InfoOn && s_forcedTags.Count > 0) what += "; debug lines also shown for " + string.Join(", ", s_forcedTags);
            try { log.LogMessage($"[FiresUnifiedCore] {Key}: {Current} ({what})"); }
            catch { }
            finally { Announcing = false; }
        }
    }
}
