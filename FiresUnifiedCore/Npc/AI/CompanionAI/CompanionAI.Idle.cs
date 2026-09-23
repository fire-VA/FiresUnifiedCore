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

            // Decorative stationed NPC (no route, no wander): now that the FSM runs for stationed NPCs, hold
            // post here instead of falling through to random wander. A patrol NPC returned above via its active
            // sub-behavior; a WANDERING stationed NPC has AllowsIdleBehaviors=true and is unaffected.
            if (_npcModule != null && _npcModule.IsStationedAsNpc && !_npcModule.AllowsIdleBehaviors
                && (_targetCreature == null || _targetCreature.IsDead()))
            {
                StopMovementThroughAuthority();
                return;
            }

            if (_shouldFollow && _followTarget != null)
            {
                // AFK-IDLE BRANCH: We landed in Idle from a Following→Idle transition
                // because the owner stopped moving for >= playerIdleThreshold. Stay idle
                // (and let _idleBehavior wander locally) until we detect the owner moving
                // again, then snap back to Following on the next tick.
                //
                // UpdateOwnerIdleState polls owner position with a 0.5s throttle and
                // resets _isOwnerIdle = false the moment ownerMovement > MovementThreshold.
                UpdateOwnerIdleState(_followTarget.transform.position);
                if (!_isOwnerIdle)
                {
                    SetState(AIState.Following);
                    return;
                }
                // Owner still AFK — fall through into idle/wander logic below.
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
                if (MoveToWithAuthority(_idleDestination, false, PositionReachedThreshold))
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

            // OWNER-AFK → IDLE-WANDER:
            // If the owner has been stationary for ≥ playerIdleThreshold seconds,
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

            // Always-on, throttled (2s) follow diagnostic — pins the "won't catch up / stick close" behavior
            // without per-frame verbose spam. Shows the live gap, the chosen speed tier, and the owner stance
            // that drove it, on whichever peer owns the companion (client when near, server when far).
            if (FiresCore.Config.ConfigManager.Instance?.configCompanionFollowDiag?.Value == true
                && Time.time - _lastFollowDiag > 2f)
            {
                _lastFollowDiag = Time.time;
                Debug.Log($"[CompanionFollowDiag] {m_character?.m_name} dist={distToOwner:F1} speed={_currentFollowSpeed} " +
                          $"ownerSpeed={_ownerSpeed:F1} ownerWalk={_isOwnerWalking} ownerSneak={_isOwnerSneaking} closing={_isClosingGap}");
            }
            
            // Apply crouch/sneak state independently of movement speed.
            // The companion should mirror the owner's crouch even when stationary.
            // _isOwnerSneaking is set by UpdateOwnerStance() from the owner's IsCrouching().
            bool shouldSneak = _isOwnerSneaking && distToOwner <= catchUpDistance; // stand up to sprint back if stranded
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

            // VikHavn prone: crawl beside a prone owner, at the owner's crawl speed.
            if (shouldSneak && _stance != null)
                _stance.SetProne(_isOwnerProne, _ownerProneSpeed);
            
            // MOVEMENT STATE SYNC: Set the companion's walk/run flags on Character
            // so Valheim's UpdateWalking() picks the correct speed tier and animation.
            //   m_walk=true  -> uses m_walkSpeed (slow walk)
            //   m_run=true   -> uses m_runSpeed  (sprint)
            //   both false   -> uses m_speed     (default jog)
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
                if (!authority.TryAcquireAuthority(source, AIAuthorityOwner, 2f))
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
            // BaseAI.MoveTo() uses FindPath() and m_path waypoints for obstacle avoidance.
            // Escape hatch (see MoveToThroughAuthority): open the single-writer gate around the vanilla
            // MoveTo so its direct SetMoveDir is allowed through; try/finally always re-closes it.
            try
            {
                authority?.DisableExternalBlocking();
                return MoveTo(Time.deltaTime, target, reachDistance, run);
            }
            finally
            {
                authority?.EnableExternalBlocking();
            }
        }
        
        /// <summary>
        /// Stops movement, clears stale path data, and releases authority.
        /// Clearing m_path prevents BaseAI.UpdateMovement from re-issuing
        /// SetMoveDir with stale waypoints in the same frame.
        /// </summary>
        private void StopMovementThroughAuthority()
        {
            var authority = GetMovementAuthority();
            authority?.ReleaseAuthority(AIAuthorityOwner);

            // Clear the pathfinding waypoint list so BaseAI doesn't fight us; without also forgetting the cached
            // search, a MoveTo inside FindPath's cache window reads the empty path as "arrived".
            if (m_path != null)
                m_path.Clear();
            InvalidatePathCache();

            StopMoving();
        }
        
        /// <summary>
        /// Reads the owner player's movement stance (crouch, walk, run) at a throttled interval.
        /// Valheim's Character class exposes:
        ///   - IsCrouching() - true when sneaking
        ///   - IsWalking()   - true when m_walk is set and moving (toggled walk mode)
        ///   - IsRunning()   - true when sprinting (m_run + moving + stamina)
        ///   - Default jog   - when none of the above are true but moving
        /// </summary>
        private void UpdateOwnerStance()
        {
            if (Time.time - _lastOwnerStanceCheck < OwnerStanceCheckInterval) return;
            _lastOwnerStanceCheck = Time.time;
            
            // Lazy-cache the Player component from the follow target
            if (_ownerPlayer == null && _followTarget != null)
            {
                _ownerPlayer = _followTarget.GetComponent<Player>();
            }
            
            if (_ownerPlayer != null)
            {
                _isOwnerSneaking = _ownerPlayer.IsCrouching();
                _isOwnerProne = _isOwnerSneaking && Movement.CompanionStance.IsPlayerProne(_ownerPlayer);
                if (_isOwnerProne) _ownerProneSpeed = Movement.CompanionStance.PlayerProneSpeedMultiplier(_ownerPlayer);
                _isOwnerWalking = _ownerPlayer.IsWalking();
                _isOwnerRunning = _ownerPlayer.IsRunning();

                // Measured horizontal speed is the ground truth for pace matching — the IsRunning/IsWalking flags
                // read stale (they logged False while the owner was clearly moving). This drives which gait the
                // companion mirrors once it's tucked into the trail band.
                Vector3 ownerVelocity = _ownerPlayer.GetVelocity();
                ownerVelocity.y = 0f;
                _ownerSpeed = ownerVelocity.magnitude;
            }
        }
        
        private void DetermineFollowSpeed(float distToOwner)
        {
            // Resolve the gait that MATCHES the owner right now, from their measured speed + stance. Player
            // default movement is run, walk is a toggle, crouch is sneak. Measured owner speed (not the stale
            // IsRunning flag) picks the pace tier; the crouch flag forces Sneaking.
            FollowSpeed matched;
            if (_isOwnerSneaking)
                matched = FollowSpeed.Sneaking;
            else if (_ownerSpeed < OwnerStandingSpeed)
                matched = FollowSpeed.Stopped;
            else if (_isOwnerWalking || _ownerSpeed < OwnerWalkSpeed)
                matched = FollowSpeed.Walking;
            else if (_ownerSpeed < OwnerJogSpeed)
                matched = FollowSpeed.Jogging;
            else
                matched = FollowSpeed.Running;

            // EMERGENCY: way behind the owner -> sprint to close, regardless of the owner's pace or stance.
            if (distToOwner > catchUpDistance)
            {
                _currentFollowSpeed = FollowSpeed.Sprinting;
                _isClosingGap = true;
                return;
            }

            // OUT OF THE TRAIL BAND -> CLOSE the gap. Match the owner's urgency: a running owner needs a run to
            // keep pace AND close; a slow / standing owner only needs a gentle jog to tuck back in, never a
            // sprint (this is what stops "always running/sprinting behind a walking owner"). Hysteresis: once
            // closing, keep closing until tucked inside the inner trail distance.
            bool closing = distToOwner > runDistanceOuter
                        || (_isClosingGap && distToOwner > stopDistanceInner);
            if (closing)
            {
                _isClosingGap = distToOwner > stopDistanceInner;
                _currentFollowSpeed = (matched == FollowSpeed.Running) ? FollowSpeed.Running : FollowSpeed.Jogging;
                return;
            }

            // TUCKED IN behind the owner -> MATCH their pace + stance exactly (walk when they walk, sneak when
            // they sneak, run when they run, stop when they stop). This is the pace / stance match.
            _isClosingGap = false;
            _currentFollowSpeed = matched;
        }
        
        /// <summary>
        /// Applies the correct m_walk / m_run flags on the companion's Character
        /// based on the current follow speed tier. This ensures Valheim's
        /// Character.UpdateWalking() uses the matching speed and animation.
        /// </summary>
        private void ApplyFollowSpeedToCharacter(FollowSpeed speed)
        {
            if (m_character == null) return;

            // Publish the run-tier catch-up boost so CompanionSpeedRamp can push the follower ABOVE its
            // archetype run speed while catching up (a plain run only matches an owner running at the same
            // speed and never closes). Sprinting = emergency, Running = gentle tuck-in, everything else
            // clears the boost back to 1.0 so a stopped/jogging/walking companion runs at its base speed.
            float catchUpBoost = 1f;
            if (speed == FollowSpeed.Sprinting) catchUpBoost = SprintCatchUpBoost;
            else if (speed == FollowSpeed.Running) catchUpBoost = RunCatchUpBoost;
            CompanionSpeedRamp.SetCatchUpMultiplier(m_character, catchUpBoost);

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
                    // Toggled walk mode - uses m_walkSpeed.
                    m_character.SetWalk(true);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Jogging:
                    // Default movement - uses m_speed (jog).
                    m_character.SetWalk(false);
                    m_character.SetRun(false);
                    break;
                    
                case FollowSpeed.Running:
                    // Sprint - uses m_runSpeed.
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
            const float CheckInterval = 0.5f;
            const float MovementThreshold = 0.3f;
            
            if (Time.time - _lastOwnerIdleCheck < CheckInterval) return;
            _lastOwnerIdleCheck = Time.time;
            
            float ownerMovement = Vector3.Distance(currentOwnerPos, _lastOwnerPosition);
            
            if (ownerMovement > MovementThreshold)
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
