using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Logging
{
    // Drops known-noise messages at BepInEx's Logger.InternalLogEvent, the fan-out every BepInEx-internal source
    // (HarmonyX, ManualLogSources, ConfigSync, the chainloader) goes through without passing Unity. Currently
    // the HarmonyX AccessTools "Could not find method/type" probe misses. Verbose mode lets everything through.
    [HarmonyPatch]
    internal static class BepInExLogSuppressionPatch
    {
        private const string LoggerTypeName       = "BepInEx.Logging.Logger";
        private const string BepInExAssemblyName  = "BepInEx";
        private const string InternalMethodName   = "InternalLogEvent";
        private const string HarmonySourceName    = "HarmonyX";
        private const string SummaryPrefix        = "[FiresUnifiedCore]";

        private const string MissingMethodFragment = "Could not find method";
        private const string MissingTypeFragment   = "Could not find type";

        // Checked for EVERY source, not just HarmonyX. Prefixing UnityLogSource.OnUnityLogMessageReceived did not
        // catch it (verified 2026-09-25: filter wired at log line 25, the error still printed at line 1519 and the
        // suppression counter never announced), and "[Error : Unity Log]" does not identify the source - Core's own
        // RateLimitedLogHandler owns a ManualLogSource of that very name. This fan-out is where every source meets.
        private const string EnsureSortedFragment = "EnsureSorted called before all files have been added";

        private const int HarmonySummaryInterval = 25;

        // Third-party quieting. Most mods ship no verbose switch, so their Info/Message chatter is all-or-nothing and
        // there is no way to turn it down without turning the mod off. This gives one from the outside: below the kept
        // level, a quieted source's lines are dropped at BepInEx's own fan-out, before any listener sees them.
        // Warnings and errors always survive the default, so silencing chatter never silences a real problem.
        // OFF unless the user turns it on - a mod's output is its author's to decide until someone says otherwise.
        private const string QuietSection      = "Logging";
        private const string QuietEnabledKey   = "QuietOtherMods";
        private const string QuietKeepLevelKey = "QuietOtherModsKeepLevel";
        private const string QuietSourcesKey   = "QuietOtherModsSources";
        private const string QuietExemptKey    = "QuietOtherModsExempt";
        private const string FiresSourcePrefix = "Fires";
        private const string DefaultExempt     = "BepInEx, Unity Log";

        private const string PerModLevelKey = "PerModLogLevel";

        /// <summary>
        /// The family members whose BepInEx source name does NOT start with "Fires", so the prefix test alone would
        /// quiet OUR OWN mods - the exact opposite of what QuietOtherMods is for.
        ///
        /// <para>Origin is the ship list, <c>Tools\Deploy-Fires.ps1</c>'s <c>$Manifest</c>, because that is the actual
        /// list of what we ship rather than a guess at it. These are the runtime <c>PluginName</c> values, which are
        /// NOT the manifest keys: VAGhettoNetworking logs as "FiresGhettoNetworkMod" (covered by the prefix),
        /// TechPriestDhakharsPrefabs as "TechPriestDhakharPieces", VAassets as "VerdantsAscentAssets" and
        /// VerdantsAscentShips as "VAShips". Add a new non-Fires-named mod here when it ships.</para>
        /// </summary>
        private static readonly string[] FamilySourceNames =
        {
            "VAInventory",
            "VABackpacks",
            "TechPriestDhakharPieces",
            "VerdantsAscentAssets",
            "VAShips",
            "IsThisThingOn",
            "Vedr",
        };

        // Fires F5 test lines ("[VedrTest] BEGIN ...", Tools\FIRES_F5_TESTS.md) are read by the rig's watcher and by
        // fires_test_all, so no rule here may drop one, whichever mod writes it: a third-party test cut off at its
        // BEGIN line reads as a failed run. The area name is letters only and short, so the search stops early.
        private const char TestMarkerOpen = '[';
        private const string TestMarkerClose = "Test] ";
        private const int TestMarkerSearchChars = 40;
        private const int MinTestMarkerClose = 2;

        private const string QuietEnabledDescription =
            "Drop low-level log lines from mods other than this family, for mods that ship no verbose switch of their "
            + "own. Warnings and errors still print at the default keep level. This machine only.";
        private const string QuietKeepLevelDescription =
            "Lines at this level and more severe are always kept. Fatal, Error, Warning, Message, Info, Debug.";
        private const string QuietSourcesDescription =
            "Comma-separated BepInEx source names to quiet. Empty quiets every source that is not part of this family "
            + "and is not exempt.";
        private const string QuietExemptDescription =
            "Comma-separated source names never quieted, whatever the settings above say.";
        private const string PerModLevelDescription =
            "Per-mod logging, independent of QuietOtherMods above. Comma-separated Name=Level pairs, where Name is the "
            + "mod's BepInEx source name as it appears in [brackets] in the log. Lines at that level and MORE SEVERE "
            + "are kept, the rest are dropped; None turns a mod off completely and Debug turns everything on. "
            + "An entry here DECIDES for that mod and overrides every other rule in this section, including the "
            + "HarmonyX probe filter - so it can turn noise back on as well as off. F5 test lines ([...Test] ...) always "
            + "print. Example: VAInventory=Debug, FiresValcast=None, HarmonyX=Debug. Applies live; no restart. This machine only.";

        private static ConfigEntry<bool>     _quietEnabled;
        private static ConfigEntry<LogLevel> _quietKeepLevel;
        private static ConfigEntry<string>   _quietSources;
        private static ConfigEntry<string>   _quietExempt;
        private static ConfigEntry<string>   _perModLevel;

        private static string[] _quietSourceList = Array.Empty<string>();
        private static string[] _quietExemptList = Array.Empty<string>();
        private static Dictionary<string, LogLevel> _perModLevels =
            new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        private static int _perModDroppedLines;

        private static int _suppressedHarmonyMissing;
        private static int _quietedOtherModLines;
        private static int _suppressedEnsureSorted;
        private static bool _diagnosticEmitted;
        private static bool _quietAnnounced;

        internal static int SuppressedHarmonyMissing => _suppressedHarmonyMissing;
        internal static int QuietedOtherModLines => _quietedOtherModLines;
        internal static int PerModDroppedLines => _perModDroppedLines;

        // Core binds these from its own config file at setup; nothing else should.
        internal static void BindConfig(ConfigFile config)
        {
            if (config == null) return;

            _quietEnabled   = config.Bind(QuietSection, QuietEnabledKey, false, QuietEnabledDescription);
            _quietKeepLevel = config.Bind(QuietSection, QuietKeepLevelKey, LogLevel.Warning, QuietKeepLevelDescription);
            _quietSources   = config.Bind(QuietSection, QuietSourcesKey, string.Empty, QuietSourcesDescription);
            _quietExempt    = config.Bind(QuietSection, QuietExemptKey, DefaultExempt, QuietExemptDescription);
            _perModLevel    = config.Bind(QuietSection, PerModLevelKey, string.Empty, PerModLevelDescription);

            RebuildLists();
            _quietSources.SettingChanged += (_, __) => RebuildLists();
            _quietExempt.SettingChanged  += (_, __) => RebuildLists();
            _perModLevel.SettingChanged  += (_, __) => RebuildLists();
        }

        private static void RebuildLists()
        {
            _quietSourceList = SplitNames(_quietSources != null ? _quietSources.Value : null);
            _quietExemptList = SplitNames(_quietExempt != null ? _quietExempt.Value : null);
            _perModLevels = ParsePerModLevels(_perModLevel != null ? _perModLevel.Value : null);
        }

        /// <summary>
        /// Reads the Name=Level pairs. A malformed entry is REPORTED rather than ignored - a setting that silently
        /// does nothing is worse than one that refuses, because the next hour goes on wondering why the mod is still
        /// logging.
        /// </summary>
        private static Dictionary<string, LogLevel> ParsePerModLevels(string raw)
        {
            var parsed = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return parsed;

            var rejected = new List<string>();
            foreach (string entry in SplitNames(raw))
            {
                int split = entry.IndexOf('=');
                string name = split > 0 ? entry.Substring(0, split).Trim() : null;
                string levelText = split > 0 ? entry.Substring(split + 1).Trim() : null;

                LogLevel level;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(levelText)
                    || !Enum.TryParse(levelText, true, out level))
                {
                    rejected.Add(entry);
                    continue;
                }
                parsed[name] = level;
            }

            if (rejected.Count > 0)
                Debug.LogWarning($"{SummaryPrefix} {PerModLevelKey}: ignored {rejected.Count} malformed entr"
                    + (rejected.Count == 1 ? "y" : "ies") + $" [{string.Join(", ", rejected.ToArray())}]. "
                    + "Each must be Name=Level, e.g. VAInventory=Warning. Levels: None, Fatal, Error, Warning, "
                    + "Message, Info, Debug, All.");

            return parsed;
        }

        private static string[] SplitNames(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            var parts = raw.Split(',');
            var kept = new System.Collections.Generic.List<string>(parts.Length);
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) kept.Add(trimmed);
            }
            return kept.ToArray();
        }

        private static bool Prepare()
        {
            var method = TargetMethod();
            if (method != null && !_diagnosticEmitted)
            {
                _diagnosticEmitted = true;
                Debug.Log($"{SummaryPrefix} BepInExLogSuppressionPatch wired to {LoggerTypeName}.{InternalMethodName} - HarmonyX noise will be filtered.");
            }
            else if (method == null && !_diagnosticEmitted)
            {
                _diagnosticEmitted = true;
                Debug.LogWarning($"{SummaryPrefix} BepInExLogSuppressionPatch could NOT locate {LoggerTypeName}.{InternalMethodName} - HarmonyX suppression disabled.");
            }
            return method != null;
        }

        private static MethodBase TargetMethod()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    if (asm.GetName().Name != BepInExAssemblyName) continue;

                    var type = asm.GetType(LoggerTypeName, throwOnError: false);
                    if (type == null) continue;

                    return type.GetMethod(
                        InternalMethodName,
                        BindingFlags.Static | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(object), typeof(LogEventArgs) },
                        modifiers: null);
                }
            }
            catch
            {
                // fall through to null
            }
            return null;
        }

        private static bool Prefix(LogEventArgs eventArgs)
        {
            try
            {
                if (VerbosePassThrough()) return true;
                if (eventArgs == null) return true;
                return !ShouldSuppress(eventArgs);
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldSuppress(LogEventArgs eventArgs)
        {
            var source = eventArgs.Source;
            if (source == null) return false;

            string sourceName = source.SourceName;
            if (string.IsNullOrEmpty(sourceName)) return false;
            if (IsTestLine(eventArgs.Data as string)) return false;

            // An explicit per-mod level DECIDES (F5 test lines aside), and nothing below gets a say. Someone who names a
            // mod has said what they want from it, so this can turn a mod's output back ON as readily as off - including
            // HarmonyX's probe warnings and vanilla's EnsureSorted spam, which the blanket rules further down would eat.
            LogLevel wanted;
            if (_perModLevels.TryGetValue(sourceName, out wanted))
            {
                if ((int)eventArgs.Level <= (int)wanted && wanted != LogLevel.None) return false;
                _perModDroppedLines++;
                return true;
            }

            if (ShouldQuietOtherMod(sourceName, eventArgs.Level))
            {
                _quietedOtherModLines++;
                AnnounceQuietOnce();
                return true;
            }

            string anySourceMessage = eventArgs.Data?.ToString();
            if (!string.IsNullOrEmpty(anySourceMessage) && Contains(anySourceMessage, EnsureSortedFragment))
            {
                if (++_suppressedEnsureSorted == 1) AnnounceEnsureSortedOnce();
                return true;
            }

            // Only filter HarmonyX-source messages here. Other sources
            // (Unity, ConfigSync, ManualLogSources) pass through.
            if (!string.Equals(sourceName, HarmonySourceName, StringComparison.Ordinal))
                return false;

            string message = eventArgs.Data?.ToString();
            if (string.IsNullOrEmpty(message)) return false;

            if (eventArgs.Level == LogLevel.Warning
                && (Contains(message, MissingMethodFragment) || Contains(message, MissingTypeFragment)))
            {
                _suppressedHarmonyMissing++;
                EmitHarmonySummaryIfDue();
                return true;
            }

            return false;
        }

        // BepInEx's LogLevel is a flags enum ordered by severity, most severe LOWEST: Fatal 1, Error 2, Warning 4,
        // Message 8, Info 16, Debug 32. So "keep this level and more severe" is a <= on the numeric value.
        private static bool ShouldQuietOtherMod(string sourceName, LogLevel level)
        {
            if (_quietEnabled == null || !_quietEnabled.Value) return false;
            if ((int)level <= (int)_quietKeepLevel.Value) return false;

            foreach (string exempt in _quietExemptList)
                if (string.Equals(sourceName, exempt, StringComparison.OrdinalIgnoreCase)) return false;

            // A named list quiets exactly those sources. An empty one quiets everything outside this family, which is
            // the setting's whole point - the mods with no verbose switch are the ones nobody can enumerate up front.
            if (_quietSourceList.Length > 0)
            {
                foreach (string named in _quietSourceList)
                    if (string.Equals(sourceName, named, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            return !IsFamilySource(sourceName);
        }

        /// <summary>"[&lt;Area&gt;Test] ..." with a letters-only area name, e.g. "[VedrTest] 3/9 providers PASS - ...".</summary>
        private static bool IsTestLine(string message)
        {
            if (string.IsNullOrEmpty(message) || message[0] != TestMarkerOpen) return false;
            int close = message.IndexOf(TestMarkerClose, 1, Math.Min(TestMarkerSearchChars, message.Length - 1), StringComparison.Ordinal);
            if (close < MinTestMarkerClose) return false;
            for (int i = 1; i < close; i++)
                if (!char.IsLetter(message[i])) return false;
            return true;
        }

        /// <summary>
        /// Whether a source is one of OURS. The "Fires" prefix alone was wrong: five shipped family members log under
        /// names that do not start with it, so enabling QuietOtherMods used to gag part of our own family while
        /// claiming to quiet third parties. The prefix stays as the cheap path and the explicit list covers the rest.
        /// </summary>
        private static bool IsFamilySource(string sourceName)
        {
            if (sourceName.StartsWith(FiresSourcePrefix, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string known in FamilySourceNames)
                if (string.Equals(sourceName, known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void AnnounceEnsureSortedOnce()
        {
            Debug.Log($"{SummaryPrefix} Holding back vanilla's \"EnsureSorted called before all files have been added\" errors: its own assertion while it enumerates every world in the shared worlds_local, naming chunk files of worlds this process is not loading. Set verbose to surface them.");
        }

        private static void AnnounceQuietOnce()
        {
            if (_quietAnnounced) return;
            _quietAnnounced = true;
            Debug.Log($"{SummaryPrefix} Quieting other mods' log lines below {_quietKeepLevel.Value}; the status box keeps the count. Set verbose, or QuietOtherMods=false, to surface them.");
        }

        private static bool Contains(string haystack, string needle)
            => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool VerbosePassThrough()
        {
            try { return FiresLogger.VerboseEnabled; }
            catch { return false; }
        }

        private static void EmitHarmonySummaryIfDue()
        {
            if (_suppressedHarmonyMissing != 1
                && _suppressedHarmonyMissing % HarmonySummaryInterval != 0) return;
            Debug.Log($"{SummaryPrefix} Suppressed {_suppressedHarmonyMissing} HarmonyX 'Could not find method/type' warnings (PTB-targeted probes). Set verbose to surface.");
        }
    }
}
