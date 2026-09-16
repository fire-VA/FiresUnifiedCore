using System;
using System.Reflection;
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

        private static int _suppressedHarmonyMissing;
        private static bool _diagnosticEmitted;

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
