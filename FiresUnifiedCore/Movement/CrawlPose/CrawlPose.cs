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

        private static ConfigEntry<float> s_holdSeconds, s_strideLength;
        private static bool s_decided, s_coreDrives;
        private static AnimationClip s_crawlIdle, s_crawlForward;
        private static SE_Stats s_effect;

        internal static void Bind(ConfigFile config)
        {
            s_holdSeconds = config.Bind("Crawling", "Prone hold seconds", 0.6f, new ConfigDescription(
                "Seconds the Crouch button must be held (while crouched) to go prone and crawl; tapping Crouch stands back up. 0 = no prone by key " +
                "(only under low ceilings). Unused with VikHavn Combat Moveset Additions installed (its own setting applies). Client-local.",
                new AcceptableValueRange<float>(0f, 3f)));
            s_strideLength = config.Bind("Crawling", "Crawl stride length", 0.96f, new ConfigDescription(
                "Meters of ground one full crawl cycle covers, used to pace the crawl animation. Lower it if hands and knees slide forward, " +
                "raise it if they slide backward. Client-local.", new AcceptableValueRange<float>(0.2f, 2f)));
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Crawling, false, s_holdSeconds, s_strideLength);
        }

        internal static float HoldSeconds => s_holdSeconds != null ? s_holdSeconds.Value : 0.6f;
        internal static float StrideLength => s_strideLength != null ? s_strideLength.Value : 0.96f;

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
            return zdo != null && zdo.GetBool(ProneKey);
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
