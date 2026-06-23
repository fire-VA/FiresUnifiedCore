using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;
using FiresCore.Npc.AI;
using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc
{
    /// <summary>
    /// Handles combat mechanics for companion NPCs.
    /// Manages weapon attacks, armor stats, and combat animations.
    /// Delegates weapon-specific behavior to WeaponBehavior subclasses.
    /// Uses CompanionEquipmentData for all equipment information.
    /// 
    /// ATTACK COMBOS:
    /// Weapons with multiple chain levels execute proper combos (swing_0 -> swing_1 -> swing_2).
    /// Combos reset if too much time passes between attacks.
    /// 
    /// SECONDARY ATTACKS:
    /// Weapons with secondary attacks use them tactically:
    /// - Target staggered (guaranteed hit)
    /// - Target blocking (guard break)
    /// - Random chance for variety
    /// 
    /// WEAPON SWAPPING:
    /// Companions intelligently swap between melee/ranged based on:
    /// - Target distance and accessibility
    /// - Elevation differences
    /// - Combat situation
    /// </summary>
    public class CompanionCombat : MonoBehaviour
    {
      // ...existing code until after [Header("Debug")] and VerboseLogging...
     [Header("Combat Settings")]
        public float baseAttackCooldown = 2f;
        public float attackRange = 2.5f;
        public float attackAngle = 90f;
        public bool useNativeAttackSystem = true;

        [Header("Ranged Settings")]
        public float rangedAttackRange = 25f;
        public float rangedMinRange = 12f;
        public float defaultBowDrawTime = 1.5f;
        public float defaultCrossbowReloadTime = 2f;
        public float bowAimHoldTime = 0.3f;
        public float earlyReleaseMinDraw = 0.5f;
        public float earlyReleaseThreatRange = 6f;
        public float rangedRetreatRange = 8f;

   [Header("Blocking Settings")]
        public float blockChance = 0.3f;
        public float parryWindow = 0.25f;
        public float parryChance = 0.15f;
        public float blockStaminaCost = 10f;
        public float blockDamageReduction = 0.5f;
        public float parryDamageReduction = 0.9f;

        [Header("Dodge Settings")]
        public float dodgeChance = 0.2f;
        public float dodgeCooldown = 3f;
        public float dodgeDistance = 4f;
        public float dodgeThreatRange = 5f;
        public float dodgeStaminaCost = 15f;

     [Header("Aggression Settings")]
        public float threatDetectionRange = 30f;
        public float threatUpdateInterval = 0.5f;
        public float aggroStickyTime = 5f;
        public float opportunisticAttackRange = 1.5f;

        [Header("Movement Settings")]
        public float attackMovementSpeedMultiplier = 0.4f;

        [Header("Weapon Swapping")]
        [Tooltip("Enable intelligent weapon swapping between melee/ranged")]
        public bool enableWeaponSwapping = true;

        [Header("Debug")]
        public static bool VerboseLogging = false;

        // Core components
        private CompanionController _companion;
        private CompanionInventory _inventory;
        private CompanionSkills _skills;
        private Humanoid _humanoid;
        private CompanionAI _companionAI;
        private Character _character;
        private Animator _animator;
        private ZNetView _nview;
        private ZSyncAnimation _zanim;
        private CharacterAnimEvent _animEvent;
        
        // Threat analysis system
        private ThreatAnalyzer _threatAnalyzer;

        // Equipment data system
        private CompanionEquipmentData _equipmentData;

        // Combat context shared with behaviors
        private CombatContext _context;

        // Weapon behaviors
        private Dictionary<WeaponType, WeaponBehavior> _behaviors;
        private WeaponBehavior _activeBehavior;
        private BlockingBehavior _blockingBehavior;
        private DodgeBehavior _dodgeBehavior;
        
        // Weapon swap manager
        private WeaponSwapManager _weaponSwapManager;

        // Weapon state
        private WeaponType _weaponType = WeaponType.Unarmed;

        // Threat tracking
        private float _lastThreatUpdate;
        private Character _currentThreat;
        private float _lastSeenThreatTime;

        // CompanionAI doesn't need an AI-settings backup the way
        // MonsterAI did â€” we control every relevant setting directly each
        // frame. The original backup fields and the _aiSettingsBackedUp
        // guard were removed in the warnings cleanup pass; BackupAISettings
        // is now a no-op kept for symmetry with the call site.

        public enum WeaponType
        {
       Unarmed,
       OneHandedMelee,
            TwoHandedMelee,
     Bow,
       Crossbow,
  Staff,
       Tool
        }

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
            _skills = GetComponent<CompanionSkills>();
            _humanoid = GetComponent<Humanoid>();
            _companionAI = GetComponent<CompanionAI>();
            _character = GetComponent<Character>();
            _animator = GetComponentInChildren<Animator>(true);
            _nview = GetComponent<ZNetView>();
            _zanim = GetComponent<ZSyncAnimation>();
            _animEvent = GetComponentInChildren<CharacterAnimEvent>(true);
     
         // Get or add the equipment data component
          _equipmentData = GetComponent<CompanionEquipmentData>();
        if (_equipmentData == null)
  {
         _equipmentData = gameObject.AddComponent<CompanionEquipmentData>();
      }
            
            // Get or add weapon swap manager
            _weaponSwapManager = GetComponent<WeaponSwapManager>();
            if (_weaponSwapManager == null && enableWeaponSwapping)
            {
                _weaponSwapManager = gameObject.AddComponent<WeaponSwapManager>();
            }
            
            // Get or add threat analyzer
            _threatAnalyzer = GetComponent<ThreatAnalyzer>();
            if (_threatAnalyzer == null)
            {
                _threatAnalyzer = gameObject.AddComponent<ThreatAnalyzer>();
            }

            InitializeCombatSystem();

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombat] Awake - Components found: " +
                    $"Humanoid={_humanoid != null}, CompanionAI={_companionAI != null}, " +
                    $"Animator={_animator != null}, ZSyncAnimation={_zanim != null}, " +
                    $"CharacterAnimEvent={_animEvent != null}, EquipmentData={_equipmentData != null}, " +
                    $"WeaponSwapManager={_weaponSwapManager != null}");
            }
        }

     private void Start()
        {
            if (_nview != null)
 {
        _nview.Register<string, int>("RPC_CompanionAttack", RPC_CompanionAttack);
      _nview.Register<bool>("RPC_CompanionBlock", RPC_CompanionBlock);
     _nview.Register<bool>("RPC_CompanionBowAim", RPC_CompanionBowAim);
     _nview.Register("RPC_CompanionDodge", RPC_CompanionDodge);
 }

      // Subscribe to equipment changes
  if (_equipmentData != null)
         {
         _equipmentData.OnEquipmentChanged += OnEquipmentDataChanged;
            }

       BackupAISettings();
        
            // Ensure CharacterAnimEvent is properly set up
           EnsureCharacterAnimEvent();

          if (VerboseLogging && _animator != null)
      {
 LogAnimatorParameters();
  }
        }

        /// <summary>
        /// Ensures CharacterAnimEvent is present and properly initialized.
   /// This is critical for native Attack.Start() to work - it requires a valid animEvent.
        /// </summary>
      private void EnsureCharacterAnimEvent()
        {
     if (VerboseLogging)
     {
         Debug.Log($"[CompanionCombat] ========== EnsureCharacterAnimEvent START ==========");
         Debug.Log($"[CompanionCombat] Existing _animEvent: {(_animEvent != null ? _animEvent.gameObject.name : "NULL")}");
         Debug.Log($"[CompanionCombat] _animator: {(_animator != null ? _animator.gameObject.name : "NULL")}");
     }
          
     if (_animEvent != null)
         {
       if (VerboseLogging)
       {
           Debug.Log($"[CompanionCombat] CharacterAnimEvent already exists on {_animEvent.gameObject.name}");

           // Verify it can find Character
           var characterCheck = _animEvent.GetComponentInParent<Character>();
           Debug.Log($"[CompanionCombat] AnimEvent can find Character: {(characterCheck != null ? characterCheck.name : "NULL")}" );
          
           Debug.Log($"[CompanionCombat] ========== EnsureCharacterAnimEvent END (ALREADY EXISTS) ==========");
       }
       return;
       }
   
   
  // Try to find or create CharacterAnimEvent
   if (_animator != null)
         {
  if (VerboseLogging)
      Debug.Log($"[CompanionCombat] Looking for CharacterAnimEvent on animator GameObject: {_animator.gameObject.name}");
       
     // CharacterAnimEvent should be on the same GameObject as the Animator
     _animEvent = _animator.GetComponent<CharacterAnimEvent>();
        
  if (_animEvent == null)
     {
  if (VerboseLogging)
      Debug.Log($"[CompanionCombat] CharacterAnimEvent not found - adding dynamically to {_animator.gameObject.name}");
     _animEvent = _animator.gameObject.AddComponent<CharacterAnimEvent>();
     if (VerboseLogging)
         Debug.Log($"[CompanionCombat] Added CharacterAnimEvent to {_animator.gameObject.name} at runtime");
     }
    else
       {
     if (VerboseLogging)
             Debug.Log($"[CompanionCombat] Found existing CharacterAnimEvent on {_animEvent.gameObject.name}");
     }
            
   // Update context with the animEvent
 if (_context != null)
    {
    if (VerboseLogging)
                Debug.Log($"[CompanionCombat] Updating context with AnimEvent");
    _context.AnimEvent = _animEvent;
          }
     
   // Verify it can find the Character
    var characterInParent = _animEvent.GetComponentInParent<Character>();
 if (VerboseLogging)
 {
     Debug.Log($"[CompanionCombat] Character in parent check: {(characterInParent != null ? characterInParent.name : "NULL")}");
          
     // Check the hierarchy
     Transform current = _animEvent.transform;
     Debug.Log($"[CompanionCombat] Hierarchy check from AnimEvent:");
  while (current != null)
   {
      var charComp = current.GetComponent<Character>();
            Debug.Log($"[CompanionCombat]   - {current.name}: Character={charComp != null}");
       current = current.parent;
       }
 }
         
if (characterInParent == null)
        {
       Debug.LogWarning($"[CompanionCombat] CharacterAnimEvent cannot find Character in parent! " +
            $"AnimEvent is on '{_animEvent.gameObject.name}', Character should be on '{gameObject.name}'");
     
               // This is a problem - CharacterAnimEvent.Awake() will fail
     // Let's check if Character is on the root
  var charOnRoot = gameObject.GetComponent<Character>();
  if (VerboseLogging)
         Debug.Log($"[CompanionCombat] Character on root ({gameObject.name}): {charOnRoot != null}");
    
     if (charOnRoot != null && VerboseLogging)
    {
       Debug.Log($"[CompanionCombat] Character exists on root but AnimEvent can't find it via GetComponentInParent!");
    Debug.Log($"[CompanionCombat] This means the Animator is on a child object that's NOT a child of the Character.");
              }
 }
   else if (VerboseLogging)
      {
     Debug.Log($"[CompanionCombat] CharacterAnimEvent properly linked to Character: {characterInParent.name}");
   }
        }
       else
  {
   Debug.LogWarning($"[CompanionCombat] No Animator found - CharacterAnimEvent cannot be created");
     }
      
    if (VerboseLogging)
    {
        Debug.Log($"[CompanionCombat] Final _animEvent: {(_animEvent != null ? _animEvent.gameObject.name : "NULL")}");
        Debug.Log($"[CompanionCombat] Final _context.AnimEvent: {(_context?.AnimEvent != null ? _context.AnimEvent.gameObject.name : "NULL")}");
        Debug.Log($"[CompanionCombat] ========== EnsureCharacterAnimEvent END ==========");
    }
        }

        private void OnDestroy()
   {
            if (_equipmentData != null)
         {
     _equipmentData.OnEquipmentChanged -= OnEquipmentDataChanged;
  }
        }

        private void Update()
   {
            // Allow both tamed and wild companions to fight
            // Wild companions need to defend themselves against hostile creatures
            if (_companion == null) return;
       if (_nview == null || !_nview.IsValid()) return;
            if (_companion.isDefeated) return;

        UpdateCombat();
        UpdateThreatDetection();

     // Update behaviors
        _activeBehavior?.Update();
     _blockingBehavior?.Update(_activeBehavior?.IsAttacking ?? false, _dodgeBehavior?.IsDodging ?? false, _weaponType);
  _dodgeBehavior?.Update(_activeBehavior?.IsAttacking ?? false, _weaponType, CancelBowDrawIfNeeded);

       UpdateOpportunisticAttacks();
          
    // Update food timers
        _equipmentData?.UpdateFoodTimers();
     }

        private void InitializeCombatSystem()
        {
            // Create combat context
            _context = new CombatContext
            {
                Companion = _companion,
                Inventory = _inventory,
                CompanionSkills = _skills,
                Humanoid = _humanoid,
                CompanionAI = _companionAI,
                Character = _character,
                Animator = _animator,
                NView = _nview,
                ZAnim = _zanim,
                AnimEvent = _animEvent,
                Transform = transform,
                EquipmentData = _equipmentData,
                ThreatAnalyzer = _threatAnalyzer,

                // Copy settings
                BaseAttackCooldown = baseAttackCooldown,
                AttackRange = attackRange,
                AttackAngle = attackAngle,
                UseNativeAttackSystem = useNativeAttackSystem,
                RangedAttackRange = rangedAttackRange,
                RangedMinRange = rangedMinRange,
                DefaultBowDrawTime = defaultBowDrawTime,
                DefaultCrossbowReloadTime = defaultCrossbowReloadTime,
                BowAimHoldTime = bowAimHoldTime,
                EarlyReleaseMinDraw = earlyReleaseMinDraw,
                EarlyReleaseThreatRange = earlyReleaseThreatRange,
                RangedRetreatRange = rangedRetreatRange,
                BlockChance = blockChance,
                ParryWindow = parryWindow,
                ParryChance = parryChance,
                BlockDamageReduction = blockDamageReduction,
                ParryDamageReduction = parryDamageReduction,
                DodgeChance = dodgeChance,
                DodgeCooldown = dodgeCooldown,
                DodgeDistance = dodgeDistance,
                DodgeThreatRange = dodgeThreatRange
            };

          // Create support behaviors first so we can wire them up
            _dodgeBehavior = new DodgeBehavior();
  _dodgeBehavior.Initialize(_context, this);

            _blockingBehavior = new BlockingBehavior();
     _blockingBehavior.Initialize(_context, this);

    // Create weapon behaviors
  var bowBehavior = new BowBehavior();
 var crossbowBehavior = new CrossbowBehavior();

    _behaviors = new Dictionary<WeaponType, WeaponBehavior>
            {
       { WeaponType.Unarmed, new UnarmedBehavior() },
                { WeaponType.OneHandedMelee, new MeleeBehavior() },
         { WeaponType.TwoHandedMelee, new MeleeBehavior() },
       { WeaponType.Bow, bowBehavior },
                { WeaponType.Crossbow, crossbowBehavior },
   { WeaponType.Staff, new StaffBehavior() },
            { WeaponType.Tool, new MeleeBehavior() }
            };

            // Initialize all behaviors
            foreach (var behavior in _behaviors.Values)
            {
                behavior.Initialize(_context, this);
            }

            // Wire up dodge behavior with crossbow (crossbow still uses dodge)
            crossbowBehavior.SetDodgeBehavior(_dodgeBehavior);
            _dodgeBehavior.SetBowBehavior(bowBehavior);

            // Start with unarmed
            _activeBehavior = _behaviors[WeaponType.Unarmed];
            _activeBehavior.OnActivate();
     }

        private void BackupAISettings()
        {
            // No-op â€” see field-region comment for why.
        }

        #endregion

  #region Combat Update

        // ...existing OnEquipmentDataChanged method...
        private void OnEquipmentDataChanged()
        {
   if (_equipmentData == null) return;

         // Determine weapon type from animation state
       WeaponType newType = DetermineWeaponTypeFromAnimState(_equipmentData.WeaponAnimationState);
       
   if (newType != _weaponType)
        {
         _weaponType = newType;

          // Switch behavior
         _activeBehavior?.OnDeactivate();
   _activeBehavior = _behaviors.TryGetValue(newType, out var behavior) ? behavior : _behaviors[WeaponType.Unarmed];
     _activeBehavior.OnActivate();
       _activeBehavior.UpdateAttackData();

      // Update animation state
      UpdateAnimationState();
            }
            
          // Refresh context from equipment data
            _context.RefreshFromEquipmentData();

            if (VerboseLogging)
  {
        Debug.Log($"[CompanionCombat] Equipment changed - Weapon: {_equipmentData.WeaponShared?.m_name ?? "Unarmed"}, " +
   $"Type: {_weaponType}, ChainLevels: {_equipmentData.AttackChainLevels}, " +
                    $"HasSecondary: {_equipmentData.SecondaryAttack != null}");
        }
        }

        private void UpdateCombat()
        {
            // Throttled equipment checks - don't run every frame
            if (Time.time - _lastEquipmentChangeCheckTime >= EQUIPMENT_CHECK_INTERVAL)
            {
                _lastEquipmentChangeCheckTime = Time.time;
                // Check if equipment needs refresh (inventory might have changed)
                CheckForEquipmentChanges();
            }
            
            // Throttled weapon selection checks - only check periodically
            if (Time.time - _lastWeaponCheckTime >= WEAPON_CHECK_INTERVAL)
            {
                _lastWeaponCheckTime = Time.time;
                // Check if we're using a gathering tool and need to swap to a real weapon
                CheckAndSwapFromGatheringTool();
                
                // CRITICAL: Check if we're unarmed but have weapons available
                // This fixes the issue where weapons on back slots don't get equipped
                CheckAndEquipAvailableWeapon();
            }

            // Check if we should attack
            if (_activeBehavior != null && !_blockingBehavior.IsBlocking && !_dodgeBehavior.IsDodging)
            {
                var target = _companionAI?.GetTargetCreature();
                if (target != null && _activeBehavior.ShouldAttack(target))
                {
                    // STEALTH CHECK: If stealthed, only attack when in backstab position
                    // This prevents the rogue from immediately attacking and wasting the stealth bonus
                    if (IsStealthed() && !IsInBackstabPosition(target))
                    {
                        // Don't attack yet - let the AI position us behind the target first
                        // The companion will continue approaching and attack once in position
                        return;
                    }
                    
                    if (_nview != null && _nview.IsOwner())
                    {
                        _blockingBehavior.StopBlocking();
                        _activeBehavior.ExecuteAttack(target);
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if the companion currently has an active stealth effect.
        /// </summary>
        private bool IsStealthed()
        {
            if (_character == null) return false;
            return StatusEffectManager.HasEffect(_character, StatusEffectManager.EFFECT_STEALTH);
        }
        
        /// <summary>
        /// Checks if the companion is in a backstab position (behind the target).
        /// For stealth attacks, we want to be behind the target for maximum damage.
        /// </summary>
        /// <param name="target">The target to check position against</param>
        /// <returns>True if behind the target (within ~120 degree cone from behind)</returns>
        private bool IsInBackstabPosition(Character target)
        {
            if (target == null) return true; // If no target, allow attack
            
            // Get direction from target to us
            Vector3 toCompanion = (transform.position - target.transform.position).normalized;
            toCompanion.y = 0; // Ignore vertical component
            
            // Get target's forward direction
            Vector3 targetForward = target.transform.forward;
            targetForward.y = 0;
            targetForward.Normalize();
            
            // Calculate dot product - negative means we're behind the target
            // Dot of -1 = directly behind, 0 = to the side, 1 = in front
            float dot = Vector3.Dot(targetForward, toCompanion);
            
            // We consider "backstab position" as being behind or to the side-rear
            // dot < 0.2 means we're in roughly the back 144 degrees (generous for gameplay)
            // This allows attacks from the side-back, not just directly behind
            bool isBehind = dot < 0.2f;
            
            // Also check distance - must be close enough to attack
            float dist = Vector3.Distance(transform.position, target.transform.position);
            bool inRange = dist <= attackRange * 1.5f; // Slightly generous range check
            
            if (VerboseLogging && IsStealthed())
            {
                Debug.Log($"[CompanionCombat] Backstab check: dot={dot:F2}, behind={isBehind}, dist={dist:F1}, inRange={inRange}");
            }
            
            return isBehind && inRange;
        }
        
        // Track if we've already logged the unarmed warning for this combat session
        private float _lastUnarmedLogTime;
        private const float UNARMED_LOG_COOLDOWN = 30f;
        
        // Throttle timers for weapon selection checks
        private float _lastWeaponCheckTime;
        private const float WEAPON_CHECK_INTERVAL = 0.5f;
        private float _lastEquipmentChangeCheckTime;
        private const float EQUIPMENT_CHECK_INTERVAL = 0.3f;
        
        /// <summary>
        /// Checks if companion is unarmed but has weapons available in back slots or storage.
        /// Uses intelligent tactical evaluation to pick the best weapon for the situation.
        /// This fixes the issue where companions fight with fists when they have weapons.
        /// </summary>
        private void CheckAndEquipAvailableWeapon()
        {
            // Only check when we have a target (in combat)
            var target = _companionAI?.GetTargetCreature();
            if (target == null || target.IsDead()) return;
            
            // Check if currently unarmed
            if (_weaponType != WeaponType.Unarmed) return;
            
            // Check hands - if either hand has a weapon, we're not truly unarmed
            var rightHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            if (rightHand != null && rightHand.IsWeapon() && !IsShield(rightHand)) return;
            if (leftHand != null && (IsBow(leftHand) || leftHand.IsWeapon())) return;
            
            // We're truly unarmed - check what's in back slots
            var rightBack = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            // Log once every few seconds to avoid spam.
            bool shouldLog = Time.time - _lastUnarmedLogTime > UNARMED_LOG_COOLDOWN;
            if (shouldLog)
            {
                _lastUnarmedLogTime = Time.time;
                if (rightBack != null || leftBack != null)
                {
                    Debug.Log($"[CompanionCombat] {_companion?.companionName} is UNARMED but has back slots: " +
                        $"RightBack={rightBack?.m_shared?.m_name ?? "empty"}, LeftBack={leftBack?.m_shared?.m_name ?? "empty"}");
                }
            }
            
            // First, scan what weapons we have available
            if (_weaponSwapManager != null)
            {
                _weaponSwapManager.ScanAvailableWeapons();
                var (melee, ranged) = _weaponSwapManager.GetAvailableWeapons();
                
                // If we only have one type, use it
                if (melee != null && ranged == null)
                {
                    if (shouldLog)
                        Debug.Log($"[CompanionCombat] Only melee available: {melee.PrefabName} - forcing swap");
                    EquipWeaponFromAnywhere(false);
                    return;
                }
                if (ranged != null && melee == null)
                {
                    if (shouldLog)
                        Debug.Log($"[CompanionCombat] Only ranged available: {ranged.PrefabName} - forcing swap");
                    EquipWeaponFromAnywhere(true);
                    return;
                }
                
                // We have both - use tactical evaluation to pick the best one
                if (melee != null && ranged != null)
                {
                    float distToTarget = Vector3.Distance(transform.position, target.transform.position);
                    var recommendation = _weaponSwapManager.EvaluateCombatOpeningWeapon(target, distToTarget);
                    
                    if (shouldLog)
                        Debug.Log($"[CompanionCombat] Both weapons available - Tactical recommendation: {recommendation} (dist: {distToTarget:F1}m)");
                    
                    bool useRanged = recommendation == WeaponSwapManager.WeaponRecommendation.Ranged;
                    EquipWeaponFromAnywhere(useRanged);
                    return;
                }
                
                // WeaponSwapManager found nothing - try fallback
                if (shouldLog)
                    Debug.Log($"[CompanionCombat] {_companion?.companionName} WeaponSwapManager found NO weapons - falling back to direct back slot check");
            }
            else
            {
                if (shouldLog)
                    Debug.Log($"[CompanionCombat] {_companion?.companionName} No WeaponSwapManager - falling back to direct back slot check");
            }
            
            // Fallback: manually check back slots if WeaponSwapManager not available or found nothing
            EquipWeaponFromBackSlots();
        }
        
        /// <summary>
        /// Equips a weapon from wherever it's stored (back slots or storage).
        /// </summary>
        /// <param name="preferRanged">True to prefer ranged weapon, false for melee</param>
        private void EquipWeaponFromAnywhere(bool preferRanged)
        {
            if (_weaponSwapManager != null)
            {
                if (preferRanged)
                {
                    if (_weaponSwapManager.ForceSwapToRanged())
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombat] Equipped ranged weapon via WeaponSwapManager");
                        return;
                    }
                    // Fallback to melee if ranged failed
                    if (_weaponSwapManager.ForceSwapToMelee())
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombat] Ranged failed, equipped melee instead");
                        return;
                    }
                }
                else
                {
                    if (_weaponSwapManager.ForceSwapToMelee())
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombat] Equipped melee weapon via WeaponSwapManager");
                        return;
                    }
                    // Fallback to ranged if melee failed
                    if (_weaponSwapManager.ForceSwapToRanged())
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombat] Melee failed, equipped ranged instead");
                        return;
                    }
                }
            }
            
            // Ultimate fallback - try back slots directly
            EquipWeaponFromBackSlots();
        }
        
        /// <summary>
        /// Directly equips weapons from back slots - used as fallback.
        /// </summary>
        private void EquipWeaponFromBackSlots()
        {
            var rightBack = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            // Check for melee on right back
            if (rightBack != null && rightBack.IsWeapon() && !IsShield(rightBack) && !IsBow(rightBack) && !IsRangedItem(rightBack))
            {
                Debug.Log($"[CompanionCombat] Equipping melee from RightBack: {rightBack.m_shared?.m_name}");
                
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, rightBack);
                
                // Also equip shield from left back if available
                if (leftBack != null && IsShield(leftBack))
                {
                    Debug.Log($"[CompanionCombat] Also equipping shield from LeftBack: {leftBack.m_shared?.m_name}");
                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftBack);
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, leftBack);
                }
                
                FinalizeEquipmentChange();
                return;
            }
            
            // Check for ranged on left back
            if (leftBack != null && (IsBow(leftBack) || IsRangedItem(leftBack)))
            {
                Debug.Log($"[CompanionCombat] Equipping ranged from LeftBack: {leftBack.m_shared?.m_name}");
                
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, leftBack);
                FinalizeEquipmentChange();
                return;
            }
            
            // Check for ranged on right back (unusual but possible)
            if (rightBack != null && (IsBow(rightBack) || IsRangedItem(rightBack)))
            {
                Debug.Log($"[CompanionCombat] Equipping ranged from RightBack: {rightBack.m_shared?.m_name}");
                
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, rightBack);
                FinalizeEquipmentChange();
                return;
            }
            
            // Check for melee on left back (unusual but possible)
            if (leftBack != null && leftBack.IsWeapon() && !IsShield(leftBack) && !IsBow(leftBack) && !IsRangedItem(leftBack))
            {
                Debug.Log($"[CompanionCombat] Equipping melee from LeftBack: {leftBack.m_shared?.m_name}");
                
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, leftBack);
                FinalizeEquipmentChange();
                return;
            }
            
            // â”€â”€ LAST-RESORT STORAGE SCAN â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Back slots had nothing usable. Walk the storage inventory and
            // pull the first weapon we find. WeaponSwapManager.ScanStorageInventory
            // *should* have caught this earlier â€” if we're here it didn't, and
            // the user has explicitly said "we should always find it." This is
            // the unconditional safety net.
            if (TryEquipFirstWeaponFromStorage()) return;

            // Only log failure if we had back items but couldn't equip them
            if (rightBack != null || leftBack != null)
            {
                Debug.LogWarning($"[CompanionCombat] {_companion?.companionName} EquipWeaponFromBackSlots: Could not equip - " +
                    $"RightBack={rightBack?.m_shared?.m_name ?? "empty"} (type={rightBack?.m_shared?.m_itemType}), " +
                    $"LeftBack={leftBack?.m_shared?.m_name ?? "empty"} (type={leftBack?.m_shared?.m_itemType})");
            }
        }

        /// <summary>
        /// Final fallback when neither WeaponSwapManager nor back slots produced a
        /// weapon: scan storage for any usable weapon and equip it. Returns true if
        /// something was equipped. Permissive â€” accepts any item that <c>IsWeapon()</c>
        /// reports true for, minus shields and gathering tools, minus broken types.
        /// </summary>
        private bool TryEquipFirstWeaponFromStorage()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;

            ItemDrop.ItemData chosen = null;
            bool chosenIsRanged = false;

            foreach (var item in storage.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (!item.IsWeapon()) continue;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield) continue;
                if (IsGatheringToolItem(item)) continue;

                bool isRanged = IsBow(item) || IsRangedItem(item);
                chosen = item;
                chosenIsRanged = isRanged;
                break; // first usable weapon wins; melee preferred order is up to caller
            }

            if (chosen == null) return false;

            // Move the storage item into a hand slot. EquipItemSilent + storage
            // bookkeeping: remove from storage so it's not double-counted.
            try
            {
                var targetSlot = chosenIsRanged
                    ? CompanionInventory.EquipmentSlot.LeftHand
                    : CompanionInventory.EquipmentSlot.RightHand;

                storage.RemoveItem(chosen);
                _inventory.EquipItemSilent(targetSlot, chosen);

                Debug.Log($"[CompanionCombat] {_companion?.companionName} last-resort: equipped {chosen.m_shared.m_name} from storage to {targetSlot}");
                FinalizeEquipmentChange();
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CompanionCombat] Last-resort storage equip failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>True for pickaxes / axes used as gathering tools (we don't want
        /// to swap to a stone pickaxe in combat just because it's classified as a weapon).</summary>
        private static bool IsGatheringToolItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            // Pickaxes are PickAxe item type; harvesting axes are tagged via skill.
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool) return true;
            string n = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            if (n.Contains("pickaxe") || n.Contains("hoe") || n.Contains("cultivator")) return true;
            return false;
        }
        
        /// <summary>
        /// Finalizes equipment change - recalculates bonuses, applies visuals, saves.
        /// </summary>
        private void FinalizeEquipmentChange()
        {
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            _equipmentData?.RefreshAllEquipmentData();
        }
        
        /// <summary>
        /// Checks if an item is a ranged weapon (bow, crossbow, staff).
        /// </summary>
        private bool IsRangedItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Bows) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Crossbows) return true;
            if (item.m_shared.m_attack?.m_bowDraw == true) return true;
            if (item.m_shared.m_attack?.m_requiresReload == true) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.ElementalMagic) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.BloodMagic) return true;
            if (item.m_shared.m_attack?.m_attackProjectile != null) return true;
            
            return false;
        }
        
        /// <summary>
        /// Checks if the currently equipped weapon is a gathering tool (pickaxe, axe) and
        /// swaps to a real combat weapon if one is available.
        /// </summary>
        private void CheckAndSwapFromGatheringTool()
        {
            if (_weaponSwapManager == null || !enableWeaponSwapping) return;
            
            // Only check when we have a target
            var target = _companionAI?.GetTargetCreature();
            if (target == null || target.IsDead()) return;
            
            // Check if current weapon is a gathering tool
            var currentWeapon = _equipmentData?.WeaponItem;
            if (currentWeapon == null) return;
            
            // Check if it's a tool type or has gathering skill
            bool isGatheringTool = false;
            var itemType = currentWeapon.m_shared?.m_itemType ?? ItemDrop.ItemData.ItemType.None;
            var skillType = currentWeapon.m_shared?.m_skillType ?? Skills.SkillType.None;
            
            // Tools are for gathering
            if (itemType == ItemDrop.ItemData.ItemType.Tool)
            {
                isGatheringTool = true;
            }
            // Pickaxes and WoodCutting are gathering skills
            else if (skillType == Skills.SkillType.Pickaxes || skillType == Skills.SkillType.WoodCutting)
            {
                isGatheringTool = true;
            }
            
            if (isGatheringTool)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionCombat] {_companion?.companionName} has gathering tool equipped during combat, trying to swap to combat weapon");
                }
                
                // Try to swap to melee first, then ranged
                if (_weaponSwapManager.ForceSwapToMelee())
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionCombat] Swapped from gathering tool to melee weapon");
                    }
                }
                else if (_weaponSwapManager.ForceSwapToRanged())
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionCombat] Swapped from gathering tool to ranged weapon");
                    }
                }
            }
        }

      // ...existing CheckForEquipmentChanges method...
      private void CheckForEquipmentChanges()
        {
       if (_inventory == null || _equipmentData == null) return;

       // Check if weapon changed
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
  var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);

            ItemDrop.ItemData currentWeapon = null;
         if (leftHand != null && (IsBow(leftHand) || leftHand.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft))
    {
                currentWeapon = leftHand;
 }
  else if (rightHand != null && rightHand.IsWeapon() && !IsShield(rightHand))
         {
    currentWeapon = rightHand;
}
            else if (leftHand != null && leftHand.IsWeapon() && !IsShield(leftHand))
            {
                // Only use left hand as weapon if it's not a shield
      currentWeapon = leftHand;
          }

    // If weapon changed, refresh equipment data
            if (currentWeapon != _equipmentData.WeaponItem)
  {
     _equipmentData.RefreshAllEquipmentData();
   }
        }

        // ...existing UpdateOpportunisticAttacks...
        private void UpdateOpportunisticAttacks()
        {
            if (_activeBehavior?.IsAttacking ?? false) return;
            if (_dodgeBehavior?.IsDodging ?? false) return;
 if (_weaponType == WeaponType.Bow || _weaponType == WeaponType.Crossbow) return;

         // Find any enemy very close to us
      var characters = Character.GetAllCharacters();
            foreach (var character in characters)
        {
   if (character == null || character.IsDead()) continue;
  if (character == _character) continue;
            if (!IsValidTarget(character)) continue;

          float dist = Vector3.Distance(transform.position, character.transform.position);
   if (dist <= opportunisticAttackRange)
     {
         SetTargetCreature(character);
         if (VerboseLogging)
     {
         Debug.Log($"[CompanionCombat] Opportunistic attack on {character.m_name} at {dist:F1}m");
      }
           _activeBehavior?.ExecuteAttack(character);
       return;
         }
   }
      }

        // ...existing DetermineWeaponTypeFromAnimState...
        private WeaponType DetermineWeaponTypeFromAnimState(ItemDrop.ItemData.AnimationState animState)
        {
    return animState switch
 {
            ItemDrop.ItemData.AnimationState.Unarmed => WeaponType.Unarmed,
      ItemDrop.ItemData.AnimationState.OneHanded => WeaponType.OneHandedMelee,
    ItemDrop.ItemData.AnimationState.TwoHandedClub => WeaponType.TwoHandedMelee,
            ItemDrop.ItemData.AnimationState.TwoHandedAxe => WeaponType.TwoHandedMelee,
             ItemDrop.ItemData.AnimationState.Greatsword => WeaponType.TwoHandedMelee,
                ItemDrop.ItemData.AnimationState.Atgeir => WeaponType.TwoHandedMelee,
            ItemDrop.ItemData.AnimationState.Knives => WeaponType.OneHandedMelee,
    ItemDrop.ItemData.AnimationState.DualAxes => WeaponType.TwoHandedMelee,
       ItemDrop.ItemData.AnimationState.Scythe => WeaponType.TwoHandedMelee,
       ItemDrop.ItemData.AnimationState.Bow => WeaponType.Bow,
  ItemDrop.ItemData.AnimationState.Crossbow => WeaponType.Crossbow,
     ItemDrop.ItemData.AnimationState.Staves => WeaponType.Staff,
    ItemDrop.ItemData.AnimationState.MagicItem => WeaponType.Staff,
 ItemDrop.ItemData.AnimationState.Torch => WeaponType.OneHandedMelee,
     _ => WeaponType.Unarmed
   };
        }

        private bool IsBow(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
  return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow ||
           item.m_shared.m_skillType == Skills.SkillType.Bows;
        }

        /// <summary>
        /// Checks if an item is a shield.
        /// Shields should NOT be treated as weapons - they are defensive items only.
        /// </summary>
        private bool IsShield(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
        }

   // ...existing UpdateAnimationState...
        private void UpdateAnimationState()
        {
            if (_animator == null && _zanim == null) return;

          var animState = _equipmentData?.WeaponAnimationState ?? ItemDrop.ItemData.AnimationState.Unarmed;

            if (_zanim != null)
          {
  _zanim.SetFloat(CombatContext.Hash_statef, (float)animState);
          _zanim.SetInt(CombatContext.Hash_statei, (int)animState);
    }
        else if (_animator != null)
      {
          _animator.SetFloat("statef", (float)animState);
            _animator.SetInteger("statei", (int)animState);
    }

        if (VerboseLogging)
            {
     Debug.Log($"[CompanionCombat] Animation state set to: {animState} ({(int)animState})");
   }
        }

        private void LogAnimatorParameters()
{
            if (!VerboseLogging) return;
        if (_animator == null) return;

   Debug.Log($"[CompanionCombat] Animator parameters for {gameObject.name}:");
            foreach (var param in _animator.parameters)
       {
                Debug.Log($"  - {param.name} ({param.type})");
            }
        }

   private void CancelBowDrawIfNeeded()
        {
            if (_activeBehavior is BowBehavior bowBehavior && bowBehavior.IsBowDrawing)
       {
                bowBehavior.CancelAttack();
         }
        }

        #endregion

        #region Threat Detection

    // Reduced threat range when doing chores/commands - only react to immediate threats
    private const float COMMAND_MODE_THREAT_RANGE = 5f;
    
    // Relaxed mode threat range - only alert when enemies are close
    // Used for following companions NOT in active combat
    // Set to half of the default threatDetectionRange (30m / 2 = 15m)
    private const float RELAXED_MODE_THREAT_RANGE = 15f;
    
    // Time window to consider "recently damaged" for alert state
    private const float RECENTLY_DAMAGED_WINDOW = 10f;
    
    // Track last damage time for relaxed mode detection
    private float _lastDamageReceivedTime = 0f;

private void UpdateThreatDetection()
{
    if (Time.time - _lastThreatUpdate < threatUpdateInterval) return;
    _lastThreatUpdate = Time.time;

    if (_companionAI == null || _character == null) return;

    var currentTarget = _companionAI.GetTargetCreature();

    if (currentTarget != null && !currentTarget.IsDead())
    {
        _currentThreat = currentTarget;
        _lastSeenThreatTime = Time.time;
        return;
    }
    
    // CRITICAL: Check if companion is busy with a command or sub-behavior
    // When doing chores/commands, only enter combat when attacked or enemies are VERY close
    bool isDoingCommandOrChore = IsDoingCommandOrChore();
    
    // Check if companion should be in "relaxed mode" - following but not actively in combat
    // Relaxed mode uses shorter threat detection range to prevent unnecessary aggro
    bool isInRelaxedMode = ShouldUseRelaxedThreatDetection();
    
    // Determine effective threat range based on state
    float effectiveThreatRange;
    if (isDoingCommandOrChore)
    {
        effectiveThreatRange = COMMAND_MODE_THREAT_RANGE;
    }
    else if (isInRelaxedMode)
    {
        effectiveThreatRange = RELAXED_MODE_THREAT_RANGE;
    }
    else
    {
        effectiveThreatRange = threatDetectionRange;
    }

            if (_currentThreat != null && !_currentThreat.IsDead() &&
        Time.time - _lastSeenThreatTime < aggroStickyTime)
    {
         float distToThreat = Vector3.Distance(transform.position, _currentThreat.transform.position);
        if (distToThreat < effectiveThreatRange)
   {
  SetTargetCreature(_currentThreat);
   return;
         }
         else if (isDoingCommandOrChore || isInRelaxedMode)
         {
             // If doing a command or in relaxed mode and threat is outside reduced range, clear it
             _currentThreat = null;
             return;
         }
  }

            Character newThreat = FindNearestThreat(effectiveThreatRange);
            if (newThreat != null)
            {
_currentThreat = newThreat;
       _lastSeenThreatTime = Time.time;
    SetTargetCreature(newThreat);
   SetAlerted(true);

        if (VerboseLogging)
  {
        Debug.Log($"[CompanionCombat] Acquired new threat: {newThreat.m_name} (range: {effectiveThreatRange:F0}m, commandMode: {isDoingCommandOrChore}, relaxedMode: {isInRelaxedMode})");
                }
  }
        }
    
    /// <summary>
    /// Determines if the companion should use relaxed threat detection.
    /// Relaxed mode is used when:
    /// - Companion is following the player
    /// - NOT currently in active combat (no current threat or threat lost)
    /// - NOT recently damaged (player or companion)
    /// - Owner is not in combat
    /// 
    /// This prevents companions from aggressively chasing every distant enemy
    /// while still protecting the player when danger is close.
    /// </summary>
    private bool ShouldUseRelaxedThreatDetection()
    {
        if (_companion == null) return false;
        
        // Only apply relaxed mode to following companions
        if (!_companion.ShouldBeFollowing) return false;
        
        // If already in active combat (have a target), don't use relaxed mode
        if (_currentThreat != null && !_currentThreat.IsDead())
        {
            return false;
        }
        
        // If recently damaged, don't use relaxed mode
        if (Time.time - _lastDamageReceivedTime < RECENTLY_DAMAGED_WINDOW)
        {
            return false;
        }
        
        // Check if owner was recently damaged
        var owner = _companion.GetOwner();
        if (owner != null)
        {
            // Check owner's health - if it changed recently, they might be in combat
            // We can approximate this by checking if owner has a target
            var ownerHumanoid = owner as Humanoid;
            if (ownerHumanoid != null)
            {
                // If owner is actively attacking, companion should be alert
                if (ownerHumanoid.InAttack())
                {
                    return false;
                }
            }
        }
        
        // All conditions met - use relaxed mode
        return true;
    }
    
    /// <summary>
    /// Called when the companion takes damage. Exits relaxed mode temporarily.
    /// </summary>
    public void OnDamageReceived()
    {
        _lastDamageReceivedTime = Time.time;
    }
        
    /// <summary>
    /// Checks if the companion is currently doing a player command or sub-behavior (chore).
    /// When true, the companion should use reduced threat detection range.
    /// </summary>
    private bool IsDoingCommandOrChore()
    {
        // Check for active player command via CompanionCombatMovement
        var combatMovement = GetComponent<CompanionCombatMovement>();
        if (combatMovement != null && combatMovement.HasCommandPriority)
        {
            return true;
        }
        
        // Check for active sub-behavior via CompanionIdleBehavior
        var idleBehavior = GetComponent<CompanionIdleBehavior>();
        if (idleBehavior != null && idleBehavior.IsInSubBehavior)
        {
            return true;
        }
        
        return false;
    }

     private Character FindNearestThreat()
     {
         return FindNearestThreat(threatDetectionRange);
     }
     
     private Character FindNearestThreat(float range)
        {
 Character bestThreat = null;
            float bestScore = float.MaxValue;

            Vector3 ownerPos = transform.position;
      if (_companion != null)
    {
        var owner = _companion.GetOwner();
          if (owner != null)
        {
    ownerPos = owner.transform.position;
                }
            }

      var characters = Character.GetAllCharacters();
     foreach (var character in characters)
            {
        if (character == null || character.IsDead()) continue;
if (character == _character) continue;
  if (!IsValidTarget(character)) continue;

     float distToCompanion = Vector3.Distance(transform.position, character.transform.position);
  if (distToCompanion > range) continue;

 float distToOwner = Vector3.Distance(ownerPos, character.transform.position);
         float score = Mathf.Min(distToCompanion, distToOwner);

        if (score < bestScore)
        {
  bestScore = score;
            bestThreat = character;
}
            }

       return bestThreat;
        }

        private bool IsValidTarget(Character target)
        {
            if (target == null) return false;
            if (target.IsDead()) return false;
            if (target == _character) return false;
            if (target.IsTamed()) return false;
            if (target.IsPlayer()) return false;

            if (_companionAI != null)
            {
                return _companionAI.IsEnemy(target);
            }

            return true;
        }

        private void SetTargetCreature(Character target)
        {
            if (_companionAI == null || target == null) return;
            
            // CompanionAI provides direct access - no reflection needed
            _companionAI.ForceTarget(target);
        }

        private void SetAlerted(bool alert)
        {
            // CompanionAI handles alerted state internally
            // Setting a target via ForceTarget already sets alert state
        }

      #endregion

    #region RPCs

        private void RPC_CompanionAttack(long sender, string animTrigger, int attackIndex)
        {
  if (_nview != null && _nview.IsOwner()) return;
 _context.PlayAttackAnimation(animTrigger, attackIndex);
        }

        private void RPC_CompanionBlock(long sender, bool blocking)
        {
            _blockingBehavior?.HandleBlockRPC(blocking);
 }

        private void RPC_CompanionBowAim(long sender, bool aiming)
    {
       if (_nview != null && _nview.IsOwner()) return;

      if (_zanim != null)
         {
   _zanim.SetBool(CombatContext.Hash_bow_aim, aiming);
       }
            else if (_animator != null && _context.HasAnimatorParameter("bow_aim"))
   {
         _animator.SetBool("bow_aim", aiming);
            }
      }

        private void RPC_CompanionDodge(long sender)
        {
     _dodgeBehavior?.HandleDodgeRPC();
        }

      #endregion

        #region Public API

    /// <summary>Gets the equipment data system.</summary>
        public CompanionEquipmentData GetEquipmentData() => _equipmentData;

        /// <summary>Gets the current weapon item.</summary>
        public ItemDrop.ItemData GetCurrentWeapon() => _equipmentData?.WeaponItem;

        public bool IsAttacking() => _activeBehavior?.IsAttacking ?? false ||
         (_activeBehavior is BowBehavior bow && bow.IsBowDrawing) ||
      (_activeBehavior is CrossbowBehavior xbow && xbow.IsReloading);

        public bool IsBlocking() => _blockingBehavior?.IsBlocking ?? false;

        public bool IsRangedWeapon() => _weaponType == WeaponType.Bow || _weaponType == WeaponType.Crossbow || _weaponType == WeaponType.Staff;

        public bool IsBowDrawing() => _activeBehavior is BowBehavior bow && bow.IsBowDrawing;

        public bool IsDodging() => _dodgeBehavior?.IsDodging ?? false;

        public WeaponType GetWeaponType() => _weaponType;

   /// <summary>Gets total armor from equipment.</summary>
        public float GetTotalArmor() => _equipmentData?.TotalArmor ?? 0f;

        /// <summary>Gets movement modifier from equipment.</summary>
   public float GetMovementModifier() => _equipmentData?.TotalMovementModifier ?? 0f;

        /// <summary>Checks if a shield is equipped.</summary>
  public bool HasShieldEquipped()
 {
            if (_equipmentData == null || _inventory == null) return false;
     var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand?.m_shared == null) return false;
            return leftHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
   }

  /// <summary>Alias for HasShieldEquipped for compatibility.</summary>
   public bool HasShield() => HasShieldEquipped();

        /// <summary>Requests an emergency dodge in a direction.</summary>
        public void RequestEmergencyDodge(Vector3 direction)
        {
            if (_dodgeBehavior == null) return;
            
            var target = _companionAI?.GetTargetCreature();
            if (target != null)
            {
                _dodgeBehavior.ExecuteRetreatDodge(target, true);
            }
        }

    /// <summary>Requests blocking to start.</summary>
        public void RequestBlock()
    {
  _blockingBehavior?.StartBlocking();
        }

        /// <summary>Requests blocking to stop.</summary>
   public void RequestStopBlocking()
        {
       _blockingBehavior?.StopBlocking();
        }

        /// <summary>Calculates damage reduction using equipment armor.</summary>
        public float CalculateDamageReduction(float damage)
        {
          return _equipmentData?.CalculateDamageReduction(damage) ?? damage;
        }

        /// <summary>Forces equipment data refresh.</summary>
   public void ReconfigureAIForWeapon()
        {
         _equipmentData?.RefreshAllEquipmentData();
   }

        public void ForceAttack()
        {
            var target = _companionAI?.GetTargetCreature();
            if (target != null && !(_activeBehavior?.IsAttacking ?? false))
            {
                _activeBehavior?.ExecuteAttack(target);
            }
        }

        public void CancelAttack()
        {
            _activeBehavior?.CancelAttack();
            _blockingBehavior?.StopBlocking();
        }

        public float ProcessIncomingDamage(HitData hit)
        {
            // CRITICAL: Record that we received damage to exit relaxed mode
            OnDamageReceived();
            
            return _blockingBehavior?.ProcessIncomingDamage(hit) ?? hit?.GetTotalDamage() ?? 0f;
        }
  
        // ===== NEW COMBO/SECONDARY API =====
        
 /// <summary>Gets the current attack chain level (0, 1, 2, etc.)</summary>
        public int GetCurrentChainLevel() => _activeBehavior?.CurrentChainLevel ?? 0;
      
        /// <summary>Returns true if a combo is currently active.</summary>
    public bool IsComboActive() => _activeBehavior?.IsComboActive ?? false;
      
        /// <summary>Gets the maximum chain level for current weapon.</summary>
        public int GetMaxChainLevel() => _equipmentData?.AttackChainLevels ?? 1;
   
        /// <summary>Returns true if current weapon has a secondary attack.</summary>
        public bool HasSecondaryAttack() => _equipmentData?.SecondaryAttack != null;
        
   // ===== NEW WEAPON SWAP API =====
        
        /// <summary>Gets the weapon swap manager.</summary>
        public WeaponSwapManager GetWeaponSwapManager() => _weaponSwapManager;
        
        /// <summary>Returns true if companion can swap between melee and ranged.</summary>
        public bool CanSwapWeapons() => _weaponSwapManager?.CanSwapWeapons() ?? false;
        
        /// <summary>Forces a swap to ranged weapon if available.</summary>
        public bool ForceSwapToRanged() => _weaponSwapManager?.ForceSwapToRanged() ?? false;
        
        /// <summary>Forces a swap to melee weapon if available.</summary>
        public bool ForceSwapToMelee() => _weaponSwapManager?.ForceSwapToMelee() ?? false;
        
        // ===== THREAT ANALYZER API =====
        
        /// <summary>Gets the threat analyzer for tactical decisions.</summary>
        public ThreatAnalyzer GetThreatAnalyzer() => _threatAnalyzer;
        
        /// <summary>Gets the current combat situation assessment.</summary>
        public ThreatAnalyzer.CombatSituation GetCombatSituation() => _threatAnalyzer?.GetCurrentSituation() ?? ThreatAnalyzer.CombatSituation.Safe;
        
        /// <summary>Gets the threat profile for a specific enemy.</summary>
        public ThreatAnalyzer.ThreatProfile GetThreatProfile(Character enemy) => _threatAnalyzer?.GetThreatProfile(enemy) ?? ThreatAnalyzer.ThreatProfile.Empty;
        
        /// <summary>Gets the recommended combat stance.</summary>
        public ThreatAnalyzer.CombatStance GetRecommendedStance() => _threatAnalyzer?.GetCurrentSituation().RecommendedStance ?? ThreatAnalyzer.CombatStance.Balanced;
        
        /// <summary>Reports damage received for threat tracking.</summary>
        public void ReportDamageReceived(float damage, Character source)
        {
            _threatAnalyzer?.RecordDamageReceived(damage, source);
        }

        #endregion
    }
}
