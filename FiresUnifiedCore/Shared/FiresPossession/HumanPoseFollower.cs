using System;
using UnityEngine;

// Shared\FiresPossession: one source, compiled into Core (FIRES_POSSESSION_PUBLIC defined: public, the API other mods call) and
// into Valcast (internal: Valcast stays standalone). Frozen API - additive changes only; Core, Valcast and
// FiresMounts depend on it.
//
// Also used for VCS player model swaps: copies one humanoid rig's pose onto
// another each Step, the pose copy PossessionProxy's Hidden (Wildshape) style always did, now in one place. Both animators need a
// humanoid avatar. SizeRatio is the target's human scale over the source's (hips height), for callers that scale the target model.
namespace FiresCore.Animation
{
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    sealed class HumanPoseFollower : IDisposable
    {
        private HumanPoseHandler _from, _to;
        private HumanPose _pose;
        private readonly Animator _target;

        public Animator Source { get; }
        public Animator Target => _target;

        /// <summary>The target avatar's human scale over the source's (hips height ratio); 1 when either is unknown.</summary>
        public float SizeRatio { get; }

        public bool IsValid => _from != null && _to != null;

        /// <summary>True when both animators carry a humanoid avatar; otherwise `why` says which one doesn't.</summary>
        public static bool CanFollow(Animator src, Animator dst, out string why)
        {
            if (src == null || dst == null) { why = "no animator"; return false; }
            if (src.avatar == null || !src.avatar.isHuman) { why = "the source rig has no humanoid avatar"; return false; }
            if (dst.avatar == null || !dst.avatar.isHuman) { why = "the target rig has no humanoid avatar"; return false; }
            why = null;
            return true;
        }

        public HumanPoseFollower(Animator src, Animator dst)
        {
            Source = src;
            _target = dst;
            SizeRatio = 1f;
            if (!CanFollow(src, dst, out _)) return;
            _from = new HumanPoseHandler(src.avatar, src.transform);
            _to = new HumanPoseHandler(dst.avatar, dst.transform);
            _pose = new HumanPose();
            float s = src.humanScale, d = dst.humanScale;
            if (s > 0.0001f && d > 0.0001f) SizeRatio = d / s;
        }

        /// <summary>Copies the source's current human pose onto the target. Call once per frame (LateUpdate, after the source animated).</summary>
        public void Step()
        {
            if (_from == null || _to == null || Source == null || _target == null) return;
            _from.GetHumanPose(ref _pose);
            _to.SetHumanPose(ref _pose);
        }

        /// <summary>The target rig's bone for a humanoid bone (null when the rig has none), e.g. for remapping attach points.</summary>
        public Transform BoneFor(HumanBodyBones bone) =>
            _target != null && _target.avatar != null && _target.avatar.isHuman && bone != HumanBodyBones.LastBone ? _target.GetBoneTransform(bone) : null;

        public void Dispose()
        {
            _from?.Dispose();
            _to?.Dispose();
            _from = _to = null;
        }
    }
}
