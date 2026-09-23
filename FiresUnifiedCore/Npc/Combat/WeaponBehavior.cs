using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Base class for weapon combat behaviors. Attacks run through a clone of the weapon's vanilla Attack:
    /// Start chains combos from the previous attack, animation events call OnAttackTrigger for hit detection, and
    /// Update runs every frame. When a runtime-assembled NPC never fires animation events, CompanionAttackBridge
    /// triggers the hit from animation progress instead.
    /// </summary>
    public abstract class WeaponBehavior
    {
        protected CombatContext Context { get; private set; }
        protected MonoBehaviour Owner { get; private set; }

        /// <summary>The item whose attacks this behaviour starts: the equipped weapon, or for bare hands the unarmed weapon.</summary>
        protected virtual ItemDrop.ItemData AttackWeapon => Context.CurrentWeapon;

        // Common state
        protected float _lastAttackTime;
        protected bool _isAttacking;
        protected int _attackChainLevel;
        
        // Attack timing - derived from weapon data
        protected float _weaponAttackInterval;
        protected float _animationDuration;
        protected float _effectiveCooldown;

        // Native attack system - we keep the Attack instance alive to receive callbacks
        protected Attack _activeAttackInstance;
        protected Attack _previousAttackInstance; // Track previous attack for combos
        protected bool _nativeHitTriggered;
        protected float _hitDelayTime;
        
        // Attack bridge for monitoring native callbacks
        protected CompanionAttackBridge _attackBridge;

        // Coroutine references for cleanup
        protected Coroutine _attackCoroutine;

        // Cached target
        protected Character _currentTarget;

        // Track if we're using native hit detection
        protected bool _usingNativeHitDetection;

        // COMBO SYSTEM - vanilla Attack.Start picks the chain level; this mirrors its choice for decisions.
        // Attack.cs:243 chains only when the previous attack ended at most this long ago.
        private const float VanillaChainWindow = 0.2f;
        private static readonly AccessTools.FieldRef<Attack, int> StartedChainLevelRef =
            AccessTools.FieldRefAccess<Attack, int>("m_currentAttackCainLevel");
        protected int _maxChainLevel;
        protected bool _comboActive;

        // SECONDARY ATTACK SYSTEM
        // Reduced chance to prefer primary/light attacks for better stamina management
        protected bool _hasSecondaryAttack;
        protected float _secondaryAttackChance = 0.08f;  // Reduced from 0.15 to prefer primary attacks
        protected float _lastSecondaryTime;
        protected float _secondaryCooldown = 8f;  // Increased from 5f to use secondaries less often
        
        // DECISION THROTTLING
        protected float _lastDecisionTime;
        protected const float MIN_DECISION_INTERVAL = 0.15f;
        protected const float ACTION_COMMIT_DURATION = 0.3f;
        protected float _actionCommitEndTime;
        protected bool _isCommittedToAction;
        
        // Log throttling to prevent spam
        protected float _lastStaminaLogTime;
        protected const float STAMINA_LOG_THROTTLE = 2.0f;
        protected bool _lastLoggedStaminaBlock;

        public virtual void Initialize(CombatContext context, MonoBehaviour owner)
  {
     Context = context;
      Owner = owner;
            
       // Try to get or add the attack bridge
   _attackBridge = owner.GetComponent<CompanionAttackBridge>();
            if (_attackBridge == null)
 {
            _attackBridge = owner.gameObject.AddComponent<CompanionAttackBridge>();
       }
  
      UpdateAttackTimingFromWeapon();
    }

        public virtual void Update()
        {
            UpdateNativeAttack();
            UpdateComboState();
            UpdateActionCommitment();

            if (_isAttacking && Context != null && !Context.IsAnimationLocked)
            {
                if (_attackCoroutine == null && (_activeAttackInstance == null || _activeAttackInstance.IsDone()))
                {
                    _isAttacking = false;
                }
            }
        }
   
        protected virtual void UpdateActionCommitment()
      {
   if (_isCommittedToAction && Time.time >= _actionCommitEndTime)
        {
       _isCommittedToAction = false;
}
        }
        
        protected bool CanMakeDecision()
   {
            if (_isCommittedToAction) return false;
       if (_isAttacking) return false;
 if (Time.time - _lastDecisionTime < MIN_DECISION_INTERVAL) return false;
         return true;
        }
        
        protected void CommitToDecision(float duration = -1f)
        {
      _lastDecisionTime = Time.time;
     _isCommittedToAction = true;
         _actionCommitEndTime = Time.time + (duration > 0 ? duration : ACTION_COMMIT_DURATION);
        }

    public virtual void OnActivate()
        {
 _attackChainLevel = 0;
          _comboActive = false;
            _isCommittedToAction = false;
            UpdateAttackTimingFromWeapon();
ConfigureAI();

            _hasSecondaryAttack = HasUsableSecondaryAttack();
          _maxChainLevel = Context.AttackChainLevels;

     if (CompanionCombat.VerboseLogging)
            {
   Debug.Log($"[{GetType().Name}] Activated - ChainLevels: {_maxChainLevel}, HasSecondary: {_hasSecondaryAttack}");
    }
   }

        public virtual void OnDeactivate()
        {
      CancelAttack();
   _comboActive = false;
            _attackChainLevel = 0;
          _isCommittedToAction = false;
        }

        public abstract void ConfigureAI();

        public virtual bool ShouldAttack(Character target)
        {
            if (_isAttacking) return false;
            if (!CanMakeDecision()) return false;
            
            // ==========================================
            // CHECK STAMINA MANAGER FIRST - This is the key check!
            // ==========================================
            var staminaManager = Owner?.GetComponent<StaminaManager>();
            if (staminaManager != null)
            {
                if (!staminaManager.CanAttack())
                {
                    // Only log on state change or throttle interval to prevent spam
                    // StaminaManager already logs its own state, so we only log here on state change
                    if (StaminaManager.CombatFlowLogging && (!_lastLoggedStaminaBlock || Time.time - _lastStaminaLogTime > STAMINA_LOG_THROTTLE))
                    {
                        Debug.Log($"[{GetType().Name}] ShouldAttack=FALSE: StaminaManager says no " +
                            $"(Recovery={staminaManager.IsRecovering()}, Critical={staminaManager.IsInCriticalRecovery()}, " +
                            $"Stamina={staminaManager.GetStaminaPercent():P0})");
                        _lastStaminaLogTime = Time.time;
                        _lastLoggedStaminaBlock = true;
                    }
                    return false;
                }
                _lastLoggedStaminaBlock = false;
            }
            
            // During active combo, use minimal cooldown to allow chaining
            // Vanilla Attack system requires < 0.2s for chain, but we check at animation end
            float cooldownToUse = _comboActive ? 0.1f : _effectiveCooldown;
            float timeSinceAttack = Time.time - _lastAttackTime;
            if (timeSinceAttack < cooldownToUse) return false;
      
            if (Context.CompanionAI == null) return false;
            if (Context.Character != null && (Context.Character.IsStaggering() || !Context.Character.CanMove())) return false;
            if (target == null || target.IsDead()) return false;
            if (Context != null && !Context.CanPlayAnimation(CombatContext.AnimationPriority.Attack)) return false;

            float distance = Vector3.Distance(Context.Transform.position, target.transform.position);
            if (distance > Context.AttackRange) return false;

            Vector3 dirToTarget = (target.transform.position - Context.Transform.position).normalized;
            float angle = Vector3.Angle(Context.Transform.forward, dirToTarget);
            if (angle > 120f) return false;

            // Check ThreatAnalyzer for tactical adjustments
            if (Context.ThreatAnalyzer != null)
            {
                var situation = Context.ThreatAnalyzer.GetCurrentSituation();
                
                // In survival/retreat mode, be more hesitant to attack
                if (situation.RecommendedStance == ThreatAnalyzer.CombatStance.Survival ||
                    situation.RecommendedStance == ThreatAnalyzer.CombatStance.Retreat)
                {
                    // Only attack if target is low health or we have a clear opening
                    var profile = Context.ThreatAnalyzer.GetThreatProfile(target);
                    if (profile.HealthPercent > 0.3f && target.InAttack())
                    {
                        // Don't attack while enemy is swinging at us in survival mode
                        return false;
                    }
                }
                
                // Apply aggression modifier - reduces attack frequency in defensive stances
                if (situation.AggressionModifier < 1f)
                {
                    // Random chance to skip attack based on aggression
                    if (Random.value > situation.AggressionModifier)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// A secondary exists when it has an animation (ItemDrop.ItemData.HaveSecondaryAttack). A spear throw spends the
        /// spear from the companion's own inventory (CompanionConsumablePatches) and it lands retrievable.
        /// </summary>
        private bool HasUsableSecondaryAttack()
        {
            var weapon = AttackWeapon;
            return weapon != null && weapon.HaveSecondaryAttack();
        }

        protected virtual bool ShouldUseSecondaryAttack(Character target)
        {
            if (!_hasSecondaryAttack) return false;
            if (Time.time - _lastSecondaryTime < _secondaryCooldown) return false;

            // Use ThreatAnalyzer for smarter secondary attack decisions
            if (Context.ThreatAnalyzer != null)
            {
                // Ask the threat analyzer if we should use secondary
                if (Context.ThreatAnalyzer.ShouldUseSecondaryAttack(target))
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[{GetType().Name}] ThreatAnalyzer recommends secondary attack");
                    return true;
                }
                
                var profile = Context.ThreatAnalyzer.GetThreatProfile(target);
                
                // Against bosses, use secondaries more often for damage
                if (profile.IsBoss && Random.value < 0.3f)
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[{GetType().Name}] Using secondary attack against boss");
                    return true;
                }
                
                // Against elites that can be staggered, try to stagger them
                if (profile.Classification >= ThreatAnalyzer.EnemyClass.Elite && profile.CanBeStaggered)
                {
                    if (Random.value < 0.25f)
                    {
                        if (CompanionCombat.VerboseLogging)
                            Debug.Log($"[{GetType().Name}] Using secondary to stagger elite");
                        return true;
                    }
                }
            }

            // Existing checks
            if (target.IsStaggering())
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] Using secondary attack - target staggered!");
                return true;
            }

            var targetHumanoid = target.GetComponent<Humanoid>();
            if (targetHumanoid != null && targetHumanoid.IsBlocking())
            {
                if (Random.value < 0.4f)
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[{GetType().Name}] Using secondary attack - target blocking!");
                    return true;
                }
            }

            if (Random.value < _secondaryAttackChance)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] Using secondary attack - random chance!");
                return true;
            }

            return false;
        }

  public abstract void ExecuteAttack(Character target);

    public virtual void CancelAttack()
        {
            _isAttacking = false;
            _nativeHitTriggered = false;
            _currentTarget = null;
            _usingNativeHitDetection = false;
            _comboActive = false;
            _attackChainLevel = 0;
            _isCommittedToAction = false;
   
            // Stop and abort the active attack
            if (_activeAttackInstance != null)
            {
                try { _activeAttackInstance.Abort(); } catch { }
            }
            
            // Clear the active attack from the bridge
            _attackBridge?.ClearActiveAttack();
            _activeAttackInstance = null;
            // Note: Don't clear _previousAttackInstance here - it may be needed for next combo

            if (Context != null)
            {
                Context.ReleaseAnimationLock();
            }

            if (_attackCoroutine != null)
            {
                Owner.StopCoroutine(_attackCoroutine);
                _attackCoroutine = null;
            }
        }

        public virtual void UpdateAttackData()
        {
 _attackChainLevel = 0;
      _comboActive = false;
     UpdateAttackTimingFromWeapon();

            _hasSecondaryAttack = HasUsableSecondaryAttack();
       _maxChainLevel = Context.AttackChainLevels;

  if (CompanionCombat.VerboseLogging)
          {
                Debug.Log($"[{GetType().Name}] Attack configured: range={Context.AttackRange:F1}, " +
   $"interval={_weaponAttackInterval:F2}s, animDuration={_animationDuration:F2}s, " +
       $"effectiveCooldown={_effectiveCooldown:F2}s, chainLevels={_maxChainLevel}, hasSecondary={_hasSecondaryAttack}");
            }
        }

        protected virtual void UpdateAttackTimingFromWeapon()
        {
       _weaponAttackInterval = Context.AIAttackInterval;

        if (_weaponAttackInterval <= 0 && Context.AttackStamina > 0)
   {
         _weaponAttackInterval = Mathf.Max(0.5f, Context.AttackStamina / 20f);
            }

            if (_weaponAttackInterval <= 0)
            {
     _weaponAttackInterval = Context.BaseAttackCooldown;
}

 _animationDuration = Context.GetEstimatedAttackDuration();
            _effectiveCooldown = Mathf.Max(_weaponAttackInterval, _animationDuration);
     _effectiveCooldown += 0.1f;
            _maxChainLevel = Context.AttackChainLevels;

     if (CompanionCombat.VerboseLogging)
  {
        Debug.Log($"[{GetType().Name}] Attack timing updated: " +
        $"weaponInterval={_weaponAttackInterval:F2}s, " +
          $"animDuration={_animationDuration:F2}s, " +
       $"effectiveCooldown={_effectiveCooldown:F2}s, " +
    $"maxChain={_maxChainLevel}");
            }
      }

        /// <summary>
        /// The chain lapses once the last attack has been over for longer than vanilla's window
        /// (Humanoid.m_timeSinceLastAttack, Humanoid.cs:333-338).
        /// </summary>
        protected virtual void UpdateComboState()
        {
            if (!_comboActive || _isAttacking || Context.Humanoid == null) return;
            if (Context.Humanoid.GetTimeSinceLastAttack() <= VanillaChainWindow) return;

            _comboActive = false;
            _attackChainLevel = 0;

            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] Combo window expired, resetting chain");
            }
        }

      protected virtual void AdvanceComboChain()
        {
 if (_maxChainLevel <= 1) return;

  _comboActive = true;

   int previousLevel = _attackChainLevel;
            _attackChainLevel = (_attackChainLevel + 1) % _maxChainLevel;

            if (CompanionCombat.VerboseLogging)
  {
    Debug.Log($"[{GetType().Name}] Combo advanced: {previousLevel} -> {_attackChainLevel} (max: {_maxChainLevel})");
        }
        }

      public bool IsAttacking => _isAttacking;
        public float AttackCooldown => _effectiveCooldown;
        public float CooldownRemaining => Mathf.Max(0f, _effectiveCooldown - (Time.time - _lastAttackTime));
        public int CurrentChainLevel => _attackChainLevel;
        public bool IsComboActive => _comboActive;
        public bool IsCommittedToAction => _isCommittedToAction;

        #region Protected Helpers

     protected void FaceTarget(Character target)
        {
            if (target == null) return;
            FaceDirectionThroughAuthority(target.transform.position - Context.Transform.position, "CompanionWeapon");
        }

        /// <summary>
        /// Faces a direction through the FacingAuthority at Animation priority — precise aim during an
        /// attack wins over AI's general enemy-facing (Combat) and releases back to it between attacks.
        /// Direct rotation write only as a no-authority fallback. Shared by the bow/crossbow overrides.
        /// </summary>
        protected void FaceDirectionThroughAuthority(Vector3 dir, string owner)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            var facing = Context?.Companion != null ? Context.Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                // Park if a same-priority incumbent holds facing; never raw-write against the holder.
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.Animation, owner, 0.4f))
                    facing.SetLookDirection(owner, dir);
                return;
            }
            Context.Transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        /// <summary>
        /// Starts a native attack using Valheim's Attack system.
        /// Properly passes previousAttack for combo chain support.
        /// </summary>
        protected bool TryStartNativeAttack(Character target, bool secondaryAttack = false)
        {
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] ========== TryStartNativeAttack START ==========");
                Debug.Log($"[{GetType().Name}] Target: {target?.m_name ?? "NULL"}, SecondaryAttack: {secondaryAttack}");
            }

            if (Context.Humanoid == null || AttackWeapon == null)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: No humanoid or weapon");
                return false;
            }

            var shared = AttackWeapon.m_shared;
            if (shared == null)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: Weapon has no SharedData");
                return false;
            }

            Attack attackTemplate = secondaryAttack ? shared.m_secondaryAttack : shared.m_attack;
            if (attackTemplate == null)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: No attack template");
                return false;
            }

            // Check and consume stamina
            if (!TryConsumeAttackStamina(attackTemplate))
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: Not enough stamina");
                return false;
            }

            float attackDuration = _animationDuration;

            // Humanoid.StartAttack's inputs (Humanoid.cs:199): the last attack started and the time since an attack
            // animation last ended; Attack.Start continues the chain from them (Attack.cs:239-245).
            Attack previousAttack = _previousAttackInstance;
            float timeSinceLastAttack = Context.Humanoid.GetTimeSinceLastAttack();
            bool chainedAttack = !secondaryAttack && attackTemplate.m_attackChainLevels > 1;

            if (!Context.TryLockAnimation(attackTemplate.m_attackAnimation, attackDuration, CombatContext.AnimationPriority.Attack))
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: Animation lock failed");
                return false;
            }

            // Clone the attack for this instance
            _activeAttackInstance = attackTemplate.Clone();
            _currentTarget = target;
            _nativeHitTriggered = false;
            
            // Get animation-based hit timing
            float hitNormalizedTime = CompanionAttackBridge.GetHitNormalizedTime(Context.WeaponAnimationState);
            float fallbackDelay = GetHitDelayForAttackType(attackTemplate.m_attackType);
            _hitDelayTime = fallbackDelay;

            var body = Owner.GetComponent<Rigidbody>();
            var visEquip = Owner.GetComponent<VisEquipment>();
            float drawPercentage = 1f;

            bool nativeStarted = false;

            // Try to start via native Attack.Start()
            if (Context.AnimEvent != null)
            {
                try
                {
                    nativeStarted = _activeAttackInstance.Start(
                        Context.Humanoid,
                        body,
                        Context.ZAnim,
                        Context.AnimEvent,
                        visEquip,
                        AttackWeapon,
                        previousAttack,
                        timeSinceLastAttack,
                        drawPercentage
                    );

                    if (nativeStarted && CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[{GetType().Name}] Attack.Start() succeeded - vanilla will handle animation and timing");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[{GetType().Name}] Attack.Start() exception: {ex.Message}");
                    nativeStarted = false;
                }
            }

            if (nativeStarted && chainedAttack)
            {
                _attackChainLevel = StartedChainLevelRef(_activeAttackInstance);
            }
            string actualAnimName = chainedAttack
                ? $"{attackTemplate.m_attackAnimation}{_attackChainLevel}"
                : attackTemplate.m_attackAnimation;

            // Register with the attack bridge for animation monitoring (fallback if animation events don't fire)
            _attackBridge?.SetActiveAttack(_activeAttackInstance, actualAnimName, hitNormalizedTime, fallbackDelay);
            
            // If native start failed, play animation manually
            if (!nativeStarted)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] Native start failed - playing animation manually");
                Context.PlayAttackAnimation(actualAnimName, Context.GetAttackAnimationIndex());
            }

            _lastAttackTime = Time.time;
            _isAttacking = true;
            _usingNativeHitDetection = true;
            
            // Notify StaminaManager that we attacked
            Owner?.GetComponent<StaminaManager>()?.OnAttackPerformed();
            
            CommitToDecision(attackDuration);

            if (nativeStarted)
            {
                _previousAttackInstance = _activeAttackInstance;
            }

            if (secondaryAttack)
            {
                _lastSecondaryTime = Time.time;
                _comboActive = false;
                _attackChainLevel = 0;
            }
            else
            {
                AdvanceComboChain();
            }

            // Start coroutine as safety net (CompanionAttackBridge handles primary timing now)
            _attackCoroutine = Owner.StartCoroutine(TrackAttackAndTriggerHit(target, attackDuration, secondaryAttack));

            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] ATTACK STARTED - native={nativeStarted}, anim={actualAnimName}, " +
                    $"chain={_attackChainLevel}/{_maxChainLevel}, hitNorm={hitNormalizedTime:P0}");
            }
            
            return true;
        }

        /// <summary>
        /// Safety net coroutine that monitors the attack and ensures cleanup.
        /// The native Attack system handles hit timing via animation events.
        /// This coroutine only acts as a fallback if animation events don't fire.
        /// 
        /// IMPORTANT: We no longer delay for hitDelay - the native system handles timing.
        /// We just poll to check if hit detection fired, and only intervene if it didn't.
        /// </summary>
        protected IEnumerator TrackAttackAndTriggerHit(Character target, float totalDuration, bool isSecondary = false)
        {
            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[{GetType().Name}] Attack monitor started - duration: {totalDuration:F2}s");
            
            // Poll periodically to check if the native system triggered the hit
            // We DON'T add delay - the native Attack.Start() handles timing
            float elapsed = 0f;
            float pollInterval = 0.05f; // Check every 50ms
            float fallbackCheckTime = totalDuration * 0.8f; // Only intervene near end if nothing happened
            bool hitWasTriggered = false;
            
            while (elapsed < totalDuration)
            {
                yield return new WaitForSeconds(pollInterval);
                elapsed += pollInterval;
                
                // Check if companion died
                if (Context.Companion?.isDefeated ?? true)
                {
                    _attackCoroutine = null;
                    yield break;
                }
                
                // Check if attack completed naturally
                if (_activeAttackInstance == null || _activeAttackInstance.IsDone())
                {
                    hitWasTriggered = true;
                    break;
                }
                
                // Check if CompanionAttackBridge triggered the hit via animation monitoring
                if (_attackBridge?.WasAttackTriggered ?? false)
                {
                    hitWasTriggered = true;
                    if (!_nativeHitTriggered)
                    {
                        _nativeHitTriggered = true;
                        RaiseWeaponSkill(isSecondary ? 1.2f : 1f);
                    }
                    break;
                }
                
                // Check if native hit was triggered via some other path
                if (_nativeHitTriggered)
                {
                    hitWasTriggered = true;
                    break;
                }
                
                // Safety fallback: If we're near the end and nothing triggered, do manual hit detection
                if (elapsed >= fallbackCheckTime && !hitWasTriggered)
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[{GetType().Name}] Safety fallback: Animation events didn't fire, triggering manually");
                    
                    try
                    {
                        if (_activeAttackInstance != null)
                        {
                            _activeAttackInstance.OnAttackTrigger();
                            _nativeHitTriggered = true;
                            hitWasTriggered = true;
                            RaiseWeaponSkill(isSecondary ? 1.2f : 1f);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[{GetType().Name}] Safety fallback OnAttackTrigger failed: {ex.Message}");
                        DoFallbackHitDetection(isSecondary ? 1.2f : 1f);
                        hitWasTriggered = true;
                    }
                    break;
                }
            }

            // Wait for remaining duration if we broke out early
            if (elapsed < totalDuration)
            {
                yield return new WaitForSeconds(totalDuration - elapsed);
            }

            if (CompanionCombat.VerboseLogging && hitWasTriggered)
                Debug.Log($"[{GetType().Name}] Attack completed - hit was triggered");

            FinishAttack();
            _attackCoroutine = null;
        }
        
        /// <summary>
        /// Raises the skill for the current weapon type.
        /// Called when an attack successfully lands.
      /// </summary>
     protected virtual void RaiseWeaponSkill(float factor = 1f)
      {
          if (AttackWeapon?.m_shared == null) return;
        if (Context.CompanionSkills == null) return;
     
       var skillType = AttackWeapon.m_shared.m_skillType;
            if (skillType != Skills.SkillType.None)
 {
           Context.CompanionSkills.RaiseSkill(skillType, factor);

       if (CompanionCombat.VerboseLogging)
    {
          Debug.Log($"[{GetType().Name}] Raised skill {skillType} by {factor:F2}");
    }
         }
        }

        protected virtual float GetHitDelayForAttackType(Attack.AttackType attackType)
        {
     return Context.GetEstimatedHitDelay();
        }

        /// <summary>
        /// Checks if companion has enough stamina for the attack and consumes it.
        /// Returns false if not enough stamina.
        /// </summary>
        protected virtual bool TryConsumeAttackStamina(Attack attackTemplate)
        {
            if (attackTemplate == null) return true; // No attack template = no stamina cost
            
            var stats = Context.Companion?.GetStats();
            if (stats == null) return true; // No stats system = unlimited stamina (fallback)
            
            float baseCost = attackTemplate.m_attackStamina;
            if (baseCost <= 0) return true; // Free attack
            
            // Get skill-adjusted cost
            var skills = Context.Companion?.GetSkills();
            float adjustedCost = baseCost;
            
            if (skills != null && AttackWeapon != null)
            {
                var weaponSkill = AttackWeapon.m_shared?.m_skillType ?? Skills.SkillType.None;
                adjustedCost = stats.GetAttackStaminaCost(baseCost, weaponSkill);
            }
            
            // Try to use stamina
            if (stats.UseStamina(adjustedCost))
            {
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[{GetType().Name}] Consumed {adjustedCost:F1} stamina (base: {baseCost:F1})");
                }
                return true;
            }
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] Not enough stamina: need {adjustedCost:F1}, have {stats.CurrentStamina:F1}");
            }
            return false;
        }

   /// <summary>
      /// Fallback hit detection - only used if Attack.OnAttackTrigger() fails.
        /// </summary>
     protected virtual void DoFallbackHitDetection(float damageMultiplier = 1f)
        {
            if (Context.Humanoid == null || AttackWeapon == null)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] DoFallbackHitDetection: no humanoid or weapon");
                return;
            }

            float attackRange = Context.AttackRange > 0 ? Context.AttackRange : 2.5f;
float attackAngle = Context.AttackAngle > 0 ? Context.AttackAngle : 90f;

 Vector3 attackOrigin = Context.Transform.position + Vector3.up * 1.2f;
       Vector3 attackDir = Context.Transform.forward;

     var charactersInRange = new List<Character>();
         var allCharacters = Character.GetAllCharacters();

            foreach (var character in allCharacters)
            {
        if (character == null || character == Context.Character) continue;
     if (character.IsDead()) continue;

            float dist = Vector3.Distance(attackOrigin, character.transform.position + Vector3.up);

  if (dist <= attackRange + 0.5f)
 {
      Vector3 dirToTarget = (character.transform.position - attackOrigin).normalized;
            float angle = Vector3.Angle(attackDir, dirToTarget);

            if (angle <= attackAngle / 2f && BaseAI.IsEnemy(Context.Character, character))
              {
        charactersInRange.Add(character);
     }
    }
      }

            foreach (var hitTarget in charactersInRange)
            {
                // CreateWeaponHitData applies the attacker's SEMan.ModifyAttack last, as vanilla does before Damage.
                HitData hit = Context.EquipmentData.CreateWeaponHitData(hitTarget, Context.Character, damageMultiplier);
                hitTarget.Damage(hit);
                PlayWeaponHitEffects(hitTarget, hit);
                RaiseWeaponSkill(damageMultiplier);
            }

            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[{GetType().Name}] DoFallbackHitDetection hit {charactersInRange.Count} target(s)");
  }

  protected void ExecuteFallbackAttack(Character target, float hitDelay = -1f, bool isSecondary = false)
        {
        if (CompanionCombat.VerboseLogging)
            Debug.Log($"[{GetType().Name}] ========== ExecuteFallbackAttack START ==========");

            if (AttackWeapon == null)
            {
          if (CompanionCombat.VerboseLogging)
              Debug.Log($"[{GetType().Name}] FAILED: No weapon equipped");
      return;
            }

            var shared = AttackWeapon.m_shared;
  Attack attackTemplate = isSecondary ? shared?.m_secondaryAttack : shared?.m_attack;

     if (attackTemplate == null)
         {
   if (CompanionCombat.VerboseLogging)
       Debug.Log($"[{GetType().Name}] FAILED: No attack template for fallback");
      return;
   }

            // Check and consume stamina
            if (!TryConsumeAttackStamina(attackTemplate))
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[{GetType().Name}] FAILED: Not enough stamina for fallback attack");
                return;
            }

          if (hitDelay < 0) hitDelay = GetHitDelayForAttackType(attackTemplate.m_attackType);
 float duration = _animationDuration > 0 ? _animationDuration : 1f;
      float damageMultiplier = isSecondary ? 1.2f : 1f;

 string animTrigger = isSecondary ? attackTemplate.m_attackAnimation : Context.GetAttackAnimationTrigger(_attackChainLevel);

 Context.PlayAttackAnimation(animTrigger, Context.GetAttackAnimationIndex());

            _lastAttackTime = Time.time;
     _isAttacking = true;
         _usingNativeHitDetection = false;
            _currentTarget = target;

            CommitToDecision(duration);

    if (isSecondary) _lastSecondaryTime = Time.time;

          _attackCoroutine = Owner.StartCoroutine(FallbackAttackCoroutine(hitDelay, duration, damageMultiplier));

  if (!isSecondary) AdvanceComboChain();

            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[{GetType().Name}] ========== ExecuteFallbackAttack END ==========");
     }

        private IEnumerator FallbackAttackCoroutine(float hitDelay, float duration, float damageMultiplier = 1f)
        {
  yield return new WaitForSeconds(hitDelay);

     if (Context.Companion?.isDefeated ?? true)
            {
 _attackCoroutine = null;
   yield break;
         }

         DoFallbackHitDetection(damageMultiplier);

            float remainingTime = duration - hitDelay;
            if (remainingTime > 0)
         {
          yield return new WaitForSeconds(remainingTime);
            }

     FinishAttack();
            _attackCoroutine = null;
        }

        protected virtual void UpdateNativeAttack()
        {
            if (_activeAttackInstance != null && _usingNativeHitDetection)
            {
                // CRITICAL: Call Attack.Update() every frame!
                // This lets vanilla attack system manage timing, projectiles, and state
                try
                {
                    _activeAttackInstance.Update(Time.deltaTime);
                }
                catch (System.Exception ex)
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.LogWarning($"[{GetType().Name}] Attack.Update() exception: {ex.Message}");
                }
                
                // Check if attack completed - this is now the PRIMARY completion path
                if (_activeAttackInstance.IsDone())
                {
                    // Ensure skill was raised if hit was triggered
                    if (!_nativeHitTriggered && (_attackBridge?.WasAttackTriggered ?? false))
                    {
                        _nativeHitTriggered = true;
                        RaiseWeaponSkill(1f);
                    }
                    
                    FinishAttack();
                }
            }
        }

        protected virtual void FinishAttack()
        {
            _isAttacking = false;
            _nativeHitTriggered = false;
            _currentTarget = null;
            _usingNativeHitDetection = false;
        
            _attackBridge?.ClearActiveAttack();
            // Store as previous for combo chain, then clear active
            // _previousAttackInstance is already set in TryStartNativeAttack
            _activeAttackInstance = null;
     
            Context?.ReleaseAnimationLock();

            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] Attack finished - chain level {_attackChainLevel}/{_maxChainLevel}, comboActive={_comboActive}");
            }
        }

        protected virtual void PlayWeaponHitEffects(Character target, HitData hit)
        {
            var hitEffect = AttackWeapon?.m_shared?.m_hitEffect;
            if (hitEffect == null || !FiresCore.Npc.Core.NpcFxRange.NearAnyPlayer(hit.m_point)) return;
            hitEffect.Create(hit.m_point, Quaternion.LookRotation(hit.m_dir));
        }

        #endregion
    }
}
