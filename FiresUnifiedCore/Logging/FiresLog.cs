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
            if (!FiresLogLevel.InfoOn) return;
            UnityEngine.Debug.Log(Compose(message));
        }

        public void Verbose(string message)
        {
            if (!VerboseEnabled) return;
            UnityEngine.Debug.Log(Compose(message));
        }

        /// <summary>
        /// Noisy info lines belong at debug level, visible when the config asks for
        /// them: a per-action evidence line. It prints only when Core's [Logging] Log Level is Debug or Verbose, and then through Unity's
        /// log, which BepInEx shows at Info: never ManualLogSource.LogDebug, which the default BepInEx LogLevels hide (turning those on
        /// would bring back every other mod's debug spam).
        /// </summary>
        public void Debug(string message)
        {
            // At Info, a tag named in [Logging] DebugTags (this mod's own tag or the message's leading tag) still prints.
            if (!FiresLogLevel.DebugOn && !FiresLogLevel.DebugForcedFor(_prefix, message)) return;
            UnityEngine.Debug.Log(Compose(message));
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
