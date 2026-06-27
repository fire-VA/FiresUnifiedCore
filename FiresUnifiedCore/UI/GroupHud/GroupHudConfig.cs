using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// Client-side layout config for the shared group HUD. NOT server-synced — it's a per-client
    /// HUD preference, like the vanilla HUD scale. The live panel listens to SettingChanged so edits
    /// via the config file / Configuration Manager apply without a restart.
    /// </summary>
    public static class GroupHudConfig
    {
        public static ConfigEntry<bool> ShowGroupHud;
        public static ConfigEntry<int> MaxRowsPerColumn;
        public static ConfigEntry<int> Columns;
        public static ConfigEntry<float> DefaultPosX;
        public static ConfigEntry<float> DefaultPosY;

        public static void Initialize(ConfigFile config)
        {
            ShowGroupHud = config.Bind(
                "GroupHud", "ShowGroupHud", true,
                "Show the group HUD — the below-minimap panel listing tracked members (companions, " +
                "party, …) with name, distance and health/stamina/eitr bars.");

            MaxRowsPerColumn = config.Bind(
                "GroupHud", "MaxRowsPerColumn", 8,
                new ConfigDescription(
                    "Rows shown per column before the panel starts scrolling.",
                    new AcceptableValueRange<int>(1, 20)));

            Columns = config.Bind(
                "GroupHud", "Columns", 1,
                new ConfigDescription(
                    "Number of side-by-side columns. 1 = single list, 2 = two columns.",
                    new AcceptableValueRange<int>(1, 2)));

            DefaultPosX = config.Bind(
                "GroupHud", "DefaultPosX", -10f,
                "Default panel X anchored to the screen's top-right corner (negative = left of the " +
                "corner). Only used until you drag the panel; your dragged position then wins.");

            DefaultPosY = config.Bind(
                "GroupHud", "DefaultPosY", -200f,
                "Default panel Y anchored to the screen's top-right corner (negative = below the " +
                "corner, i.e. under the minimap).");
        }

        public static Vector2 DefaultPosition =>
            new Vector2(DefaultPosX?.Value ?? -10f, DefaultPosY?.Value ?? -200f);
    }
}
