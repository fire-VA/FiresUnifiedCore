using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Manages intelligent weapon swapping for companions.
    /// Evaluates combat situations and swaps between melee/ranged weapons as needed.
    /// 
    /// SWAP TRIGGERS:
    /// - Target unreachable (elevation difference, water, obstacles) ? Swap to ranged
    /// - Target fleeing and out of melee range ? Swap to ranged
    /// - Target closing in while using ranged ? Swap to melee
    /// - Out of ammo ? Swap to melee
    /// - No valid target nearby ? Keep current
    /// 
    /// SWAP COOLDOWN:
    /// Prevents constant weapon swapping with a cooldown period.
    /// Also considers animation state - won't swap mid-attack.
    /// </summary>
    public class WeaponSwapManager : MonoBehaviour
    {
        [Header("Swap Settings")]
  [Tooltip("Minimum time between weapon swaps")]
      public float swapCooldown = 5f;
      
        [Tooltip("Distance at which we consider swapping to ranged")]
        public float rangedPreferenceDistance = 12f;
        
    [Tooltip("Distance at which we consider swapping to melee")]
        public float meleePreferenceDistance = 5f;
        
    [Tooltip("Height difference that triggers ranged swap")]
    public float elevationSwapThreshold = 3f;
        
        [Tooltip("Time to wait before considering target unreachable")]
        public float unreachableCheckTime = 2f;

   [Header("Combat Evaluation")]
        [Tooltip("How often to evaluate weapon swap")]
        public float evaluationInterval = 2f;
     
        [Tooltip("Minimum confidence to trigger a swap")]
public float swapConfidenceThreshold = 0.7f;

        [Header("Weapon Commitment")]
        [Tooltip("How long to commit to a weapon choice before allowing re-evaluation")]
        public float weaponCommitmentDuration = 8f;
        
        [Tooltip("Health percent threshold that allows emergency weapon swap even during commitment")]
        public float emergencyHealthThreshold = 0.25f;

        // Components
      private CompanionController _companion;
        private CompanionInventory _inventory;
      private CompanionCombat _combat;
        private CompanionCombatMovement _movement;
        private CompanionAI _companionAI;
  private Character _character;

 // State
 private float _lastSwapTime = -100f;
        private float _lastEvaluationTime;
        private float _targetUnreachableStartTime;
      private Vector3 _lastTargetPosition;
        private bool _hasTriedToReachTarget;
        
        // Weapon commitment state - once a weapon is chosen, stick with it
        private float _weaponCommitmentExpiry;
        private bool _isCommittedToWeapon;
        private float _lastScanTime = -100f;
        private const float SCAN_COOLDOWN = 1.5f;
        
        // Cached weapon info
        private CachedWeaponInfo _meleeWeapon;
        private CachedWeaponInfo _rangedWeapon;
        private CachedWeaponInfo _supportStaff;      // NEW: Support/buff staves (protection, healing)
        private CachedWeaponInfo _offensiveStaff;    // NEW: Damage staves (fire, ice, lightning)
        private WeaponSlot _currentWeaponSlot = WeaponSlot.None;
        
        // Staff swap state
        private float _lastStaffSwapTime = -100f;
        private const float STAFF_SWAP_COOLDOWN = 5f;  // Don't swap staves too frequently
        private const float PARTY_BUFF_CHECK_INTERVAL = 3f;
        private float _lastPartyBuffCheck;

        public static bool VerboseLogging = false;

     public enum WeaponSlot
        {
     None,
     RightHand,  // Melee typically
 LeftHand,   // Bows, crossbows
  RightBack,  // Stored melee
LeftBack// Stored ranged
     }

        public class CachedWeaponInfo
        {
    public string PrefabName;
     public int Quality;
            public CompanionInventory.EquipmentSlot Slot;
  public CompanionInventory.EquipmentSlot BackSlot;
public bool IsRanged;
            public float Range;
      public ItemDrop.ItemData.ItemType ItemType;
            public bool IsInStorage;
            public ItemDrop.ItemData StorageItem;
     }

        #region Unity Lifecycle

        private void Awake()
        {
     _companion = GetComponent<CompanionController>();
    _inventory = GetComponent<CompanionInventory>();
 _combat = GetComponent<CompanionCombat>();
            _movement = GetComponent<CompanionCombatMovement>();
            _companionAI = GetComponent<CompanionAI>();
            _character = GetComponent<Character>();
    }

     private void Start()
        {
      // Initial weapon scan
          ScanAvailableWeapons();
        }

        private void Update()
        {
            // Allow both tamed AND wild companions to swap weapons
            if (_companion == null) return;
            if (_combat == null || _inventory == null) return;

            // Don't evaluate while attacking or dodging
            if (_combat.IsAttacking() || _combat.IsDodging()) return;

            // Evaluation interval
            if (Time.time - _lastEvaluationTime < evaluationInterval) return;
            _lastEvaluationTime = Time.time;

            // Cooldown check
            if (Time.time - _lastSwapTime < swapCooldown) return;

            // WEAPON COMMITMENT: If committed to current weapon, skip evaluation
            // unless an emergency condition is met
            if (_isCommittedToWeapon && Time.time < _weaponCommitmentExpiry)
            {
                if (!IsEmergencySwapNeeded())
                    return;
                    
                if (VerboseLogging)
                    Debug.Log($"[WeaponSwapManager] {_companion?.companionName} breaking weapon commitment due to emergency");
            }
            else if (_isCommittedToWeapon)
            {
                // Commitment expired
                _isCommittedToWeapon = false;
            }

            // NEW: Evaluate staff swapping first (for mages/healers with multiple staves)
            EvaluateStaffSwap();
            
            // Then evaluate melee/ranged swapping
            EvaluateWeaponSwap();
        }
        
        /// <summary>
        /// Checks if an emergency condition exists that warrants breaking weapon commitment.
        /// Emergency conditions:
        /// - Health is critically low and current weapon type isn't helping (e.g. melee but can't reach)
        /// - Target is completely unreachable with current weapon
        /// - Companion is unarmed (weapon was somehow lost)
        /// </summary>
        private bool IsEmergencySwapNeeded()
        {
            // Unarmed is always an emergency
            if (_combat != null && _combat.GetWeaponType() == CompanionCombat.WeaponType.Unarmed)
                return true;
            
            // Critical health + melee + target far away = need ranged to kite
            if (_character != null && _character.GetHealthPercentage() < emergencyHealthThreshold)
            {
                var target = _companionAI?.GetTargetCreature();
                if (target != null && !target.IsDead())
                {
                    float dist = Vector3.Distance(transform.position, target.transform.position);
                    bool currentlyRanged = _combat?.IsRangedWeapon() ?? false;
                    
                    // Low health + melee + target far = emergency swap to ranged
                    if (!currentlyRanged && dist > meleePreferenceDistance * 1.5f)
                        return true;
                }
            }
            
            // Target unreachable with current weapon type
            var currentTarget = _companionAI?.GetTargetCreature();
            if (currentTarget != null && !currentTarget.IsDead() && IsTargetUnreachable(currentTarget))
            {
                bool currentlyRanged = _combat?.IsRangedWeapon() ?? false;
                if (!currentlyRanged)
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Commits to the current weapon for the configured duration.
        /// Called after any successful weapon swap or initial weapon equip.
        /// </summary>
        private void CommitToCurrentWeapon()
        {
            _isCommittedToWeapon = true;
            _weaponCommitmentExpiry = Time.time + weaponCommitmentDuration;
            
            if (VerboseLogging)
                Debug.Log($"[WeaponSwapManager] {_companion?.companionName} committed to current weapon for {weaponCommitmentDuration}s");
        }
        
        /// <summary>
        /// Evaluates whether the companion should swap between support and offensive staves.
        /// Healers/Mages with both types will intelligently switch based on:
        /// - Party needs buffing/healing ? Use support staff
        /// - Everyone buffed and enemies to fight ? Use offensive staff
        /// </summary>
        private void EvaluateStaffSwap()
        {
            // Need both staff types to swap between them
            if (_supportStaff == null || _offensiveStaff == null) return;
            
            // Check staff swap cooldown
            if (Time.time - _lastStaffSwapTime < STAFF_SWAP_COOLDOWN) return;
            
            // Get current weapon
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand == null) return;
            
            bool currentlyUsingSupportStaff = Archetypes.ArchetypeUtils.IsSupportStaff(leftHand);
            
            // Check if party needs buffs (periodic check)
            bool partyNeedsBuffs = false;
            if (Time.time - _lastPartyBuffCheck >= PARTY_BUFF_CHECK_INTERVAL)
            {
                _lastPartyBuffCheck = Time.time;
                partyNeedsBuffs = CheckIfPartyNeedsBuffs();
            }
            
            // Check for active enemies
            bool hasActiveEnemies = HasActiveEnemiesNearby();
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Staff eval - CurrentlySupport: {currentlyUsingSupportStaff}, " +
                    $"PartyNeedsBuffs: {partyNeedsBuffs}, HasEnemies: {hasActiveEnemies}");
            }
            
            // Decision logic:
            // 1. If party needs buffs and we're using offensive ? swap to support
            // 2. If party is buffed and there are enemies and we're using support ? swap to offensive
            // 3. If no enemies at all, don't swap (stay in current state)
            
            if (partyNeedsBuffs && !currentlyUsingSupportStaff)
            {
                // Party needs buffs - swap to support staff
                ExecuteStaffSwap(_supportStaff, isSupport: true);
            }
            else if (!partyNeedsBuffs && hasActiveEnemies && currentlyUsingSupportStaff)
            {
                // Party is buffed and enemies present - swap to offensive staff
                ExecuteStaffSwap(_offensiveStaff, isSupport: false);
            }
        }
        
        /// <summary>
        /// Checks if any party member is missing buffs that the support staff provides.
        /// </summary>
        private bool CheckIfPartyNeedsBuffs()
        {
            if (_companion == null) return false;
            
            long ownerId = _companion.ownerPlayerId;
            Vector3 myPos = _companion.transform.position;
            float checkRange = 20f;
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(myPos, owner.transform.position);
                if (dist <= checkRange && !HasAnyShieldBuff(owner))
                {
                    return true;
                }
            }
            
            // Check other party companions
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsPlayer()) continue;
                
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null && otherCompanion.ownerPlayerId == ownerId)
                {
                    float dist = Vector3.Distance(myPos, character.transform.position);
                    if (dist <= checkRange && !HasAnyShieldBuff(character))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a character has any shield/protection buff active.
        /// </summary>
        private bool HasAnyShieldBuff(Character character)
        {
            if (character == null) return true;
            
            var seman = character.GetSEMan();
            if (seman == null) return true;
            
            // Check for common shield effect names
            string[] shieldEffects = { "SE_Shield", "Shield", "SE_StaffShield", "StaffShield" };
            foreach (var effectName in shieldEffects)
            {
                int hash = effectName.GetStableHashCode();
                if (seman.HaveStatusEffect(hash))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if there are active enemies nearby that we should be fighting.
        /// </summary>
        private bool HasActiveEnemiesNearby()
        {
            var target = _companionAI?.GetTargetCreature();
            if (target != null && !target.IsDead())
            {
                return true;
            }
            
            // Check for nearby enemies even without a target
            Vector3 myPos = _companion.transform.position;
            float checkRange = 25f;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (_character != null && !BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(myPos, character.transform.position);
                if (dist <= checkRange)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Executes a staff swap from current to the specified staff.
        /// </summary>
        private void ExecuteStaffSwap(CachedWeaponInfo targetStaff, bool isSupport)
        {
            if (targetStaff == null) return;
            
            Debug.Log($"[WeaponSwapManager] {_companion?.companionName} swapping to {(isSupport ? "SUPPORT" : "OFFENSIVE")} staff: {targetStaff.PrefabName}");
            
            // Move current staff to back/storage
            var currentLeftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (currentLeftHand != null)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                
                // Try to put in back slot first
                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (leftBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, currentLeftHand);
                }
                else
                {
                    // Back slot occupied - try right back
                    var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                    if (rightBack == null)
                    {
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, currentLeftHand);
                    }
                    else
                    {
                        // Both back slots full - put in storage
                        _inventory.GetStorageInventory()?.AddItem(currentLeftHand);
                    }
                }
            }
            
            // Equip the target staff
            MoveWeaponToHand(targetStaff);
            
            _lastStaffSwapTime = Time.time;
            _lastSwapTime = Time.time;
            
            // Refresh
            _lastScanTime = -100f;
            ScanAvailableWeapons();
            FinalizeWeaponSwap();
            
            // Commit to the new staff
            CommitToCurrentWeapon();
        }

      #endregion

 #region Weapon Scanning

        /// <summary>
        /// Scans inventory for available melee and ranged weapons.
        /// Now also scans for support/offensive staves and storage inventory.
        /// Throttled to avoid excessive scanning - will only re-scan after SCAN_COOLDOWN seconds.
        /// </summary>
        public void ScanAvailableWeapons()
        {
            // Throttle scans to prevent excessive inventory iteration
            if (Time.time - _lastScanTime < SCAN_COOLDOWN && (_meleeWeapon != null || _rangedWeapon != null))
                return;
            _lastScanTime = Time.time;
            
            _meleeWeapon = null;
            _rangedWeapon = null;
            _supportStaff = null;
            _offensiveStaff = null;

            // Check equipped slots
            ScanSlot(CompanionInventory.EquipmentSlot.RightHand, CompanionInventory.EquipmentSlot.RightBack);
            ScanSlot(CompanionInventory.EquipmentSlot.LeftHand, CompanionInventory.EquipmentSlot.LeftBack);
            ScanSlot(CompanionInventory.EquipmentSlot.RightBack, CompanionInventory.EquipmentSlot.RightHand);
            ScanSlot(CompanionInventory.EquipmentSlot.LeftBack, CompanionInventory.EquipmentSlot.LeftHand);

            // NEW: Also scan storage inventory for weapons
            ScanStorageInventory();

            // Determine current weapon slot
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);

            if (leftHand != null && IsRangedItem(leftHand))
            {
                _currentWeaponSlot = WeaponSlot.LeftHand;
            }
            else if (rightHand != null && rightHand.IsWeapon())
            {
                _currentWeaponSlot = WeaponSlot.RightHand;
            }
            else
            {
                _currentWeaponSlot = WeaponSlot.None;
            }

            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Scanned weapons - Melee: {_meleeWeapon?.PrefabName ?? "None"}, " +
                    $"Ranged: {_rangedWeapon?.PrefabName ?? "None"}, " +
                    $"SupportStaff: {_supportStaff?.PrefabName ?? "None"}, " +
                    $"OffensiveStaff: {_offensiveStaff?.PrefabName ?? "None"}, " +
                    $"Current: {_currentWeaponSlot}");
            }
        }

        /// <summary>
        /// Scans the storage inventory for weapons that aren't currently equipped.
        /// Also specifically tracks support and offensive staves for mage/healer builds.
        /// </summary>
        private void ScanStorageInventory()
        {
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null) return;
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (!item.IsWeapon()) continue;
                
                // Skip shields - they're not weapons for swapping purposes
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield) continue;
                
                // Skip tools (pickaxes, axes) - they're for gathering, not combat
                if (IsGatheringTool(item)) continue;
                
                // Check for staves first (before generic ranged check)
                if (IsStaff(item))
                {
                    if (Archetypes.ArchetypeUtils.IsSupportStaff(item))
                    {
                        if (_supportStaff == null)
                        {
                            _supportStaff = CreateWeaponInfo(item, CompanionInventory.EquipmentSlot.LeftHand, 
                                CompanionInventory.EquipmentSlot.LeftBack, true, true);
                            _supportStaff.IsInStorage = true;
                            _supportStaff.StorageItem = item;
                            
                            if (VerboseLogging)
                                Debug.Log($"[WeaponSwapManager] Found SUPPORT staff in storage: {_supportStaff.PrefabName}");
                        }
                    }
                    else // Offensive staff
                    {
                        if (_offensiveStaff == null)
                        {
                            _offensiveStaff = CreateWeaponInfo(item, CompanionInventory.EquipmentSlot.LeftHand, 
                                CompanionInventory.EquipmentSlot.LeftBack, true, true);
                            _offensiveStaff.IsInStorage = true;
                            _offensiveStaff.StorageItem = item;
                            
                            if (VerboseLogging)
                                Debug.Log($"[WeaponSwapManager] Found OFFENSIVE staff in storage: {_offensiveStaff.PrefabName}");
                        }
                        
                        // Offensive staves also count as ranged for general swap logic
                        if (_rangedWeapon == null)
                        {
                            _rangedWeapon = _offensiveStaff;
                        }
                    }
                    continue;
                }
                
                bool isRanged = IsRangedItem(item);
                
                // Only record if we don't already have this type from equipped slots
                if (isRanged && _rangedWeapon == null)
                {
                    _rangedWeapon = new CachedWeaponInfo
                    {
                        PrefabName = GetItemPrefabName(item),
                        Quality = item.m_quality,
                        Slot = CompanionInventory.EquipmentSlot.LeftHand, // Target slot for ranged
                        BackSlot = CompanionInventory.EquipmentSlot.LeftBack,
                        IsRanged = true,
                        Range = item.m_shared.m_aiAttackRange > 0 ? item.m_shared.m_aiAttackRange : 25f,
                        ItemType = item.m_shared.m_itemType,
                        IsInStorage = true,
                        StorageItem = item
                    };
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Found ranged weapon in storage: {_rangedWeapon.PrefabName}");
                    }
                }
                else if (!isRanged && _meleeWeapon == null)
                {
                    _meleeWeapon = new CachedWeaponInfo
                    {
                        PrefabName = GetItemPrefabName(item),
                        Quality = item.m_quality,
                        Slot = CompanionInventory.EquipmentSlot.RightHand, // Target slot for melee
                        BackSlot = CompanionInventory.EquipmentSlot.RightBack,
                        IsRanged = false,
                        Range = item.m_shared.m_attack?.m_attackRange ?? 2.5f,
                        ItemType = item.m_shared.m_itemType,
                        IsInStorage = true,
                        StorageItem = item
                    };
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Found melee weapon in storage: {_meleeWeapon.PrefabName}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if an item is a staff (elemental or blood magic).
        /// </summary>
        private bool IsStaff(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            var skill = item.m_shared.m_skillType;
            if (skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic)
                return true;
            
            // Also check prefab name
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            return prefabName.Contains("staff");
        }
        
        /// <summary>
        /// Creates a CachedWeaponInfo from an item.
        /// </summary>
        private CachedWeaponInfo CreateWeaponInfo(ItemDrop.ItemData item, CompanionInventory.EquipmentSlot slot, 
            CompanionInventory.EquipmentSlot backSlot, bool isRanged, bool isStaff)
        {
            return new CachedWeaponInfo
            {
                PrefabName = GetItemPrefabName(item),
                Quality = item.m_quality,
                Slot = slot,
                BackSlot = backSlot,
                IsRanged = isRanged,
                Range = isRanged ? (item.m_shared.m_aiAttackRange > 0 ? item.m_shared.m_aiAttackRange : 18f) : 
                    (item.m_shared.m_attack?.m_attackRange ?? 2.5f),
                ItemType = item.m_shared.m_itemType,
                IsInStorage = false,
                StorageItem = null
            };
        }

        private void ScanSlot(CompanionInventory.EquipmentSlot slot, CompanionInventory.EquipmentSlot backSlot)
        {
            var item = _inventory.GetEquippedItem(slot);
            if (item?.m_shared == null) return;
            if (!item.IsWeapon()) return;
            
            // Skip shields
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield) return;
            
            // Skip tools (pickaxes, axes) - they're for gathering, not combat
            if (IsGatheringTool(item)) return;

            // Check for staves first - categorize as support vs offensive
            if (IsStaff(item))
            {
                bool isSupport = Archetypes.ArchetypeUtils.IsSupportStaff(item);
                
                var staffInfo = new CachedWeaponInfo
                {
                    PrefabName = GetItemPrefabName(item),
                    Quality = item.m_quality,
                    Slot = slot,
                    BackSlot = backSlot,
                    IsRanged = true,
                    Range = item.m_shared.m_aiAttackRange > 0 ? item.m_shared.m_aiAttackRange : 18f,
                    ItemType = item.m_shared.m_itemType,
                    IsInStorage = false,
                    StorageItem = null
                };
                
                if (isSupport)
                {
                    if (_supportStaff == null)
                    {
                        _supportStaff = staffInfo;
                        if (VerboseLogging)
                            Debug.Log($"[WeaponSwapManager] Found SUPPORT staff in slot {slot}: {staffInfo.PrefabName}");
                    }
                }
                else
                {
                    if (_offensiveStaff == null)
                    {
                        _offensiveStaff = staffInfo;
                        if (VerboseLogging)
                            Debug.Log($"[WeaponSwapManager] Found OFFENSIVE staff in slot {slot}: {staffInfo.PrefabName}");
                    }
                    
                    // Offensive staves also count as ranged for general swap logic
                    if (_rangedWeapon == null)
                    {
                        _rangedWeapon = staffInfo;
                    }
                }
                return;
            }

            bool isRanged = IsRangedItem(item);
            
            var info = new CachedWeaponInfo
            {
                PrefabName = GetItemPrefabName(item),
                Quality = item.m_quality,
                Slot = slot,
                BackSlot = backSlot,
                IsRanged = isRanged,
                Range = isRanged ? (item.m_shared.m_aiAttackRange > 0 ? item.m_shared.m_aiAttackRange : 25f) : (item.m_shared.m_attack?.m_attackRange ?? 2.5f),
                ItemType = item.m_shared.m_itemType,
                IsInStorage = false,
                StorageItem = null
            };

            if (isRanged && _rangedWeapon == null)
            {
                _rangedWeapon = info;
            }
            else if (!isRanged && _meleeWeapon == null)
            {
                _meleeWeapon = info;
            }
        }

        private bool IsRangedItem(ItemDrop.ItemData item)
    {
     if (item?.m_shared == null) return false;
    
       // Bows and crossbows are ranged
       if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
           return true;
       if (item.m_shared.m_skillType == Skills.SkillType.Bows ||
           item.m_shared.m_skillType == Skills.SkillType.Crossbows)
           return true;
       if (item.m_shared.m_attack?.m_bowDraw == true ||
           item.m_shared.m_attack?.m_requiresReload == true)
           return true;
       
       // Staves are ranged (magic weapons with projectiles)
       if (item.m_shared.m_skillType == Skills.SkillType.ElementalMagic ||
           item.m_shared.m_skillType == Skills.SkillType.BloodMagic)
           return true;
       
       // Check if the attack spawns a projectile (catches other magic weapons)
       if (item.m_shared.m_attack?.m_attackProjectile != null)
           return true;
       
       // Check name for staff indicators
       string itemName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
       if (itemName.Contains("staff") || itemName.Contains("wand"))
           return true;
       
       return false;
  }
  
        /// <summary>
        /// Checks if an item is a gathering tool (pickaxe, axe, etc.) that should NOT be used for combat.
        /// These items are intended for resource gathering, not fighting enemies.
        /// </summary>
        private bool IsGatheringTool(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            // Check if it's a Tool type item (pickaxes are Tools in Valheim)
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                return true;
            
            // Check skill type - Pickaxes and WoodCutting are gathering skills
            if (item.m_shared.m_skillType == Skills.SkillType.Pickaxes ||
                item.m_shared.m_skillType == Skills.SkillType.WoodCutting)
                return true;
            
            // Check name for common gathering tool names
            // Note: We want to EXCLUDE these from combat, but they're valid for gathering
            string itemName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            
            // Pickaxes are always gathering tools
            if (itemName.Contains("pickaxe") || prefabName.Contains("pickaxe"))
                return true;
            
            // Standalone "axe" items for chopping (not battleaxes which are combat weapons)
            // Check if it's a pure axe by looking at the name pattern
            // "axe_" prefix or "_axe" suffix without "battle" or "greataxe" indicates gathering axe
            if ((prefabName.StartsWith("axe") || prefabName.Contains("_axe")) && 
                !prefabName.Contains("battle") && !prefabName.Contains("greataxe") &&
                !prefabName.Contains("dualaxe") && !prefabName.Contains("jotunbane"))
            {
                // Further check: gathering axes have WoodCutting skill or are Tool type
                if (item.m_shared.m_skillType == Skills.SkillType.WoodCutting ||
                    item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                    return true;
            }
            
            return false;
        }

        private string GetItemPrefabName(ItemDrop.ItemData item)
   {
            if (item?.m_dropPrefab != null)
  {
      string name = item.m_dropPrefab.name;
      if (name.EndsWith("(Clone)"))
   name = name.Substring(0, name.Length - 7).Trim();
       return name;
      }
            return item?.m_shared?.m_name ?? "";
        }

        #endregion

        #region Swap Evaluation

        private void EvaluateWeaponSwap()
        {
            // Need both weapon types to swap
            if (_meleeWeapon == null || _rangedWeapon == null) return;

            var target = _companionAI?.GetTargetCreature();
            if (target == null || target.IsDead()) return;

            bool currentlyRanged = _combat.IsRangedWeapon();
            float distToTarget = Vector3.Distance(transform.position, target.transform.position);
            
            // Get comprehensive tactical assessment
            var assessment = EvaluateTacticalSituation(target, distToTarget, currentlyRanged);
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Tactical Assessment - " +
                    $"Ranged Score: {assessment.rangedScore:F2}, Melee Score: {assessment.meleeScore:F2}, " +
                    $"Currently: {(currentlyRanged ? "Ranged" : "Melee")}, Recommendation: {assessment.recommendation}");
            }

            // Execute swap based on recommendation
            if (assessment.recommendation == WeaponRecommendation.Ranged && !currentlyRanged)
            {
                if (assessment.rangedScore >= swapConfidenceThreshold)
                {
                    ExecuteSwapToRanged();
                }
            }
            else if (assessment.recommendation == WeaponRecommendation.Melee && currentlyRanged)
            {
                if (assessment.meleeScore >= swapConfidenceThreshold)
                {
                    ExecuteSwapToMelee();
                }
            }
        }
        
        public enum WeaponRecommendation
        {
            KeepCurrent,
            Melee,
            Ranged
        }
        
        /// <summary>
        /// Comprehensive tactical situation assessment for weapon choice.
        /// Considers: distance, enemy count, enemy strength, terrain, stamina, health, and more.
        /// </summary>
        public (float rangedScore, float meleeScore, WeaponRecommendation recommendation) EvaluateTacticalSituation(
            Character target, float distToTarget, bool currentlyRanged)
        {
            float rangedScore = 0f;
            float meleeScore = 0f;
            
            // ==========================================
            // FACTOR 1: DISTANCE TO TARGET
            // The most important factor - further = ranged, closer = melee
            // ==========================================
            
            // Very close (0-3m) - heavily favor melee
            if (distToTarget <= 3f)
            {
                meleeScore += 0.6f;
            }
            // Close range (3-6m) - favor melee but ranged still viable
            else if (distToTarget <= meleePreferenceDistance)
            {
                meleeScore += 0.4f;
                rangedScore += 0.1f;
            }
            // Medium range (6-12m) - slight ranged preference
            else if (distToTarget <= rangedPreferenceDistance)
            {
                float t = Mathf.InverseLerp(meleePreferenceDistance, rangedPreferenceDistance, distToTarget);
                meleeScore += 0.3f * (1f - t);
                rangedScore += 0.3f * t;
            }
            // Long range (12m+) - heavily favor ranged
            else
            {
                rangedScore += 0.5f;
                // Even more if very far
                if (distToTarget > rangedPreferenceDistance * 1.5f)
                {
                    rangedScore += 0.2f;
                }
            }
            
            // ==========================================
            // FACTOR 2: ENEMY COUNT AND POSITIONING
            // Multiple close enemies = melee (can hit many), spread out = ranged
            // ==========================================
            
            int veryCloseEnemies = CountEnemiesInRange(0f, 4f);
            int closeEnemies = CountEnemiesInRange(0f, meleePreferenceDistance);
            int mediumRangeEnemies = CountEnemiesInRange(meleePreferenceDistance, rangedPreferenceDistance);
            int farEnemies = CountEnemiesInRange(rangedPreferenceDistance, rangedPreferenceDistance * 2f);
            
            // Multiple enemies in melee range - melee is efficient (cleave/AOE)
            if (veryCloseEnemies >= 2)
            {
                meleeScore += 0.4f;
            }
            else if (closeEnemies >= 2)
            {
                meleeScore += 0.25f;
            }
            
            // Enemies spread out at range - ranged can pick them off
            if (farEnemies >= 2 && closeEnemies <= 1)
            {
                rangedScore += 0.3f;
            }
            
            // Surrounded by many enemies - melee to fight through
            if (closeEnemies >= 3)
            {
                meleeScore += 0.3f;
            }
            
            // ==========================================
            // FACTOR 3: ENEMY STRENGTH/TYPE
            // Tough enemies at range = soften with ranged first
            // Weak enemies = melee cleave is efficient
            // ==========================================
            
            var threatAnalyzer = GetComponent<ThreatAnalyzer>();
            if (threatAnalyzer != null)
            {
                var profile = threatAnalyzer.GetThreatProfile(target);
                
                // Boss or elite at distance - use ranged to chip away
                if ((profile.Classification == ThreatAnalyzer.EnemyClass.Boss || 
                     profile.Classification == ThreatAnalyzer.EnemyClass.Elite) &&
                    distToTarget > meleePreferenceDistance)
                {
                    rangedScore += 0.3f;
                }
                
                // Dangerous enemy approaching - get shots off before they arrive
                if (profile.Classification >= ThreatAnalyzer.EnemyClass.Dangerous &&
                    IsTargetApproaching(target) && distToTarget > meleePreferenceDistance)
                {
                    rangedScore += 0.25f;
                }
                
                // Trivial enemies close by - melee is faster
                if (profile.Classification == ThreatAnalyzer.EnemyClass.Trivial && closeEnemies >= 1)
                {
                    meleeScore += 0.2f;
                }
                
                // Low health enemy far away - finish with ranged
                if (profile.HealthPercent < 0.3f && distToTarget > meleePreferenceDistance)
                {
                    rangedScore += 0.2f;
                }
            }
            
            // ==========================================
            // FACTOR 4: TERRAIN AND REACHABILITY
            // Can't reach = ranged, clear path = can use either
            // ==========================================
            
            // Elevation difference
            float heightDiff = Mathf.Abs(target.transform.position.y - transform.position.y);
            if (heightDiff > elevationSwapThreshold)
            {
                rangedScore += 0.5f;
                meleeScore -= 0.3f; // Penalize melee when we can't reach
            }
            
            // Target unreachable (tried to path but failed)
            if (IsTargetUnreachable(target))
            {
                rangedScore += 0.6f;
                meleeScore -= 0.4f;
            }
            
            // Water between us (swimming enemies or we're in water)
            if (_character != null && _character.IsSwimming() && distToTarget > 3f)
            {
                rangedScore += 0.3f; // Hard to melee while swimming
            }
            
            // ==========================================
            // FACTOR 5: ENEMY BEHAVIOR
            // Fleeing = ranged, approaching = prepare for melee
            // ==========================================
            
            if (IsTargetFleeing(target))
            {
                if (distToTarget > meleePreferenceDistance)
                {
                    rangedScore += 0.4f; // Can't catch them, shoot them
                }
            }
            
            if (IsTargetApproaching(target))
            {
                if (distToTarget > rangedPreferenceDistance)
                {
                    // Still far - get shots off while they approach
                    rangedScore += 0.3f;
                }
                else if (distToTarget <= meleePreferenceDistance * 1.5f)
                {
                    // Close enough - prepare for melee
                    meleeScore += 0.2f;
                }
            }
            
            // ==========================================
            // FACTOR 6: COMPANION STATE (Health/Stamina)
            // Low resources = prefer ranged (safer distance)
            // ==========================================
            
            var staminaManager = GetComponent<StaminaManager>();
            if (staminaManager != null)
            {
                float staminaPercent = staminaManager.GetStaminaPercent();
                
                // Low stamina - ranged uses less stamina per attack
                if (staminaPercent < 0.3f)
                {
                    rangedScore += 0.2f;
                }
                
                // In recovery - definitely prefer ranged (kiting)
                if (staminaManager.IsInCriticalRecovery())
                {
                    rangedScore += 0.5f;
                    meleeScore -= 0.3f;
                }
            }
            
            if (_character != null)
            {
                float healthPercent = _character.GetHealthPercentage();
                
                // Low health - prefer ranged for safety
                if (healthPercent < 0.3f)
                {
                    rangedScore += 0.3f;
                }
            }
            
            // ==========================================
            // FACTOR 7: COMBAT ENTRY (Opening shots)
            // Just entered combat at range = get shots off first
            // ==========================================
            
            // If we're far and combat just started, ranged opening is smart
            if (distToTarget > rangedPreferenceDistance && !currentlyRanged)
            {
                // Haven't been attacking much - this is probably combat entry
                var combatMovement = GetComponent<CompanionCombatMovement>();
                if (combatMovement != null && !combatMovement.IsInCombat)
                {
                    rangedScore += 0.3f;
                }
            }
            
            // ==========================================
            // FACTOR 8: WEAPON SWAP HYSTERESIS
            // Add slight bonus to current weapon to prevent constant swapping
            // ==========================================
            
            if (currentlyRanged)
            {
                rangedScore += 0.15f;
            }
            else
            {
                meleeScore += 0.15f;
            }
            
            // ==========================================
            // DETERMINE RECOMMENDATION
            // ==========================================
            
            rangedScore = Mathf.Clamp01(rangedScore);
            meleeScore = Mathf.Clamp01(meleeScore);
            
            WeaponRecommendation recommendation;
            
            // Need clear advantage to recommend swap
            float scoreDiff = Mathf.Abs(rangedScore - meleeScore);
            
            if (scoreDiff < 0.15f)
            {
                // Scores are close - keep current weapon
                recommendation = WeaponRecommendation.KeepCurrent;
            }
            else if (rangedScore > meleeScore)
            {
                recommendation = WeaponRecommendation.Ranged;
            }
            else
            {
                recommendation = WeaponRecommendation.Melee;
            }
            
            return (rangedScore, meleeScore, recommendation);
        }
        
        /// <summary>
        /// Evaluates what weapon to start combat with based on initial conditions.
        /// Called when first entering combat to pick the optimal opening weapon.
        /// </summary>
        public WeaponRecommendation EvaluateCombatOpeningWeapon(Character target, float distToTarget)
        {
            if (_meleeWeapon == null && _rangedWeapon == null) return WeaponRecommendation.KeepCurrent;
            if (_meleeWeapon == null) return WeaponRecommendation.Ranged;
            if (_rangedWeapon == null) return WeaponRecommendation.Melee;
            
            // Get full tactical assessment
            bool currentlyRanged = _combat?.IsRangedWeapon() ?? false;
            var (rangedScore, meleeScore, _) = EvaluateTacticalSituation(target, distToTarget, currentlyRanged);
            
            // For opening, we don't add hysteresis - pure tactical choice
            // Remove the hysteresis bonus we added
            if (currentlyRanged) rangedScore -= 0.15f;
            else meleeScore -= 0.15f;
            
            // Additional opening-specific logic:
            
            // Far away = definitely open with ranged to get damage in while closing
            if (distToTarget > rangedPreferenceDistance)
            {
                rangedScore += 0.3f;
            }
            
            // Medium distance with approaching enemy = ranged opening
            if (distToTarget > meleePreferenceDistance && IsTargetApproaching(target))
            {
                rangedScore += 0.2f;
            }
            
            // Very close = melee immediately
            if (distToTarget <= 4f)
            {
                meleeScore += 0.3f;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Combat Opening Eval - Dist: {distToTarget:F1}m, " +
                    $"Ranged: {rangedScore:F2}, Melee: {meleeScore:F2}");
            }
            
            return rangedScore > meleeScore ? WeaponRecommendation.Ranged : WeaponRecommendation.Melee;
        }

    private bool IsTargetUnreachable(Character target)
        {
     if (_movement == null) return false;

        // Check if we've been trying to reach target without success
     Vector3 targetPos = target.transform.position;
          float distMoved = Vector3.Distance(transform.position, _lastTargetPosition);
      float distToTarget = Vector3.Distance(transform.position, targetPos);

      if (!_hasTriedToReachTarget)
    {
           _lastTargetPosition = transform.position;
         _targetUnreachableStartTime = Time.time;
   _hasTriedToReachTarget = true;
          return false;
            }

        // If we haven't moved much but target is still far
if (Time.time - _targetUnreachableStartTime > unreachableCheckTime)
{
             if (distMoved < 2f && distToTarget > meleePreferenceDistance)
     {
      return true;
        }
             
       // Reset for next check
                _lastTargetPosition = transform.position;
  _targetUnreachableStartTime = Time.time;
     }

          return false;
        }

        private bool IsTargetFleeing(Character target)
        {
     if (target == null) return false;
        
       Vector3 velocity = target.GetVelocity();
            if (velocity.magnitude < 1f) return false;

            Vector3 toUs = (transform.position - target.transform.position).normalized;
         float dot = Vector3.Dot(velocity.normalized, toUs);
            
   return dot < -0.5f; // Moving away from us
        }

        private bool IsTargetApproaching(Character target)
        {
        if (target == null) return false;
    
  Vector3 velocity = target.GetVelocity();
            if (velocity.magnitude < 0.5f) return false;

       Vector3 toUs = (transform.position - target.transform.position).normalized;
          float dot = Vector3.Dot(velocity.normalized, toUs);
      
          return dot > 0.5f; // Moving toward us
        }

 private int CountEnemiesInRange(float minRange, float maxRange)
      {
         int count = 0;
            foreach (var character in Character.GetAllCharacters())
          {
      if (character == null || character.IsDead()) continue;
    if (character == _character) continue;
        if (character.IsTamed() || character.IsPlayer()) continue;
     if (!BaseAI.IsEnemy(_character, character)) continue;

         float dist = Vector3.Distance(transform.position, character.transform.position);
    if (dist >= minRange && dist <= maxRange)
 {
       count++;
     }
            }
            return count;
        }

        #endregion

        #region Swap Execution

        private void ExecuteSwapToRanged()
        {
            if (_rangedWeapon == null) return;

            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] {_companion?.companionName} swapping to ranged: {_rangedWeapon.PrefabName}");
            }

            // CRITICAL: Must holster right-hand weapon BEFORE equipping bow
            // Bows go in left hand and cannot coexist with right-hand weapons
            if (_meleeWeapon != null && IsWeaponInHand(_meleeWeapon))
            {
                MoveWeaponToBack(_meleeWeapon);
            }
            
            // Also clear any weapon in right hand that wasn't tracked
            var rightHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && rightHand.IsWeapon())
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, rightHand);
                }
                else
                {
                    _inventory.GetStorageInventory()?.AddItem(rightHand);
                }
            }

            // Move ranged to hand
            MoveWeaponToHand(_rangedWeapon);

            _lastSwapTime = Time.time;
            _hasTriedToReachTarget = false;
            
            // Force re-scan on next request (clear throttle)
            _lastScanTime = -100f;
            ScanAvailableWeapons();
            
            // CRITICAL: Force complete visual and network sync
            FinalizeWeaponSwap();
            
            // Commit to the new weapon
            CommitToCurrentWeapon();
        }

        private void ExecuteSwapToMelee()
        {
            if (_meleeWeapon == null) return;

            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] {_companion?.companionName} swapping to melee: {_meleeWeapon.PrefabName}");
            }

            // CRITICAL: Must holster bow BEFORE equipping melee weapon
            // Bows are in left hand and cannot coexist with right-hand weapons
            if (_rangedWeapon != null && IsWeaponInHand(_rangedWeapon))
            {
                MoveWeaponToBack(_rangedWeapon);
            }
            
            // Also clear any bow/ranged in left hand that wasn't tracked
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && (leftHand.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow || IsRangedItem(leftHand)))
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (leftBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHand);
                }
                else
                {
                    _inventory.GetStorageInventory()?.AddItem(leftHand);
                }
            }

            // Move melee to hand
            MoveWeaponToHand(_meleeWeapon);

            _lastSwapTime = Time.time;
            
            // Force re-scan on next request (clear throttle)
            _lastScanTime = -100f;
            ScanAvailableWeapons();
            
            // CRITICAL: Force complete visual and network sync
            FinalizeWeaponSwap();
            
            // Commit to the new weapon
            CommitToCurrentWeapon();
        }
        
        /// <summary>
        /// Finalizes a weapon swap by refreshing visuals and syncing over network.
        /// This ensures other players see the weapon change.
        /// </summary>
        private void FinalizeWeaponSwap()
        {
            // Apply visual equipment (updates NpcVisEquipment)
            _inventory?.ApplyVisualEquipment();
            
            // Save to ZDO (saves equipment prefab names for our custom system)
            _inventory?.SaveToZDO();
            
            // Notify equipment data to refresh
            var equipmentData = GetComponent<CompanionEquipmentData>();
            equipmentData?.RefreshAllEquipmentData();
            
            // CRITICAL: Force VisEquipment to update its ZDO hashes for multiplayer sync
            // VisEquipment stores its own hash values in ZDO which other clients read
            ForceVisEquipmentSync();
        }
        
        /// <summary>
        /// Forces VisEquipment to sync its hash values to ZDO for multiplayer.
        /// This ensures other clients see the weapon change.
        /// </summary>
        private void ForceVisEquipmentSync()
        {
            var npcVisEquip = GetComponent<NpcVisEquipment>();
            if (npcVisEquip == null) return;
            
            var visEquip = npcVisEquip.VisEquipment;
            if (visEquip == null) return;
            
            // Get the ZNetView used by VisEquipment
            var nview = visEquip.m_nViewOverride ?? GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            
            // Get current equipment data
            var equipment = _inventory?.GetEquipmentDataForVisuals();
            if (equipment == null) return;
            
            // CRITICAL: Force clear the current hash values via reflection
            // This makes VisEquipment think the equipment has changed and triggers visual recreation
            try
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var visType = typeof(VisEquipment);
                
                // Clear current hash values to force update
                string[] currentHashFields = {
                    "m_currentLeftItemHash",
                    "m_currentRightItemHash",
                    "m_currentLeftBackItemHash",
                    "m_currentRightBackItemHash"
                };
                
                foreach (var fieldName in currentHashFields)
                {
                    var field = visType.GetField(fieldName, flags);
                    if (field != null && field.FieldType == typeof(int))
                    {
                        field.SetValue(visEquip, -1); // Force mismatch
                    }
                }
            }
            catch (Exception ex)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[WeaponSwapManager] Failed to clear hash fields: {ex.Message}");
            }
            
            // Apply the equipment which will trigger the Set methods
            // The Set methods write hash values to ZDO internally
            npcVisEquip.ApplyEquipment(equipment);
            
            // Force visual update to ensure visuals are recreated
            npcVisEquip.ForceVisualUpdate();
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Forced VisEquipment sync for multiplayer");
            }
        }

        private bool IsWeaponInHand(CachedWeaponInfo weapon)
        {
            return weapon.Slot == CompanionInventory.EquipmentSlot.RightHand ||
                   weapon.Slot == CompanionInventory.EquipmentSlot.LeftHand;
        }

        private void MoveWeaponToBack(CachedWeaponInfo weapon)
        {
            if (weapon == null) return;
            if (weapon.IsInStorage) return; // Already in storage, nothing to do
            
            // Find the weapon in any hand slot
            ItemDrop.ItemData item = null;
            CompanionInventory.EquipmentSlot foundSlot = weapon.Slot;
            
            // Check both hand slots for this weapon
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            if (rightHand != null && GetItemPrefabName(rightHand) == weapon.PrefabName)
            {
                item = rightHand;
                foundSlot = CompanionInventory.EquipmentSlot.RightHand;
            }
            else if (leftHand != null && GetItemPrefabName(leftHand) == weapon.PrefabName)
            {
                item = leftHand;
                foundSlot = CompanionInventory.EquipmentSlot.LeftHand;
            }
            else
            {
                // Try the recorded slot
                item = _inventory.GetEquippedItem(weapon.Slot);
                if (item != null && GetItemPrefabName(item) != weapon.PrefabName)
                {
                    item = null;
                }
            }
            
            if (item == null)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[WeaponSwapManager] No item found to move to back for {weapon.PrefabName}");
                }
                return;
            }

            // CRITICAL: Clone the item before unequipping to prevent data loss
            // This ensures we have a valid reference even if unequip does something unexpected
            var itemClone = item.Clone();
            itemClone.m_dropPrefab = item.m_dropPrefab; // Ensure prefab reference is preserved
            
            // Unequip from hand
            _inventory.UnequipSlotSilent(foundSlot);
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Moving {weapon.PrefabName} from {foundSlot} to back");
            }

            // Determine the correct back slot based on weapon type
            var targetBackSlot = weapon.IsRanged 
                ? CompanionInventory.EquipmentSlot.LeftBack 
                : CompanionInventory.EquipmentSlot.RightBack;

            // Try to equip to back slot
            var existingBackItem = _inventory.GetEquippedItem(targetBackSlot);
            if (existingBackItem == null)
            {
                // Back slot is free, use it
                _inventory.EquipItemSilent(targetBackSlot, itemClone);
                weapon.Slot = targetBackSlot;
                weapon.IsInStorage = false;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[WeaponSwapManager] Moved {weapon.PrefabName} to back slot {targetBackSlot}");
                }
            }
            else
            {
                // Back slot is occupied - try the OTHER back slot first
                var alternateBackSlot = targetBackSlot == CompanionInventory.EquipmentSlot.RightBack 
                    ? CompanionInventory.EquipmentSlot.LeftBack 
                    : CompanionInventory.EquipmentSlot.RightBack;
                    
                var alternateBackItem = _inventory.GetEquippedItem(alternateBackSlot);
                if (alternateBackItem == null)
                {
                    // Use alternate back slot
                    _inventory.EquipItemSilent(alternateBackSlot, itemClone);
                    weapon.Slot = alternateBackSlot;
                    weapon.IsInStorage = false;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Moved {weapon.PrefabName} to alternate back slot {alternateBackSlot}");
                    }
                }
                else
                {
                    // Both back slots occupied, put in storage inventory
                    var storageInv = _inventory?.GetStorageInventory();
                    if (storageInv != null && storageInv.AddItem(itemClone))
                    {
                        weapon.IsInStorage = true;
                        weapon.StorageItem = itemClone;
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[WeaponSwapManager] Moved {weapon.PrefabName} to storage (back slots occupied)");
                        }
                    }
                    else
                    {
                        // CRITICAL: Couldn't store - re-equip to hand to prevent weapon loss
                        Debug.LogWarning($"[WeaponSwapManager] Couldn't store {weapon.PrefabName} - re-equipping to hand!");
                        _inventory.EquipItemSilent(foundSlot, itemClone);
                        return; // Don't refresh visuals since we didn't actually move anything
                    }
                }
            }
            
            // Recalculate bonuses and apply visual changes
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
        }

        private void MoveWeaponToHand(CachedWeaponInfo weapon)
        {
            if (weapon == null) return;
            
            ItemDrop.ItemData item = null;
            CompanionInventory.EquipmentSlot sourceSlot = CompanionInventory.EquipmentSlot.RightHand; // Track where we found it
            
            // Check if weapon is in storage inventory
            if (weapon.IsInStorage && weapon.StorageItem != null)
            {
                item = weapon.StorageItem;
                
                // Remove from storage
                var storageInv = _inventory?.GetStorageInventory();
                if (storageInv != null)
                {
                    storageInv.RemoveItem(item);
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Removed {weapon.PrefabName} from storage inventory");
                    }
                }
            }
            else
            {
                // Find where the weapon currently is in equipment slots
                // Check ALL slots to find and remove the weapon
                foreach (CompanionInventory.EquipmentSlot slot in Enum.GetValues(typeof(CompanionInventory.EquipmentSlot)))
                {
                    var checkItem = _inventory.GetEquippedItem(slot);
                    if (checkItem != null)
                    {
                        string foundPrefab = GetItemPrefabName(checkItem);
                        if (foundPrefab == weapon.PrefabName)
                        {
                            if (item == null)
                            {
                                // First instance found - this is our weapon
                                item = checkItem;
                                sourceSlot = slot;
                            }
                            
                            // ALWAYS unequip from this slot to clear duplicates
                            _inventory.UnequipSlotSilent(slot);
                            
                            if (VerboseLogging)
                            {
                                Debug.Log($"[WeaponSwapManager] Cleared {weapon.PrefabName} from slot {slot}");
                            }
                        }
                    }
                }
            }

            if (item == null)
            {
                Debug.LogWarning($"[WeaponSwapManager] Could not find weapon {weapon.PrefabName} to move to hand");
                return;
            }

            // Determine correct hand slot
            CompanionInventory.EquipmentSlot handSlot;
            if (weapon.IsRanged)
            {
                handSlot = CompanionInventory.EquipmentSlot.LeftHand;
            }
            else
            {
                handSlot = CompanionInventory.EquipmentSlot.RightHand;
            }
            
            // Check if hand slot is occupied by something else (different weapon)
            var existingHandItem = _inventory.GetEquippedItem(handSlot);
            if (existingHandItem != null && GetItemPrefabName(existingHandItem) != weapon.PrefabName)
            {
                // Move existing item to its back slot first
                var existingBackSlot = handSlot == CompanionInventory.EquipmentSlot.RightHand 
                    ? CompanionInventory.EquipmentSlot.RightBack 
                    : CompanionInventory.EquipmentSlot.LeftBack;
                    
                _inventory.UnequipSlotSilent(handSlot);
                
                var backOccupied = _inventory.GetEquippedItem(existingBackSlot);
                if (backOccupied == null)
                {
                    _inventory.EquipItemSilent(existingBackSlot, existingHandItem);
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Moved existing {existingHandItem.m_shared?.m_name} to {existingBackSlot}");
                    }
                }
                else
                {
                    // Put in storage
                    var storageInv = _inventory?.GetStorageInventory();
                    storageInv?.AddItem(existingHandItem);
                    if (VerboseLogging)
                    {
                        Debug.Log($"[WeaponSwapManager] Moved existing {existingHandItem.m_shared?.m_name} to storage");
                    }
                }
            }

            // Equip weapon to hand
            _inventory.EquipItemSilent(handSlot, item);
            weapon.Slot = handSlot;
            weapon.IsInStorage = false;
            weapon.StorageItem = null;
            
            if (VerboseLogging)
            {
                Debug.Log($"[WeaponSwapManager] Equipped {weapon.PrefabName} to {handSlot}");
            }
            
            // CRITICAL: Force complete visual refresh
            // This ensures the weapon is removed from back and shown in hand
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            // Also notify equipment data to refresh
            var equipmentData = GetComponent<CompanionEquipmentData>();
            if (equipmentData != null)
            {
                equipmentData.RefreshAllEquipmentData();
            }
        }

        #endregion

        #region Public API

        /// <summary>
      /// Forces a weapon swap evaluation and execution if appropriate.
        /// Clears commitment and cooldowns to allow immediate re-evaluation.
        /// </summary>
        public void ForceEvaluateSwap()
   {
     _lastEvaluationTime = 0f;
            _lastSwapTime = 0f;
            _isCommittedToWeapon = false;
            _lastScanTime = -100f;
 EvaluateWeaponSwap();
        }

        /// <summary>
  /// Forces swap to ranged weapon if available.
        /// Respects weapon commitment unless called with an emergency context.
        /// </summary>
        public bool ForceSwapToRanged()
        {
            // Refresh scan cache if stale
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
                
            if (_rangedWeapon == null) return false;
            
            // Only skip if we're ACTUALLY holding the ranged weapon
            // Don't skip just because IsRangedWeapon() is false (could be unarmed)
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && GetItemPrefabName(leftHand) == _rangedWeapon.PrefabName)
            {
                return true; // Already holding this ranged weapon
            }
            
            ExecuteSwapToRanged();
            return true;
        }

        /// <summary>
        /// Forces swap to melee weapon if available.
        /// Respects weapon commitment unless called with an emergency context.
        /// </summary>
        public bool ForceSwapToMelee()
        {
            // Refresh scan cache if stale
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
                
            if (_meleeWeapon == null) return false;
            
            // Only skip if we're ACTUALLY holding the melee weapon
            // Don't skip just because IsRangedWeapon() is false (could be unarmed)
            var rightHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && GetItemPrefabName(rightHand) == _meleeWeapon.PrefabName)
            {
                return true; // Already holding this melee weapon
            }
            
            ExecuteSwapToMelee();
            return true;
        }

        /// <summary>
        /// Returns true if companion has both melee and ranged weapons available.
        /// </summary>
        public bool CanSwapWeapons()
        {
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
            return _meleeWeapon != null && _rangedWeapon != null;
        }
        
        /// <summary>
        /// Returns true if companion has both support and offensive staves available.
        /// </summary>
        public bool CanSwapStaves()
        {
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
            return _supportStaff != null && _offensiveStaff != null;
        }
        
        /// <summary>
        /// Returns true if the companion is currently committed to a weapon and shouldn't swap.
        /// </summary>
        public bool IsCommittedToWeapon()
        {
            return _isCommittedToWeapon && Time.time < _weaponCommitmentExpiry;
        }
        
        /// <summary>
        /// Clears weapon commitment, allowing immediate re-evaluation.
        /// Use sparingly - only for player commands or state transitions.
        /// </summary>
        public void ClearCommitment()
        {
            _isCommittedToWeapon = false;
        }
        
        /// <summary>
        /// Forces a swap to the support staff if available.
        /// </summary>
        public bool ForceSwapToSupportStaff()
        {
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
            if (_supportStaff == null) return false;
            
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && GetItemPrefabName(leftHand) == _supportStaff.PrefabName)
            {
                return true; // Already holding support staff
            }
            
            ExecuteStaffSwap(_supportStaff, isSupport: true);
            return true;
        }
        
        /// <summary>
        /// Forces a swap to the offensive staff if available.
        /// </summary>
        public bool ForceSwapToOffensiveStaff()
        {
            if (Time.time - _lastScanTime >= SCAN_COOLDOWN)
                ScanAvailableWeapons();
            if (_offensiveStaff == null) return false;
            
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && GetItemPrefabName(leftHand) == _offensiveStaff.PrefabName)
            {
                return true; // Already holding offensive staff
            }
            
            ExecuteStaffSwap(_offensiveStaff, isSupport: false);
            return true;
        }

        /// <summary>
        /// Gets info about available weapons.
        /// </summary>
        public (CachedWeaponInfo melee, CachedWeaponInfo ranged) GetAvailableWeapons()
        {
            return (_meleeWeapon, _rangedWeapon);
        }
        
        /// <summary>
        /// Gets info about available staves.
        /// </summary>
        public (CachedWeaponInfo support, CachedWeaponInfo offensive) GetAvailableStaves()
        {
            return (_supportStaff, _offensiveStaff);
        }

        #endregion
    }
}
