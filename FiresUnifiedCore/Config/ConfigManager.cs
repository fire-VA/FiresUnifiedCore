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
        private const string VerboseLoggingKey = "VerboseLogging";
        private const string ServerAuthorityKey = "ServerAuthority";
        private const string VerboseLoggingDescription = "Enable verbose log output.";
        private const string ServerAuthorityDescription =
            "When true, the server forces clients to match its config (ConfigSync lock).";

        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;

        public event Action ConfigLockChanged;

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            configVerboseLogging = config.Bind(GeneralSection, VerboseLoggingKey, false, VerboseLoggingDescription);
            configServerAuthority = config.Bind(GeneralSection, ServerAuthorityKey, true, ServerAuthorityDescription);
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
