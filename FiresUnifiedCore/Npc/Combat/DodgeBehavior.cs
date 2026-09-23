using UnityEngine;
using System.Collections;
using FiresCore.Npc.AI;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Dodging, strafing and tactical movement. A dodge always moves: away from ranged threats, sideways to flank
    /// melee, backward when there is no threat. Committed movement completes before the direction changes, and a
    /// ranged companion must shoot before it dodges again.
    /// </summary>
    public class DodgeBehavior
    {
        private CombatContext _context;
        private MonoBehaviour _owner;
        private ThreatAnalyzer _threatAnalyzer;
        private CombatExperience _combatExperience;
        private StaminaManager _staminaManager;

        // Single-writer: dodge/strafe is a brief tactical override that must beat CompanionCombatMovement
        // (which holds Combat authority and keeps it for its 2s lease even while it yields to a dodge).
        // We drive through UMA at Animation priority (80 > Combat 70) so dodge/strafe deterministically
        // preempts positioning for its committed window, then release hands the body back. Below
        // PlayerCommand (90) and Forced (100), so a command or teleport still overrides a dodge.
        private UnifiedMovementAuthority _uma;
        private bool _umaResolved;
        private const string DodgeAuthorityOwner = "CompanionDodge";

        // Dodge state
  private float _lastDodgeTime;
        private bool _isDodging;
        private Coroutine _dodgeCoroutine;
        private const float DodgeAnimationDuration = 0.6f;

      // After one dodge, must shoot before next dodge (ranged only)
        private bool _mustShootBeforeNextDodge = false;
  private float _postDodgeShootWindowEnd;
        private const float PostDodgeShootWindow = 3f;

        // Standard dodge cooldown - throttled to prevent spam rolling
        private const float DodgeCooldown = 3.0f;

   // Strafe state
        private bool _isStrafing;
        private float _strafeDirection;
private float _strafeTimer;
      private float _strafeDuration;
        private float _lastStrafeTime;
     private const float StrafeCooldown = 2f;
        private const float StrafeSpeed = 0.5f;

        // Ranged positioning constants
        private const float OptimalRangeMin = 10f;
        private const float OptimalRangeMax = 15f;
     private const float DangerRange = 5f;

    private BowBehavior _bowBehavior;

        // MOVEMENT THROTTLING
        private float _lastMovementChange;
        private const float MinMovementChangeInterval = 0.2f;
        private Vector3 _committedMoveDir;
        private float _movementCommitEndTime;
        private bool _isMovementCommitted;
        
        // PROJECTILE DODGING
        private float _lastProjectileScan;
        private const float ProjectileScanInterval = 0.15f;
        private const float ProjectileDetectRange = 25f;
        private const float ProjectileDodgeTime = 0.6f; // Dodge if projectile arrives within this time
        private Projectile _incomingProjectile;
        private Vector3 _projectileDirection;
        private float _projectileTimeToImpact;

    public enum MovementState
     {
          Idle,
   PlantedFiring,
     Repositioning,
        Retreating,
            Approaching,
        Strafing,
      Dodging,
            PostDodgeShoot
   }

        private MovementState _currentState = MovementState.Idle;
        private float _stateEnterTime;
     private float _reassessTimer;
    private const float ReassessDuration = 0.2f;

        private Vector3 _repositionTarget;
        private bool _isRepositioning;

        public void Initialize(CombatContext context, MonoBehaviour owner)
        {
            _context = context;
            _owner = owner;
            _threatAnalyzer = owner.GetComponent<ThreatAnalyzer>();
            _combatExperience = owner.GetComponent<CombatExperience>();
            _staminaManager = owner.GetComponent<StaminaManager>();
        }

   public void SetBowBehavior(BowBehavior bowBehavior)
        {
            _bowBehavior = bowBehavior;
        }

      public bool IsDodging => _isDodging;
        public bool IsStrafing => _isStrafing;
        public bool IsRepositioning => _isRepositioning;
        public MovementState CurrentState => _currentState;

     /// <summary>
        /// Returns true if we're in the post-dodge state where we MUST shoot.
   /// </summary>
 public bool MustShootNow => _mustShootBeforeNextDodge && Time.time < _postDodgeShootWindowEnd;

        /// <summary>
        /// Check if we can dodge. For ranged, we can't dodge if we haven't shot since last dodge.
        /// SURVIVAL INSTINCT: Low stamina = don't dodge, retreat instead.
        /// </summary>
        public bool CanDodge(bool isRanged)
        {
            if (_isDodging) return false;
            if (Time.time - _lastDodgeTime < DodgeCooldown) return false;

            if (isRanged && _mustShootBeforeNextDodge)
            {
                return false;
            }

            // Check StaminaManager - SURVIVAL INSTINCT
            if (_staminaManager != null)
            {
                // SURVIVAL INSTINCT: Treat low stamina like low health
                // Don't dodge when stamina is critical - just retreat
                if (_staminaManager.ShouldTreatAsLowHealth())
                {
                    return false;
                }
                
                // During critical recovery - NO dodging
                if (_staminaManager.IsInCriticalRecovery())
                {
                    return false;
                }
                
                // During normal recovery - NO dodging (save stamina for regen)
                if (_staminaManager.IsRecovering())
                {
                    return false;
                }
                
                // Check if StaminaManager allows the dodge
                if (!_staminaManager.CanDodge())
                {
                    return false;
                }
            }

            // Fallback: Check CompanionStats if StaminaManager not available
            var stats = _context?.Companion?.GetStats();
            if (stats != null && !stats.HasStamina(GetDodgeStaminaCost()))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Gets the stamina cost for dodging.
        /// </summary>
        private float GetDodgeStaminaCost()
        {
            // Base dodge cost - similar to player dodge
            float baseCost = 15f;
            
            // Reduce cost based on skill
            var skills = _context?.Companion?.GetSkills();
            if (skills != null)
            {
                float skillFactor = skills.GetSkillFactor(Skills.SkillType.Jump);
                baseCost *= (1f - skillFactor * 0.25f); // Up to 25% reduction at max skill
            }

            var stats = _context?.Companion?.GetStats();
            return stats != null ? stats.ModifyDodgeStaminaCost(baseCost) : baseCost;
        }

    /// <summary>
        /// Called when a ranged attack completes - allows dodging again.
        /// </summary>
        public void NotifyRangedAttackComplete()
        {
            _mustShootBeforeNextDodge = false;

            if (CompanionCombat.VerboseLogging)
    Debug.Log("[DodgeBehavior] Shot fired - dodge unlocked");
   }

  public void Update(bool isAttacking, CompanionCombat.WeaponType weaponType, System.Action cancelBowDraw = null)
        {
            UpdateStateTimers();
            UpdateMovementCommitment();

            if (_isDodging) return;
            
            // ==========================================
            // SURVIVAL INSTINCT: Low stamina = DO ABSOLUTELY NOTHING
            // No dodging, no strafing, no repositioning, no facing
            // Just let CompanionCombatMovement handle the retreat
            // ==========================================
            if (_staminaManager != null)
            {
                // CRITICAL: In critical recovery - complete shutdown
                if (_staminaManager.IsInCriticalRecovery() || _staminaManager.ShouldTreatAsLowHealth())
                {
                    // Stop everything
                    StopStrafing();
                    StopMovement();
                    _isRepositioning = false;
                    EnterState(MovementState.Idle);
                    
                    // DO NOT face target, DO NOT do anything - just let retreat happen
                    return;
                }
                
                // In normal recovery - also do nothing, let stamina regen
                if (_staminaManager.IsRecovering())
                {
                    StopStrafing();
                    StopMovement();
                    _isRepositioning = false;
                    EnterState(MovementState.Idle);
                    return;
                }
            }
            
            // CHECK FOR INCOMING PROJECTILES - only if NOT in recovery
            if (!isAttacking && CanDodge(false))
            {
                if (ScanForIncomingProjectiles())
                {
                    ExecuteProjectileDodge();
                    return;
                }
            }

            var target2 = _context.CompanionAI?.GetTargetCreature();
            if (target2 == null)
            {
                EnterState(MovementState.Idle);
                _isRepositioning = false;
                return;
            }

            float distToTarget = Vector3.Distance(_context.Transform.position, target2.transform.position);
            bool isRanged = weaponType == CompanionCombat.WeaponType.Bow || weaponType == CompanionCombat.WeaponType.Crossbow;

            if (isRanged)
            {
                UpdateRangedBehavior(target2, distToTarget, isAttacking);
            }
            else
            {
                UpdateMeleeBehavior(target2, distToTarget, isAttacking);
            }
        }

        private void UpdateMovementCommitment()
        {
            if (_isMovementCommitted && Time.time >= _movementCommitEndTime)
   {
   _isMovementCommitted = false;
     }
      }

  private bool CanChangeMovement()
        {
 if (_isMovementCommitted) return false;
     if (Time.time - _lastMovementChange < MinMovementChangeInterval) return false;
     return true;
        }

        private void CommitMovement(Vector3 moveDir, float duration)
        {
            _committedMoveDir = moveDir;
            _isMovementCommitted = true;
            _movementCommitEndTime = Time.time + duration;
            _lastMovementChange = Time.time;

            // Single-writer: drive the committed dodge/strafe through UMA at Animation priority so it
            // preempts CompanionCombatMovement for the window, instead of a raw SetMoveDir that races it.
            var authority = GetAuthority();
            if (authority != null)
            {
                if (authority.TryAcquireAuthority(UnifiedMovementAuthority.MovementSource.Animation, DodgeAuthorityOwner, Mathf.Max(duration, 0.5f)))
                    authority.SetMoveDirection(DodgeAuthorityOwner, moveDir, walk: false, run: true);
                return;
            }

            // No-UMA fallback (kinematic-guarded, matches the old behavior).
            var body = _owner?.GetComponent<Rigidbody>();
            if (_context.Character != null && (body == null || !body.isKinematic))
            {
                _context.Character.SetMoveDir(moveDir);
            }
        }

        /// <summary>Lazily resolves the companion's movement authority (lives on the same GameObject as
        /// the owning CompanionCombat / the Character). Cached after first lookup.</summary>
        private UnifiedMovementAuthority GetAuthority()
        {
            if (!_umaResolved)
            {
                _umaResolved = true;
                _uma = _owner != null ? _owner.GetComponent<UnifiedMovementAuthority>() : null;
                if (_uma == null && _context?.Character != null)
                    _uma = _context.Character.GetComponent<UnifiedMovementAuthority>();
            }
            return _uma;
        }

        private void UpdateStateTimers()
        {
    if (_currentState == MovementState.Repositioning && _isMovementCommitted)
            {
      _reassessTimer += Time.deltaTime;
 if (_reassessTimer >= ReassessDuration)
             {
         _reassessTimer = 0f;
    }
      }

            if (_isStrafing)
       {
        _strafeTimer += Time.deltaTime;
           if (_strafeTimer >= _strafeDuration)
         {
           StopStrafing();
              }
    }
        }

        private void EnterState(MovementState newState)
        {
       if (_currentState == newState) return;

 if (_currentState == MovementState.Strafing)
  {
    StopStrafing();
            }

            _currentState = newState;
            _stateEnterTime = Time.time;
       _reassessTimer = 0f;

         if (CompanionCombat.VerboseLogging)
    {
    Debug.Log($"[DodgeBehavior] State changed to: {newState}");
            }
      }

        private void UpdateRangedBehavior(Character target, float distToTarget, bool isAttacking)
        {
            // CRITICAL: For ranged companions, DodgeBehavior should NOT control movement or trigger dodges
            // The retreat system in CompanionCombatMovement handles stamina recovery retreat
            // BowBehavior handles kiting and repositioning
            // We only handle the post-dodge shoot state here for crossbow compatibility
            
            // POST-DODGE SHOOT STATE: We just dodged, now we MUST shoot (crossbow only)
            if (_currentState == MovementState.PostDodgeShoot || _mustShootBeforeNextDodge)
            {
                EnterState(MovementState.PostDodgeShoot);
                FaceTarget(target);
                StopMovement();
                _isRepositioning = false;

                if (Time.time >= _postDodgeShootWindowEnd)
                {
                    _mustShootBeforeNextDodge = false;
                    EnterState(MovementState.Idle);
                }
                return;
            }
            
            // For ranged companions, just face target and let other systems handle movement
            // Do NOT trigger dodges or control repositioning - that causes conflicts
            FaceTarget(target);
            EnterState(MovementState.Idle);
            _isRepositioning = false;
        }

        private void RepositionAwayFromTarget(Character target, float desiredDistance)
{
            EnterState(MovementState.Repositioning);
        _isRepositioning = true;

         Vector3 dirFromTarget = (_context.Transform.position - target.transform.position).normalized;
            dirFromTarget.y = 0;

            if (dirFromTarget.sqrMagnitude < 0.01f)
            {
    // Fallback: move backward from current facing
     dirFromTarget = -_context.Transform.forward;
}

 _repositionTarget = target.transform.position + dirFromTarget * desiredDistance;
            FaceTarget(target);

            Vector3 moveDir = dirFromTarget * 0.7f;
 CommitMovement(moveDir, 0.5f);
        }

    private void RepositionTowardTarget(Character target, float desiredDistance)
        {
    EnterState(MovementState.Repositioning);
            _isRepositioning = true;

   Vector3 dirToTarget = (target.transform.position - _context.Transform.position).normalized;
   dirToTarget.y = 0;

          if (dirToTarget.sqrMagnitude < 0.01f)
         {
      // Fallback: move forward from current facing
                dirToTarget = _context.Transform.forward;
            }

   _repositionTarget = target.transform.position - dirToTarget * desiredDistance;
FaceTarget(target);

    Vector3 moveDir = dirToTarget * 0.5f;
            CommitMovement(moveDir, 0.5f);
        }

        private void StopMovement()
        {
            _isRepositioning = false;

            if (_isMovementCommitted)
            {
                // Single-writer: release our Animation-priority slot whenever a movement is committed
                // (even one that resolved to a zero vector — otherwise the slot leaks and CombatMovement
                // is denied for the lease window). ReleaseAuthority does a clean StopMovementImmediate.
                var authority = GetAuthority();
                if (authority != null)
                {
                    authority.ReleaseAuthority(DodgeAuthorityOwner);
                }
                else
                {
                    var body = _owner?.GetComponent<Rigidbody>();
                    if (_context.Character != null && (body == null || !body.isKinematic))
                        _context.Character.SetMoveDir(Vector3.zero);
                }
                _isMovementCommitted = false;
            }
        }

        private void UpdateMeleeBehavior(Character target, float distToTarget, bool isAttacking)
        {
            if (isAttacking) return;

            // Get level-based combat modifiers
            float levelDodgeBonus = 0f;
            if (_combatExperience != null)
            {
                levelDodgeBonus = _combatExperience.GetDodgeChanceAgainst(target) - _combatExperience.baseDodgeChance;
            }

            // Get threat modifiers from analyzer
            float dodgeChanceModifier = 1f;
            bool shouldDodgeMore = false;
            
            if (_threatAnalyzer != null)
            {
                var situation = _threatAnalyzer.GetCurrentSituation();
                dodgeChanceModifier = situation.DodgeChanceModifier;
                shouldDodgeMore = situation.ShouldDodgeMore;
                
                // Get enemy-specific analysis
                var profile = _threatAnalyzer.GetThreatProfile(target);
                
                // Against bosses/elites, dodge more aggressively
                if (profile.IsBoss || profile.Classification >= ThreatAnalyzer.EnemyClass.Elite)
                {
                    dodgeChanceModifier *= 1.5f;
                    shouldDodgeMore = true;
                }
                
                // Against dangerous enemies with high damage, dodge more
                if (profile.Classification >= ThreatAnalyzer.EnemyClass.Dangerous)
                {
                    dodgeChanceModifier *= 1.25f;
                }
            }

            // Check for incoming attack - consider dodging
            if (distToTarget < _context.DodgeThreatRange)
            {
                if (target.InAttack())
                {
                    // Base dodge chance + level bonus, modified by threat analysis
                    float effectiveDodgeChance = (_context.DodgeChance + levelDodgeBonus) * dodgeChanceModifier;
                    
                    // In defensive/survival mode, always try to dodge attacks
                    if (shouldDodgeMore)
                    {
                        effectiveDodgeChance = Mathf.Max(effectiveDodgeChance, 0.6f);
                    }
                    
                    if (Random.value < effectiveDodgeChance && CanDodge(false))
                    {
                        ExecuteDodge(target);
                        _combatExperience?.OnDodgeAttempt(true);
                        return;
                    }
                }

                // Strafe when close but not dodging
                if (distToTarget < _context.AttackRange * 1.5f)
                {
                    if (!_isStrafing && Time.time - _lastStrafeTime > StrafeCooldown && CanChangeMovement())
                    {
                        StartStrafe(target, Random.Range(0.8f, 1.5f));
                    }
                }
            }

            if (_isStrafing)
            {
                UpdateStrafe(target);
            }
        }

        private void StartStrafe(Character target, float duration)
    {
         _isStrafing = true;
            _strafeTimer = 0f;
         _strafeDuration = duration;
    _lastStrafeTime = Time.time;

         // Choose a definite strafe direction
            _strafeDirection = Random.value > 0.5f ? 1f : -1f;

            // Try to flank - strafe toward the target's back
   Vector3 toTarget = (target.transform.position - _context.Transform.position).normalized;
            Vector3 targetForward = target.transform.forward;
            float angle = Vector3.SignedAngle(toTarget, targetForward, Vector3.up);

            if (Mathf.Abs(angle) < 45f)
      {
     _strafeDirection = angle > 0 ? 1f : -1f;
       }

        EnterState(MovementState.Strafing);

            if (CompanionCombat.VerboseLogging)
    {
      Debug.Log($"[DodgeBehavior] Started strafe: direction={_strafeDirection}, duration={_strafeDuration:F1}s");
            }
        }

        private void UpdateStrafe(Character target)
        {
   if (!_isStrafing || target == null) return;

         FaceTarget(target);

            Vector3 strafeDir = _context.Transform.right * _strafeDirection;
            Vector3 moveDir = strafeDir * StrafeSpeed;

       if (CanChangeMovement())
      {
 CommitMovement(moveDir, 0.3f);
    }
     }

        private void StopStrafing()
        {
            _isStrafing = false;
            _strafeTimer = 0f;

            // Single-writer: release our Animation-priority slot (clean stop via the single writer).
            var authority = GetAuthority();
            if (authority != null)
            {
                authority.ReleaseAuthority(DodgeAuthorityOwner);
            }
            else
            {
                var body = _owner?.GetComponent<Rigidbody>();
                if (_context.Character != null && (body == null || !body.isKinematic))
                    _context.Character.SetMoveDir(Vector3.zero);
            }
            _isMovementCommitted = false;
        }

     private void FaceTarget(Character target)
        {
            if (target == null) return;

            Vector3 dirToTarget = target.transform.position - _context.Transform.position;
            dirToTarget.y = 0;
            if (dirToTarget.sqrMagnitude < 0.0001f) return;

            // Single facing-writer: face the threat through the FacingAuthority at Animation priority
            // (the band the dodge drives at). Short lease so it hands back to AI enemy-facing fast when
            // the dodge ends. If a same-priority incumbent holds facing, park — never raw-write here.
            var facing = _context?.Companion != null ? _context.Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(UnifiedMovementAuthority.MovementSource.Animation, DodgeAuthorityOwner, 0.4f))
                    facing.SetLookTarget(DodgeAuthorityOwner, target.transform.position);
                return;
            }

            _context.Transform.rotation = Quaternion.Slerp(
                _context.Transform.rotation,
                Quaternion.LookRotation(dirToTarget.normalized),
                Time.deltaTime * 10f);
        }

        /// <summary>
        /// Execute a melee dodge - always to the SIDE.
        /// Uses CombatExperience for optimal direction at higher levels.
        /// </summary>
        public void ExecuteDodge(Character threat)
        {
            if (_isDodging) return;
            if (!CanDodge(false)) return;

            if (_context != null && !_context.TryLockAnimation("dodge", DodgeAnimationDuration, CombatContext.AnimationPriority.Dodge))
            {
                return;
            }

            // Consume stamina for dodge - notify StaminaManager
            _staminaManager?.OnDodgePerformed();
            
            // Also update CompanionStats for backward compatibility
            var stats = _context?.Companion?.GetStats();
            if (stats != null)
            {
                stats.UseStamina(GetDodgeStaminaCost());
            }

            _isDodging = true;
            _lastDodgeTime = Time.time;
            StopStrafing();
            StopMovement();

            // Calculate dodge direction - ALWAYS to the side or backward, NEVER in place
            Vector3 threatDir = (threat.transform.position - _context.Transform.position).normalized;
            threatDir.y = 0;

            Vector3 dodgeDir;

            // If threat direction is too small, use forward direction
            if (threatDir.sqrMagnitude < 0.01f)
            {
                threatDir = _context.Transform.forward;
            }

            bool threatAttackingUs = threat.InAttack();

            if (threatAttackingUs)
            {
                // Dodge to the side or backward when enemy is attacking
                float rand = Random.value;
                if (rand < 0.4f)
                {
                    dodgeDir = Vector3.Cross(threatDir, Vector3.up).normalized;
                }
                else if (rand < 0.8f)
                {
                    dodgeDir = -Vector3.Cross(threatDir, Vector3.up).normalized;
                }
                else
                {
                    dodgeDir = -threatDir;
                }
                
                // At higher levels, use CombatExperience for optimal direction
                if (_combatExperience != null)
                {
                    dodgeDir = _combatExperience.GetOptimalDodgeDirection(threat, dodgeDir);
                }
            }
            else
            {
                // Strafe dodge when enemy isn't attacking
                float side = Random.value > 0.5f ? 1f : -1f;
                dodgeDir = Vector3.Cross(threatDir, Vector3.up).normalized * side;
            }

            // Ensure we have a valid direction
            if (dodgeDir.sqrMagnitude < 0.01f)
            {
                dodgeDir = -_context.Transform.forward;
            }

            PlayDodgeAnimation();
            ApplyDodgeForce(dodgeDir, 3f);

            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[DodgeBehavior] Melee dodge from {threat.m_name}, direction: {dodgeDir}");

            _dodgeCoroutine = _owner.StartCoroutine(FinishDodgeCoroutine(DodgeAnimationDuration));
        }

     /// <summary>
        /// Execute a ranged retreat dodge - ALWAYS directly away from threat.
        /// </summary>
   public void ExecuteRetreatDodge(Character threat, bool isEmergency = false)
{
            if (_isDodging) return;
        if (!CanDodge(true) && !isEmergency) return;

            var priority = isEmergency ?
         CombatContext.AnimationPriority.EmergencyDodge :
        CombatContext.AnimationPriority.Dodge;

       if (_context != null && !_context.TryLockAnimation("dodge", DodgeAnimationDuration, priority))
            {
     if (CompanionCombat.VerboseLogging)
           Debug.Log($"[DodgeBehavior] Retreat dodge blocked by animation lock");
       return;
            }

            // Consume stamina for dodge - notify StaminaManager
            _staminaManager?.OnDodgePerformed();
            
            // Also update CompanionStats for backward compatibility
            var stats = _context?.Companion?.GetStats();
            if (stats != null)
            {
                stats.UseStamina(GetDodgeStaminaCost());
            }

            _isDodging = true;
            _lastDodgeTime = Time.time;
            StopStrafing();
            StopMovement();

            // Calculate direction AWAY from threat
    Vector3 threatDir = (threat.transform.position - _context.Transform.position).normalized;
            Vector3 dodgeDir = -threatDir;
   dodgeDir.y = 0;

    // Ensure valid direction
            if (dodgeDir.sqrMagnitude < 0.01f)
    {
           dodgeDir = -_context.Transform.forward;
            }
       dodgeDir.Normalize();

       // Lock out further dodges until we shoot
            _mustShootBeforeNextDodge = true;
      _postDodgeShootWindowEnd = Time.time + DodgeAnimationDuration + PostDodgeShootWindow;

        PlayDodgeAnimation();
            ApplyDodgeForce(dodgeDir, 5f);

            if (CompanionCombat.VerboseLogging)
            Debug.Log($"[DodgeBehavior] Ranged retreat dodge from {threat.m_name}, direction: {dodgeDir}");

            _dodgeCoroutine = _owner.StartCoroutine(FinishDodgeWithShootRequirementCoroutine(DodgeAnimationDuration));
        }

        private void PlayDodgeAnimation()
      {
        if (_context.ZAnim != null)
  {
         _context.ZAnim.SetTrigger("dodge");
          }
    else if (_context.Animator != null && _context.HasAnimatorParameter("dodge"))
       {
           _context.Animator.SetTrigger("dodge");
         }
        }

        private void ApplyDodgeForce(Vector3 direction, float multiplier)
        {
            if (_context.Character == null) return;

            // Ensure direction is valid and normalized
            if (direction.sqrMagnitude < 0.01f)
            {
                Debug.LogWarning("[DodgeBehavior] Dodge direction is zero! Using backward direction.");
                direction = -_context.Transform.forward;
            }
            direction.Normalize();

            Vector3 dodgeVelocity = direction * _context.DodgeDistance * multiplier;
            var body = _owner.GetComponent<Rigidbody>();
            
            // Only apply force if rigidbody is NOT kinematic
            // Unity 6 doesn't allow AddForce on kinematic bodies
            if (body != null && !body.isKinematic)
            {
                body.AddForce(dodgeVelocity, ForceMode.VelocityChange);
            }
        }

        private IEnumerator FinishDodgeCoroutine(float delay)
        {
        yield return new WaitForSeconds(delay);
   _isDodging = false;
    _dodgeCoroutine = null;
      _context?.ReleaseAnimationLock();
        }

    private IEnumerator FinishDodgeWithShootRequirementCoroutine(float delay)
        {
        yield return new WaitForSeconds(delay);
            _isDodging = false;
       _dodgeCoroutine = null;
     _context?.ReleaseAnimationLock();

   EnterState(MovementState.PostDodgeShoot);
        _isRepositioning = false;

      if (CompanionCombat.VerboseLogging)
     Debug.Log("[DodgeBehavior] Dodge complete - entering MUST SHOOT state");
        }

        public bool IsInMustShootState()
        {
         return _mustShootBeforeNextDodge;
        }

        public void ClearMustShootState()
        {
    _mustShootBeforeNextDodge = false;
        }
        
        /// <summary>
        /// Scans for incoming projectiles that we should dodge.
        /// Returns true if a dangerous projectile is detected.
        /// </summary>
        private bool ScanForIncomingProjectiles()
        {
            if (Time.time - _lastProjectileScan < ProjectileScanInterval)
            {
                // Use cached result
                return _incomingProjectile != null && _projectileTimeToImpact < ProjectileDodgeTime;
            }
            _lastProjectileScan = Time.time;
            _incomingProjectile = null;
            _projectileTimeToImpact = float.MaxValue;
            
            // Find all projectiles in range
            var colliders = Physics.OverlapSphere(_context.Transform.position, ProjectileDetectRange);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var projectile = collider.GetComponent<Projectile>();
                if (projectile == null) continue;
                
                // Check if projectile is heading toward us
                var body = projectile.GetComponent<Rigidbody>();
                if (body == null) continue;
                
                Vector3 projectilePos = projectile.transform.position;
                Vector3 projectileVel = body.linearVelocity;
                
                if (projectileVel.sqrMagnitude < 1f) continue; // Not moving fast enough
                
                // Calculate if projectile will hit us
                Vector3 toUs = _context.Transform.position - projectilePos;
                float distToUs = toUs.magnitude;
                
                // Check if projectile is coming toward us
                float dot = Vector3.Dot(projectileVel.normalized, toUs.normalized);
                if (dot < 0.6f) continue; // Not heading our way
                
                // Estimate time to impact
                float speed = projectileVel.magnitude;
                float timeToImpact = distToUs / speed;
                
                // Check if this is the most imminent threat
                if (timeToImpact < _projectileTimeToImpact && timeToImpact < ProjectileDodgeTime)
                {
                    // Verify it will actually hit us (within ~2m)
                    Vector3 impactPoint = projectilePos + projectileVel * timeToImpact;
                    float impactDist = Vector3.Distance(impactPoint, _context.Transform.position);
                    
                    if (impactDist < 2.5f)
                    {
                        _incomingProjectile = projectile;
                        _projectileDirection = projectileVel.normalized;
                        _projectileTimeToImpact = timeToImpact;
                    }
                }
            }
            
            return _incomingProjectile != null && _projectileTimeToImpact < ProjectileDodgeTime;
        }
        
        /// <summary>
        /// Execute a dodge to avoid an incoming projectile.
        /// Dodges perpendicular to the projectile's path.
        /// </summary>
        private void ExecuteProjectileDodge()
        {
            if (_isDodging) return;
            if (_incomingProjectile == null) return;
            
            if (_context != null && !_context.TryLockAnimation("dodge", DodgeAnimationDuration, CombatContext.AnimationPriority.Dodge))
            {
                return;
            }
            
            // Consume stamina for dodge - notify StaminaManager
            _staminaManager?.OnDodgePerformed();
            
            // Also update CompanionStats for backward compatibility
            var stats = _context?.Companion?.GetStats();
            if (stats != null)
            {
                stats.UseStamina(GetDodgeStaminaCost());
            }
            
            _isDodging = true;
            _lastDodgeTime = Time.time;
            StopStrafing();
            StopMovement();
            
            // Dodge perpendicular to projectile direction
            Vector3 perpendicular = Vector3.Cross(Vector3.up, _projectileDirection).normalized;
            
            // Randomly pick left or right
            if (Random.value < 0.5f)
            {
                perpendicular = -perpendicular;
            }
            
            // Ensure valid direction
            if (perpendicular.sqrMagnitude < 0.01f)
            {
                perpendicular = _context.Transform.right;
            }
            
            PlayDodgeAnimation();
            ApplyDodgeForce(perpendicular, 4f);

            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[DodgeBehavior] Dodging projectile! TimeToImpact: {_projectileTimeToImpact:F2}s, Direction: {perpendicular}");
            }
            
            _incomingProjectile = null;
            _dodgeCoroutine = _owner.StartCoroutine(FinishDodgeCoroutine(DodgeAnimationDuration));
        }
  }
}
