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
                // An owner who stood still far away left an idle companion behind forever: an owner who is idle but no longer close sends it back to Following too, not only one
                // who moves. Following then closes in and drops to idle again within 3 m.
                // Resume at once when the owner has moved > 0.5 m since we went idle, or is > 5 m away.
                Vector3 idleOwnerNow = _followTarget.transform.position;
                float ownerDist = Vector3.Distance(transform.position, idleOwnerNow);
                bool farFromIdleOwner = ownerDist > IdleResumeDistance;
                bool ownerMovedSinceIdle = _idleOwnerAnchorValid && Vector3.Distance(idleOwnerNow, _idleOwnerAnchor) > IdleResumeOwnerMove;
                if (!_isOwnerIdle || farFromIdleOwner || ownerMovedSinceIdle)
                {
                    if (farFromIdleOwner)
                        FollowState($"-> Following: owner {ownerDist:0.0} m away");
                    else if (ownerMovedSinceIdle)
                        FollowState("-> Following: owner moved");
                    _idleOwnerAnchorValid = false;
                    _isOwnerIdle = false;
                    _ownerIdleStartTime = Time.time;   // a fresh 8 s of stillness before idling again
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
                FollowState("sub-behaviour active, not moving");
                StopMoving();
                return;
            }

            if (_targetCreature != null && !_targetCreature.IsDead())
            {
                FollowState($"-> Combat (target {_targetCreature.m_name})");
                SetState(AIState.Combat);
                return;
            }

            if (!_shouldFollow || _followTarget == null)
            {
                FollowState($"-> Idle (shouldFollow {_shouldFollow}, target {(_followTarget != null ? _followTarget.name : "none")})");
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
            // Idle only near the owner (otherwise it wanders and never reaches a still owner): idle
            // wander only once it has come to the owner (within stopDistanceOuter); farther out it keeps following.
            // Idle only after >= 8 s of owner stillness AND within 4 m.
            if (_isOwnerIdle && _idleBehavior != null
                && Vector3.Distance(transform.position, _followTarget.transform.position) <= IdleEnterDistance)
            {
                _idleOwnerAnchor = _followTarget.transform.position;
                _idleOwnerAnchorValid = true;
                FollowState($"-> Idle: owner idle (still for >= {playerIdleThreshold:0.#} s) at {Vector3.Distance(transform.position, _followTarget.transform.position):0.0} m");
                SetState(AIState.Idle);
                return;
            }

            if (TryCatchUpWhenFollowStuck()) return;
            UpdateFollowMovement(dt);
        }

        // A companion can sit a few metres from its owner with no path, too near for the far
        // teleport-with-owner): following with no progress toward the owner for FollowStuckSeconds ends any climb and catches up next to the
        // owner (vanilla-style, the same safe spot as TeleportToOwner). Logged at Debug with the distance and the reason.
        private const float FollowStuckSeconds = 8f, FollowStuckProgress = 0.5f, FollowStuckMinDistance = 3f, FollowStuckCooldown = 20f;
        private const float FollowStuckMoved = 1.5f;
        private const float FollowStuckPauseGap = 1f;          // a gap this long between checks means following paused: restart the watch
        private const float FollowFarStuckMultiplier = 2f;     // over catchUpDistance x this counts as far behind
        private const float FollowFarStuckSeconds = 4f, FollowFarStuckCooldown = 8f;
        private Vector3 _followMovedFrom;
        private float _followBestDistance = float.MaxValue, _followBestAt = -1f, _followCatchUpAt = -100f, _followLastCheck = -100f;

        private bool TryCatchUpWhenFollowStuck()
        {
            if (_followTarget == null || _companion == null || m_character == null) return false;
            var view = m_character.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            // A pause in following (idle, combat, a command) starts the watch over.
            if (Time.time - _followLastCheck > FollowStuckPauseGap) _followBestAt = -1f;
            _followLastCheck = Time.time;
            float dist = Vector3.Distance(transform.position, _followTarget.transform.position);
            // A companion running behind a sprinting owner is not stuck: the companion's own travel counts as progress too, so only a companion that has
            // stayed put (under FollowStuckMoved metres since the watch began) while the gap stays is caught up
            bool movedSinceWatchStart = _followBestAt >= 0f && (transform.position - _followMovedFrom).sqrMagnitude > FollowStuckMoved * FollowStuckMoved;
            if (dist <= FollowStuckMinDistance || _followBestAt < 0f || dist < _followBestDistance - FollowStuckProgress || movedSinceWatchStart)
            {
                _followBestDistance = dist;
                _followBestAt = Time.time;
                _followMovedFrom = transform.position;
                return false;
            }
            // Far behind (over twice the catch-up distance) the stuck catch-up waits 4 s with an 8 s cooldown.
            bool farStuck = _followTarget != null && Vector3.Distance(transform.position, _followTarget.transform.position) > catchUpDistance * FollowFarStuckMultiplier;
            if (Time.time - _followBestAt < (farStuck ? FollowFarStuckSeconds : FollowStuckSeconds) || Time.time - _followCatchUpAt < (farStuck ? FollowFarStuckCooldown : FollowStuckCooldown)) return false;

            bool climbing = FiresCore.Movement.Climb.HasRun(m_character);
            if (climbing) FiresCore.Movement.Climb.Stop(m_character, "follow stuck: catching up to the owner");
            FiresCore.Logging.FiresLogger.LogDebug($"[FollowStuck] {m_character.m_name}: no progress toward {(_followTarget.GetComponent<Player>()?.GetPlayerName() ?? _followTarget.name)} for " +
                $"{Time.time - _followBestAt:0} s at {dist:0.0} m (best {_followBestDistance:0.0} m){(climbing ? ", climb ended" : "")}; catching up next to the owner");
            _followCatchUpAt = Time.time;
            _followBestDistance = float.MaxValue;
            _followBestAt = -1f;
            StopMoving();
            _companion.TeleportToOwner();
            return true;
        }

        // The follow state, why it moves or stops.
        // Debug, at most every 2 s per companion, and at once when the reason changes.
        private const float IdleEnterDistance = 4f;
        private const float IdleResumeDistance = 5f;
        private const float IdleResumeOwnerMove = 0.5f;
        private Vector3 _idleOwnerAnchor;
        private bool _idleOwnerAnchorValid;

        private string _lastFollowState;
        private float _lastFollowStateTime;
        private void FollowState(string what)
        {
            string head = what.Length > 12 ? what.Substring(0, 12) : what;
            bool changed = _lastFollowState == null || !_lastFollowState.StartsWith(head, System.StringComparison.Ordinal);
            if (!changed && Time.time - _lastFollowStateTime < 2f) return;
            _lastFollowState = what;
            _lastFollowStateTime = Time.time;
            FiresCore.Logging.FiresLogger.LogDebug($"[FollowState] {m_character?.m_name}: {what}");
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
            // Only a CHANGE of shouldSneak used to write the crouch bool,
            // so a "crouching" / FiresCompanion_Prone left set on the ZDO (fresh spawn, inherited owner, a missed stand-up) stayed set while
            // _isCompanionSneaking was already false. Not sneaking = the stance is made to match, with a Debug line.
            else if (!shouldSneak && _stance != null && (_stance.IsCrouching || _stance.IsProne))
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {m_character?.m_name}: crouch{(_stance.IsProne ? "/prone" : "")} was still set while not sneaking; standing up");
                SetCompanionCrouch(false);
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
            var followAuthority = GetMovementAuthority();
            FollowState($"moving: target {ownerPos:F1} (owner {_followTarget.name}), dist {distToOwner:0.0} m, speed {_currentFollowSpeed}, "
                + $"stop at {stopDistanceInner:0.0} m, path {(m_path != null ? m_path.Count : -1)} pt(s), "
                + $"authority {(followAuthority == null ? "none" : followAuthority.HasAuthority(AIAuthorityOwner) || followAuthority.CanAcquireAuthority(GetAIMovementSource()) ? "ok" : "held by another")}");

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
                // Closing used the OWNER's tier, so behind a jogging owner it jogged at the same base speed
                // with no catch-up boost and the gap never closed. Closing now goes one tier above the owner: Running (with its RunCatchUpBoost)
                // unless the owner is walking, sneaking or standing, then Jogging.
                _currentFollowSpeed = (matched == FollowSpeed.Running || matched == FollowSpeed.Jogging) ? FollowSpeed.Running : FollowSpeed.Jogging;
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
        // True while the follow sprint is held back for stamina (logged when it flips).
        private bool _sprintPaced;

        private void ApplyFollowSpeedToCharacter(FollowSpeed speed)
        {
            if (m_character == null) return;

            // Pace the sprint by stamina so a follower isn't left empty. StaminaManager.ShouldSprint starts a sprint at
            // 50 % and stops it under 30 %; in between the follower jogs and recovers.
            // An EMERGENCY catch-up (Sprinting: past catchUpDistance) is not held back by combat recovery, only by a nearly empty
            // bar: after a fight the recovery flag kept the follower jogging until 85 % stamina, and a jog never closes on a
            // running owner.
            // Far behind (over twice the
            // catch-up distance) stamina never paces the sprint
            bool farBehind = _followTarget != null && Vector3.Distance(transform.position, _followTarget.transform.position) > catchUpDistance * FollowFarStuckMultiplier;
            bool paced = !farBehind && _staminaManager != null && (speed == FollowSpeed.Sprinting
                ? _staminaManager.GetStaminaPercent() < _staminaManager.sprintStopThreshold
                : speed == FollowSpeed.Running && !_staminaManager.ShouldSprint());
            if (paced != _sprintPaced && (paced || speed == FollowSpeed.Running || speed == FollowSpeed.Sprinting))
            {
                _sprintPaced = paced;
                Debug.Log($"[CompanionAI] {m_character.m_name} follow sprint {(paced ? "paused: jogging to recover" : "resumed")} " +
                          $"(stamina={_staminaManager.GetStaminaPercent():P0})");
            }
            if (paced) speed = FollowSpeed.Jogging;

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
