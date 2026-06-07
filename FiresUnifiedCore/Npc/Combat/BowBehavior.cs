using UnityEngine;
using System.Collections;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Combat behavior for bows - implements proper kiting archer gameplay.
    /// 
    /// KITING PHILOSOPHY (How real players use bows in Valheim):
    /// 1. KITE - Run away while enemies chase
    /// 2. CREATE DISTANCE - Get to ~12-15m (optimal bow range)
    /// 3. QUICK STOP & SHOOT - Plant feet, quick draw (50-80%), release
    /// 4. IMMEDIATELY MOVE - Don't stand still after shooting
    /// 5. RETREAT if threatened - backpedal to create distance
    /// 
    /// NO DODGE/ROLL:
    /// Bow companions should NOT roll/dodge - this wastes stamina.
    /// Instead, they kite by running away and backpedaling.
    /// DodgeBehavior handles dodging separately if needed.
    /// 
    /// DISTANCE ZONES:
    /// - DANGER (0-5m): Retreat while quick-shooting
    /// - CLOSE (5-10m): Quick shot while backpedaling
    /// - OPTIMAL (10-15m): Plant feet, full draw for max damage
    /// - FAR (15-25m): Close distance slightly, full draw
    /// - OUT OF RANGE (25m+): Chase to get in range
    /// </summary>
    public class BowBehavior : WeaponBehavior
    {
        #region Enums
   
        public enum RangedCombatPhase
        {
            Idle,
            Approaching,
            Retreating,
            Planting,       // Stopping to shoot
            Drawing,        // Actively drawing the bow
            Releasing,      // Firing the arrow
            Repositioning   // Moving after a shot
        }
        
        public enum MovementRequest
        {
            None,
            Stop,
            RunAway,
            Backpedal,
            Strafe,
            Approach
        }
        
      #endregion
        
#region Constants
        
// Distance zones
  private const float DANGER_RANGE = 5f;
    private const float CLOSE_RANGE = 10f;
        private const float OPTIMAL_MIN = 10f;
        private const float OPTIMAL_MAX = 15f;
        private const float MAX_RANGE = 25f;

        // Draw timing
        private const float QUICK_DRAW_PERCENT = 0.4f;  // Emergency minimum
    private const float FAST_DRAW_PERCENT = 0.6f;   // Quick shot when pressured
        private const float FULL_DRAW_PERCENT = 1.0f;   // Optimal damage
        
        // Timing
      private const float MIN_SHOT_INTERVAL = 0.3f;
        private const float REPOSITION_DURATION = 1.0f;
      private const float PLANT_DURATION = 0.1f; // Very quick plant
  private const float THREAT_CHECK_INTERVAL = 0.15f;

        #endregion
        
        #region State
        
        // Core bow state
   private bool _isBowDrawing;
   private float _bowDrawStartTime;
        private float _currentDrawDuration;
        private Character _bowTarget;
     private bool _bowShotCompleted;
     private Coroutine _bowShotCoroutine;
        
        // Kiting state
        private RangedCombatPhase _currentPhase = RangedCombatPhase.Idle;
        private MovementRequest _currentMovementRequest = MovementRequest.None;
        private float _phaseStartTime;
    private float _lastThreatCheck;
        private float _lastShotTime;
        private float _targetDrawPercent = FULL_DRAW_PERCENT;
        
        // Tracking
        private float _lastKnownTargetDistance;
        private Vector3 _lastKnownTargetPosition;
        private Vector3 _lastKnownTargetVelocity;
        private bool _targetIsApproaching;
        
        // Post-shot retreat state (just backpedal, no dodge)
        private bool _retreatAfterShot;
        private Character _retreatThreat;
   
        // Strafe state
        private int _strafeDirection = 1;
        private float _lastStrafeChange;
        
        #endregion
        
        #region Public Properties
        
        public bool IsBowDrawing => _isBowDrawing;
    public RangedCombatPhase CurrentPhase => _currentPhase;
        public MovementRequest CurrentMovementNeed => _currentMovementRequest;
        public float TargetDistance => _lastKnownTargetDistance;
        
        public System.Action<MovementRequest, Vector3> OnMovementRequested;
  
        #endregion
        
     #region Initialization
        
  public override void Initialize(CombatContext context, MonoBehaviour owner)
    {
       base.Initialize(context, owner);
            _currentDrawDuration = context.DefaultBowDrawTime;
        }
        
        public override void OnActivate()
        {
  base.OnActivate();
     ResetBowState();
          _currentPhase = RangedCombatPhase.Idle;
    _currentMovementRequest = MovementRequest.None;
        }
      
   public override void OnDeactivate()
        {
            CancelBowDraw();
  _currentPhase = RangedCombatPhase.Idle;
     _currentMovementRequest = MovementRequest.None;
          base.OnDeactivate();
        }
  
        public override void ConfigureAI()
        {
            // CompanionAI handles combat behavior directly - no MonsterAI settings needed
            float attackRange = Context.AIAttackRange > 5f ? Context.AIAttackRange : MAX_RANGE;
            Context.AttackRange = attackRange;
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[BowBehavior] Configured: range={attackRange}, drawTime={_currentDrawDuration}");
            }
        }
     
     public override void UpdateAttackData()
        {
      base.UpdateAttackData();
            
   if (Context.IsBowDraw && Context.DrawDurationMin > 0)
  {
      _currentDrawDuration = Context.DrawDurationMin;
         }
            else if (Context.CurrentAttack != null && Context.CurrentAttack.m_bowDraw && Context.CurrentAttack.m_drawDurationMin > 0)
      {
       _currentDrawDuration = Context.CurrentAttack.m_drawDurationMin;
    }
     else
 {
       _currentDrawDuration = Context.DefaultBowDrawTime;
       }
    
         _effectiveCooldown = _currentDrawDuration * 0.5f + 0.3f;
        }
        
        #endregion
 
        #region Update Loop
      
        public override void Update()
        {
            base.Update();
            
            // CRITICAL: Check StaminaManager FIRST - if in recovery, do NOTHING
            // Just cancel any active bow draw and let CompanionCombatMovement handle retreat
            var staminaManager = Owner?.GetComponent<StaminaManager>();
            if (staminaManager != null)
            {
                if (staminaManager.IsInCriticalRecovery() || staminaManager.ShouldTreatAsLowHealth())
                {
                    // Cancel any bow draw - we need to retreat
                    if (_isBowDrawing)
                    {
                        CancelBowDraw();
                    }
                    
                    // Set to idle and stop all movement requests - let retreat system take over
                    SetPhase(RangedCombatPhase.Idle);
                    _currentMovementRequest = MovementRequest.None;
                    
                    // Do NOT request any movement - CompanionCombatMovement handles retreat
                    return;
                }
                
                // Also check normal recovery - be passive, don't control movement
                if (staminaManager.IsRecovering())
                {
                    // Can continue a bow draw in progress, but don't start new combat actions
                    if (!_isBowDrawing)
                    {
                        SetPhase(RangedCombatPhase.Idle);
                        _currentMovementRequest = MovementRequest.None;
                        return;
                    }
                }
            }
            
            var target = Context.CompanionAI?.GetTargetCreature();
            
            if (target == null || target.IsDead())
            {
                if (_isBowDrawing) CancelBowDraw();
                SetPhase(RangedCombatPhase.Idle);
                RequestMovement(MovementRequest.None, Vector3.zero);
                return;
            }
         
            // Update target tracking
            UpdateTargetTracking(target);
      
            // Check for threats frequently
            if (Time.time - _lastThreatCheck >= THREAT_CHECK_INTERVAL)
            {
                _lastThreatCheck = Time.time;
                CheckThreatLevel(target);
            }
            
         // Update bow draw if active
            if (_isBowDrawing)
            {
 UpdateBowDraw();
     }
    
            // State machine update
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
            // DANGER ZONE - retreat while shooting
            if (_lastKnownTargetDistance < DANGER_RANGE)
            {
                HandleDangerZone(target);
                return;
            }
 
            // Close range with approaching enemy
            if (_lastKnownTargetDistance < CLOSE_RANGE && _targetIsApproaching)
            {
                if (_isBowDrawing)
                {
                    float drawProgress = GetCurrentDrawPercent();
                    if (drawProgress >= QUICK_DRAW_PERCENT)
                    {
                        // Fire and then retreat (backpedal) - NO dodge, just movement
                        _retreatAfterShot = true;
                        _retreatThreat = target;
                        CompleteBowShot(drawProgress);
                        return;
                    }
                }
            }
        }
        
        private void HandleDangerZone(Character target)
        {
            if (_isBowDrawing)
            {
                float drawProgress = GetCurrentDrawPercent();
                if (drawProgress >= QUICK_DRAW_PERCENT)
                {
                    // Fire and then retreat (backpedal)
                    _retreatAfterShot = true;
                    _retreatThreat = target;
                    CompleteBowShot(drawProgress);
                }
                else
                {
                    // Draw too low - cancel and retreat
                    CancelBowDraw();
                    SetPhase(RangedCombatPhase.Retreating);
                    RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                }
            }
            else if (_currentPhase != RangedCombatPhase.Retreating)
            {
                // Not drawing - just retreat
                SetPhase(RangedCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
            }
        }
      
        private void UpdateCombatPhase(Character target)
   {
      float timeSincePhaseStart = Time.time - _phaseStartTime;
         
            switch (_currentPhase)
            {
                case RangedCombatPhase.Idle:
                    DecideNextAction(target);
                    break;
            
                case RangedCombatPhase.Approaching:
                    UpdateApproaching(target);
                    break;
       
                case RangedCombatPhase.Retreating:
                    UpdateRetreating(target);
                    break;
          
                case RangedCombatPhase.Planting:
                    if (timeSincePhaseStart >= PLANT_DURATION)
                    {
                        StartBowDraw(target);
                        SetPhase(RangedCombatPhase.Drawing);
                    }
                    break;
      
                case RangedCombatPhase.Drawing:
                    // Drawing handled in UpdateBowDraw()
                    break;
        
                case RangedCombatPhase.Releasing:
                    if (!_isAttacking && !_isBowDrawing)
                    {
                        // Decide whether to reposition or immediately fire again
                        DecidePostShotAction(target);
                    }
                    break;
                   
                case RangedCombatPhase.Repositioning:
                    UpdateRepositioning(target, timeSincePhaseStart);
                    break;
            }
        }
        
     #endregion
        
        #region Combat Decisions
        
        // ?? Line-of-sight reposition tracking ?????????????????????????????
        // When the active target has no LOS we attempt to reposition for up to
        // LOS_REPOSITION_TIMEOUT seconds before blacklisting the target so
        // CompanionAI re-acquires a different enemy.  This is the "either
        // reposition to be able to shoot or target a new enemy" behavior.
        private const float LOS_REPOSITION_TIMEOUT = 4f;
        private Character _losTarget;
        private float _losBlockedSinceTime = -1f;

private void DecideNextAction(Character target)
        {
            if (Time.time - _lastShotTime < MIN_SHOT_INTERVAL)
            {
      return;
            }

            // ?? LINE-OF-SIGHT GATE ??????????????????????????????????????????
            // Active-target LOS is checked here, ONCE per engagement decision,
            // not on every potential target in IsValidTarget.  Cost: one
            // raycast per ~MIN_SHOT_INTERVAL seconds while engaged.
            //
            // No LOS ? don't plant/draw.  Instead, transition to Approaching
            // so pathfinding tries to route around the obstacle.  After
            // LOS_REPOSITION_TIMEOUT seconds of failed LOS we tell the AI to
            // blacklist this target temporarily so it picks something else.
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
                        Debug.Log($"[BowBehavior] No LOS to {target.m_name} for {blockedFor:F1}s ï¿½ blacklisting and re-acquiring");
                    }
                    Context.CompanionAI.BlacklistTargetForLineOfSight(target);
                    SetPhase(RangedCombatPhase.Idle);
                    RequestMovement(MovementRequest.None, Vector3.zero);
                    _losTarget = null;
                    _losBlockedSinceTime = -1f;
                    return;
                }

                // Try to reposition: move toward the target.  Pathfinding will
                // route around walls; once we round the corner the next call
                // here will pass the LOS check and we'll transition to plant.
                SetPhase(RangedCombatPhase.Approaching);
                RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
                return;
            }

            // LOS is clear ï¿½ reset the failure tracker.
            if (_losTarget != null)
            {
                _losTarget = null;
                _losBlockedSinceTime = -1f;
            }

    if (Context != null && Context.IsAnimationLocked)
       {
       return;
     }
         
            if (_lastKnownTargetDistance > MAX_RANGE)
          {
          SetPhase(RangedCombatPhase.Approaching);
      RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
            }
     else if (_lastKnownTargetDistance < DANGER_RANGE)
            {
              HandleDangerZone(target);
     }
   else if (_lastKnownTargetDistance < CLOSE_RANGE)
   {
                _targetDrawPercent = FAST_DRAW_PERCENT;
     SetPhase(RangedCombatPhase.Planting);
         RequestMovement(MovementRequest.Stop, Vector3.zero);
      }
     else if (_lastKnownTargetDistance >= OPTIMAL_MIN && _lastKnownTargetDistance <= OPTIMAL_MAX)
        {
  _targetDrawPercent = FULL_DRAW_PERCENT;
     SetPhase(RangedCombatPhase.Planting);
     RequestMovement(MovementRequest.Stop, Vector3.zero);
         }
        else if (_lastKnownTargetDistance > OPTIMAL_MAX)
      {
                if (_lastKnownTargetDistance > OPTIMAL_MAX + 3f)
{
       SetPhase(RangedCombatPhase.Approaching);
   RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
       }
                else
       {
             _targetDrawPercent = FULL_DRAW_PERCENT;
     SetPhase(RangedCombatPhase.Planting);
        RequestMovement(MovementRequest.Stop, Vector3.zero);
              }
        }
        }
  
        private void UpdateApproaching(Character target)
        {
            if (_lastKnownTargetDistance <= OPTIMAL_MAX)
   {
      _targetDrawPercent = FULL_DRAW_PERCENT;
            SetPhase(RangedCombatPhase.Planting);
         RequestMovement(MovementRequest.Stop, Vector3.zero);
      return;
            }
            
       RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
        }
        
        private void UpdateRetreating(Character target)
  {
       if (_lastKnownTargetDistance >= OPTIMAL_MIN)
       {
            _targetDrawPercent = _lastKnownTargetDistance < CLOSE_RANGE ? FAST_DRAW_PERCENT : FULL_DRAW_PERCENT;
 SetPhase(RangedCombatPhase.Planting);
    RequestMovement(MovementRequest.Stop, Vector3.zero);
     return;
     }
            
   RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
   }
        
        private void DecideRepositionDirection(Character target)
        {
            // CRITICAL: Only reposition if we actually NEED to
            // If we're safe at optimal range with no approaching enemy, stay put and keep shooting!
            
            // Must retreat - enemy is too close
            if (_lastKnownTargetDistance < CLOSE_RANGE)
            {
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                return;
            }
            
            // Enemy approaching fast - back up while shooting
            if (_targetIsApproaching && _lastKnownTargetDistance < OPTIMAL_MAX)
            {
                RequestMovement(MovementRequest.Backpedal, -GetDirectionToTarget(target));
                return;
            }
            
            // We're at a good distance and enemy isn't rushing us
            // Stay put and fire again! No need to reposition.
            RequestMovement(MovementRequest.Stop, Vector3.zero);
        }
        
        /// <summary>
        /// After firing a shot, decide what to do next.
        /// If safe, immediately start another shot. If threatened, reposition briefly.
        /// IMPROVED: Less aggressive retreating - only retreat when truly in danger.
        /// </summary>
        private void DecidePostShotAction(Character target)
        {
            // DANGER - must retreat immediately
            if (_lastKnownTargetDistance < DANGER_RANGE)
            {
                SetPhase(RangedCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                return;
            }
            
            // Close range AND enemy is RAPIDLY approaching - retreat
            // Only retreat if enemy is actually moving fast toward us
            if (_lastKnownTargetDistance < CLOSE_RANGE && _targetIsApproaching)
            {
                float approachSpeed = Vector3.Dot(_lastKnownTargetVelocity, 
                    (Context.Transform.position - target.transform.position).normalized);
                
                // Only retreat if enemy is moving toward us at decent speed (>3 m/s)
                if (approachSpeed > 3f)
                {
                    SetPhase(RangedCombatPhase.Retreating);
                    RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                    return;
                }
            }
            
            // Enemy approaching but slowly - just sidestep slightly, don't full retreat
            // This prevents the "backing up forever" issue
            if (_targetIsApproaching && _lastKnownTargetDistance < OPTIMAL_MAX)
            {
                float approachSpeed = Vector3.Dot(_lastKnownTargetVelocity, 
                    (Context.Transform.position - target.transform.position).normalized);
                
                // If enemy is moving toward us but slowly, just sidestep
                if (approachSpeed > 1f && approachSpeed <= 3f)
                {
                    // Quick sidestep strafe instead of full retreat
                    if (Time.time - _lastStrafeChange > 2f)
                    {
                        _strafeDirection = -_strafeDirection; // Alternate sides
                        _lastStrafeChange = Time.time;
                    }
                    
                    Vector3 strafeDir = Vector3.Cross(GetDirectionToTarget(target), Vector3.up) * _strafeDirection;
                    SetPhase(RangedCombatPhase.Repositioning);
                    RequestMovement(MovementRequest.Strafe, strafeDir);
                    return;
                }
            }
            
            // We're safe - fire again immediately!
            // This is the key: don't move if we don't need to
            _targetDrawPercent = FULL_DRAW_PERCENT;
            SetPhase(RangedCombatPhase.Planting);
            RequestMovement(MovementRequest.Stop, Vector3.zero);
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[BowBehavior] Safe to fire again (dist: {_lastKnownTargetDistance:F1}m, approaching: {_targetIsApproaching})");
            }
        }
        
        private void UpdateRepositioning(Character target, float timeSinceStart)
        {
            // MUCH shorter reposition time - just a brief sidestep, not prolonged retreat
            // Reduced from 0.5s to 0.3s
            if (timeSinceStart >= REPOSITION_DURATION * 0.3f)
            {
                // After very brief repositioning, fire again
                // Don't keep retreating unless we're actually in danger
                if (_lastKnownTargetDistance >= DANGER_RANGE)
                {
                    // We're safe enough, stop moving and shoot
                    _targetDrawPercent = _lastKnownTargetDistance < CLOSE_RANGE ? FAST_DRAW_PERCENT : FULL_DRAW_PERCENT;
                    SetPhase(RangedCombatPhase.Planting);
                    RequestMovement(MovementRequest.Stop, Vector3.zero);
                }
                else
                {
                    // Still in danger zone, keep retreating
                    SetPhase(RangedCombatPhase.Retreating);
                    RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                }
                return;
            }
              
            // Emergency check - only switch to retreat if enemy is REALLY close
            if (_lastKnownTargetDistance < DANGER_RANGE)
            {
                SetPhase(RangedCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
            }
        }
    
        #endregion
        
        #region Attack Execution
        
        public override bool ShouldAttack(Character target)
        {
    return false;
        }
    
      public override void ExecuteAttack(Character target)
        {
        if (_currentPhase == RangedCombatPhase.Idle)
            {
                _targetDrawPercent = FULL_DRAW_PERCENT;
     SetPhase(RangedCombatPhase.Planting);
        RequestMovement(MovementRequest.Stop, Vector3.zero);
     }
  }
        
 private void StartBowDraw(Character target)
        {
    if (_isBowDrawing) return;
            if (target == null || target.IsDead()) return;
        
            _isBowDrawing = true;
            _bowDrawStartTime = Time.time;
            _bowTarget = target;
            _lastAttackTime = Time.time;
            _bowShotCompleted = false;
            _retreatAfterShot = false;
            _retreatThreat = null;
      
 string drawState = !string.IsNullOrEmpty(Context.DrawAnimationState)
           ? Context.DrawAnimationState
                : Context.CurrentAttack?.m_drawAnimationState;
  
    if (Context.ZAnim != null)
            {
           Context.ZAnim.SetBool(CombatContext.Hash_bow_aim, true);
           Context.ZAnim.SetFloat(CombatContext.Hash_drawpercent, 0f);
 
    if (!string.IsNullOrEmpty(drawState))
         {
               Context.ZAnim.SetBool(drawState, true);
           }
 }
     else if (Context.Animator != null)
 {
 if (Context.HasAnimatorParameter("bow_aim"))
        Context.Animator.SetBool("bow_aim", true);
       if (Context.HasAnimatorParameter("drawpercent"))
Context.Animator.SetFloat("drawpercent", 0f);
 }
        
     Context.BroadcastRPC("RPC_CompanionBowAim", true);
            
   if (CompanionCombat.VerboseLogging)
  {
      Debug.Log($"[BowBehavior] Started draw - target: {target.m_name}, " +
   $"targetDraw: {_targetDrawPercent * 100:F0}%, distance: {_lastKnownTargetDistance:F1}m");
            }
        }
        
     private void UpdateBowDraw()
       {
      if (!_isBowDrawing || _bowShotCompleted) return;

      if (_bowTarget == null || _bowTarget.IsDead())
        {
      CancelBowDraw();
          SetPhase(RangedCombatPhase.Idle);
     return;
       }

           // LINE-OF-SIGHT ABORT
           // If the target ducked behind a wall / column / piece while we were
           // drawing, abandon the shot instead of firing into the obstacle.
           // The targeting filter (CompanionAI.IsValidTarget) prevents engaging
           // unsightable enemies in the first place, but a target can move into
           // cover after engagement begins ï¿½ this catches that case so we don't
           // waste arrows into walls in dungeons.
           if (Context.CompanionAI != null && !Context.CompanionAI.HasClearShotToCurrentTarget())
           {
               if (CompanionCombat.VerboseLogging)
               {
                   Debug.Log($"[BowBehavior] Aborting draw ï¿½ target {_bowTarget.m_name} no longer has line-of-sight");
               }
               CancelBowDraw();
               SetPhase(RangedCombatPhase.Idle);
               return;
           }

         float drawProgress = GetCurrentDrawPercent();
        
  if (Context.ZAnim != null)
   {
       Context.ZAnim.SetFloat(CombatContext.Hash_drawpercent, drawProgress);
     }
  else if (Context.Animator != null && Context.HasAnimatorParameter("drawpercent"))
  {
      Context.Animator.SetFloat("drawpercent", drawProgress);
            }
            
  FaceTarget(_bowTarget);
  
            // Fire when we reach target draw percentage
 if (drawProgress >= _targetDrawPercent)
      {
   CompleteBowShot(drawProgress);
       }
        }
        
        private void CompleteBowShot(float drawPercentage)
        {
    if (!_isBowDrawing || _bowTarget == null) return;
            if (_bowShotCompleted) return;
          
         _bowShotCompleted = true;
            _isAttacking = true;
     _lastShotTime = Time.time;
          
      SetPhase(RangedCombatPhase.Releasing);
        
         string animTrigger = !string.IsNullOrEmpty(Context.AttackAnimation)
 ? Context.AttackAnimation
       : (Context.CurrentAttack?.m_attackAnimation ?? "bow_fire");
 
            if (Context.ZAnim != null)
            {
    Context.ZAnim.SetTrigger(animTrigger);
    }
            else if (Context.Animator != null && Context.HasAnimatorParameter(animTrigger))
            {
   Context.Animator.SetTrigger(animTrigger);
   }
            
    Context.BroadcastRPC("RPC_CompanionAttack", animTrigger, (int)Context.WeaponAnimationState);
      
            if (CompanionCombat.VerboseLogging)
   {
                Debug.Log($"[BowBehavior] SHOT FIRED at {_bowTarget.m_name}, " +
  $"draw: {drawPercentage * 100:F0}%, distance: {_lastKnownTargetDistance:F1}m");
      }
            
      SpawnArrowProjectile(_bowTarget, drawPercentage);
            
  _bowShotCoroutine = Owner.StartCoroutine(FinishBowShotCoroutine(Context.BowAimHoldTime));
        }
        
        private void SpawnArrowProjectile(Character target, float drawPercentage)
        {
            // Check and consume stamina for the bow shot
            var stats = Context.Companion?.GetStats();
            if (stats != null)
            {
                float baseCost = Context.CurrentAttack?.m_attackStamina ?? 20f;
                float adjustedCost = stats.GetStaminaCost(baseCost, Skills.SkillType.Bows);
                
                if (!stats.UseStamina(adjustedCost))
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BowBehavior] Not enough stamina for shot: need {adjustedCost:F1}, have {stats.CurrentStamina:F1}");
                    return; // Can't fire without stamina
                }
                
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[BowBehavior] Consumed {adjustedCost:F1} stamina for shot");
            }
            
            GameObject projectilePrefab = Context.AttackProjectile ?? GetArrowProjectile();
            if (projectilePrefab == null)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.LogWarning("[BowBehavior] No arrow projectile found!");
                return;
            }

            Vector3 spawnPos = Context.Transform.position + Vector3.up * (1.5f * Context.Transform.localScale.y) + Context.Transform.forward * 0.3f;
            
            Vector3 targetPos = PredictTargetPosition(target, spawnPos);
            Vector3 direction = (targetPos - spawnPos).normalized;

            float distance = Vector3.Distance(spawnPos, targetPos);
            float velocity = Context.ProjectileVelocity * Mathf.Max(0.5f, drawPercentage);
            
            // Calculate proper arc for gravity compensation
            // Valheim arrows have significant drop, especially at range
            // The arc formula: arc = 0.5 * g * t^2 / distance where t = distance/velocity
            float flightTime = distance / velocity;
            float gravityCompensation = 0.5f * 9.81f * flightTime * flightTime;
            
            // Convert to arc angle - aim higher proportionally to distance
            // At 10m: small arc. At 25m: significant arc
            float arcHeight = gravityCompensation / distance;
            arcHeight = Mathf.Clamp(arcHeight, 0f, 0.5f); // Cap at 0.5 to prevent extreme angles
            
            // Add skill-based accuracy variation
            float skillLevel = Context.CompanionSkills?.GetSkillLevel(global::Skills.SkillType.Bows) ?? 0f;
            float accuracyBonus = Mathf.Lerp(0.02f, 0f, skillLevel / 100f); // Less wobble at higher skill
            float randomSpread = UnityEngine.Random.Range(-accuracyBonus, accuracyBonus);
            
            direction = (direction + Vector3.up * arcHeight).normalized;
            
            // Add tiny random spread based on skill
            if (accuracyBonus > 0)
            {
                direction = Vector3.Slerp(direction, UnityEngine.Random.onUnitSphere, randomSpread);
                direction.Normalize();
            }

            Quaternion rotation = Quaternion.LookRotation(direction);
            GameObject projectileObj = Object.Instantiate(projectilePrefab, spawnPos, rotation);

            var projectile = projectileObj.GetComponent<Projectile>();
            if (projectile != null)
            {
                HitData hitData = Context.CreateHitData(target, drawPercentage);
                
                projectile.Setup(
                    Context.Character,
                    direction * velocity,
                    Context.CurrentAttack?.m_attackHitNoise ?? 0f,
                    hitData,
                    null,
                    Context.CurrentWeapon
                );
            }
            
            Context.CompanionSkills?.RaiseSkill(global::Skills.SkillType.Bows, 1f);
        }
    
        private Vector3 PredictTargetPosition(Character target, Vector3 firePos)
        {
            // Determine the target's center of mass from their collider instead of using
            // a fixed 1m offset. This is critical for hitting tall enemies (trolls ~3m)
            // and short enemies correctly, especially when the shooter is a dwarf.
            float aimHeight = 1f; // fallback
            var capsule = target.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                aimHeight = capsule.center.y * target.transform.localScale.y;
            }
            else
            {
                var col = target.GetComponent<Collider>();
                if (col != null)
                {
                    aimHeight = col.bounds.center.y - target.transform.position.y;
                }
            }
            // Clamp to reasonable range
            aimHeight = Mathf.Clamp(aimHeight, 0.5f, 5f);
            
            Vector3 targetPos = target.transform.position + Vector3.up * aimHeight;
            
            float distance = Vector3.Distance(firePos, targetPos);
            float arrowSpeed = Context.ProjectileVelocity;
            
            // Calculate flight time more accurately
            // Account for arrow drop by using a slightly higher effective speed for close targets
            float flightTime = distance / arrowSpeed;
            
            // Predict where target will be - use full prediction for moving targets
            // Scale prediction by how fast the target is moving
            float targetSpeed = _lastKnownTargetVelocity.magnitude;
            float predictionMultiplier = Mathf.Clamp01(targetSpeed / 5f); // Full prediction at 5+ m/s
            
            Vector3 predictedPos = targetPos + _lastKnownTargetVelocity * flightTime * predictionMultiplier;
            
            return predictedPos;
        }
        
        private GameObject GetArrowProjectile()
        {
          if (Context.CurrentAttack?.m_attackProjectile != null)
            {
      return Context.CurrentAttack.m_attackProjectile;
  }
         
            if (ObjectDB.instance != null)
            {
        var arrowPrefab = ObjectDB.instance.GetItemPrefab("ArrowWood");
         if (arrowPrefab != null)
     {
                    var itemDrop = arrowPrefab.GetComponent<ItemDrop>();
        if (itemDrop?.m_itemData?.m_shared?.m_attack?.m_attackProjectile != null)
   {
                  return itemDrop.m_itemData.m_shared.m_attack.m_attackProjectile;
   }
     }
            }
       
            return null;
        }
        
     private IEnumerator FinishBowShotCoroutine(float delay)
        {
 yield return new WaitForSeconds(delay);
            FinishBowShot();
        _bowShotCoroutine = null;
        }
        
        private void FinishBowShot()
  {
  _isBowDrawing = false;
         _isAttacking = false;
    _bowShotCompleted = false;
   
  string drawState = !string.IsNullOrEmpty(Context.DrawAnimationState)
        ? Context.DrawAnimationState
              : Context.CurrentAttack?.m_drawAnimationState;
      
       if (Context.ZAnim != null)
            {
        Context.ZAnim.SetBool(CombatContext.Hash_bow_aim, false);
         Context.ZAnim.SetFloat(CombatContext.Hash_drawpercent, 0f);
     
       if (!string.IsNullOrEmpty(drawState))
     {
        Context.ZAnim.SetBool(drawState, false);
                }
         }
            else if (Context.Animator != null)
            {
    if (Context.HasAnimatorParameter("bow_aim"))
 Context.Animator.SetBool("bow_aim", false);
           if (Context.HasAnimatorParameter("drawpercent"))
          Context.Animator.SetFloat("drawpercent", 0f);
   }
     
            Context.BroadcastRPC("RPC_CompanionBowAim", false);
            
            // Execute post-shot retreat if needed (just backpedal)
            if (_retreatAfterShot && _retreatThreat != null && !_retreatThreat.IsDead())
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log("[BowBehavior] Post-shot retreat (backpedaling, not dodging)");
  
                SetPhase(RangedCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(_retreatThreat));
            }
            
            _bowTarget = null;
            _retreatAfterShot = false;
            _retreatThreat = null;
        }
   
        public override void CancelAttack()
        {
    CancelBowDraw();
            base.CancelAttack();
        }
        
        private void CancelBowDraw()
        {
     if (!_isBowDrawing) return;
 
            _isBowDrawing = false;
            _bowTarget = null;
            _bowShotCompleted = false;
            _retreatAfterShot = false;
            _retreatThreat = null;
   
     if (_bowShotCoroutine != null)
      {
             Owner.StopCoroutine(_bowShotCoroutine);
              _bowShotCoroutine = null;
            }
    
    string drawState = !string.IsNullOrEmpty(Context.DrawAnimationState)
     ? Context.DrawAnimationState
     : Context.CurrentAttack?.m_drawAnimationState;
    
 if (Context.ZAnim != null)
   {
     Context.ZAnim.SetBool(CombatContext.Hash_bow_aim, false);
       Context.ZAnim.SetFloat(CombatContext.Hash_drawpercent, 0f);
   
    if (!string.IsNullOrEmpty(drawState))
        {
          Context.ZAnim.SetBool(drawState, false);
      }
 }
         else if (Context.Animator != null)
         {
        if (Context.HasAnimatorParameter("bow_aim"))
           Context.Animator.SetBool("bow_aim", false);
    if (Context.HasAnimatorParameter("drawpercent"))
        Context.Animator.SetFloat("drawpercent", 0f);
            }
          
  Context.BroadcastRPC("RPC_CompanionBowAim", false);
            
        if (CompanionCombat.VerboseLogging)
  Debug.Log("[BowBehavior] Bow draw cancelled");
        }
        
        private void ResetBowState()
        {
            _isBowDrawing = false;
            _bowShotCompleted = false;
            _bowTarget = null;
            _retreatAfterShot = false;
            _retreatThreat = null;
        }
        
        #endregion
  
        #region Helpers
        
  private void SetPhase(RangedCombatPhase newPhase)
        {
    if (newPhase == _currentPhase) return;
      
   if (CompanionCombat.VerboseLogging)
   {
         Debug.Log($"[BowBehavior] Phase: {_currentPhase} -> {newPhase}");
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
    
        private float GetCurrentDrawPercent()
        {
         if (!_isBowDrawing) return 0f;
         return Mathf.Clamp01((Time.time - _bowDrawStartTime) / _currentDrawDuration);
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
        /// Called when threat is very close - fire quickly and retreat.
        /// NO LONGER triggers dodge - just backpedals to preserve stamina.
        /// </summary>
        public void RequestEarlyFireAndDodge(Character threat)
        {
            if (!_isBowDrawing || _bowShotCompleted) return;
 
            float drawProgress = GetCurrentDrawPercent();
      
            if (drawProgress >= QUICK_DRAW_PERCENT)
            {
                _retreatAfterShot = true;
                _retreatThreat = threat;
       
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[BowBehavior] Early fire - {drawProgress * 100:F0}% draw, then retreat");
                }
        
                CompleteBowShot(drawProgress);
            }
            else
            {
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[BowBehavior] Draw too low ({drawProgress * 100:F0}%) - canceling and retreating");
                }
                CancelBowDraw();
                // Just retreat, don't waste stamina on dodge
                SetPhase(RangedCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(threat));
            }
        }
      
        public void ForceRetreat()
        {
            if (_isBowDrawing)
            {
                float drawProgress = GetCurrentDrawPercent();
                if (drawProgress >= QUICK_DRAW_PERCENT)
                {
                    _retreatAfterShot = true;
                    CompleteBowShot(drawProgress);
                    return;
                }
                CancelBowDraw();
            }
    
            SetPhase(RangedCombatPhase.Retreating);
        }
        
        public bool CanBeInterrupted()
        {
            return !_isBowDrawing && !_isAttacking;
        }
        
    #endregion
    }
}
