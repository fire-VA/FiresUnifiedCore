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
        public ConfigEntry<bool> configCompanionJobVerbose;

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            configVerboseLogging = config.Bind(GeneralSection, VerboseLoggingKey, false, VerboseLoggingDescription);
            configServerAuthority = config.Bind(GeneralSection, ServerAuthorityKey, true, ServerAuthorityDescription);
            configCompanionFollowDiag = config.Bind(DebugSection, CompanionFollowDiagKey, false, CompanionFollowDiagDescription);
            configCompanionVerbose = config.Bind(DebugSection, CompanionVerboseKey, false, CompanionVerboseDescription);
            configCompanionJobVerbose = config.Bind(DebugSection, CompanionJobVerboseKey, false, CompanionJobVerboseDescription);

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
