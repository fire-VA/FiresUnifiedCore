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
    // 0.2.269: every Info / Verbose line also passes FiresLogLevel ([Logging] Log Level); warnings and errors always print.
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
            Debug.Log(Compose(message));
        }

        public void Verbose(string message)
        {
            if (!VerboseEnabled) return;
            Debug.Log(Compose(message));
        }

        public void Warning(string message) => Debug.LogWarning(Compose(message));

        public void Error(string message) => Debug.LogError(Compose(message));

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

    /// <summary>0.2.269: the Fires family's log levels, lowest to highest. Errors and warnings always print, whatever the level.</summary>
    public enum FiresLogVerbosity
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Verbose = 3,
        Debug = 4,
    }

    /// <summary>
    /// 0.2.269 (Fire: "Core has no way to adjust its logging level in the config"): one local key, [Logging] Log Level (default Warning),
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

        /// <summary>Every verbose line prints, whatever each mod's own verbose switch says.</summary>
        public static bool VerboseOn => Current >= FiresLogVerbosity.Verbose;

        /// <summary>True only while the load line below is written, so the gates let it through.</summary>
        internal static bool Announcing { get; private set; }

        // Core binds this from its own config file at setup; nothing else should.
        internal static void Bind(ConfigFile config)
        {
            if (config == null || s_level != null) return;
            s_level = config.Bind(Section, Key, FiresLogVerbosity.Warning, new ConfigDescription(
                "How much the Fires mods write to the log. Error and Warning: only warnings and errors. Info: also each mod's "
                + "info lines (what companions and bots did, load summaries). Verbose / Debug: also every verbose line. "
                + "Errors and warnings always print. A local setting: the server does not set it for you."));
            s_level.SettingChanged += (_, __) => Announce(s_announceTo);
        }

        /// <summary>The once-per-load line (and again when the level changes) saying the level and how to turn it up; it always prints.</summary>
        internal static void Announce(ManualLogSource log)
        {
            if (log == null) return;
            s_announceTo = log;
            string what = InfoOn
                ? (VerboseOn ? "info and verbose lines from Fires mods shown" : "info lines from Fires mods shown; Verbose adds the verbose ones")
                : $"info lines from Fires mods hidden; set [{Section}] {Key} = Info to see them";
            Announcing = true;
            try { log.LogMessage($"[FiresUnifiedCore] {Key}: {Current} ({what})"); }
            catch { }
            finally { Announcing = false; }
        }
    }
}
