using BepInEx.Configuration;
using HarmonyLib;

namespace FiresCore.Lifecycle
{
    /// <summary>
    /// Skips the intro video the game plays on its way to the main menu.
    ///
    /// <para>Vanilla gates it on a field rather than a setting: <c>FejdStartup</c> plays
    /// <c>CinematicsManager.Settings.Intro</c> when <c>!Game.m_hasStartedOnce</c> and the scene-wired
    /// <c>CinematicsManager.m_introOnStartup</c> is true, and <c>Game</c> does the same on a new world through
    /// <c>m_introOnNewWorld</c>. Both fields are public, so the whole job is writing them before anything reads
    /// them - no need to intercept the player, the video, or the menu flow.</para>
    ///
    /// <para>The vanilla values are captured per instance and re-applied, so turning the setting back off restores
    /// exactly what the scene shipped instead of leaving the fields latched false.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class IntroCinematicSkip
    {
        private const string Section = "General";
        private const string Key = "Skip Intro Cinematic";
        private const string Description =
            "OFF by default. Skips the intro video on the way to the main menu, and the one a brand new world plays. " +
            "Nothing else about cinematics changes - dreams, the outro and the credits still play, and the console " +
            "'cinematic' command still works. This machine only; read live, and since the video only ever plays on " +
            "the way in, a change lands on the next launch.";

        private static ConfigEntry<bool> s_skip;
        private static CinematicsManager s_manager;
        private static bool s_vanillaOnStartup;
        private static bool s_vanillaOnNewWorld;

        internal static void Initialize(ConfigFile config)
        {
            if (config == null) return;
            s_skip = config.Bind(Section, Key, false, Description);
            s_skip.SettingChanged += (_, __) => Apply();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CinematicsManager), "Awake")]
        private static void CinematicsManager_Awake_Postfix(CinematicsManager __instance)
        {
            if (__instance == null) return;
            s_manager = __instance;
            s_vanillaOnStartup = __instance.m_introOnStartup;
            s_vanillaOnNewWorld = __instance.m_introOnNewWorld;
            Apply();
        }

        private static void Apply()
        {
            CinematicsManager manager = s_manager;
            if (manager == null) return;
            bool skip = s_skip != null && s_skip.Value;
            manager.m_introOnStartup = !skip && s_vanillaOnStartup;
            manager.m_introOnNewWorld = !skip && s_vanillaOnNewWorld;
        }
    }
}
