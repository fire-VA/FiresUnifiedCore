using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement.CrawlPose
{
    /// <summary>
    /// VikHavn's crawl patches (PlayerComponentsAttachPatch, CrawlSpeedParameterPatch, CrawlPlaybackRatePatch, CrawlLocomotion), live only
    /// while Core drives the crawl. VikHavn's camera lowering is not ported: Core's crawl camera (CrawlCamera) already takes the view to the
    /// head while crawling.
    /// Vanilla's crouch state is a 2D blend tree over (sideway_speed, forward_speed) with the idle clips at rest and the sneak-walk clips at
    /// forward speed 2, played at time scale 0.7. A crawling player's speed parameters are scaled so full prone speed lands on the walk child,
    /// and the animator plays at the rate that keeps planted hands and knees moving with the ground.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Crawling)]
    internal static class CrawlPosePatches
    {
        private const float SneakWalkBlendSpeed = 2f;
        private const float SneakWalkTimeScale = 0.7f;
        private const float StandardPlaybackRate = 1f, MinPlaybackRate = 0.25f, MaxPlaybackRate = 4f;

        private static readonly int ForwardSpeedHash = ZSyncAnimation.GetHash("forward_speed");
        private static readonly int SidewaySpeedHash = ZSyncAnimation.GetHash("sideway_speed");

        private static float SpeedParameterScale(Player player) =>
            SneakWalkBlendSpeed / (player.m_crouchSpeed * CrawlPose.SpeedMultiplier(player));

        private static float PlaybackRate(Player player, Animator animator)
        {
            float blendSpeed = new Vector2(animator.GetFloat(SidewaySpeedHash), animator.GetFloat(ForwardSpeedHash)).magnitude;
            float walkWeight = Mathf.Clamp01(blendSpeed / SneakWalkBlendSpeed);
            if (walkWeight <= 0f) return StandardPlaybackRate;

            float groundSpeed = blendSpeed / SpeedParameterScale(player);
            float walkCycleSeconds = CrawlPose.CrawlForwardLength / SneakWalkTimeScale;
            float blendedCycleSeconds = Mathf.Lerp(CrawlPose.CrawlIdleLength, walkCycleSeconds, walkWeight);
            float groundMatchedRate = groundSpeed * blendedCycleSeconds / (walkWeight * CrawlPose.StrideLength);
            return Mathf.Clamp(Mathf.Lerp(StandardPlaybackRate, groundMatchedRate, walkWeight), MinPlaybackRate, MaxPlaybackRate);
        }

        // Every peer adds the stance to its copy of each player (decides VikHavn-or-Core and loads the clips at the first player).
        [HarmonyPatch(typeof(Player), "Awake")]
        private static class Player_Awake_CrawlPose
        {
            private static void Postfix(Player __instance)
            {
                if (!CrawlPose.CoreDrives || __instance.GetComponent<CrawlPoseStance>() != null) return;
                __instance.gameObject.AddComponent<CrawlPoseStance>();
            }
        }

        // Only the owner writes speed parameters; other peers read the scaled values from the player's ZDO.
        [HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SetFloat), typeof(int), typeof(float))]
        private static class ZSyncAnimation_SetFloat_CrawlPose
        {
            private static void Prefix(ZSyncAnimation __instance, int hash, ref float value)
            {
                if (hash != ForwardSpeedHash && hash != SidewaySpeedHash) return;
                if (!CrawlPoseStance.TryGetCrawling(__instance, out CrawlPoseStance stance)) return;
                value *= SpeedParameterScale(stance.Player);
            }
        }

        // Vanilla resets the animator to normal speed every fixed step outside attacks, emotes and freeze frames; a crawling player's
        // playback rate is applied right after that reset, on every peer.
        [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.CustomFixedUpdate))]
        private static class CharacterAnimEvent_CustomFixedUpdate_CrawlPose
        {
            private static readonly AccessTools.FieldRef<CharacterAnimEvent, Character> EventCharacter =
                AccessTools.FieldRefAccess<CharacterAnimEvent, Character>("m_character");
            private static readonly AccessTools.FieldRef<CharacterAnimEvent, Animator> EventAnimator =
                AccessTools.FieldRefAccess<CharacterAnimEvent, Animator>("m_animator");
            private static readonly AccessTools.FieldRef<CharacterAnimEvent, float> FreezeFrameTimer =
                AccessTools.FieldRefAccess<CharacterAnimEvent, float>("m_pauseTimer");

            private static void Postfix(CharacterAnimEvent __instance)
            {
                Character character = EventCharacter(__instance);
                if (character == null || !(character is Player)) return;
                if (!CrawlPoseStance.TryGetCrawling(CrawlPose.SyncAnimationOf(character), out CrawlPoseStance stance)) return;
                if (FreezeFrameTimer(__instance) > 0f || character.InAttack() || character.InMinorAction() || character.InEmote() || !character.CanMove()) return;
                Animator animator = EventAnimator(__instance);
                if (animator != null) animator.speed = PlaybackRate(stance.Player, animator);
            }
        }
    }
}
