using System;
using UnityEngine;

namespace FiresCore.Logging
{
    // Wraps Unity's default ILogHandler to throttle / suppress known noisy
    // messages. Verbose mode is a pass-through — when the FiresLogger
    // verbose toggle is on, nothing is filtered.
    //
    // Suppressed categories (non-verbose only):
    //   - Steam k_EResultLimitExceeded floods (throttled to 1/30s)
    //   - Shader binary-data warnings during bundle load (our shader
    //     replacement handles the missing-on-export shaders)
    //   - "Referenced script is missing" warnings (bundle prefabs whose
    //     C# refs point at SpaceCraft / DungeonGenerator / other content
    //     mods not in the user's profile — cosmetic, no functional impact)
    //   - Kinematic-rigidbody velocity warnings (Valheim sets velocity on
    //     attached Characters and the warning is benign)
    //   - Known Valheim NRE stacks that we have no fix for (ShieldGenerator,
    //     ArcheryTarget) — summary line emitted every NreSummaryEmitInterval
    //     suppressions so the user knows the suppression is alive.
    //
    // Summaries (one line per N suppressed) provide ongoing visibility
    // without flooding the log per-occurrence.
    internal sealed class RateLimitedLogHandler : ILogHandler
    {
        private const string LogPrefix = "[FiresUnifiedCore]";

        private const string LimitExceededFragment        = "Failed to send data k_EResultLimitExceeded";
        private const string ShaderBinaryWarningFragment  = "Failed to find expected binary shader data";
        private const string MissingScriptFragment        = "The referenced script";
        private const string KinematicLinearFragment      = "Setting linear velocity of a kinematic body is not supported";
        private const string KinematicAngularFragment     = "Setting angular velocity of a kinematic body is not supported";
        private const string ShieldGeneratorStackFragment = "ShieldGenerator.OnDestroy";
        private const string ArcheryTargetStackFragment   = "ArcheryTarget.Start";

        private const double LimitExceededThrottleSeconds   = 30.0;
        private const int    NreSummaryEmitInterval         = 50;
        private const int    ShaderWarningSummaryInterval   = 100;
        private const int    MissingScriptSummaryInterval   = 100;
        private const int    KinematicSummaryInterval       = 200;

        private static readonly TimeSpan LimitExceededInterval = TimeSpan.FromSeconds(LimitExceededThrottleSeconds);

        private readonly ILogHandler _inner;

        private DateTime _lastLimitExceeded = DateTime.MinValue;
        private int _suppressedShaderWarnings;
        private int _suppressedMissingScriptWarnings;
        private int _suppressedKinematicWarnings;
        private int _suppressedValheimNreBugs;

        public RateLimitedLogHandler(ILogHandler inner)
        {
            _inner = inner ?? Debug.unityLogger.logHandler;
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            if (!VerbosePassThrough() && ShouldSuppress(logType, format, args)) return;
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (!VerbosePassThrough()
                && exception is NullReferenceException
                && IsKnownVanillaNre(exception))
            {
                _suppressedValheimNreBugs++;
                EmitNreSummaryIfDue();
                return;
            }
            _inner.LogException(exception, context);
        }

        // Verbose mode short-circuits all suppression. The flag lives on
        // FiresLogger and is driven by ConfigManager.configVerboseLogging.
        // Caught in a try/catch because FiresLogger may not be initialized
        // yet when the first Unity log fires during BepInEx chainload.
        private static bool VerbosePassThrough()
        {
            try { return FiresLogger.VerboseEnabled; }
            catch { return false; }
        }

        private bool ShouldSuppress(LogType logType, string format, object[] args)
        {
            string message = ResolveMessage(format, args);
            if (string.IsNullOrEmpty(message)) return false;

            if (logType == LogType.Warning && IsKinematicVelocityWarning(message))
            {
                _suppressedKinematicWarnings++;
                EmitKinematicSummaryIfDue();
                return true;
            }
            if (logType == LogType.Warning && IsShaderBinaryWarning(message))
            {
                _suppressedShaderWarnings++;
                EmitShaderSummaryIfDue();
                return true;
            }
            if (logType == LogType.Warning && IsMissingScriptWarning(message))
            {
                _suppressedMissingScriptWarnings++;
                EmitMissingScriptSummaryIfDue();
                return true;
            }
            if (IsLimitExceededAndThrottled(message)) return true;

            return false;
        }

        private static string ResolveMessage(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); }
            catch (FormatException) { return format; }
        }

        private static bool IsKinematicVelocityWarning(string message)
            => message.IndexOf(KinematicLinearFragment, StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf(KinematicAngularFragment, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsShaderBinaryWarning(string message)
            => message.IndexOf(ShaderBinaryWarningFragment, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsMissingScriptWarning(string message)
            => message.IndexOf(MissingScriptFragment, StringComparison.OrdinalIgnoreCase) >= 0;

        private bool IsLimitExceededAndThrottled(string message)
        {
            if (message.IndexOf(LimitExceededFragment, StringComparison.OrdinalIgnoreCase) < 0) return false;

            var now = DateTime.UtcNow;
            if (now - _lastLimitExceeded < LimitExceededInterval) return true;
            _lastLimitExceeded = now;
            return false;
        }

        private static bool IsKnownVanillaNre(Exception exception)
        {
            string stack = exception?.StackTrace;
            if (string.IsNullOrEmpty(stack)) return false;
            if (stack.Contains(ShieldGeneratorStackFragment)) return true;
            if (stack.Contains(ArcheryTargetStackFragment)) return true;
            return false;
        }

        private void EmitNreSummaryIfDue()
        {
            if (_suppressedValheimNreBugs != 1 && _suppressedValheimNreBugs % NreSummaryEmitInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} known Valheim NRE bugs (ShieldGenerator, ArcheryTarget, etc.) — set verbose to surface.",
                _suppressedValheimNreBugs);
        }

        private void EmitShaderSummaryIfDue()
        {
            if (_suppressedShaderWarnings != 1 && _suppressedShaderWarnings % ShaderWarningSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} shader binary-data warnings — VanillaAssetResolver handles the missing-on-export shaders. Set verbose to surface.",
                _suppressedShaderWarnings);
        }

        private void EmitMissingScriptSummaryIfDue()
        {
            if (_suppressedMissingScriptWarnings != 1 && _suppressedMissingScriptWarnings % MissingScriptSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} 'referenced script missing' warnings (bundle prefabs reference content-mod scripts not in this profile — cosmetic). Set verbose to surface.",
                _suppressedMissingScriptWarnings);
        }

        private void EmitKinematicSummaryIfDue()
        {
            if (_suppressedKinematicWarnings != 1 && _suppressedKinematicWarnings % KinematicSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} kinematic-rigidbody velocity warnings (Valheim sets velocity on attached Characters — benign). Set verbose to surface.",
                _suppressedKinematicWarnings);
        }
    }
}
