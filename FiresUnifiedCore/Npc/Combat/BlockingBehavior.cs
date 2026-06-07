using UnityEngine;
using FiresCore.Npc.AI;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Handles blocking and parrying mechanics for companion NPCs.
    /// 
    /// DESIGN PHILOSOPHY:
    /// Blocking should be deliberate and meaningful, not spammy.
    /// - When we decide to block, we COMMIT to it for a minimum duration
    /// - After dropping block, there's a cooldown before we can block again
    /// - We evaluate threats properly before deciding to block
    /// - Perfect parries (timed blocks) require skill and timing
    /// 
    /// BLOCK DECISION FACTORS:
    /// - Enemy is attacking AND facing us
    /// - Incoming projectile detected
    /// - EnemyAttackRecognition says we should block
    /// - We're in melee range
    /// - We have stamina/resources to block
    /// - We haven't just stopped blocking (cooldown)
    /// 
    /// BLOCK COMMITMENT:
    /// Once we start blocking, we hold it for at least MIN_BLOCK_DURATION.
    /// This prevents jittery on/off behavior and makes blocks meaningful.
    /// </summary>
    public class BlockingBehavior
    {
        private CombatContext _context;
        private MonoBehaviour _owner;
        private EnemyAttackRecognition _attackRecognition;
        private ThreatAnalyzer _threatAnalyzer;
        private CombatExperience _combatExperience;
        private StaminaManager _staminaManager;
        private ArchetypeController _archetypeController;

        // ========================================
        // BLOCK STATE
        // ========================================
        private bool _isBlocking;
        private float _blockStartTime;
        private float _blockCommitEndTime;    // When we're allowed to DROP block
        private float _lastBlockStopTime;       // When we last stopped blocking
        private float _lastBlockDecisionTime;   // When we last made a block decision
        
        // ========================================
        // PROJECTILE TRACKING
        // ========================================
        private float _lastProjectileScan;
        private const float PROJECTILE_SCAN_INTERVAL = 0.1f;
        private const float PROJECTILE_DETECT_RANGE = 20f;
        private const float PROJECTILE_DANGER_TIME = 0.8f; // Block if projectile arrives within this time
        private Projectile _incomingProjectile;
        private float _projectileTimeToImpact;
        
        // ========================================
        // TIMING CONSTANTS - THE KEY TO NON-SPAM
    // ========================================
        
        /// <summary>
        /// Once we start blocking, hold for at least this long.
        /// This prevents jittery block spam.
   /// </summary>
        private const float MIN_BLOCK_DURATION = 0.5f;
        
        /// <summary>
   /// Maximum time to hold a block before forcing release.
        /// Prevents getting stuck in block state.
        /// </summary>
        private const float MAX_BLOCK_DURATION = 2.5f;
        
        /// <summary>
        /// After stopping a block, wait this long before blocking again.
  /// This is the key to preventing spam - forces deliberate decisions.
     /// </summary>
   private const float BLOCK_COOLDOWN = 0.8f;
        
        /// <summary>
        /// Minimum time between block DECISIONS (not state changes).
        /// Prevents rapid evaluation spam.
        /// </summary>
        private const float DECISION_INTERVAL = 0.15f;
        
        /// <summary>
    /// Time window for a parry (block started just before hit).
      /// </summary>
      private const float PARRY_WINDOW = 0.25f;
        
        /// <summary>
        /// How far ahead to anticipate attacks (start block before hit).
     /// </summary>
        private const float BLOCK_ANTICIPATION = 0.3f;
      
      // ========================================
        // ENEMY TRACKING
  // ========================================
        private Character _trackedEnemy;
        private bool _enemyWasAttacking;
        private float _enemyAttackStartTime;
        private float _lastEnemyAttackEndTime;
        
  // ========================================
        // STATISTICS (for adaptive behavior)
        // ========================================
  private int _successfulBlocks;
    private int _successfulParries;
        private int _missedBlocks;
        private float _lastDamageTakenTime;
   
        // Debug
        private bool _lastLoggedState;

        public void Initialize(CombatContext context, MonoBehaviour owner)
        {
            _context = context;
            _owner = owner;
            _attackRecognition = owner.GetComponent<EnemyAttackRecognition>();
            _threatAnalyzer = owner.GetComponent<ThreatAnalyzer>();
            _combatExperience = owner.GetComponent<CombatExperience>();
            _staminaManager = owner.GetComponent<StaminaManager>();
            _archetypeController = owner.GetComponent<ArchetypeController>();
            _lastBlockStopTime = -10f;  // Allow immediate blocking on init
            _lastBlockDecisionTime = -10f;
            _lastEnemyAttackEndTime = -10f;
        }

        /// <summary>
        /// Whether currently blocking.
        /// </summary>
        public bool IsBlocking => _isBlocking;
        
        /// <summary>
        /// Whether we're committed to the current block (can't drop yet).
        /// </summary>
 public bool IsBlockCommitted => _isBlocking && Time.time < _blockCommitEndTime;
     
        /// <summary>
        /// Whether block is on cooldown (can't start new block).
        /// </summary>
        public bool IsOnCooldown => Time.time - _lastBlockStopTime < BLOCK_COOLDOWN;

        /// <summary>
        /// Check if a shield is equipped.
        /// </summary>
        public bool HasShield()
        {
if (_context.Inventory == null) return false;
            var leftHand = _context.Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            return leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield;
    }

        /// <summary>
        /// Check if current equipment can block (any weapon can block/parry).
        /// All melee weapons can block to some degree - shields are just better at it.
        /// </summary>
        public bool CanBlock()
        {
            // Shields are best for blocking
            if (HasShield()) return true;

            // Any melee weapon can block/parry (with reduced effectiveness)
            if (_context.CurrentWeapon?.m_shared != null)
            {
                var itemType = _context.CurrentWeapon.m_shared.m_itemType;
                
                // All weapon types can block
                return itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                       itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                       itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                       itemType == ItemDrop.ItemData.ItemType.Torch;
            }

            // Even unarmed can attempt to block (arms up)
            return true;
        }
        
        /// <summary>
        /// Gets the block power multiplier based on weapon type.
        /// Shields: 1.0x, Dedicated parry weapons: 0.8x, Other weapons: 0.5x, Unarmed: 0.25x
        /// </summary>
        private float GetBlockPowerMultiplier()
        {
            if (HasShield()) return 1.0f;
            
            if (_context.CurrentWeapon?.m_shared != null)
            {
                var skillType = _context.CurrentWeapon.m_shared.m_skillType;
                
                // Traditional parry weapons are better at blocking
                if (skillType == Skills.SkillType.Swords ||
                    skillType == Skills.SkillType.Axes ||
                    skillType == Skills.SkillType.Clubs ||
                    skillType == Skills.SkillType.Polearms ||
                    skillType == Skills.SkillType.Knives)
                {
                    return 0.8f;
                }
                
                // Other weapons can still block but less effectively
                return 0.5f;
            }
            
            // Unarmed blocking
            return 0.25f;
        }

   /// <summary>
        /// Main update - called every frame during combat.
        /// Makes deliberate, throttled decisions about blocking.
        /// </summary>
        public void Update(bool isAttacking, bool isDodging, CompanionCombat.WeaponType weaponType)
        {
    // ==========================================
       // IMMEDIATE EXITS - Can't block in these states
  // ==========================================
            
  if (_context.Character == null) return;
    
            // Can't block while attacking or dodging
    if (isAttacking || isDodging)
       {
        if (_isBlocking) ForceStopBlocking("attacking/dodging");
                return;
        }

       // Can't block with ranged weapons
     if (weaponType == CompanionCombat.WeaponType.Bow ||
          weaponType == CompanionCombat.WeaponType.Crossbow ||
weaponType == CompanionCombat.WeaponType.Staff)
            {
          if (_isBlocking) ForceStopBlocking("ranged weapon");
  return;
     }

            // Can't block without blocking capability
    if (!CanBlock())
       {
           if (_isBlocking) ForceStopBlocking("no block capability");
        return;
      }

            // ==========================================
            // GET CURRENT THREAT
            // ==========================================
            
            var target = _context.CompanionAI?.GetTargetCreature();
        if (target == null || target.IsDead())
            {
                if (_isBlocking) TryStopBlocking("no target");
      ClearEnemyTracking();
       return;
   }

    float distToTarget = Vector3.Distance(_context.Transform.position, target.transform.position);

// Too far - no need to block
            if (distToTarget > _context.AttackRange * 2.5f)
       {
      if (_isBlocking) TryStopBlocking("target too far");
      return;
            }

            // ==========================================
     // TRACK ENEMY ATTACK STATE
        // ==========================================
            
     UpdateEnemyTracking(target);

        // ==========================================
            // BLOCK STATE MACHINE
            // ==========================================
         
    if (_isBlocking)
    {
  UpdateWhileBlocking(target, distToTarget);
    }
            else
{
                UpdateWhileNotBlocking(target, distToTarget);
          }
        }

        /// <summary>
        /// Update logic while we ARE blocking.
      /// </summary>
        private void UpdateWhileBlocking(Character target, float distToTarget)
  {
    // Check if we've held block too long (force release)
 if (Time.time - _blockStartTime > MAX_BLOCK_DURATION)
          {
    ForceStopBlocking("max duration");
    return;
            }

     // If committed, keep blocking no matter what
       if (Time.time < _blockCommitEndTime)
      {
         return;
            }

    // Past commitment - evaluate if we should continue
    
            // Keep blocking if enemy is still attacking
     if (target.InAttack())
        {
     return;
            }
            
            // Keep blocking briefly after enemy stops attacking (recovery frames)
     if (Time.time - _lastEnemyAttackEndTime < 0.2f)
            {
        return;
        }

         // Enemy not attacking, not committed - we can drop block
          TryStopBlocking("enemy not attacking");
        }

        /// <summary>
        /// Update logic while we are NOT blocking.
        /// SURVIVAL INSTINCT: Low stamina = don't block, retreat instead.
        /// </summary>
        private void UpdateWhileNotBlocking(Character target, float distToTarget)
        {
            // Check cooldown
            if (IsOnCooldown)
            {
                return;
            }

            // Throttle decisions
            if (Time.time - _lastBlockDecisionTime < DECISION_INTERVAL)
            {
                return;
            }
            _lastBlockDecisionTime = Time.time;

            // ==========================================
            // SURVIVAL INSTINCT: Low stamina = NO blocking
            // Just like low health, companion needs to RETREAT not fight
            // ==========================================
            
            if (_staminaManager != null)
            {
                // SURVIVAL INSTINCT: Treat low stamina like low health
                if (_staminaManager.ShouldTreatAsLowHealth())
                {
                    return;  // NO blocking - need to retreat and regenerate
                }
                
                // In critical recovery - NO blocking at all
                if (_staminaManager.IsInCriticalRecovery())
                {
                    return;
                }
                
                // In normal recovery - NO blocking (save stamina for regen)
                if (_staminaManager.IsRecovering())
                {
                    return;
                }
                
                // Check if we can afford to block
                if (!_staminaManager.CanBlock())
                {
                    return;
                }
            }

            // ==========================================
            // CHECK FOR INCOMING PROJECTILES
            // ==========================================
            
            if (ScanForIncomingProjectiles())
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[BlockingBehavior] Blocking incoming projectile! Time to impact: {_projectileTimeToImpact:F2}s");
                StartBlocking();
                return;
            }
            
            // ==========================================
            // USE ENEMY ATTACK RECOGNITION
            // ==========================================
            
            if (_attackRecognition != null)
            {
                // ShouldBlockNow already checks StaminaManager
                if (_attackRecognition.ShouldBlockNow())
                {
                    var threat = _attackRecognition.GetCurrentThreat();
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] AttackRecognition recommends block, TimeToImpact: {threat.TimeToImpact:F2}s");
                    StartBlocking();
                    return;
                }
            }

            // ==========================================
            // EVALUATE IF WE SHOULD START BLOCKING
            // ==========================================
            
            bool shouldBlock = EvaluateBlockDecision(target, distToTarget);
            
            if (shouldBlock)
            {
                StartBlocking();
            }
        }

        /// <summary>
        /// Core decision: Should we start blocking right now?
        /// SURVIVAL INSTINCT: Low stamina = don't block, retreat instead.
        /// Uses ThreatAnalyzer for situation-aware decisions.
        /// Uses CombatExperience for level-based improvements.
        /// </summary>
        private bool EvaluateBlockDecision(Character target, float distToTarget)
        {
            // Not in blocking range
            if (distToTarget > 5f)
                return false;

            // SURVIVAL INSTINCT: Low stamina = NO blocking
            if (_staminaManager != null)
            {
                if (_staminaManager.ShouldTreatAsLowHealth())
                    return false;
                
                if (_staminaManager.IsInCriticalRecovery())
                    return false;
                
                if (_staminaManager.IsRecovering())
                    return false;  // NO blocking during recovery
                    
                if (!_staminaManager.CanBlock())
                    return false;
            }

            // Get level-based combat modifiers
            float levelBlockBonus = 0f;
            float levelAnticipation = BLOCK_ANTICIPATION;
            
            if (_combatExperience != null)
            {
                levelBlockBonus = _combatExperience.GetBlockChanceAgainst(target) - _combatExperience.baseBlockChance;
                levelAnticipation = _combatExperience.GetAnticipationWindow(target);
            }

            // Get threat analysis for smarter decisions
            float blockChanceModifier = 1f;
            bool shouldBeDefensive = false;
            
            if (_threatAnalyzer != null)
            {
                var situation = _threatAnalyzer.GetCurrentSituation();
                blockChanceModifier = situation.BlockChanceModifier;
                shouldBeDefensive = situation.ShouldUseShield;
                
                // In survival/defensive mode, block much more aggressively
                if (situation.RecommendedStance >= ThreatAnalyzer.CombatStance.Defensive)
                {
                    shouldBeDefensive = true;
                }
                
                // Get enemy-specific threat profile
                var profile = _threatAnalyzer.GetThreatProfile(target);
                
                // Against bosses/elites, always try to block if they're attacking
                if (profile.IsBoss || profile.Classification >= ThreatAnalyzer.EnemyClass.Elite)
                {
                    if (target.InAttack())
                    {
                        return true; // Always block boss attacks
                    }
                }
                
                // Against dangerous enemies, be more defensive
                if (profile.Classification >= ThreatAnalyzer.EnemyClass.Dangerous)
                {
                    blockChanceModifier *= 1.5f;
                }
            }

            // Check if enemy is attacking
            bool enemyAttacking = target.InAttack();
            
            // Check if enemy is facing us (their attack would hit us)
            Vector3 toUs = (_context.Transform.position - target.transform.position).normalized;
            float facingDot = Vector3.Dot(target.transform.forward, toUs);
            bool enemyFacingUs = facingDot > 0.2f; // Lowered threshold for wider detection

            // If enemy is attacking AND facing us, HIGH chance to block
            if (enemyAttacking && enemyFacingUs)
            {
                // Base block chance - modified by threat analysis AND level
                float blockChance = (_context.BlockChance + levelBlockBonus) * 2.5f * blockChanceModifier;
                
                // ARCHETYPE BONUS: Tanks have significantly higher block priority
                float archetypeMultiplier = _archetypeController?.GetBlockPriorityMultiplier() ?? 1.0f;
                blockChance *= archetypeMultiplier;
                
                // Shield users block more reliably
                if (HasShield())
                {
                    blockChance *= 1.5f;
                }
                
                // In defensive mode, block even more
                if (shouldBeDefensive)
                {
                    blockChance *= 1.3f;
                }
                
                // Recently got hit? More likely to block
                if (Time.time - _lastDamageTakenTime < 3f)
                {
                    blockChance *= 1.3f;
                }

                // Distance factor - closer = more likely to block
                float distanceFactor = 1f - (distToTarget / 5f);
                blockChance *= (0.7f + distanceFactor * 0.3f);

                // Cap at 95% to allow some hits through
                blockChance = Mathf.Min(blockChance, 0.95f);
                
                bool shouldBlock = Random.value < blockChance;
                
                // Track block attempt for learning
                if (shouldBlock)
                {
                    _combatExperience?.OnBlockAttempt(true);
                }
                
                return shouldBlock;
            }

            // Enemy just started attacking (proactive block for parry attempt)
            // Use level-based anticipation window
            if (_enemyWasAttacking && Time.time - _enemyAttackStartTime < levelAnticipation)
            {
                // Parry attempt - timing-based, improved by level
                float parryChance = _context.ParryChance * 2f * blockChanceModifier;
                
                // Add level-based parry bonus
                if (_combatExperience != null)
                {
                    parryChance += _combatExperience.ParryChanceBonus;
                }
                
                if (HasShield())
                {
                    parryChance *= 1.5f;
                }
                
                bool shouldParry = Random.value < parryChance;
                
                if (shouldParry)
                {
                    _combatExperience?.OnParryAttempt(true);
                }
                
                return shouldParry;
            }
            
            // Enemy very close and moving toward us - preemptive block
            if (distToTarget < 3f)
            {
                Vector3 enemyVelocity = target.GetVelocity();
                float approachSpeed = Vector3.Dot(enemyVelocity.normalized, -toUs);
                if (approachSpeed > 2f) // Enemy charging at us
                {
                    return Random.value < (_context.BlockChance + levelBlockBonus) * 1.5f * blockChanceModifier;
                }
            }

            return false;
        }
        
        /// <summary>
        /// Scans for incoming projectiles that we should block.
        /// Returns true if a dangerous projectile is detected.
        /// </summary>
        private bool ScanForIncomingProjectiles()
        {
            if (Time.time - _lastProjectileScan < PROJECTILE_SCAN_INTERVAL)
            {
                // Use cached result
                return _incomingProjectile != null && _projectileTimeToImpact < PROJECTILE_DANGER_TIME;
            }
            _lastProjectileScan = Time.time;
            _incomingProjectile = null;
            _projectileTimeToImpact = float.MaxValue;
            
            // Find all projectiles in range
            var colliders = Physics.OverlapSphere(_context.Transform.position, PROJECTILE_DETECT_RANGE);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var projectile = col.GetComponent<Projectile>();
                if (projectile == null) continue;
                
                // Check if projectile is heading toward us
                var rb = projectile.GetComponent<Rigidbody>();
                if (rb == null) continue;
                
                Vector3 projectilePos = projectile.transform.position;
                Vector3 projectileVel = rb.linearVelocity;
                
                if (projectileVel.sqrMagnitude < 1f) continue; // Not moving
                
                // Calculate if projectile will hit us
                Vector3 toUs = _context.Transform.position - projectilePos;
                float distToUs = toUs.magnitude;
                
                // Check if projectile is coming toward us
                float dot = Vector3.Dot(projectileVel.normalized, toUs.normalized);
                if (dot < 0.5f) continue; // Not heading our way
                
                // Estimate time to impact
                float speed = projectileVel.magnitude;
                float timeToImpact = distToUs / speed;
                
                // Check if this is the most imminent threat
                if (timeToImpact < _projectileTimeToImpact && timeToImpact < PROJECTILE_DANGER_TIME)
                {
                    // Verify it will actually hit us (within ~2m)
                    Vector3 impactPoint = projectilePos + projectileVel * timeToImpact;
                    float impactDist = Vector3.Distance(impactPoint, _context.Transform.position);
                    
                    if (impactDist < 2f)
                    {
                        _incomingProjectile = projectile;
                        _projectileTimeToImpact = timeToImpact;
                    }
                }
            }
            
            return _incomingProjectile != null;
        }

        /// <summary>
        /// Track enemy attack state changes.
        /// </summary>
    private void UpdateEnemyTracking(Character target)
        {
            if (target != _trackedEnemy)
            {
              // New enemy - reset tracking
       _trackedEnemy = target;
         _enemyWasAttacking = target.InAttack();
          _enemyAttackStartTime = _enemyWasAttacking ? Time.time : 0f;
     return;
         }

            bool enemyAttackingNow = target.InAttack();

       // Detect attack START
         if (enemyAttackingNow && !_enemyWasAttacking)
       {
        _enemyAttackStartTime = Time.time;
      }
 // Detect attack END
      else if (!enemyAttackingNow && _enemyWasAttacking)
            {
   _lastEnemyAttackEndTime = Time.time;
 }

          _enemyWasAttacking = enemyAttackingNow;
 }

    private void ClearEnemyTracking()
      {
            _trackedEnemy = null;
     _enemyWasAttacking = false;
          _enemyAttackStartTime = 0f;
    }

        /// <summary>
        /// Start blocking - commits to blocking for MIN_BLOCK_DURATION.
        /// SURVIVAL INSTINCT: Will NOT block if stamina is critically low.
        /// </summary>
        private void StartBlocking()
        {
            if (_isBlocking) return;
            if (IsOnCooldown) return;

            // SURVIVAL INSTINCT: Low stamina = NO blocking
            if (_staminaManager != null)
            {
                // Treat low stamina like low health - retreat, don't block
                if (_staminaManager.ShouldTreatAsLowHealth())
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] SURVIVAL INSTINCT: Won't block - stamina critically low!");
                    return;
                }
                
                // During critical recovery - absolutely no blocking
                if (_staminaManager.IsInCriticalRecovery())
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] Can't block - in critical stamina recovery!");
                    return;
                }
                
                // During normal recovery - no blocking (save stamina for regen)
                if (_staminaManager.IsRecovering())
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] Can't block - in recovery mode, must regenerate stamina");
                    return;
                }
                
                // Check if StaminaManager allows blocking
                if (!_staminaManager.CanBlock())
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] Can't block - not enough stamina");
                    return;
                }
            }

            // Check if we have enough stamina to start blocking via CompanionStats (fallback)
            var stats = _context?.Companion?.GetStats();
            float blockStartCost = 5f; // Base stamina cost to raise block
            if (stats != null && !stats.HasStamina(blockStartCost))
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[BlockingBehavior] Not enough stamina to block! Have {stats.CurrentStamina:F1}, need {blockStartCost}");
                return;
            }

            // Notify StaminaManager that we're blocking
            _staminaManager?.OnBlockStarted();

            // Consume stamina for starting block
            stats?.UseStamina(blockStartCost);

            _isBlocking = true;
            _blockStartTime = Time.time;
            _blockCommitEndTime = Time.time + MIN_BLOCK_DURATION;

            // Set animation
            if (_context.ZAnim != null)
            {
                _context.ZAnim.SetBool(CombatContext.Hash_blocking, true);
            }
            else if (_context.Animator != null && _context.HasAnimatorParameter("blocking"))
            {
                _context.Animator.SetBool("blocking", true);
            }

            _context.BroadcastRPC("RPC_CompanionBlock", true);

            if (CompanionCombat.VerboseLogging && !_lastLoggedState)
            {
                Debug.Log($"[BlockingBehavior] Started blocking (committed until {_blockCommitEndTime:F2})");
                _lastLoggedState = true;
            }
        }

        /// <summary>
        /// Try to stop blocking - respects commitment period.
        /// </summary>
        private void TryStopBlocking(string reason)
        {
            if (!_isBlocking) return;
      
     // Can't stop if still committed
     if (Time.time < _blockCommitEndTime)
            {
           return;
            }

            StopBlockingInternal(reason);
 }

        /// <summary>
      /// Force stop blocking - ignores commitment.
        /// Used when we MUST stop (attacking, dodging, etc.)
    /// </summary>
     private void ForceStopBlocking(string reason)
        {
 if (!_isBlocking) return;
      StopBlockingInternal(reason);
        }

        private void StopBlockingInternal(string reason)
        {
            _isBlocking = false;
            _lastBlockStopTime = Time.time;

            // Notify StaminaManager that blocking ended
            _staminaManager?.OnBlockEnded();

            // Clear animation
            if (_context.ZAnim != null)
            {
                _context.ZAnim.SetBool(CombatContext.Hash_blocking, false);
            }
            else if (_context.Animator != null && _context.HasAnimatorParameter("blocking"))
            {
                _context.Animator.SetBool("blocking", false);
            }

            _context.BroadcastRPC("RPC_CompanionBlock", false);

            if (CompanionCombat.VerboseLogging && _lastLoggedState)
            {
                Debug.Log($"[BlockingBehavior] Stopped blocking ({reason})");
                _lastLoggedState = false;
            }
        }

   /// <summary>
   /// Public method to force start blocking (called externally).
        /// Still respects cooldown.
 /// </summary>
        public void StartBlocking(float minDuration = -1f)
        {
            if (IsOnCooldown) return;
         
            if (minDuration > 0f)
          {
       _isBlocking = true;
   _blockStartTime = Time.time;
         _blockCommitEndTime = Time.time + minDuration;
    
       if (_context.ZAnim != null)
     _context.ZAnim.SetBool(CombatContext.Hash_blocking, true);
             else if (_context.Animator != null && _context.HasAnimatorParameter("blocking"))
    _context.Animator.SetBool("blocking", true);
            
      _context.BroadcastRPC("RPC_CompanionBlock", true);
      }
            else
            {
          StartBlocking();
            }
        }

     /// <summary>
        /// Public method to force stop blocking (called externally).
        /// Always stops regardless of commitment.
        /// </summary>
        public void StopBlocking()
        {
         ForceStopBlocking("external request");
        }

        /// <summary>
  /// Called when we take damage - used for adaptive behavior.
        /// </summary>
 public void OnDamageTaken(float damage, bool wasBlocked)
        {
     _lastDamageTakenTime = Time.time;
            
        if (wasBlocked)
  {
       _successfulBlocks++;
       }
            else if (_isBlocking)
            {
    // We were blocking but still got hit (attack from behind, etc.)
        _missedBlocks++;
 }
        }

        /// <summary>
        /// Process incoming damage and apply blocking/parrying.
        /// Returns the modified damage amount.
        /// </summary>
  public float ProcessIncomingDamage(HitData hit)
    {
            if (hit == null) return 0f;

            float totalDamage = hit.GetTotalDamage();

      if (!hit.m_blockable) return totalDamage;

            // Check if we're facing the attack
   float dot = Vector3.Dot(hit.m_dir, _context.Transform.forward);
   if (dot > 0) // Attack from behind - can't block
        {
        OnDamageTaken(totalDamage, false);
    return totalDamage;
            }

   // Check if we're blocking
            if (!_isBlocking)
    {
           // Emergency reaction block - very low chance
         if (Random.value > _context.BlockChance * 0.2f)
     {
     OnDamageTaken(totalDamage, false);
          return totalDamage;
     }

       // Quick reaction block - start blocking now
   StartBlocking();
  }

         // We are blocking - calculate damage reduction
   float timeSinceBlockStart = Time.time - _blockStartTime;
            bool isPerfectParry = timeSinceBlockStart < PARRY_WINDOW;

     // Calculate block power
 float blockPower = 0f;
            float timedBlockBonus = 1f;

            if (HasShield())
            {
       var shield = _context.Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
    if (shield?.m_shared != null)
     {
             blockPower = shield.m_shared.m_blockPower;
     timedBlockBonus = shield.m_shared.m_timedBlockBonus;
           }
            }
            else if (_context.CurrentWeapon?.m_shared != null)
            {
                // Weapon parry - block power based on weapon type
                float weaponBlockPower = _context.CurrentWeapon.m_shared.m_blockPower;
                if (weaponBlockPower <= 0) weaponBlockPower = 20f; // Default if weapon has no block power
                blockPower = weaponBlockPower * GetBlockPowerMultiplier();
                timedBlockBonus = _context.CurrentWeapon.m_shared.m_timedBlockBonus;
                if (timedBlockBonus <= 0) timedBlockBonus = 1.5f; // Default parry bonus
            }
            else
            {
                // Unarmed blocking - minimal protection
                blockPower = 10f * GetBlockPowerMultiplier();
                timedBlockBonus = 2f; // Good parry timing matters more when unarmed
            }

  if (isPerfectParry)
            {
      blockPower *= timedBlockBonus;
        _successfulParries++;
     }

            // Apply block
            float blockedDamage = Mathf.Min(totalDamage, blockPower);
            float finalDamage = totalDamage - blockedDamage;

            // Consume stamina based on blocked damage (like players)
            float staminaCost = blockedDamage * 0.5f; // Half of blocked damage as stamina cost
            var stats = _context.Companion?.GetStats();
            if (stats != null)
            {
                // If we don't have enough stamina, block breaks and we take more damage
                if (!stats.UseStamina(staminaCost))
                {
                    // Block broken - take full remaining damage
                    finalDamage = totalDamage;
                    StopBlocking();
                    
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[BlockingBehavior] Block BROKEN - not enough stamina! Need {staminaCost:F1}, have {stats.CurrentStamina:F1}");
                    }
                    
                    // Record failed block in archetype statistics
                    _archetypeController?.OnBlockPerformed(false, 0f, false);
                }
                else
                {
                    // Successful block - record in archetype statistics
                    _archetypeController?.OnBlockPerformed(true, blockedDamage, isPerfectParry);
                }
            }
            else
            {
                // No stats component - still record the block
                _archetypeController?.OnBlockPerformed(true, blockedDamage, isPerfectParry);
            }

            // Apply damage reduction
            float damageReduction = isPerfectParry ? _context.ParryDamageReduction : _context.BlockDamageReduction;
     finalDamage *= (1f - damageReduction);

            OnDamageTaken(totalDamage, true);

      if (CompanionCombat.VerboseLogging)
            {
 Debug.Log($"[BlockingBehavior] {(isPerfectParry ? "PARRY" : "Block")}: {totalDamage:F1} -> {finalDamage:F1}");
   }

            // Stagger attacker on perfect parry
            if (isPerfectParry)
            {
                var attacker = hit.GetAttacker();
                if (attacker != null && attacker.m_staggerWhenBlocked)
                {
                    attacker.Stagger(-hit.m_dir);

                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[BlockingBehavior] Staggered {attacker.m_name} with perfect parry!");
                    
                    // PARRY COUNTER-ATTACK: Immediately attack the staggered enemy
                    TryParryCounterAttack(attacker);
                }
            }

            return finalDamage;
        }
        
        /// <summary>
        /// Attempts a counter-attack after a successful parry.
        /// Chance scales with level via CombatExperience.
        /// </summary>
        private void TryParryCounterAttack(Character staggeredEnemy)
        {
            if (staggeredEnemy == null || staggeredEnemy.IsDead()) return;
            
            // Get counter-attack chance from combat experience (scales with level + enemy familiarity)
            float counterChance = 0.3f; // Base 30% chance
            
            if (_combatExperience != null)
            {
                // Use the enemy-specific counter chance which includes level and combat memory bonuses
                counterChance = _combatExperience.GetParryCounterChanceAgainst(staggeredEnemy);
            }
            
            if (Random.value > counterChance)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[BlockingBehavior] Parry counter-attack chance failed ({counterChance:P0})");
                return;
            }
            
            // Stop blocking to allow attack
            ForceStopBlocking("parry counter-attack");
            
            // Get the combat system to execute immediate attack
            var combat = _owner.GetComponent<CompanionCombat>();
            if (combat != null)
            {
                // Force target to the staggered enemy
                var companionAI = _owner.GetComponent<CompanionAI>();
                companionAI?.ForceTarget(staggeredEnemy);
                
                // Execute immediate counter-attack
                combat.ForceAttack();
                
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[BlockingBehavior] PARRY COUNTER-ATTACK on staggered {staggeredEnemy.m_name}!");
            }
        }

        /// <summary>
      /// Handle RPC for syncing block state.
        /// </summary>
        public void HandleBlockRPC(bool blocking)
  {
   if (_context.NView != null && _context.NView.IsOwner()) return;

         if (_context.ZAnim != null)
   {
       _context.ZAnim.SetBool(CombatContext.Hash_blocking, blocking);
 }
          else if (_context.Animator != null && _context.HasAnimatorParameter("blocking"))
            {
        _context.Animator.SetBool("blocking", blocking);
    }
        }
        
        /// <summary>
        /// Get debug info about current state.
        /// </summary>
        public string GetDebugInfo()
        {
            if (!_isBlocking)
            {
      float cooldownRemaining = BLOCK_COOLDOWN - (Time.time - _lastBlockStopTime);
      if (cooldownRemaining > 0)
                    return $"Cooldown: {cooldownRemaining:F1}s";
    return "Ready";
 }
      
      float commitRemaining = _blockCommitEndTime - Time.time;
if (commitRemaining > 0)
     return $"Blocking (committed: {commitRemaining:F1}s)";
     return "Blocking";
        }
    }
}
