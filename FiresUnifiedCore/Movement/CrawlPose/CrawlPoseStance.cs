using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement.CrawlPose
{
    /// <summary>
    /// Added to every Player on every peer while Core drives the crawl (VikHavn's PlayerStance + PlayerAnimations + LocalProneController,
    /// crawl part only). Scales the capsule for prone on every peer so hits line up with the lowered body, swaps the crouch-tree clips for the
    /// crawl clips on clients, and on the local player turns a held Crouch into prone.
    /// </summary>
    internal sealed class CrawlPoseStance : MonoBehaviour
    {
        private static readonly Dictionary<ZSyncAnimation, CrawlPoseStance> s_crawling = new Dictionary<ZSyncAnimation, CrawlPoseStance>();

        private static readonly Func<PlayerController, bool, bool> ControllerTakesInput =
            AccessTools.MethodDelegate<Func<PlayerController, bool, bool>>(AccessTools.Method(typeof(PlayerController), "TakeInput"));
        private static readonly Func<PlayerController, bool> ControllerInInventoryEtc =
            AccessTools.MethodDelegate<Func<PlayerController, bool>>(AccessTools.Method(typeof(PlayerController), "InInventoryEtc"));

        private ZSyncAnimation _syncAnimation;
        private CapsuleCollider _capsule;
        private float _standingHeight;
        private Vector3 _standingCenter;
        private CrawlPoseOverrides _overrides;
        private float _crouchHeldSince = float.PositiveInfinity;

        public Player Player { get; private set; }

        public static bool TryGetCrawling(ZSyncAnimation syncAnimation, out CrawlPoseStance stance)
        {
            stance = null;
            return syncAnimation != null && s_crawling.TryGetValue(syncAnimation, out stance) && stance.Player.IsCrouching();
        }

        private void Awake()
        {
            Player = GetComponent<Player>();
            _syncAnimation = CrawlPose.SyncAnimationOf(Player);
            _capsule = Player.GetCollider();
            if (_capsule != null)
            {
                _standingHeight = _capsule.height;
                _standingCenter = _capsule.center;
            }
            Animator animator = CrawlPose.AnimatorOf(Player);
            if (CrawlPose.ClipsAvailable && animator != null) _overrides = new CrawlPoseOverrides(animator);
        }

        private void Update()
        {
            if (Player != Player.m_localPlayer) return;
            bool proneAllowed = CrawlPose.HoldSeconds > 0f && Crawl.Enabled && !Player.IsDead();
            if (CrawlPose.IsProne(Player))
            {
                // Core's low-ceiling crawl owns its own prone (Headroom ends it when the ceiling lifts).
                if (!Crawl.Active && (!proneAllowed || !CrawlPose.CrouchToggled(Player))) CrawlPose.SetProne(Player, false, "crouch released");
                _crouchHeldSince = float.PositiveInfinity;
                return;
            }
            if (!proneAllowed || !CrouchButtonHeld())
            {
                _crouchHeldSince = float.PositiveInfinity;
                return;
            }
            if (float.IsPositiveInfinity(_crouchHeldSince)) _crouchHeldSince = Time.time;
            if (Time.time - _crouchHeldSince < CrawlPose.HoldSeconds) return;
            CrawlPose.CrouchToggled(Player) = true;
            CrawlPose.SetProne(Player, true, "Crouch held");
        }

        private bool CrouchButtonHeld()
        {
            if (!ZInput.GetButton("Crouch") && !ZInput.GetButton("JoyCrouch")) return false;
            if (FiresCore.Input.FiresInputBlock.IsTyping() || Hud.InRadial()) return false;
            PlayerController controller = Player.GetComponent<PlayerController>();
            return controller != null && ControllerTakesInput(controller, false) && !ControllerInInventoryEtc(controller);
        }

        private void FixedUpdate()
        {
            // A body with no capsule has nothing to scale (FiresValcast's replay ghosts drop theirs after Awake).
            if (_capsule == null) return;
            float heightFraction = CrawlPose.HeightFraction(Player);
            float targetHeight = _standingHeight * heightFraction;
            if (Mathf.Approximately(_capsule.height, targetHeight)) return;
            _capsule.height = targetHeight;
            _capsule.center = new Vector3(_standingCenter.x, _standingCenter.y * heightFraction, _standingCenter.z);
        }

        private void LateUpdate()
        {
            if (_overrides == null) return;
            bool crawling = CrawlPose.IsProne(Player);
            _overrides.SetCrawling(crawling);
            if (crawling) s_crawling[_syncAnimation] = this;
            else s_crawling.Remove(_syncAnimation);
        }

        private void OnDestroy()
        {
            if (_syncAnimation != null) s_crawling.Remove(_syncAnimation);
        }
    }

    /// <summary>
    /// VikHavn's PlayerAnimationOverrides, crawl slots only. The player keeps its controller until a crawl first needs the clips; then an
    /// override controller wraps the current one. Only the slots this class fills are ever touched: another mod's clip in a slot is remembered
    /// and put back when the crawl ends (FiresValcast's old-walk clips live in the same override controller), and a slot is cleared only while
    /// it still holds our own clip. If another mod replaces the controller later, the override is rebuilt around it.
    /// </summary>
    internal sealed class CrawlPoseOverrides
    {
        private readonly Animator _animator;
        private readonly List<KeyValuePair<AnimationClip, AnimationClip>> _clipOverrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        private readonly Dictionary<AnimationClip, AnimationClip> _ownClip = new Dictionary<AnimationClip, AnimationClip>();
        private readonly Dictionary<AnimationClip, AnimationClip> _displaced = new Dictionary<AnimationClip, AnimationClip>();
        private AnimatorOverrideController _overrideController;
        private bool _crawling;

        public CrawlPoseOverrides(Animator animator)
        {
            _animator = animator;
        }

        public void SetCrawling(bool crawling)
        {
            if (crawling == _crawling && (OverrideInstalled() || !crawling)) return;
            _crawling = crawling;
            ApplyOverrides();
        }

        private bool OverrideInstalled() => _overrideController != null && _animator.runtimeAnimatorController == _overrideController;

        private void ApplyOverrides()
        {
            bool installed = OverrideInstalled();
            if (!installed && !_crawling) return;

            Snapshot snapshot = Snapshot.Capture(_animator);
            if (!installed)
            {
                _overrideController = new AnimatorOverrideController(_animator.runtimeAnimatorController);
                _animator.runtimeAnimatorController = _overrideController;
                _ownClip.Clear();
                _displaced.Clear();
            }

            _clipOverrides.Clear();
            _overrideController.GetOverrides(_clipOverrides);
            bool changed = false;
            for (int index = 0; index < _clipOverrides.Count; index++)
            {
                AnimationClip originalClip = _clipOverrides[index].Key;
                AnimationClip current = _clipOverrides[index].Value;
                AnimationClip next = SlotValue(originalClip, current);
                if (next == current) continue;
                _clipOverrides[index] = new KeyValuePair<AnimationClip, AnimationClip>(originalClip, next);
                changed = true;
            }

            if (changed) _overrideController.ApplyOverrides(_clipOverrides);
            snapshot.Restore(_animator);
        }

        private AnimationClip SlotValue(AnimationClip originalClip, AnimationClip current)
        {
            AnimationClip wanted = _crawling ? CrawlPose.ReplacementFor(originalClip) : null;
            _ownClip.TryGetValue(originalClip, out AnimationClip ours);
            if (wanted != null)
            {
                if (current != null && current != ours && current != wanted) _displaced[originalClip] = current;
                _ownClip[originalClip] = wanted;
                return wanted;
            }

            if (ours == null) return current;
            _ownClip.Remove(originalClip);
            if (current != ours) { _displaced.Remove(originalClip); return current; }
            _displaced.TryGetValue(originalClip, out AnimationClip previous);
            _displaced.Remove(originalClip);
            return previous;
        }

        // Changing the override clips can reset the animator's parameters and states; capture and restore keeps the pose through the swap.
        private sealed class Snapshot
        {
            private readonly List<(int Hash, AnimatorControllerParameterType Type, float F, int I, bool B)> _parameters =
                new List<(int, AnimatorControllerParameterType, float, int, bool)>();
            private readonly List<(int StateHash, float NormalizedTime)> _layerStates = new List<(int, float)>();

            public static Snapshot Capture(Animator animator)
            {
                var snapshot = new Snapshot();
                foreach (AnimatorControllerParameter parameter in animator.parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                    snapshot._parameters.Add((parameter.nameHash, parameter.type,
                        animator.GetFloat(parameter.nameHash), animator.GetInteger(parameter.nameHash), animator.GetBool(parameter.nameHash)));
                }
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                    snapshot._layerStates.Add((state.fullPathHash, state.normalizedTime));
                }
                return snapshot;
            }

            public void Restore(Animator animator)
            {
                foreach (var p in _parameters)
                {
                    switch (p.Type)
                    {
                        case AnimatorControllerParameterType.Float: animator.SetFloat(p.Hash, p.F); break;
                        case AnimatorControllerParameterType.Int: animator.SetInteger(p.Hash, p.I); break;
                        case AnimatorControllerParameterType.Bool: animator.SetBool(p.Hash, p.B); break;
                    }
                }
                for (int layer = 0; layer < _layerStates.Count && layer < animator.layerCount; layer++)
                    animator.Play(_layerStates[layer].StateHash, layer, _layerStates[layer].NormalizedTime);
            }
        }
    }
}
