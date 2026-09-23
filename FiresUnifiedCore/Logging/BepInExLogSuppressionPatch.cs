using System;
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

        private static ConfigEntry<bool>     _quietEnabled;
        private static ConfigEntry<LogLevel> _quietKeepLevel;
        private static ConfigEntry<string>   _quietSources;
        private static ConfigEntry<string>   _quietExempt;

        private static string[] _quietSourceList = Array.Empty<string>();
        private static string[] _quietExemptList = Array.Empty<string>();

        private static int _suppressedHarmonyMissing;
        private static int _quietedOtherModLines;
        private static bool _diagnosticEmitted;
        private static bool _quietAnnounced;

        internal static int SuppressedHarmonyMissing => _suppressedHarmonyMissing;
        internal static int QuietedOtherModLines => _quietedOtherModLines;

        // Core binds these from its own config file at setup; nothing else should.
        internal static void BindConfig(ConfigFile config)
        {
            if (config == null) return;

            _quietEnabled   = config.Bind(QuietSection, QuietEnabledKey, false, QuietEnabledDescription);
            _quietKeepLevel = config.Bind(QuietSection, QuietKeepLevelKey, LogLevel.Warning, QuietKeepLevelDescription);
            _quietSources   = config.Bind(QuietSection, QuietSourcesKey, string.Empty, QuietSourcesDescription);
            _quietExempt    = config.Bind(QuietSection, QuietExemptKey, DefaultExempt, QuietExemptDescription);

            RebuildLists();
            _quietSources.SettingChanged += (_, __) => RebuildLists();
            _quietExempt.SettingChanged  += (_, __) => RebuildLists();
        }

        private static void RebuildLists()
        {
            _quietSourceList = SplitNames(_quietSources != null ? _quietSources.Value : null);
            _quietExemptList = SplitNames(_quietExempt != null ? _quietExempt.Value : null);
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

            if (ShouldQuietOtherMod(sourceName, eventArgs.Level))
            {
                _quietedOtherModLines++;
                AnnounceQuietOnce();
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

            return !sourceName.StartsWith(FiresSourcePrefix, StringComparison.OrdinalIgnoreCase);
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
