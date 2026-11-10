using BepInEx.Configuration;
using HarmonyLib;

namespace FiresCore.Lifecycle
{
    /// <summary>
    /// Skips the intro cutscene a brand new world plays when you first enter it.
    ///
    /// <para>0.2.966 (Fire 10-28: "change the toggle name and the function so that it no longer skips the intro cinematic but still works
    /// to auto skip the beginning cut scene entering a world"). The video on the way to the main menu is vanilla's own setting now
    /// (Settings > Gameplay, "Skip intro cinematic": FejdStartup.TryPlayIntroCinematic reads PlatformPrefs "SkipIntroCinematic"), so Core
    /// no longer touches it. The new-world intro has no vanilla setting: <c>Game</c> plays it through the scene-wired public
    /// <c>CinematicsManager.m_introOnNewWorld</c>, so the whole job is writing that field before anything reads it.</para>
    ///
    /// <para>The vanilla value is captured per instance and re-applied, so turning the setting off restores exactly what the scene shipped.
    /// The old key [General] "Skip Intro Cinematic" is carried over once (a hidden marker) and dropped from the cfg.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class IntroCinematicSkip
    {
        private const string Section = "General";
        private const string Key = "Skip new world intro cutscene";
        private const string OldKey = "Skip Intro Cinematic";
        private const string MigratedKey = "Skip new world intro migrated";
        private const string Description =
            "Skips the cutscene a brand new world plays when you first enter it. The intro video on the way to the main menu is " +
            "vanilla's own setting (Settings > Gameplay > Skip intro cinematic). Dreams, the outro and the credits still play, and the " +
            "console 'cinematic' command still works. This machine only; read live.";

        private static ConfigEntry<bool> s_skip;
        private static CinematicsManager s_manager;
        private static bool s_vanillaOnNewWorld;

        internal static void Initialize(ConfigFile config)
        {
            if (config == null) return;
            s_skip = config.Bind(Section, Key, false, Description);
            MigrateOldKey(config);
            s_skip.SettingChanged += (_, __) => Apply();
        }

        // The old key is no longer bound, so its saved value sits in the file's orphan table: carry an ON over once, then drop it.
        private static void MigrateOldKey(ConfigFile config)
        {
            var migrated = config.Bind(Section, MigratedKey, false, new ConfigDescription(
                "Set by Core once the old Skip Intro Cinematic value was carried over. Leave it alone.", null,
                new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true, Browsable = false }));
            if (migrated.Value) return;
            string old = null;
            var orphans = FiresCore.Movement.MovementFeature.Orphans(config);
            var oldDefinition = new ConfigDefinition(Section, OldKey);
            if (orphans != null && orphans.TryGetValue(oldDefinition, out old)) orphans.Remove(oldDefinition);
            bool wasOn = old != null && bool.TryParse(old.Trim(), out bool on) && on;
            if (wasOn) s_skip.Value = true;
            migrated.Value = true;
            try { config.Save(); } catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"[IntroSkip] migrated [{Section}] {OldKey} = {(old ?? "absent")} -> {Key} = {s_skip.Value}"
                + " (the main-menu video is vanilla's own Skip intro cinematic setting now)");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CinematicsManager), "Awake")]
        private static void CinematicsManager_Awake_Postfix(CinematicsManager __instance)
        {
            if (__instance == null) return;
            s_manager = __instance;
            s_vanillaOnNewWorld = __instance.m_introOnNewWorld;
            Apply();
        }

        private static void Apply()
        {
            CinematicsManager manager = s_manager;
            if (manager == null) return;
            bool skip = s_skip != null && s_skip.Value;
            manager.m_introOnNewWorld = !skip && s_vanillaOnNewWorld;
            if (skip && s_vanillaOnNewWorld)
                FiresCore.Logging.FiresLogger.LogDebug($"[IntroSkip] the new world intro cutscene will not play ([{Section}] {Key} = on)");
        }
    }
}
