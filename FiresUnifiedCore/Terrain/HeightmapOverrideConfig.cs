using BepInEx.Configuration;

using FiresCore.Sync;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Config for the built-in heightmap override: how far terrain may be raised or lowered from its generated
    /// height. All entries are server-locked via ConfigSync. Read at call time by <see cref="HeightmapOverrideLimits"/>.
    /// </summary>
    public static class HeightmapOverrideConfig
    {
        private const string Section = "HeightmapOverride";
        private const float HeightLimitRange = 1000f;
        private const float MinimumLimitMagnitude = 1f;

        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<float> MaxHeight;
        public static ConfigEntry<float> MinHeight;
        internal static ConfigSync Sync;

        public static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                Section, "Enabled", true,   // Core 0.2.1086 (Fire 11-02, Tools\CONFIG_PROPOSAL_20261102.md): back in Core, default ON
                "Lift Valheim's 8 m limit on raising and lowering terrain, to the MaxHeight / MinHeight below. " +
                "On a client it only applies when the server runs Core with this on (a vanilla server keeps vanilla limits). " +
                "Applies live, no restart. The log reports the limits in force. [Synced with Server]");

            MaxHeight = config.Bind(
                Section, "MaxHeight", HeightLimitRange,
                new ConfigDescription(
                    "How far terrain can be raised above its generated height, in metres. Vanilla is 8. [Synced with Server]",
                    new AcceptableValueRange<float>(MinimumLimitMagnitude, HeightLimitRange)));

            MinHeight = config.Bind(
                Section, "MinHeight", -HeightLimitRange,
                new ConfigDescription(
                    "How far terrain can be dug below its generated height, in metres (negative). Vanilla is -8. [Synced with Server]",
                    new AcceptableValueRange<float>(-HeightLimitRange, -MinimumLimitMagnitude)));

            Enabled.SettingChanged += OnLimitsChanged;
            MaxHeight.SettingChanged += OnLimitsChanged;
            MinHeight.SettingChanged += OnLimitsChanged;
        }

        public static void BindToSync(ConfigSync configSync)
        {
            // 0.2.971 (Fire 10-28: "it needs to stay off if the mod is not installed on the server as well"): a client follows the
            // override only once the server's Core config package arrived (IsSourceOfTruth false); a vanilla server never sends one
            Sync = configSync;
            configSync.SourceOfTruthChanged += _ => { HeightmapOverrideStatus.ReportLimits(); HeightmapOverrideReapply.Check("server config arrived / left"); };
            configSync.AddConfigEntry(Enabled);
            configSync.AddConfigEntry(MaxHeight);
            configSync.AddConfigEntry(MinHeight);
        }

        private static void OnLimitsChanged(object sender, System.EventArgs e) { HeightmapOverrideStatus.ReportLimits(); HeightmapOverrideReapply.Check("a limit setting changed"); }
    }
}
