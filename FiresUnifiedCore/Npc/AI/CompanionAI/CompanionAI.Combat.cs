using UnityEngine;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Combat state updates, fleeing, and kiting behavior.
    /// Routes all movement through UnifiedMovementAuthority.
    /// </summary>
    public partial class CompanionAI
    {
        #region Combat State

        // Kiting behavior state
        private int _kiteDirection = 0;
        private float _lastKiteDirectionChange = -100f;
        private const float KiteDirectionCommitTime = 3f;
        private const float KiteRadiusMin = 10f;
        private const float KiteRadiusMax = 18f;
        private const float GroupAwarenessRange = 15f;
        private const float EnemyDangerRadius = 6f;
        private const float MinFleeTime = 3f;
        private const float FleeThreatScanRange = 30f;

        // After exiting flee, the AI must spend at least this long in
        // a non-flee state before it is allowed to re-enter flee. Prevents
        // the Combat<->Fleeing flap when the underlying threat-analyzer
        // condition oscillates around its trigger threshold (or returns
        // a transient stale value right after a respawn). Tuned to be
        // long enough that a real "oh god retreat" still fires within a
        // second after taking damage — OnDamaged bypasses this gate.
        private const float MinFleeReentryDelay = 1.5f;

        // Rate-limit for the spammy "transitioning to Fleeing" log so a
        // misbehaving threat-analyzer doesn't flood BepInEx output at
        // 60 Hz. The actual state transition is NOT rate-limited —
        // SetState() already short-circuits when the state hasn't
        // changed; the cooldown above is what governs re-entry.
        private const float FleeLogInterval = 1.0f;
        private float _lastFleeEntryLogTime = -100f;
        private float _lastFleeExitTime = -100f;
        private bool _hasTriedKitingRangedSwap = false;
        
        // Reusable list for caching nearby enemy positions during kiting
        // Avoids iterating Character.GetAllCharacters() multiple times per frame
        private static readonly System.Collections.Generic.List<Vector3> _nearbyEnemyPositions = new System.Collections.Generic.List<Vector3>();

        // ── Stationed self-defense (defend-the-post combat for static/patrol NPCs) ────────────────────────
        // Defend range (aggro bubble), leash range (give-up distance), and combat-hold (alert buffer after the
        // last threat clears) are live-tunable via the BepInEx .cfg — see CompanionSettings.Stationed* for the
        // keys, defaults, and clamps. Read fresh each tick so config edits apply without a relog.
        private const float StationedThreatScanInterval = 0.3f;
        private float _stationedThreatLastSeen = -100f;
        private float _stationedLastScan = -100f;

        /// <summary>
        /// Combat for a stationed NPC: keep/acquire a threat in the defend bubble, pursue it, and let the
        /// CompanionCombat component fire the actual swing. Holds the line (never enters flee). Stays engaged
        /// through a buffer after the last threat clears so it doesn't snap straight back to patrol, then
        /// returns to Idle so the patrol/idle sub-behavior resumes.
        /// </summary>
        private void UpdateStationedCombat(float dt)
        {
            Vector3 post = _npcModule.StationedPosition;

            bool haveThreat = _targetCreature != null && !_targetCreature.IsDead()
                              && Vector3.Distance(_targetCreature.transform.position, post) <= CompanionSettings.StationedLeashRange;

            if (!haveThreat && Time.time - _stationedLastScan >= StationedThreatScanInterval)
            {
                _stationedLastScan = Time.time;
                var found = FindStationedThreat(post, CompanionSettings.StationedDefendRange);
                if (found != null) ForceTarget(found);   // validates enemy + sets _targetCreature + Combat state
                haveThreat = _targetCreature != null && !_targetCreature.IsDead();
            }

            if (haveThreat)
                _stationedThreatLastSeen = Time.time;

            if (Time.time - _stationedThreatLastSeen < CompanionSettings.StationedCombatHold)
            {
                if (_currentState != AIState.Combat) SetState(AIState.Combat);
                UpdateCombatMovement(dt);   // pursue/position when a target is live; no-op otherwise. No flee.
            }
            else if (_currentState == AIState.Combat)
            {
                ClearForceTarget();
                SetState(AIState.Idle);
            }
        }

        /// <summary>Nearest live HOSTILE within <paramref name="range"/> of the post that is a genuine threat,
        /// using the companion threat test anchored at the post with this NPC as the defender. Non-enemies
        /// (passive wildlife, players, tamed/allied NPCs) are skipped outright: previously the scan picked the
        /// nearest IsThreatToOwnerOrSelf and relied on ForceTarget's IsEnemy gate to reject it, so a passive
        /// standing closer than a real threat would shadow it and drop the whole scan.</summary>
        private Character FindStationedThreat(Vector3 post, float range)
        {
            Character best = null;
            float bestSq = range * range;
            var all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                var candidate = all[i];
                if (candidate == null || candidate == m_character || candidate.IsDead() || candidate.m_aiSkipTarget) continue;
                if (candidate.IsTamed() || candidate.IsPlayer()) continue;   // never aggro players or allied tames
                if (!IsEnemy(candidate)) continue;                    // hostiles only — passives must not shadow a real threat
                float sqrDistance = (candidate.transform.position - post).sqrMagnitude;
                if (sqrDistance > bestSq) continue;                   // (cull before the heavier passive probe below)
                if (IsPassiveCreature(candidate)) continue;           // honor the admin HuntList — never PROACTIVELY aggro prey
                                                              // (deer/boar/hunt-list); OnDamaged still retaliates if hit
                if (!IsThreatToOwnerOrSelf(candidate, post, m_character)) continue;
                bestSq = sqrDistance;
                best = candidate;
            }
            return best;
        }

        private void UpdateCombatState(float dt)
        {
            if (_stateController != null && _stateController.HasAbsolutePriorityCommand)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} interrupting combat - player command has absolute priority");
                
                ClearTarget();
                SetState(AIState.Following);
                return;
            }
            
            float timeInCombat = Time.time - _stateStartTime;
            
            bool isTank = _archetypeController != null && _archetypeController.IsTank;
            
            if (StateTransitionLogging && _staminaManager != null && Time.time - _lastStateLogTime >= StateLogInterval)
            {
                _lastStateLogTime = Time.time;
                bool inCritical = _staminaManager.IsInCriticalRecovery();
                bool inRecovery = _staminaManager.IsRecovering();
                
                if (inCritical != _lastLoggedCriticalRecovery || inRecovery != _lastLoggedRecovery)
                {
                    Debug.Log($"[CompanionAI] COMBAT STAMINA: {m_character?.m_name} stamina={_staminaManager.GetStaminaPercent():P0}, " +
                        $"critical={inCritical}, recovering={inRecovery}, shouldFlee={ShouldFlee()}, isTank={isTank}");
                    _lastLoggedCriticalRecovery = inCritical;
                    _lastLoggedRecovery = inRecovery;
                }
            }
            
            // PLAYER COMMAND OVERRIDES FLEE. See OnDamaged for the rationale.
            bool underPlayerCommand = _combatMovement != null && _combatMovement.HasCommandPriority;

            if (!underPlayerCommand && _staminaManager != null && _staminaManager.IsInCriticalRecovery() && !isTank)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} CRITICAL STAMINA in UpdateCombatState - forcing flee state! " +
                    $"(stamina={_staminaManager.GetStaminaPercent():P0})");

                SetState(AIState.Fleeing);
                return;
            }
            else if (_staminaManager != null && _staminaManager.IsInCriticalRecovery() && isTank)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] TANK {m_character?.m_name} staying in combat despite critical stamina - holding the line!");
            }
            
            _timeSinceAttacking += dt;
            _timeSinceTargetSeen += dt;
            
            // Break sneak stance once the companion starts attacking \u2014 attacking alerts enemies
            // anyway, so continuing to sneak would just slow the companion down.
            if (_isCompanionSneaking && m_character != null && m_character.InAttack())
            {
                _isCompanionSneaking = false;
                SetCompanionCrouch(false);
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} broke sneak stance \u2014 attacking");
            }

            bool shouldFlee = ShouldFlee();
            if (shouldFlee && underPlayerCommand)
            {
                // Player gave an order — don't enter flee even if every
                // stamina/threat heuristic says we should. The command
                // priority block above us in CompanionAI.Tick() will
                // continue executing the order.
                shouldFlee = false;
            }
            if (shouldFlee)
            {
                // Re-entry cooldown: if we just exited flee, hold the
                // line in Combat for a moment so a wobble in the threat
                // analyzer doesn't bounce us back into flee on the very
                // next tick. Real new damage events go through OnDamaged
                // which bypasses this gate.
                if (Time.time - _lastFleeExitTime < MinFleeReentryDelay)
                {
                    if (Time.time - _lastFleeEntryLogTime >= FleeLogInterval)
                    {
                        _lastFleeEntryLogTime = Time.time;
                        Debug.Log($"[CompanionAI] {m_character?.m_name} ShouldFlee=true but re-entry cooldown active ({MinFleeReentryDelay - (Time.time - _lastFleeExitTime):F1}s left) — staying in Combat");
                    }
                }
                else
                {
                    if (Time.time - _lastFleeEntryLogTime >= FleeLogInterval)
                    {
                        _lastFleeEntryLogTime = Time.time;
                        Debug.Log($"[CompanionAI] {m_character?.m_name} UpdateCombatState detected ShouldFlee=true ({GetFleeReason()}) — transitioning to Fleeing!");
                    }
                    SetState(AIState.Fleeing);
                    return;
                }
            }

            if (_targetCreature == null || _targetCreature.IsDead())
            {
                if (FindNewTarget())
                {
                    return;
                }

                if (timeInCombat < MinCombatStateTime)
                {
                    return;
                }

                if (_shouldFollow && _followTarget != null)
                {
                    SetState(AIState.Returning);
                }
                else
                {
                    SetState(AIState.Idle);
                }
                return;
            }

            if (_shouldFollow && _followTarget != null)
            {
                float distToOwner = Vector3.Distance(transform.position, _followTarget.transform.position);
                if (distToOwner > combatLeashDistance)
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] Too far from owner ({distToOwner:F0}m), returning");
                    SetState(AIState.Returning);
                    return;
                }
            }

            if (_timeSinceAttacking > giveUpTime && _beenAtLastTargetPos)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] Giving up on unreachable target after {_timeSinceAttacking:F0}s");
                ClearTarget();
                SetState(_shouldFollow ? AIState.Returning : AIState.Idle);
                return;
            }

            UpdateCombatMovement(dt);
        }

        private void UpdateReturningState(float dt)
        {
            float timeInReturning = Time.time - _stateStartTime;
            
            if (_targetCreature != null && !_targetCreature.IsDead())
            {
                float distToTarget = Vector3.Distance(transform.position, _targetCreature.transform.position);
                // ONLY re-engage if the enemy is right on top of us — actively in
                // our face. Do NOT re-engage just because MinReturningStateTime
                // has elapsed; that caused a yo-yo where the companion kept leaving
                // the player to chase a running enemy, ratcheting further away each
                // cycle until it was 100 m out.
                bool isImmediateThreat = distToTarget < attackRange * 1.5f;
                if (isImmediateThreat)
                {
                    SetState(AIState.Combat);
                    return;
                }
            }

            if (_followTarget == null)
            {
                SetState(AIState.Idle);
                return;
            }

            float distToOwner = Vector3.Distance(transform.position, _followTarget.transform.position);
            
            if (distToOwner <= stopDistanceOuter)
            {
                _currentFollowSpeed = FollowSpeed.Stopped;
                SetState(AIState.Following);
                return;
            }

            // Use vanilla pathfinding with authority coordination
            MoveToWithAuthority(_followTarget.transform.position, true, stopDistanceOuter);
        }

        private void UpdateFleeingState(float dt)
        {
            // PLAYER COMMAND OVERRIDES FLEE.
            // If the player has issued a move/attack command while we were
            // already fleeing, abort flee immediately and let the command
            // priority handler in Tick() take over on the next frame.
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
            {
                _kiteDirection = 0;
                _hasTriedKitingRangedSwap = false;
                _lastFleeExitTime = Time.time;
                Debug.Log($"[CompanionAI] EXITING FLEE: {m_character?.m_name} player command issued — obeying order, returning to normal");
                SetState(_shouldFollow ? AIState.Following : AIState.Idle);
                return;
            }

            float healthPercent = m_character.GetHealthPercentage();
            float staminaPercent = _staminaManager?.GetStaminaPercent() ?? 1f;
            bool inCriticalRecovery = _staminaManager?.IsInCriticalRecovery() ?? false;
            bool inRecovery = _staminaManager?.IsRecovering() ?? false;
            
            if (StateTransitionLogging && Time.time - _lastStateLogTime >= StateLogInterval)
            {
                _lastStateLogTime = Time.time;
                float fleeTimeRemaining = fleeTime - (Time.time - _fleeStartTime);
                Debug.Log($"[CompanionAI] FLEEING STATUS: {m_character?.m_name} health={healthPercent:P0}, stamina={staminaPercent:P0}, " +
                    $"critical={inCriticalRecovery}, recovering={inRecovery}, fleeTimeRemaining={fleeTimeRemaining:F1}s");
            }
            
            // Range-limited scan — only check characters within 30m, not the entire world
            bool hasActiveThreat = false;
            Character.GetCharactersInRange(transform.position, FleeThreatScanRange, _tempCharacterList);
            foreach (var character in _tempCharacterList)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!IsEnemy(character)) continue;
                
                hasActiveThreat = true;
                break;
            }
            _tempCharacterList.Clear();
            
            if (!hasActiveThreat && !inCriticalRecovery && staminaPercent >= 0.70f)
            {
                float timeInFlee = Time.time - _fleeStartTime;
                if (timeInFlee >= MinFleeTime)
                {
                    _kiteDirection = 0;
                    _hasTriedKitingRangedSwap = false;
                    _lastFleeExitTime = Time.time;
                    Debug.Log($"[CompanionAI] EXITING FLEE: {m_character?.m_name} no active threats nearby (stamina={staminaPercent:P0}) - returning to normal");
                    SetState(_shouldFollow ? AIState.Following : AIState.Idle);
                    return;
                }
            }
            
            if (healthPercent >= healReturnPercent)
            {
                float timeInFlee = Time.time - _fleeStartTime;
                if (timeInFlee < MinFleeTime)
                {
                    // Continue fleeing
                }
                else if (_staminaManager == null || !inCriticalRecovery)
                {
                    if (_staminaManager == null || !inRecovery ||
                    staminaPercent >= 0.85f)
                    {
                        _kiteDirection = 0;
                        _hasTriedKitingRangedSwap = false;
                        _lastFleeExitTime = Time.time;
                        Debug.Log($"[CompanionAI] EXITING FLEE: {m_character?.m_name} health={healthPercent:P0}, stamina={staminaPercent:P0} - returning to normal");
                        SetState(_shouldFollow ? AIState.Following : AIState.Idle);
                        return;
                    }
                }
            }

            if (Time.time - _fleeStartTime > fleeTime)
            {
                if (_staminaManager != null && (inCriticalRecovery || (inRecovery && staminaPercent < 0.7f)))
                {
                    _fleeStartTime = Time.time;
                    Debug.Log($"[CompanionAI] {m_character?.m_name} flee time expired but stamina still low ({staminaPercent:P0}) - EXTENDING flee timer");
                }
                else
                {
                    _kiteDirection = 0;
                    _hasTriedKitingRangedSwap = false;
                    _lastFleeExitTime = Time.time;
                    Debug.Log($"[CompanionAI] EXITING FLEE (timeout): {m_character?.m_name} stamina={staminaPercent:P0} - returning to normal");
                    SetState(_shouldFollow ? AIState.Following : AIState.Idle);
                    return;
                }
            }

            ExecuteKitingBehavior(dt);
        }
        
        private void ExecuteKitingBehavior(float dt)
        {
            TrySwapToRangedForKiting();
            
            Vector3 ownerPos = _followTarget != null ? _followTarget.transform.position : transform.position;
            Vector3 myPos = transform.position;
            
            Vector3 combinedThreatDirection = Vector3.zero;
            int enemyCount = 0;
            float closestEnemyDist = float.MaxValue;
            Character closestEnemy = null;
            
            // Range-limited single pass: compute threat direction, find closest enemy, AND cache
            // positions for the kite target danger check below.
            // Uses GetCharactersInRange instead of scanning every entity in the world.
            _nearbyEnemyPositions.Clear();
            Character.GetCharactersInRange(myPos, GroupAwarenessRange, _tempCharacterList);
            
            foreach (var character in _tempCharacterList)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!IsEnemy(character)) continue;
                
                float dist = Vector3.Distance(myPos, character.transform.position);
                
                // Cache position for danger check later
                _nearbyEnemyPositions.Add(character.transform.position);
                
                float weight = 1f / Mathf.Max(1f, (dist * dist) / 9f);
                Vector3 awayFromEnemy = (myPos - character.transform.position).normalized;
                combinedThreatDirection += awayFromEnemy * weight;
                enemyCount++;
                
                if (dist < closestEnemyDist)
                {
                    closestEnemyDist = dist;
                    closestEnemy = character;
                }
            }
            _tempCharacterList.Clear();
            
            if (enemyCount == 0 && _targetCreature != null && !_targetCreature.IsDead())
            {
                closestEnemy = _targetCreature;
                closestEnemyDist = Vector3.Distance(myPos, closestEnemy.transform.position);
                combinedThreatDirection = (myPos - closestEnemy.transform.position).normalized;
                enemyCount = 1;
            }
            
            if (combinedThreatDirection.sqrMagnitude > 0.01f)
            {
                combinedThreatDirection = combinedThreatDirection.normalized;
            }
            else
            {
                combinedThreatDirection = (ownerPos - myPos).normalized;
            }
            
            if (_kiteDirection == 0 || Time.time - _lastKiteDirectionChange > KiteDirectionCommitTime * 3f)
            {
                Vector3 initOwnerToMe = (myPos - ownerPos).normalized;
                if (initOwnerToMe.sqrMagnitude < 0.1f) initOwnerToMe = Vector3.forward;
                
                float cross = Vector3.Cross(initOwnerToMe, combinedThreatDirection).y;
                _kiteDirection = cross > 0 ? 1 : -1;
                
                if (UnityEngine.Random.value < 0.2f)
                {
                    _kiteDirection *= -1;
                }
                
                _lastKiteDirectionChange = Time.time;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} initialized kite direction: {(_kiteDirection > 0 ? "counter-clockwise" : "clockwise")} (avoiding {enemyCount} enemies)");
            }
            
            float distToOwner = Vector3.Distance(myPos, ownerPos);
            Vector3 ownerToMe = (myPos - ownerPos);
            ownerToMe.y = 0;
            
            if (ownerToMe.sqrMagnitude < 0.1f)
            {
                ownerToMe = combinedThreatDirection;
            }
            ownerToMe = ownerToMe.normalized;
            
            float targetRadius = KiteRadiusMin;
            
            if (closestEnemyDist < EnemyDangerRadius)
            {
                targetRadius = KiteRadiusMax;
            }
            else if (closestEnemyDist < EnemyDangerRadius * 1.5f)
            {
                targetRadius = Mathf.Lerp(KiteRadiusMin, KiteRadiusMax, 0.7f);
            }
            else if (enemyCount >= 3)
            {
                targetRadius = Mathf.Lerp(KiteRadiusMin, KiteRadiusMax, 0.5f);
            }
            
            float rotationSpeed = closestEnemyDist < EnemyDangerRadius ? 60f : 45f;
            Quaternion rotation = Quaternion.Euler(0, _kiteDirection * rotationSpeed * dt * 2f, 0);
            Vector3 newDirection = rotation * ownerToMe;
            
            Vector3 kiteTarget = ownerPos + newDirection * targetRadius;
            
            // Use cached enemy positions from the single pass above instead of iterating
            // Character.GetAllCharacters() again
            bool wouldEnterDanger = false;
            for (int i = 0; i < _nearbyEnemyPositions.Count; i++)
            {
                float distToKiteTarget = Vector3.Distance(kiteTarget, _nearbyEnemyPositions[i]);
                if (distToKiteTarget < EnemyDangerRadius)
                {
                    wouldEnterDanger = true;
                    break;
                }
            }
            
            if (wouldEnterDanger)
            {
                _kiteDirection *= -1;
                _lastKiteDirectionChange = Time.time;
                
                kiteTarget = myPos + combinedThreatDirection * 8f;
                
                float distFromOwnerToKiteTarget = Vector3.Distance(ownerPos, kiteTarget);
                if (distFromOwnerToKiteTarget > KiteRadiusMax * 1.5f)
                {
                    Vector3 toOwner = (ownerPos - kiteTarget).normalized;
                    kiteTarget += toOwner * (distFromOwnerToKiteTarget - KiteRadiusMax);
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} kite path blocked by enemies - escaping directly!");
            }
            
            if (_attackRecognition != null)
            {
                var threat = _attackRecognition.GetCurrentThreat();
                
                bool isEmergency = m_character.GetHealthPercentage() < 0.3f || 
                    (_staminaManager?.IsInCriticalRecovery() ?? false);
                
                float predictionWindow = isEmergency ? 1.5f : 0.8f;
                
                if (threat.Level >= EnemyAttackRecognition.ThreatLevel.Medium && 
                    threat.TimeToImpact < predictionWindow)
                {
                    Vector3 moveDir = (kiteTarget - myPos).normalized;
                    float dotWithThreat = Vector3.Dot(moveDir, threat.ThreatDirection);
                    
                    if (dotWithThreat > 0.3f)
                    {
                        _kiteDirection *= -1;
                        _lastKiteDirectionChange = Time.time;
                        
                        rotation = Quaternion.Euler(0, _kiteDirection * rotationSpeed * dt * 3f, 0);
                        newDirection = rotation * ownerToMe;
                        kiteTarget = ownerPos + newDirection * targetRadius;
                        
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAI] {m_character?.m_name} reversed kite direction to avoid attack!");
                    }
                }
            }
            
            if (_terrainAwareness != null)
            {
                Vector3 moveDir = (kiteTarget - myPos).normalized;
                if (!_terrainAwareness.IsDirectionSafe(moveDir))
                {
                    Vector3 safeDir = _terrainAwareness.GetSafeMovementDirection(moveDir);
                    kiteTarget = myPos + safeDir * 5f;
                }
            }
            
            float finalDistToOwner = Vector3.Distance(kiteTarget, ownerPos);
            if (finalDistToOwner > KiteRadiusMax * 1.5f)
            {
                Vector3 toOwner = (ownerPos - kiteTarget).normalized;
                kiteTarget += toOwner * (finalDistToOwner - KiteRadiusMax);
            }
            else if (finalDistToOwner < KiteRadiusMin * 0.5f)
            {
                Vector3 awayFromOwner = (kiteTarget - ownerPos).normalized;
                if (awayFromOwner.sqrMagnitude < 0.1f) awayFromOwner = combinedThreatDirection;
                kiteTarget = ownerPos + awayFromOwner * KiteRadiusMin;
            }
            
            if (_combatMovement != null)
            {
                _combatMovement.RequestFleeMovement(kiteTarget);
            }
            else
            {
                // Fallback: Route through authority
                Vector3 kiteMoveDir = (kiteTarget - myPos).normalized;
                kiteMoveDir.y = 0;
                
                if (kiteMoveDir.sqrMagnitude > 0.01f && m_character != null)
                {
                    SetMoveDirThroughAuthority(kiteMoveDir, run: true);
                }
            }
            
            if (closestEnemy != null && !closestEnemy.IsDead())
            {
                FaceThroughAuthority(closestEnemy.transform.position);
            }
        }

        /// <summary>
        /// Faces a world position through the FacingAuthority at Combat priority (owner "CompanionAI"), so
        /// enemy-facing wins over CombatMovement's move-direction facing (Following) — you face your target
        /// in a fight, even while standing still to attack. Falls back to vanilla LookAt with no authority.
        /// </summary>
        private void FaceThroughAuthority(Vector3 worldPos)
        {
            var facing = _companion != null ? _companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                // Owner-gated: if a higher facer (e.g. weapon aim at Animation) holds facing, the acquire
                // is denied and we PARK — never fall back to a raw LookAt that would fight the holder.
                if (facing.TryAcquireFacing(UnifiedMovementAuthority.MovementSource.Combat, "CompanionAI", 0.4f))
                    facing.SetLookTarget("CompanionAI", worldPos);
                return;
            }
            LookAt(worldPos);
        }

        private Character FindNearestEnemy()
        {
            Character nearest = null;
            float nearestDist = float.MaxValue;
            
            // Range-limited scan — only enemies within aggro range matter
            Character.GetCharactersInRange(transform.position, aggroRange, _tempCharacterList);
            
            foreach (var character in _tempCharacterList)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!IsEnemy(character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = character;
                }
            }
            
            _tempCharacterList.Clear();
            
            return nearest;
        }
        
        private void TrySwapToRangedForKiting()
        {
            if (_hasTriedKitingRangedSwap) return;
            _hasTriedKitingRangedSwap = true;
            
            if (_staminaManager != null)
            {
                if (_staminaManager.IsInCriticalRecovery() || _staminaManager.ShouldTreatAsLowHealth())
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} NOT swapping to ranged - critical stamina recovery active!");
                    return;
                }
            }
            
            if (_weaponSwapManager == null) return;
            
            if (_combatRef != null && _combatRef.IsRangedWeapon()) return;
            
            _weaponSwapManager.ScanAvailableWeapons();
            var (melee, ranged) = _weaponSwapManager.GetAvailableWeapons();
            
            if (ranged != null)
            {
                if (_weaponSwapManager.ForceSwapToRanged())
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} swapped to ranged weapon for kiting!");
                }
            }
        }

        private void UpdateCombatMovement(float dt)
        {
            if (_targetCreature == null) return;

            UpdateWeaponType();

            Vector3 targetPos = _targetCreature.transform.position;
            float distToTarget = Vector3.Distance(transform.position, targetPos);

            bool canSee = CanSeeTarget(_targetCreature);
            bool canHear = CanHearTarget(_targetCreature);

            if (canSee || canHear)
            {
                _timeSinceTargetSeen = 0f;
                _lastKnownTargetPos = targetPos;
                _beenAtLastTargetPos = false;
            }

            if (_isRangedWeapon)
            {
                FaceThroughAuthority(targetPos);
                
                if (m_character.InAttack())
                {
                    _timeSinceAttacking = 0f;
                }
                return;
            }

            UpdateMeleeCombatMovement(dt, targetPos, distToTarget, canSee);
        }
        
        private void UpdateWeaponType()
        {
            if (_combatRef == null)
            {
                _combatRef = GetComponent<CompanionCombat>();
            }
            
            if (_combatRef != null)
            {
                _isRangedWeapon = _combatRef.IsRangedWeapon();
            }
        }
        
        private void UpdateMeleeCombatMovement(float dt, Vector3 targetPos, float distToTarget, bool canSee)
        {
            if (distToTarget <= attackRange && canSee)
            {
                // STOP MOVEMENT: the companion must plant its feet and swing,
                // not keep running into/past the enemy. Without this the previous
                // MoveToWithAuthority call's momentum carries the companion through
                // the enemy, causing them to overshoot, spin to face, overshoot
                // again, and never satisfy ShouldAttack's angle gate.
                StopMovementThroughAuthority();

                FaceThroughAuthority(targetPos);

                if (m_character.InAttack())
                {
                    _timeSinceAttacking = 0f;
                }

                // MICRO-REPOSITION: if the companion has somehow overshot and is
                // now nose-to-chest with the enemy (< 0.6 * attackRange), or the
                // facing angle is still too wide for ShouldAttack to fire, step
                // back just enough to get to the ideal striking distance so the
                // attack swing can connect cleanly.
                Vector3 dirToTarget = (targetPos - transform.position);
                dirToTarget.y = 0f;
                float facing = dirToTarget.sqrMagnitude > 0.01f
                    ? Vector3.Angle(transform.forward, dirToTarget.normalized)
                    : 0f;

                bool tooClose = distToTarget < attackRange * 0.55f;
                bool badAngle = facing > 90f;

                if (tooClose || badAngle)
                {
                    // Step back / re-position to the sweet-spot in front of the target
                    Vector3 idealPos = targetPos - dirToTarget.normalized * (attackRange * 0.8f);
                    MoveToWithAuthority(idealPos, false, 0.4f);
                }
            }
            else
            {
                Vector3 moveTarget = canSee ? targetPos : _lastKnownTargetPos;

                if (!canSee && Vector3.Distance(transform.position, _lastKnownTargetPos) < PositionReachedThreshold)
                {
                    _beenAtLastTargetPos = true;
                }

                // Use vanilla pathfinding with authority coordination
                MoveToWithAuthority(moveTarget, true, attackRange);
            }
        }

        private bool ShouldFlee()
        {
            bool isTank = _archetypeController != null && _archetypeController.IsTank;

            if (_staminaManager != null && !isTank)
            {
                bool inCritical = _staminaManager.IsInCriticalRecovery();
                bool shouldTreatAsLow = _staminaManager.ShouldTreatAsLowHealth();
                float staminaPct = _staminaManager.GetStaminaPercent();

                if (inCritical)
                {
                    _lastFleeReason = $"critical-stamina-recovery (stamina={staminaPct:P0})";
                    return true;
                }

                if (shouldTreatAsLow)
                {
                    _lastFleeReason = $"stamina-emergency (stamina={staminaPct:P0})";
                    return true;
                }
            }
            else if (isTank && _staminaManager != null)
            {
                float staminaPct = _staminaManager.GetStaminaPercent();
                if (staminaPct < 0.3f && VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] TANK {m_character?.m_name} staying in combat despite low stamina ({staminaPct:P0}) - holding the line!");
                }
            }

            if (fleeHealthPercent <= 0)
                return false;

            float healthPercent = m_character.GetHealthPercentage();

            if (healthPercent <= fleeHealthPercent)
            {
                // Don't flee from low HP unless there is an *actual* hostile
                // threat. Otherwise we get a Combat\u2194Fleeing flap when the
                // companion locked onto a passive (deer/boar) it shouldn't be
                // targeting in the first place, or its own HP value is stale
                // right after a respawn. A real threat is either:
                //   - we were directly damaged in the recent past, or
                //   - the threat analyzer reports a dangerous enemy nearby, or
                //   - we have a current target that is not a passive creature.
                bool wasRecentlyDamaged = Time.time - _lastDirectlyDamagedTime < DirectDamageAlertDuration;
                bool analyzerSeesThreat = false;
                if (_threatAnalyzer != null)
                {
                    var currentSituation = _threatAnalyzer.GetCurrentSituation();
                    analyzerSeesThreat = currentSituation.DangerousEnemies > 0 || currentSituation.IsCompanionInDanger;
                }
                bool hostileTarget = _targetCreature != null
                    && !_targetCreature.IsDead()
                    && !IsPassiveCreature(_targetCreature);

                if (wasRecentlyDamaged || analyzerSeesThreat || hostileTarget)
                {
                    _lastFleeReason = $"health-below-flee-threshold (hp={healthPercent:P0} \u2264 {fleeHealthPercent:P0})";
                    return true;
                }
                // Low HP but no real threat \u2014 stay calm, no flee.
            }

            if (_threatAnalyzer != null)
            {
                var situation = _threatAnalyzer.GetCurrentSituation();

                if (situation.RecommendedStance == ThreatAnalyzer.CombatStance.Retreat)
                {
                    if (situation.IsCompanionInDanger || situation.OverallThreatLevel > 70f)
                    {
                        _lastFleeReason = $"threat-analyzer-retreat (threatLvl={situation.OverallThreatLevel:F0}, danger={situation.IsCompanionInDanger}, enemies={situation.TotalEnemies}/{situation.DangerousEnemies}d, hp={healthPercent:P0})";
                        return true;
                    }
                }
                
                if (situation.PrimaryThreat.IsBoss && healthPercent < fleeHealthPercent * 1.5f)
                {
                    _lastFleeReason = $"boss-low-hp (boss={situation.PrimaryThreat.PrefabName}, hp={healthPercent:P0})";
                    return true;
                }

                if (situation.DangerousEnemies >= 2 && healthPercent < fleeHealthPercent * 1.3f)
                {
                    _lastFleeReason = $"multi-dangerous-low-hp (dangerous={situation.DangerousEnemies}, hp={healthPercent:P0})";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the human-readable reason recorded the last time
        /// <see cref="ShouldFlee"/> returned true. Used by the combat
        /// state log so we can see WHICH branch is firing instead of
        /// just the bare "ShouldFlee=true" boolean.
        /// </summary>
        private string GetFleeReason()
        {
            return string.IsNullOrEmpty(_lastFleeReason) ? "unknown" : _lastFleeReason;
        }

        private string _lastFleeReason;

        #endregion
    }
}
