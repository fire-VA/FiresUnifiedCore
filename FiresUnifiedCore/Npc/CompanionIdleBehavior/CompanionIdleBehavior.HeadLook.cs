using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    // Head look-at system
    public partial class CompanionIdleBehavior
    {
        #region Head Look-At

        private void FindHeadBone()
        {
            if (_animator == null) return;

            _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);

            if (_headBone == null)
            {
                var bones = GetComponentsInChildren<Transform>();
                foreach (var bone in bones)
                {
                    string boneName = bone.name.ToLowerInvariant();
                    if (boneName.Contains("head") && !boneName.Contains("headeffect"))
                    {
                        _headBone = bone;
                        break;
                    }
                }
            }

            if (_headBone != null)
            {
                _headBaseRotation = _headBone.localRotation;
                _headBoneFound = true;
            }
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
                if (character == _character) return;
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
            if (!enableHeadLookAt || !_headBoneFound || _headBone == null) return;
            if (_isSittingOnChair || _isPlayingEmote) return;
            
            // Skip if weight is very low
            if (_headLookWeight < 0.01f)
            {
                _headBone.localRotation = _headBaseRotation;
                return;
            }

            try
            {
                Vector3 lookDir = _currentHeadLookDirection;
                lookDir.y *= 0.5f;

                // CRITICAL: Check magnitude BEFORE operations to avoid zero vector issues
                float magnitude = lookDir.magnitude;
                if (magnitude < 0.001f)
                {
                    _headBone.localRotation = _headBaseRotation;
                    return;
                }
                
                lookDir = lookDir / magnitude; // Manual normalize

                Quaternion targetRotation = Quaternion.LookRotation(lookDir);
                
                // Validate quaternion before using
                if (float.IsNaN(targetRotation.x) || float.IsNaN(targetRotation.y) || 
                    float.IsNaN(targetRotation.z) || float.IsNaN(targetRotation.w))
                {
                    _headBone.localRotation = _headBaseRotation;
                    return;
                }
                
                Quaternion localTarget = Quaternion.Inverse(transform.rotation) * targetRotation;
                
                // Validate inverse result
                if (float.IsNaN(localTarget.x) || float.IsNaN(localTarget.y) || 
                    float.IsNaN(localTarget.z) || float.IsNaN(localTarget.w))
                {
                    _headBone.localRotation = _headBaseRotation;
                    return;
                }

                Vector3 euler = localTarget.eulerAngles;
                euler.x = ClampAngle(euler.x, -25f, 35f);
                euler.y = ClampAngle(euler.y, -maxLookAtAngle, maxLookAtAngle);
                euler.z = 0f;

                localTarget = Quaternion.Euler(euler);
                
                Quaternion blendedRotation = Quaternion.Slerp(_headBaseRotation, _headBaseRotation * localTarget, _headLookWeight);
                
                // Final validation before applying
                if (!float.IsNaN(blendedRotation.x) && !float.IsNaN(blendedRotation.y) && 
                    !float.IsNaN(blendedRotation.z) && !float.IsNaN(blendedRotation.w))
                {
                    _headBone.localRotation = blendedRotation;
                }
                else
                {
                    _headBone.localRotation = _headBaseRotation;
                }
            }
            catch { }
        }

        private float ClampAngle(float angle, float min, float max)
        {
            if (angle > 180f) angle -= 360f;
            return Mathf.Clamp(angle, min, max);
        }

        #endregion
    }
}
