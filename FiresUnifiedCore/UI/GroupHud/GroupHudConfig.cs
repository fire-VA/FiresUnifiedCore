using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// Client-side layout config for the shared group HUD. NOT server-synced — it's a per-client
    /// HUD preference, like the vanilla HUD scale.
    /// 0.2.1069 ([ghost], lead 10-31, Fire's "Core = our Jotunn" rule): the keys live in the owner mod (FiresRPGmaker binds
    /// [GroupHud] ShowGroupHud / MaxRowsPerColumn / Columns / DefaultPosX / DefaultPosY under the same names, copies a value saved
    /// in Core's cfg once, and hands them to Core with CoreSettings.Provide). Core no longer binds them; it reads the owner's live
    /// value through CoreSettings.Get, falling back to the old defaults when no owner provides them. The panel re-applies the
    /// layout when a read value changes (polled, GroupHudController.PollLayout).
    /// The ConfigEntry fields stay (add-only API) and are null now.
    /// </summary>
    public static class GroupHudConfig
    {
        public const string Section = "GroupHud";

        public static ConfigEntry<bool> ShowGroupHud;
        public static ConfigEntry<int> MaxRowsPerColumn;
        public static ConfigEntry<int> Columns;
        public static ConfigEntry<float> DefaultPosX;
        public static ConfigEntry<float> DefaultPosY;

        public static bool Show => FiresCore.Config.CoreSettings.Get(Section, "ShowGroupHud", true);
        public static int Rows => FiresCore.Config.CoreSettings.Get(Section, "MaxRowsPerColumn", 8);
        public static int Cols => FiresCore.Config.CoreSettings.Get(Section, "Columns", 1);

        /// <summary>Kept for callers; binds nothing now (the owner mod binds the keys).</summary>
        public static void Initialize(ConfigFile config) { }

        public static Vector2 DefaultPosition =>
            new Vector2(FiresCore.Config.CoreSettings.Get(Section, "DefaultPosX", -10f), FiresCore.Config.CoreSettings.Get(Section, "DefaultPosY", -200f));
    }
}
