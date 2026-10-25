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
        public ConfigEntry<bool> configCompanionFollowDiag;
        public ConfigEntry<bool> configCompanionVerbose;
        public ConfigEntry<bool> configPeriodicDiagnostics;
        public ConfigEntry<bool> configCompanionJobVerbose;
        public ConfigEntry<bool> configPerfLog;
        // Companions gear up from base chests / craft their own clothing and tools.
        public ConfigEntry<bool> configGearUp;
        public ConfigEntry<int> configSelfCraftPerDay, configSelfCraftKeep;

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            configVerboseLogging = config.Bind(GeneralSection, VerboseLoggingKey, false, VerboseLoggingDescription);
            configServerAuthority = config.Bind(GeneralSection, ServerAuthorityKey, true, ServerAuthorityDescription);
            configCompanionFollowDiag = config.Bind(DebugSection, CompanionFollowDiagKey, false, CompanionFollowDiagDescription);
            configCompanionVerbose = config.Bind(DebugSection, CompanionVerboseKey, false, CompanionVerboseDescription);
            // Diagnostic and periodic lines go behind a toggle that is OFF until needed.
            configPeriodicDiagnostics = config.Bind(DebugSection, "PeriodicDiagnostics", false,
                "Dev diagnostic (off by default): periodic timing lines (companion census passes, the teleport census/heartbeat split, the kinematic sync summary). They also need the Core log level at Debug.");
            FiresCore.Logging.Diag.Periodic = configPeriodicDiagnostics.Value;
            configPeriodicDiagnostics.SettingChanged += (_, __) => FiresCore.Logging.Diag.Periodic = configPeriodicDiagnostics.Value;
            configCompanionJobVerbose = config.Bind(DebugSection, CompanionJobVerboseKey, false, CompanionJobVerboseDescription);
            // Companions gear up from base chests / craft their own armour
            configGearUp = config.Bind("Companions", "Companions gear up from base chests", true,
                "An idle companion at base takes better armour (or a cape) from base chests you can open, puts it on and puts its old piece back. Never from private chests, chests behind a door it may not open, hoppers, carts, ships, your inventory or another companion.");
            configSelfCraftPerDay = config.Bind("Companions", "Companion self-craft per day", 1,
                new ConfigDescription("When no spare fits, a companion may craft itself a better armour piece this many times per in-game day, from base chests at a station in the base, using only recipes its owner knows. 0 = never.", new AcceptableValueRange<int>(0, 5)));
            configSelfCraftKeep = config.Bind("Companions", "Self-craft keeps materials", 5,
                new ConfigDescription("Self-crafting never uses the last this-many of any material in the base chests.", new AcceptableValueRange<int>(0, 100)));
            // A "[Perf]" line every 10 s on a client; a test round turns it on
            configPerfLog = config.Bind(DebugSection, "PerfLog", false,
                "Dev diagnostic (off by default): every 10 s one [Perf] line with the average / worst frame time and FPS (clients only).");
            // Companion diagnostics leave the config without a companion host.
            OwnerGatedConfig.Gate(OwnerGatedConfig.Owners.COMP, false, configCompanionFollowDiag, configCompanionVerbose, configCompanionJobVerbose);

            // The companion classes keep their verbose flags as statics, so drive them from here and on every change.
            Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
            configCompanionVerbose.SettingChanged += (_, __) => Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
            configCompanionJobVerbose.SettingChanged += (_, __) => Npc.CompanionDebugLogging.Apply(configCompanionVerbose.Value, configCompanionJobVerbose.Value);
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
