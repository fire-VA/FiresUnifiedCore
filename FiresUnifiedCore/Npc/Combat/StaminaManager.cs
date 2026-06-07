using UnityEngine;
using System;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Manages stamina for companion combat decisions.
    /// Prevents the AI from exhausting itself and being unable to dodge/block.
    /// 
    /// DESIGN PRINCIPLE:
    /// Good players always keep stamina reserve for defensive actions.
    /// This system ensures companions don't spam attacks until they can't dodge.
    /// 
    /// STAMINA BUDGET:
    /// - Always reserve enough for 1 dodge
    /// - Don't attack if we can't also block afterward
    /// - Sprint only when necessary, not by default
    /// - Regenerate awareness (pause attacks when low)
    /// 
    /// THROTTLING:
    /// Prevents rapid stamina-consuming actions to avoid exhaustion.
    /// </summary>
public class StaminaManager : MonoBehaviour
  {
      #region Settings

    [Header("Stamina Reserves")]
    [Tooltip("Always keep this much stamina available for dodge")]
        public float dodgeReserve = 20f;
   
        [Tooltip("Stamina needed for a single dodge")]
        public float dodgeCost = 15f;
        
        [Tooltip("Stamina needed for blocking")]
      public float blockCost = 10f;
        
     [Tooltip("Percentage of max stamina to reserve for emergencies")]
        [Range(0.1f, 0.5f)]
        public float emergencyReservePercent = 0.2f;

        [Header("Attack Management")]
        [Tooltip("Minimum stamina percentage to initiate attack combo")]
        [Range(0.2f, 0.8f)]
        public float minStaminaToAttack = 0.50f;  // Increased from 0.35 - need healthy stamina to attack
     
        [Tooltip("Stamina percentage to stop attacking and recover")]
        [Range(0.1f, 0.5f)]
        public float recoveryThreshold = 0.40f;  // Increased from 0.25 - enter recovery EARLIER
  
        [Tooltip("Stamina percentage to resume attacking after recovery")]
        [Range(0.6f, 0.95f)]
        public float resumeThreshold = 0.85f;  // Increased from 0.6 - wait until NEARLY FULL
        
        [Header("Stamina-Aware Attack Pacing")]
        [Tooltip("When stamina is between minStaminaToAttack and this threshold, space attacks further apart")]
        [Range(0.5f, 0.9f)]
        public float conservativeAttackThreshold = 0.70f;
        
        [Tooltip("Extra delay between attacks when in conservative mode (seconds)")]
        [Range(0.5f, 3f)]
        public float conservativeAttackDelay = 1.5f;
        
        [Tooltip("Minimum attacks to allow stamina regeneration between them")]
        [Range(1, 5)]
        public int minAttacksBeforePacing = 2;

        [Header("Sprint Management")]
        [Tooltip("Only sprint if stamina is above this percentage")]
      [Range(0.3f, 0.8f)]
        public float sprintThreshold = 0.5f;
   
        [Tooltip("Stop sprinting when stamina drops to this percentage")]
   [Range(0.2f, 0.5f)]
      public float sprintStopThreshold = 0.3f;

        [Header("Recovery Boost")]
        [Tooltip("Stamina regeneration multiplier when actively retreating due to critical stamina")]
        [Range(1.0f, 3.0f)]
        public float criticalRetreatRegenBoost = 2.0f;  // 100% faster regen while in critical retreat
        
        [Tooltip("Stamina regeneration multiplier during flee state (after initial boost)")]
        [Range(1.0f, 3.0f)]
        public float fleeStateRegenBoost = 1.75f;  // 75% faster regen while fleeing
        
        [Tooltip("Instant stamina boost percentage when entering flee (0.2 = 20%)")]
        [Range(0.1f, 0.5f)]
        public float fleeInstantBoostPercent = 0.20f;  // 20% instant boost when entering flee

        [Header("Throttling")]
        [Tooltip("Minimum time between stamina-heavy actions")]
        public float actionThrottleTime = 0.3f;
        
        [Tooltip("Time to wait after dodging before attacking")]
        public float postDodgeCooldown = 0.5f;
  
        [Tooltip("Time to wait after blocking before attacking")]
        public float postBlockCooldown = 0.3f;

        [Header("Level-Based Improvements")]
        [Tooltip("Minimum stamina % to maintain (improves with level)")]
        public float baseMinStaminaToMaintain = 0.15f;
        [Tooltip("Additional % per companion level (e.g. 0.003 = +0.3% per level, +30% at level 100)")]
        public float levelStaminaMaintenanceBonus = 0.003f;
        [Tooltip("Base action throttle time reduction per level (better timing with experience)")]
        public float levelThrottleReduction = 0.002f;

        #endregion

        #region Components

        private CompanionController _companion;
        private Character _character;
        private CompanionEquipmentData _equipmentData;
        private CompanionProgression _progression;
        private CompanionStats _companionStats;  // THIS is where stamina is actually tracked!

        #endregion

        #region State

        // Current stamina state
        private float _currentStamina;
        private float _maxStamina;
        private bool _isRecovering;
        private bool _isSprinting;
        
        // Recovery behavior
        private bool _isInCriticalRecovery;  // True when stamina hit critical, need to fully retreat
        private float _criticalRecoveryStartTime;
        private float _normalRecoveryStartTime;
        private const float CRITICAL_RECOVERY_MIN_TIME = 4f;  // Minimum time to stay in critical recovery
        private const float NORMAL_RECOVERY_MIN_TIME = 2f;    // Minimum time in normal recovery before exiting
        private const float CRITICAL_STAMINA_THRESHOLD = 0.15f;  // Enter critical below this (was 0.05)
        
        // Flee state tracking
        private bool _isInFleeState;  // True when companion is actively fleeing
        private float _fleeStateStartTime;
        
        // Action timing
        private float _lastActionTime;
        private float _lastDodgeTime;
        private float _lastBlockTime;
        private float _lastAttackTime;
        
        // Cached costs from equipment
        private float _cachedAttackCost = 10f;
        private float _cachedSecondaryAttackCost = 15f;
        private float _cachedDodgeCost = 15f;
        private float _cachedBlockCost = 10f;
        
        // Attack pacing state
        private int _consecutiveAttacks = 0;
        private float _lastAttackPacingCheck = 0f;
        private float _nextAllowedAttackTime = 0f;
        
        // Level-adjusted thresholds (cached for performance)
        private float _adjustedRecoveryThreshold;
        private float _adjustedResumeThreshold;
        private float _adjustedActionThrottle;
        private int _cachedLevel = -1;
        
        // Archetype modifiers (set by ArchetypeController)
        private float _archetypeMaxStaminaMult = 1.0f;
        private float _archetypeStaminaRegenMult = 1.0f;
        private float _archetypeBlockCostMult = 1.0f;
        private float _archetypeDodgeCostMult = 1.0f;
        private float _archetypeAttackCostMult = 1.0f;
        
        public static bool VerboseLogging = false;
        
        // COMBAT FLOW LOGGING - Enable to trace all stamina decisions
        // Note: Throttled to prevent log spam - only logs state changes, not every frame
        public static bool CombatFlowLogging = false;
        
        // Throttle logging to prevent spam
        private float _lastLogTime;
        private const float LOG_THROTTLE_INTERVAL = 2.0f;
        private bool _lastLoggedCriticalState;
        private bool _lastLoggedRecoveryState;

  #endregion

    #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _character = GetComponent<Character>();
            _equipmentData = GetComponent<CompanionEquipmentData>();
            _progression = GetComponent<CompanionProgression>();
            _companionStats = GetComponent<CompanionStats>();  // Get the ACTUAL stamina tracker
        }

        private void Start()
        {
            // Initialize stamina from character
            UpdateStaminaFromCharacter();
            UpdateCostsFromEquipment();
            UpdateLevelAdjustedThresholds();
        }

        private void Update()
        {
            // Allow both tamed AND wild companions to manage stamina
            if (_companion == null) return;
            
            // Update stamina values
            UpdateStaminaFromCharacter();
            
            // Update level-adjusted thresholds if level changed
            UpdateLevelAdjustedThresholds();
            
            // Check recovery state
            UpdateRecoveryState();
        }

        #endregion

   #region Public API - Queries

        /// <summary>
      /// Gets current stamina as a percentage (0-1).
 /// </summary>
        public float GetStaminaPercent()
        {
  if (_maxStamina <= 0) return 1f;
       return _currentStamina / _maxStamina;
        }

 /// <summary>
  /// Gets current absolute stamina value.
   /// </summary>
        public float GetCurrentStamina()
{
          return _currentStamina;
      }

        /// <summary>
 /// Gets maximum stamina value.
        /// </summary>
        public float GetMaxStamina()
        {
          return _maxStamina;
        }

        /// <summary>
        /// Checks if we have enough stamina to perform an action safely.
        /// This includes the emergency reserve.
        /// </summary>
   public bool HasStaminaFor(StaminaAction action)
        {
        float cost = GetActionCost(action);
       float reserve = GetEmergencyReserve();
   return _currentStamina >= cost + reserve;
        }

        /// <summary>
        /// Checks if we can attack without exhausting ourselves.
        /// Higher level companions have better timing and can attack more efficiently.
        /// Uses stamina-aware pacing to space attacks when stamina is getting low.
        /// </summary>
        public bool CanAttack()
        {
            // CRITICAL: Never attack during critical recovery - must fully retreat
            if (_isInCriticalRecovery)
            {
                // Only log on state change or throttle interval to prevent spam
                if (CombatFlowLogging && (!_lastLoggedCriticalState || Time.time - _lastLogTime > LOG_THROTTLE_INTERVAL))
                {
                    Debug.Log($"[StaminaManager] CanAttack=FALSE: In critical recovery (stamina={GetStaminaPercent():P0})");
                    _lastLogTime = Time.time;
                    _lastLoggedCriticalState = true;
                }
                return false;
            }
            _lastLoggedCriticalState = false;
            
            if (_isRecovering)
            {
                // Only log on state change or throttle interval to prevent spam
                if (CombatFlowLogging && (!_lastLoggedRecoveryState || Time.time - _lastLogTime > LOG_THROTTLE_INTERVAL))
                {
                    Debug.Log($"[StaminaManager] CanAttack=FALSE: In recovery mode (stamina={GetStaminaPercent():P0})");
                    _lastLogTime = Time.time;
                    _lastLoggedRecoveryState = true;
                }
                return false;
            }
            _lastLoggedRecoveryState = false;
            
            if (Time.time - _lastDodgeTime < postDodgeCooldown)
            {
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] CanAttack=FALSE: Post-dodge cooldown");
                return false;
            }
            
            if (Time.time - _lastBlockTime < postBlockCooldown)
            {
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] CanAttack=FALSE: Post-block cooldown");
                return false;
            }
            
            if (Time.time - _lastActionTime < _adjustedActionThrottle)
            {
                return false; // Don't log this one, too spammy
            }
            
            // Check basic stamina requirement
            float staminaPercent = GetStaminaPercent();
            if (staminaPercent < minStaminaToAttack || !HasStaminaFor(StaminaAction.Attack))
            {
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] CanAttack=FALSE: Stamina too low ({staminaPercent:P0} < {minStaminaToAttack:P0})");
                return false;
            }
            
            // STAMINA-AWARE PACING: When stamina is getting low but not yet in recovery,
            // space attacks further apart to allow regeneration between them.
            // This prevents the companion from depleting stamina through rapid attacks.
            if (staminaPercent < conservativeAttackThreshold)
            {
                // Check if we need to wait longer between attacks
                if (Time.time < _nextAllowedAttackTime)
                {
                    if (CombatFlowLogging)
                        Debug.Log($"[StaminaManager] CanAttack=FALSE: Conservative pacing - wait {_nextAllowedAttackTime - Time.time:F1}s more");
                    return false;
                }
                
                // Also check if we can afford multiple attacks + a dodge
                // Smart stamina management means keeping a reserve
                float attacksNeeded = Mathf.Min(minAttacksBeforePacing, _consecutiveAttacks + 1);
                float staminaNeeded = (_cachedAttackCost * attacksNeeded) + _cachedDodgeCost;
                
                if (_currentStamina < staminaNeeded)
                {
                    // Can't afford attack + dodge reserve, delay attack
                    _nextAllowedAttackTime = Time.time + conservativeAttackDelay;
                    if (CombatFlowLogging)
                        Debug.Log($"[StaminaManager] CanAttack=FALSE: Need stamina reserve ({_currentStamina:F0} < {staminaNeeded:F0}), delaying {conservativeAttackDelay:F1}s");
                    return false;
                }
            }
            
            return true;
        }

        /// <summary>
        /// Checks if we can dodge.
        /// SURVIVAL INSTINCT: Low stamina = as dangerous as low health.
        /// If we dodge without stamina, we'll be completely defenseless.
        /// </summary>
        public bool CanDodge()
        {
            // SURVIVAL INSTINCT: In critical recovery, almost NEVER dodge
            // Dodging would consume stamina we desperately need to regenerate
            // Only exception: if we would die otherwise (handled by threat level in calling code)
            if (_isInCriticalRecovery)
            {
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] CanDodge=FALSE: CRITICAL RECOVERY - must retreat, not dodge!");
                return false;  // Hard no - retreating is safer than dodging
            }
            
            // In normal recovery, be very conservative about dodging
            if (_isRecovering)
            {
                // Only dodge if we have MORE than enough stamina
                // This ensures we don't deplete during recovery
                if (_currentStamina < _cachedDodgeCost * 2f)
                {
                    if (CombatFlowLogging)
                        Debug.Log($"[StaminaManager] CanDodge=FALSE: In recovery, need 2x dodge cost ({_cachedDodgeCost * 2f}), have {_currentStamina}");
                    return false;
                }
            }
            
            // Cooldown check - stricter during recovery
            float dodgeCooldown = _isRecovering ? 
                _adjustedActionThrottle * 3 : _adjustedActionThrottle * 2;
            
            if (Time.time - _lastDodgeTime < dodgeCooldown)
            {
                return false; // Don't log, too spammy
            }
            
            // Must have enough stamina for the dodge
            bool canDodge = _currentStamina >= _cachedDodgeCost;
            if (!canDodge && CombatFlowLogging)
            {
                Debug.Log($"[StaminaManager] CanDodge=FALSE: Not enough stamina ({_currentStamina} < {_cachedDodgeCost})");
            }
            return canDodge;
        }

        /// <summary>
        /// Checks if we can block.
        /// SURVIVAL INSTINCT: Blocking costs stamina too - can't afford it during recovery.
        /// </summary>
        public bool CanBlock()
        {
            // SURVIVAL INSTINCT: In critical recovery, NO blocking
            // Every point of stamina needs to regenerate
            if (_isInCriticalRecovery)
            {
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] CanBlock=FALSE: CRITICAL RECOVERY - must retreat, not block!");
                return false;
            }
            
            // In normal recovery, only block if we have plenty of stamina
            if (_isRecovering)
            {
                // Need double the cost to block during recovery
                if (_currentStamina < _cachedBlockCost * 2f)
                {
                    if (CombatFlowLogging)
                        Debug.Log($"[StaminaManager] CanBlock=FALSE: In recovery, need 2x block cost ({_cachedBlockCost * 2f}), have {_currentStamina}");
                    return false;
                }
            }
            
            bool canBlock = _currentStamina >= _cachedBlockCost;
            if (!canBlock && CombatFlowLogging)
            {
                Debug.Log($"[StaminaManager] CanBlock=FALSE: Not enough stamina ({_currentStamina} < {_cachedBlockCost})");
            }
            return canBlock;
        }
        
        /// <summary>
        /// Checks if stamina is so low it should be treated like low health.
        /// When true, companion should prioritize retreat over ALL defensive actions.
        /// </summary>
        public bool IsStaminaCriticallyLow()
        {
            return _isInCriticalRecovery || GetStaminaPercent() < CRITICAL_STAMINA_THRESHOLD;
        }
        
        /// <summary>
        /// Returns true if we should treat current stamina state as a survival emergency.
        /// This means: retreat, don't fight, don't even block - just RUN.
        /// 
        /// IMPORTANT: This should only trigger flee at TRULY critical levels.
        /// Rangers/Berserkers at 25% stamina should NOT flee - they can still fight.
        /// Only flee when stamina is so low the companion can't even defend itself.
        /// </summary>
        public bool ShouldTreatAsLowHealth()
        {
            // Critical recovery = always treat as emergency
            // This is set when stamina drops below CRITICAL_STAMINA_THRESHOLD (15%)
            if (_isInCriticalRecovery) return true;
            
            // Very low stamina (can't even dodge once) = emergency
            // This catches the case where stamina dropped below 15% then recovered slightly
            // but is still dangerously low (can't afford a single dodge)
            if (_currentStamina < _cachedDodgeCost) return true;
            
            // NOTE: Removed the "in recovery with low stamina" check that triggered at 30%
            // This was causing companions to flee too easily. Recovery mode just means
            // they should stop attacking, not that they should flee.
            
            return false;
        }

        /// <summary>
        /// Checks if we should sprint or conserve stamina.
        /// </summary>
        public bool ShouldSprint()
        {
     if (_isRecovering) return false;
  
            if (_isSprinting)
            {
     // Continue sprinting until stop threshold
          if (GetStaminaPercent() < sprintStopThreshold)
                {
   _isSprinting = false;
         return false;
      }
       return true;
      }
    else
            {
       // Start sprinting only if above threshold
    if (GetStaminaPercent() >= sprintThreshold)
          {
      _isSprinting = true;
             return true;
        }
        return false;
            }
 }

     /// <summary>
        /// Checks if we're in stamina recovery mode (pausing attacks).
        /// </summary>
        public bool IsRecovering()
     {
            return _isRecovering;
        }

        /// <summary>
        /// Gets how many attacks we can perform before needing to recover.
        /// Accounts for dodge reserve.
        /// </summary>
        public int GetAvailableAttacks()
        {
            float available = _currentStamina - GetEmergencyReserve();
            if (available <= 0) return 0;
            return Mathf.FloorToInt(available / _cachedAttackCost);
        }
        
        /// <summary>
        /// Gets the stamina cost for a primary attack.
        /// </summary>
        public float GetPrimaryAttackCost() => _cachedAttackCost;
        
        /// <summary>
        /// Gets the stamina cost for a secondary attack.
        /// </summary>
        public float GetSecondaryAttackCost() => _cachedSecondaryAttackCost;
        
        /// <summary>
        /// Checks if we can afford a specific number of attacks plus a dodge.
        /// Used for intelligent attack planning.
        /// </summary>
        public bool CanAffordAttacksWithDodgeReserve(int attackCount, bool includeSecondary = false)
        {
            float attackCost = includeSecondary ? _cachedSecondaryAttackCost : _cachedAttackCost;
            float totalNeeded = (attackCost * attackCount) + _cachedDodgeCost;
            return _currentStamina >= totalNeeded;
        }
        
        /// <summary>
        /// Resets the attack pacing when combat ends or companion is safe.
        /// </summary>
        public void ResetAttackPacing()
        {
            _consecutiveAttacks = 0;
            _nextAllowedAttackTime = 0f;
        }

        /// <summary>
        /// Gets recommended action based on stamina state.
        /// SURVIVAL INSTINCT: Low stamina = treat like low health = RETREAT.
        /// Higher level companions are better at maintaining optimal stamina levels.
        /// </summary>
        public StaminaRecommendation GetRecommendation()
        {
            float percent = GetStaminaPercent();
            
            // SURVIVAL INSTINCT: Treat low stamina like low health
            // If stamina is critically low, companion is essentially defenseless
            if (ShouldTreatAsLowHealth())
            {
                return StaminaRecommendation.CriticalRetreat;
            }
            
            // CRITICAL: Must retreat and do nothing offensive
            if (_isInCriticalRecovery)
            {
                return StaminaRecommendation.CriticalRetreat;
            }
            
            // In recovery mode - NO combat actions at all, just retreat/evade
            if (_isRecovering)
            {
                // During recovery, don't even defend - just back off and let stamina regen
                if (percent < 0.5f)
                {
                    return StaminaRecommendation.CriticalRetreat;  // Upgraded from DefendOnly
                }
                // Higher stamina during recovery - can defend but not attack
                return StaminaRecommendation.DefendOnly;
            }
            
            // Not in recovery - check current stamina level
            if (percent < _adjustedRecoveryThreshold)
            {
                return StaminaRecommendation.DefendOnly;
            }
            else if (percent < minStaminaToAttack)
            {
                return StaminaRecommendation.CautiousAttack;
            }
            else if (percent < conservativeAttackThreshold)
            {
                // In the conservative zone - attack but with pacing
                return StaminaRecommendation.CautiousAttack;
            }
            else if (percent > 0.85f)
            {
                return StaminaRecommendation.FullAggression;
            }
            else
            {
                return StaminaRecommendation.BalancedCombat;
            }
        }
        
        /// <summary>
        /// Returns true if in critical recovery (stamina depleted, must retreat).
        /// </summary>
        public bool IsInCriticalRecovery()
        {
            return _isInCriticalRecovery;
        }
        
        /// <summary>
        /// Gets the current stamina regeneration multiplier.
        /// Returns a boosted value when actively retreating due to critical stamina or fleeing.
        /// </summary>
        public float GetStaminaRegenMultiplier()
        {
            // FLEE STATE: Highest priority - significant regen boost while fleeing
            // This allows the companion to recover stamina while kiting/retreating
            if (_isInFleeState)
            {
                // During critical recovery while fleeing, use the highest boost
                if (_isInCriticalRecovery)
                {
                    return criticalRetreatRegenBoost;
                }
                // Otherwise use the flee state boost
                return fleeStateRegenBoost;
            }
            
            // During critical recovery (actively retreating), boost stamina regen
            // This rewards the companion for disengaging and helps them recover faster
            if (_isInCriticalRecovery)
            {
                return criticalRetreatRegenBoost;
            }
            
            // Normal recovery also gets a smaller boost to encourage disengagement
            if (_isRecovering)
            {
                return 1.0f + (criticalRetreatRegenBoost - 1.0f) * 0.5f; // Half the boost
            }
            
            return 1.0f;
        }
        
        /// <summary>
        /// Returns true if in flee state (actively fleeing from combat).
        /// </summary>
        public bool IsInFleeState()
        {
            return _isInFleeState;
        }

   #endregion

        #region Public API - Notifications

        /// <summary>
        /// Call when an attack is performed.
        /// </summary>
        public void OnAttackPerformed()
        {
            _lastAttackTime = Time.time;
            _lastActionTime = Time.time;
            _consecutiveAttacks++;
            
            // When in conservative mode, set the next allowed attack time
            float staminaPercent = GetStaminaPercent();
            if (staminaPercent < conservativeAttackThreshold)
            {
                // Calculate delay based on how low stamina is
                // Lower stamina = longer delay to allow more regeneration
                float t = Mathf.InverseLerp(minStaminaToAttack, conservativeAttackThreshold, staminaPercent);
                float delay = Mathf.Lerp(conservativeAttackDelay, conservativeAttackDelay * 0.5f, t);
                _nextAllowedAttackTime = Time.time + delay;
                
                if (CombatFlowLogging)
                    Debug.Log($"[StaminaManager] Attack performed in conservative mode. Next attack in {delay:F1}s. Stamina: {staminaPercent:P0}");
            }
            else
            {
                // Reset consecutive attack counter when stamina is healthy
                if (staminaPercent > conservativeAttackThreshold + 0.1f)
                {
                    _consecutiveAttacks = 0;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[StaminaManager] Attack performed. Stamina: {staminaPercent:P0}, Consecutive: {_consecutiveAttacks}");
            }
        }

        /// <summary>
        /// Call when a dodge is performed.
        /// </summary>
        public void OnDodgePerformed()
        {
        _lastDodgeTime = Time.time;
      _lastActionTime = Time.time;
            
      if (VerboseLogging)
       {
        Debug.Log($"[StaminaManager] Dodge performed. Stamina: {GetStaminaPercent():P0}");
 }
      }

        /// <summary>
        /// Call when blocking starts.
   /// </summary>
        public void OnBlockStarted()
        {
       _lastBlockTime = Time.time;
            _lastActionTime = Time.time;
        }

        /// <summary>
    /// Call when blocking ends.
 /// </summary>
 public void OnBlockEnded()
        {
         _lastBlockTime = Time.time;
        }

        /// <summary>
        /// Forces entry into recovery mode.
        /// </summary>
        public void ForceRecovery()
        {
            _isRecovering = true;
            _isSprinting = false;
            
            if (VerboseLogging)
            {
                Debug.Log($"[StaminaManager] Forced into recovery mode");
            }
        }
        
        /// <summary>
        /// Forces immediate stamina recovery to allow the companion to flee.
        /// Called when entering flee state due to critical stamina.
        /// 
        /// NEW DESIGN: Instead of boosting to 60%, we now:
        /// 1. Give a small instant boost (20%) to ensure the companion can start running
        /// 2. Enable enhanced regeneration rate during flee state
        /// 3. The companion will naturally regenerate to full while fleeing
        /// 
        /// This creates more realistic behavior where the companion recovers WHILE fleeing
        /// rather than instantly getting a large stamina boost.
        /// </summary>
        public void ForceStaminaRecoveryForFlee()
        {
            // Enter flee state - this enables enhanced regen
            _isInFleeState = true;
            _fleeStateStartTime = Time.time;
            
            // Calculate the instant boost (20% of max stamina)
            float instantBoost = _maxStamina * fleeInstantBoostPercent;
            float targetStamina = Mathf.Min(_maxStamina, _currentStamina + instantBoost);
            
            float previousStamina = _currentStamina;
            
            // Apply the instant boost to CompanionStats (the authoritative source)
            if (_companionStats != null)
            {
                _companionStats.SetStamina(targetStamina);
                _currentStamina = targetStamina;
            }
            else
            {
                _currentStamina = targetStamina;
            }
            
            // Exit critical recovery since we have some stamina and enhanced regen
            // The companion will continue recovering while fleeing
            _isInCriticalRecovery = false;
            _isRecovering = true;  // Keep in recovery mode to prevent attacks
            _isSprinting = false;
            
            Debug.Log($"[StaminaManager] === FLEE STATE ENTERED === " +
                $"Instant boost: {previousStamina:F0} -> {targetStamina:F0} (+{fleeInstantBoostPercent:P0}), " +
                $"Enhanced regen: {fleeStateRegenBoost:F2}x until full");
        }
        
        /// <summary>
        /// Exits flee state. Called when flee behavior ends.
        /// </summary>
        public void ExitFleeState()
        {
            if (_isInFleeState)
            {
                float fleeTime = Time.time - _fleeStateStartTime;
                _isInFleeState = false;
                
                Debug.Log($"[StaminaManager] === FLEE STATE EXITED === " +
                    $"Duration: {fleeTime:F1}s, Final stamina: {GetStaminaPercent():P0}");
            }
        }
        
        /// <summary>
        /// Clears critical recovery state without requiring full stamina.
        /// Used when flee behavior needs to end but stamina hasn't fully recovered.
        /// </summary>
        public void ClearCriticalRecovery()
        {
            if (_isInCriticalRecovery)
            {
                _isInCriticalRecovery = false;
                Debug.Log($"[StaminaManager] Critical recovery cleared manually (stamina: {GetStaminaPercent():P0})");
            }
        }
        
        /// <summary>
        /// Sets archetype-based stamina modifiers.
        /// Called by ArchetypeController when archetype is assigned or level changes.
        /// </summary>
        /// <param name="maxStaminaMult">Multiplier for max stamina (e.g., 1.25 for tank)</param>
        /// <param name="staminaRegenMult">Multiplier for stamina regeneration</param>
        /// <param name="blockCostMult">Multiplier for block stamina cost (e.g., 0.7 for tank)</param>
        /// <param name="dodgeCostMult">Multiplier for dodge stamina cost</param>
        /// <param name="attackCostMult">Multiplier for attack stamina cost</param>
        public void SetArchetypeModifiers(float maxStaminaMult, float staminaRegenMult, 
            float blockCostMult, float dodgeCostMult, float attackCostMult)
        {
            _archetypeMaxStaminaMult = maxStaminaMult;
            _archetypeStaminaRegenMult = staminaRegenMult;
            _archetypeBlockCostMult = blockCostMult;
            _archetypeDodgeCostMult = dodgeCostMult;
            _archetypeAttackCostMult = attackCostMult;
            
            // Update cached costs with archetype modifiers
            _cachedBlockCost = blockCost * _archetypeBlockCostMult;
            _cachedDodgeCost = dodgeCost * _archetypeDodgeCostMult;
            
            // Update max stamina in CompanionStats
            if (_companionStats != null)
            {
                _companionStats.SetArchetypeStaminaMultiplier(maxStaminaMult);
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[StaminaManager] Archetype modifiers set: MaxStam={maxStaminaMult:F2}x, " +
                    $"Regen={staminaRegenMult:F2}x, Block={blockCostMult:F2}x, Dodge={dodgeCostMult:F2}x");
            }
        }
        
        /// <summary>
        /// Restores stamina directly. Used by archetype abilities like parry restore for tanks.
        /// </summary>
        public void RestoreStamina(float amount)
        {
            if (amount <= 0) return;
            
            float newStamina = Mathf.Min(_maxStamina, _currentStamina + amount);
            
            if (_companionStats != null)
            {
                _companionStats.SetStamina(newStamina);
            }
            
            _currentStamina = newStamina;
            
            if (VerboseLogging)
            {
                Debug.Log($"[StaminaManager] Restored {amount:F1} stamina. Now at {GetStaminaPercent():P0}");
            }
        }
        
        /// <summary>
        /// Gets the effective stamina regen multiplier including archetype bonuses.
        /// </summary>
        public float GetEffectiveStaminaRegenMultiplier()
        {
            float baseMult = GetStaminaRegenMultiplier();
            return baseMult * _archetypeStaminaRegenMult;
        }

 #endregion

        #region Internal Updates

  private void UpdateStaminaFromCharacter()
        {
            // CRITICAL: Read from CompanionStats, NOT Character!
            // CompanionStats is where the companion's stamina is actually tracked and displayed.
            if (_companionStats != null)
            {
                _currentStamina = _companionStats.CurrentStamina;
                _maxStamina = _companionStats.MaxStamina;
                
                // Ensure valid values
                if (_maxStamina <= 0) _maxStamina = 100f;
                
                return;
            }
            
            // Fallback: Try to get CompanionStats if we didn't have it at Awake
            if (_companionStats == null)
            {
                _companionStats = GetComponent<CompanionStats>();
                if (_companionStats != null)
                {
                    _currentStamina = _companionStats.CurrentStamina;
                    _maxStamina = _companionStats.MaxStamina;
                    if (_maxStamina <= 0) _maxStamina = 100f;
                    return;
                }
            }
            
            // Last resort fallback: Try Character reflection (shouldn't be needed)
            if (_character == null) return;
      
            try
            {
                var staminaField = typeof(Character).GetField("m_stamina", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var maxStaminaField = typeof(Character).GetField("m_maxStamina", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
          
                if (staminaField != null)
                    _currentStamina = (float)staminaField.GetValue(_character);
                if (maxStaminaField != null)
                    _maxStamina = (float)maxStaminaField.GetValue(_character);
                
                if (_maxStamina <= 0) _maxStamina = 100f;
                if (_currentStamina <= 0) _currentStamina = _maxStamina;
            }
            catch
            {
                _maxStamina = 100f;
                _currentStamina = _maxStamina;
            }
        }

        private void UpdateCostsFromEquipment()
        {
            // Get attack costs from equipped weapon
            if (_equipmentData != null)
            {
                _cachedAttackCost = _equipmentData.AttackStamina > 0 ? _equipmentData.AttackStamina : 10f;
                
                // Get secondary attack cost if available
                var secondaryAttack = _equipmentData.SecondaryAttack;
                if (secondaryAttack != null)
                {
                    _cachedSecondaryAttackCost = secondaryAttack.m_attackStamina > 0 ? secondaryAttack.m_attackStamina : _cachedAttackCost * 1.5f;
                }
                else
                {
                    _cachedSecondaryAttackCost = _cachedAttackCost * 1.5f;
                }
            }
            
            // Dodge cost is usually fixed but could be modified
            _cachedDodgeCost = dodgeCost;
            _cachedBlockCost = blockCost;
        }

        private void UpdateRecoveryState()
        {
            float percent = GetStaminaPercent();
            
            // CRITICAL RECOVERY: Triggered when stamina hits low threshold
            // This forces a full retreat and prevents ALL offensive actions
            if (_isInCriticalRecovery)
            {
                float timeSinceCritical = Time.time - _criticalRecoveryStartTime;
                
                // Exit critical recovery only when:
                // 1. Stamina is nearly full (90%+)
                // 2. Minimum time has passed (to let stamina fully regen)
                if (percent >= 0.90f && timeSinceCritical >= CRITICAL_RECOVERY_MIN_TIME)
                {
                    _isInCriticalRecovery = false;
                    _isRecovering = false;
                    
                    if (CombatFlowLogging || VerboseLogging)
                    {
                        Debug.Log($"[StaminaManager] === EXITING CRITICAL RECOVERY === Stamina: {percent:P0}, time: {timeSinceCritical:F1}s");
                    }
                }
                return;  // Don't process normal recovery while in critical
            }
            
            // Check for critical stamina depletion
            if (percent < CRITICAL_STAMINA_THRESHOLD)
            {
                _isInCriticalRecovery = true;
                _criticalRecoveryStartTime = Time.time;
                _isRecovering = true;
                _isSprinting = false;
                
                if (CombatFlowLogging || VerboseLogging)
                {
                    Debug.Log($"[StaminaManager] === ENTERING CRITICAL RECOVERY === Stamina: {percent:P0} < {CRITICAL_STAMINA_THRESHOLD:P0}");
                    Debug.Log($"[StaminaManager] SURVIVAL INSTINCT: Companion MUST retreat - no attacks, no dodges, no blocks!");
                }
                return;
            }
            
            // Normal recovery logic with level-adjusted thresholds
            if (_isRecovering)
            {
                float timeSinceRecovery = Time.time - _normalRecoveryStartTime;
                
                // Exit recovery when:
                // 1. Stamina is above resume threshold
                // 2. Minimum recovery time has passed (prevents rapid on/off cycling)
                if (percent >= _adjustedResumeThreshold && timeSinceRecovery >= NORMAL_RECOVERY_MIN_TIME)
                {
                    _isRecovering = false;
                    
                    if (CombatFlowLogging || VerboseLogging)
                    {
                        Debug.Log($"[StaminaManager] === EXITING RECOVERY === Stamina: {percent:P0} >= {_adjustedResumeThreshold:P0}");
                    }
                }
            }
            else
            {
                // Enter recovery when stamina drops below threshold
                if (percent < _adjustedRecoveryThreshold)
                {
                    _isRecovering = true;
                    _normalRecoveryStartTime = Time.time;
                    _isSprinting = false;
                    
                    if (CombatFlowLogging || VerboseLogging)
                    {
                        Debug.Log($"[StaminaManager] === ENTERING RECOVERY === Stamina: {percent:P0} < {_adjustedRecoveryThreshold:P0}");
                        Debug.Log($"[StaminaManager] Companion should stop attacking and let stamina regenerate to {_adjustedResumeThreshold:P0}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Updates level-adjusted thresholds for smarter stamina management.
        /// Higher level companions maintain more stamina reserve and have better timing.
        /// </summary>
        private void UpdateLevelAdjustedThresholds()
        {
            int currentLevel = _progression?.Level ?? 0;
            
            // Only recalculate if level changed
            if (currentLevel == _cachedLevel) return;
            _cachedLevel = currentLevel;
            
            // Higher level = better at maintaining stamina reserve
            // Level 0: base thresholds (40% recovery, 85% resume)
            // Level 100: +15% higher thresholds (55% recovery, 95% resume - almost never depletes)
            float levelBonus = currentLevel * levelStaminaMaintenanceBonus;
            
            // Recovery threshold: when to STOP attacking and start recovering
            // Higher = more conservative, enters recovery earlier
            _adjustedRecoveryThreshold = Mathf.Min(0.55f, recoveryThreshold + (levelBonus * 0.5f));
            
            // Resume threshold: when to START attacking again after recovery
            // Higher = more patient, waits for fuller stamina bar
            _adjustedResumeThreshold = Mathf.Min(0.95f, resumeThreshold + (levelBonus * 0.33f));
            
            // Higher level = better timing between actions (can attack more efficiently)
            // Level 0: base throttle (0.3s)
            // Level 100: 20% faster action timing (0.24s)
            float throttleReduction = currentLevel * levelThrottleReduction;
            _adjustedActionThrottle = Mathf.Max(0.15f, actionThrottleTime - throttleReduction);
            
            if (VerboseLogging && currentLevel > 0)
            {
                Debug.Log($"[StaminaManager] Level {currentLevel} adjustments: " +
                    $"Recovery at {_adjustedRecoveryThreshold:P0}, Resume at {_adjustedResumeThreshold:P0}, " +
                    $"Action throttle {_adjustedActionThrottle:F2}s");
            }
        }

        private float GetActionCost(StaminaAction action)
        {
  return action switch
    {
   StaminaAction.Attack => _cachedAttackCost,
  StaminaAction.Dodge => _cachedDodgeCost,
              StaminaAction.Block => _cachedBlockCost,
          StaminaAction.Sprint => 5f, // Approximate per-second cost
       _ => 0f
            };
        }

        private float GetEmergencyReserve()
        {
 // Always keep enough for at least one dodge
            float percentReserve = _maxStamina * emergencyReservePercent;
    return Mathf.Max(dodgeReserve, percentReserve);
        }

  #endregion

        #region Enums

        public enum StaminaAction
        {
     Attack,
            Dodge,
        Block,
        Sprint
        }

        public enum StaminaRecommendation
        {
            /// <summary>
            /// CRITICAL: Stamina depleted, must retreat immediately and avoid all combat
            /// </summary>
            CriticalRetreat,
            
            /// <summary>
            /// Only block/dodge, no attacking
            /// </summary>
            DefendOnly,
            
            /// <summary>
            /// Attack sparingly, prioritize defense
            /// </summary>
            CautiousAttack,
            
            /// <summary>
            /// Normal combat, balance attack and defense
            /// </summary>
            BalancedCombat,
            
            /// <summary>
            /// Stamina is high, can be aggressive
            /// </summary>
            FullAggression
        }

        #endregion
    }
}
