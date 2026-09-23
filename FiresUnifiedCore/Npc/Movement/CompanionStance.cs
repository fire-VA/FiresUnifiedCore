using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// A companion's stance, the same on every client. Crouch is the replicated "crouching" animator bool. Prone exists
    /// only with VikHavn Combat Moveset Additions installed and is a bool on the companion's ZDO, so every client lowers
    /// the capsule and swaps VikHavn's crawl clips into the crouch tree the way VikHavn poses a prone player. VikHavn only
    /// poses Players, and Core never references it: its ZDO keys and clip names are the contract.
    /// </summary>
    public sealed class CompanionStance : MonoBehaviour
    {
        private static readonly int PlayerProneKey = "VikHavnCombat_Prone".GetStableHashCode();
        private static readonly int PlayerProneSpeedKey = "VikHavnCombat_ProneSpeedMultiplier".GetStableHashCode();
        private static readonly int ProneKey = "FiresCompanion_Prone".GetStableHashCode();
        private static readonly int ProneSpeedKey = "FiresCompanion_ProneSpeed".GetStableHashCode();

        // VikHavn Sneak Stance settings, read live so companions follow the server's values; VikHavn's defaults as fallbacks.
        private static float DefaultProneSpeedMultiplier => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Prone Speed Multiplier", 0.3f);
        private static float ProneVisibilityReduction => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Prone Visibility Reduction", 0.5f);
        private static float SneakHeightFraction => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Sneak Height", 0.75f);
        private static float ProneHeightFraction => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Prone Height", 0.5f);
        private static float CrawlStrideLength => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Crawl Stride Length", 0.96f);
        private static bool StanceEnabled => Combat.VikHavnBridge.Setting(Combat.VikHavnBridge.StanceSection, "Enabled", true);

        private const string CrawlIdleClipName = "Crawl_Idle_Loop";
        private const string CrawlForwardClipName = "Crawl_Fwd_Loop";
        private const string SneakClipFragment = "Sneak";
        private const string CrouchClipFragment = "Crouch";

        // Vanilla's crouch state is a blend tree with the sneak-walk clips at forward speed 2, played at time scale 0.7.
        private const float SneakWalkBlendSpeed = 2f;
        private const float SneakWalkTimeScale = 0.7f;
        private const float StandardPlaybackRate = 1f;
        private const float MinPlaybackRate = 0.25f;
        private const float MaxPlaybackRate = 4f;

        // Vanilla Player.UpdateStealth: unskilled 0.5 + light/2, fully skilled 0.2 + light × 0.4.
        private const float UnskilledStealthBase = 0.5f;
        private const float UnskilledStealthLight = 0.5f;
        private const float SkilledStealthBase = 0.2f;
        private const float SkilledStealthLight = 0.4f;
        private const float DefaultLightFactor = 0.5f;

        internal static readonly int CrouchingHash = ZSyncAnimation.GetHash("crouching");
        internal static readonly int ForwardSpeedHash = ZSyncAnimation.GetHash("forward_speed");
        internal static readonly int SidewaySpeedHash = ZSyncAnimation.GetHash("sideway_speed");

        private static readonly Dictionary<Character, CompanionStance> ByCharacter = new Dictionary<Character, CompanionStance>();
        private static readonly Dictionary<ZSyncAnimation, CompanionStance> Crawling = new Dictionary<ZSyncAnimation, CompanionStance>();
        private static bool _crawlClipsSearched;
        private static AnimationClip _crawlIdle;
        private static AnimationClip _crawlForward;

        private ZNetView _nview;
        private ZSyncAnimation _zanim;
        private Character _character;
        private CompanionSkills _skills;
        private CapsuleCollider _capsule;
        private float _standingHeight;
        private Vector3 _standingCenter;
        private Animator _crawlAnimator;
        private AnimatorOverrideController _crawlOverride;
        private readonly List<KeyValuePair<AnimationClip, AnimationClip>> _clipOverrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();

        public static bool VikHavnInstalled => Combat.VikHavnBridge.Installed;

        /// <summary>A player prone under VikHavn, read from the player's own ZDO.</summary>
        public static bool IsPlayerProne(Player player)
        {
            var zdo = ZdoOf(player);
            return VikHavnInstalled && zdo != null && zdo.GetBool(PlayerProneKey);
        }

        /// <summary>The crawl speed a prone player's Sneak skill gives, as a fraction of crouch speed.</summary>
        public static float PlayerProneSpeedMultiplier(Player player)
        {
            var zdo = ZdoOf(player);
            float multiplier = zdo != null ? zdo.GetFloat(PlayerProneSpeedKey) : 0f;
            return multiplier > 0f ? multiplier : DefaultProneSpeedMultiplier;
        }

        public static bool TryGet(Character character, out CompanionStance stance)
        {
            stance = null;
            return character != null && ByCharacter.TryGetValue(character, out stance) && stance != null;
        }

        /// <summary>The walk speed while crouched or prone. Character.IsCrouching is false for non-players, so vanilla
        /// UpdateWalking never picks m_crouchSpeed for a companion.</summary>
        public static bool TryGetSneakSpeed(Character character, out float speed)
        {
            speed = 0f;
            if (!TryGet(character, out var stance) || !stance.IsCrouching) return false;
            speed = character.m_crouchSpeed * (stance.IsProne ? stance.ProneSpeed : 1f);
            return true;
        }

        internal static bool TryGetCrawling(ZSyncAnimation zanim, out CompanionStance stance)
        {
            stance = null;
            return zanim != null && Crawling.TryGetValue(zanim, out stance) && stance != null;
        }

        public bool IsCrouching
        {
            get
            {
                var animator = DrivenAnimator;
                return animator != null && animator.GetBool(CrouchingHash);
            }
        }

        public bool IsProne => _nview != null && _nview.IsValid() && _nview.GetZDO().GetBool(ProneKey);

        private float ProneSpeed
        {
            get
            {
                float multiplier = _nview != null && _nview.IsValid() ? _nview.GetZDO().GetFloat(ProneSpeedKey) : 0f;
                return multiplier > 0f ? multiplier : DefaultProneSpeedMultiplier;
            }
        }

        private Animator DrivenAnimator => _zanim != null ? _zanim.m_animator : null;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _zanim = GetComponent<ZSyncAnimation>();
            _character = GetComponent<Character>();
            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule != null)
            {
                _standingHeight = _capsule.height;
                _standingCenter = _capsule.center;
            }
            if (_character != null) ByCharacter[_character] = this;
        }

        private void OnDestroy()
        {
            if (_character != null) ByCharacter.Remove(_character);
            if (_zanim != null) Crawling.Remove(_zanim);
        }

        /// <summary>Owner: crouch or stand up. Getting up from a crouch also ends prone.</summary>
        public void SetCrouch(bool crouch)
        {
            if (_zanim != null) _zanim.SetBool(CrouchingHash, crouch);
            if (!crouch) SetProne(false, 0f);
        }

        /// <summary>Owner: go prone at the given fraction of crouch speed, or get up to a crouch.</summary>
        public void SetProne(bool prone, float speedMultiplier)
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            if (prone && _zanim != null) _zanim.SetBool(CrouchingHash, true);
            var zdo = _nview.GetZDO();
            if (zdo.GetBool(ProneKey) != prone) zdo.Set(ProneKey, prone);
            if (prone && !Mathf.Approximately(zdo.GetFloat(ProneSpeedKey), speedMultiplier)) zdo.Set(ProneSpeedKey, speedMultiplier);
        }

        /// <summary>
        /// Vanilla Player.UpdateStealth for a crouched companion: its Sneak skill, the light, its status effects
        /// (SEMan.ModifyStealth) and, while prone, VikHavn's prone modifier.
        /// </summary>
        public float StealthFactor()
        {
            float light = StealthSystem.instance != null ? StealthSystem.instance.GetLightFactor(_character.GetCenterPoint()) : DefaultLightFactor;
            if (_skills == null) _skills = GetComponent<CompanionSkills>();
            float skill = _skills != null ? _skills.GetSkillFactor(Skills.SkillType.Sneak) : 0f;
            float baseStealth = Mathf.Clamp01(Mathf.Lerp(UnskilledStealthBase + light * UnskilledStealthLight,
                SkilledStealthBase + light * SkilledStealthLight, skill));
            float stealth = baseStealth;
            _character.GetSEMan()?.ModifyStealth(baseStealth, ref stealth);
            if (IsProne) stealth -= baseStealth * ProneVisibilityReduction;
            return Mathf.Clamp01(stealth);
        }

        // Every client sizes the capsule like VikHavn sizes a player's, so hits resolve against the lowered body anywhere.
        private void FixedUpdate()
        {
            if (_capsule == null) return;
            float fraction = !VikHavnInstalled || !IsCrouching || !StanceEnabled ? 1f : IsProne ? ProneHeightFraction : SneakHeightFraction;
            float height = _standingHeight * fraction;
            if (Mathf.Approximately(_capsule.height, height)) return;
            _capsule.height = height;
            _capsule.center = new Vector3(_standingCenter.x, _standingCenter.y * fraction, _standingCenter.z);
        }

        private void LateUpdate()
        {
            if (!VikHavnInstalled || _zanim == null) return;
            bool crawl = IsProne && IsCrouching && CrawlClipsAvailable();
            if (crawl) Crawling[_zanim] = this;
            else Crawling.Remove(_zanim);
            SetCrawlClips(crawl);
        }

        private static bool CrawlClipsAvailable()
        {
            if (_crawlClipsSearched) return _crawlIdle != null && _crawlForward != null;
            _crawlClipsSearched = true;
            foreach (var clip in Resources.FindObjectsOfTypeAll<AnimationClip>())
            {
                if (clip.name == CrawlIdleClipName) _crawlIdle = clip;
                else if (clip.name == CrawlForwardClipName) _crawlForward = clip;
            }
            return _crawlIdle != null && _crawlForward != null;
        }

        /// <summary>Wraps the driven animator's controller while crawling and puts it back after, keeping the pose.</summary>
        private void SetCrawlClips(bool crawl)
        {
            var animator = DrivenAnimator;
            if (animator == null) return;
            bool installed = _crawlOverride != null && _crawlAnimator == animator && animator.runtimeAnimatorController == _crawlOverride;
            if (crawl == installed) return;

            var snapshot = AnimatorSnapshot.Capture(animator);
            if (crawl)
            {
                _crawlAnimator = animator;
                _crawlOverride = new AnimatorOverrideController(animator.runtimeAnimatorController);
                _clipOverrides.Clear();
                _crawlOverride.GetOverrides(_clipOverrides);
                for (int i = 0; i < _clipOverrides.Count; i++)
                    _clipOverrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(_clipOverrides[i].Key, CrawlReplacementFor(_clipOverrides[i].Key));
                _crawlOverride.ApplyOverrides(_clipOverrides);
                animator.runtimeAnimatorController = _crawlOverride;
            }
            else
            {
                animator.runtimeAnimatorController = _crawlOverride.runtimeAnimatorController;
                _crawlOverride = null;
                _crawlAnimator = null;
            }
            snapshot.Restore(animator);
        }

        private static AnimationClip CrawlReplacementFor(AnimationClip clip)
        {
            if (clip.name.IndexOf(SneakClipFragment, System.StringComparison.OrdinalIgnoreCase) >= 0) return _crawlForward;
            if (clip.name.IndexOf(CrouchClipFragment, System.StringComparison.OrdinalIgnoreCase) >= 0) return _crawlIdle;
            return null;
        }

        /// <summary>Scales the speed parameters so full prone speed lands on the crouch tree's walk child.</summary>
        internal float SpeedParameterScale => SneakWalkBlendSpeed / (_character.m_crouchSpeed * ProneSpeed);

        /// <summary>The playback rate that keeps planted hands and knees moving with the ground (VikHavn's CrawlLocomotion).</summary>
        internal float PlaybackRate(Animator animator)
        {
            float blendSpeed = new Vector2(animator.GetFloat(SidewaySpeedHash), animator.GetFloat(ForwardSpeedHash)).magnitude;
            float walkWeight = Mathf.Clamp01(blendSpeed / SneakWalkBlendSpeed);
            if (walkWeight <= 0f) return StandardPlaybackRate;

            float groundSpeed = blendSpeed / SpeedParameterScale;
            float walkCycleSeconds = _crawlForward.length / SneakWalkTimeScale;
            float blendedCycleSeconds = Mathf.Lerp(_crawlIdle.length, walkCycleSeconds, walkWeight);
            float groundMatchedRate = groundSpeed * blendedCycleSeconds / (walkWeight * CrawlStrideLength);
            return Mathf.Clamp(Mathf.Lerp(StandardPlaybackRate, groundMatchedRate, walkWeight), MinPlaybackRate, MaxPlaybackRate);
        }

        private static ZDO ZdoOf(Character character)
        {
            var nview = character != null ? character.m_nview : null;
            return nview != null && nview.IsValid() ? nview.GetZDO() : null;
        }

        /// <summary>Swapping a controller or its clips resets parameters and states; this carries them across.</summary>
        private sealed class AnimatorSnapshot
        {
            private readonly List<(int Hash, AnimatorControllerParameterType Type, float Float, int Int, bool Bool)> _parameters =
                new List<(int, AnimatorControllerParameterType, float, int, bool)>();
            private readonly List<(int State, float Time)> _layers = new List<(int, float)>();

            public static AnimatorSnapshot Capture(Animator animator)
            {
                var snapshot = new AnimatorSnapshot();
                foreach (var parameter in animator.parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                    snapshot._parameters.Add((parameter.nameHash, parameter.type, animator.GetFloat(parameter.nameHash),
                        animator.GetInteger(parameter.nameHash), animator.GetBool(parameter.nameHash)));
                }
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    var state = animator.GetCurrentAnimatorStateInfo(layer);
                    snapshot._layers.Add((state.fullPathHash, state.normalizedTime));
                }
                return snapshot;
            }

            public void Restore(Animator animator)
            {
                foreach (var (hash, type, floatValue, intValue, boolValue) in _parameters)
                {
                    switch (type)
                    {
                        case AnimatorControllerParameterType.Float: animator.SetFloat(hash, floatValue); break;
                        case AnimatorControllerParameterType.Int: animator.SetInteger(hash, intValue); break;
                        case AnimatorControllerParameterType.Bool: animator.SetBool(hash, boolValue); break;
                    }
                }
                for (int layer = 0; layer < _layers.Count && layer < animator.layerCount; layer++)
                    animator.Play(_layers[layer].State, layer, _layers[layer].Time);
            }
        }
    }

    /// <summary>Only the owner writes speed parameters; other clients read the scaled values from the companion's ZDO.</summary>
    [HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SetFloat), typeof(int), typeof(float))]
    internal static class CompanionCrawlSpeedParameterPatch
    {
        [HarmonyPrefix]
        private static void ScaleCrawlSpeed(ZSyncAnimation __instance, int hash, ref float value)
        {
            if (hash != CompanionStance.ForwardSpeedHash && hash != CompanionStance.SidewaySpeedHash) return;
            if (CompanionStance.TryGetCrawling(__instance, out var stance)) value *= stance.SpeedParameterScale;
        }
    }

    /// <summary>
    /// Vanilla sets the animator back to normal speed each fixed step outside attacks, emotes and freeze frames; a crawling
    /// companion's playback rate goes on right after, on every client.
    /// </summary>
    [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.CustomFixedUpdate))]
    internal static class CompanionCrawlPlaybackRatePatch
    {
        [HarmonyPostfix]
        private static void MatchCrawlToGround(CharacterAnimEvent __instance)
        {
            var character = __instance.m_character;
            if (character == null || !CompanionStance.TryGetCrawling(character.m_zanim, out var stance)) return;
            if (__instance.m_pauseTimer > 0f || character.InAttack() || character.InMinorAction() || !character.CanMove()) return;
            __instance.m_animator.speed = stance.PlaybackRate(__instance.m_animator);
        }
    }
}
