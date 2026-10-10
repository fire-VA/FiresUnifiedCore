using FiresCore.Config;

namespace FiresCore.Logging
{
    // FiresUnifiedCore's own tagged logger. Thin wrapper over the shared FiresLog engine;
    // consuming mods define their own equivalent with their own tag rather than calling this.
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresUnifiedCore",
            () => ConfigManager.Instance?.configVerboseLogging?.Value ?? false);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        /// <summary>0.2.279: a per-action evidence line, shown only at [Logging] Log Level = Debug or Verbose (see FiresLog.Debug).</summary>
        public static void LogDebug(string message) => Log.Debug(message);

        /// <summary>0.2.279: true when per-action (debug) lines print; build an expensive line only when it is.</summary>
        public static bool DebugEnabled => FiresLogLevel.DebugOn;

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
