using System;
using UnityEngine;

namespace FiresCore.Logging
{
    // Wraps Unity's default ILogHandler to throttle / suppress known noisy
    // messages. Verbose mode is a pass-through - when the FiresLogger
    // verbose toggle is on, nothing is filtered.
    //
    // Suppressed categories (non-verbose only):
    //   - Steam k_EResultLimitExceeded floods (throttled to 1/30s)
    //   - Shader binary-data warnings during bundle load (our shader
    //     replacement handles the missing-on-export shaders)
    //   - "Referenced script is missing" warnings (bundle prefabs whose
    //     C# refs point at SpaceCraft / DungeonGenerator / other content
    //     mods not in the user's profile - cosmetic, no functional impact)
    //   - Kinematic-rigidbody velocity warnings (Valheim sets velocity on
    //     attached Characters and the warning is benign)
    //   - Known Valheim NRE stacks that we have no fix for (ShieldGenerator,
    //     ArcheryTarget) - summary line emitted every NreSummaryEmitInterval
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
        // ExpandWorld Prefabs throws an unhandled NRE in its ZDO-destroy handler (reads a null prefab name for every
        // destroyed dungeon/streamed ZDO it doesn't recognise). It is a third-party bug we cannot fix; without this it
        // floods the relay thousands of times per session. Matched on its throw-site frame (works through the
        // TargetInvocationException the routed-RPC dispatch wraps it in).
        private const string ExpandWorldPrefabDestroyFragment = "ExpandWorld.Prefab.Manager.Handle";

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
            string message = ResolveMessage(format, args);

            // Drop the raw native-console echo for our OWN mods' lines. Every Debug.Log ALSO reaches BepInEx via
            // Application.logMessageReceived (independent of this handler), where FiresLogColorPatch prints it as a
            // single colored line — so forwarding to the native writer here is exactly what produces the second,
            // uncolored "white duplicate". Suppressing the native echo leaves only the colored BepInEx line, the
            // same result mods get by logging through their own ManualLogSource. Not verbose-gated: this removes a
            // redundant duplicate, not diagnostic content, so it applies even in verbose mode.
            if (FiresLogColorPatch.IsOurModMessage(message)) return;

            if (!VerbosePassThrough() && ShouldSuppress(logType, message)) return;
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (!VerbosePassThrough() && IsKnownNoFixNre(exception))
            {
                _suppressedValheimNreBugs++;
                EmitNreSummaryIfDue();
                return;
            }
            RelayException(exception, context);
        }

        // Relay a logged exception WITHOUT stamping our log-handler chain across its stack trace. Reads as
        // "[FiresUnifiedCore] relayed exception from '<mod>': <type>: <msg>\n<original throw stack>" so a reader
        // immediately sees which mod actually threw and that FiresUnifiedCore is only the relay - not the source.
        //
        // We emit the exception's OWN captured stack as text and suppress Unity's live call-stack append for this one
        // write. That append is what was plastering every chained handler frame (FiresCore.Logging.RateLimitedLogHandler,
        // the per-mod RateLimitedLogHandler/LogFilter forks, …) onto unrelated mods' errors. This handler is the
        // innermost link in the chain, so suppressing the append here clears the whole chain's frames in one place.
        private void RelayException(Exception exception, UnityEngine.Object context)
        {
            if (exception == null) return;

            string origin = ResolveOriginAssembly(exception);
            string body = $"{LogPrefix} relayed exception from '{origin}' (FiresUnifiedCore is only relaying this - the error is in that mod):\n"
                        + $"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";

            StackTraceLogType previous = Application.GetStackTraceLogType(LogType.Error);
            bool toggled = previous != StackTraceLogType.None;
            if (toggled) Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
            try
            {
                _inner.LogFormat(LogType.Error, context, "{0}", body);
            }
            finally
            {
                if (toggled) Application.SetStackTraceLogType(LogType.Error, previous);
            }
        }

        // The assembly of the deepest (throw-site) frame = the mod that actually threw. assembly_valheim/utils map to
        // "Valheim"; Unity/BCL frames to "Unity/runtime". Falls back to parsing the textual stack, then "unknown".
        private static string ResolveOriginAssembly(Exception exception)
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(exception, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    string asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly?.GetName()?.Name;
                    if (!string.IsNullOrEmpty(asm))
                        return Friendly(asm);
                }
            }
            catch { }
            return ParseTopTypeFromStackString(exception.StackTrace) ?? "unknown";
        }

        private static string Friendly(string assemblyName)
        {
            if (assemblyName == "assembly_valheim" || assemblyName == "assembly_utils" || assemblyName == "assembly_guiutils")
                return "Valheim";
            if (assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) || assemblyName == "mscorlib" || assemblyName.StartsWith("System", StringComparison.Ordinal))
                return "Unity/runtime";
            return assemblyName;
        }

        // Fallback when the exception carries no reconstructable frames: pull the root namespace from the first
        // "  at Namespace.Type.Method (...)" line of the textual stack.
        private static string ParseTopTypeFromStackString(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return null;
            foreach (string raw in stack.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);
                int paren = line.IndexOf('(');
                if (paren > 0) line = line.Substring(0, paren).Trim();
                int lastDot = line.LastIndexOf('.');
                if (lastDot <= 0) return line;
                string typeFull = line.Substring(0, lastDot);
                int rootDot = typeFull.IndexOf('.');
                return rootDot > 0 ? typeFull.Substring(0, rootDot) : typeFull;
            }
            return null;
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

        private bool ShouldSuppress(LogType logType, string message)
        {
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

        // Known NRE stacks we have no fix for: vanilla bugs (ShieldGenerator, ArcheryTarget) and third-party floods we
        // cannot patch (ExpandWorld Prefabs' unhandled ZDO-destroy NRE). Scans the whole inner-exception chain because
        // the routed-RPC dispatch wraps the real throw in a TargetInvocationException.
        private static bool IsKnownNoFixNre(Exception exception)
        {
            for (Exception e = exception; e != null; e = e.InnerException)
            {
                string stack = e.StackTrace;
                if (string.IsNullOrEmpty(stack)) continue;
                if (stack.Contains(ShieldGeneratorStackFragment)) return true;
                if (stack.Contains(ArcheryTargetStackFragment)) return true;
                if (stack.Contains(ExpandWorldPrefabDestroyFragment)) return true;
            }
            return false;
        }

        private void EmitNreSummaryIfDue()
        {
            if (_suppressedValheimNreBugs != 1 && _suppressedValheimNreBugs % NreSummaryEmitInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} known no-fix NRE floods (vanilla ShieldGenerator/ArcheryTarget + ExpandWorld prefab-destroy) - set verbose to surface.",
                _suppressedValheimNreBugs);
        }

        private void EmitShaderSummaryIfDue()
        {
            if (_suppressedShaderWarnings != 1 && _suppressedShaderWarnings % ShaderWarningSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} shader binary-data warnings - VanillaAssetResolver handles the missing-on-export shaders. Set verbose to surface.",
                _suppressedShaderWarnings);
        }

        private void EmitMissingScriptSummaryIfDue()
        {
            if (_suppressedMissingScriptWarnings != 1 && _suppressedMissingScriptWarnings % MissingScriptSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} 'referenced script missing' warnings (bundle prefabs reference content-mod scripts not in this profile - cosmetic). Set verbose to surface.",
                _suppressedMissingScriptWarnings);
        }

        private void EmitKinematicSummaryIfDue()
        {
            if (_suppressedKinematicWarnings != 1 && _suppressedKinematicWarnings % KinematicSummaryInterval != 0) return;
            _inner.LogFormat(LogType.Log, null,
                $"{LogPrefix} Suppressed {{0}} kinematic-rigidbody velocity warnings (Valheim sets velocity on attached Characters - benign). Set verbose to surface.",
                _suppressedKinematicWarnings);
        }
    }
}
