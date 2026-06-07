using UnityEngine;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles close-range combat tactics: strafe, retreat, intercept, emergency actions.
    /// Only active when within takeover distance (1.5m melee, 8m ranged).
    /// Beyond that distance, CompanionAI (via BaseAI pathfinding) handles movement.
    /// 
    /// PHILOSOPHY:
    /// BaseAI pathfinding is great at getting TO the enemy. We take over for the actual fighting.
    /// - Strafe to avoid attacks while staying in range
    /// - Retreat when overwhelmed or low stamina
    /// - Intercept enemies heading for owner
    /// - Emergency dodge/block for immediate threats
    /// </summary>
    public class CombatTacticsController
    {
    public enum CombatIntent
        {
            None,
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

     // Settings
 public float MeleeStafeDistance { get; set; } = 3f;
        public float StrafeCommitmentDuration { get; set; } = 1.0f;
        public float MaxStrafeDistance { get; set; } = 3f;
        public float ApproachCommitmentDuration { get; set; } = 1.5f;
        public float MaxCommitmentDuration { get; set; } = 4.0f;
        public float DamageReassessDelay { get; set; } = 0.2f;
        public float OwnerProtectionRadius { get; set; } = 15f;
        public float ProjectileDetectionRange { get; set; } = 10f;

        // State
        private CombatIntent _currentIntent = CombatIntent.None;
        private Character _committedTarget;
        private Vector3 _committedMoveDirection;
        private Vector3 _targetMoveDirection;
        private float _commitmentStartTime;
        private float _commitmentDuration;
        private bool _hasActiveCommitment = false;
      private float _lastReassessTime;
        private float _lastDamageTime = -10f;

        // Strafe state
        private int _strafeDirection;
        private float _strafeCommitEndTime;
      private bool _isStrafeCommitted = false;

        // References
        private readonly Transform _transform;
        private readonly Character _character;
     private readonly CompanionController _companion;
        private readonly EnemyAttackRecognition _attackRecognition;
    private readonly StaminaManager _staminaManager;
        private readonly TerrainAwareness _terrainAwareness;
        private readonly CompanionCombat _combat;

    public static bool VerboseLogging = false;

  // Public state
   public CombatIntent CurrentIntent => _currentIntent;
        public Character CommittedTarget => _committedTarget;
        public bool HasActiveCommitment => _hasActiveCommitment;
     public Vector3 TargetMoveDirection => _targetMoveDirection;
   public Vector3 CommittedMoveDirection => _committedMoveDirection;

        public CombatTacticsController(
       Transform transform,
            Character character,
      CompanionController companion,
    EnemyAttackRecognition attackRecognition,
      StaminaManager staminaManager,
            TerrainAwareness terrainAwareness,
          CompanionCombat combat)
        {
       _transform = transform;
          _character = character;
        _companion = companion;
          _attackRecognition = attackRecognition;
       _staminaManager = staminaManager;
            _terrainAwareness = terrainAwareness;
  _combat = combat;
      }

     /// <summary>
   /// Called when damage is taken - may trigger reassessment.
        /// </summary>
        public void OnDamageTaken(float damage, Character attacker)
  {
    _lastDamageTime = Time.time;
 _staminaManager?.ForceRecovery();

            if (attacker != null && attacker != _committedTarget && !attacker.IsDead())
  {
             float distToAttacker = Vector3.Distance(_transform.position, attacker.transform.position);
           float distToTarget = _committedTarget != null ?
  Vector3.Distance(_transform.position, _committedTarget.transform.position) : float.MaxValue;

    if (distToAttacker < distToTarget * 0.7f || _committedTarget == null || _committedTarget.IsDead())
{
          ForceTargetSwitch(attacker);
             }
       }

            _hasActiveCommitment = false;
        }

        /// <summary>
      /// Checks for and executes emergency actions (dodge/block).
      /// Returns true if an emergency action was taken.
   /// </summary>
      public bool CheckForEmergencyAction(System.Action<Vector3> onDodge, System.Action onBlock)
  {
   // Don't do emergency actions if we're still approaching
       if (_currentIntent == CombatIntent.Approach || 
  _currentIntent == CombatIntent.Chase ||
            _currentIntent == CombatIntent.Intercept)
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
          Vector3 dodgeDir = _attackRecognition.GetDodgeDirection();
if (_terrainAwareness != null)
    {
            dodgeDir = _terrainAwareness.GetSafeMovementDirection(dodgeDir);
  }
       
       SetIntent(CombatIntent.Dodge);
           _committedMoveDirection = dodgeDir;
          _targetMoveDirection = dodgeDir;
              _commitmentStartTime = Time.time;
              _commitmentDuration = 0.5f;
  _hasActiveCommitment = true;
     
 _staminaManager?.OnDodgePerformed();
       onDodge?.Invoke(dodgeDir);
          
             if (VerboseLogging)
              Debug.Log("[CombatTactics] Emergency dodge!");
        
         return true;
      }
  else if (_staminaManager.CanBlock())
       {
        ExecuteBlock(onBlock);
      return true;
 }
            }

if (_attackRecognition.ShouldBlockNow() && !_attackRecognition.ShouldDodgeNow())
            {
 if (_staminaManager == null || _staminaManager.CanBlock())
  {
  if (_combat != null && _combat.HasShieldEquipped())
         {
           ExecuteBlock(onBlock);
   return true;
      }
        }
      }

          return false;
        }

        private void ExecuteBlock(System.Action onBlock)
        {
            SetIntent(CombatIntent.Block);
 _commitmentStartTime = Time.time;
            _commitmentDuration = 1.0f;
      _hasActiveCommitment = true;

            _staminaManager?.OnBlockStarted();
            onBlock?.Invoke();

            if (VerboseLogging)
      Debug.Log("[CombatTactics] Emergency block!");
  }

    /// <summary>
        /// Checks if combat situation should be reassessed.
        /// </summary>
        public bool ShouldReassess()
        {
     if (_committedTarget == null || _committedTarget.IsDead())
        return true;

      if (_hasActiveCommitment && Time.time > _commitmentStartTime + _commitmentDuration)
       return true;

            if (_hasActiveCommitment && Time.time > _commitmentStartTime + MaxCommitmentDuration)
    return true;

      if (Time.time - _lastDamageTime < DamageReassessDelay && Time.time - _lastDamageTime > 0.05f)
     return true;

          if (IsProjectileIncoming())
            return true;

if (_attackRecognition != null)
        {
      var threat = _attackRecognition.GetCurrentThreat();
             if (threat.Level >= EnemyAttackRecognition.ThreatLevel.High)
             return true;
      }

            if (_combat != null && !_combat.IsAttacking())
            {
      if (_currentIntent == CombatIntent.Strafe || _currentIntent == CombatIntent.PlantedFiring)
                {
   if (Time.time - _lastReassessTime > 0.3f)
      return true;
        }
            }

            return false;
        }

      /// <summary>
        /// Reassesses combat and determines new intent.
        /// </summary>
        public void Reassess(Character target, float distToTarget)
        {
            _lastReassessTime = Time.time;

            if (_committedTarget == null || _committedTarget.IsDead())
            {
                _committedTarget = target;
            }

            if (_committedTarget == null) return;

            bool isRanged = _combat != null && _combat.IsRangedWeapon();
            bool isAttacking = _combat != null && (_combat.IsAttacking() || _combat.IsBowDrawing());

            var staminaRec = _staminaManager?.GetRecommendation() ?? StaminaManager.StaminaRecommendation.BalancedCombat;
            var threat = _attackRecognition?.GetCurrentThreat() ?? EnemyAttackRecognition.ThreatAssessment.Safe;

            CombatIntent newIntent;
            float commitDuration;

            // CRITICAL: Stamina depleted - must retreat to owner immediately
            if (staminaRec == StaminaManager.StaminaRecommendation.CriticalRetreat)
            {
                newIntent = CombatIntent.Retreat;
                commitDuration = ApproachCommitmentDuration * 2f;  // Longer retreat
                
                if (VerboseLogging)
                {
                    Debug.Log("[CombatTactics] CRITICAL stamina! Retreating to recover.");
                }
            }
            else if (threat.Level >= EnemyAttackRecognition.ThreatLevel.Critical)
            {
                newIntent = CombatIntent.Retreat;
                commitDuration = ApproachCommitmentDuration;
            }
            else if (staminaRec == StaminaManager.StaminaRecommendation.DefendOnly)
            {
                newIntent = CombatIntent.Retreat;
                commitDuration = ApproachCommitmentDuration;
            }
            else if (ShouldInterceptForOwner(_committedTarget))
            {
                newIntent = CombatIntent.Intercept;
                commitDuration = ApproachCommitmentDuration;
            }
            else if (isRanged)
            {
                (newIntent, commitDuration) = DetermineRangedIntent(distToTarget, isAttacking, staminaRec);
            }
            else
            {
                (newIntent, commitDuration) = DetermineMeleeIntent(distToTarget, isAttacking, staminaRec, threat);
            }

            CommitToIntent(newIntent, commitDuration);

            if (VerboseLogging)
            {
                Debug.Log($"[CombatTactics] Committed to {newIntent} for {commitDuration:F1}s (dist={distToTarget:F1})");
            }
        }

        private (CombatIntent intent, float duration) DetermineRangedIntent(
            float distToTarget, bool isAttacking, StaminaManager.StaminaRecommendation staminaRec)
        {
            const float DANGER_RANGE = 5f;
            const float OPTIMAL_MIN = 10f;
            const float OPTIMAL_MAX = 15f;

            // Critical stamina - retreat regardless of position
            if (staminaRec == StaminaManager.StaminaRecommendation.CriticalRetreat)
            {
                return (CombatIntent.Retreat, ApproachCommitmentDuration * 2f);
            }

            if (isAttacking)
            {
                return (CombatIntent.PlantedFiring, 2f);
            }

            if (distToTarget < DANGER_RANGE)
            {
                return (CombatIntent.Retreat, ApproachCommitmentDuration);
            }

            if (distToTarget >= OPTIMAL_MIN && distToTarget <= OPTIMAL_MAX)
            {
                if (staminaRec != StaminaManager.StaminaRecommendation.DefendOnly &&
                    staminaRec != StaminaManager.StaminaRecommendation.CriticalRetreat &&
                    (_attackRecognition?.IsSafeToAttack() ?? true))
                {
                    return (CombatIntent.PlantedFiring, 1.5f);
                }
                return (CombatIntent.Reposition, 1f);
            }

            if (distToTarget < OPTIMAL_MIN)
            {
                return (CombatIntent.Retreat, ApproachCommitmentDuration);
            }

            return (CombatIntent.Approach, ApproachCommitmentDuration);
        }

        private (CombatIntent intent, float duration) DetermineMeleeIntent(
            float distToTarget, bool isAttacking,
            StaminaManager.StaminaRecommendation staminaRec, 
            EnemyAttackRecognition.ThreatAssessment threat)
        {
            float attackRange = _combat?.attackRange ?? 2.5f;

            // Critical stamina - retreat regardless of situation
            if (staminaRec == StaminaManager.StaminaRecommendation.CriticalRetreat)
            {
                return (CombatIntent.Retreat, ApproachCommitmentDuration * 2f);
            }

            if (isAttacking)
            {
                return (CombatIntent.None, 0.5f);
            }

            // DefendOnly - strafe defensively but don't retreat unless necessary
            if (staminaRec == StaminaManager.StaminaRecommendation.DefendOnly)
            {
                if (distToTarget <= attackRange * 1.5f)
                {
                    return (CombatIntent.Retreat, ApproachCommitmentDuration);
                }
            }

            if (staminaRec == StaminaManager.StaminaRecommendation.CautiousAttack)
            {
                if (distToTarget <= attackRange * 1.2f)
                {
                    return (CombatIntent.Strafe, StrafeCommitmentDuration * 1.5f);
                }
            }

            if (IsTargetFleeing(_committedTarget) && distToTarget > attackRange * 1.5f)
            {
                return (CombatIntent.Chase, ApproachCommitmentDuration * 1.5f);
            }

            if (distToTarget <= attackRange * 1.2f)
            {
                if (threat.Level <= EnemyAttackRecognition.ThreatLevel.Low &&
                    staminaRec == StaminaManager.StaminaRecommendation.FullAggression)
                {
                    return (CombatIntent.Strafe, StrafeCommitmentDuration * 0.5f);
                }
                return (CombatIntent.Strafe, StrafeCommitmentDuration);
            }

            return (CombatIntent.Approach, ApproachCommitmentDuration);
        }

   private void CommitToIntent(CombatIntent intent, float duration)
  {
            SetIntent(intent);
      _commitmentStartTime = Time.time;
            _commitmentDuration = duration;
    _hasActiveCommitment = true;

        if (intent == CombatIntent.Strafe && !_isStrafeCommitted)
            {
    CommitToNewStrafe();
       }

     if (_committedTarget != null)
    {
     Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
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
   }

        private void SetIntent(CombatIntent newIntent)
        {
      _currentIntent = newIntent;
        }

        private void CommitToNewStrafe()
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
        /// Executes the current strafe movement.
/// </summary>
        public Vector3 ExecuteStrafe()
  {
    if (_committedTarget == null) return Vector3.zero;

 if (Time.time > _strafeCommitEndTime)
         {
      CommitToNewStrafe();
      }

          if (_strafeDirection != 0 && _character != null)
       {
        Vector3 toTarget = (_committedTarget.transform.position - _transform.position).normalized;
  Vector3 strafeDir = Vector3.Cross(Vector3.up, toTarget) * _strafeDirection;

     if (_terrainAwareness != null && !_terrainAwareness.IsDirectionSafe(strafeDir))
{
     _strafeDirection *= -1;
          strafeDir *= -1;
}

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

    return Vector3.zero;
}

 /// <summary>
        /// Executes retreat movement.
 /// </summary>
      public Vector3 ExecuteRetreat()
        {
  if (_committedTarget == null) return Vector3.zero;

        if (_committedMoveDirection.sqrMagnitude > 0.1f)
            {
                Vector3 safeDir = _terrainAwareness != null ?
          _terrainAwareness.GetSafeMovementDirection(_committedMoveDirection) :
         _committedMoveDirection;

    _targetMoveDirection = safeDir;
   return safeDir;
            }

      return Vector3.zero;
        }

     /// <summary>
    /// Executes intercept movement (positioning between enemy and owner).
        /// </summary>
     public Vector3 ExecuteIntercept()
 {
       if (_committedTarget == null) return Vector3.zero;

        var owner = _companion?.GetOwner();
            if (owner == null) return Vector3.zero;

      Vector3 enemyPos = _committedTarget.transform.position;
            Vector3 ownerPos = owner.transform.position;
       Vector3 enemyToOwner = (ownerPos - enemyPos).normalized;
            Vector3 interceptPos = enemyPos + enemyToOwner * 3f;
    Vector3 toIntercept = (interceptPos - _transform.position).normalized;

    _targetMoveDirection = toIntercept;
 return toIntercept;
     }

        /// <summary>
        /// Executes reposition movement (for ranged).
        /// </summary>
        public Vector3 ExecuteReposition()
    {
            if (_committedTarget == null) return Vector3.zero;

    float distToTarget = Vector3.Distance(_transform.position, _committedTarget.transform.position);
const float OPTIMAL_RANGE = 12f;

            Vector3 moveDir;
       if (distToTarget < OPTIMAL_RANGE)
{
      moveDir = (_transform.position - _committedTarget.transform.position).normalized;
            }
     else
            {
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

        /// <summary>
        /// Clears all combat commitments.
        /// </summary>
        public void ClearCommitment()
    {
     _hasActiveCommitment = false;
   _committedTarget = null;
            _isStrafeCommitted = false;
            _strafeDirection = 0;
         _currentIntent = CombatIntent.None;
       _staminaManager?.OnBlockEnded();
        }

        public void ForceTargetSwitch(Character newTarget)
        {
     _committedTarget = newTarget;
        _hasActiveCommitment = false;

      if (VerboseLogging)
   {
       Debug.Log($"[CombatTactics] Force-switched to {newTarget?.m_name}");
            }
        }

        public void SetTarget(Character target)
        {
          _committedTarget = target;
}

        private bool ShouldInterceptForOwner(Character enemy)
        {
if (enemy == null) return false;

       var owner = _companion?.GetOwner();
 if (owner == null) return false;

            var enemyAI = enemy.GetComponent<BaseAI>();
        if (enemyAI == null) return false;

      var enemyTarget = enemyAI.GetTargetCreature();
    if (enemyTarget == null) return false;

    var ownerCharacter = owner.GetComponent<Character>();
   if (enemyTarget != ownerCharacter) return false;

       float enemyToOwner = Vector3.Distance(enemy.transform.position, owner.transform.position);
            if (enemyToOwner > OwnerProtectionRadius) return false;

            float companionToOwner = Vector3.Distance(_transform.position, owner.transform.position);
   return enemyToOwner < companionToOwner;
        }

        private bool IsTargetFleeing(Character target)
      {
 if (target == null) return false;
      var velocity = target.GetVelocity();
         if (velocity.magnitude < 0.5f) return false;
            Vector3 toUs = (_transform.position - target.transform.position).normalized;
            float dot = Vector3.Dot(velocity.normalized, toUs);
            return dot < -0.5f;
 }

        private bool IsProjectileIncoming()
        {
      var projectiles = Physics.OverlapSphere(_transform.position, ProjectileDetectionRange, 
       LayerMask.GetMask("projectile", "piece_nonsolid"));

            foreach (var col in projectiles)
         {
  var proj = col.GetComponent<Projectile>();
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
    }
}
