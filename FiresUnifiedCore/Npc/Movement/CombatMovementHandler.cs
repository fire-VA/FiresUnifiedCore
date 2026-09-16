using UnityEngine;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Combat movement (strafing, retreating, intercepting threats to the owner, approaching, repositioning for
    /// ranged attacks, emergency dodge and block), owned by CompanionCombatMovement, which makes every actual
    /// SetMoveDir call.
    /// </summary>
    public class CombatMovementHandler
    {
        #region Settings
        
        public float MeleeStafeDistance { get; set; } = 3f;
        public float StrafeDirectionChangeInterval { get; set; } = 2f;
        public float MaxStrafeDistance { get; set; } = 3f;
        public float OwnerProtectionRadius { get; set; } = 15f;
        public float InterceptionLeadTime { get; set; } = 0.5f;
        public float ApproachCommitmentDuration { get; set; } = 1.5f;
        public float StrafeCommitmentDuration { get; set; } = 1.0f;
        public float MaxCommitmentDuration { get; set; } = 4.0f;
        public float DamageReassessDelay { get; set; } = 0.2f;
        public float ProjectileDetectionRange { get; set; } = 10f;
        public float MovementBlendSpeed { get; set; } = 5f;
        
        // NEW: Owner distance constraints for combat movement
        /// <summary>Maximum distance from owner before combat should be abandoned.</summary>
        public float MaxOwnerDistance { get; set; } = 25f;
        /// <summary>Distance at which we start preferring to stay near owner during combat.</summary>
        public float OwnerLeashDistance { get; set; } = 18f;
        /// <summary>Distance at which we should actively return to owner.</summary>
        public float OwnerReturnDistance { get; set; } = 22f;
        
        #endregion
        
        #region Dependencies
        
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly CompanionController _companion;
        private readonly TerrainAwareness _terrainAwareness;
        private ArchetypeController _archetypeController;
        
        #endregion
        
        #region Group Combat Positioning
        
        // Assigned flanking angle from CombatRoleDirector (degrees around target)
        private float _assignedFlankAngle;
        private bool _hasAssignedFlankAngle;
        
        /// <summary>
        /// Sets the flanking angle for this companion when multiple melee companions
        /// attack the same target. Angles are distributed evenly (e.g., 0°, 120°, 240°).
        /// </summary>
        public void SetFlankAngle(float angleDegrees)
        {
            _assignedFlankAngle = angleDegrees;
            _hasAssignedFlankAngle = true;
        }
        
        /// <summary>
        /// Clears the flanking angle assignment.
        /// </summary>
        public void ClearFlankAngle()
        {
            _hasAssignedFlankAngle = false;
            _assignedFlankAngle = 0f;
        }
        
        #endregion
        
        #region State
        
        // Strafe state
        private int _strafeDirection;
        private float _strafeCommitEndTime;
        private bool _isStrafeCommitted;
        
        // Combat commitment
        private Character _committedTarget;
        private CombatIntent _committedIntent;
        private Vector3 _committedMoveDirection;
        private float _commitmentStartTime;
        private float _commitmentDuration;
        private bool _hasActiveCommitment;
        
        // Movement tracking
        private Vector3 _currentMoveDirection;
        private Vector3 _targetMoveDirection;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Enums
        
        public enum CombatIntent
        {
            Idle,
            Approach,
            Strafe,
            Retreat,
            Intercept,
            Reposition,
            Chase,
            PlantedFiring,
            Dodge,
            Block
        }
        
        #endregion
        
        #region Properties
        
        public Character CommittedTarget => _committedTarget;
        public CombatIntent CurrentIntent => _committedIntent;
        public bool HasActiveCommitment => _hasActiveCommitment;
        public Vector3 TargetMoveDirection => _targetMoveDirection;
        public Vector3 CurrentMoveDirection => _currentMoveDirection;
        public int StrafeDirection => _strafeDirection;
        public bool IsStrafeCommitted => _isStrafeCommitted;
        
        #endregion
        
        #region Constructor
        
        public CombatMovementHandler(
            Transform transform,
            Character character,
            CompanionController companion,
            TerrainAwareness terrainAwareness)
        {
            _transform = transform;
            _character = character;
            _companion = companion;
            _terrainAwareness = terrainAwareness;
            _archetypeController = companion?.GetArchetypeController();
        }
        
        #endregion
        
        #region Commitment Management
        
        /// <summary>
        /// Commits to a new combat intent for a specified duration.
        /// </summary>
        public void CommitToIntent(CombatIntent intent, Character target, float duration)
        {
            _committedIntent = intent;
            _committedTarget = target;
            _commitmentStartTime = Time.time;
            _commitmentDuration = duration;
            _hasActiveCommitment = true;
            
            if (intent == CombatIntent.Strafe && !_isStrafeCommitted)
            {
                CommitToNewStrafe();
            }
            
            // Calculate movement direction based on intent
            if (target != null)
            {
                Vector3 toTarget = (target.transform.position - _transform.position).normalized;
                toTarget.y = 0;
                
                switch (intent)
                {
                    case CombatIntent.Approach:
                    case CombatIntent.Chase:
                    case CombatIntent.Intercept:
                        _committedMoveDirection = toTarget;
                        _targetMoveDirection = toTarget;
                        break;
                        
                    case CombatIntent.Retreat:
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
            
            if (VerboseLogging)
            {
                Debug.Log($"[CombatMovementHandler] Committed to {intent} for {duration:F1}s");
            }
        }
        
        /// <summary>
        /// Clears the current combat commitment.
        /// </summary>
        public void ClearCommitment()
        {
            _hasActiveCommitment = false;
            _committedTarget = null;
            _isStrafeCommitted = false;
            _strafeDirection = 0;
            _committedMoveDirection = Vector3.zero;
            _targetMoveDirection = Vector3.zero;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CombatMovementHandler] Combat commitment cleared");
            }
        }
        
        /// <summary>
        /// Forces a target switch during combat.
        /// </summary>
        public void ForceTargetSwitch(Character newTarget)
        {
            _committedTarget = newTarget;
            _hasActiveCommitment = false;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CombatMovementHandler] Force-switched target to {newTarget?.m_name}");
            }
        }
        
        /// <summary>
        /// Checks if the current commitment has expired.
        /// </summary>
        public bool IsCommitmentExpired()
        {
            if (!_hasActiveCommitment) return true;
            if (_committedTarget == null || _committedTarget.IsDead()) return true;
            if (Time.time > _commitmentStartTime + _commitmentDuration) return true;
            if (Time.time > _commitmentStartTime + MaxCommitmentDuration) return true;
            return false;
        }
        
        #endregion
        
        #region Strafe Movement
        
        /// <summary>
        /// Commits to a new random strafe direction.
        /// </summary>
        public void CommitToNewStrafe()
        {
            float rand = UnityEngine.Random.value;
            if (rand < 0.4f)
                _strafeDirection = -1;
            else if (rand < 0.8f)
                _strafeDirection = 1;
            else
                _strafeDirection = 0;
            
            _strafeCommitEndTime = Time.time + StrafeCommitmentDuration;
            _isStrafeCommitted = true;
        }
        
        /// <summary>
        /// Calculates strafe movement around the target.
        /// Returns the desired move direction.
        /// </summary>
        public Vector3 CalculateStrafeMovement()
        {
            if (_committedTarget == null) return Vector3.zero;
            
            // Check if strafe commitment expired
            if (Time.time > _strafeCommitEndTime)
            {
                CommitToNewStrafe();
            }
            
            if (_strafeDirection == 0 || _character == null)
            {
                return Vector3.zero;
            }
            
            Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
            Vector3 strafeDir = Vector3.Cross(Vector3.up, toTarget) * _strafeDirection;
            
            // Check terrain safety
            if (_terrainAwareness != null && !_terrainAwareness.IsDirectionSafe(strafeDir))
            {
                _strafeDirection *= -1;
                strafeDir *= -1;
            }
            
            // Check if strafing would take us too far or too close
            float distToTarget = Vector3.Distance(_transform.position, _committedTarget.transform.position);
            Vector3 newPos = _transform.position + strafeDir * _character.m_walkSpeed * Time.deltaTime;
            float newDist = Vector3.Distance(newPos, _committedTarget.transform.position);
            
            if (newDist > MeleeStafeDistance + MaxStrafeDistance || newDist < 1.5f)
            {
                _strafeDirection *= -1;
                strafeDir *= -1;
            }
            
            _targetMoveDirection = strafeDir.normalized;
            return strafeDir.normalized;
        }
        
        #endregion
        
        #region Retreat Movement
        
        /// <summary>
        /// Calculates retreat movement away from the target.
        /// Returns the desired move direction.
        /// </summary>
        public Vector3 CalculateRetreatMovement()
        {
            if (_committedTarget == null) return Vector3.zero;
            
            if (_committedMoveDirection.sqrMagnitude > 0.1f)
            {
                Vector3 safeDir = _terrainAwareness != null
                    ? _terrainAwareness.GetSafeMovementDirection(_committedMoveDirection)
                    : _committedMoveDirection;
                
                _targetMoveDirection = safeDir;
                return safeDir;
            }
            
            return Vector3.zero;
        }
        
        /// <summary>
        /// Calculates retreat direction toward the owner (for stamina recovery).
        /// </summary>
        public Vector3 CalculateRetreatToOwner()
        {
            var owner = _companion?.GetOwner();
            if (owner == null)
            {
                // No owner - retreat from target
                if (_committedTarget != null)
                {
                    Vector3 retreatDir = (_transform.position - _committedTarget.transform.position).normalized;
                    retreatDir.y = 0;
                    return retreatDir;
                }
                return Vector3.zero;
            }
            
            Vector3 toOwner = (owner.transform.position - _transform.position).normalized;
            toOwner.y = 0;
            
            if (_terrainAwareness != null)
            {
                toOwner = _terrainAwareness.GetSafeMovementDirection(toOwner);
            }
            
            return toOwner;
        }
        
        #endregion
        
        #region Intercept Movement
        
        /// <summary>
        /// Calculates intercept position to protect the owner.
        /// ARCHETYPE: Tanks use enhanced interception, positioning aggressively in front of owner.
        /// Returns the desired move direction.
        /// </summary>
        public Vector3 CalculateInterceptMovement()
        {
            if (_committedTarget == null) return Vector3.zero;
            
            var owner = _companion?.GetOwner();
            if (owner == null) return Vector3.zero;
            
            Vector3 enemyPos = _committedTarget.transform.position;
            Vector3 ownerPos = owner.transform.position;
            Vector3 enemyToOwner = (ownerPos - enemyPos).normalized;
            
            // ARCHETYPE: Tanks position much more aggressively in front
            float interceptDistance = 3f;
            if (_archetypeController != null && _archetypeController.IsTank)
            {
                // Tanks get closer to the enemy, creating a wall between enemy and allies
                interceptDistance = 2f;
            }
            
            Vector3 interceptPos = enemyPos + enemyToOwner * interceptDistance;
            Vector3 toIntercept = (interceptPos - _transform.position).normalized;
            
            _targetMoveDirection = toIntercept;
            return toIntercept;
        }
        
        /// <summary>
        /// Determines if the companion should intercept for the owner.
        /// ARCHETYPE: Tanks are much more likely to intercept - it's their job!
        /// </summary>
        public bool ShouldInterceptForOwner(Character enemy)
        {
            if (enemy == null) return false;
            
            var owner = _companion?.GetOwner();
            if (owner == null) return false;
            
            // ARCHETYPE: Tanks ALWAYS try to intercept when owner is threatened
            bool isTank = _archetypeController?.IsTank ?? false;
            
            var enemyAI = enemy.GetComponent<BaseAI>();
            if (enemyAI != null)
            {
                var enemyTarget = enemyAI.GetTargetCreature();
                var ownerCharacter = owner.GetComponent<Character>();
                
                // If enemy is targeting owner, tanks always intercept
                if (enemyTarget == ownerCharacter && isTank)
                {
                    float enemyToOwner = Vector3.Distance(enemy.transform.position, owner.transform.position);
                    if (enemyToOwner < OwnerProtectionRadius * 1.5f) // Tanks intercept from further away
                    {
                        return true;
                    }
                }
            }
            
            // Standard interception check
            var standardAI = enemy.GetComponent<BaseAI>();
            if (standardAI == null) return false;
            
            var target = standardAI.GetTargetCreature();
            if (target == null) return false;
            
            var ownerChar = owner.GetComponent<Character>();
            if (target != ownerChar) return false;
            
            float distToOwner = Vector3.Distance(enemy.transform.position, owner.transform.position);
            if (distToOwner > OwnerProtectionRadius) return false;
            
            float companionToOwner = Vector3.Distance(_transform.position, owner.transform.position);
            return distToOwner < companionToOwner;
        }
        
        #endregion
        
        #region Approach Movement
        
        /// <summary>
        /// Calculates approach movement toward the target.
        /// When a flanking angle is assigned (from CombatRoleDirector), approaches
        /// from the assigned angle instead of straight on.
        /// Returns the desired move direction.
        /// </summary>
        public Vector3 CalculateApproachMovement()
        {
            if (_committedTarget == null) return Vector3.zero;
            
            Vector3 targetPos = _committedTarget.transform.position;
            
            // GROUP COMBAT: If we have a flanking angle assignment, approach from that angle
            // instead of straight on. This spreads melee companions around the target.
            if (_hasAssignedFlankAngle)
            {
                float engageDist = CompanionSettings.CombatCoordinationMeleeEngageDistance;
                Vector3 flankOffset = Quaternion.Euler(0f, _assignedFlankAngle, 0f) * Vector3.forward * engageDist;
                Vector3 flankPosition = targetPos + flankOffset;
                
                Vector3 toFlankPos = (flankPosition - _transform.position);
                toFlankPos.y = 0f;
                
                if (toFlankPos.sqrMagnitude > 0.5f) // More than 0.7m from flank position
                {
                    _targetMoveDirection = toFlankPos.normalized;
                    return _targetMoveDirection;
                }
                // Already at flank position — face the target
                Vector3 toTarget = (targetPos - _transform.position);
                toTarget.y = 0f;
                _targetMoveDirection = toTarget.normalized;
                return _targetMoveDirection;
            }
            
            // Default: direct approach
            Vector3 directToTarget = (targetPos - _transform.position).normalized;
            directToTarget.y = 0;
            
            if (directToTarget.sqrMagnitude > 0.01f)
            {
                _targetMoveDirection = directToTarget;
                return directToTarget;
            }
            
            return Vector3.zero;
        }
        
        #endregion
        
        #region Reposition Movement
        
        /// <summary>
        /// Calculates repositioning movement for optimal range.
        /// Used primarily for ranged combat.
        /// </summary>
        public Vector3 CalculateRepositionMovement(float optimalRange = 12f)
        {
            if (_committedTarget == null) return Vector3.zero;
            
            float distToTarget = Vector3.Distance(_transform.position, _committedTarget.transform.position);
            
            Vector3 moveDir;
            if (distToTarget < optimalRange)
            {
                // Too close - back away
                moveDir = (_transform.position - _committedTarget.transform.position).normalized;
            }
            else
            {
                // Too far - move closer
                moveDir = (_committedTarget.transform.position - _transform.position).normalized;
            }
            moveDir.y = 0;
            
            if (_terrainAwareness != null)
            {
                moveDir = _terrainAwareness.GetSafeMovementDirection(moveDir);
            }
            
            _targetMoveDirection = moveDir;
            return moveDir;
        }
        
        #endregion
        
        #region Projectile Detection
        
        /// <summary>
        /// Checks if there's an incoming projectile that should trigger evasion.
        /// </summary>
        public bool IsProjectileIncoming()
        {
            var projectiles = Physics.OverlapSphere(
                _transform.position, 
                ProjectileDetectionRange, 
                LayerMask.GetMask("projectile", "piece_nonsolid"));
            
            foreach (var collider in projectiles)
            {
                var proj = collider.GetComponent<Projectile>();
                if (proj != null)
                {
                    Vector3 toUs = (_transform.position - proj.transform.position).normalized;
                    float dot = Vector3.Dot(proj.transform.forward, toUs);
                    
                    if (dot > 0.7f)
                    {
                        float dist = Vector3.Distance(_transform.position, proj.transform.position);
                        if (dist < 5f)
                            return true;
                    }
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Target Analysis
        
        /// <summary>
        /// Checks if the target is fleeing (moving away from us).
        /// </summary>
        public bool IsTargetFleeing()
        {
            if (_committedTarget == null) return false;
            
            var velocity = _committedTarget.GetVelocity();
            if (velocity.magnitude < 0.5f) return false;
            
            Vector3 toUs = (_transform.position - _committedTarget.transform.position).normalized;
            float dot = Vector3.Dot(velocity.normalized, toUs);
            return dot < -0.5f;
        }
        
        /// <summary>
        /// Counts nearby enemies within range.
        /// </summary>
        public int CountNearbyEnemies(float range)
        {
            int count = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_transform.position, character.transform.position);
                if (dist <= range)
                {
                    count++;
                }
            }
            return count;
        }
        
        #endregion
        
        #region Owner Distance Checking
        
        /// <summary>
        /// Gets the current distance to the owner.
        /// Returns float.MaxValue if no owner.
        /// </summary>
        public float GetDistanceToOwner()
        {
            var owner = _companion?.GetOwner();
            if (owner == null) return float.MaxValue;
            return Vector3.Distance(_transform.position, owner.transform.position);
        }
        
        /// <summary>
        /// Checks if we're too far from the owner and should abandon combat pursuit.
        /// ALL archetypes use the same distance - tanks protect the group by staying CLOSE.
        /// </summary>
        public bool IsTooFarFromOwner()
        {
            float dist = GetDistanceToOwner();
            return dist > MaxOwnerDistance;
        }
        
        /// <summary>
        /// Checks if we're at the leash distance and should avoid moving further from owner.
        /// </summary>
        public bool IsAtLeashDistance()
        {
            float dist = GetDistanceToOwner();
            return dist > OwnerLeashDistance;
        }
        
        /// <summary>
        /// Checks if we should actively return to the owner.
        /// </summary>
        public bool ShouldReturnToOwner()
        {
            float dist = GetDistanceToOwner();
            return dist > OwnerReturnDistance;
        }
        
        /// <summary>
        /// Checks if moving in a direction would take us further from the owner.
        /// </summary>
        public bool WouldMoveFurtherFromOwner(Vector3 moveDirection)
        {
            var owner = _companion?.GetOwner();
            if (owner == null) return false;
            
            Vector3 toOwner = (owner.transform.position - _transform.position).normalized;
            float dot = Vector3.Dot(moveDirection.normalized, toOwner);
            
            // Negative dot means moving away from owner
            return dot < -0.3f;
        }
        
        /// <summary>
        /// Checks if a target position would be too far from the owner.
        /// </summary>
        public bool WouldTargetBeTooFar(Vector3 targetPosition)
        {
            var owner = _companion?.GetOwner();
            if (owner == null) return false;
            
            float distFromOwner = Vector3.Distance(targetPosition, owner.transform.position);
            return distFromOwner > MaxOwnerDistance;
        }
        
        /// <summary>
        /// Checks if pursuing the current target would take us too far from owner.
        /// </summary>
        public bool WouldPursuitTakeTooFar()
        {
            if (_committedTarget == null) return false;
            
            var owner = _companion?.GetOwner();
            if (owner == null) return false;
            
            // Check where target is relative to owner
            float targetToOwner = Vector3.Distance(_committedTarget.transform.position, owner.transform.position);
            float currentToOwner = Vector3.Distance(_transform.position, owner.transform.position);
            
            // If target is further from owner than max distance, pursuing would take us too far
            return targetToOwner > MaxOwnerDistance;
        }
        
        /// <summary>
        /// Gets the direction to return to owner.
        /// </summary>
        public Vector3 GetDirectionToOwner()
        {
            var owner = _companion?.GetOwner();
            if (owner == null) return Vector3.zero;
            
            Vector3 toOwner = (owner.transform.position - _transform.position).normalized;
            toOwner.y = 0;
            return toOwner;
        }
        
        /// <summary>
        /// Evaluates if combat should continue based on owner distance.
        /// Returns a recommendation for how to handle combat movement.
        /// </summary>
        public OwnerDistanceRecommendation EvaluateOwnerDistance()
        {
            float dist = GetDistanceToOwner();
            
            if (dist > MaxOwnerDistance)
                return OwnerDistanceRecommendation.AbandonCombat;
            
            if (dist > OwnerReturnDistance)
                return OwnerDistanceRecommendation.ReturnToOwner;
            
            if (dist > OwnerLeashDistance)
                return OwnerDistanceRecommendation.StayNear;
            
            return OwnerDistanceRecommendation.ContinueCombat;
        }
        
        public enum OwnerDistanceRecommendation
        {
            ContinueCombat,   // Normal combat allowed
            StayNear,         // Avoid moving further from owner
            ReturnToOwner,    // Actively return to owner
            AbandonCombat     // Too far - drop target and return
        }
        
        #endregion
        
        #region Movement Blending
        
        /// <summary>
        /// Updates the current move direction by blending toward target.
        /// Call this every frame to get smooth movement transitions.
        /// </summary>
        public Vector3 UpdateBlendedMovement()
        {
            _currentMoveDirection = Vector3.Lerp(
                _currentMoveDirection, 
                _targetMoveDirection, 
                Time.deltaTime * MovementBlendSpeed);
            
            return _currentMoveDirection;
        }
        
        /// <summary>
        /// Resets movement direction tracking.
        /// </summary>
        public void ResetMovementTracking()
        {
            _currentMoveDirection = Vector3.zero;
            _targetMoveDirection = Vector3.zero;
            _committedMoveDirection = Vector3.zero;
        }
        
        #endregion
        
        #region Execute Movement Methods
        
        /// <summary>
        /// Result of executing a combat movement.
        /// </summary>
        public class MovementExecutionResult
        {
            public Vector3 MoveDirection { get; set; }
            public bool ShouldStop { get; set; }
            public bool DirectionChanged { get; set; }
            public bool StrafeDirectionFlipped { get; set; }
        }
        
        /// <summary>
        /// Executes strafe movement around the committed target.
        /// Respects owner distance - prefers strafing toward owner when at leash.
        /// </summary>
        public MovementExecutionResult ExecuteStrafe()
        {
            var result = new MovementExecutionResult();
            
            if (_committedTarget == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            // CHECK OWNER DISTANCE
            var ownerRec = EvaluateOwnerDistance();
            if (ownerRec >= OwnerDistanceRecommendation.ReturnToOwner)
            {
                // Too far - stop strafing and return to owner
                result.MoveDirection = GetDirectionToOwner();
                _targetMoveDirection = result.MoveDirection;
                
                if (VerboseLogging)
                    Debug.Log($"[CombatMovementHandler] Strafe abandoned - returning to owner ({GetDistanceToOwner():F1}m)");
                
                return result;
            }
            
            // Check if strafe commitment expired
            if (Time.time > _strafeCommitEndTime)
            {
                CommitToNewStrafe();
                result.DirectionChanged = true;
            }
            
            if (_strafeDirection == 0 || _character == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
            Vector3 strafeDir = Vector3.Cross(Vector3.up, toTarget) * _strafeDirection;
            
            // Check terrain safety
            if (_terrainAwareness != null && !_terrainAwareness.IsDirectionSafe(strafeDir))
            {
                _strafeDirection *= -1;
                strafeDir *= -1;
                result.StrafeDirectionFlipped = true;
                result.DirectionChanged = true;
            }
            
            // Check if strafing would take us too far or too close to TARGET
            float distToTarget = Vector3.Distance(_transform.position, _committedTarget.transform.position);
            Vector3 newPos = _transform.position + strafeDir * _character.m_walkSpeed * Time.deltaTime;
            float newDist = Vector3.Distance(newPos, _committedTarget.transform.position);
            
            if (newDist > MeleeStafeDistance + MaxStrafeDistance || newDist < 1.5f)
            {
                _strafeDirection *= -1;
                strafeDir *= -1;
                result.StrafeDirectionFlipped = true;
                result.DirectionChanged = true;
            }
            
            // NEW: If at leash distance, prefer strafing toward owner
            if (ownerRec == OwnerDistanceRecommendation.StayNear)
            {
                // Check if current strafe direction takes us away from owner
                if (WouldMoveFurtherFromOwner(strafeDir))
                {
                    // Flip strafe direction to go toward owner instead
                    _strafeDirection *= -1;
                    strafeDir *= -1;
                    result.StrafeDirectionFlipped = true;
                    result.DirectionChanged = true;
                    
                    if (VerboseLogging)
                        Debug.Log($"[CombatMovementHandler] Strafe flipped to stay near owner");
                }
            }
            
            _targetMoveDirection = strafeDir.normalized;
            result.MoveDirection = strafeDir.normalized;
            return result;
        }
        
        /// <summary>
        /// Executes retreat movement away from the committed target.
        /// </summary>
        public MovementExecutionResult ExecuteRetreat()
        {
            var result = new MovementExecutionResult();
            
            if (_committedTarget == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            if (_committedMoveDirection.sqrMagnitude > 0.1f)
            {
                Vector3 safeDir = _terrainAwareness != null
                    ? _terrainAwareness.GetSafeMovementDirection(_committedMoveDirection)
                    : _committedMoveDirection;
                
                _targetMoveDirection = safeDir;
                result.MoveDirection = safeDir;
            }
            else
            {
                result.ShouldStop = true;
            }
            
            return result;
        }
        
        /// <summary>
        /// Executes intercept movement to protect the owner.
        /// Note: Intercept naturally keeps us near owner since we're positioning between enemy and owner.
        /// </summary>
        public MovementExecutionResult ExecuteIntercept()
        {
            var result = new MovementExecutionResult();
            
            if (_committedTarget == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            var owner = _companion?.GetOwner();
            if (owner == null)
            {
                // No owner - let AI handle it
                result.ShouldStop = true;
                return result;
            }
            
            // Intercept position is between enemy and owner, so it naturally keeps us near owner
            Vector3 enemyPos = _committedTarget.transform.position;
            Vector3 ownerPos = owner.transform.position;
            Vector3 enemyToOwner = (ownerPos - enemyPos).normalized;
            
            // Position ourselves between enemy and owner, closer to the enemy
            Vector3 interceptPos = enemyPos + enemyToOwner * 3f;
            
            // But clamp the intercept position so we don't go too far from owner
            float interceptToOwner = Vector3.Distance(interceptPos, ownerPos);
            if (interceptToOwner > OwnerLeashDistance)
            {
                // Intercept position is too far - clamp it closer to owner
                Vector3 ownerToIntercept = (interceptPos - ownerPos).normalized;
                interceptPos = ownerPos + ownerToIntercept * OwnerLeashDistance;
            }
            
            Vector3 toIntercept = (interceptPos - _transform.position).normalized;
            
            _targetMoveDirection = toIntercept;
            result.MoveDirection = toIntercept;
            return result;
        }
        
        /// <summary>
        /// Executes repositioning movement for optimal range (primarily for ranged combat).
        /// Respects owner distance when repositioning.
        /// </summary>
        public MovementExecutionResult ExecuteReposition(float optimalRange = 12f)
        {
            var result = new MovementExecutionResult();
            
            if (_committedTarget == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            // Check owner distance first
            var ownerRec = EvaluateOwnerDistance();
            if (ownerRec >= OwnerDistanceRecommendation.ReturnToOwner)
            {
                // Too far - return to owner instead of repositioning
                result.MoveDirection = GetDirectionToOwner();
                _targetMoveDirection = result.MoveDirection;
                return result;
            }
            
            float distToTarget = Vector3.Distance(_transform.position, _committedTarget.transform.position);
            
            Vector3 moveDir;
            if (distToTarget < optimalRange)
            {
                // Too close - back away
                moveDir = (_transform.position - _committedTarget.transform.position).normalized;
                
                // But don't back away if it takes us further from owner at leash distance
                if (ownerRec == OwnerDistanceRecommendation.StayNear && WouldMoveFurtherFromOwner(moveDir))
                {
                    // Try to reposition laterally instead
                    Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
                    moveDir = Vector3.Cross(Vector3.up, toTarget); // Strafe instead
                    
                    // Check which lateral direction is toward owner
                    if (WouldMoveFurtherFromOwner(moveDir))
                        moveDir *= -1;
                }
            }
            else
            {
                // Too far - move closer (this naturally brings us to a good position)
                moveDir = (_committedTarget.transform.position - _transform.position).normalized;
                
                // But don't approach if target is too far from owner
                if (WouldPursuitTakeTooFar())
                {
                    result.ShouldStop = true;
                    return result;
                }
            }
            moveDir.y = 0;
            
            if (_terrainAwareness != null)
            {
                moveDir = _terrainAwareness.GetSafeMovementDirection(moveDir);
            }
            
            _targetMoveDirection = moveDir;
            result.MoveDirection = moveDir;
            return result;
        }
        
        /// <summary>
        /// Executes approach movement toward the committed target.
        /// Respects owner distance constraints - won't chase if it takes us too far.
        /// </summary>
        public MovementExecutionResult ExecuteApproach()
        {
            var result = new MovementExecutionResult();
            
            if (_committedTarget == null)
            {
                result.ShouldStop = true;
                return result;
            }
            
            // CHECK OWNER DISTANCE BEFORE PURSUING
            var ownerRec = EvaluateOwnerDistance();
            if (ownerRec == OwnerDistanceRecommendation.AbandonCombat)
            {
                // Too far from owner - don't pursue, return to owner instead
                result.MoveDirection = GetDirectionToOwner();
                _targetMoveDirection = result.MoveDirection;
                
                if (VerboseLogging)
                    Debug.Log($"[CombatMovementHandler] Approach abandoned - too far from owner ({GetDistanceToOwner():F1}m)");
                
                return result;
            }
            
            if (ownerRec == OwnerDistanceRecommendation.ReturnToOwner)
            {
                // Should return to owner - don't pursue further
                result.MoveDirection = GetDirectionToOwner();
                _targetMoveDirection = result.MoveDirection;
                
                if (VerboseLogging)
                    Debug.Log($"[CombatMovementHandler] Approach redirected to owner ({GetDistanceToOwner():F1}m)");
                
                return result;
            }
            
            Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
            toTarget.y = 0;
            
            // If at leash distance, check if pursuing would take us further from owner
            if (ownerRec == OwnerDistanceRecommendation.StayNear)
            {
                if (WouldMoveFurtherFromOwner(toTarget))
                {
                    // Target is leading us away from owner - don't pursue
                    // Instead, stay where we are or move toward owner
                    if (VerboseLogging)
                        Debug.Log($"[CombatMovementHandler] Approach blocked - would move further from owner");
                    
                    result.ShouldStop = true;
                    return result;
                }
            }
            
            if (toTarget.sqrMagnitude > 0.01f)
            {
                _targetMoveDirection = toTarget;
                result.MoveDirection = toTarget;
            }
            else
            {
                result.ShouldStop = true;
            }
            
            return result;
        }
        
        /// <summary>
        /// Executes chase movement for pursuing fleeing targets.
        /// Respects owner distance - won't chase forever.
        /// </summary>
        public MovementExecutionResult ExecuteChase()
        {
            // Check if chase would take us too far
            var ownerRec = EvaluateOwnerDistance();
            if (ownerRec >= OwnerDistanceRecommendation.ReturnToOwner)
            {
                // Don't chase - return to owner instead
                var result = new MovementExecutionResult();
                result.MoveDirection = GetDirectionToOwner();
                _targetMoveDirection = result.MoveDirection;
                
                if (VerboseLogging)
                    Debug.Log($"[CombatMovementHandler] Chase abandoned - returning to owner ({GetDistanceToOwner():F1}m)");
                
                return result;
            }
            
            // Also check if target itself is too far from owner
            if (WouldPursuitTakeTooFar())
            {
                var result = new MovementExecutionResult();
                result.ShouldStop = true;
                
                if (VerboseLogging)
                    Debug.Log($"[CombatMovementHandler] Chase abandoned - target too far from owner");
                
                return result;
            }
            
            return ExecuteApproach();
        }
        
        /// <summary>
        /// Sets the committed target for movement calculations.
        /// </summary>
        public void SetCommittedTarget(Character target)
        {
            _committedTarget = target;
        }
        
        /// <summary>
        /// Sets the committed movement direction (for external control).
        /// </summary>
        public void SetCommittedDirection(Vector3 direction)
        {
            _committedMoveDirection = direction;
            _targetMoveDirection = direction;
        }
        
        #endregion
    }
}
