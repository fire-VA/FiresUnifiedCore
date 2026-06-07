using UnityEngine;
using System.Collections;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Combat behavior for crossbows.
    /// Implements kiting similar to BowBehavior but with reload mechanics.
    /// 
    /// CROSSBOW VS BOW:
    /// - Crossbows have higher damage per shot but require reload
    /// - Reload happens after shooting, can't draw and hold
    /// - Best used by shooting, retreating while reloading, then planting again
    /// 
    /// KITING PATTERN:
    /// 1. If loaded: Plant, aim, fire
    /// 2. After shot: Retreat while reloading
    /// 3. Once reloaded: Plant and fire again
    /// 4. Emergency: Fire if loaded, then dodge
    /// </summary>
    public class CrossbowBehavior : WeaponBehavior
    {
     #region Enums
      
        public enum CrossbowPhase
        {
            Idle,
Approaching,
          Retreating,
     Planting,
            Aiming,
   Firing,
        Reloading,
            EmergencyDodge
        }
   
        public enum MovementRequest
 {
         None,
  Stop,
      RunAway,
     Approach,
        Strafe,
         Dodge
   }
     
        #endregion
        
        #region Constants
   
        private const float DANGER_RANGE = 5f;
        private const float CLOSE_RANGE = 10f;
      private const float OPTIMAL_MIN = 10f;
private const float OPTIMAL_MAX = 15f;
        private const float MAX_RANGE = 25f;
  private const float MIN_SHOT_INTERVAL = 0.3f;
        private const float PLANT_DURATION = 0.15f;
        private const float THREAT_CHECK_INTERVAL = 0.1f;
     
        #endregion
        
        #region State
        
      // Crossbow state
        private bool _isReloading;
        private float _reloadStartTime;
        private float _currentReloadDuration;
     private bool _isLoaded;
        private float _lastShotTime;
      
     // Kiting state
      private CrossbowPhase _currentPhase = CrossbowPhase.Idle;
   private MovementRequest _currentMovementRequest = MovementRequest.None;
     private float _phaseStartTime;
      private float _lastThreatCheck;

        // Line-of-sight reposition tracking ï¿½ see DecideNextAction.
        private const float LOS_REPOSITION_TIMEOUT = 4f;
        private Character _losTarget;
        private float _losBlockedSinceTime = -1f;

        // Target tracking
        private new Character _currentTarget;
        private float _lastKnownTargetDistance;
        private Vector3 _lastKnownTargetPosition;
        private Vector3 _lastKnownTargetVelocity;
     private bool _targetIsApproaching;
        
     // References
        private DodgeBehavior _dodgeBehavior;
        private bool _dodgeAfterShot;
        private Character _dodgeThreat;
        
   // Strafe
      private int _strafeDirection = 1;
        private float _lastStrafeChange;
     
     #endregion
        
        #region Properties
    
    public bool IsReloading => _isReloading;
   public bool IsLoaded => _isLoaded;
        public CrossbowPhase CurrentPhase => _currentPhase;
        public MovementRequest CurrentMovementNeed => _currentMovementRequest;
      
        /// <summary>
    /// Event for movement coordination with CompanionCombatMovement.
    /// </summary>
        public System.Action<MovementRequest, Vector3> OnMovementRequested;
        
      #endregion
        
        #region Initialization
        
        public override void Initialize(CombatContext context, MonoBehaviour owner)
  {
     base.Initialize(context, owner);
            _currentReloadDuration = context.DefaultCrossbowReloadTime;
 }
      
        public void SetDodgeBehavior(DodgeBehavior dodgeBehavior)
        {
       _dodgeBehavior = dodgeBehavior;
        }
     
        public override void OnActivate()
        {
        base.OnActivate();
            _isReloading = false;
            _isLoaded = false; // Need to reload when switching to crossbow
     _dodgeAfterShot = false;
            _dodgeThreat = null;
  _currentPhase = CrossbowPhase.Idle;
     _currentMovementRequest = MovementRequest.None;
        }
        
  public override void OnDeactivate()
  {
     _isReloading = false;
            _isLoaded = false;
            _dodgeAfterShot = false;
   _dodgeThreat = null;
        _currentPhase = CrossbowPhase.Idle;
   _currentMovementRequest = MovementRequest.None;
   base.OnDeactivate();
        }
        
        public override void ConfigureAI()
        {
            // CompanionAI handles combat behavior directly - no MonsterAI settings needed
            Context.AttackRange = MAX_RANGE;
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[CrossbowBehavior] Configured for kiting combat: range={MAX_RANGE}, reloadTime={_currentReloadDuration}");
            }
        }
        
        public override void UpdateAttackData()
        {
  base.UpdateAttackData();
          
            if (Context.CurrentAttack != null && Context.CurrentAttack.m_requiresReload && Context.CurrentAttack.m_reloadTime > 0)
  {
           _currentReloadDuration = Context.CurrentAttack.m_reloadTime;
            }
            else
            {
    _currentReloadDuration = Context.DefaultCrossbowReloadTime;
      }
      
            _effectiveCooldown = _currentReloadDuration * 0.5f + 0.3f;
      
            // Reset loaded state when weapon changes
    _isLoaded = false;
            _isReloading = false;
        }
        
    #endregion
        
        #region Update Loop
        
        public override void Update()
        {
            base.Update();
            
            var target = Context.CompanionAI?.GetTargetCreature();
            
            if (target == null || target.IsDead())
            {
      SetPhase(CrossbowPhase.Idle);
       RequestMovement(MovementRequest.None, Vector3.zero);
        _currentTarget = null;
      return;
            }
        
            _currentTarget = target;
            UpdateTargetTracking(target);
          
            // Check threats frequently
            if (Time.time - _lastThreatCheck >= THREAT_CHECK_INTERVAL)
            {
     _lastThreatCheck = Time.time;
     CheckThreatLevel(target);
  }
    
            // Update reload if active
    if (_isReloading)
     {
          UpdateReload();
    }
       
        // State machine
      UpdateCombatPhase(target);
   }
  
        private void UpdateTargetTracking(Character target)
        {
      Vector3 currentPos = target.transform.position;
            _lastKnownTargetVelocity = (currentPos - _lastKnownTargetPosition) / Mathf.Max(0.01f, Time.deltaTime);
   _lastKnownTargetPosition = currentPos;
            _lastKnownTargetDistance = Vector3.Distance(Context.Transform.position, currentPos);
          
  Vector3 toUs = (Context.Transform.position - currentPos).normalized;
       _targetIsApproaching = Vector3.Dot(_lastKnownTargetVelocity.normalized, toUs) > 0.3f;
        }
     
    private void CheckThreatLevel(Character target)
        {
      if (_lastKnownTargetDistance < DANGER_RANGE)
       {
        HandleDangerZone(target);
      }
        }
        
        private void HandleDangerZone(Character target)
      {
            if (_isLoaded && !_isAttacking)
 {
           // Fire immediately then dodge
        _dodgeAfterShot = true;
       _dodgeThreat = target;
             SetPhase(CrossbowPhase.Firing);
       ExecuteCrossbowShot(target);
     }
            else if (_currentPhase != CrossbowPhase.EmergencyDodge)
          {
                // Emergency retreat
      SetPhase(CrossbowPhase.EmergencyDodge);
              RequestMovement(MovementRequest.Dodge, -GetDirectionToTarget(target));
  _dodgeBehavior?.ExecuteRetreatDodge(target, true);
         }
        }
    
        private void UpdateCombatPhase(Character target)
        {
    float timeSincePhaseStart = Time.time - _phaseStartTime;
       
            switch (_currentPhase)
       {
  case CrossbowPhase.Idle:
      DecideNextAction(target);
      break;
     
            case CrossbowPhase.Approaching:
        UpdateApproaching(target);
           break;
        
   case CrossbowPhase.Retreating:
       UpdateRetreating(target);
 break;
   
       case CrossbowPhase.Planting:
          if (timeSincePhaseStart >= PLANT_DURATION)
       {
         if (_isLoaded)
            {
       SetPhase(CrossbowPhase.Aiming);
        }
    else if (!_isReloading)
 {
        StartReload();
  SetPhase(CrossbowPhase.Reloading);
      }
     }
   break;
    
                 case CrossbowPhase.Aiming:
           FaceTarget(target);
                if (timeSincePhaseStart >= 0.1f) // Brief aim time
               {
                             // LINE-OF-SIGHT ABORT ï¿½ don't fire into a wall if the
                             // target ducked behind cover after we entered Aiming.
                             if (Context.CompanionAI != null && !Context.CompanionAI.HasClearShotToCurrentTarget())
                             {
                                 if (CompanionCombat.VerboseLogging)
                                 {
                                     Debug.Log($"[CrossbowBehavior] Aborting shot ï¿½ target {target?.m_name} no longer has line-of-sight");
                                 }
                                 SetPhase(CrossbowPhase.Idle);
                                 break;
                             }

              SetPhase(CrossbowPhase.Firing);
                  ExecuteCrossbowShot(target);
                }
             break;
         
         case CrossbowPhase.Firing:
   if (!_isAttacking)
          {
           // After firing, start retreating while we reload
   SetPhase(CrossbowPhase.Retreating);
         RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
          if (!_isLoaded && !_isReloading)
          {
     StartReload();
 }
    }
    break;
      
                case CrossbowPhase.Reloading:
   UpdateReloadingPhase(target);
      break;
  
         case CrossbowPhase.EmergencyDodge:
               if (_dodgeBehavior == null || !_dodgeBehavior.IsDodging)
            {
              SetPhase(CrossbowPhase.Retreating);
               RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
           }
       break;
            }
      }
        
  #endregion
        
        #region Combat Decisions
        
    private void DecideNextAction(Character target)
    {
            if (Time.time - _lastShotTime < MIN_SHOT_INTERVAL)
        {
  return;
            }

             if (Context != null && Context.IsAnimationLocked)
   {
        return;
           }

            // ?? LINE-OF-SIGHT GATE ??????????????????????????????????????????
            // Same model as BowBehavior ï¿½ one raycast per engagement decision,
            // not per potential target in IsValidTarget.  No LOS triggers a
            // reposition attempt; if reposition fails after the timeout we
            // blacklist the target so AI re-acquires.
            if (Context.CompanionAI != null && !Context.CompanionAI.HasClearShotTo(target))
            {
                if (_losTarget != target)
                {
                    _losTarget = target;
                    _losBlockedSinceTime = Time.time;
                }

                float blockedFor = Time.time - _losBlockedSinceTime;
                if (blockedFor >= LOS_REPOSITION_TIMEOUT)
                {
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[CrossbowBehavior] No LOS to {target.m_name} for {blockedFor:F1}s ï¿½ blacklisting and re-acquiring");
                    }
                    Context.CompanionAI.BlacklistTargetForLineOfSight(target);
                    SetPhase(CrossbowPhase.Idle);
                    RequestMovement(MovementRequest.None, Vector3.zero);
                    _losTarget = null;
                    _losBlockedSinceTime = -1f;
                    return;
                }

                SetPhase(CrossbowPhase.Approaching);
                RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
                return;
            }

            if (_losTarget != null)
            {
                _losTarget = null;
                _losBlockedSinceTime = -1f;
            }

        if (_lastKnownTargetDistance > MAX_RANGE)
            {
         SetPhase(CrossbowPhase.Approaching);
       RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
            }
else if (_lastKnownTargetDistance < DANGER_RANGE)
         {
                HandleDangerZone(target);
 }
 else if (_isLoaded)
            {
    // Have ammo, find good position to shoot
       if (_lastKnownTargetDistance >= OPTIMAL_MIN && _lastKnownTargetDistance <= OPTIMAL_MAX)
       {
          SetPhase(CrossbowPhase.Planting);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
    }
    else if (_lastKnownTargetDistance < OPTIMAL_MIN)
           {
        // Too close but loaded - shoot then retreat
      SetPhase(CrossbowPhase.Planting);
        RequestMovement(MovementRequest.Stop, Vector3.zero);
      }
   else
            {
       // Too far - approach
SetPhase(CrossbowPhase.Approaching);
         RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
                }
          }
       else
     {
          // Not loaded - reload while maintaining distance
     if (_lastKnownTargetDistance < OPTIMAL_MIN || _targetIsApproaching)
            {
            SetPhase(CrossbowPhase.Retreating);
   RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
            if (!_isReloading) StartReload();
         }
         else
 {
         // Safe distance - stand and reload
     SetPhase(CrossbowPhase.Reloading);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
   if (!_isReloading) StartReload();
   }
     }
        }
        
        private void UpdateApproaching(Character target)
      {
       if (_lastKnownTargetDistance <= OPTIMAL_MAX)
            {
   if (_isLoaded)
  {
           SetPhase(CrossbowPhase.Planting);
     RequestMovement(MovementRequest.Stop, Vector3.zero);
          }
     else if (!_isReloading)
    {
         StartReload();
    SetPhase(CrossbowPhase.Reloading);
            }
           return;
            }
    
      RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
        }
    
        private void UpdateRetreating(Character target)
      {
   if (_lastKnownTargetDistance >= OPTIMAL_MIN)
 {
        if (_isLoaded)
        {
         SetPhase(CrossbowPhase.Planting);
      RequestMovement(MovementRequest.Stop, Vector3.zero);
     }
              else if (_isReloading)
      {
 // Keep retreating while reloading, or strafe
             if (_targetIsApproaching)
    {
         RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
           }
              else
         {
             // Safe, strafe while reloading
           UpdateStrafeDirection();
          Vector3 toTarget = GetDirectionToTarget(target);
         Vector3 strafeDir = Vector3.Cross(Vector3.up, toTarget) * _strafeDirection;
   RequestMovement(MovementRequest.Strafe, strafeDir);
             }
      }
        return;
  }
     
          RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
        }
        
        private void UpdateReloadingPhase(Character target)
        {
      FaceTarget(target);
            
    // Check if reload complete
       if (_isLoaded && !_isReloading)
            {
        SetPhase(CrossbowPhase.Idle); // Will decide next action
             return;
            }
            
// While reloading, check if we need to retreat
            if (_lastKnownTargetDistance < CLOSE_RANGE && _targetIsApproaching)
            {
         SetPhase(CrossbowPhase.Retreating);
     RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
    }
        }
        
        private void UpdateStrafeDirection()
   {
         if (Time.time - _lastStrafeChange > 2f)
 {
                _strafeDirection = Random.value > 0.5f ? 1 : -1;
        _lastStrafeChange = Time.time;
     }
    }
     
    #endregion
        
#region Attack Execution
        
        public override bool ShouldAttack(Character target)
        {
            // Crossbow manages its own timing through state machine
       return false;
    }
        
        public override void ExecuteAttack(Character target)
        {
  if (!_isLoaded) return;
     
       if (_currentPhase == CrossbowPhase.Idle)
            {
       SetPhase(CrossbowPhase.Planting);
           RequestMovement(MovementRequest.Stop, Vector3.zero);
        }
   }
        
        private void ExecuteCrossbowShot(Character target)
    {
            if (!_isLoaded) return;
 
            _lastAttackTime = Time.time;
            _lastShotTime = Time.time;
        _isAttacking = true;
          _isLoaded = false;
            
            FaceTarget(target);
            
         string animTrigger = Context.CurrentAttack?.m_attackAnimation ?? "crossbow_fire";
   
            if (Context.ZAnim != null)
            {
 Context.ZAnim.SetTrigger(animTrigger);
       }
     else if (Context.Animator != null && Context.HasAnimatorParameter(animTrigger))
            {
    Context.Animator.SetTrigger(animTrigger);
     }

            Context.BroadcastRPC("RPC_CompanionAttack", animTrigger, 0);
       
         if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[CrossbowBehavior] Shot at {target.m_name}, distance: {_lastKnownTargetDistance:F1}m");
 }
     
            SpawnBoltProjectile(target);
     
    _attackCoroutine = Owner.StartCoroutine(FinishCrossbowShotCoroutine(0.4f));
        }
        
     private void SpawnBoltProjectile(Character target)
  {
            GameObject projectilePrefab = GetBoltProjectile();
            if (projectilePrefab == null)
            {
      if (CompanionCombat.VerboseLogging)
  Debug.LogWarning("[CrossbowBehavior] No bolt projectile found!");
      return;
            }

            Vector3 spawnPos = Context.Transform.position + Vector3.up * 1.5f + Context.Transform.forward * 0.3f;
         Vector3 targetPos = PredictTargetPosition(target, spawnPos);
    Vector3 direction = (targetPos - spawnPos).normalized;
    
         Quaternion rotation = Quaternion.LookRotation(direction);
            GameObject projectileObj = Object.Instantiate(projectilePrefab, spawnPos, rotation);
     
   var projectile = projectileObj.GetComponent<Projectile>();
            if (projectile != null)
    {
    HitData hitData = Context.CreateHitData(target);
        float velocity = Context.CurrentAttack?.m_projectileVel ?? 60f;
                
          projectile.Setup(
      Context.Character,
    direction * velocity,
         Context.CurrentAttack?.m_attackHitNoise ?? 0f,
        hitData,
      null,
            Context.CurrentWeapon
                );
        }
            
     Context.CompanionSkills?.RaiseSkill(global::Skills.SkillType.Crossbows, 1f);
        }
        
        private Vector3 PredictTargetPosition(Character target, Vector3 firePos)
        {
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;
       float distance = Vector3.Distance(firePos, targetPos);
            float boltSpeed = Context.CurrentAttack?.m_projectileVel ?? 60f;
            float flightTime = distance / boltSpeed;
          
      return targetPos + _lastKnownTargetVelocity * flightTime * 0.4f;
        }
        
        private GameObject GetBoltProjectile()
        {
      if (Context.CurrentAttack?.m_attackProjectile != null)
{
          return Context.CurrentAttack.m_attackProjectile;
      }
  
        if (ObjectDB.instance != null)
            {
     var boltPrefab = ObjectDB.instance.GetItemPrefab("BoltBone");
  if (boltPrefab != null)
    {
        var itemDrop = boltPrefab.GetComponent<ItemDrop>();
   if (itemDrop?.m_itemData?.m_shared?.m_attack?.m_attackProjectile != null)
    {
       return itemDrop.m_itemData.m_shared.m_attack.m_attackProjectile;
    }
       }
    }
  
        return null;
        }
      
     private IEnumerator FinishCrossbowShotCoroutine(float delay)
        {
      yield return new WaitForSeconds(delay);
 
        _isAttacking = false;
            
         _dodgeBehavior?.NotifyRangedAttackComplete();
 
        if (_dodgeAfterShot && _dodgeThreat != null && !_dodgeThreat.IsDead())
            {
    if (CompanionCombat.VerboseLogging)
        Debug.Log("[CrossbowBehavior] Executing post-shot retreat");
         
    SetPhase(CrossbowPhase.Retreating);
           RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(_dodgeThreat));
}
            
        _dodgeAfterShot = false;
   _dodgeThreat = null;
      _attackCoroutine = null;
   }
   
        public override void CancelAttack()
        {
       _isReloading = false;
   _dodgeAfterShot = false;
    _dodgeThreat = null;
     base.CancelAttack();
        }
        
        #endregion
        
        #region Reload System
        
      private void StartReload()
        {
    if (_isReloading) return;
            
     _isReloading = true;
      _reloadStartTime = Time.time;
            
  if (Context.ZAnim != null)
            {
         Context.ZAnim.SetBool("reload_crossbow", true);
  }
      else if (Context.Animator != null && Context.HasAnimatorParameter("reload_crossbow"))
            {
     Context.Animator.SetBool("reload_crossbow", true);
       }
            
     string reloadAnim = Context.CurrentAttack?.m_reloadAnimation;
       if (!string.IsNullOrEmpty(reloadAnim))
            {
     if (Context.ZAnim != null)
                {
     Context.ZAnim.SetTrigger(reloadAnim);
           }
    else if (Context.Animator != null && Context.HasAnimatorParameter(reloadAnim))
    {
    Context.Animator.SetTrigger(reloadAnim);
    }
            }
            
         if (CompanionCombat.VerboseLogging)
     {
     Debug.Log($"[CrossbowBehavior] Started reload, time: {_currentReloadDuration}s");
          }
 }
        
   private void UpdateReload()
 {
            if (!_isReloading) return;
          
            float reloadProgress = (Time.time - _reloadStartTime) / _currentReloadDuration;
    
       if (reloadProgress >= 1f)
        {
                _isReloading = false;
  _isLoaded = true;
    
      if (Context.ZAnim != null)
                {
 Context.ZAnim.SetBool("reload_crossbow", false);
      }
 else if (Context.Animator != null && Context.HasAnimatorParameter("reload_crossbow"))
                {
                    Context.Animator.SetBool("reload_crossbow", false);
 }
        
                if (CompanionCombat.VerboseLogging)
         Debug.Log("[CrossbowBehavior] Reload complete - ready to fire");
}
   }
        
        #endregion
        
        #region Helpers
        
    private void SetPhase(CrossbowPhase newPhase)
     {
            if (newPhase == _currentPhase) return;
     
         if (CompanionCombat.VerboseLogging)
    {
    Debug.Log($"[CrossbowBehavior] Phase: {_currentPhase} -> {newPhase}");
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
        
     private new void FaceTarget(Character target)
        {
     if (target == null) return;
        
     Vector3 dirToTarget = GetDirectionToTarget(target);
        if (dirToTarget != Vector3.zero)
   {
       Context.Transform.rotation = Quaternion.Slerp(
        Context.Transform.rotation,
   Quaternion.LookRotation(dirToTarget),
Time.deltaTime * 10f
    );
        }
        }
      
        #endregion
        
  #region External API
    
        /// <summary>
        /// Called by DodgeBehavior when threat requires evasive action.
        /// </summary>
  public void RequestEarlyFireAndDodge(Character threat)
      {
    if (_isLoaded && !_isAttacking)
            {
     _dodgeAfterShot = true;
            _dodgeThreat = threat;
         
                if (CompanionCombat.VerboseLogging)
         {
    Debug.Log($"[CrossbowBehavior] Emergency shot requested");
       }
           
    SetPhase(CrossbowPhase.Firing);
    ExecuteCrossbowShot(threat);
         }
     else
  {
         // Not loaded - just dodge
     SetPhase(CrossbowPhase.EmergencyDodge);
        _dodgeBehavior?.ExecuteRetreatDodge(threat);
            }
        }
        
     public void ForceRetreat()
        {
          if (_isLoaded && !_isAttacking && _currentTarget != null)
            {
       _dodgeAfterShot = true;
 ExecuteCrossbowShot(_currentTarget);
      return;
         }
            
       SetPhase(CrossbowPhase.Retreating);
        }
  
        public bool CanBeInterrupted()
        {
            return !_isAttacking && _currentPhase != CrossbowPhase.EmergencyDodge;
      }
        
        #endregion
    }
}
