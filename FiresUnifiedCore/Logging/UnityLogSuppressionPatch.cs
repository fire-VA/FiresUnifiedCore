using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Logging
{
    // BepInEx 5.4's UnityLogSource subscribes to
    // Application.logMessageReceived directly. Specifically: the static
    // ctor wires UnityLogSource.OnUnityLogMessageReceived as the
    // Application.LogCallback handler. That callback fires Unity-side
    // for every Debug.Log/LogWarning regardless of what ILogHandler is
    // installed on Debug.unityLogger, then routes the message through
    // BepInEx's LogSource → LogListener fan-out (console + disk).
    //
    // Swapping Debug.unityLogger.logHandler - what the old
    // RateLimitedLogHandler did - only intercepts Unity's own native
    // console writer, never BepInEx's. That's why 248 shader binary
    // warnings still landed in LogOutput.log despite the handler being
    // installed: the suppression path is unreachable.
    //
    // Real fix: HarmonyPrefix on UnityLogSource.OnUnityLogMessageReceived.
    // Returning false skips BepInEx's original handler entirely - no
    // LogEvent is constructed, no listener (UnityLogSource → ConsoleLogListener
    // → DiskLogListener) ever sees the suppressed message. Verbose mode
    // is a top-level pass-through so the user can opt back into seeing
    // everything.
    //
    // Type is resolved by name at PatchAll time because UnityLogSource
    // lives in BepInEx.dll which we don't have a project reference to.
    // Reference source confirmed against
    // valheimRip2/BepInExREF/Logging/UnityLogSource.cs (decompile of
    // BepInEx 5.4.23.3).
    [HarmonyPatch]
    internal static class UnityLogSuppressionPatch
    {
        private const string UnityLogSourceTypeName = "BepInEx.Logging.UnityLogSource";
        private const string BepInExAssemblyName    = "BepInEx";
        private const string CallbackMethodName     = "OnUnityLogMessageReceived";

        private const string ShaderBinaryWarningFragment = "Failed to find expected binary shader data";
        private const string MissingScriptFragment       = "The referenced script";
        private const string KinematicLinearFragment     = "Setting linear velocity of a kinematic body is not supported";
        private const string KinematicAngularFragment    = "Setting angular velocity of a kinematic body is not supported";
        private const string LimitExceededFragment       = "Failed to send data k_EResultLimitExceeded";
        // Non-readable (R/W-off) mesh warnings - generic Unity/PhysX/navmesh spam from Ashlands/Mistlands + modded
        // content. Moved to Core so suppression doesn't depend on FiresAdminPrefabs being loaded (it kept its own
        // copy of these). Mod-specific fragments (e.g. FAP's "FAPBlueprintBake_") stay in that mod's own patch.
        private const string CombineMeshFragment         = "Cannot combine mesh that does not allow access";
        private const string NavMeshReadFragment         = "RuntimeNavMeshBuilder: Source mesh";
        private const string NavMeshReadAccessFragment   = "does not allow read access";

        private const int ShaderSummaryInterval         = 100;
        private const int MissingScriptSummaryInterval  = 100;
        private const int KinematicSummaryInterval      = 200;
        private const int NonReadableMeshSummaryInterval = 100;
        private const double LimitExceededThrottleSeconds = 30.0;

        private const string SummaryPrefix = "[FiresUnifiedCore]";

        private static readonly TimeSpan LimitExceededInterval =
            TimeSpan.FromSeconds(LimitExceededThrottleSeconds);

        private static int _suppressedShaderWarnings;
        private static int _suppressedMissingScriptWarnings;
        private static int _suppressedKinematicWarnings;
        private static int _suppressedNonReadableMeshWarnings;
        private static DateTime _lastLimitExceeded = DateTime.MinValue;

        // Diagnostic - surfaces in BepInEx logs once at startup so we
        // can confirm the patch wired in. If this isn't present in the
        // log on next boot, BepInEx has renamed the type and the patch
        // skipped cleanly via Prepare returning false.
        private static bool _diagnosticEmitted;

        private static bool Prepare()
        {
            var method = TargetMethod();
            if (method != null && !_diagnosticEmitted)
            {
                _diagnosticEmitted = true;
                Debug.Log($"{SummaryPrefix} UnityLogSuppressionPatch wired to {UnityLogSourceTypeName}.{CallbackMethodName} - Unity log noise will be filtered.");
            }
            else if (method == null && !_diagnosticEmitted)
            {
                _diagnosticEmitted = true;
                Debug.LogWarning($"{SummaryPrefix} UnityLogSuppressionPatch could NOT locate {UnityLogSourceTypeName}.{CallbackMethodName} - BepInEx version may have renamed it. Suppression disabled.");
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

                    var type = asm.GetType(UnityLogSourceTypeName, throwOnError: false);
                    if (type == null) continue;

                    // Reference source: BepInExREF/Logging/UnityLogSource.cs L66
                    //   private static void OnUnityLogMessageReceived(
                    //       string message, string stackTrace, LogType type)
                    return type.GetMethod(
                        CallbackMethodName,
                        BindingFlags.Static | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(string), typeof(string), typeof(LogType) },
                        modifiers: null);
                }
            }
            catch
            {
                // fall through to null
            }
            return null;
        }

        // Returning false skips BepInEx's original callback. No LogEvent
        // is built, no listener sees the message, the line never reaches
        // console or disk.
        private static bool Prefix(string message, LogType type)
        {
            try
            {
                if (VerbosePassThrough()) return true;
                if (string.IsNullOrEmpty(message)) return true;
                return !ShouldSuppress(message, type);
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldSuppress(string message, LogType type)
        {
            if (type == LogType.Warning && Contains(message, ShaderBinaryWarningFragment))
            {
                _suppressedShaderWarnings++;
                EmitShaderSummaryIfDue();
                return true;
            }
            if (type == LogType.Warning && Contains(message, MissingScriptFragment))
            {
                _suppressedMissingScriptWarnings++;
                EmitMissingScriptSummaryIfDue();
                return true;
            }
            if (type == LogType.Warning
                && (Contains(message, KinematicLinearFragment) || Contains(message, KinematicAngularFragment)))
            {
                _suppressedKinematicWarnings++;
                EmitKinematicSummaryIfDue();
                return true;
            }
            if ((type == LogType.Warning || type == LogType.Error)
                && (Contains(message, CombineMeshFragment)
                    || (Contains(message, NavMeshReadFragment) && Contains(message, NavMeshReadAccessFragment))))
            {
                _suppressedNonReadableMeshWarnings++;
                EmitNonReadableMeshSummaryIfDue();
                return true;
            }
            if (Contains(message, LimitExceededFragment))
            {
                var now = DateTime.UtcNow;
                if (now - _lastLimitExceeded < LimitExceededInterval) return true;
                _lastLimitExceeded = now;
                return false;
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

        // Summaries route through Debug.Log → Unity native → BepInEx
        // captures via Application.logMessageReceived → our prefix sees
        // the summary text but it doesn't match a suppression fragment,
        // so it passes through normally.
        private static void EmitShaderSummaryIfDue()
        {
            if (_suppressedShaderWarnings != 1
                && _suppressedShaderWarnings % ShaderSummaryInterval != 0) return;
            Debug.Log($"{SummaryPrefix} Suppressed {_suppressedShaderWarnings} shader binary-data warnings. Set verbose to surface.");
        }

        private static void EmitMissingScriptSummaryIfDue()
        {
            if (_suppressedMissingScriptWarnings != 1
                && _suppressedMissingScriptWarnings % MissingScriptSummaryInterval != 0) return;
            Debug.Log($"{SummaryPrefix} Suppressed {_suppressedMissingScriptWarnings} 'referenced script missing' warnings. Set verbose to surface.");
        }

        private static void EmitKinematicSummaryIfDue()
        {
            if (_suppressedKinematicWarnings != 1
                && _suppressedKinematicWarnings % KinematicSummaryInterval != 0) return;
            Debug.Log($"{SummaryPrefix} Suppressed {_suppressedKinematicWarnings} kinematic-rigidbody velocity warnings. Set verbose to surface.");
        }

        private static void EmitNonReadableMeshSummaryIfDue()
        {
            if (_suppressedNonReadableMeshWarnings != 1
                && _suppressedNonReadableMeshWarnings % NonReadableMeshSummaryInterval != 0) return;
            Debug.Log($"{SummaryPrefix} Suppressed {_suppressedNonReadableMeshWarnings} non-readable-mesh warnings (CombineMeshes / RuntimeNavMeshBuilder read access - R/W-off meshes on Ashlands + modded content). Set verbose to surface.");
        }
    }
}
