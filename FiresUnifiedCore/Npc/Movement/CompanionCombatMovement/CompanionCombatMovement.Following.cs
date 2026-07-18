using UnityEngine;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc
{
    /// <summary>
    /// Following state, player idle handling, and non-combat intent management.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Following State

        private void ExecuteIdleState()
        {
            ClearCombatCommitment();
            UpdateNonCombatIntent();
            
            if (_currentIntent == MovementIntent.Idle && _idleBehavior != null)
            {
                // A movement sub-behavior (patrol, farming, smelter, chest, ...) is already driving the body
                // through CompanionAI's vanilla pathfinding. Park here exactly like ExecuteFollowingState parks
                // for follow — otherwise StopMovementGradually() below becomes a SECOND per-frame SetMoveDir
                // writer fighting the sub-behavior, zeroing the move it just set (body reads commanded-but-frozen).
                // The sub-behavior owns its own walk/run while it's active.
                if (_idleBehavior.IsInSubBehavior)
                    return;

                // Let idle behavior handle movement - it uses CompanionAI.RequestPathfindingMovement()
                // which properly integrates with the authority system
                bool idleHandlingMovement = _idleBehavior.UpdateIdleBehavior();
                if (idleHandlingMovement)
                {
                    // IdleBehavior is handling movement - just set walk mode
                    SetWalkRunSafe(true, false);
                }
                else
                {
                    // Not actively moving - gradually stop any residual movement
                    if (_currentMoveDirection.sqrMagnitude > 0.01f)
                        StopMovementGradually();
                }
            }
            else
            {
                // No idle behavior or different intent
                // Don't apply movement behavior - let CompanionAI handle idle wander
                ApplyVelocityClamping();
            }
        }
        
        private void ExecuteFollowingState()
        {
            ClearCombatCommitment();

            // Defense-in-depth (mirrors ExecuteIdleState's guard at the top of this file): if a work
            // sub-behavior owns the body, park — never churn follow intent / relaxed-follow movement that
            // would fight it and pull the companion back to the owner. The FSM should already be Skipped
            // via EvaluateState's IsInSubBehavior gate, but we never drive here regardless.
            if (_idleBehavior != null && _idleBehavior.IsInSubBehavior)
                return;

            // CRITICAL FIX: CompanionCombatMovement should NOT handle following movement!
            // CompanionAI.UpdateFollowMovement() handles following with proper vanilla pathfinding.
            // If we also try to move here, we fight for the same Following authority and cause glitchy movement.
            // 
            // CompanionCombatMovement's role is:
            // - Combat movement (strafing, dodging, approaching enemies)
            // - Player command movement (ping move)
            // - NOT following - that's CompanionAI's job
            //
            // We still update non-combat intent to track player distance/speed for state transitions,
            // but we do NOT apply movement behavior - let CompanionAI handle the actual following.
            UpdateNonCombatIntent();
            
            // Only apply velocity clamping to smooth out any inherited velocity from combat
            ApplyVelocityClamping();
        }

        #endregion

        #region Player Idle Detection

        private void UpdatePlayerIdleState()
        {
            _playerIdleHandler?.Update();
            
            _isPlayerIdle = _playerIdleHandler?.IsPlayerIdle ?? false;
            _isRelaxedFollowing = _playerIdleHandler?.IsRelaxedFollowing ?? false;
        }
        
        public bool ShouldRelaxDueToPlayerIdle()
        {
            return _playerIdleHandler?.ShouldRelaxDueToPlayerIdle() ?? false;
        }
        
        public void ResetPlayerIdleState()
        {
            _playerIdleHandler?.ResetPlayerIdleState();
            _isPlayerIdle = false;
        }

        #endregion

        #region Non-Combat Intent

        private void UpdateNonCombatIntent()
        {
            var owner = _companion?.GetOwner();
            bool shouldBeFollowing = _companion?.ShouldBeFollowing ?? false;

            if ((shouldBeFollowing && owner == null) || !shouldBeFollowing)
            {
                SetFollowIntent(MovementIntent.Idle);
                _shouldCheckStuck = false;
                return;
            }

            float distToOwner = Vector3.Distance(transform.position, owner.transform.position);
            bool ownerMoving = IsPlayerMoving(owner);
            bool canSprint = _staminaManager?.ShouldSprint() ?? true;
            
            if (ownerMoving && _isRelaxedFollowing)
            {
                _playerIdleHandler?.ResetPlayerIdleState();
                _isPlayerIdle = false;
                _isRelaxedFollowing = false;
                _moveDirSet = false;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} exiting relaxed state - player is moving");
            }
            
            if (_isRelaxedFollowing && distToOwner <= idleStopDistance)
            {
                UpdateRelaxedFollowingBehavior(owner, distToOwner);
                return;
            }
            
            if (_followIntentController != null)
            {
                if (_followIntentController.ShouldStop(distToOwner, ownerMoving))
                {
                    SetFollowIntent(MovementIntent.Idle);
                    _shouldCheckStuck = false;
                    return;
                }
                
                if (_followIntentController.IsInIdleWanderZone(distToOwner, ownerMoving))
                {
                    SetFollowIntent(MovementIntent.Idle);
                    _shouldCheckStuck = false;
                    return;
                }
                
                var desiredFollowIntent = _followIntentController.DetermineIntent(distToOwner, ownerMoving, canSprint);
                MovementIntent desiredIntent = ConvertFollowIntent(desiredFollowIntent);
                
                if (_followIntentController.ShouldChangeIntent(desiredFollowIntent, distToOwner))
                {
                    _followIntentController.SetIntent(desiredFollowIntent);
                    SetFollowIntent(desiredIntent);
                }
                
                _shouldCheckStuck = _followIntentController.ShouldCheckStuck(distToOwner);
            }
            else
            {
                MovementIntent desiredIntent = DetermineFollowIntent(distToOwner, ownerMoving, canSprint);
                if (ShouldChangeFollowIntent(desiredIntent, distToOwner))
                    SetFollowIntent(desiredIntent);
                
                _shouldCheckStuck = _currentIntent == MovementIntent.FollowingFar || 
                                   _currentIntent == MovementIntent.CatchingUp;
            }
        }
        
        private MovementIntent ConvertFollowIntent(FollowIntent intent)
        {
            return intent switch
            {
                FollowIntent.Idle => MovementIntent.Idle,
                FollowIntent.Walk => MovementIntent.FollowingClose,
                FollowIntent.Jog => MovementIntent.FollowingMedium,
                FollowIntent.Run => MovementIntent.FollowingFar,
                FollowIntent.Sprint => MovementIntent.CatchingUp,
                _ => MovementIntent.Idle
            };
        }
        
        private void UpdateRelaxedFollowingBehavior(Player owner, float distToOwner)
        {
            if (_playerIdleHandler == null)
            {
                StopMovementGradually();
                return;
            }
            
            if (_playerIdleHandler.CheckPlayerMovingNow())
            {
                _playerIdleHandler.ResetPlayerIdleState();
                _isPlayerIdle = false;
                _isRelaxedFollowing = false;
                
                _moveDirSet = false;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} snapping back to following - player started moving");
                
                return;
            }
            
            SetFollowIntent(MovementIntent.Idle);
            _shouldCheckStuck = false;
            
            Vector3 wanderDir = _playerIdleHandler.UpdateRelaxedBehavior(out bool shouldStop);
            
            if (shouldStop)
            {
                StopMovementGradually();
            }
            else if (wanderDir.sqrMagnitude > 0.01f)
            {
                SetMoveDirSafe(wanderDir);
                SetWalkRunSafe(true, false);
            }
        }
        
        private MovementIntent DetermineFollowIntent(float distToOwner, bool ownerMoving, bool canSprint)
        {
            if (distToOwner > followSprintDistance)
                return canSprint ? MovementIntent.CatchingUp : MovementIntent.FollowingFar;
            if (distToOwner > followRunDistanceOuter)
                return MovementIntent.FollowingFar;
            if (distToOwner > followJogDistanceOuter)
                return MovementIntent.FollowingMedium;
            if (distToOwner > followWalkDistanceOuter && ownerMoving)
                return MovementIntent.FollowingClose;
            return ownerMoving ? MovementIntent.FollowingClose : MovementIntent.Idle;
        }
        
        private bool ShouldChangeFollowIntent(MovementIntent desiredIntent, float distToOwner)
        {
            if (_lastFollowIntent == MovementIntent.Idle && desiredIntent != MovementIntent.Idle)
                return true;
            if (desiredIntent == MovementIntent.Idle && distToOwner <= followStopDistanceOuter)
                return true;
            return true;
        }
        
        private void SetFollowIntent(MovementIntent intent)
        {
            if (intent != _lastFollowIntent)
            {
                _followIntentChangeTime = Time.time;
                _lastFollowIntent = intent;
            }
            SetIntent(intent);
        }

        #endregion
    }
}
