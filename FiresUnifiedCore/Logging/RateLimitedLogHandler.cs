using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FiresCore.Logging
{
    // Wraps Unity's default ILogHandler to throttle or drop known noise: Steam k_EResultLimitExceeded floods,
    // shader binary-data warnings during bundle load, missing-script warnings from other mods' prefabs, benign
    // kinematic-velocity warnings, and known unfixable vanilla NRE stacks. Each category announces itself once, and
    // the running totals are Core's line in the status box. Verbose mode passes everything through.
    internal sealed class RateLimitedLogHandler : ILogHandler
    {
        private const string LogPrefix = "[FiresUnifiedCore]";
        private const string UnityLogSourceName = "Unity Log";
        private const string InnerExceptionPrefix = "---> ";
        private const string UnknownOrigin = "unknown";
        private const string UnityRuntimeOrigin = "Unity/runtime";

        private const string RouteSection = "Logging";
        private const string RouteKey = "RouteOtherModsThroughQueue";
        private const string RouteDescription =
            "Send other mods' Unity log lines through this family's output queue instead of letting Unity echo them "
            + "raw. Stops a third-party line printing through the middle of a banner. The cost is that those lines "
            + "stop appearing in Player.log; they are still in LogOutput.log and the console.";

        private static ConfigEntry<bool> _routeOtherModLines;

        // Core binds this from its own config file at setup; nothing else should.
        internal static void BindConfig(ConfigFile config)
        {
            if (config == null) return;
            _routeOtherModLines = config.Bind(RouteSection, RouteKey, true, RouteDescription);
        }

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
        // ExpandWorldData throws an unhandled NRE from Terrain.FindCompiler while leveling/painting a location's
        // terrain during bulk zone spawning (pregen / world reset): hm.GetAndCreateTerrainCompiler().m_nview is
        // null before the compiler's ZNetView finishes init. Third-party, not ours (Core only relays it); on
        // normal single-zone play the compiler is ready and it doesn't fire. Suppress the pregen flood.
        private const string ExpandWorldTerrainCompilerFragment = "ExpandWorldData.Terrain.FindCompiler";

        private const double LimitExceededThrottleSeconds   = 30.0;
        private const string StatusSource                   = "Core";

        private static readonly TimeSpan LimitExceededInterval = TimeSpan.FromSeconds(LimitExceededThrottleSeconds);

        private readonly ILogHandler _inner;
        private readonly ManualLogSource _ourModLineSource;
        private readonly int _mainThreadId;

        private DateTime _lastLimitExceeded = DateTime.MinValue;
        private int _suppressedShaderWarnings;
        private int _suppressedMissingScriptWarnings;
        private int _suppressedKinematicWarnings;
        private int _suppressedValheimNreBugs;

        public RateLimitedLogHandler(ILogHandler inner)
        {
            _inner = inner ?? Debug.unityLogger.logHandler;
            _ourModLineSource = BepInEx.Logging.Logger.CreateLogSource(UnityLogSourceName);
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            StatusBanner.Register(StatusSource, DescribeSuppressed);
        }

        // Core's line in the status box: this handler's counts plus UnityLogSuppressionPatch's, which sees the native
        // Unity warnings that never pass through this handler, so the two never count the same message.
        private string DescribeSuppressed()
        {
            var counts = new List<string>(6);
            AddCount(counts, _suppressedShaderWarnings + UnityLogSuppressionPatch.SuppressedShaderWarnings, "shader binary-data");
            AddCount(counts, _suppressedMissingScriptWarnings + UnityLogSuppressionPatch.SuppressedMissingScriptWarnings, "missing-script");
            AddCount(counts, _suppressedKinematicWarnings + UnityLogSuppressionPatch.SuppressedKinematicWarnings, "kinematic-velocity");
            AddCount(counts, UnityLogSuppressionPatch.SuppressedNonReadableMeshWarnings, "non-readable-mesh");
            AddCount(counts, UnityLogSuppressionPatch.SuppressedClutterNreWarnings, "ClutterSystem NRE");
            AddCount(counts, UnityLogSuppressionPatch.SuppressedHeadlessRenderErrors, "headless render/video");
            AddCount(counts, BepInExLogSuppressionPatch.QuietedOtherModLines, "other mods quieted");
            AddCount(counts, BepInExLogSuppressionPatch.SuppressedHarmonyMissing, "HarmonyX probe misses");
            AddCount(counts, _suppressedValheimNreBugs, "known no-fix NRE");
            return counts.Count == 0 ? null : $"log filter held back {string.Join(", ", counts)} (verbose shows them)";
        }

        private static void AddCount(List<string> counts, int count, string label)
        {
            if (count > 0) counts.Add($"{count:N0} {label}");
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string message = ResolveMessage(format, args);
            if (TryWriteOurModLineToBepInEx(logType, message)) return;

            if (!VerbosePassThrough() && ShouldSuppress(logType, message)) return;
            if (TryRouteOtherModLine(logType, message)) return;
            _inner.LogFormat(logType, context, format, args);
        }

        // Another mod's Debug.Log reaches the console twice: once as Unity's own raw stdout echo, and once through
        // BepInEx's Unity Log listener. The raw echo does not pass through Core's output queue, so it lands wherever
        // it likes - including through the middle of a banner that is still being written. Routing the line to BepInEx
        // ourselves and dropping the native forward gives it the queue's ordering, the same treatment our own tagged
        // lines already get.
        //
        // The cost, and it is the reason this is a setting: a line that does not go to the inner handler does not
        // reach Player.log either. It is still in LogOutput.log and the console. That is the trade already made for
        // our own output; this extends it to everyone else's.
        //
        // Runs AFTER ShouldSuppress on purpose. The our-line hop above deliberately precedes suppression, and reusing
        // it here would have quietly exempted every third-party line from the shader, missing-script and kinematic
        // filters.
        private bool TryRouteOtherModLine(LogType logType, string message)
        {
            if (_routeOtherModLines == null || !_routeOtherModLines.Value) return false;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return false;
            _ourModLineSource.Log(ToBepInExLevel(logType), message);
            return true;
        }

        // Our tagged lines go straight to BepInEx under the "Unity Log" source, so the console and LogOutput.log get each
        // one once, colored, without Unity's uncolored native echo. BepInEx only hears what the inner handler forwards, so
        // dropping them lost them everywhere. Off the main thread they keep the native path: BepInEx's console writer is
        // not thread-safe.
        private bool TryWriteOurModLineToBepInEx(LogType logType, string message)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId || !FiresLogColorPatch.IsOurModMessage(message)) return false;
            _ourModLineSource.Log(ToBepInExLevel(logType), message);
            return true;
        }

        private static LogLevel ToBepInExLevel(LogType logType)
        {
            switch (logType)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return LogLevel.Error;
                case LogType.Warning:
                    return LogLevel.Warning;
                default:
                    return LogLevel.Info;
            }
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (!VerbosePassThrough() && IsKnownNoFixNre(exception))
            {
                _suppressedValheimNreBugs++;
                AnnounceFirstNreSuppression();
                return;
            }
            RelayException(exception, context);
        }

        // Relay a logged exception WITHOUT stamping our log-handler chain across its stack trace. Reads as
        // "[FiresUnifiedCore] relayed exception from '<mod>': <type>: <msg>\n<original throw stack>", then
        // "---> <type>: <msg>\n<stack>" per inner exception, so a reader immediately sees which mod actually threw
        // and that FiresUnifiedCore is only the relay - not the source.
        //
        // We emit the exception's OWN captured stack as text and suppress Unity's live call-stack append for this one
        // write. That append is what was plastering every chained handler frame (FiresCore.Logging.RateLimitedLogHandler,
        // the per-mod RateLimitedLogHandler/LogFilter forks, …) onto unrelated mods' errors. This handler is the
        // innermost link in the chain, so suppressing the append here clears the whole chain's frames in one place.
        private void RelayException(Exception exception, UnityEngine.Object context)
        {
            if (exception == null) return;

            string origin = ResolveOriginThroughWrappers(exception);
            string body = $"{LogPrefix} relayed exception from '{origin}' (FiresUnifiedCore is only relaying this - the error is in that mod):\n"
                        + DescribeExceptionChain(exception);

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

        // TypeInitializationException and TargetInvocationException only wrap the real cause, so the cause's throw site names
        // the origin unless the wrapper's own throw site is more specific.
        private static string ResolveOriginThroughWrappers(Exception exception)
        {
            string ownOrigin = ResolveOriginAssembly(exception);
            if (!IsRuntimeWrapper(exception) || exception.InnerException == null) return ownOrigin;

            string causeOrigin = ResolveOriginThroughWrappers(exception.InnerException);
            return SpecificityOf(causeOrigin) >= SpecificityOf(ownOrigin) ? causeOrigin : ownOrigin;
        }

        private static bool IsRuntimeWrapper(Exception exception)
            => exception is TypeInitializationException || exception is TargetInvocationException;

        private enum OriginSpecificity { Unknown, UnityRuntime, NamedAssembly }

        private static OriginSpecificity SpecificityOf(string origin)
        {
            if (origin == UnknownOrigin) return OriginSpecificity.Unknown;
            return origin == UnityRuntimeOrigin ? OriginSpecificity.UnityRuntime : OriginSpecificity.NamedAssembly;
        }

        private static string DescribeExceptionChain(Exception exception)
        {
            var description = new StringBuilder();
            AppendTypeMessageAndStack(description, exception);
            for (Exception inner = exception.InnerException; inner != null; inner = inner.InnerException)
            {
                description.Append('\n').Append(InnerExceptionPrefix);
                AppendTypeMessageAndStack(description, inner);
            }
            return description.ToString();
        }

        private static void AppendTypeMessageAndStack(StringBuilder description, Exception exception)
        {
            description.Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            string stack = exception.StackTrace;
            if (!string.IsNullOrEmpty(stack)) description.Append('\n').Append(stack);
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
            return ParseTopTypeFromStackString(exception.StackTrace) ?? UnknownOrigin;
        }

        private static string Friendly(string assemblyName)
        {
            if (assemblyName == "assembly_valheim" || assemblyName == "assembly_utils" || assemblyName == "assembly_guiutils")
                return "Valheim";
            if (assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) || assemblyName == "mscorlib" || assemblyName.StartsWith("System", StringComparison.Ordinal))
                return UnityRuntimeOrigin;
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
                AnnounceFirstKinematicSuppression();
                return true;
            }
            if (logType == LogType.Warning && IsShaderBinaryWarning(message))
            {
                _suppressedShaderWarnings++;
                AnnounceFirstShaderSuppression();
                return true;
            }
            if (logType == LogType.Warning && IsMissingScriptWarning(message))
            {
                _suppressedMissingScriptWarnings++;
                AnnounceFirstMissingScriptSuppression();
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
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string stack = current.StackTrace;
                if (string.IsNullOrEmpty(stack)) continue;
                if (stack.Contains(ShieldGeneratorStackFragment)) return true;
                if (stack.Contains(ArcheryTargetStackFragment)) return true;
                if (stack.Contains(ExpandWorldPrefabDestroyFragment)) return true;
                if (stack.Contains(ExpandWorldTerrainCompilerFragment)) return true;
            }
            return false;
        }

        private void AnnounceFirstNreSuppression()
        {
            if (_suppressedValheimNreBugs != 1) return;
            _inner.LogFormat(LogType.Log, null, "{0}",
                $"{LogPrefix} Suppressing known no-fix NRE floods (vanilla ShieldGenerator/ArcheryTarget + ExpandWorld prefab-destroy + ExpandWorld terrain-compiler-during-pregen); the status box keeps the count. Set verbose to surface.");
        }

        private void AnnounceFirstShaderSuppression()
        {
            if (_suppressedShaderWarnings != 1) return;
            _inner.LogFormat(LogType.Log, null, "{0}",
                $"{LogPrefix} Suppressing shader binary-data warnings - VanillaAssetResolver handles the missing-on-export shaders; the status box keeps the count. Set verbose to surface.");
        }

        private void AnnounceFirstMissingScriptSuppression()
        {
            if (_suppressedMissingScriptWarnings != 1) return;
            _inner.LogFormat(LogType.Log, null, "{0}",
                $"{LogPrefix} Suppressing 'referenced script missing' warnings (bundle prefabs reference content-mod scripts not in this profile - cosmetic); the status box keeps the count. Set verbose to surface.");
        }

        private void AnnounceFirstKinematicSuppression()
        {
            if (_suppressedKinematicWarnings != 1) return;
            _inner.LogFormat(LogType.Log, null, "{0}",
                $"{LogPrefix} Suppressing kinematic-rigidbody velocity warnings (Valheim sets velocity on attached Characters - benign); the status box keeps the count. Set verbose to surface.");
        }
    }
}
