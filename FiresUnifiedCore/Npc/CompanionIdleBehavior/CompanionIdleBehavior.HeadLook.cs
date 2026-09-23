using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    // Head look-at system
    public partial class CompanionIdleBehavior
    {
        #region Head Look-At

        private const float MinHeadLookWeight = 0.01f;
        private const string HeadBoneName = "Head";
        private const float MaxLookUpDegrees = 25f;
        private const float MaxLookDownDegrees = 35f;

        // The animated pose under the look offset, and the rotation written over it: the animator does not rewrite the head
        // every frame (culled, or a clip without a head key), and blending from our own last write compounded the offset.
        private Quaternion _headBaseLocal;
        private Quaternion _headWrittenLocal;
        private bool _headWritten;

        private void FindHeadBone()
        {
            if (_animator == null) return;

            _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
            if (_headBone == null)
            {
                foreach (var bone in GetComponentsInChildren<Transform>(true))
                {
                    if (bone.name != HeadBoneName) continue;
                    _headBone = bone;
                    break;
                }
            }

            _headBoneFound = _headBone != null;
        }

        private void UpdateHeadLookAt()
        {
            if (!enableHeadLookAt || !_headBoneFound) return;
            if (_isSittingOnChair || _isPlayingEmote) return;

            // In combat, prioritize looking at target
            if (_combatMovement != null && _combatMovement.IsInCombat)
            {
                var target = _companionAI?.GetTargetCreature();
                if (target != null && !target.IsDead())
                {
                    _lookAtTarget = target.transform;
                    _lookAtEndTime = Time.time + 1f;
                    _targetHeadLookWeight = 1f;
                }
            }
            else if (Time.time > _lookAtEndTime || _lookAtTarget == null)
            {
                if (Time.time - _lastLookAtSwitch >= lookAtSwitchCooldown)
                {
                    FindLookAtTarget();
                    _lastLookAtSwitch = Time.time;
                }
            }

            // Check if target moved out of range
            if (_lookAtTarget != null)
            {
                float dist = Vector3.Distance(transform.position, _lookAtTarget.position);
                if (dist > lookAtDetectionRange * 1.2f)
                {
                    _lookAtTarget = null;
                    _targetHeadLookWeight = 0f;
                }
            }

            if (_lookAtTarget != null)
            {
                Vector3 toTarget = (_lookAtTarget.position + Vector3.up * 1.5f - transform.position).normalized;
                float angle = Vector3.Angle(transform.forward, toTarget);
                
                if (angle <= maxLookAtAngle)
                {
                    _targetHeadLookDirection = toTarget;
                    _targetHeadLookWeight = 1f - (angle / maxLookAtAngle) * 0.3f;
                }
                else
                {
                    _targetHeadLookDirection = transform.forward;
                    _targetHeadLookWeight = 0f;
                }
            }
            else
            {
                _targetHeadLookDirection = transform.forward;
                _targetHeadLookWeight = 0f;
            }

            // Smoothly interpolate direction and weight
            float directionSpeed = _targetHeadLookWeight > 0.1f ? headRotationSpeed : headReturnSpeed;
            _currentHeadLookDirection = Vector3.Slerp(
                _currentHeadLookDirection,
                _targetHeadLookDirection,
                Time.deltaTime * directionSpeed
            );
            
            _headLookWeight = Mathf.Lerp(
                _headLookWeight,
                _targetHeadLookWeight,
                Time.deltaTime * directionSpeed
            );
        }

        private void FindLookAtTarget()
        {
            _lookAtTarget = null;
            float bestScore = float.MinValue;

            if (_combatMovement != null && _combatMovement.IsInCombat)
            {
                var target = _companionAI?.GetTargetCreature();
                if (target != null && !target.IsDead())
                {
                    _lookAtTarget = target.transform;
                    _lookAtEndTime = Time.time + lookAtDuration * 2f;
                    return;
                }
            }

            Vector3 myPos = transform.position;
            List<(Transform target, float score)> candidates = new List<(Transform, float)>();

            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                float dist = Vector3.Distance(myPos, player.transform.position);
                if (dist > lookAtDetectionRange) continue;

                float score = 100f - dist;
                if (_companion != null && _companion.IsOwner(player))
                    score += 50f;

                candidates.Add((player.transform, score));
            }

            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsPlayer() || character.IsTamed()) continue;

                float dist = Vector3.Distance(myPos, character.transform.position);
                if (dist > lookAtDetectionRange) continue;

                float score = 80f - dist;
                candidates.Add((character.transform, score));
            }

            foreach (var (target, score) in candidates)
            {
                Vector3 toTarget = (target.position - myPos).normalized;
                float angle = Vector3.Angle(transform.forward, toTarget);

                if (angle > maxLookAtAngle) continue;

                float adjustedScore = score - (angle * 0.5f);

                if (adjustedScore > bestScore)
                {
                    bestScore = adjustedScore;
                    _lookAtTarget = target;
                }
            }

            if (_lookAtTarget != null)
            {
                _lookAtEndTime = Time.time + lookAtDuration + UnityEngine.Random.Range(-1f, 1f);
            }
        }

        private void ApplyHeadLookAt()
        {
            if (!_headBoneFound || _headBone == null) return;

            // The animated pose this frame, or the one under last frame's write when the animator left the bone alone.
            Quaternion current = _headBone.localRotation;
            Quaternion baseLocal = _headWritten && current == _headWrittenLocal ? _headBaseLocal : current;

            Vector3 lookDir = _currentHeadLookDirection;
            lookDir.y *= 0.5f;
            // Single-writer (facing): an active sub-behavior (bow training, gathering, station work) owns the facing.
            bool looking = enableHeadLookAt && !_isSittingOnChair && !_isPlayingEmote && !IsInSubBehavior
                && _headLookWeight >= MinHeadLookWeight && lookDir.sqrMagnitude > 0.000001f;
            if (!looking)
            {
                if (_headWritten) _headBone.localRotation = baseLocal;
                _headWritten = false;
                return;
            }

            // Yaw and pitch relative to the body, clamped, then applied as a world-space turn so the bone's own axes
            // (which are not the body's on the Valheim rig) can't turn a yaw into roll.
            Vector3 localDir = Quaternion.Inverse(transform.rotation) * lookDir.normalized;
            float yaw = Mathf.Clamp(Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg, -maxLookAtAngle, maxLookAtAngle);
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(localDir.y, -1f, 1f)) * Mathf.Rad2Deg, -MaxLookUpDegrees, MaxLookDownDegrees);
            Vector3 clampedDir = transform.rotation * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);

            Transform parent = _headBone.parent;
            Quaternion baseWorld = parent != null ? parent.rotation * baseLocal : baseLocal;
            Quaternion turned = Quaternion.FromToRotation(transform.forward, clampedDir) * baseWorld;
            _headBone.rotation = Quaternion.Slerp(baseWorld, turned, _headLookWeight);

            _headBaseLocal = baseLocal;
            _headWrittenLocal = _headBone.localRotation;
            _headWritten = true;
        }

        #endregion
    }
}
