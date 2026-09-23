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

        public static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                Section, "Enabled", true,
                "Lift Valheim's ±8 m limit on raising and lowering terrain, to the MaxHeight / MinHeight below. " +
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
            configSync.AddConfigEntry(Enabled);
            configSync.AddConfigEntry(MaxHeight);
            configSync.AddConfigEntry(MinHeight);
        }

        private static void OnLimitsChanged(object sender, System.EventArgs e) => HeightmapOverrideStatus.ReportLimits();
    }
}
