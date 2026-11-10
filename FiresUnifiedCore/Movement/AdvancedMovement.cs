using FiresCore.Logging;

namespace FiresCore.Movement
{
    /// <summary>
    /// 0.2.1043 (Fire 10-30: "All of our movement and special animations can stay in core, but they're getting their own mod, Fires Advanced
    /// Player Movement, and all of their configs go to that mod, and none of their code runs unless that mod is installed"): the one gate.
    /// Without FiresAdvancedPlayerMovement every Core movement / animation feature (climbing, vault, pull-up, clip slots, double jump, run
    /// slide, movement styles, crawling, low ceilings, first person, swimming) is off and movement is vanilla. Add-only contract.
    /// </summary>
    public static class AdvancedMovement
    {
        public const string Guid = "com.Fire.FiresAdvancedPlayerMovement";

        private static bool s_installed, s_logged;

        /// <summary>True when Fires Advanced Player Movement is loaded. Once seen it stays true (plugins never unload).</summary>
        public static bool Installed
        {
            get
            {
                if (s_installed) return true;
                s_installed = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
                if (!s_logged && (s_installed || FiresCore.Config.CoreFeatures.Resolved))
                {
                    s_logged = true;
                    FiresLogger.LogInfo(s_installed
                        ? "[Movement] Fires Advanced Player Movement installed: custom movement on"
                        : "[Movement] Fires Advanced Player Movement not installed: player movement is vanilla");
                }
                return s_installed;
            }
        }
    }
    /// <summary>0.2.1137 (Fire: "core can help handle this cleanly honestly", "that is the purpose of core"; builders run neither mod): the one place mods ask whether an optional owner is loaded, else use vanilla's method.
    /// Add-only. <see cref="Skip"/> says once at Debug what was left to vanilla.</summary>
    public static class OwnerMods
    {
        private static readonly System.Collections.Generic.HashSet<string> s_said = new System.Collections.Generic.HashSet<string>();
        public static bool Movement => AdvancedMovement.Installed;
        public static bool Classes => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FiresCore.Config.OwnerGatedConfig.Owners.RpgClassesGuid);
        public static void Skip(string owner, string what) { if (s_said.Add(owner + what)) FiresLogger.LogDebug($"[Owners] {owner} not installed: {what}"); }
    }
}
