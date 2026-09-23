using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Logging
{
    // Suppresses known-noise Unity messages by prefixing BepInEx's UnityLogSource.OnUnityLogMessageReceived,
    // which listens on Application.logMessageReceived directly; replacing Debug.unityLogger's handler never
    // reached BepInEx's console and disk listeners. The type is resolved by name since Core has no reference to
    // it. Each category announces itself once; the running totals join Core's line in the status box.
    // Verbose mode passes everything through.
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
        // [EnvironmentBoxPatches] "Suppressed ClutterSystem NRE for patch (x, y)" — FAT/Galaxies log a warning
        // each time they swallow a ClutterSystem NRE during custom-terrain / zone load; one per patch coordinate,
        // so it floods world-load. Harmless noise — suppress centrally like the rest of this list.
        private const string ClutterNreFragment          = "Suppressed ClutterSystem NRE";
        // A dedicated server has no GPU, so vanilla's render and video paths fail on every boot: three
        // "AsyncResourceUpload failed", the Hidden/VideoDecode + Hidden/VideoComposite materials and each of their
        // shader passes. 17 errors a boot, all of them fake, which buries a real one. Headless only - on a client with
        // a graphics device these mean something and still print.
        private const string AsyncUploadFragment         = "AsyncResourceUpload failed";
        private const string RenderPathPassesFragment    = "custom render path shader needs to have at least 1 passes";
        private const string VideoMaterialFragment       = "Could not find material Hidden/Video";
        private const string VideoPassFragment           = "Could not find video decode shader pass";

        private const double LimitExceededThrottleSeconds = 30.0;

        private const string SummaryPrefix = "[FiresUnifiedCore]";

        private static readonly TimeSpan LimitExceededInterval =
            TimeSpan.FromSeconds(LimitExceededThrottleSeconds);

        private static int _suppressedShaderWarnings;
        private static int _suppressedMissingScriptWarnings;
        private static int _suppressedKinematicWarnings;
        private static int _suppressedNonReadableMeshWarnings;
        private static int _suppressedClutterNreWarnings;
        private static int _suppressedHeadlessRenderErrors;
        private static DateTime _lastLimitExceeded = DateTime.MinValue;

        // Read once while patching, on the main thread: the callback this patches fires on whichever thread logged,
        // and SystemInfo is not worth touching from those.
        private static bool _headless;

        internal static int SuppressedShaderWarnings => _suppressedShaderWarnings;
        internal static int SuppressedMissingScriptWarnings => _suppressedMissingScriptWarnings;
        internal static int SuppressedKinematicWarnings => _suppressedKinematicWarnings;
        internal static int SuppressedNonReadableMeshWarnings => _suppressedNonReadableMeshWarnings;
        internal static int SuppressedClutterNreWarnings => _suppressedClutterNreWarnings;
        internal static int SuppressedHeadlessRenderErrors => _suppressedHeadlessRenderErrors;

        // Diagnostic - surfaces in BepInEx logs once at startup so we
        // can confirm the patch wired in. If this isn't present in the
        // log on next boot, BepInEx has renamed the type and the patch
        // skipped cleanly via Prepare returning false.
        private static bool _diagnosticEmitted;

        private static bool Prepare()
        {
            try { _headless = Lifecycle.FiresMod.IsHeadless; }
            catch { _headless = false; }

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
                if (++_suppressedShaderWarnings == 1)
                    Announce("shader binary-data warnings");
                return true;
            }
            if (type == LogType.Warning && Contains(message, MissingScriptFragment))
            {
                if (++_suppressedMissingScriptWarnings == 1)
                    Announce("'referenced script missing' warnings");
                return true;
            }
            if (type == LogType.Warning
                && (Contains(message, KinematicLinearFragment) || Contains(message, KinematicAngularFragment)))
            {
                if (++_suppressedKinematicWarnings == 1)
                    Announce("kinematic-rigidbody velocity warnings");
                return true;
            }
            if ((type == LogType.Warning || type == LogType.Error)
                && (Contains(message, CombineMeshFragment)
                    || (Contains(message, NavMeshReadFragment) && Contains(message, NavMeshReadAccessFragment))))
            {
                if (++_suppressedNonReadableMeshWarnings == 1)
                    Announce("non-readable-mesh warnings (CombineMeshes / RuntimeNavMeshBuilder read access - R/W-off meshes on Ashlands + modded content)");
                return true;
            }
            if (type == LogType.Warning && Contains(message, ClutterNreFragment))
            {
                if (++_suppressedClutterNreWarnings == 1)
                    Announce("'[EnvironmentBoxPatches] ClutterSystem NRE' warnings (custom-terrain / zone load)");
                return true;
            }
            if (_headless
                && (Contains(message, AsyncUploadFragment)
                    || Contains(message, RenderPathPassesFragment)
                    || Contains(message, VideoMaterialFragment)
                    || Contains(message, VideoPassFragment)))
            {
                if (++_suppressedHeadlessRenderErrors == 1)
                    Announce("vanilla render/video errors a headless server cannot avoid (no GPU)");
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

        // The announcement routes through Debug.Log → Unity native → BepInEx captures via
        // Application.logMessageReceived → our prefix sees the text but it doesn't match a suppression
        // fragment, so it passes through normally.
        private static void Announce(string category)
        {
            Debug.Log($"{SummaryPrefix} Suppressing {category}; the status box keeps the count. Set verbose to surface.");
        }
    }
}
