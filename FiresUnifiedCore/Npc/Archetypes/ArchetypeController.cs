using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Component that manages archetype-specific behavior for a companion.
    /// Attached to each companion to handle their role in the group.
    /// 
    /// RESPONSIBILITIES:
    /// - Determines and tracks the companion's archetype (ArchetypeClass)
    /// - Applies archetype-specific stat modifiers (stamina, damage, etc.)
    /// - Handles archetype-specific abilities (taunt, berserk, backstab, etc.)
    /// - Tracks archetype statistics for UI display
    /// - Coordinates with combat/movement systems for role-appropriate behavior
    /// 
    /// STAT MODIFIERS:
    /// - Tank: +25% max stamina, +15% stamina regen, -30% block cost, parry restores stamina
    /// - Berserker: +25% damage when low health, +15% attack speed
    /// - Rogue: +50% backstab damage, +30% dodge distance
    /// - Ranger: +10% draw speed, +kiting behavior
    /// - Mage: +30% eitr, -15% eitr cost
    /// - Healer: +30% eitr regen, can heal allies
    /// - Paladin: Balanced tank/healer hybrid
    /// </summary>
    public class ArchetypeController : MonoBehaviour
    {
        [Header("Archetype State")]
        [SerializeField] private ArchetypeClass _currentArchetype = ArchetypeClass.None;
        [SerializeField] private ArchetypeClass _subArchetype = ArchetypeClass.None;
        [SerializeField] private ArchetypeDefinition _currentDefinition;
        [SerializeField] private ArchetypeDefinition _subDefinition;
        
        // Legacy support - maps to new system
        [SerializeField] private CompanionArchetypeType _legacyArchetype = CompanionArchetypeType.None;
        
        [Header("Tank Settings")]
        public float tauntCooldown = 40f; // 30s duration + 10s gap = 40s cooldown
        public float tauntDuration = 30f; // 30 seconds taunt effect
        public float tauntRange = 5f; // 5m radius for shockwave
        public float blockPriorityMultiplier = 2.0f;
        public float interceptionRange = 10f;
        public float parryStaminaRestoreBase = 5f;
        public float parryStaminaRestorePerLevel = 0.5f;
        public bool useTauntShockwave = true; // Use the new shockwave taunt system
        
        [Header("Berserker Settings")]
        public float berserkHealthThreshold = 0.35f;
        public float berserkDamageBonus = 0.5f;
        public float berserkSpeedBonus = 0.2f;
        
        [Header("Rogue Settings")]
        public float backstabAngle = 90f;
        public float backstabDamageMultiplier = 1.5f;
        public float backstabBonusPerLevel = 0.02f;
        
        [Header("Support Settings")]
        public float buffCheckInterval = 5f;
        public float healPriorityHealthPercent = 0.5f;
        public float preferredSupportRange = 12f;
        
        [Header("DPS Settings")]
        public float aggressionLevel = 1.0f;
        public float flankingPreference = 0.5f;
        public float targetSwitchHealthThreshold = 0.3f;
        
        // Components
        private CompanionController _companion;
        private CompanionCombat _combat;
        private CompanionCombatMovement _movement;
        private CompanionInventory _inventory;
        private CompanionStats _stats;
        private CompanionProgression _progression;
        private StaminaManager _staminaManager;
        private Character _character;
        
        // Statistics tracking
        private ArchetypeStatistics _statistics;
        private float _blockingStartTime;
        
        // Group combat directive ï¿½ set by GroupCombatCoordinator via CombatRoleDirector
        private CombatRoleDirector.RoleDirective _currentDirective;
        
        // Tank state
        private float _lastTauntTime = -100f;
        private bool _isBlocking = false;
        
        // Berserker state
        private bool _isBerserkActive = false;
        private float _lastBerserkCheck = 0f;
        
        // Events
        public event Action<ArchetypeClass> OnArchetypeAssigned;
        public event Action<CompanionArchetypeType> OnLegacyArchetypeAssigned; // Legacy support
        
        public static bool VerboseLogging = false;
        
        #region Properties
        
        /// <summary>Current assigned archetype class (primary).</summary>
        public ArchetypeClass CurrentArchetypeClass => _currentArchetype;
        
        /// <summary>Secondary archetype class based on alternate weapons in inventory.</summary>
        public ArchetypeClass SubArchetypeClass => _subArchetype;
        
        /// <summary>Current archetype definition with all bonuses.</summary>
        public ArchetypeDefinition CurrentDefinition => _currentDefinition;
        
        /// <summary>Sub-archetype definition (for hybrid bonuses).</summary>
        public ArchetypeDefinition SubDefinition => _subDefinition;
        
        /// <summary>Whether companion has a valid sub-archetype.</summary>
        public bool HasSubArchetype => _subArchetype != ArchetypeClass.None && _subArchetype != _currentArchetype;
        
        /// <summary>Legacy archetype type (for backwards compatibility).</summary>
        public CompanionArchetypeType CurrentArchetype => _legacyArchetype;
        
        /// <summary>Archetype statistics tracker.</summary>
        public ArchetypeStatistics Statistics => _statistics;
        
        /// <summary>True if this companion is the tank for their group.</summary>
        public bool IsTank => _currentArchetype == ArchetypeClass.Tank || _currentArchetype == ArchetypeClass.Paladin;
        
        /// <summary>True if this companion is a support/healer.</summary>
        public bool IsSupport => _currentArchetype == ArchetypeClass.Healer || _currentArchetype == ArchetypeClass.Paladin;
        
        /// <summary>True if this companion is a DPS (melee or ranged).</summary>
        public bool IsDPS => _currentArchetype == ArchetypeClass.Berserker || 
                            _currentArchetype == ArchetypeClass.Rogue ||
                            _currentArchetype == ArchetypeClass.Ranger ||
                            _currentArchetype == ArchetypeClass.Mage;
        
        /// <summary>True if this companion is a melee fighter.</summary>
        public bool IsMelee => _currentArchetype == ArchetypeClass.Tank ||
                              _currentArchetype == ArchetypeClass.Paladin ||
                              _currentArchetype == ArchetypeClass.Berserker ||
                              _currentArchetype == ArchetypeClass.Rogue;
        
        /// <summary>True if this companion is a ranged fighter.</summary>
        public bool IsRanged => _currentArchetype == ArchetypeClass.Ranger ||
                               _currentArchetype == ArchetypeClass.Mage ||
                               _currentArchetype == ArchetypeClass.Healer;
        
        /// <summary>True if berserk mode is currently active.</summary>
        public bool IsBerserkActive => _isBerserkActive;
        
        /// <summary>True if taunt is currently on cooldown.</summary>
        public bool IsTauntOnCooldown => Time.time - _lastTauntTime < tauntCooldown;
        
        /// <summary>Remaining cooldown time for taunt.</summary>
        public float TauntCooldownRemaining => Mathf.Max(0, tauntCooldown - (Time.time - _lastTauntTime));
        
        /// <summary>Gets the companion's level for scaling bonuses.</summary>
        private int CompanionLevel => _progression?.Level ?? 1;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combat = GetComponent<CompanionCombat>();
            _movement = GetComponent<CompanionCombatMovement>();
            _inventory = GetComponent<CompanionInventory>();
            _stats = GetComponent<CompanionStats>();
            _progression = GetComponent<CompanionProgression>();
            _staminaManager = GetComponent<StaminaManager>();
            _character = GetComponent<Character>();
            
            // Initialize statistics
            _statistics = new ArchetypeStatistics();
            
            // Initialize archetype registry
            ArchetypeRegistry.Initialize();
        }
        
        private void Start()
        {
            // Evaluate archetype on start (delayed to ensure inventory is loaded)
            Invoke(nameof(EvaluateArchetype), 0.5f);
            
            // Try to load saved statistics
            LoadStatistics();
        }
        
        private void Update()
        {
            if (_companion == null || !_companion.isTamed) return;
            if (_currentArchetype == ArchetypeClass.None) return;
            
            // Read latest directive from GroupCombatCoordinator (cheap ï¿½ just a dictionary lookup)
            var coordinator = GroupCombatCoordinator.Instance;
            if (coordinator != null)
            {
                var directive = coordinator.GetDirective(_companion);
                SetDirective(directive);
            }
            
            // Execute archetype-specific update logic
            switch (_currentArchetype)
            {
                case ArchetypeClass.Tank:
                case ArchetypeClass.Paladin:
                    UpdateTankBehavior();
                    break;
                case ArchetypeClass.Berserker:
                    UpdateBerserkerBehavior();
                    break;
                case ArchetypeClass.Rogue:
                    UpdateRogueBehavior();
                    break;
                case ArchetypeClass.Ranger:
                    UpdateRangerBehavior();
                    break;
                case ArchetypeClass.Mage:
                    UpdateMageBehavior();
                    break;
                case ArchetypeClass.Healer:
                    UpdateHealerBehavior();
                    break;
            }
            
            // Track blocking time for statistics
            UpdateBlockingTracking();
        }
        
        private void OnDestroy()
        {
            // Finalize statistics before destruction
            _statistics?.FinalizeTracking();
            
            // Notify the group manager that we're gone
            if (_companion != null)
            {
                GroupRoleManager.Instance.OnCompanionRemoved(_companion);
            }
        }
        
        #endregion
        
        #region Archetype Evaluation
        
        // Archetype persistence tracking
        private float _lastArchetypeChangeTime = 0f;
        private int _archetypeChangeCount = 0;
        private const float ARCHETYPE_CHANGE_COOLDOWN = 60f; // Don't change archetype more than once per minute
        private const int MAX_ARCHETYPE_CHANGES_PER_SESSION = 3; // Limit total changes
        
        /// <summary>
        /// Evaluates and assigns the appropriate archetype for this companion.
        /// Uses the expanded ArchetypeClass system.
        /// IMPORTANT: Archetypes are "sticky" - once assigned, they don't change unless:
        /// - Equipment fundamentally changes (e.g., shield removed)
        /// - Cooldown has passed AND equipment suggests a different role
        /// </summary>
        public void EvaluateArchetype()
        {
            if (_companion == null || _companion.ownerPlayerId == 0) return;
            
            // Evaluate best archetype based on CURRENTLY EQUIPPED items (not storage)
            var newArchetype = EvaluateBestArchetype();
            
            // ARCHETYPE STICKINESS: If we already have an archetype, only change if:
            // 1. New archetype is fundamentally different (equipment mismatch)
            // 2. Cooldown has passed
            // 3. We haven't changed too many times this session
            if (_currentArchetype != ArchetypeClass.None && newArchetype != _currentArchetype)
            {
                // Check if current archetype is still VALID (equipment supports it)
                bool currentStillValid = IsArchetypeStillValid(_currentArchetype);
                
                if (currentStillValid)
                {
                    // Current archetype is still valid - check if we should change
                    float timeSinceLastChange = Time.time - _lastArchetypeChangeTime;
                    
                    // Don't change if cooldown hasn't passed
                    if (timeSinceLastChange < ARCHETYPE_CHANGE_COOLDOWN)
                    {
                        if (VerboseLogging)
                        {
                            Debug.Log($"[Archetype] {_companion?.companionName} keeping {_currentArchetype} (cooldown: {ARCHETYPE_CHANGE_COOLDOWN - timeSinceLastChange:F0}s remaining)");
                        }
                        return;
                    }
                    
                    // Don't change if we've changed too many times
                    if (_archetypeChangeCount >= MAX_ARCHETYPE_CHANGES_PER_SESSION)
                    {
                        if (VerboseLogging)
                        {
                            Debug.Log($"[Archetype] {_companion?.companionName} keeping {_currentArchetype} (max changes reached: {_archetypeChangeCount})");
                        }
                        return;
                    }
                }
                // If current is no longer valid, force the change
            }
            
            // For tanks, check with group role manager (only one tank per group)
            if (newArchetype == ArchetypeClass.Tank)
            {
                var legacyAssigned = GroupRoleManager.Instance.AssignArchetype(_companion);
                if (legacyAssigned != CompanionArchetypeType.Tank)
                {
                    // Someone else is tank - fall back to paladin or berserker
                    newArchetype = HasShield() ? ArchetypeClass.Paladin : ArchetypeClass.Berserker;
                }
            }
            
            if (newArchetype != _currentArchetype)
            {
                _lastArchetypeChangeTime = Time.time;
                _archetypeChangeCount++;
                SetArchetype(newArchetype);
            }
        }
        
        /// <summary>
        /// Checks if the current archetype is still valid based on equipped items.
        /// Returns false if equipment has fundamentally changed (e.g., shield removed for tank).
        /// </summary>
        private bool IsArchetypeStillValid(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                case ArchetypeClass.Paladin:
                    // Tank/Paladin requires shield equipped
                    return HasShield() && HasMeleeWeapon();
                    
                case ArchetypeClass.Healer:
                    // Healer requires support staff equipped
                    return HasSupportStaffEquipped();
                    
                case ArchetypeClass.Mage:
                    // Mage requires offensive staff equipped
                    return HasOffensiveStaff();
                    
                case ArchetypeClass.Ranger:
                    // Ranger requires bow/crossbow equipped
                    return HasRangedWeapon();
                    
                case ArchetypeClass.Rogue:
                    // Rogue requires knives equipped
                    return HasKnives();
                    
                case ArchetypeClass.Berserker:
                    // Berserker needs melee weapon (any)
                    return HasMeleeWeapon() || HasTwoHandedWeapon();
                    
                case ArchetypeClass.Monk:
                    // Monk uses unarmed or clubs
                    return HasUnarmedOrClubs();
                    
                default:
                    return true;
            }
        }
        
        /// <summary>
        /// Evaluates the best archetype based on CURRENTLY EQUIPPED items.
        /// Priority order matters! Shield+Melee beats having a bow in backup.
        /// 
        /// DETECTION PRIORITY:
        /// 1. Support Staff EQUIPPED in hand ? Healer
        /// 2. Shield EQUIPPED + Melee EQUIPPED ? Tank/Paladin (giants favor Tank)
        /// 3. Offensive Staff EQUIPPED ? Mage
        /// 4. Bow/Crossbow EQUIPPED in hand ? Ranger
        /// 5. Knives EQUIPPED ? Rogue
        /// 6. Unarmed/Clubs EQUIPPED ? Monk
        /// 7. Two-handed or other melee ? Berserker
        /// </summary>
        private ArchetypeClass EvaluateBestArchetype()
        {
            if (_inventory == null) return ArchetypeClass.None;
            
            // Get what's ACTUALLY EQUIPPED (not in storage/backup)
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            // Log equipped items for debugging
            if (VerboseLogging)
            {
                string rhName = rightHand?.m_shared?.m_name ?? "empty";
                string lhName = leftHand?.m_shared?.m_name ?? "empty";
                Debug.Log($"[Archetype] Evaluating {_companion?.companionName}: RightHand={rhName}, LeftHand={lhName}");
            }
            
            // 1. SUPPORT STAFF EQUIPPED ? HEALER (highest priority for support role)
            if (HasSupportStaffEquipped())
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as HEALER (support staff equipped)");
                return ArchetypeClass.Healer;
            }
            
            // 2. SHIELD + MELEE EQUIPPED ? TANK or PALADIN
            // This takes priority over having a bow in backup!
            bool shieldEquipped = leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield;
            bool meleeEquipped = rightHand != null && IsMeleeWeaponItem(rightHand);
            
            if (shieldEquipped && meleeEquipped)
            {
                // Giants strongly favor Tank
                if (ArchetypeUtils.IsGiant(_companion))
                {
                    Debug.Log($"[Archetype] {_companion?.companionName} detected as TANK (giant with shield+melee)");
                    return ArchetypeClass.Tank;
                }
                
                // Paladin if has mace/club
                if (rightHand?.m_shared?.m_skillType == Skills.SkillType.Clubs)
                {
                    Debug.Log($"[Archetype] {_companion?.companionName} detected as PALADIN (shield+club)");
                    return ArchetypeClass.Paladin;
                }
                
                Debug.Log($"[Archetype] {_companion?.companionName} detected as TANK (shield+melee)");
                return ArchetypeClass.Tank;
            }
            
            // 3. OFFENSIVE STAFF EQUIPPED ? MAGE
            if (HasOffensiveStaff())
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as MAGE (offensive staff equipped)");
                return ArchetypeClass.Mage;
            }
            
            // 4. BOW/CROSSBOW EQUIPPED ? RANGER
            // Only if bow is in LEFT HAND (equipped) or if they have no shield
            bool bowEquipped = (leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow) ||
                               (rightHand?.m_shared?.m_skillType == Skills.SkillType.Bows) ||
                               (rightHand?.m_shared?.m_skillType == Skills.SkillType.Crossbows);
            
            if (bowEquipped)
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as RANGER (bow equipped)");
                return ArchetypeClass.Ranger;
            }
            
            // 5. KNIVES EQUIPPED ? ROGUE
            if (rightHand?.m_shared?.m_skillType == Skills.SkillType.Knives)
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as ROGUE (knives equipped)");
                return ArchetypeClass.Rogue;
            }
            
            // 6. UNARMED OR CLUBS (fists) ? MONK
            if (HasUnarmedOrClubs())
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as MONK (unarmed/clubs)");
                return ArchetypeClass.Monk;
            }
            
            // 7. TWO-HANDED OR OTHER MELEE ? BERSERKER
            if (HasTwoHandedWeapon() || HasMeleeWeapon())
            {
                Debug.Log($"[Archetype] {_companion?.companionName} detected as BERSERKER (melee weapon)");
                return ArchetypeClass.Berserker;
            }
            
            // If nothing equipped, check backup slots for hints
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            // Check backup slots as fallback
            if (rightBack != null || leftBack != null)
            {
                if (ArchetypeUtils.IsSupportStaff(rightBack) || ArchetypeUtils.IsSupportStaff(leftBack))
                    return ArchetypeClass.Healer;
                if (leftBack?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                    return ArchetypeClass.Ranger;
                if (IsMeleeWeaponItem(rightBack))
                    return ArchetypeClass.Berserker;
            }
            
            Debug.Log($"[Archetype] {_companion?.companionName} defaulting to NONE (no weapons detected)");
            return ArchetypeClass.None;
        }
        
        /// <summary>
        /// Checks if item is a melee weapon (not shield, not bow, not staff, not tool).
        /// </summary>
        private bool IsMeleeWeaponItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (!item.IsWeapon()) return false;
            
            var itemType = item.m_shared.m_itemType;
            var skill = item.m_shared.m_skillType;
            
            // Not shields
            if (itemType == ItemDrop.ItemData.ItemType.Shield) return false;
            // Not bows
            if (itemType == ItemDrop.ItemData.ItemType.Bow) return false;
            // Not tools
            if (itemType == ItemDrop.ItemData.ItemType.Tool) return false;
            
            // Not ranged skills
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows) return false;
            // Not magic skills
            if (skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic) return false;
            // Not gathering tools
            if (skill == Skills.SkillType.Pickaxes || skill == Skills.SkillType.WoodCutting) return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if a SUPPORT STAFF is currently equipped in hand (not just in storage).
        /// </summary>
        private bool HasSupportStaffEquipped()
        {
            var rightHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            return ArchetypeUtils.IsSupportStaff(rightHand) || ArchetypeUtils.IsSupportStaff(leftHand);
        }
        
        /// <summary>
        /// Checks if companion is using unarmed or club-type weapons (Monk).
        /// </summary>
        private bool HasUnarmedOrClubs()
        {
            var rightHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            // Unarmed - no weapon equipped but ready to fight
            if (rightHand == null && leftHand == null)
            {
                // Check if companion has unarmed skill or is meant to be unarmed
                // For now, don't auto-assign monk to weaponless companions
                return false;
            }
            
            // Check for clubs/fists
            if (rightHand?.m_shared?.m_skillType == Skills.SkillType.Clubs)
            {
                // Make sure it's not a mace with a shield (that's paladin)
                var shield = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
                if (shield?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                    return false; // Shield + Club = Paladin, not Monk
                    
                return true;
            }
            
            if (rightHand?.m_shared?.m_skillType == Skills.SkillType.Unarmed)
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Sets the archetype and applies all associated bonuses.
        /// Also determines sub-archetype from storage inventory (if level requirement met).
        /// </summary>
        public void SetArchetype(ArchetypeClass newArchetype)
        {
            var oldArchetype = _currentArchetype;
            _currentArchetype = newArchetype;
            _currentDefinition = ArchetypeRegistry.GetDefinition(newArchetype);

            // Evaluate sub-archetype from storage inventory (only if level >= 25)
            EvaluateSubArchetype(newArchetype);
            
            // Update statistics
            _statistics?.SetCurrentArchetype(newArchetype);
            
            // Set legacy archetype for backwards compatibility
            _legacyArchetype = MapToLegacyArchetype(newArchetype);
            
            // Apply archetype-specific configuration
            ConfigureForArchetype(newArchetype);
            
            // Apply stat modifiers
            ApplyArchetypeStatModifiers();
            
            // Fire events
            OnArchetypeAssigned?.Invoke(newArchetype);
            OnLegacyArchetypeAssigned?.Invoke(_legacyArchetype);
            
            // ALWAYS log archetype changes - important for debugging role assignment
            if (oldArchetype != newArchetype)
            {
                string subInfo = HasSubArchetype ? $" (sub: {_subArchetype})" : "";
                if (!HasSubArchetype && CompanionLevel < AbilityUnlockSystem.HYBRID_UNLOCK_LEVEL)
                {
                    subInfo = $" (hybrid unlocks at L{AbilityUnlockSystem.HYBRID_UNLOCK_LEVEL})";
                }
                Debug.Log($"[Archetype] ASSIGNED: {_companion?.companionName} => {newArchetype}{subInfo} (was {oldArchetype})");
                
                // Announce via chat bubble
                ArchetypeChatManager.AnnounceArchetypeAssigned(_companion, newArchetype, oldArchetype);
                
                // Trigger compendium discovery for this archetype
                try
                {
                    var owner = _companion?.GetOwner();
                    if (owner != null)
                    {
                        FiresCore.Bridge.CompanionEventBridge.RaiseArchetypeSeen(owner, (int)newArchetype);
                        
                        // Also trigger companion_recruited if this is a new assignment
                        if (oldArchetype == ArchetypeClass.None)
                        {
                            FiresCore.Bridge.CompanionEventBridge.RaiseCompanionRecruited(owner);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ArchetypeController] Compendium discovery error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Evaluates sub-archetype based on weapons available in storage inventory AND backup equipment slots.
        /// The sub-archetype provides secondary abilities that can be used situationally.
        /// LEVEL REQUIREMENT: Sub-archetypes (hybrid classes) only unlock at level 25+.
        /// </summary>
        private void EvaluateSubArchetype(ArchetypeClass primaryArchetype)
        {
            _subArchetype = ArchetypeClass.None;
            _subDefinition = null;
            
            if (_inventory == null) return;
            
            // LEVEL GATE: Sub-archetypes only unlock at level 25+
            int level = CompanionLevel;
            if (level < AbilityUnlockSystem.HYBRID_UNLOCK_LEVEL)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[Archetype] {_companion?.companionName} level {level} < {AbilityUnlockSystem.HYBRID_UNLOCK_LEVEL}, no sub-archetype yet");
                }
                return;
            }
            
            // Score potential sub-archetypes based on what's in storage AND backup slots
            var archetypeScores = new Dictionary<ArchetypeClass, float>();
            
            // Check equipped backup slots FIRST (higher priority - they're ready to swap to)
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            // Log backup slots for debugging
            if (VerboseLogging)
            {
                string rbName = rightBack?.m_shared?.m_name ?? "empty";
                string lbName = leftBack?.m_shared?.m_name ?? "empty";
                Debug.Log($"[Archetype] {_companion?.companionName} backup slots: RightBack={rbName}, LeftBack={lbName}");
            }
            
            // Special check: If backup has Shield + Melee weapon, strongly suggest Tank
            bool hasBackupShield = leftBack?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield;
            bool hasBackupMelee = rightBack != null && IsMeleeWeaponItem(rightBack);
            
            if (hasBackupShield && hasBackupMelee)
            {
                // This is a tank loadout in backup - give it a strong score
                AddScore(archetypeScores, ArchetypeClass.Tank, 15f);
                if (VerboseLogging)
                {
                    Debug.Log($"[Archetype] {_companion?.companionName} has Tank loadout in backup (shield+melee)");
                }
            }
            else
            {
                // Score backup items individually
                if (rightBack != null)
                {
                    ScoreItemForArchetype(rightBack, archetypeScores, 10f); // Full weight for backup slots
                }
                if (leftBack != null)
                {
                    ScoreItemForArchetype(leftBack, archetypeScores, 10f);
                }
            }
            
            // Then check storage inventory
            var storage = _inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    
                    var itemType = item.m_shared.m_itemType;
                    var skillType = item.m_shared.m_skillType;
                    
                    // Shield suggests Tank potential
                    if (itemType == ItemDrop.ItemData.ItemType.Shield)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Tank, 10f);
                        AddScore(archetypeScores, ArchetypeClass.Paladin, 5f);
                    }
                    
                    // Bow/Crossbow suggests Ranger
                    if (itemType == ItemDrop.ItemData.ItemType.Bow ||
                        skillType == Skills.SkillType.Bows ||
                        skillType == Skills.SkillType.Crossbows)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Ranger, 10f);
                    }
                    
                    // Knives suggest Rogue
                    if (skillType == Skills.SkillType.Knives)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Rogue, 10f);
                    }
                    
                    // Clubs suggest Monk or Paladin
                    if (skillType == Skills.SkillType.Clubs)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Monk, 5f);
                        AddScore(archetypeScores, ArchetypeClass.Paladin, 5f);
                    }
                    
                    // Two-handed weapons suggest Berserker
                    if (itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                        itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Berserker, 8f);
                    }
                    
                    // Offensive staff suggests Mage
                    if (ArchetypeUtils.IsOffensiveStaff(item))
                    {
                        AddScore(archetypeScores, ArchetypeClass.Mage, 10f);
                    }
                    
                    // Support staff suggests Healer
                    if (ArchetypeUtils.IsSupportStaff(item))
                    {
                        AddScore(archetypeScores, ArchetypeClass.Healer, 10f);
                    }
                    
                    // Swords/Axes suggest melee DPS
                    if (skillType == Skills.SkillType.Swords || 
                        skillType == Skills.SkillType.Axes ||
                        skillType == Skills.SkillType.Polearms)
                    {
                        AddScore(archetypeScores, ArchetypeClass.Berserker, 5f);
                    }
                }
            }
            
            // Remove the primary archetype from consideration
            archetypeScores.Remove(primaryArchetype);
            
            // Find the highest scoring sub-archetype
            ArchetypeClass bestSub = ArchetypeClass.None;
            float bestScore = 0f;
            
            foreach (var kvp in archetypeScores)
            {
                if (kvp.Value > bestScore)
                {
                    bestScore = kvp.Value;
                    bestSub = kvp.Key;
                }
            }
            
            // Only assign sub-archetype if there's meaningful equipment for it
            if (bestScore >= 5f)
            {
                _subArchetype = bestSub;
                _subDefinition = ArchetypeRegistry.GetDefinition(bestSub);
                
                // Always log sub-archetype assignment for debugging
                Debug.Log($"[Archetype] {_companion?.companionName} sub-archetype: {_subArchetype} (score: {bestScore:F1})");
                
                // Notify hybrid ability manager to refresh
                var hybridManager = GetComponent<HybridAbilityManager>();
                if (hybridManager != null)
                {
                    hybridManager.RefreshHybridDefinition();
                }
                
                // Trigger compendium discovery for this hybrid
                try
                {
                    var owner = _companion?.GetOwner();
                    if (owner != null)
                    {
                        FiresCore.Bridge.CompanionEventBridge.RaiseHybridUnlocked(owner, (int)primaryArchetype, (int)bestSub);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ArchetypeController] Hybrid compendium discovery error: {ex.Message}");
                }
            }
            else if (VerboseLogging)
            {
                Debug.Log($"[Archetype] {_companion?.companionName} no sub-archetype (best score: {bestScore:F1})");
            }
        }
        
        /// <summary>
        /// Adds a score to an archetype in the scoring dictionary.
        /// </summary>
        private void AddScore(Dictionary<ArchetypeClass, float> scores, ArchetypeClass archetype, float amount)
        {
            if (!scores.ContainsKey(archetype))
                scores[archetype] = 0f;
            scores[archetype] += amount;
        }
        
        /// <summary>
        /// Scores an individual item for archetype determination.
        /// </summary>
        private void ScoreItemForArchetype(ItemDrop.ItemData item, Dictionary<ArchetypeClass, float> scores, float multiplier)
        {
            if (item?.m_shared == null) return;
            
            var itemType = item.m_shared.m_itemType;
            var skillType = item.m_shared.m_skillType;
            
            // Shield suggests Tank/Paladin
            if (itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                AddScore(scores, ArchetypeClass.Tank, 10f * multiplier / 10f);
                AddScore(scores, ArchetypeClass.Paladin, 5f * multiplier / 10f);
            }
            
            // Bow/Crossbow suggests Ranger
            if (itemType == ItemDrop.ItemData.ItemType.Bow || 
                skillType == Skills.SkillType.Bows ||
                skillType == Skills.SkillType.Crossbows)
            {
                AddScore(scores, ArchetypeClass.Ranger, 10f * multiplier / 10f);
            }
            
            // Knives suggest Rogue
            if (skillType == Skills.SkillType.Knives)
            {
                AddScore(scores, ArchetypeClass.Rogue, 10f * multiplier / 10f);
            }
            
            // Clubs suggest Monk or Paladin
            if (skillType == Skills.SkillType.Clubs)
            {
                AddScore(scores, ArchetypeClass.Monk, 5f * multiplier / 10f);
                AddScore(scores, ArchetypeClass.Paladin, 5f * multiplier / 10f);
            }
            
            // Offensive staff suggests Mage
            if (ArchetypeUtils.IsOffensiveStaff(item))
            {
                AddScore(scores, ArchetypeClass.Mage, 10f * multiplier / 10f);
            }
            
            // Support staff suggests Healer
            if (ArchetypeUtils.IsSupportStaff(item))
            {
                AddScore(scores, ArchetypeClass.Healer, 10f * multiplier / 10f);
            }
            
            // Two-handed weapons suggest Berserker
            if (itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
            {
                AddScore(scores, ArchetypeClass.Berserker, 8f * multiplier / 10f);
            }
            
            // Swords/Axes/Polearms (one-handed melee) suggest Berserker or Tank (if paired with shield)
            if (skillType == Skills.SkillType.Swords || 
                skillType == Skills.SkillType.Axes ||
                skillType == Skills.SkillType.Polearms ||
                itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon)
            {
                AddScore(scores, ArchetypeClass.Berserker, 5f * multiplier / 10f);
                // One-handed melee also suggests potential for Tank (needs shield)
                AddScore(scores, ArchetypeClass.Tank, 3f * multiplier / 10f);
            }
        }
        
        /// <summary>
        /// Maps new ArchetypeClass to legacy CompanionArchetypeType.
        /// </summary>
        private CompanionArchetypeType MapToLegacyArchetype(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                case ArchetypeClass.Paladin:
                    return CompanionArchetypeType.Tank;
                case ArchetypeClass.Healer:
                    return CompanionArchetypeType.Support;
                case ArchetypeClass.Berserker:
                case ArchetypeClass.Rogue:
                    return CompanionArchetypeType.MeleeDPS;
                case ArchetypeClass.Ranger:
                case ArchetypeClass.Mage:
                    return CompanionArchetypeType.RangedDPS;
                default:
                    return CompanionArchetypeType.None;
            }
        }
        
        /// <summary>
        /// Called when archetype changes - sets up archetype-specific configurations.
        /// </summary>
        public void OnArchetypeChanged(CompanionArchetypeType newLegacyArchetype)
        {
            // Map legacy to new system
            ArchetypeClass newArchetype = ArchetypeClass.None;
            switch (newLegacyArchetype)
            {
                case CompanionArchetypeType.Tank:
                    newArchetype = HasShield() ? ArchetypeClass.Tank : ArchetypeClass.Berserker;
                    break;
                case CompanionArchetypeType.Support:
                    newArchetype = ArchetypeClass.Healer;
                    break;
                case CompanionArchetypeType.MeleeDPS:
                    newArchetype = HasKnives() ? ArchetypeClass.Rogue : ArchetypeClass.Berserker;
                    break;
                case CompanionArchetypeType.RangedDPS:
                    newArchetype = HasRangedWeapon() ? ArchetypeClass.Ranger : ArchetypeClass.Mage;
                    break;
            }
            
            if (newArchetype != ArchetypeClass.None)
            {
                SetArchetype(newArchetype);
            }
        }
        
        /// <summary>
        /// Forces re-evaluation of archetype (call after equipment changes).
        /// </summary>
        public void ForceReevaluate()
        {
            EvaluateArchetype();
        }
        
        #endregion
        
        #region Tank Behavior
        
        /// <summary>
        /// Configures companion for the specified archetype.
        /// </summary>
        private void ConfigureForArchetype(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                    ConfigureAsTank();
                    break;
                case ArchetypeClass.Paladin:
                    ConfigureAsPaladin();
                    break;
                case ArchetypeClass.Berserker:
                    ConfigureAsBerserker();
                    break;
                case ArchetypeClass.Rogue:
                    ConfigureAsRogue();
                    break;
                case ArchetypeClass.Ranger:
                    ConfigureAsRanger();
                    break;
                case ArchetypeClass.Mage:
                    ConfigureAsMage();
                    break;
                case ArchetypeClass.Healer:
                    ConfigureAsHealer();
                    break;
            }
        }
        
        /// <summary>
        /// Applies archetype stat modifiers to companion stats.
        /// These scale with companion level.
        /// </summary>
        private void ApplyArchetypeStatModifiers()
        {
            if (_currentDefinition == null || _stats == null) return;
            
            int level = CompanionLevel;
            
            // Apply stamina modifiers
            if (_staminaManager != null)
            {
                float maxStaminaMult = _currentDefinition.GetScaledBonus(_currentDefinition.MaxStaminaMultiplier, level);
                float staminaRegenMult = _currentDefinition.GetScaledBonus(_currentDefinition.StaminaRegenMultiplier, level);
                float blockCostMult = _currentDefinition.GetScaledBonus(_currentDefinition.BlockStaminaCostMultiplier, level);
                float dodgeCostMult = _currentDefinition.GetScaledBonus(_currentDefinition.DodgeStaminaCostMultiplier, level);
                float attackCostMult = _currentDefinition.GetScaledBonus(_currentDefinition.AttackStaminaCostMultiplier, level);
                
                _staminaManager.SetArchetypeModifiers(
                    maxStaminaMult, 
                    staminaRegenMult, 
                    blockCostMult, 
                    dodgeCostMult, 
                    attackCostMult);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeController] {_companion?.companionName} stamina modifiers: " +
                        $"Max={maxStaminaMult:F2}x, Regen={staminaRegenMult:F2}x, Block={blockCostMult:F2}x");
                }
            }
            
            // Apply combat modifiers (only if method exists)
            // CompanionCombat may not have SetArchetypeModifiers method
            /*
            if (_combat != null)
            {
                _combat.SetArchetypeModifiers(
                    _currentDefinition.GetScaledBonus(_currentDefinition.AttackDamageMultiplier, level),
                    _currentDefinition.GetScaledBonus(_currentDefinition.AttackSpeedMultiplier, level),
                    _currentDefinition.CriticalChanceBonus + (level * 0.005f),
                    _currentDefinition.GetScaledBonus(_currentDefinition.CriticalDamageMultiplier, level),
                    _currentDefinition.GetScaledBonus(blockPriorityMultiplier, level));
            }
            */
        }
        
        private void ConfigureAsTank()
        {
            // Increase block preference
            if (_combat != null)
            {
                _combat.blockChance *= blockPriorityMultiplier;
                _combat.parryChance *= blockPriorityMultiplier;
            }
            
            // Tank needs shield + melee - ensure proper weapon setup
            EnsureTankLoadout();
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as TANK");
            }
        }
        
        private void ConfigureAsPaladin()
        {
            if (_combat != null)
            {
                _combat.blockChance *= blockPriorityMultiplier * 0.8f;
            }
            
            EnsureTankLoadout();
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as PALADIN");
            }
        }
        
        private void ConfigureAsBerserker()
        {
            _isBerserkActive = false;
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as BERSERKER");
            }
        }
        
        private void ConfigureAsRogue()
        {
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as ROGUE");
            }
        }
        
        private void ConfigureAsRanger()
        {
            if (_combat != null)
            {
                _combat.attackRange = 18f; // Prefer range
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as RANGER");
            }
        }
        
        private void ConfigureAsMage()
        {
            if (_combat != null)
            {
                _combat.attackRange = 15f;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as MAGE");
            }
        }
        
        private void ConfigureAsHealer()
        {
            if (_combat != null)
            {
                _combat.attackRange = preferredSupportRange;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} configured as HEALER");
            }
        }
        
        private void EnsureTankLoadout()
        {
            if (_inventory == null) return;
            
            // Make sure shield is equipped
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand?.m_shared?.m_itemType != ItemDrop.ItemData.ItemType.Shield)
            {
                // Try to find and equip a shield
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                    {
                        if (item?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                        {
                            // Unequip bow if in left hand
                            if (leftHand != null)
                            {
                                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                                if (leftBack == null)
                                {
                                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHand);
                                }
                            }
                            
                            // Equip shield
                            storage.RemoveItem(item);
                            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, item);
                            break;
                        }
                    }
                }
            }
            
            // Make sure melee weapon is in right hand
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand == null || !rightHand.IsWeapon())
            {
                // Try to find a melee weapon
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack != null && ArchetypeUtils.IsMeleeWeapon(rightBack))
                {
                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightBack);
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, rightBack);
                }
            }
            
            // Apply visual changes
            _inventory.ApplyVisualEquipment();
        }
        
        private void UpdateTankBehavior()
        {
            // GROUP DIRECTIVE: If the coordinator tells us to do something specific, prioritize it
            var directive = GetDirective();
            if (directive != null && directive.IsValid)
            {
                switch (directive.Directive)
                {
                    case CombatRoleDirector.CombatDirective.InterceptThreat:
                        // Force target the intercepted enemy
                        if (directive.TargetEnemy != null && !directive.TargetEnemy.IsDead())
                        {
                            var ai = _companion?.GetCompanionAI();
                            ai?.ForceTarget(directive.TargetEnemy);
                        }
                        break;
                        
                    case CombatRoleDirector.CombatDirective.EngageAndTaunt:
                        if (directive.TargetEnemy != null && !directive.TargetEnemy.IsDead())
                        {
                            var ai = _companion?.GetCompanionAI();
                            ai?.ForceTarget(directive.TargetEnemy);
                        }
                        if (!IsTauntOnCooldown)
                            ExecuteTaunt();
                        break;
                        
                    case CombatRoleDirector.CombatDirective.HoldPosition:
                        // Don't chase ï¿½ stay and block
                        break;
                        
                    case CombatRoleDirector.CombatDirective.Regroup:
                        // Handled by movement system ï¿½ tank should still protect player
                        break;
                }
                // Still run normal tank logic (taunt cooldown check, intercept) as a fallback
            }
            
            // Check if we should use taunt
            if (ShouldUseTaunt())
            {
                ExecuteTaunt();
            }
            
            // Check if allies need protection
            CheckAndIntercept();
        }
        
        /// <summary>
        /// Determines if the tank should use their taunt ability.
        /// PROACTIVE TAUNT: Tank should taunt early and often to maintain aggro.
        /// SMART TAUNT: Don't waste cooldowns on trivial enemies (1-2 greydwarfs).
        /// LEVEL REQUIREMENT: Taunt unlocks at level 5.
        /// </summary>
        private bool ShouldUseTaunt()
        {
            if (IsTauntOnCooldown) return false;
            if (_character == null || _character.IsDead()) return false;
            if (_companion == null) return false;
            
            // LEVEL GATE: Taunt requires level 5
            if (!AbilityUnlockSystem.IsAbilityUnlocked("tank_taunt", ArchetypeClass.Tank, CompanionLevel))
            {
                return false;
            }
            
            var owner = _companion.GetOwner();
            
            // Build list of all allies (owner + same-owner companions)
            var allies = new System.Collections.Generic.List<Character>();
            if (owner != null) allies.Add(owner);
            
            foreach (var comp in CompanionController.AllCompanions)
            {
                if (comp == null || comp.isDefeated) continue;
                if (comp.ownerPlayerId != _companion.ownerPlayerId) continue;
                var compChar = comp.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    allies.Add(compChar);
                }
            }
            
            int enemiesNearAllies = 0;
            int enemiesTargetingAllies = 0;
            int significantEnemies = 0; // Medium threat or higher
            bool ownerTargeted = false;
            
            // Check enemies - are any within 5m of ANY ally?
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                // Check if this enemy is within tauntRange of ANY ally
                bool nearAnyAlly = false;
                foreach (var ally in allies)
                {
                    float distToAlly = Vector3.Distance(ally.transform.position, character.transform.position);
                    if (distToAlly <= tauntRange)
                    {
                        nearAnyAlly = true;
                        break;
                    }
                }
                
                if (nearAnyAlly)
                {
                    enemiesNearAllies++;
                    
                    // Evaluate threat level - only count significant enemies
                    if (EvaluateEnemyThreat(character) >= 1) // Medium or higher
                    {
                        significantEnemies++;
                    }
                    
                    // Check if this enemy is targeting anyone OTHER than us
                    var ai = character.GetComponent<BaseAI>();
                    if (ai != null)
                    {
                        var target = ai.GetTargetCreature();
                        if (target != null && target != _character)
                        {
                            // Enemy is targeting someone else - we should taunt!
                            enemiesTargetingAllies++;
                            
                            // Priority: If targeting owner, taunt immediately (even for weak enemies)
                            if (target == owner)
                            {
                                ownerTargeted = true;
                            }
                        }
                    }
                }
            }
            
            // ALWAYS taunt if owner is being targeted (protect the player!)
            if (ownerTargeted)
            {
                if (VerboseLogging)
                    Debug.Log($"[Archetype] {_companion.companionName} TAUNT TRIGGER: Enemy targeting owner!");
                return true;
            }
            
            // SMART TAUNT: Don't waste 40s cooldown on just 1-2 weak enemies
            // Only taunt if:
            // 1. There's at least 1 significant (medium+) threat, OR
            // 2. There are 3+ enemies (even if weak), OR
            // 3. Allies are actually taking damage (enemies targeting them)
            
            bool worthTaunting = significantEnemies >= 1 || 
                                 enemiesNearAllies >= 3 || 
                                 (enemiesTargetingAllies >= 2 && enemiesNearAllies >= 2);
            
            if (!worthTaunting)
            {
                if (VerboseLogging && enemiesNearAllies > 0)
                {
                    Debug.Log($"[Archetype] {_companion.companionName} skipping taunt - not worth it ({enemiesNearAllies} enemies, {significantEnemies} significant)");
                }
                return false;
            }
            
            // PROACTIVE TAUNT: If there are enemies near any ally and any are targeting allies, taunt
            if (enemiesNearAllies > 0 && enemiesTargetingAllies > 0)
            {
                if (VerboseLogging)
                    Debug.Log($"[Archetype] {_companion.companionName} TAUNT TRIGGER: {enemiesTargetingAllies}/{enemiesNearAllies} enemies near allies targeting them");
                return true;
            }
            
            // PROACTIVE TAUNT: If there are 3+ enemies near any ally, use taunt
            if (enemiesNearAllies >= 3)
            {
                if (VerboseLogging)
                    Debug.Log($"[Archetype] {_companion.companionName} TAUNT TRIGGER: {enemiesNearAllies} enemies near allies");
                return true;
            }
            
            // Check if other companions need help (supports being attacked)
            var supports = GroupRoleManager.Instance.GetSupports(_companion.ownerPlayerId);
            foreach (var support in supports)
            {
                if (support == null || support.isDefeated) continue;
                
                var supportChar = support.GetCharacter();
                if (supportChar != null && supportChar.GetHealthPercentage() < 0.7f)
                {
                    // Support is hurt, taunt to draw aggro
                    if (VerboseLogging)
                        Debug.Log($"[Archetype] {_companion.companionName} TAUNT TRIGGER: Support {support.companionName} hurt");
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Quick threat evaluation for taunt decisions.
        /// Returns: 0 = trivial, 1 = medium, 2 = high, 3 = boss
        /// </summary>
        private int EvaluateEnemyThreat(Character enemy)
        {
            if (enemy == null) return 0;
            
            string name = enemy.m_name?.ToLower() ?? "";
            float maxHealth = enemy.GetMaxHealth();
            int level = enemy.GetLevel();
            
            // Starred enemies are always significant
            if (level >= 2) return 2; // 1-star = high
            if (level >= 3) return 3; // 2-star = boss-level
            
            // Boss check
            if (maxHealth > 2000 || name.Contains("boss") || name.Contains("yagluth") || 
                name.Contains("bonemass") || name.Contains("moder") || name.Contains("queen"))
                return 3;
            
            // High threat
            if (maxHealth > 500 || name.Contains("troll") || name.Contains("golem") || 
                name.Contains("abomination") || name.Contains("lox") || name.Contains("seeker") ||
                name.Contains("gjall") || name.Contains("dvergr"))
                return 2;
            
            // Medium threat
            if (maxHealth > 150 || name.Contains("draugr") || name.Contains("fuling") || 
                name.Contains("fenring") || name.Contains("wraith") || name.Contains("blob"))
                return 1;
            
            // Trivial - greydwarfs, necks, etc
            return 0;
        }
        
        /// <summary>
        /// Executes the taunt ability with shockwave.
        /// The companion performs a taunting emote (flex, challenge, or roar) which triggers
        /// a hammer-like shockwave in a 5m radius. The shockwave deals minimal damage (0.01)
        /// to only monsters and enemy companions (not buildings, trees, or players), and applies
        /// a 30-second taunt effect that forces enemies to target the tank.
        /// </summary>
        public void ExecuteTaunt()
        {
            if (_character == null || IsTauntOnCooldown) return;
            
            _lastTauntTime = Time.time;

            int tauntedCount = 0;
            
            // Use the new shockwave taunt system by default
            if (useTauntShockwave)
            {
                // Apply taunt with shockwave (emote + AoE effect + taunt)
                tauntedCount = TauntManager.ApplyTauntWithShockwave(_character, tauntRange, tauntDuration);
            }
            else
            {
                // Fall back to simple taunt (no emote, no shockwave)
                tauntedCount = TauntManager.ApplyTaunt(_character, tauntRange, tauntDuration);
                PlayTauntEffects();
            }
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Taunt", tauntedCount);
            
            // ALWAYS log taunts - they're significant combat events that help diagnose tank behavior
            if (tauntedCount > 0)
            {
                Debug.Log($"[Archetype] TAUNT: {_companion?.companionName} taunted {tauntedCount} enemies for {tauntDuration}s with {(useTauntShockwave ? "shockwave" : "basic taunt")}");
            }
            
            // Reset taunting state after duration
            Invoke(nameof(EndTaunt), tauntDuration);
        }
        
        private void EndTaunt()
        {
            // Taunt end-state was tracked by an `_isTaunting` bool that nothing
            // ever read. Removed in the warnings cleanup pass; this hook is
            // retained as a no-op so callers (PlayTauntEffects sequencing) and
            // anything that looks for the symbol still compile.
        }
        
        private void PlayTauntEffects()
        {
            // Play a shout/roar animation if available
            var animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                // Try to trigger a shout/roar animation
                animator.SetTrigger("shout");
            }
            
            // Play sound effect
            // TODO: Add custom taunt sound
        }
        
        /// <summary>
        /// Checks if any allies need protection and intercepts if so.
        /// </summary>
        private void CheckAndIntercept()
        {
            if (_companion == null || _movement == null) return;
            
            var owner = _companion.GetOwner();
            if (owner == null) return;
            
            // Find the most threatening enemy to the owner
            Character mostThreatening = null;
            float closestDist = float.MaxValue;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float distToOwner = Vector3.Distance(owner.transform.position, character.transform.position);
                if (distToOwner < closestDist && distToOwner <= interceptionRange)
                {
                    closestDist = distToOwner;
                    mostThreatening = character;
                }
            }
            
            // If there's a threat close to owner, intercept it
            if (mostThreatening != null && closestDist < interceptionRange * 0.5f)
            {
                // TODO: Request interception via combat movement
                // _movement.SetPriorityTarget(mostThreatening, 5f);
            }
        }
        
        #endregion
        
        #region Support Behavior
        
        private void UpdateHealerBehavior()
        {
            // GROUP DIRECTIVE: If the coordinator specifies a heal target, prioritize it
            var directive = GetDirective();
            if (directive != null && directive.IsValid)
            {
                switch (directive.Directive)
                {
                    case CombatRoleDirector.CombatDirective.HealTarget:
                        // TODO: Direct healing to specific target when heal system is implemented
                        // For now, the directive informs priority ï¿½ CheckAndHealAllies handles it
                        break;
                        
                    case CombatRoleDirector.CombatDirective.StayProtected:
                        // TODO: Position behind tank/player ï¿½ handled by combat movement in Phase 5
                        break;
                        
                    case CombatRoleDirector.CombatDirective.Regroup:
                        // Emergency ï¿½ handled by movement system
                        break;
                }
            }
            
            // Check if allies need healing
            CheckAndHealAllies();
            
            // Maintain distance from enemies
            EnsureSupportLoadout();
        }
        
        private void CheckAndHealAllies()
        {
            // Healer checks for wounded allies
            // This would integrate with consumable/potion usage
            // Or staff-based healing abilities
        }
        
        private void EnsureSupportLoadout()
        {
            if (_inventory == null) return;
            
            // Check if we have support staff equipped
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            
            bool hasSupportEquipped = ArchetypeUtils.IsSupportStaff(leftHand) || ArchetypeUtils.IsSupportStaff(rightHand);
            
            if (!hasSupportEquipped && ArchetypeUtils.HasSupportStaff(_inventory))
            {
                // Need to equip support staff
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                    {
                        if (ArchetypeUtils.IsSupportStaff(item))
                        {
                            // Equip the support staff
                            storage.RemoveItem(item);
                            
                            // Staves typically go in right hand
                            if (rightHand != null)
                            {
                                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                                if (rightBack == null)
                                {
                                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, rightHand);
                                }
                                else
                                {
                                    storage.AddItem(rightHand);
                                }
                            }
                            
                            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, item);
                            _inventory.ApplyVisualEquipment();
                            break;
                        }
                    }
                }
            }
        }
        
        #endregion
        
        #region Berserker Behavior
        
        private void UpdateBerserkerBehavior()
        {
            // GROUP DIRECTIVE: Focus/flank assigned target
            ApplyDpsDirective();
            
            // Check health for berserk mode
            if (Time.time - _lastBerserkCheck > 0.5f)
            {
                _lastBerserkCheck = Time.time;
                
                if (_character != null)
                {
                    float healthPercent = _character.GetHealthPercentage();
                    bool shouldBerserk = healthPercent <= berserkHealthThreshold;
                    
                    if (shouldBerserk && !_isBerserkActive)
                    {
                        ActivateBerserk();
                    }
                    else if (!shouldBerserk && _isBerserkActive)
                    {
                        DeactivateBerserk();
                    }
                }
            }
        }
        
        private void ActivateBerserk()
        {
            _isBerserkActive = true;
            _statistics?.RecordBerserkActivation();
            
            // Apply berserk bonuses
            if (_combat != null)
            {
                // Temporary damage and speed boost
                // This could be handled via combat modifiers
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} BERSERK MODE ACTIVATED!");
            }
        }
        
        private void DeactivateBerserk()
        {
            _isBerserkActive = false;
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeController] {_companion?.companionName} berserk mode ended");
            }
        }
        
        #endregion
        
        #region Rogue Behavior
        
        private void UpdateRogueBehavior()
        {
            // GROUP DIRECTIVE: Focus/flank assigned target
            ApplyDpsDirective();
            
            // Rogues look for flanking opportunities
            // They also check for backstab positions
        }
        
        /// <summary>
        /// Calculates backstab damage multiplier if attacking from behind.
        /// </summary>
        public float GetBackstabMultiplier(Character target)
        {
            if (_currentArchetype != ArchetypeClass.Rogue) return 1.0f;
            if (target == null) return 1.0f;
            
            // Check angle to target
            Vector3 toTarget = (target.transform.position - transform.position).normalized;
            float angle = Vector3.Angle(target.transform.forward, toTarget);
            
            // If attacking from behind (within backstab angle)
            if (angle < backstabAngle)
            {
                float multiplier = backstabDamageMultiplier + (CompanionLevel * backstabBonusPerLevel);
                _statistics?.RecordBackstab();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeController] {_companion?.companionName} BACKSTAB! {multiplier:F1}x damage");
                }
                
                return multiplier;
            }
            
            return 1.0f;
        }
        
        #endregion
        
        #region Ranger Behavior
        
        private void UpdateRangerBehavior()
        {
            // GROUP DIRECTIVE: Focus assigned target
            ApplyDpsDirective();
            
            // Rangers maintain optimal range and kite
            // This is mostly handled by movement system
        }
        
        #endregion
        
        #region Mage Behavior
        
        private void UpdateMageBehavior()
        {
            // GROUP DIRECTIVE: Focus assigned target
            ApplyDpsDirective();
            
            // Mages manage eitr and use AoE attacks
            // This integrates with staff behaviors
        }
        
        /// <summary>
        /// Shared helper for DPS archetypes: reads the combat directive and forces the AI
        /// to target the assigned enemy (FocusTarget, FlankTarget, or Assist).
        /// Only changes target if the directive specifies a different one.
        /// </summary>
        private void ApplyDpsDirective()
        {
            var directive = GetDirective();
            if (directive == null || !directive.IsValid) return;
            
            switch (directive.Directive)
            {
                case CombatRoleDirector.CombatDirective.FocusTarget:
                case CombatRoleDirector.CombatDirective.FlankTarget:
                    if (directive.TargetEnemy != null && !directive.TargetEnemy.IsDead())
                    {
                        var ai = _companion?.GetCompanionAI();
                        if (ai != null)
                        {
                            // Only force target if we're not already attacking the assigned target
                            var currentTarget = ai.GetTargetCreature();
                            if (currentTarget != directive.TargetEnemy)
                            {
                                ai.ForceTarget(directive.TargetEnemy);
                                
                                if (VerboseLogging)
                                    Debug.Log($"[Archetype] {_companion?.companionName} DPS directive: {directive.Directive} on {directive.TargetEnemy.m_name}");
                            }
                        }
                    }
                    break;
                    
                case CombatRoleDirector.CombatDirective.Regroup:
                    // Emergency regroup ï¿½ stop chasing and return to player
                    // Movement system handles the actual repositioning
                    break;
                    
                case CombatRoleDirector.CombatDirective.Assist:
                    // Free to pick targets independently
                    break;
            }
        }
        
        #endregion
        
        #region Statistics Tracking
        
        private void UpdateBlockingTracking()
        {
            bool currentlyBlocking = _combat != null && _combat.IsBlocking();
            
            if (currentlyBlocking && !_isBlocking)
            {
                // Started blocking
                _isBlocking = true;
                _blockingStartTime = Time.time;
            }
            else if (!currentlyBlocking && _isBlocking)
            {
                // Stopped blocking
                _isBlocking = false;
                float blockDuration = Time.time - _blockingStartTime;
                _statistics?.AddBlockingTime(blockDuration);
            }
        }
        
        /// <summary>
        /// Called when a block is performed. Records statistics.
        /// </summary>
        public void OnBlockPerformed(bool successful, float damageBlocked, bool wasParry)
        {
            _statistics?.RecordBlock(successful, damageBlocked, wasParry);
            
            // TANK BONUS: Parry restores stamina
            if (wasParry && IsTank && _staminaManager != null)
            {
                float restoreAmount = parryStaminaRestoreBase + (CompanionLevel * parryStaminaRestorePerLevel);
                _staminaManager.RestoreStamina(restoreAmount);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeController] {_companion?.companionName} parry restored {restoreAmount:F1} stamina!");
                }
            }
        }
        
        /// <summary>
        /// Called when a dodge is performed. Records statistics.
        /// </summary>
        public void OnDodgePerformed(bool successful)
        {
            _statistics?.RecordDodge(successful);
        }
        
        /// <summary>
        /// Called when a critical hit is landed. Records statistics.
        /// </summary>
        public void OnCriticalHit()
        {
            _statistics?.RecordCriticalHit();
        }
        
        /// <summary>
        /// Called when a kill is made. Records weapon type.
        /// </summary>
        public void OnKill(string weaponType)
        {
            _statistics?.RecordKillWithWeapon(weaponType);
        }
        
        /// <summary>
        /// Called when weapon changes.
        /// </summary>
        public void OnWeaponChanged(string weaponType)
        {
            _statistics?.SetCurrentWeaponType(weaponType);
        }
        
        private void LoadStatistics()
        {
            // Try to load from vault
            if (_companion != null && _stats != null)
            {
                string savedData = _stats.GetArchetypeStatisticsData();
                if (!string.IsNullOrEmpty(savedData))
                {
                    _statistics = ArchetypeStatistics.Deserialize(savedData);
                }
            }
        }
        
        /// <summary>
        /// Gets statistics data for vault storage.
        /// </summary>
        public string GetStatisticsDataForVault()
        {
            _statistics?.FinalizeTracking();
            return _statistics?.Serialize() ?? "";
        }
        
        /// <summary>
        /// Restores statistics from vault data.
        /// </summary>
        public void RestoreStatisticsFromVault(string data)
        {
            if (!string.IsNullOrEmpty(data))
            {
                _statistics = ArchetypeStatistics.Deserialize(data);
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Sets the current combat directive from the GroupCombatCoordinator.
        /// Directives are suggestions ï¿½ if expired or null, archetype falls back to independent behavior.
        /// Called once per coordinator tick (0.3s).
        /// </summary>
        public void SetDirective(CombatRoleDirector.RoleDirective directive)
        {
            _currentDirective = directive;
        }
        
        /// <summary>
        /// Gets the current combat directive, or null if none/expired.
        /// </summary>
        public CombatRoleDirector.RoleDirective GetDirective()
        {
            if (_currentDirective != null && _currentDirective.IsExpired)
                _currentDirective = null;
            return _currentDirective;
        }
        
        /// <summary>
        /// Gets the block priority multiplier based on archetype.
        /// Tank has higher block priority.
        /// </summary>
        public float GetBlockPriorityMultiplier()
        {
            if (_currentArchetype == ArchetypeClass.Tank) return blockPriorityMultiplier;
            if (_currentArchetype == ArchetypeClass.Paladin) return blockPriorityMultiplier * 0.8f;
            return 1.0f;
        }
        
        /// <summary>
        /// Gets the attack priority multiplier based on archetype.
        /// DPS has higher attack priority.
        /// </summary>
        public float GetAttackPriorityMultiplier()
        {
            return IsDPS ? aggressionLevel : 0.8f;
        }
        
        /// <summary>
        /// Returns true if this archetype should prioritize protecting allies.
        /// </summary>
        public bool ShouldProtectAllies()
        {
            return IsTank || IsSupport;
        }
        
        /// <summary>
        /// Returns true if this archetype should maintain distance from enemies.
        /// </summary>
        public bool ShouldMaintainDistance()
        {
            return IsRanged;
        }
        
        /// <summary>
        /// Gets the berserk damage multiplier (1.0 if not berserk or not berserker).
        /// </summary>
        public float GetBerserkDamageMultiplier()
        {
            if (_currentArchetype != ArchetypeClass.Berserker) return 1.0f;
            return _isBerserkActive ? (1.0f + berserkDamageBonus) : 1.0f;
        }
        
        /// <summary>
        /// Gets archetype display name for UI.
        /// Shows hybrid name if sub-archetype exists.
        /// </summary>
        public string GetArchetypeDisplayName()
        {
            // Check for hybrid name first
            if (HasSubArchetype)
            {
                var hybrid = HybridArchetypeDefinitions.GetHybrid(_currentArchetype, _subArchetype);
                if (hybrid != null)
                    return hybrid.DisplayName;
                
                // Fallback to simple "Primary / Sub" format
                string primary = _currentDefinition?.DisplayName ?? _currentArchetype.ToString();
                string sub = _subDefinition?.DisplayName ?? _subArchetype.ToString();
                return $"{primary} / {sub}";
            }
            
            return _currentDefinition?.DisplayName ?? _currentArchetype.ToString();
        }
        
        /// <summary>
        /// Gets the hybrid definition if this companion has a valid main/sub combination.
        /// </summary>
        public HybridArchetypeDefinitions.HybridDefinition GetHybridDefinition()
        {
            if (!HasSubArchetype) return null;
            return HybridArchetypeDefinitions.GetHybrid(_currentArchetype, _subArchetype);
        }
        
        /// <summary>
        /// Gets the hybrid special ability name, or null if no hybrid.
        /// </summary>
        public string GetHybridAbilityName()
        {
            return GetHybridDefinition()?.SpecialAbilityName;
        }
        
        /// <summary>
        /// Gets the hybrid special ability description, or null if no hybrid.
        /// </summary>
        public string GetHybridAbilityDescription()
        {
            return GetHybridDefinition()?.SpecialAbilityDescription;
        }
        
        /// <summary>
        /// Gets archetype icon color for UI.
        /// Uses hybrid color if available.
        /// </summary>
        public Color GetArchetypeColor()
        {
            // Check for hybrid color first
            if (HasSubArchetype)
            {
                var hybrid = GetHybridDefinition();
                if (hybrid != null)
                    return hybrid.IconColor;
            }
            
            return _currentDefinition?.IconColor ?? Color.white;
        }
        
        #endregion
        
        #region Equipment Checking
        
        private bool HasShield()
        {
            if (_inventory == null) return false;
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            return leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield;
        }
        
        private bool HasRangedWeapon()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            return rightHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow ||
                   leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow;
        }
        
        private bool HasOffensiveStaff()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            return ArchetypeUtils.IsOffensiveStaff(rightHand) || ArchetypeUtils.IsOffensiveStaff(leftHand);
        }
        
        private bool HasMaceOrClub()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            
            if (rightHand?.m_shared == null) return false;
            return rightHand.m_shared.m_skillType == Skills.SkillType.Clubs;
        }
        
        private bool HasKnives()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            
            if (rightHand?.m_shared == null) return false;
            return rightHand.m_shared.m_skillType == Skills.SkillType.Knives;
        }
        
        private bool HasTwoHandedWeapon()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            
            if (rightHand?.m_shared == null) return false;
            return rightHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                   rightHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
        }
        
        private bool HasMeleeWeapon()
        {
            if (_inventory == null) return false;
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            
            if (rightHand?.m_shared == null) return false;
            return rightHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                   rightHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                   rightHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
        }
        
        #endregion
    }
}
