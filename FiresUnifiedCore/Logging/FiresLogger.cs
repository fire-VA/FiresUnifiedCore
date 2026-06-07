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

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
