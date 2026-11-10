using BepInEx.Configuration;
using System;

namespace FiresCore.Config
{
    // Base ConfigManager hosting the universal entries every consumer needs:
    // verbose-log flag and the server-authority lock for ConfigSync.
    // Consuming mods extend with their own ConfigEntry fields and Bind calls.
    public sealed class ConfigManager : IDisposable
    {
        private const string GeneralSection = "General";
        private const string DebugSection = "Debug";
        private const string VerboseLoggingKey = "VerboseLogging";
        private const string ServerAuthorityKey = "ServerAuthority";
        private const string CompanionFollowDiagKey = "CompanionFollowDiagnostics";
        private const string CompanionVerboseKey = "CompanionVerboseLogging";
        private const string CompanionJobVerboseKey = "CompanionJobVerboseLogging";
        private const string VerboseLoggingDescription = "Enable verbose log output.";
        private const string ServerAuthorityDescription =
            "When true, the server forces clients to match its config (ConfigSync lock).";
        private const string CompanionFollowDiagDescription =
            "Dev diagnostic (off by default): throttled per-companion follow speed/distance log ([CompanionFollowDiag]).";
        private const string CompanionVerboseDescription =
            "Dev diagnostic (off by default): per-companion stats, food and mead, combat, skills and equipment logging.";
        private const string CompanionJobVerboseDescription =
            "Dev diagnostic (off by default): per-companion idle and job logging. Separate from CompanionVerboseLogging because jobs log far more.";

        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): the companion diagnostics and gear-up keys are bound by
        // FiresCompanions; Core reads the owner's value (old default without it). Same member names; .Value still reads.
        public OwnedSetting<bool> configCompanionFollowDiag = new OwnedSetting<bool>(DebugSection, CompanionFollowDiagKey, false);
        public OwnedSetting<bool> configCompanionVerbose = new OwnedSetting<bool>(DebugSection, CompanionVerboseKey, false);
        public ConfigEntry<bool> configPeriodicDiagnostics;   // 1086 ([generator], Fire 11-01: configs live in their own mods): stays in Core (proposal 11-02, question 14)
        public OwnedSetting<bool> configCompanionJobVerbose = new OwnedSetting<bool>(DebugSection, CompanionJobVerboseKey, false);
        public ConfigEntry<bool> configPerfLog;
        // Companions gear up from base chests / craft their own clothing and tools.
        public OwnedSetting<bool> configGearUp = new OwnedSetting<bool>("Companions", "Companions gear up from base chests", true);
        public OwnedSetting<int> configSelfCraftPerDay = new OwnedSetting<int>("Companions", "Companion self-craft per day", 1),
            configSelfCraftKeep = new OwnedSetting<int>("Companions", "Self-craft keeps materials", 5);

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            configVerboseLogging = config.Bind(GeneralSection, VerboseLoggingKey, false, VerboseLoggingDescription);
            configServerAuthority = config.Bind(GeneralSection, ServerAuthorityKey, true, ServerAuthorityDescription);
            // 1086 ([generator], Fire 11-01: configs live in their own mods): the 4 Debug companion keys and the 3 gear-up keys are no longer bound here.
            configPeriodicDiagnostics = config.Bind(DebugSection, "PeriodicDiagnostics", false,
                "Dev diagnostic (off by default): periodic timing lines (companion census passes, the teleport census/heartbeat split, the kinematic sync summary). They also need the Core log level at Debug.");
            FiresCore.Logging.Diag.Periodic = configPeriodicDiagnostics.Value;
            configPeriodicDiagnostics.SettingChanged += (_, __) => FiresCore.Logging.Diag.Periodic = configPeriodicDiagnostics.Value;
            // A "[Perf]" line every 10 s on a client; a test round turns it on
            configPerfLog = config.Bind(DebugSection, "PerfLog", false,
                "Dev diagnostic (off by default): every 10 s one [Perf] line with the average / worst frame time and FPS (clients only).");

            // The companion classes keep their verbose flags as statics, so drive them from here and on every change.
            Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
            configCompanionVerbose.Changed += () => Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
            configCompanionJobVerbose.Changed += () => Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
        }

        public void OnConfigLockChanged()
        {
            try { ConfigLockChanged?.Invoke(); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FiresUnifiedCore] ConfigLockChanged subscriber threw: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _instance = null;
        }
    }
}
