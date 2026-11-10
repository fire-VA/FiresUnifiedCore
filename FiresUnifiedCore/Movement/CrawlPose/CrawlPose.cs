using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement.CrawlPose
{
    /// <summary>
    /// Core's own crawl pose, a copy of what already works in VikHavn. A port of
    /// VikHavn Combat Moveset Additions 1.0.10's prone stance (Modules\Stance + the crawl part of Modules\Common), used only when VikHavn
    /// is NOT installed; with VikHavn installed VikHavn drives every crawl (Crawl.cs asks its prone switch) and none of this loads or runs.
    /// - Prone: holding Crouch past "Prone hold seconds" while crouched, or Core's low-ceiling crawl (Headroom -> Crawl.Begin).
    /// - The owner writes FiresCore_Prone / FiresCore_ProneSpeedMultiplier on the player's ZDO (VikHavn's keys are VikHavnCombat_*);
    ///   every peer reads them to lower the capsule and swap the crouch-tree clips for the crawl clips (CrawlPoseStance).
    /// - Speed and stealth ride one status effect, SE_FiresCoreProne, as in VikHavn.
    /// - The clips are Core's own copy of VikHavn's bundle (embedded "firescorecrawl"). Unity loads a bundle's internal name once per
    ///   session, so the VikHavn check happens BEFORE the load, at the first Player.Awake (every plugin has loaded by then).
    /// Debug: "[CrawlPose] clips loaded ..." / "skipped (VikHavn drives the crawl)", "[CrawlPose] prone ON/OFF (why)".
    /// </summary>
    public static class CrawlPose
    {
        private const string BundleResourceSuffix = "firescorecrawl";
        private const string CrawlIdleClipName = "Crawl_Idle_Loop";
        private const string CrawlForwardClipName = "Crawl_Fwd_Loop";
        private const string SneakClipNameFragment = "Sneak";
        private const string CrouchClipNameFragment = "Crouch";

        // VikHavn's "Sneak Stance" defaults, fixed here: the same look is the point.
        public const float ProneHeight = 0.5f;
        public const float ProneVisibilityReduction = 0.5f;
        public const float ProneSpeedMultiplier = 0.3f, ProneSpeedMultiplierAtMaxSkill = 0.5f;

        private static readonly int ProneKey = "FiresCore_Prone".GetStableHashCode();
        private static readonly int ProneSpeedMultiplierKey = "FiresCore_ProneSpeedMultiplier".GetStableHashCode();
        private const string EffectAssetName = "SE_FiresCoreProne";
        private static readonly int EffectNameHash = EffectAssetName.GetStableHashCode();

        internal static readonly AccessTools.FieldRef<Character, ZNetView> NetView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<Character, Animator> AnimatorOf = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        internal static readonly AccessTools.FieldRef<Character, ZSyncAnimation> SyncAnimationOf = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
        internal static readonly AccessTools.FieldRef<Player, bool> CrouchToggled = AccessTools.FieldRefAccess<Player, bool>("m_crouchToggled");

        private static readonly FiresCore.Config.OwnedSetting<float> s_holdSeconds = new FiresCore.Config.OwnedSetting<float>("Crawling", "Prone hold seconds", 0.6f), s_strideLength = new FiresCore.Config.OwnedSetting<float>("Crawling", "Crawl stride length", 0.96f);   // 1086 ([generator], Fire 11-01: configs live in their own mods): owner FAPM
        private static bool s_decided, s_coreDrives;
        private static AnimationClip s_crawlIdle, s_crawlForward;
        private static SE_Stats s_effect;

        internal static void Bind(ConfigFile config)
        {
            // 1086 ([generator], Fire 11-01: configs live in their own mods): [Crawling] Prone hold seconds / Crawl stride length are the owner's (FAPM); no Core bind
        }

        internal static float HoldSeconds => s_holdSeconds.Value;
        internal static float StrideLength => s_strideLength.Value;

        /// <summary>True when Core drives the crawl pose (VikHavn absent). Decided once, at the first player.</summary>
        public static bool CoreDrives
        {
            get
            {
                if (!s_decided) Decide();
                return s_coreDrives;
            }
        }

        public static bool ClipsAvailable => s_crawlIdle != null && s_crawlForward != null;
        internal static AnimationClip CrawlIdle => s_crawlIdle;
        internal static AnimationClip CrawlForward => s_crawlForward;
        internal static float CrawlIdleLength => s_crawlIdle.length;
        internal static float CrawlForwardLength => s_crawlForward.length;

        private static void Decide()
        {
            s_decided = true;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Crawl.VikHavnGuid))
            {
                FiresCore.Logging.FiresLogger.LogDebug("[CrawlPose] skipped (VikHavn drives the crawl; Core's bundle copy not loaded)");
                return;
            }
            s_coreDrives = true;
            try
            {
                Assembly assembly = typeof(CrawlPose).Assembly;
                string resourceName = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(BundleResourceSuffix, StringComparison.Ordinal));
                if (resourceName == null) { Debug.LogWarning("[CrawlPose] crawl bundle not embedded; crawling keeps the crouch pose"); return; }
                AssetBundle bundle;
                using (Stream resource = assembly.GetManifestResourceStream(resourceName))
                using (var buffer = new MemoryStream())
                {
                    resource.CopyTo(buffer);
                    bundle = AssetBundle.LoadFromMemory(buffer.ToArray());
                }
                if (bundle == null) { Debug.LogWarning("[CrawlPose] crawl bundle failed to load (already loaded by another mod?)"); return; }
                s_crawlIdle = bundle.LoadAsset<AnimationClip>(CrawlIdleClipName);
                s_crawlForward = bundle.LoadAsset<AnimationClip>(CrawlForwardClipName);
                FiresCore.Logging.FiresLogger.LogDebug($"[CrawlPose] clips loaded: idle {(s_crawlIdle != null ? "ok" : "missing")}, forward {(s_crawlForward != null ? "ok" : "missing")}");
            }
            catch (Exception ex) { Debug.LogWarning($"[CrawlPose] crawl bundle: {ex.Message}"); }
        }

        internal static AnimationClip ReplacementFor(AnimationClip originalClip)
        {
            string clipName = originalClip.name;
            if (clipName.IndexOf(SneakClipNameFragment, StringComparison.OrdinalIgnoreCase) >= 0) return s_crawlForward;
            if (clipName.IndexOf(CrouchClipNameFragment, StringComparison.OrdinalIgnoreCase) >= 0) return s_crawlIdle;
            return null;
        }

        internal static ZDO ZdoOf(Character character)
        {
            ZNetView view = character != null ? NetView(character) : null;
            return view != null ? view.GetZDO() : null;
        }

        public static bool IsProne(Player player)
        {
            ZDO zdo = ZdoOf(player);
            return zdo != null && (zdo.GetBool(ProneKey) || zdo.GetBool(VikHavnProneKey));   // 0.2.1190: VikHavn's prone too
        }

        internal static float SpeedMultiplier(Player player)
        {
            ZDO zdo = ZdoOf(player);
            float value = zdo != null ? zdo.GetFloat(ProneSpeedMultiplierKey, 0f) : 0f;
            return value > 0f ? value : ProneSpeedMultiplier;
        }

        internal static float HeightFraction(Player player) => player.IsCrouching() && IsProne(player) ? ProneHeight : 1f;

        /// <summary>The owner's prone switch: the ZDO flag, the crawl speed its Sneak skill gives, and the speed/stealth effect.</summary>
        public static bool SetProne(Player player, bool prone, string why)
        {
            ZDO zdo = ZdoOf(player);
            if (zdo == null || !CoreDrives) return false;
            float speed = Mathf.Lerp(ProneSpeedMultiplier, ProneSpeedMultiplierAtMaxSkill, player.GetSkillFactor(Skills.SkillType.Sneak));
            bool was = zdo.GetBool(ProneKey);
            if (was != prone) zdo.Set(ProneKey, prone);
            if (prone && zdo.GetFloat(ProneSpeedMultiplierKey) != speed) zdo.Set(ProneSpeedMultiplierKey, speed);
            if (prone)
            {
                SE_Stats effect = Effect();
                effect.m_stealthModifier = -ProneVisibilityReduction;
                effect.m_speedModifier = speed - 1f;
                player.GetSEMan().AddStatusEffect(effect, true);
            }
            else player.GetSEMan().RemoveStatusEffect(EffectNameHash, true);
            if (was != prone) FiresCore.Logging.FiresLogger.LogDebug($"[CrawlPose] prone {(prone ? "ON" : "OFF")} ({why}; speed x{speed:0.00})");
            return true;
        }

        /// <summary>0.2.1190 (Fire 11-06: Core holds the crawl API, APM decides; lead: one event whoever draws the crawl): raised on every peer
        /// when a player's prone state flips (player, now prone) - Core's own flag or VikHavn's (VikHavnCombat_Prone, read only, VikHavn
        /// untouched). Raised by <see cref="ProneWatch"/> from the synced ZDO. Add-only.</summary>
        public static event System.Action<Player, bool> ProneChanged;

        private static readonly int VikHavnProneKey = "VikHavnCombat_Prone".GetStableHashCode();
        private static bool s_proneSaid;

        internal static void RaiseProneChanged(Player player, bool prone)
        {
            if (!s_proneSaid) { s_proneSaid = true; FiresCore.Logging.FiresLogger.LogInfo($"[CrawlPose] prone state events on (drawn by {(CoreDrives ? "Core" : "VikHavn")})"); }
            FiresCore.Logging.FiresLogger.LogDebug($"[CrawlPose] ProneChanged {(player != null ? player.GetPlayerName() : "?")} -> {(prone ? "prone" : "up")}");
            var h = ProneChanged;
            if (h != null) try { h(player, prone); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[CrawlPose] ProneChanged listener failed: {e.Message}"); }
        }

        private static SE_Stats Effect()
        {
            if (s_effect != null) return s_effect;
            s_effect = ScriptableObject.CreateInstance<SE_Stats>();
            s_effect.name = EffectAssetName;
            s_effect.m_name = "Prone";
            s_effect.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s_effect;
        }
    }
}
