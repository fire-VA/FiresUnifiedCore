using UnityEngine;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc
{
    // Stuck detection and animation reset utilities
    public partial class CompanionIdleBehavior
    {
        #region Stuck Prevention

        // Movement progress tracking for wander stuck detection
        private Vector3 _lastWanderProgressPosition;
        private float _lastWanderProgressTime;
        private const float WANDER_PROGRESS_CHECK_INTERVAL = 8f;
        private const float WANDER_MIN_PROGRESS_DISTANCE = 0.5f;

        // Mecanim trigger that the looping-emote state machine watches for
        // to exit; clearing the bool alone does NOT leave the looping state.
        private static readonly int EmoteStopTriggerHash = Animator.StringToHash("emote_stop");

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
                    if (_hasActiveDestination && Time.time - _lastWanderProgressTime >= WANDER_PROGRESS_CHECK_INTERVAL)
                    {
                        float moved = Vector3.Distance(transform.position, _lastWanderProgressPosition);
                        _lastWanderProgressPosition = transform.position;
                        _lastWanderProgressTime = Time.time;

                        if (moved < WANDER_MIN_PROGRESS_DISTANCE)
                        {
                            if (VerboseLogging)
                                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stuck wandering (moved {moved:F2}m in {WANDER_PROGRESS_CHECK_INTERVAL}s) - resetting");
                            _isWandering = false;
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
            // Call StopEmote() on the character via reflection.
            // Vanilla Valheim's StopEmote() sets the emote bool to false in
            // ZSyncAnimator AND clears the internal m_emoteID field. Both steps
            // are needed for the Mecanim state machine to exit the looping
            // emote state. Without clearing m_emoteID the animator controller
            // keeps the state active even after the bool transitions out.
            // StopEmote lives on Player but the underlying animation system is
            // shared with Humanoid/Character, so the call works on companion NPCs.
            if (_character != null)
            {
                var stopEmote = _character.GetType().GetMethod(
                    "StopEmote",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (stopEmote != null)
                {
                    stopEmote.Invoke(_character, null);
                }
                else
                {
                    // Fallback: replicate exactly what StopEmote does internally ï¿½
                    // clear the ZSyncAnimator bool and null out m_emoteID.
                    if (!string.IsNullOrEmpty(_currentEmote) && _zanim != null)
                        _zanim.SetBool(_currentEmote, false);

                    var emoteIdField = typeof(Character).GetField(
                        "m_emoteID",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    emoteIdField?.SetValue(_character, "");
                }
            }

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
            // Clear emote animation bool before clearing _currentEmote
            if (_isPlayingEmote && !string.IsNullOrEmpty(_currentEmote))
            {
                if (_zanim != null)
                    _zanim.SetBool(_currentEmote, false);
                if (_animator != null && HasAnimatorParameter(_currentEmote))
                    _animator.SetBool(_currentEmote, false);
            }
            
            _isLookingAround = false;
            _isWandering = false;
            _isWaitingAtWanderPoint = false;
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

        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Forces a complete animation state reset to fix stuck/sliding animations.
        /// This ensures the character returns to proper idle/movement states.
        /// </summary>
        private void ForceAnimationStateReset()
        {
            string[] allEmoteBools = new string[]
            {
                // Sitting and resting poses
                "sitting", "resting", "sleeping",
                
                // Persistent emotes
                "emote_sit", "emote_rest", "emote_vibe",
                "emote_kneel", "emote_despair", "emote_headbang", "emote_dance",
                
                // Quick emotes
                "emote_point", "emote_wave", "emote_challenge",
                "emote_cheer", "emote_nonono", "emote_thumbsup", "emote_flex",
                "emote_laugh", "emote_shrug", "emote_blowkiss", "emote_bow",
                "emote_cry", "emote_comehere", "emote_roar", "emote_toast", "emote_loveyou",
                
                // Attach animations
                "attach_chair", "attach_stool", "attach_bed", "attach_mast",
                
                // Movement states
                "forward", "backward", "left", "right",
                "run", "walk", "crouch", "jump", "inwater"
            };
            
            // Reset ZSyncAnimation
            if (_zanim != null)
            {
                foreach (var emoteBool in allEmoteBools)
                {
                    _zanim.SetBool(emoteBool, false);
                }

                _zanim.SetFloat("statef", 0f);
                _zanim.SetFloat("statei", 0f);
                _zanim.SetTrigger("idle");
                // Looping-emote transitions are gated on emote_stop, not on
                // the bool going false. Without firing this trigger the
                // animator stays parked in the emote state.
                _zanim.SetTrigger("emote_stop");
            }

            // Reset Animator
            if (_animator != null)
            {
                foreach (var emoteBool in allEmoteBools)
                {
                    if (HasAnimatorParameter(emoteBool))
                        _animator.SetBool(emoteBool, false);
                }

                if (HasAnimatorParameter("forward_speed"))
                    _animator.SetFloat("forward_speed", 0f);
                if (HasAnimatorParameter("sideways_speed"))
                    _animator.SetFloat("sideways_speed", 0f);
                if (HasAnimatorParameter("turn_speed"))
                    _animator.SetFloat("turn_speed", 0f);

                if (HasAnimatorParameter("moving"))
                    _animator.SetBool("moving", false);

                _animator.SetTrigger(EmoteStopTriggerHash);

                _animator.Update(0f);
            }
            
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
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} animation state forcefully reset (cleared {allEmoteBools.Length} emote bools)");

                    }

                    #endregion
                }
            }
