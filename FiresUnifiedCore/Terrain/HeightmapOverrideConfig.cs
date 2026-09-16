using System;
using BepInEx.Configuration;

using FiresCore.Sync;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Config entries for the built-in heightmap override module.
    ///
    /// Replaces the external HeightmapUnlimited (Jotunn-dependent) mod.
    /// Allows server admins to configure how far terrain can be raised or
    /// lowered beyond Valheim's default ±8 unit clamp.
    ///
    /// All entries are server-locked via ConfigSync.
    /// </summary>
    public static class HeightmapOverrideConfig
    {
        // Config Entries
        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<float> MaxHeight;
        public static ConfigEntry<float> MinHeight;

        // Accessor Helpers (used directly by transpiler call targets)

        /// <summary>Returns the configured minimum height delta (negative value).</summary>
        public static float Min() => MinHeight.Value;

        /// <summary>Returns the absolute value of the configured minimum height delta.</summary>
        public static float MinAbs() => Math.Abs(MinHeight.Value);

        /// <summary>Returns the configured maximum height delta (positive value).</summary>
        public static float Max() => MaxHeight.Value;

        // Initialization

        /// <summary>
        /// Bind all heightmap override config entries.
        /// Called from FiresUnifiedCore config init.
        /// </summary>
        public static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                "HeightmapOverride", "Enabled", true,
                "Enable the built-in heightmap limit override. " +
                "When true, terrain can be raised/lowered beyond Valheim's default ±8 unit clamp. " +
                "[Synced with Server]");

            MaxHeight = config.Bind(
                "HeightmapOverride", "MaxHeight", 1000f,
                new ConfigDescription(
                    "How high terrain can be stacked relative to its original position. " +
                    "Replaces Valheim's hard-coded +8 limit. [Synced with Server]",
                    new AcceptableValueRange<float>(1f, 1000f)));

            MinHeight = config.Bind(
                "HeightmapOverride", "MinHeight", -1000f,
                new ConfigDescription(
                    "How far terrain can be dug down relative to its original position. " +
                    "Replaces Valheim's hard-coded -8 limit. [Synced with Server]",
                    new AcceptableValueRange<float>(-1000f, -1f)));
        }

        /// <summary>
        /// Register all entries with the ConfigSync instance so that server values
        /// override client values. Called from FiresUnifiedCore config sync.
        /// </summary>
        public static void BindToSync(ConfigSync configSync)
        {
            configSync.AddConfigEntry(Enabled);
            configSync.AddConfigEntry(MaxHeight);
            configSync.AddConfigEntry(MinHeight);
        }
    }
}
