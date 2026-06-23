using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Combat behavior for shield-bearing tank companions.
    /// Triggers when companion has a SHIELD equipped (with any one-handed weapon).
    /// Unlike regular melee which kites and retreats, tanks:
    /// - Stay close to the action (reduced retreat distance)
    /// - Hold ground when taunting
    /// - Prioritize blocking over dodging
    /// - Only retreat when health is critical AND no healer available
    /// - Try to stay between enemies and squishier allies
    /// 
    /// DETECTION: Companion has Shield in left hand + any weapon in right hand
    /// 
    /// TANK POSITIONING PHILOSOPHY:
    /// - "Center of the group" - stay near the average position of allies
    /// - "Face the danger" - orient toward the most threatening enemies
    /// - "Hold the line" - don't retreat unless absolutely necessary
    /// - "Shield the weak" - intercept threats targeting squishier allies
    /// 
    /// MOVEMENT REQUEST SYSTEM:
    /// Like BowBehavior and StaffBehavior, this behavior fires OnMovementRequested
    /// events that CompanionCombatMovement subscribes to. This ensures consistent
    /// movement handling across all weapon types.
    /// </summary>
    public class ShieldBearerBehavior : MeleeBehavior
    {
        #region Enums
        
        public enum TankCombatPhase
        {
            Idle,
            Approaching,
            Engaging,         // In melee range, actively fighting
            HoldingGround,    // Standing firm (during taunt or high threat)
            Intercepting,     // Moving to intercept threat to ally
            TacticalRetreat,  // Only when health is critical
            Repositioning     // Adjusting position relative to group
        }
        
        public enum MovementRequest
        {
            None,
            Stop,               // Plant feet, hold position
            Approach,           // Move toward target
            SlowApproach,       // Walk toward target (tank pace)
            Strafe,             // Circle strafe around target
            Intercept,          // Move to intercept threat
            TacticalRetreat,    // Controlled retreat (slower than flee)
            Reposition          // Move to optimal tank position
        }
        
        #endregion
        
        #region Constants
        
        // Tank positioning - closer engagement than other melee
        private const float OPTIMAL_ENGAGEMENT_RANGE = 2.5f;   // Sweet spot for melee
        private const float MAX_ENGAGEMENT_RANGE = 4f;         // Start approaching if further
        private const float MIN_ENGAGEMENT_RANGE = 1.5f;       // Too close, adjust slightly
        
        // Health thresholds for retreat decisions
        private const float CRITICAL_HEALTH_WITH_HEALER = 0.15f;    // Only retreat at 15% if healer present
        private const float CRITICAL_HEALTH_NO_HEALER = 0.30f;      // Retreat at 30% if no healer
        
        // Group positioning
        private const float MAX_DISTANCE_FROM_GROUP = 15f;     // Don't stray too far from allies
        private const float INTERCEPTION_TRIGGER_DIST = 8f;    // Distance to ally before intercepting threat
        
        // Timing
        private const float THREAT_CHECK_INTERVAL = 0.25f;
        private const float GROUP_CENTER_UPDATE_INTERVAL = 1f;
        private const float HOLD_GROUND_DURATION = 3f;         // Hold position after taunt
        
        #endregion
        
        #region State
        
        // Tank combat state
        private TankCombatPhase _currentPhase = TankCombatPhase.Idle;
        private MovementRequest _currentMovementRequest = MovementRequest.None;
        private float _phaseStartTime;
        private float _lastThreatCheck;
        
        // Group awareness
        private Vector3 _groupCenterPosition;
        private float _lastGroupCenterUpdate;
        private bool _hasHealerInGroup;
        private Character _healerCharacter;
        
        // Threat tracking
        private Character _mostThreateningEnemy;
        private Character _interceptionTarget;
        private float _lastKnownTargetDistance;
        private bool _isTaunting;
        private float _tauntEndTime;
        
        // Strafe state
        private int _strafeDirection = 1;
        private float _lastStrafeChange;
        private const float STRAFE_CHANGE_INTERVAL = 2f;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Public Properties
        
        public TankCombatPhase CurrentPhase => _currentPhase;
        public MovementRequest CurrentMovementNeed => _currentMovementRequest;
        public float TargetDistance => _lastKnownTargetDistance;
        public bool IsTaunting => _isTaunting;
        
        /// <summary>Event fired when movement is requested. CompanionCombatMovement subscribes to this.</summary>
        public System.Action<MovementRequest, Vector3> OnMovementRequested;
        
        #endregion
        
        #region Initialization
        
        public override void OnActivate()
        {
            base.OnActivate();
            _currentPhase = TankCombatPhase.Idle;
            _currentMovementRequest = MovementRequest.None;
            UpdateGroupAwareness();
        }
        
        public override void OnDeactivate()
        {
            _currentPhase = TankCombatPhase.Idle;
            _currentMovementRequest = MovementRequest.None;
            base.OnDeactivate();
        }
        
        public override void ConfigureAI()
        {
            base.ConfigureAI();
            
            // Tanks prefer closer engagement
            Context.AttackRange = MAX_ENGAGEMENT_RANGE;
            
            if (VerboseLogging)
            {
                Debug.Log($"[ShieldBearerBehavior] Configured for TANK combat: " +
                    $"engageRange={OPTIMAL_ENGAGEMENT_RANGE}m, hasHealer={_hasHealerInGroup}");
            }
        }
        
        #endregion
        
        #region Update Loop
        
        public override void Update()
        {
            base.Update();
            
            // Check StaminaManager - if in critical recovery, allow limited retreat
            var staminaManager = Owner?.GetComponent<StaminaManager>();
            if (staminaManager != null && staminaManager.IsInCriticalRecovery())
            {
                // Tanks don't fully flee - they do tactical retreat
                SetPhase(TankCombatPhase.TacticalRetreat);
                return;
            }
            
            // Update group awareness periodically
            if (Time.time - _lastGroupCenterUpdate >= GROUP_CENTER_UPDATE_INTERVAL)
            {
                _lastGroupCenterUpdate = Time.time;
                UpdateGroupAwareness();
            }
            
            // Check taunt state
            UpdateTauntState();
            
            var target = Context.CompanionAI?.GetTargetCreature();
            
            if (target == null || target.IsDead())
            {
                SetPhase(TankCombatPhase.Idle);
                RequestMovement(MovementRequest.None, Vector3.zero);
                return;
            }
            
            // Update target tracking
            _lastKnownTargetDistance = Vector3.Distance(Context.Transform.position, target.transform.position);
            
            // Check threats
            if (Time.time - _lastThreatCheck >= THREAT_CHECK_INTERVAL)
            {
                _lastThreatCheck = Time.time;
                CheckThreatsToAllies();
            }
            
            // State machine update
            UpdateTankCombatPhase(target);
        }
        
        private void UpdateTauntState()
        {
            var archetypeController = Owner?.GetComponent<ArchetypeController>();
            if (archetypeController != null)
            {
                // Check if we just started taunting
                bool wasTaunting = _isTaunting;
                
                // Check for the "Taunting" buff on ourselves
                var seman = Context.Character?.GetSEMan();
                if (seman != null)
                {
                    int tauntingHash = "CompanionTaunting".GetStableHashCode();
                    _isTaunting = seman.HaveStatusEffect(tauntingHash);
                    
                    if (_isTaunting && !wasTaunting)
                    {
                        // Just started taunting - enter hold ground phase
                        SetPhase(TankCombatPhase.HoldingGround);
                        _tauntEndTime = Time.time + HOLD_GROUND_DURATION;
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[ShieldBearerBehavior] Taunt started - holding ground for {HOLD_GROUND_DURATION}s");
                        }
                    }
                }
            }
        }
        
        private void UpdateGroupAwareness()
        {
            var companion = Context.Companion;
            if (companion == null) return;
            
            // Calculate group center (average position of owner + all companions)
            Vector3 totalPos = Vector3.zero;
            int count = 0;
            
            var owner = companion.GetOwner();
            if (owner != null)
            {
                totalPos += owner.transform.position;
                count++;
            }
            
            _hasHealerInGroup = false;
            _healerCharacter = null;
            
            foreach (var comp in CompanionController.AllCompanions)
            {
                if (comp == null || comp.isDefeated) continue;
                if (comp.ownerPlayerId != companion.ownerPlayerId) continue;
                
                var compChar = comp.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    totalPos += comp.transform.position;
                    count++;
                    
                    // Check if this is a healer
                    var archetypeController = comp.GetComponent<ArchetypeController>();
                    if (archetypeController?.CurrentArchetypeClass == ArchetypeClass.Healer)
                    {
                        _hasHealerInGroup = true;
                        _healerCharacter = compChar;
                    }
                }
            }
            
            if (count > 0)
            {
                _groupCenterPosition = totalPos / count;
            }
        }
        
        private void CheckThreatsToAllies()
        {
            var companion = Context.Companion;
            if (companion == null) return;
            
            var owner = companion.GetOwner();
            _mostThreateningEnemy = null;
            _interceptionTarget = null;
            float highestThreat = 0;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(Context.Character, character)) continue;
                
                var ai = character.GetComponent<BaseAI>();
                if (ai == null) continue;
                
                var aiTarget = ai.GetTargetCreature();
                if (aiTarget == null) continue;
                
                // Calculate threat level based on who's being targeted
                float threatLevel = 0;
                
                // Highest priority: targeting owner
                if (aiTarget == owner)
                {
                    float distToOwner = Vector3.Distance(character.transform.position, owner.transform.position);
                    threatLevel = 100f - distToOwner; // Closer = higher threat
                }
                // High priority: targeting healer
                else if (aiTarget == _healerCharacter)
                {
                    float distToHealer = Vector3.Distance(character.transform.position, _healerCharacter.transform.position);
                    threatLevel = 80f - distToHealer;
                }
                // Medium priority: targeting any other companion
                else if (aiTarget.IsTamed())
                {
                    threatLevel = 50f;
                }
                
                if (threatLevel > highestThreat)
                {
                    highestThreat = threatLevel;
                    _mostThreateningEnemy = character;
                    
                    // Check if we should intercept
                    if (aiTarget != Context.Character)
                    {
                        float distToTarget = Vector3.Distance(aiTarget.transform.position, character.transform.position);
                        if (distToTarget < INTERCEPTION_TRIGGER_DIST)
                        {
                            _interceptionTarget = aiTarget as Character;
                        }
                    }
                }
            }
        }
        
        private void UpdateTankCombatPhase(Character target)
        {
            float timeSincePhaseStart = Time.time - _phaseStartTime;
            
            // Check health for tactical retreat
            float healthPercent = Context.Character?.GetHealthPercentage() ?? 1f;
            float criticalThreshold = _hasHealerInGroup ? CRITICAL_HEALTH_WITH_HEALER : CRITICAL_HEALTH_NO_HEALER;
            
            if (healthPercent < criticalThreshold && _currentPhase != TankCombatPhase.TacticalRetreat)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[ShieldBearerBehavior] Health critical ({healthPercent:P0}), tactical retreat");
                }
                SetPhase(TankCombatPhase.TacticalRetreat);
                return;
            }
            
            switch (_currentPhase)
            {
                case TankCombatPhase.Idle:
                    DecideInitialAction(target);
                    break;
                    
                case TankCombatPhase.Approaching:
                    UpdateApproaching(target);
                    break;
                    
                case TankCombatPhase.Engaging:
                    UpdateEngaging(target);
                    break;
                    
                case TankCombatPhase.HoldingGround:
                    UpdateHoldingGround(target, timeSincePhaseStart);
                    break;
                    
                case TankCombatPhase.Intercepting:
                    UpdateIntercepting();
                    break;
                    
                case TankCombatPhase.TacticalRetreat:
                    UpdateTacticalRetreat(target, healthPercent, criticalThreshold);
                    break;
                    
                case TankCombatPhase.Repositioning:
                    UpdateRepositioning(target, timeSincePhaseStart);
                    break;
            }
        }
        
        private void DecideInitialAction(Character target)
        {
            // Check for interception priority
            if (_interceptionTarget != null && _mostThreateningEnemy != null)
            {
                SetPhase(TankCombatPhase.Intercepting);
                RequestMovement(MovementRequest.Intercept, 
                    (_mostThreateningEnemy.transform.position - Context.Transform.position).normalized);
                return;
            }
            
            // Standard tank engagement
            if (_lastKnownTargetDistance > MAX_ENGAGEMENT_RANGE)
            {
                SetPhase(TankCombatPhase.Approaching);
                RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
            }
            else if (_lastKnownTargetDistance <= OPTIMAL_ENGAGEMENT_RANGE)
            {
                SetPhase(TankCombatPhase.Engaging);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
            }
            else
            {
                // In good range but could be closer
                SetPhase(TankCombatPhase.Approaching);
                RequestMovement(MovementRequest.SlowApproach, GetDirectionToTarget(target));
            }
        }
        
        private void UpdateApproaching(Character target)
        {
            if (_lastKnownTargetDistance <= OPTIMAL_ENGAGEMENT_RANGE)
            {
                SetPhase(TankCombatPhase.Engaging);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
                return;
            }
            
            RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
        }
        
        private void UpdateEngaging(Character target)
        {
            // Tanks hold position and fight
            // Only move for slight repositioning or to maintain engagement
            
            if (_lastKnownTargetDistance > MAX_ENGAGEMENT_RANGE)
            {
                // Target moved away - follow
                SetPhase(TankCombatPhase.Approaching);
                RequestMovement(MovementRequest.SlowApproach, GetDirectionToTarget(target));
                return;
            }
            
            if (_lastKnownTargetDistance < MIN_ENGAGEMENT_RANGE)
            {
                // Too close - slight strafe to adjust
                UpdateStrafeDirection();
                Vector3 strafeDir = GetStrafeDirection(target);
                RequestMovement(MovementRequest.Strafe, strafeDir);
                return;
            }
            
            // Check if we need to intercept a threat
            if (_interceptionTarget != null && _mostThreateningEnemy != null)
            {
                SetPhase(TankCombatPhase.Intercepting);
                return;
            }
            
            // Good position - hold and fight
            RequestMovement(MovementRequest.Stop, Vector3.zero);
        }
        
        private void UpdateHoldingGround(Character target, float timeSinceStart)
        {
            // During taunt or shortly after, tank plants feet
            if (_isTaunting || Time.time < _tauntEndTime)
            {
                RequestMovement(MovementRequest.Stop, Vector3.zero);
                return;
            }
            
            // Taunt ended - return to engaging
            SetPhase(TankCombatPhase.Engaging);
        }
        
        private void UpdateIntercepting()
        {
            if (_mostThreateningEnemy == null || _mostThreateningEnemy.IsDead())
            {
                _interceptionTarget = null;
                SetPhase(TankCombatPhase.Idle);
                return;
            }
            
            float distToThreat = Vector3.Distance(Context.Transform.position, _mostThreateningEnemy.transform.position);
            
            if (distToThreat <= OPTIMAL_ENGAGEMENT_RANGE)
            {
                // Reached interception target - engage
                SetPhase(TankCombatPhase.Engaging);
                
                // Try to taunt the threat
                var archetypeController = Owner?.GetComponent<ArchetypeController>();
                if (archetypeController != null && !archetypeController.IsTauntOnCooldown)
                {
                    archetypeController.ExecuteTaunt();
                }
                return;
            }
            
            RequestMovement(MovementRequest.Intercept, 
                (_mostThreateningEnemy.transform.position - Context.Transform.position).normalized);
        }
        
        private void UpdateTacticalRetreat(Character target, float healthPercent, float threshold)
        {
            // Tanks don't flee - they do controlled retreat while blocking
            // Move slowly away while keeping shield up
            
            if (healthPercent > threshold + 0.1f)
            {
                // Health recovered enough - return to combat
                SetPhase(TankCombatPhase.Engaging);
                return;
            }
            
            // Move toward healer if available, otherwise toward group center
            Vector3 retreatDir;
            if (_healerCharacter != null && !_healerCharacter.IsDead())
            {
                retreatDir = (_healerCharacter.transform.position - Context.Transform.position).normalized;
            }
            else if (_groupCenterPosition != Vector3.zero)
            {
                retreatDir = (_groupCenterPosition - Context.Transform.position).normalized;
            }
            else
            {
                retreatDir = -GetDirectionToTarget(target);
            }
            
            RequestMovement(MovementRequest.TacticalRetreat, retreatDir);
        }
        
        private void UpdateRepositioning(Character target, float timeSinceStart)
        {
            if (timeSinceStart > 1f)
            {
                SetPhase(TankCombatPhase.Engaging);
                return;
            }
            
            // Move toward optimal position
            Vector3 optimalPos = CalculateOptimalTankPosition(target);
            Vector3 moveDir = (optimalPos - Context.Transform.position).normalized;
            RequestMovement(MovementRequest.Reposition, moveDir);
        }
        
        /// <summary>
        /// Calculates the optimal position for a tank - between enemies and allies.
        /// </summary>
        private Vector3 CalculateOptimalTankPosition(Character target)
        {
            if (target == null) return Context.Transform.position;
            
            // Ideal position: OPTIMAL_ENGAGEMENT_RANGE from target, toward group center
            Vector3 dirFromTargetToGroup = (_groupCenterPosition - target.transform.position).normalized;
            if (dirFromTargetToGroup == Vector3.zero)
            {
                dirFromTargetToGroup = -target.transform.forward;
            }
            
            return target.transform.position + dirFromTargetToGroup * OPTIMAL_ENGAGEMENT_RANGE;
        }
        
        #endregion
        
        #region Movement Helpers
        
        private void UpdateStrafeDirection()
        {
            if (Time.time - _lastStrafeChange > STRAFE_CHANGE_INTERVAL)
            {
                _strafeDirection = Random.value > 0.5f ? 1 : -1;
                _lastStrafeChange = Time.time;
            }
        }
        
        private Vector3 GetStrafeDirection(Character target)
        {
            Vector3 toTarget = GetDirectionToTarget(target);
            return Vector3.Cross(Vector3.up, toTarget) * _strafeDirection;
        }
        
        private void SetPhase(TankCombatPhase newPhase)
        {
            if (newPhase == _currentPhase) return;
            
            if (VerboseLogging)
            {
                Debug.Log($"[ShieldBearerBehavior] Phase: {_currentPhase} -> {newPhase}");
            }
            
            _currentPhase = newPhase;
            _phaseStartTime = Time.time;
        }
        
        private void RequestMovement(MovementRequest request, Vector3 direction)
        {
            if (request == _currentMovementRequest && 
                (request == MovementRequest.None || request == MovementRequest.Stop))
            {
                return;
            }
            
            _currentMovementRequest = request;
            OnMovementRequested?.Invoke(request, direction);
        }
        
        private Vector3 GetDirectionToTarget(Character target)
        {
            if (target == null) return Context.Transform.forward;
            Vector3 dir = (target.transform.position - Context.Transform.position).normalized;
            dir.y = 0;
            return dir.normalized;
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Called by CompanionCombatMovement to get the preferred engagement distance.
        /// Tanks prefer closer engagement than other melee.
        /// </summary>
        public float GetPreferredRange()
        {
            return OPTIMAL_ENGAGEMENT_RANGE;
        }
        
        /// <summary>
        /// Called by CompanionCombatMovement to check if tank should back up.
        /// Tanks almost never voluntarily retreat.
        /// </summary>
        public bool ShouldBackUp(float distanceToTarget)
        {
            // Tanks don't back up based on distance - only health
            return false;
        }
        
        /// <summary>
        /// Called when tank is notified to taunt (external trigger).
        /// </summary>
        public void OnTauntStarted()
        {
            _isTaunting = true;
            _tauntEndTime = Time.time + HOLD_GROUND_DURATION;
            SetPhase(TankCombatPhase.HoldingGround);
        }
        
        #endregion
    }
}
