using UnityEngine;
using System;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Movement;
using FiresCore.Npc.AI;

namespace FiresCore.Npc
{
    /// <summary>
    /// Combat state management, commitment system, and emergency actions.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Combat State & Transitions

        // Track when we last logged critical stamina retreat (to avoid spam)
        private float _lastCriticalStaminaLogTime = -10f;
        private const float CRITICAL_STAMINA_LOG_INTERVAL = 5f;

        private void UpdateCombatState()
        {
            _fleeHandler?.ClearFleeRequest();
            
            var target = _companionAI?.GetTargetCreature();
            bool wasInCombat = _isInCombat;
            bool hasValidTarget = target != null && !target.IsDead();
            
            if (_companionAI != null && _companionAI.CurrentState == CompanionAI.AIState.Fleeing)
                hasValidTarget = false;
            
            if (hasValidTarget)
            {
                float distToTarget = Vector3.Distance(transform.position, target.transform.position);
                float combatActivationDistance = (_combat?.IsRangedWeapon() ?? false) ? 25f : 20f;
                
                if (distToTarget > combatActivationDistance)
                    hasValidTarget = false;
            }
            
            if (hasValidTarget && _combatHandler != null)
            {
                var ownerRec = _combatHandler.EvaluateOwnerDistance();
                
                if (ownerRec == CombatMovementHandler.OwnerDistanceRecommendation.AbandonCombat)
                {
                    hasValidTarget = false;
                    
                    if (VerboseLogging)
                        Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} abandoning combat - too far from owner ({_combatHandler.GetDistanceToOwner():F1}m)");
                }
                else if (target != null)
                {
                    var owner = _companion?.GetOwner();
                    if (owner != null)
                    {
                        float targetToOwner = Vector3.Distance(target.transform.position, owner.transform.position);
                        if (targetToOwner > maxOwnerCombatDistance)
                        {
                            hasValidTarget = false;
                            
                            if (VerboseLogging)
                                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} ignoring target - target too far from owner ({targetToOwner:F1}m)");
                        }
                    }
                }
            }
            
            if (!hasValidTarget && wasInCombat)
                hasValidTarget = CheckForNearbyThreats();
            
            _isInCombat = hasValidTarget;
            _currentTarget = hasValidTarget ? target : null;
            
            HandleStateTransitions(wasInCombat);
        }
        
        private void ExecuteCombatState()
        {
            _isInCombatCooldown = false;
            UpdateCommittedCombat();
        }
        
        private void ExecuteCombatCooldownState()
        {
            ApplyMovementMode();
            if (_currentIntent != MovementIntent.Idle)
            {
                SetIntent(MovementIntent.Idle);
                SetMoveDirSafe(Vector3.zero);
            }
        }

        private bool CheckForNearbyThreats()
        {
            if (_character == null) return false;
            
            var owner = _companion?.GetOwner();
            Vector3 ownerPos = owner != null ? owner.transform.position : transform.position;

            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;

                float distToUs = Vector3.Distance(transform.position, character.transform.position);
                if (distToUs <= combatEndCheckRange)
                {
                    if (owner != null)
                    {
                        float threatToOwner = Vector3.Distance(character.transform.position, ownerPos);
                        if (threatToOwner > maxOwnerCombatDistance)
                        {
                            continue;
                        }
                    }
                    
                    return true;
                }
            }

            return false;
        }

        private void TryClearAlertedState()
        {
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} cleared alerted state");
            
            _stateTransitionHandler?.TryPlayVictoryEmote(this);
        }

        public void OnEnemyKilled(Character enemy)
        {
            _stateTransitionHandler?.OnEnemyKilled();
            _lastEnemyKillTime = Time.time;

            if (!CheckForNearbyThreats())
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} killed last enemy");

                _isInCombat = false;
                _currentTarget = null;
                _committedTarget = null;
                _hasActiveCommitment = false;

                BeginStateTransition();
                _isInCombatCooldown = true;
            }
        }

        private void HandleStateTransitions(bool wasInCombat)
        {
            if (_isInCombat && !wasInCombat)
            {
                _idleBehavior?.OnCombatStarted();
                _stateTransitionHandler?.OnEnterCombat();
                _hasActiveCommitment = false;
                _isInCombatCooldown = false;
                _movementModeSet = false;
                _moveDirSet = false;
                _hasRangedMovementRequest = false;

                if (_previousIntent == MovementIntent.Idle || _lastMoveDirection.sqrMagnitude < 0.01f)
                {
                    _isInTransition = false;
                    _lastStateChangeTime = Time.time;
                    ForceMonsterAIPursuit();
                }
                else
                {
                    BeginStateTransition();
                }
            }
            else if (!_isInCombat && wasInCombat)
            {
                _idleBehavior?.OnCombatEnded();
                _stateTransitionHandler?.OnExitCombat();
                _isInCombatCooldown = true;
                _lastEnemyKillTime = Time.time;
                _movementModeSet = false;
                _moveDirSet = false;
                _hasRangedMovementRequest = false;

                BeginStateTransition();
            }

            _wasInCombat = _isInCombat;

            if (_isInTransition && (_stateTransitionHandler?.ShouldEndTransition() ?? 
                Time.time > _transitionStartTime + stateTransitionGracePeriod))
            {
                EndStateTransition();
            }

            if (_isInCombatCooldown && !_isInCombat && 
                (_stateTransitionHandler?.CheckCombatCooldownEnded() ?? 
                 Time.time > _lastEnemyKillTime + combatEndGracePeriod))
            {
                TryClearAlertedState();
                _isInCombatCooldown = false;
            }
            
            if (_isInCombat && ShouldRelaxDueToPlayerIdle())
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} relaxing - player idle");
                
                _isInCombat = false;
                _currentTarget = null;
                _committedTarget = null;
                _hasActiveCommitment = false;
                _isInCombatCooldown = false;
                
                if (_companionAI != null)
                {
                    try
                    {
                        var setAlertedMethod = typeof(BaseAI).GetMethod("SetAlerted",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        setAlertedMethod?.Invoke(_companionAI, new object[] { false });
                    }
                    catch { }
                }
                
                _idleBehavior?.OnCombatEnded();
            }
        }

        private void ForceMonsterAIPursuit()
        {
            if (_companionAI != null && _currentTarget != null)
            {
                _companionAI.ForceTarget(_currentTarget);
            }
        }

        private void BeginStateTransition()
        {
            _isInTransition = true;
            _transitionStartTime = Time.time;
            _lastMoveDirection = _currentMoveDirection;
            _previousIntent = _currentIntent;
            _moveDirSet = false;
            _stateTransitionHandler?.BeginTransition(_currentMoveDirection);
        }

        private void EndStateTransition()
        {
            _isInTransition = false;
            _lastStateChangeTime = Time.time;
            _moveDirSet = false;
            _stateTransitionHandler?.EndTransition();
        }

        private void UpdateTransitionMovement()
        {
            if (_lastMoveDirection.sqrMagnitude > 0.1f && _character != null)
            {
                float transitionProgress = (Time.time - _transitionStartTime) / stateTransitionGracePeriod;

                if (_currentIntent == MovementIntent.Idle || !_isInCombat)
                {
                    Vector3 blendedDir = Vector3.Lerp(_lastMoveDirection, Vector3.zero, transitionProgress);
                    _currentMoveDirection = blendedDir;

                    if (blendedDir.sqrMagnitude > 0.1f)
                    {
                        SetMoveDirSafe(blendedDir);
                        SetWalkRunSafe(true, false);
                    }
                    else
                    {
                        StopMovementGradually();
                    }
                }
                else
                {
                    SetMoveDirSafe(_lastMoveDirection);
                    SetWalkRunSafe(false, true);
                }
            }
        }

        private void OnDamageTaken(float damage, Character attacker)
        {
            _lastDamageTime = Time.time;
            _staminaManager?.ForceRecovery();

            if (_isInCombat)
            {
                if (attacker != null && attacker != _committedTarget && !attacker.IsDead())
                {
                    float distToAttacker = Vector3.Distance(transform.position, attacker.transform.position);
                    float distToTarget = _committedTarget != null ?
                        Vector3.Distance(transform.position, _committedTarget.transform.position) : float.MaxValue;

                    if (distToAttacker < distToTarget * 0.7f || _committedTarget == null || _committedTarget.IsDead())
                    {
                        ForceTargetSwitch(attacker);
                    }
                }

                _hasActiveCommitment = false;
            }
        }

        #endregion

        #region Committed Combat System

        private void UpdateCommittedCombat()
        {
            if (_companionAI != null && _companionAI.CurrentState == CompanionAI.AIState.Fleeing)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[CompanionCombatMovement] {_companion?.companionName} UpdateCommittedCombat reached during Fleeing - this should not happen!");
                
                if (_combat != null)
                {
                    _combat.RequestStopBlocking();
                }
                _staminaManager?.OnBlockEnded();
                _hasRangedMovementRequest = false;
                return;
            }
            
            // GROUP COMBAT POSITIONING: Sync flanking angle from CombatRoleDirector to CombatMovementHandler
            // This makes melee companions approach targets from different angles instead of stacking
            if (_combatHandler != null && _companion != null)
            {
                var coordinator = GroupCombatCoordinator.Instance;
                if (coordinator != null)
                {
                    var directive = coordinator.GetDirective(_companion);
                    if (directive != null && directive.IsValid && 
                        directive.Directive == CombatRoleDirector.CombatDirective.FlankTarget &&
                        directive.FlankAngle != 0f)
                    {
                        _combatHandler.SetFlankAngle(directive.FlankAngle);
                    }
                    else
                    {
                        _combatHandler.ClearFlankAngle();
                    }
                }
            }
            
            if (_combatHandler != null)
            {
                var ownerRec = _combatHandler.EvaluateOwnerDistance();
                
                if (ownerRec == CombatMovementHandler.OwnerDistanceRecommendation.AbandonCombat ||
                    ownerRec == CombatMovementHandler.OwnerDistanceRecommendation.ReturnToOwner)
                {
                    _hasRangedMovementRequest = false;
                    
                    Vector3 toOwner = _combatHandler.GetDirectionToOwner();
                    
                    if (toOwner.sqrMagnitude > 0.01f)
                    {
                        SetIntent(MovementIntent.FollowingFar);
                        _committedMoveDirection = toOwner;
                        _targetMoveDirection = toOwner;
                        _hasActiveCommitment = false;
                        
                        SetMoveDirSafe(toOwner);
                        SetWalkRunSafe(false, true);
                        
                        if (_combat != null)
                        {
                            _combat.RequestStopBlocking();
                        }
                        _staminaManager?.OnBlockEnded();
                        
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} returning to owner - distance: {_combatHandler.GetDistanceToOwner():F1}m");
                    }
                    
                    _isInCombat = false;
                    _currentTarget = null;
                    _committedTarget = null;
                    return;
                }
            }
            
            if (_staminaManager != null && _staminaManager.IsInCriticalRecovery())
            {
                _hasRangedMovementRequest = false;
                
                var owner = _companion?.GetOwner();
                if (owner != null)
                {
                    Vector3 retreatDir = (owner.transform.position - transform.position).normalized;
                    retreatDir.y = 0;
                    
                    if (_terrainAwareness != null)
                    {
                        retreatDir = _terrainAwareness.GetSafeMovementDirection(retreatDir);
                    }
                    
                    SetIntent(MovementIntent.CombatRetreat);
                    _committedMoveDirection = retreatDir;
                    _targetMoveDirection = retreatDir;
                    _hasActiveCommitment = true;
                    _commitmentStartTime = Time.time;
                    _commitmentDuration = 2f;

                    SetMoveDirSafe(retreatDir);
                    SetWalkRunSafe(false, true);
                    FaceMovementDirection(retreatDir);

                    if (_combat != null)
                    {
                        _combat.RequestStopBlocking();
                    }
                    _staminaManager.OnBlockEnded();
                    
                    if ((VerboseLogging || StaminaManager.CombatFlowLogging) && 
                        Time.time - _lastCriticalStaminaLogTime >= CRITICAL_STAMINA_LOG_INTERVAL)
                    {
                        _lastCriticalStaminaLogTime = Time.time;
                        Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} CRITICAL STAMINA - retreating to owner! No combat actions allowed.");
                    }
                }
                else
                {
                    var retreatFromTarget = _committedTarget ?? _currentTarget;
                    if (retreatFromTarget != null)
                    {
                        Vector3 retreatDir = (transform.position - retreatFromTarget.transform.position).normalized;
                        retreatDir.y = 0;
                        
                        SetIntent(MovementIntent.CombatRetreat);
                        _committedMoveDirection = retreatDir;
                        _targetMoveDirection = retreatDir;
                        SetMoveDirSafe(retreatDir);
                        SetWalkRunSafe(false, true);
                        FaceMovementDirection(retreatDir);
                    }
                }
                
                return;
            }
            
            var target = _committedTarget ?? _currentTarget;
    
            if (target == null || target.IsDead())
            {
                _committedTarget = null;
                _currentTarget = null;
                _hasActiveCommitment = false;
                return;
            }
         
            if (!_movementModeSet)
            {
                SetWalkRunSafe(false, true);
            }
            
            if (Time.time - _lastReassessTime > 0.5f)
            {
                _lastReassessTime = Time.time;
                
                if (CheckForEmergencyAction())
                {
                    return;
                }
            }
            
            _committedTarget = target;
        }

        private bool CheckForEmergencyAction()
        {
            if (_staminaManager != null && _staminaManager.IsInCriticalRecovery())
            {
                return false;
            }
            
            if (_currentIntent == MovementIntent.CombatApproach || 
                _currentIntent == MovementIntent.CombatChase ||
                _currentIntent == MovementIntent.CombatIntercept)
            {
                return false;
            }
 
            if (_attackRecognition == null) return false;

            var threat = _attackRecognition.GetCurrentThreat();
      
            if (threat.Level < EnemyAttackRecognition.ThreatLevel.High)
            {
                return false;
            }

            if (_attackRecognition.ShouldDodgeNow())
            {
                if (_staminaManager == null || _staminaManager.CanDodge())
                {
                    ExecuteEmergencyDodge(threat.ThreatDirection);
                    return true;
                }
                else if (_staminaManager.CanBlock())
                {
                    ExecuteEmergencyBlock();
                    return true;
                }
            }

            if (_attackRecognition.ShouldBlockNow() && !_attackRecognition.ShouldDodgeNow())
            {
                if (_staminaManager == null || _staminaManager.CanBlock())
                {
                    if (_combat != null && _combat.HasShieldEquipped())
                    {
                        ExecuteEmergencyBlock();
                        return true;
                    }
                }
            }

            return false;
        }

        private void ExecuteEmergencyDodge(Vector3 threatDirection)
        {
            Vector3 dodgeDir = _attackRecognition?.GetDodgeDirection() ?? transform.right;

            if (_terrainAwareness != null)
            {
                dodgeDir = _terrainAwareness.GetSafeMovementDirection(dodgeDir);
            }

            _committedMoveDirection = dodgeDir;
            _targetMoveDirection = dodgeDir;
            SetIntent(MovementIntent.CombatDodge);
            _commitmentStartTime = Time.time;
            _commitmentDuration = 0.5f;
            _hasActiveCommitment = true;

            _staminaManager?.OnDodgePerformed();

            if (_combat != null)
            {
                _combat.RequestEmergencyDodge(dodgeDir);
            }

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion.companionName} emergency dodge!");
            }
        }

        private void ExecuteEmergencyBlock()
        {
            SetIntent(MovementIntent.CombatBlock);
            _commitmentStartTime = Time.time;
            _commitmentDuration = 1.0f;
            _hasActiveCommitment = true;

            _staminaManager?.OnBlockStarted();

            if (_combat != null)
            {
                _combat.RequestBlock();
            }

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion.companionName} emergency block!");
            }
        }

        private void CheckWeaponSwapOpportunity(float distToTarget, bool currentlyRanged)
        {
            if (_combat == null) return;
            if (!_combat.CanSwapWeapons()) return;
            if (_combat.IsAttacking() || _combat.IsDodging()) return;
            
            if (_staminaManager != null)
            {
                if (_staminaManager.IsInCriticalRecovery() || _staminaManager.IsRecovering())
                {
                    return;
                }
            }
            
            if (_currentIntent == MovementIntent.CombatRetreat)
            {
                return;
            }

            float heightDiff = 0f;
            if (_committedTarget != null)
            {
                heightDiff = _committedTarget.transform.position.y - transform.position.y;
            }

            if (!currentlyRanged)
            {
                bool shouldSwapToRanged = false;

                if (distToTarget > 15f)
                {
                    shouldSwapToRanged = true;
                }

                if (Mathf.Abs(heightDiff) > 4f)
                {
                    shouldSwapToRanged = true;
                }

                if (shouldSwapToRanged)
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} requesting swap to ranged - dist={distToTarget:F1}, heightDiff={heightDiff:F1}");
                    }
                    _combat.ForceSwapToRanged();
                }
            }
            else
            {
                bool shouldSwapToMelee = false;

                if (distToTarget < 4f)
                {
                    shouldSwapToMelee = true;
                }

                int nearbyEnemies = CountNearbyEnemies(5f);
                if (nearbyEnemies >= 2 && distToTarget < 8f)
                {
                    shouldSwapToMelee = true;
                }

                if (shouldSwapToMelee)
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} requesting swap to melee - dist={distToTarget:F1}, nearbyEnemies={nearbyEnemies}");
                    }
                    _combat.ForceSwapToMelee();
                }
            }
        }

        private int CountNearbyEnemies(float range)
        {
            return _combatHandler?.CountNearbyEnemies(range) ?? 0;
        }

        private (MovementIntent intent, float duration) DetermineRangedIntent(float distToTarget, bool isAttacking, StaminaManager.StaminaRecommendation staminaRec)
        {
            const float DANGER_RANGE = 5f;
            const float OPTIMAL_MIN = 10f;
            const float OPTIMAL_MAX = 15f;

            if (isAttacking)
            {
                return (MovementIntent.PlantedFiring, 2f);
            }

            if (distToTarget < DANGER_RANGE)
            {
                return (MovementIntent.CombatRetreat, approachCommitmentDuration);
            }

            if (distToTarget >= OPTIMAL_MIN && distToTarget <= OPTIMAL_MAX)
            {
                if (staminaRec != StaminaManager.StaminaRecommendation.DefendOnly &&
                    (_attackRecognition?.IsSafeToAttack() ?? true))
                {
                    return (MovementIntent.PlantedFiring, 1.5f);
                }
                return (MovementIntent.Repositioning, 1f);
            }

            if (distToTarget < OPTIMAL_MIN)
            {
                return (MovementIntent.CombatRetreat, approachCommitmentDuration);
            }

            return (MovementIntent.CombatApproach, approachCommitmentDuration);
        }

        private (MovementIntent intent, float duration) DetermineMeleeIntent(float distToTarget, bool isAttacking,
            StaminaManager.StaminaRecommendation staminaRec, EnemyAttackRecognition.ThreatAssessment threat)
        {
            float attackRange = _combat?.attackRange ?? 2.5f;

            if (isAttacking)
            {
                return (MovementIntent.Idle, 0.5f);
            }

            if (staminaRec == StaminaManager.StaminaRecommendation.CautiousAttack)
            {
                if (distToTarget <= attackRange * 1.2f)
                {
                    return (MovementIntent.CombatStrafe, strafeCommitmentDuration * 1.5f);
                }
            }

            if (IsTargetFleeing(_committedTarget) && distToTarget > attackRange * 1.5f)
            {
                return (MovementIntent.CombatChase, approachCommitmentDuration * 1.5f);
            }

            if (distToTarget <= attackRange * 1.2f)
            {
                if (threat.Level <= EnemyAttackRecognition.ThreatLevel.Low &&
                    staminaRec == StaminaManager.StaminaRecommendation.FullAggression)
                {
                    return (MovementIntent.CombatStrafe, strafeCommitmentDuration * 0.5f);
                }
                return (MovementIntent.CombatStrafe, strafeCommitmentDuration);
            }

            return (MovementIntent.CombatApproach, approachCommitmentDuration);
        }

        private void CommitToIntent(MovementIntent intent, float duration)
        {
            SetIntent(intent);
            _commitmentStartTime = Time.time;
            _commitmentDuration = duration;
            _hasActiveCommitment = true;
            _moveDirSet = false;

            if (intent == MovementIntent.CombatStrafe && !_isStrafeCommitted)
            {
                CommitToNewStrafe();
            }

            if (_committedTarget != null)
            {
                Vector3 toTarget = (_committedTarget.transform.position - transform.position).normalized;
                toTarget.y = 0;

                switch (intent)
                {
                    case MovementIntent.CombatApproach:
                    case MovementIntent.CombatChase:
                    case MovementIntent.CombatIntercept:
                        _committedMoveDirection = toTarget;
                        _targetMoveDirection = toTarget;
                        break;
                    case MovementIntent.CombatRetreat:
                        Vector3 retreatDir = -toTarget;
                        if (_terrainAwareness != null)
                        {
                            retreatDir = _terrainAwareness.GetSafeRetreatDirection(toTarget);
                        }
                        _committedMoveDirection = retreatDir;
                        _targetMoveDirection = retreatDir;
                        break;
                    default:
                        _committedMoveDirection = Vector3.zero;
                        _targetMoveDirection = Vector3.zero;
                        break;
                }
            }
        }

        private void SetIntent(MovementIntent newIntent)
        {
            if (newIntent != _currentIntent)
            {
                _previousIntent = _currentIntent;
                _lastStateChangeTime = Time.time;
                
                MovementMode oldMode = GetMovementModeForIntent();
                _currentIntent = newIntent;
                MovementMode newMode = GetMovementModeForIntent();
                
                if (oldMode != newMode)
                {
                    _movementModeSet = false;
                }
                
                bool needsNewDirection = 
                    (newIntent == MovementIntent.Idle) ||
                    (newIntent == MovementIntent.PlantedFiring) ||
                    (newIntent == MovementIntent.CombatBlock) ||
                    (_previousIntent == MovementIntent.CombatStrafe && newIntent != MovementIntent.CombatStrafe) ||
                    (_previousIntent == MovementIntent.CombatRetreat && newIntent != MovementIntent.CombatRetreat);
                    
                if (needsNewDirection)
                {
                    _moveDirSet = false;
                }
            }
            _committedIntent = newIntent;
        }

        private void CommitToNewStrafe()
        {
            _combatHandler?.CommitToNewStrafe();
            
            _strafeDirection = _combatHandler?.StrafeDirection ?? 0;
            _strafeCommitEndTime = Time.time + strafeCommitmentDuration;
            _isStrafeCommitted = true;
            _moveDirSet = false;
        }

        private void ExecuteCommittedStrafe()
        {
            _combatHandler?.SetCommittedTarget(_committedTarget);
            var result = _combatHandler?.ExecuteStrafe();
            
            if (result == null || result.ShouldStop)
            {
                StopMovementGradually();
                return;
            }
            
            if (result.StrafeDirectionFlipped)
            {
                _strafeDirection = _combatHandler?.StrafeDirection ?? _strafeDirection * -1;
                _moveDirSet = false;
            }
            
            _targetMoveDirection = result.MoveDirection;
            SetMoveDirSafe(result.MoveDirection);
        }

        private void ExecuteCommittedRetreat()
        {
            _combatHandler?.SetCommittedTarget(_committedTarget);
            _combatHandler?.SetCommittedDirection(_committedMoveDirection);
            var result = _combatHandler?.ExecuteRetreat();

            if (result == null || result.ShouldStop) return;

            _targetMoveDirection = result.MoveDirection;
            SetMoveDirSafe(result.MoveDirection);
            FaceMovementDirection(result.MoveDirection);
        }

        private void ExecuteCommittedIntercept()
        {
            _combatHandler?.SetCommittedTarget(_committedTarget);
            var result = _combatHandler?.ExecuteIntercept();
            
            if (result == null || result.ShouldStop) return;
            
            _targetMoveDirection = result.MoveDirection;
            SetMoveDirSafe(result.MoveDirection);
        }

        private void ExecuteCommittedReposition()
        {
            _combatHandler?.SetCommittedTarget(_committedTarget);
            var result = _combatHandler?.ExecuteReposition();
            
            if (result == null || result.ShouldStop) return;
            
            _targetMoveDirection = result.MoveDirection;
            SetMoveDirSafe(result.MoveDirection);
        }

        private void ExecuteCommittedApproach()
        {
            _combatHandler?.SetCommittedTarget(_committedTarget);
            var result = _combatHandler?.ExecuteApproach();
            
            if (result == null || result.ShouldStop) return;
            
            _targetMoveDirection = result.MoveDirection;
            SetMoveDirSafe(result.MoveDirection);
        }

        private void ClearCombatCommitment()
        {
            _hasActiveCommitment = false;
            _committedTarget = null;
            _isStrafeCommitted = false;
            _strafeDirection = 0;
            _moveDirSet = false;
            _hasRangedMovementRequest = false;

            if (_combat != null)
            {
                _combat.RequestStopBlocking();
            }
            _staminaManager?.OnBlockEnded();
        }

        private void ForceTargetSwitch(Character newTarget)
        {
            _committedTarget = newTarget;
            _hasActiveCommitment = false;
            _moveDirSet = false;

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion.companionName} force-switched to {newTarget?.m_name}");
            }
        }

        private bool IsProjectileIncoming()
        {
            return _combatHandler?.IsProjectileIncoming() ?? false;
        }

        private bool ShouldInterceptForOwner(Character enemy)
        {
            return _combatHandler?.ShouldInterceptForOwner(enemy) ?? false;
        }

        private bool IsTargetFleeing(Character target)
        {
            if (_combatHandler != null && _committedTarget == target)
            {
                return _combatHandler.IsTargetFleeing();
            }
            
            if (target == null) return false;
            var velocity = GetCharacterVelocity(target);
            if (velocity.magnitude < 0.5f) return false;
            Vector3 toUs = (transform.position - target.transform.position).normalized;
            float dot = Vector3.Dot(velocity.normalized, toUs);
            return dot < -0.5f;
        }

        #endregion

        #region Ranged Weapon Movement Handling

        private void SubscribeToRangedBehaviors()
        {
            if (_rangedBehaviorsSubscribed) return;
            if (_combat == null) return;
        
            try
            {
                var behaviorsField = typeof(CompanionCombat).GetField("_behaviors",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
 
                if (behaviorsField != null)
                {
                    var behaviors = behaviorsField.GetValue(_combat) as System.Collections.Generic.Dictionary<CompanionCombat.WeaponType, WeaponBehavior>;
                    if (behaviors != null)
                    {
                        if (behaviors.TryGetValue(CompanionCombat.WeaponType.Bow, out var bowBehavior))
                        {
                            _bowBehavior = bowBehavior as BowBehavior;
                            if (_bowBehavior != null)
                            {
                                _bowBehavior.OnMovementRequested += HandleBowMovementRequest;
         
                                if (VerboseLogging)
                                    Debug.Log($"[CompanionCombatMovement] Subscribed to BowBehavior movement requests");
                            }
                        }
              
                        if (behaviors.TryGetValue(CompanionCombat.WeaponType.Crossbow, out var crossbowBehavior))
                        {
                            _crossbowBehavior = crossbowBehavior as CrossbowBehavior;
                            if (_crossbowBehavior != null)
                            {
                                _crossbowBehavior.OnMovementRequested += HandleCrossbowMovementRequest;
       
                                if (VerboseLogging)
                                    Debug.Log($"[CompanionCombatMovement] Subscribed to CrossbowBehavior movement requests");
                            }
                        }
     
                        if (behaviors.TryGetValue(CompanionCombat.WeaponType.Staff, out var staffBehavior))
                        {
                            _staffBehavior = staffBehavior as StaffBehavior;
                            if (_staffBehavior != null)
                            {
                                _staffBehavior.OnMovementRequested += HandleStaffMovementRequest;
                                
                                if (VerboseLogging)
                                    Debug.Log($"[CompanionCombatMovement] Subscribed to StaffBehavior movement requests (support={_staffBehavior.IsSupportStaff})");
                            }
                        }
       
                        _rangedBehaviorsSubscribed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[CompanionCombatMovement] Failed to subscribe to ranged behaviors: {ex.Message}");
            }
        }
        
        private void UnsubscribeFromRangedBehaviors()
        {
            if (_bowBehavior != null)
            {
                _bowBehavior.OnMovementRequested -= HandleBowMovementRequest;
            }
            if (_crossbowBehavior != null)
            {
                _crossbowBehavior.OnMovementRequested -= HandleCrossbowMovementRequest;
            }
            if (_staffBehavior != null)
            {
                _staffBehavior.OnMovementRequested -= HandleStaffMovementRequest;
            }
            _rangedBehaviorsSubscribed = false;
        }

        private void HandleBowMovementRequest(BowBehavior.MovementRequest request, Vector3 direction)
        {
            var result = _rangedHandler?.HandleBowRequest(request, direction, _isInCombat);
            if (result != null) ApplyRangedResult(result);
        }
        
        private void HandleCrossbowMovementRequest(CrossbowBehavior.MovementRequest request, Vector3 direction)
        {
            var result = _rangedHandler?.HandleCrossbowRequest(request, direction, _isInCombat);
            if (result != null) ApplyRangedResult(result);
        }
        
        private void HandleStaffMovementRequest(StaffBehavior.MovementRequest request, Vector3 direction)
        {
            if (_staminaManager != null && _staminaManager.IsInCriticalRecovery())
            {
                return;
            }
            
            MovementIntentType intentType = request switch
            {
                StaffBehavior.MovementRequest.None => MovementIntentType.None,
                StaffBehavior.MovementRequest.Stop => MovementIntentType.PlantedFiring,
                StaffBehavior.MovementRequest.RunAway => MovementIntentType.Retreat,
                StaffBehavior.MovementRequest.Backpedal => MovementIntentType.Reposition,
                StaffBehavior.MovementRequest.Strafe => MovementIntentType.Strafe,
                StaffBehavior.MovementRequest.Approach => MovementIntentType.Approach,
                StaffBehavior.MovementRequest.MoveToAllies => MovementIntentType.Approach,
                _ => MovementIntentType.None
            };
            
            var result = new RangedMovementResult
            {
                Intent = intentType,
                Direction = direction,
                Duration = request == StaffBehavior.MovementRequest.RunAway ? 1.5f : 
                           request == StaffBehavior.MovementRequest.Backpedal ? 1.0f : 0.5f,
                ShouldStop = request == StaffBehavior.MovementRequest.Stop || request == StaffBehavior.MovementRequest.None
            };
            
            ApplyRangedResult(result);
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] Staff movement request: {request} -> {intentType}, dir: {direction}");
            }
        }
        
        private void ApplyRangedResult(RangedMovementResult result)
        {
            if (_rangedHandler.ShouldBlockIntent(result.Intent)) return;
            
            _hasRangedMovementRequest = true;
            _rangedRequestTime = Time.time;
            
            MovementIntent intent = result.Intent switch
            {
                MovementIntentType.PlantedFiring => MovementIntent.PlantedFiring,
                MovementIntentType.Retreat => MovementIntent.CombatRetreat,
                MovementIntentType.Reposition => MovementIntent.Repositioning,
                MovementIntentType.Strafe => MovementIntent.CombatStrafe,
                MovementIntentType.Approach => MovementIntent.CombatApproach,
                MovementIntentType.Dodge => MovementIntent.CombatDodge,
                _ => MovementIntent.Idle
            };
            
            SetIntent(intent);
            _committedMoveDirection = result.Direction;
            _targetMoveDirection = result.Direction;
            _hasActiveCommitment = true;
            _commitmentStartTime = Time.time;
            _commitmentDuration = result.Duration;
            
            if (result.StrafeDirection != 0)
            {
                _strafeDirection = result.StrafeDirection;
                _isStrafeCommitted = true;
                _strafeCommitEndTime = Time.time + strafeCommitmentDuration;
            }
            
            if (result.ShouldStop)
                StopMovementGradually();
            else if (result.Direction.sqrMagnitude > 0.01f)
                SetMoveDirSafe(result.Direction);
            
            _moveDirSet = false;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] Ranged movement: {intent}, dir: {result.Direction}");
        }

        #endregion

        #region State Execution Methods
        
        private void ExecuteFleeingState()
        {
            if (_isInCombat || _hasActiveCommitment || _hasRangedMovementRequest)
            {
                _isInCombat = false;
                _currentTarget = null;
                _committedTarget = null;
                _hasActiveCommitment = false;
                _hasRangedMovementRequest = false;
                _isStrafeCommitted = false;
                _strafeDirection = 0;
                _movementModeSet = false;
                _moveDirSet = false;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} entering FLEE mode");
            }
            
            if (_fleeHandler != null && _fleeHandler.HasFleeRequest)
            {
                Vector3 moveDir = _fleeHandler.ExecuteFleeMovement();
                _currentMoveDirection = moveDir;
                _targetMoveDirection = moveDir;
                // Flee runs (CombatRetreat → Run mode). Drive through UMA, not a raw write: the handler
                // now only computes the direction; we own Combat authority and route it via SetMoveDirSafe.
                _currentIntent = MovementIntent.CombatRetreat;
                FaceMovementDirection(moveDir);
                SetMoveDirSafe(moveDir);
            }
            else
            {
                Vector3 moveDir = _fleeHandler?.ExecuteFallbackFleeMovement() ?? Vector3.zero;
                _currentMoveDirection = moveDir;
                _targetMoveDirection = moveDir;
                _currentIntent = MovementIntent.CombatRetreat;
                FaceMovementDirection(moveDir);
                SetMoveDirSafe(moveDir);
            }
        }
        
        private void ExecuteCommandPriorityState()
        {
            if (_isInCombat)
            {
                _isInCombat = false;
                _currentTarget = null;
                _committedTarget = null;
                _hasActiveCommitment = false;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} yielding combat to absolute priority command");
            }
        }
        
        private void ExecuteEmoteFrozenState()
        {
            // Movement should be frozen via authority - don't call SetMoveDir directly
            // The authority's freeze mechanism handles this
        }
        
        private void ExecuteMovementLockedState()
        {
            // Movement should be frozen via authority - don't call SetMoveDir directly
            // The authority's freeze mechanism handles this
        }

        #endregion
    }
}
