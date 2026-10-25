using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// Crawling (default off), guarded so that if both Core and VikHavn are installed only
    /// one mod handles the crawling"). Setting [Crawling] Crawling = Auto | On | Off, default Auto: on only when VikHavn Combat Moveset
    /// Additions is installed, since it crawls too. ONE owner drives the pose:
    /// - VikHavn installed: VikHavn owns crawling (its capsule scale, crawl clips, speed status effect, camera lowering). Core only asks it
    ///   to go prone, through VikHavn's own SetProne, and back. No second capsule change, no second animation, no edit to VikHavn.
    /// - VikHavn absent: Core's own crawl, on the local player: VikHavn's prone pose ported into Core (CrawlPose: capsule at
    ///   CoreHeightShare of standing on every peer, crawl clips, the prone speed effect), the camera pivot lowered with the body.
    /// Core asks for a crawl only where a crouched body doesn't fit (Headroom), and only while crawling is on.
    /// Debug: "[Crawl] owner: VikHavn (Core delegates its low-ceiling crawl)" / "owner: Core", and "[Crawl] begin …" / "end …".
    /// </summary>
    public static class Crawl
    {
        public enum CrawlingMode { Auto, On, Off }

        public const string VikHavnGuid = "VikHavn.CombatMovesetAdditions";
        /// <summary>A crouched body's height as a share of the standing capsule (where a crawl starts).</summary>
        public const float CrouchShare = 0.75f;
        /// <summary>Core's own crawl: the prone capsule height share (CrawlPose).</summary>
        public const float CoreHeightShare = FiresCore.Movement.CrawlPose.CrawlPose.ProneHeight;

        private static ConfigEntry<CrawlingMode> s_mode;
        private static int s_resolvedFor = -1;
        private static bool s_enabled, s_vikHavn;
        private static bool s_active, s_coreCrawling;
        private static float s_standingCapsule = -1f;
        private static Vector3 s_standingCenter;
        private static MethodInfo s_vikSetProne;
        private static bool s_vikLookedUp;

        internal static void Bind(ConfigFile config)
        {
            s_mode = config.Bind("Crawling", "Crawling", CrawlingMode.Auto,
                "Whether the player drops to a crawl where a crouch doesn't fit under a low ceiling. Auto: on when Fires Admin Terrain, Fires RPG Classes or VikHavn Combat Moveset Additions is installed (with VikHavn Combat Moveset Additions installed, VikHavn drives the crawl pose). On / Off: always.");
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Crawling, false, s_mode);   // FAT, RPGClasses or VikHavn
            s_mode.SettingChanged += (sender, args) =>
            {
                s_resolvedFor = -1;
                if (!Enabled && s_active) End(Player.m_localPlayer, "crawling is off (config)");
            };
        }

        /// <summary>True when Core may drop the player to a crawl.</summary>
        public static bool Enabled
        {
            get
            {
                CrawlingMode mode = s_mode != null ? s_mode.Value : CrawlingMode.Auto;
                if (s_resolvedFor == (int)mode && FiresCore.Config.CoreFeatures.Resolved) return s_enabled;
                s_vikHavn = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(VikHavnGuid);
                s_enabled = mode == CrawlingMode.On || (mode == CrawlingMode.Auto && FiresCore.Config.CoreFeatures.IsLive(FiresCore.Config.CoreFeatures.Crawling));
                s_resolvedFor = (int)mode;
                string why = mode == CrawlingMode.Auto ? $"Auto: FAT, RPGClasses or VikHavn {(s_enabled ? "installed" : "not installed")}" : $"{mode} (config)";
                FiresCore.Logging.FiresLogger.LogDebug($"[Crawl] crawling {(s_enabled ? "ON" : "OFF")} ({why}); owner: " +
                                                       (s_vikHavn ? "VikHavn (Core delegates its low-ceiling crawl)" : "Core"));
                return s_enabled;
            }
        }

        public static bool Active => s_active;
        internal static bool CoreCrawling => s_coreCrawling;

        /// <summary>The standing capsule height (m), read while the body stands.</summary>
        internal static float StandingCapsule(Player player)
        {
            var capsule = player.GetCollider();
            if (s_standingCapsule < 0f || (!s_coreCrawling && !player.IsCrouching() && capsule != null))
            {
                if (capsule != null) { s_standingCapsule = capsule.height; s_standingCenter = capsule.center; }
                else s_standingCapsule = 1.85f;
            }
            return s_standingCapsule;
        }

        internal static void Begin(Player player, string why)
        {
            if (s_active || player == null) return;
            bool ok = s_vikHavn ? VikHavnProne(player, true) : CoreBegin(player);
            if (!ok) return;
            s_active = true;
            FiresCore.Logging.FiresLogger.LogDebug($"[Crawl] begin ({why}); driven by {(s_vikHavn ? "VikHavn" : "Core")}");
        }

        internal static void End(Player player, string why)
        {
            if (!s_active) return;
            s_active = false;
            if (s_coreCrawling) CoreEnd(player);
            else if (player != null) VikHavnProne(player, false);
            if (why != null) FiresCore.Logging.FiresLogger.LogDebug($"[Crawl] end ({why})");
        }

        // Core's own crawl: VikHavn's prone pose, ported (CrawlPose): the synced prone flag lowers the capsule and swaps
        // in the crawl clips on every peer, and the prone effect sets the speed.
        private static bool CoreBegin(Player player)
        {
            StandingCapsule(player);
            if (!FiresCore.Movement.CrawlPose.CrawlPose.SetProne(player, true, "low ceiling")) return false;
            s_coreCrawling = true;
            return true;
        }

        private static void CoreEnd(Player player)
        {
            s_coreCrawling = false;
            if (player != null) FiresCore.Movement.CrawlPose.CrawlPose.SetProne(player, false, "ceiling lifted");
        }

        // VikHavn owns the pose: its own prone switch (LocalProneController.SetProne writes its ZDO flag and its speed effect). Prone ends
        // there as soon as the crouch is released, which Headroom does when the ceiling lifts.
        private static bool VikHavnProne(Player player, bool prone)
        {
            if (!s_vikLookedUp)
            {
                s_vikLookedUp = true;
                var type = AccessTools.TypeByName("VikHavnCombat.Modules.Stance.LocalProneController");
                s_vikSetProne = type != null ? AccessTools.Method(type, "SetProne", new[] { typeof(Player), typeof(bool) }) : null;
                if (s_vikSetProne == null) Debug.LogWarning("[Crawl] VikHavn is installed but its prone switch wasn't found; Core won't crawl");
            }
            if (s_vikSetProne == null) return false;
            try { s_vikSetProne.Invoke(null, new object[] { player, prone }); return true; }
            catch (Exception ex) { Debug.LogWarning($"[Crawl] VikHavn's prone switch threw: {ex.InnerException?.Message ?? ex.Message}"); return false; }
        }
    }
}
