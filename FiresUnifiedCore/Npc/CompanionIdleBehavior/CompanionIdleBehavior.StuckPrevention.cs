using UnityEngine;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Animation;

namespace FiresCore.Npc
{
    // Stuck detection and animation reset utilities
    public partial class CompanionIdleBehavior
    {
        #region Stuck Prevention

        // Movement progress tracking for wander stuck detection
        private Vector3 _lastWanderProgressPosition;
        private float _lastWanderProgressTime;
        private const float WanderProgressCheckInterval = 8f;
        private const float WanderMinProgressDistance = 0.5f;

        private void CheckForStuckState()
        {
            if (Time.time - _lastStuckCheck < stuckCheckInterval) return;
            _lastStuckCheck = Time.time;

            float timeInCurrentState = Time.time - _currentStateStartTime;

            switch (_currentIdleState)
            {
                case IdleState.PlayingEmote:
                    if (timeInCurrentState > maxEmoteHardTimeout)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} emote hard timeout - forcing reset");
                        ForceEndEmote();
                    }
                    break;

                case IdleState.SittingOnChair:
                    if (Time.time >= _chairHardTimeoutTime)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} chair sit hard timeout - forcing stand");
                        ForceStandUp();
                    }
                    break;

                case IdleState.Wandering:
                    // Quick stuck detection: if not making progress, abort early
                    if (_hasActiveDestination && Time.time - _lastWanderProgressTime >= WanderProgressCheckInterval)
                    {
                        float moved = Vector3.Distance(transform.position, _lastWanderProgressPosition);
                        _lastWanderProgressPosition = transform.position;
                        _lastWanderProgressTime = Time.time;

                        if (moved < WanderMinProgressDistance)
                        {
                            if (VerboseLogging)
                                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stuck wandering (moved {moved:F2}m in {WanderProgressCheckInterval}s) - resetting");
                            _hasActiveDestination = false;
                            ForceResetToStanding();
                            break;
                        }
                    }
                    if (timeInCurrentState > maxIdleStateDuration)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stuck in {_currentIdleState} for {timeInCurrentState:F1}s - resetting");
                        ForceResetToStanding();
                    }
                    break;

                case IdleState.LookingAround:
                case IdleState.WaitingAtWanderPoint:
                    if (timeInCurrentState > maxIdleStateDuration)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stuck in {_currentIdleState} for {timeInCurrentState:F1}s - resetting");
                        ForceResetToStanding();
                    }
                    break;

                case IdleState.SubBehavior:
                    // Sub-behaviors handle their own timeouts
                    break;
            }
        }

        private void ForceEndEmote()
        {
            // Character.StopEmote is an empty virtual on Humanoid; end it the way Player.UpdateEmote does.
            PlayerAnimationCatalog.StopEmotes(_zanim, _animator);

            // Exit state controller emote state
            if (_stateController != null)
            {
                _stateController.StopEmote();
            }

            _isPlayingEmote = false;
            _currentEmote = "";
            _isPersistentEmote = false;

            // Unlock movement
            if (_combatMovement != null && _combatMovement.IsMovementLocked)
            {
                _combatMovement.UnlockMovement();
            }

            // Clear stale movement destinations
            if (_combatMovement != null)
            {
                _combatMovement.ClearMoveDestination();
            }

            if (_companionAI != null)
            {
                _companionAI.ClearIdleDestination();
                _companionAI.ClearCommandDestination();
            }

            // Zero velocity if not kinematic
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            ForceAnimationStateReset();

            if (_stateController != null)
            {
                _stateController.ResetAnimationState();
            }

            SetIdleState(IdleState.Standing);

            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} emote ended, all movement states reset");
        }

        private void ForceStandUp()
        {
            StandUpFromChair();
            ForceAnimationStateReset();
            
            if (_stateController != null)
            {
                _stateController.ExitState(CompanionStateController.CompanionState.Idle);
            }
            
            if (_companionAI != null)
            {
                _companionAI.ClearIdleDestination();
                _companionAI.ClearCommandDestination();
            }
            
            if (_combatMovement != null)
            {
                if (_combatMovement.IsMovementLocked)
                    _combatMovement.UnlockMovement();
                _combatMovement.ClearMoveDestination();
            }
            
            SetIdleState(IdleState.Standing);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stood up - all movement states reset");
        }

        private void ForceResetToStanding()
        {
            _isPlayingEmote = false;
            _currentEmote = "";
            _isPersistentEmote = false;
            _currentWanderLeg = 0;
            _hasActiveDestination = false;
            _isRotating = false;

            if (_isSittingOnChair)
                StandUpFromChair();

            CancelActiveSubBehavior();

            ForceAnimationStateReset();

            SetIdleState(IdleState.Standing);
        }

        /// <summary>
        /// Releases emotes and seat poses (PlayerAnimationCatalog.StopAll) and stops the body. The weapon pose
        /// (statef/statei) and the locomotion floats belong to vanilla Humanoid and Character and are left alone.
        /// </summary>
        private void ForceAnimationStateReset()
        {
            PlayerAnimationCatalog.StopAll(_zanim, _animator);

            // Ensure physics are grounded
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} animation state reset");
        }

        #endregion
    }
}
