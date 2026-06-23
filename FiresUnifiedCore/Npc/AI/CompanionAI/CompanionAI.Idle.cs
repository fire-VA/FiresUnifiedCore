using UnityEngine;
using FiresCore.Npc.Core;
using FiresCore.Npc.Formation;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Idle state and following state logic.
    /// 
    /// MOVEMENT ARCHITECTURE (Post-Bug #8 Fix):
    /// - Authority = COORDINATION (decides WHO can move)
    /// - Vanilla MoveTo() = PATHFINDING (handles HOW to move)
    /// 
    /// All movement methods check authority first, then use vanilla pathfinding.
    /// </summary>
    public partial class CompanionAI
    {
        #region Idle State

        private void UpdateIdleState(float dt)
        {
            // CRITICAL: Don't transition to following if a command/sub-behavior is active
            // Sub-behaviors (like mining, smelter operation) should have priority over following
            bool hasActiveSubBehavior = _idleBehavior != null && _idleBehavior.IsInSubBehavior;

            if (hasActiveSubBehavior)
            {
                // Let the sub-behavior handle movement - don't interfere
                return;
            }

            if (_shouldFollow && _followTarget != null)
            {
                // AFK-IDLE BRANCH: We landed in Idle from a Followingâ†’Idle transition
                // because the owner stopped moving for >= playerIdleThreshold. Stay idle
                // (and let _idleBehavior wander locally) until we detect the owner moving
                // again, then snap back to Following on the next tick.
                //
                // UpdateOwnerIdleState polls owner position with a 0.5s throttle and
                // resets _isOwnerIdle = false the moment ownerMovement > MOVEMENT_THRESHOLD.
                UpdateOwnerIdleState(_followTarget.transform.position);
                if (!_isOwnerIdle)
                {
                    SetState(AIState.Following);
                    return;
                }
                // Owner still AFK â€” fall through into idle/wander logic below.
            }

            if (_targetCreature != null && !_targetCreature.IsDead())
            {
                SetState(AIState.Combat);
                return;
            }

            if (IsAlerted())
            {
                ClearAlertedState();
            }

            // Execute idle destination if CompanionIdleBehavior set one
            if (_hasIdleDestination)
            {
                float distToDest = Vector3.Distance(transform.position, _idleDestination);
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} executing idle movement to {_idleDestination} (dist: {distToDest:F1}m)");
                
                // Use vanilla pathfinding with authority coordination
                if (MoveToWithAuthority(_idleDestination, false, POSITION_REACHED_THRESHOLD))
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} reached idle destination");
                    _hasIdleDestination = false;
                }
                return;
            }

            // CONSOLIDATED: Delegate ALL idle behavior to CompanionIdleBehavior
            // This is the SINGLE source of idle wandering, emotes, sub-behaviors, etc.
            // Do NOT duplicate wandering logic here - let the idle behavior system handle it.
            if (_idleBehavior != null)
            {
                _idleBehavior.UpdateIdleBehavior();
            }
        }

        private void UpdateFollowingState(float dt)
        {
            // CRITICAL: Don't continue following if a command/sub-behavior is active
            bool hasActiveSubBehavior = _idleBehavior != null && _idleBehavior.IsInSubBehavior;

            if (hasActiveSubBehavior)
            {
                // Sub-behavior is active - stop following and let it handle movement
                StopMoving();
                return;
            }

            if (_targetCreature != null && !_targetCreature.IsDead())
            {
                SetState(AIState.Combat);
                return;
            }

            if (!_shouldFollow || _followTarget == null)
            {
                SetState(AIState.Idle);
                return;
            }

            if (IsAlerted())
            {
                ClearAlertedState();
            }

            // OWNER-AFK â†’ IDLE-WANDER:
            // If the owner has been stationary for â‰¥ playerIdleThreshold seconds,
            // drop the companion into Idle state so _idleBehavior can take over with
            // local wander/sub-behaviors. UpdateIdleState will snap us back to
            // Following the moment the owner moves again.
            UpdateOwnerIdleState(_followTarget.transform.position);
            if (_isOwnerIdle && _idleBehavior != null)
            {
                SetState(AIState.Idle);
                return;
            }

            UpdateFollowMovement(dt);
        }

        private void UpdateFollowMovement(float dt)
        {
            if (_followTarget == null) return;

            Vector3 ownerPos = _followTarget.transform.position;
            
            // FORMATION SYSTEM: Offset follow target by formation position
            // This makes companions spread into V-formation behind the player
            // instead of all pathing to the exact same point.
            var formationController = _companion?.GetFormationController();
            if (formationController != null)
            {
                ownerPos = formationController.GetAdjustedFollowTarget(ownerPos);
                // Also apply personal space separation to prevent stacking
                ownerPos = formationController.ApplySeparation(ownerPos);
            }
            
            float distToOwner = Vector3.Distance(transform.position, ownerPos);
            
            UpdateOwnerIdleState(ownerPos);
            UpdateOwnerStance();
            DetermineFollowSpeed(distToOwner);
            
            // Apply crouch/sneak state independently of movement speed.
            // The companion should mirror the owner's crouch even when stationary.
            // _isOwnerSneaking is set by UpdateOwnerStance() from the owner's IsCrouching().
            bool shouldSneak = _isOwnerSneaking;
            if (shouldSneak != _isCompanionSneaking)
            {
                _isCompanionSneaking = shouldSneak;
                if (m_character != null)
                {
                    SetCompanionCrouch(shouldSneak);
                }
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} sneak={shouldSneak} (mirroring owner)");
            }
            
            // MOVEMENT STATE SYNC: Set the companion's walk/run flags on Character
            // so Valheim's UpdateWalking() picks the correct speed tier and animation.
            //   m_walk=true  ? uses m_walkSpeed (slow walk)
            //   m_run=true   ? uses m_runSpeed  (sprint)
            //   both false   ? uses m_speed     (default jog)
            ApplyFollowSpeedToCharacter(_currentFollowSpeed);
            
            switch (_currentFollowSpeed)
            {
                case FollowSpeed.Stopped:
                    // Release follow authority so idle wander can take over,
                    // and clear the path so BaseAI doesn't fight us with stale waypoints.
                    StopMovementThroughAuthority();
                    break;

                case FollowSpeed.Sneaking:
                    // Sneak uses walk speed with crouch active
                    MoveToWithAuthority(ownerPos, false, stopDistanceInner);
                    break;
                    
                case FollowSpeed.Walking:
                    // Walking: m_walk=true set above, pass run=false to pathfinding
                    MoveToWithAuthority(ownerPos, false, stopDistanceInner);
                    break;
                    
                case FollowSpeed.Jogging:
                    // Jogging: default speed, neither walk nor run
                    MoveToWithAuthority(ownerPos, false, stopDistanceInner);
                    break;
                    
                case FollowSpeed.Running:
                case FollowSpeed.Sprinting:
                    MoveToWithAuthority(ownerPos, true, stopDistanceInner);
                    break;
            }
        }
        
        /// <summary>
        /// Moves to a target using VANILLA PATHFINDING with authority coordination.
        /// 
        /// This is the CORRECT approach:
        /// 1. Check authority - can we move?
        /// 2. Use BaseAI.MoveTo() - vanilla pathfinding handles obstacles
        /// 
        /// Returns true when destination reached.
        /// </summary>
        private bool MoveToWithAuthority(Vector3 target, bool run, float reachDistance = 0f)
        {
            if (m_character == null) return true;
            
            // Step 1: Check authority - can we move?
            var authority = GetMovementAuthority();
            if (authority != null)
            {
                var source = GetAIMovementSource();
                if (!authority.TryAcquireAuthority(source, AI_AUTHORITY_OWNER, 2f))
                {
                    // Another system has priority - don't move
                    return false;
                }
            }
            
            // Apply terrain awareness hazard avoidance if needed
            if (_terrainAwareness != null && _terrainAwareness.IsInAoeHazard())
            {
                Vector3 escapeDir = _terrainAwareness.GetAoeEscapeDirection();
                if (escapeDir.sqrMagnitude > 0.01f)
                {
                    // Override target to escape hazard
                    target = transform.position + escapeDir * 5f;
                    run = true;
                }
            }
            
            // Step 2: Use VANILLA PATHFINDING
            // BaseAI.MoveTo() uses FindPath() and m_path waypoints for obstacle avoidance
            return MoveTo(Time.deltaTime, target, reachDistance, run);
        }
        
        /// <summary>
        /// Stops movement, clears stale path data, and releases authority.
        /// Clearing m_path prevents BaseAI.UpdateMovement from re-issuing
        /// SetMoveDir with stale waypoints in the same frame.
        /// </summary>
        private void StopMovementThroughAuthority()
        {
            var authority = GetMovementAuthority();
            authority?.ReleaseAuthority(AI_AUTHORITY_OWNER);

            // Clear the pathfinding waypoint list so BaseAI doesn't fight us
            if (m_path != null)
                m_path.Clear();

            StopMoving();
        }
        
        /// <summary>
        /// Reads the owner player's movement stance (crouch, walk, run) at a throttled interval.
        /// Valheim's Character class exposes:
        ///   - IsCrouching() ï¿½ true when sneaking
        ///   - IsWalking()   ï¿½ true when m_walk is set and moving (toggled walk mode)
        ///   - IsRunning()   ï¿½ true when sprinting (m_run + moving + stamina)
        ///   - Default jog   ï¿½ when none of the above are true but moving
        /// </summary>
        private void UpdateOwnerStance()
        {
            if (Time.time - _lastOwnerStanceCheck < OWNER_STANCE_CHECK_INTERVAL) return;
            _lastOwnerStanceCheck = Time.time;
            
            // Lazy-cache the Player component from the follow target
            if (_ownerPlayer == null && _followTarget != null)
            {
                _ownerPlayer = _followTarget.GetComponent<Player>();
            }
            
            if (_ownerPlayer != null)
            {
                _isOwnerSneaking = _ownerPlayer.IsCrouching();
                _isOwnerWalking = _ownerPlayer.IsWalking();
                _isOwnerRunning = _ownerPlayer.IsRunning();
            }
        }
        
        private void DetermineFollowSpeed(float distToOwner)
        {
            // 1) EMERGENCY CATCH-UP ï¿½ distance overrides everything
            if (distToOwner > catchUpDistance)
            {
                _currentFollowSpeed = FollowSpeed.Sprinting;
                return;
            }
            
            // 2) If far enough that we risk falling behind, run regardless of owner stance
            if (distToOwner > runDistanceOuter)
            {
                _currentFollowSpeed = FollowSpeed.Running;
                return;
            }
            
            // Hysteresis: stay running until we close inside the inner threshold
            if (_currentFollowSpeed == FollowSpeed.Running && distToOwner > runDistanceInner)
            {
                return;
            }
            if (_currentFollowSpeed == FollowSpeed.Sprinting && distToOwner > runDistanceInner)
            {
                _currentFollowSpeed = FollowSpeed.Running;
                return;
            }
            
            // 3) CLOSE ENOUGH ï¿½ stop (but preserve sneak posture if owner is crouching)
            if (distToOwner <= stopDistanceInner)
            {
                _currentFollowSpeed = _isOwnerSneaking ? FollowSpeed.Sneaking : FollowSpeed.Stopped;
                return;
            }
            // Hysteresis for stopped state
            if ((_currentFollowSpeed == FollowSpeed.Stopped || (_currentFollowSpeed == FollowSpeed.Sneaking && !_isOwnerSneaking))
                && distToOwner <= stopDistanceOuter)
            {
                _currentFollowSpeed = _isOwnerSneaking ? FollowSpeed.Sneaking : FollowSpeed.Stopped;
                return;
            }
            // Owner idle and close enough ï¿½ stop
            if (_isOwnerIdle && distToOwner <= walkDistanceOuter)
            {
                _currentFollowSpeed = _isOwnerSneaking ? FollowSpeed.Sneaking : FollowSpeed.Stopped;
                return;
            }
            
            // 4) STANCE MATCHING ï¿½ mirror the owner's movement state
            //    Owner sneaking ? companion sneaks (crouch + slow walk)
            //    Owner walking  ? companion walks (m_walk = true, slow speed)
            //    Owner running  ? companion runs  (m_run = true, sprint speed)
            //    Owner jogging  ? companion jogs  (default speed, neither walk nor run)
            //    Owner standing ? companion walks to close gap, then stops
            if (_isOwnerSneaking)
            {
                _currentFollowSpeed = FollowSpeed.Sneaking;
                return;
            }
            
            if (_isOwnerRunning)
            {
                // Owner is sprinting/running ï¿½ always run to keep pace
                _currentFollowSpeed = FollowSpeed.Running;
                return;
            }
            
            if (_isOwnerWalking)
            {
                // Owner has toggled walk mode ï¿½ companion should walk too
                // Valheim walk mode uses m_walkSpeed which is much slower than jog
                _currentFollowSpeed = FollowSpeed.Walking;
                return;
            }
            
            // Owner is jogging (default movement: not walking, not running, not sneaking)
            // or owner is idle. If owner is idle and we still need to close distance, jog.
            _currentFollowSpeed = _isOwnerIdle ? FollowSpeed.Stopped : FollowSpeed.Jogging;
        }
        
        /// <summary>
        /// Applies the correct m_walk / m_run flags on the companion's Character
        /// based on the current follow speed tier. This ensures Valheim's
        /// Character.UpdateWalking() uses the matching speed and animation.
        /// </summary>
        private void ApplyFollowSpeedToCharacter(FollowSpeed speed)
        {
            if (m_character == null) return;
            
            switch (speed)
            {
                case FollowSpeed.Stopped:
                    m_character.SetWalk(false);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Sneaking:
                    // Crouch is handled separately via SetCompanionCrouch.
                    // Walk flag gives us the slow m_crouchSpeed.
                    m_character.SetWalk(true);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Walking:
                    // Toggled walk mode ï¿½ uses m_walkSpeed.
                    m_character.SetWalk(true);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Jogging:
                    // Default movement ï¿½ uses m_speed (jog).
                    m_character.SetWalk(false);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Running:
                    // Sprint ï¿½ uses m_runSpeed.
                    m_character.SetWalk(false);
                    m_character.SetRun(true);
                    break;
                    
                case FollowSpeed.Sprinting:
                    // Emergency catch-up sprint.
                    m_character.SetWalk(false);
                    m_character.SetRun(true);
                    break;
            }
        }
        
        private void UpdateOwnerIdleState(Vector3 currentOwnerPos)
        {
            const float CHECK_INTERVAL = 0.5f;
            const float MOVEMENT_THRESHOLD = 0.3f;
            
            if (Time.time - _lastOwnerIdleCheck < CHECK_INTERVAL) return;
            _lastOwnerIdleCheck = Time.time;
            
            float ownerMovement = Vector3.Distance(currentOwnerPos, _lastOwnerPosition);
            
            if (ownerMovement > MOVEMENT_THRESHOLD)
            {
                _ownerIdleStartTime = Time.time;
                _isOwnerIdle = false;
            }
            else
            {
                if (Time.time - _ownerIdleStartTime >= playerIdleThreshold)
                {
                    _isOwnerIdle = true;
                }
            }
            
            _lastOwnerPosition = currentOwnerPos;
        }

        #endregion
    }
}
