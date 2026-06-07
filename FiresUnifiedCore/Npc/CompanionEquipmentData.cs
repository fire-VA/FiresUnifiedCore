using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc
{
    /// <summary>
    /// Centralized system for extracting, caching, and managing all equipment data from ItemDrop.ItemData.SharedData.
    /// This is the foundation for making companions work like real players - with proper set bonuses, armor, food, health, etc.
    /// 
    /// Data Sources:
    /// - Weapon: Attack timing, damage, block stats, animations
    /// - Armor: Defense, movement modifiers, resistances
    /// - Shield: Block power, parry bonus, deflection
    /// - Food: Health, stamina, eitr bonuses
    /// - Set Bonuses: Multi-piece equipment effects
    /// - Status Effects: Equipment-granted buffs
    /// </summary>
    public class CompanionEquipmentData : MonoBehaviour
    {
        // ============================================
  // CACHED WEAPON DATA
     // ============================================
        
        #region Weapon Data
    
        /// <summary>Current weapon's shared data reference.</summary>
        public ItemDrop.ItemData.SharedData WeaponShared { get; private set; }
        
      /// <summary>Current weapon item data.</summary>
        public ItemDrop.ItemData WeaponItem { get; private set; }
   
        /// <summary>Weapon prefab for visual/effect references.</summary>
        public GameObject WeaponPrefab { get; private set; }
        
        /// <summary>ItemDrop component from weapon prefab.</summary>
        public ItemDrop WeaponItemDrop { get; private set; }
        
        // Tool tier for mining/woodcutting - CRITICAL for companions to chop trees or mine
        public int ToolTier { get; private set; } = 0;
      
        // Attack Timing Data (from Attack object - CRITICAL for proper hit timing)
        public float AttackHitStartTick { get; private set; } = 0.3f;  // When hit detection starts (0-1 fraction of animation)
        public float AttackHitStopTick { get; private set; } = 0.5f;   // When hit detection stops
        public float AttackSpeed { get; private set; } = 1f;          // Animation speed multiplier
        public float AttackDuration { get; private set; } = 1f;       // Total animation duration in seconds
        
        // Attack Data - pulled directly from Attack object
        public Attack PrimaryAttack { get; private set; }
        public Attack SecondaryAttack { get; private set; }
        public string AttackAnimation { get; private set; } = "";
        public Attack.AttackType AttackType { get; private set; } = Attack.AttackType.Horizontal;
        public float AttackRange { get; private set; } = 2.5f;
        public float AttackAngle { get; private set; } = 90f;
        public float AttackStamina { get; private set; } = 20f;
        public float AttackEitr { get; private set; } = 0f;           // Eitr cost for magic attacks
        public float AttackHealth { get; private set; } = 0f;         // Health cost for blood magic
        public int AttackChainLevels { get; private set; } = 1;
        public int AttackRandomAnimations { get; private set; } = 1;
        
        // Combat modifiers from Attack object
        public float StaggerMultiplier { get; private set; } = 1f;    // How much stagger this weapon applies
        public float ForceMultiplier { get; private set; } = 1f;      // Knockback force multiplier
        public float DamageMultiplierPerMissingHP { get; private set; } = 0f;  // For blood magic weapons
        public float DamageMultiplierByTotalHealthMissing { get; private set; } = 0f;
        public float LowerDamagePerHit { get; private set; } = 1f;    // Damage reduction for multi-hit (scythe, etc.)
        public bool HitTerrain { get; private set; } = true;          // Can hit terrain/objects
        public bool HitFriendly { get; private set; } = false;        // Can hit friendly targets
        public float AttackHeight { get; private set; } = 0.6f;       // Height offset for attack origin
        public float AttackOffset { get; private set; } = 0f;         // Forward offset for attack origin
    
        // AI Settings (from weapon SharedData)
        public float AIAttackRange { get; private set; } = 2.5f;
        public float AIAttackRangeMin { get; private set; } = 0f;
        public float AIAttackInterval { get; private set; } = 2f;
        public float AIAttackMaxAngle { get; private set; } = 5f;
  
        // Bow/Crossbow Data
        public bool IsBowDraw { get; private set; }
        public float DrawDurationMin { get; private set; } = 1.5f;
        public float DrawStaminaDrain { get; private set; } = 0f;     // Stamina drain while drawing bow
        public float DrawEitrDrain { get; private set; } = 0f;        // Eitr drain while drawing (staves)
        public string DrawAnimationState { get; private set; } = "";
        public bool RequiresReload { get; private set; }
        public float ReloadTime { get; private set; } = 2f;
        public string ReloadAnimation { get; private set; } = "";
        public float ProjectileVelocity { get; private set; } = 50f;
        public float ProjectileVelocityMin { get; private set; } = 2f;
        public float ProjectileAccuracy { get; private set; } = 1f;   // 0 = inaccurate, higher = more accurate
        public float ProjectileAccuracyMin { get; private set; } = 20f;
        public bool LaunchAngle { get; private set; } = false;        // Whether projectile launches at an angle
        public GameObject AttackProjectile { get; private set; }
        
        // Spawn on hit data (for weapons that spawn effects/projectiles on hit)
        public GameObject SpawnOnHit { get; private set; }
        public GameObject SpawnOnHitTerrain { get; private set; }
        public float SpawnOnHitChance { get; private set; } = 1f;
        
        // Damage Data
        public HitData.DamageTypes WeaponDamage { get; private set; }
        public HitData.DamageTypes WeaponDamagePerLevel { get; private set; }
        public float AttackForce { get; private set; } = 30f;
        public float BackstabBonus { get; private set; } = 4f;
        public StatusEffect AttackStatusEffect { get; private set; }
        public float AttackStatusEffectChance { get; private set; } = 1f;
    
  // Animation State
        public ItemDrop.ItemData.AnimationState WeaponAnimationState { get; private set; } = ItemDrop.ItemData.AnimationState.Unarmed;
        public Skills.SkillType WeaponSkillType { get; private set; } = Skills.SkillType.Unarmed;
        
        #endregion
   
   // ============================================
        // CACHED SHIELD DATA
        // ============================================
        
        #region Shield Data
 
        /// <summary>Current shield's shared data reference.</summary>
        public ItemDrop.ItemData.SharedData ShieldShared { get; private set; }
   
        /// <summary>Current shield item data.</summary>
        public ItemDrop.ItemData ShieldItem { get; private set; }
        
    public float ShieldBlockPower { get; private set; } = 0f;
        public float ShieldBlockPowerPerLevel { get; private set; } = 0f;
        public float ShieldDeflectionForce { get; private set; } = 0f;
        public float ShieldTimedBlockBonus { get; private set; } = 1.5f;
        public StatusEffect ShieldPerfectBlockEffect { get; private set; }
        public List<HitData.DamageModPair> ShieldDamageModifiers { get; private set; } = new List<HitData.DamageModPair>();
        
        #endregion
        
        // ============================================
  // CACHED ARMOR DATA (aggregated from all pieces)
    // ============================================
    
        #region Armor Data
   
        /// <summary>Individual armor pieces for set bonus tracking.</summary>
        public Dictionary<ArmorSlot, CachedArmorData> ArmorPieces { get; private set; } = new Dictionary<ArmorSlot, CachedArmorData>();
        
      // Aggregated Stats
        public float TotalArmor { get; private set; } = 0f;
        public float TotalMovementModifier { get; private set; } = 0f;
   public float TotalEitrRegenModifier { get; private set; } = 0f;
        public float TotalJumpStaminaModifier { get; private set; } = 0f;
        public float TotalAttackStaminaModifier { get; private set; } = 0f;
        public float TotalBlockStaminaModifier { get; private set; } = 0f;
        public float TotalDodgeStaminaModifier { get; private set; } = 0f;
        public float TotalSwimStaminaModifier { get; private set; } = 0f;
        public float TotalSneakStaminaModifier { get; private set; } = 0f;
        public float TotalRunStaminaModifier { get; private set; } = 0f;
        
    // Aggregated Damage Modifiers (resistances/weaknesses)
     public Dictionary<HitData.DamageType, HitData.DamageModifier> DamageModifiers { get; private set; } = new Dictionary<HitData.DamageType, HitData.DamageModifier>();
 
        #endregion
        
        // ============================================
        // SET BONUS TRACKING
        // ============================================
    
        #region Set Bonuses
        
     /// <summary>Tracks equipped pieces per set name.</summary>
        public Dictionary<string, SetBonusInfo> ActiveSets { get; private set; } = new Dictionary<string, SetBonusInfo>();
        
   /// <summary>Currently active set effects.</summary>
        public List<StatusEffect> ActiveSetEffects { get; private set; } = new List<StatusEffect>();
        
        #endregion
        
        // ============================================
        // FOOD/CONSUMABLE DATA
        // ============================================
      
    #region Food Data
        
        /// <summary>Currently active food effects.</summary>
        public List<FoodData> ActiveFoods { get; private set; } = new List<FoodData>();
        
        /// <summary>Maximum number of food slots (like player).</summary>
        public int MaxFoodSlots { get; private set; } = 3;
        
        // Aggregated Food Stats
        public float FoodHealthBonus { get; private set; } = 0f;
        public float FoodStaminaBonus { get; private set; } = 0f;
  public float FoodEitrBonus { get; private set; } = 0f;
        public float FoodHealthRegen { get; private set; } = 0f;
        
        #endregion
        
      // ============================================
// COMPUTED STATS (final values used by companion)
  // ============================================
        
        #region Computed Stats
        
   /// <summary>Base health before food/bonuses.</summary>
   public float BaseHealth { get; set; } = 100f;
        
        /// <summary>Base stamina before food/bonuses.</summary>
        public float BaseStamina { get; set; } = 100f;
        
        /// <summary>Base eitr before food/bonuses.</summary>
        public float BaseEitr { get; set; } = 0f;
        
        /// <summary>Maximum health (base + food + equipment).</summary>
        public float MaxHealth => BaseHealth + FoodHealthBonus + GetEquipmentHealthBonus();
        
        /// <summary>Maximum stamina (base + food + equipment).</summary>
    public float MaxStamina => BaseStamina + FoodStaminaBonus + GetEquipmentStaminaBonus();
        
        /// <summary>Maximum eitr (base + food + equipment).</summary>
        public float MaxEitr => BaseEitr + FoodEitrBonus + GetEquipmentEitrBonus();
     
        /// <summary>Health regeneration per tick.</summary>
      public float HealthRegen => FoodHealthRegen + GetEquipmentHealthRegen();
        
        /// <summary>Final movement speed modifier.</summary>
        public float MovementSpeedModifier => TotalMovementModifier + GetEquipmentSpeedBonus() + GetProgressionSpeedBonus();
        
 #endregion
        
        // ============================================
 // STATUS EFFECTS FROM EQUIPMENT
        // ============================================
        
        #region Equipment Status Effects
        
   /// <summary>Status effects granted by equipped items.</summary>
        public List<StatusEffect> EquipmentStatusEffects { get; private set; } = new List<StatusEffect>();
        
  #endregion
        
 // ============================================
   // EVENTS
        // ============================================
        
        /// <summary>Fired when any equipment data changes.</summary>
  public event Action OnEquipmentChanged;

        /// <summary>Fired when food data changes.</summary>
        public event Action OnFoodChanged;
 
 /// <summary>Fired when stats need to be recalculated.</summary>
    public event Action OnStatsRecalculated;
        
        // ============================================
        // REFERENCES
        // ============================================
    
        private CompanionController _companion;
 private CompanionInventory _inventory;
        private CompanionSkills _skills;
        
    // Logging
 public static bool VerboseLogging = false;
        
   #region Unity Lifecycle
   
        private void Awake()
        {
        _companion = GetComponent<CompanionController>();
 _inventory = GetComponent<CompanionInventory>();
      _skills = GetComponent<CompanionSkills>();
        }
        
        private void Start()
        {
            // Initial data extraction
 RefreshAllEquipmentData();
        }
        
        #endregion
  
        // ============================================
   // PUBLIC API - Data Refresh
    // ============================================
        
        #region Public API
        
        /// <summary>
        /// Force refreshes all equipment data. Use after major changes like
        /// random loadout generation or model swaps.
        /// </summary>
        public void ForceRefresh()
        {
            // Re-acquire references in case they changed
            if (_inventory == null) _inventory = GetComponent<CompanionInventory>();
            if (_skills == null) _skills = GetComponent<CompanionSkills>();
            if (_companion == null) _companion = GetComponent<CompanionController>();
            
            RefreshAllEquipmentData();
        }
        
        /// <summary>
        /// Refreshes all equipment data from the inventory.
      /// Call this when any equipment changes.
   /// </summary>
        public void RefreshAllEquipmentData()
      {
            if (_inventory == null) return;
    
         RefreshWeaponData();
         RefreshShieldData();
         RefreshArmorData();
   RefreshSetBonuses();
            RefreshEquipmentStatusEffects();
            RecalculateAggregatedStats();

    OnEquipmentChanged?.Invoke();
     OnStatsRecalculated?.Invoke();
            
        if (VerboseLogging)
 {
                LogEquipmentSummary();
            }
  }
        
        /// <summary>
        /// Refreshes only weapon data.
        /// </summary>
        public void RefreshWeaponData()
        {
            ClearWeaponData();
 
            if (_inventory == null) return;
            
            // Check both hands for weapons
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
       
            ItemDrop.ItemData weapon = null;
      
            // Bows and TwoHandedWeaponLeft go in left hand
            if (leftHand != null && (IsBowItem(leftHand) || leftHand.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft))
            {
                weapon = leftHand;
            }
            else if (rightHand != null && rightHand.IsWeapon() && !IsShieldItem(rightHand))
            {
                weapon = rightHand;
            }
            else if (leftHand != null && leftHand.IsWeapon() && !IsShieldItem(leftHand))
            {
                // Only use left hand as weapon if it's not a shield
                weapon = leftHand;
            }
            
            if (weapon != null)
            {
                ExtractWeaponData(weapon);
            }
        }
    
        /// <summary>
  /// Refreshes only shield data.
        /// </summary>
        public void RefreshShieldData()
        {
      ClearShieldData();
         
  if (_inventory == null) return;
         
          var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            
            if (leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
ExtractShieldData(leftHand);
       }
        }
   
        /// <summary>
        /// Refreshes all armor piece data.
        /// </summary>
        public void RefreshArmorData()
 {
            ArmorPieces.Clear();
     
            if (_inventory == null) return;
            
   // Extract data from each armor slot
            ExtractArmorPiece(CompanionInventory.EquipmentSlot.Helmet, ArmorSlot.Helmet);
      ExtractArmorPiece(CompanionInventory.EquipmentSlot.Chest, ArmorSlot.Chest);
         ExtractArmorPiece(CompanionInventory.EquipmentSlot.Legs, ArmorSlot.Legs);
            ExtractArmorPiece(CompanionInventory.EquipmentSlot.Shoulder, ArmorSlot.Shoulder);
     ExtractArmorPiece(CompanionInventory.EquipmentSlot.Utility, ArmorSlot.Utility);
   }
     
    /// <summary>
        /// Consumes a food item, adding its effects.
        /// </summary>
    public bool ConsumeFood(ItemDrop.ItemData foodItem)
        {
            if (foodItem?.m_shared == null) return false;
            if (foodItem.m_shared.m_food <= 0 && foodItem.m_shared.m_foodStamina <= 0 && foodItem.m_shared.m_foodEitr <= 0)
      return false;
    
            // Check if we have room
            if (ActiveFoods.Count >= MaxFoodSlots)
            {
     // Remove oldest food
if (ActiveFoods.Count > 0)
            {
     ActiveFoods.RemoveAt(0);
          }
            }
      
    var foodData = new FoodData
            {
   ItemName = foodItem.m_shared.m_name,
           Health = foodItem.m_shared.m_food,
 Stamina = foodItem.m_shared.m_foodStamina,
          Eitr = foodItem.m_shared.m_foodEitr,
                Duration = foodItem.m_shared.m_foodBurnTime,
    HealthRegen = foodItem.m_shared.m_foodRegen,
         TimeConsumed = Time.time,
                StatusEffect = foodItem.m_shared.m_consumeStatusEffect
            };
            
 ActiveFoods.Add(foodData);
            RecalculateFoodStats();
         
            OnFoodChanged?.Invoke();
         OnStatsRecalculated?.Invoke();
            
         if (VerboseLogging)
   {
          Debug.Log($"[CompanionEquipmentData] Consumed food: {foodData.ItemName} - " +
       $"Health: +{foodData.Health}, Stamina: +{foodData.Stamina}, Duration: {foodData.Duration}s");
       }
       
 return true;
        }
     
        /// <summary>
        /// Updates food timers and removes expired foods.
        /// Call this from Update().
    /// </summary>
        public void UpdateFoodTimers()
        {
            bool changed = false;
     
            for (int i = ActiveFoods.Count - 1; i >= 0; i--)
  {
       var food = ActiveFoods[i];
          float elapsed = Time.time - food.TimeConsumed;
                
      if (elapsed >= food.Duration)
            {
       ActiveFoods.RemoveAt(i);
         changed = true;
     
     if (VerboseLogging)
                {
    Debug.Log($"[CompanionEquipmentData] Food expired: {food.ItemName}");
        }
     }
            }
         
      if (changed)
   {
            RecalculateFoodStats();
   OnFoodChanged?.Invoke();
                OnStatsRecalculated?.Invoke();
   }
    }
     
     /// <summary>
        /// Gets the remaining time for a food item.
        /// </summary>
        public float GetFoodRemainingTime(int index)
        {
  if (index < 0 || index >= ActiveFoods.Count) return 0f;
            
            var food = ActiveFoods[index];
   float elapsed = Time.time - food.TimeConsumed;
            return Mathf.Max(0f, food.Duration - elapsed);
        }
        
        /// <summary>
        /// Calculates damage reduction from armor.
        /// Uses the same formula as players.
        /// </summary>
    public float CalculateDamageReduction(float incomingDamage)
        {
      if (TotalArmor <= 0) return incomingDamage;
       
    // Valheim armor formula
 float armorFactor = TotalArmor / (TotalArmor + incomingDamage);
          return incomingDamage * (1f - armorFactor * 0.75f);
        }
        
 /// <summary>
        /// Gets the damage modifier for a specific damage type.
        /// </summary>
   public HitData.DamageModifier GetDamageModifier(HitData.DamageType damageType)
        {
     if (DamageModifiers.TryGetValue(damageType, out var modifier))
            {
            return modifier;
   }
return HitData.DamageModifier.Normal;
     }
        
        /// <summary>
        /// Gets the estimated hit delay for current weapon.
        /// This is when during the animation the damage should be dealt.
        /// </summary>
        public float GetWeaponHitDelay()
        {
            // SIMPLE APPROACH: Use fixed delays based on weapon type
            // These are tuned to match vanilla Valheim attack animations
            // The hit should occur when the weapon visually connects
            
            float hitDelay = WeaponAnimationState switch
            {
                ItemDrop.ItemData.AnimationState.Unarmed => 0.15f,      // Fists are quick
                ItemDrop.ItemData.AnimationState.Knives => 0.12f,       // Knives are very fast
                ItemDrop.ItemData.AnimationState.OneHanded => 0.2f,     // Swords/axes/maces
                ItemDrop.ItemData.AnimationState.TwoHandedClub => 0.5f, // Sledge has big windup
                ItemDrop.ItemData.AnimationState.TwoHandedAxe => 0.35f, // Battleaxe
                ItemDrop.ItemData.AnimationState.Greatsword => 0.3f,    // Greatsword
                ItemDrop.ItemData.AnimationState.Atgeir => 0.25f,       // Polearm thrust
                ItemDrop.ItemData.AnimationState.DualAxes => 0.2f,      // Dual axes
                ItemDrop.ItemData.AnimationState.Scythe => 0.35f,       // Scythe sweep
                ItemDrop.ItemData.AnimationState.Bow => 0.05f,          // Bow release is instant
                ItemDrop.ItemData.AnimationState.Crossbow => 0.1f,      // Crossbow trigger
                ItemDrop.ItemData.AnimationState.Staves => 0.2f,        // Staff cast
                ItemDrop.ItemData.AnimationState.MagicItem => 0.15f,    // Magic item
                ItemDrop.ItemData.AnimationState.Torch => 0.15f,        // Torch swing
                _ => 0.2f
            };
            
            // Apply speed factor - faster weapons hit sooner
            if (AttackSpeed > 1f)
            {
                hitDelay /= AttackSpeed;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionEquipmentData] GetWeaponHitDelay: {WeaponAnimationState} = {hitDelay:F3}s (speed={AttackSpeed})");
            }
            
            return Mathf.Max(0.05f, hitDelay);
        }

        /// <summary>
        /// Gets the estimated attack duration for current weapon.
        /// This determines how long the full attack animation takes.
        /// The attack cooldown will be at least this long.
        /// </summary>
   public float GetWeaponAttackDuration()
        {
      // Priority 1: Use the AI attack interval if it's greater than the animation time
      // This is the weapon's intended attack speed
       if (AIAttackInterval > 0.5f)
            {
           // The AI interval should represent the full attack cycle
     return AIAttackInterval;
            }
         
    // Priority 2: Calculate from attack stamina (higher stamina = slower weapon)
            if (AttackStamina > 0)
            {
     // Stamina cost roughly correlates with weapon heaviness/speed
        // Light weapons: 6-15 stamina
      // Medium weapons: 15-25 stamina
           // Heavy weapons: 25-40+ stamina
      float staminaBasedDuration = Mathf.Clamp(AttackStamina / 20f, 0.5f, 2.5f);
                return staminaBasedDuration;
            }
      
            // Priority 3: Estimate based on weapon animation state
       return WeaponAnimationState switch
         {
      ItemDrop.ItemData.AnimationState.Unarmed => 0.6f,
                ItemDrop.ItemData.AnimationState.Knives => 0.5f,
             ItemDrop.ItemData.AnimationState.OneHanded => 0.8f,
    ItemDrop.ItemData.AnimationState.TwoHandedClub => 1.8f,  // Sledge is slow
     ItemDrop.ItemData.AnimationState.TwoHandedAxe => 1.4f,
      ItemDrop.ItemData.AnimationState.Greatsword => 1.2f,
          ItemDrop.ItemData.AnimationState.Atgeir => 1.3f,
   ItemDrop.ItemData.AnimationState.DualAxes => 1.0f,
   ItemDrop.ItemData.AnimationState.Scythe => 1.4f,
             ItemDrop.ItemData.AnimationState.Bow => 1.5f,  // Draw + release
  ItemDrop.ItemData.AnimationState.Crossbow => 2.0f,  // Reload time
         ItemDrop.ItemData.AnimationState.Staves => 1.0f,
   ItemDrop.ItemData.AnimationState.MagicItem => 0.8f,
      ItemDrop.ItemData.AnimationState.Torch => 0.8f,
       _ => 1.0f
        };
     }
   
        /// <summary>
        /// Gets total block power including shield and skill.
        /// </summary>
        public float GetTotalBlockPower(float skillFactor = 0f)
   {
            float basePower = ShieldBlockPower;
      if (ShieldItem != null && ShieldItem.m_quality > 1)
            {
      basePower += ShieldBlockPowerPerLevel * (ShieldItem.m_quality - 1);
         }
            return basePower + (basePower * skillFactor * 0.5f);
        }
     
        /// <summary>
        /// Creates HitData for dealing damage with current weapon.
        /// Pulls all combat properties directly from the weapon's ItemDrop.ItemData.SharedData.
        /// </summary>
        public HitData CreateWeaponHitData(Character target, Character attacker, float damageMultiplier = 1f)
        {
         HitData hit = new HitData();
            hit.m_point = target.transform.position + Vector3.up;
 hit.m_dir = (target.transform.position - attacker.transform.position).normalized;
            hit.m_attacker = attacker.GetZDOID();
    hit.m_skill = WeaponSkillType;
            hit.m_toolTier = (short)ToolTier;  // Set tool tier for mining/chopping
    
      if (WeaponShared != null)
     {
          hit.m_damage = WeaponDamage.Clone();
        
  // Apply quality bonus (same formula as vanilla)
  if (WeaponItem != null && WeaponItem.m_quality > 1)
      {
                    // Add per-level damage for each quality level above 1
                    hit.m_damage.Add(WeaponDamagePerLevel, WeaponItem.m_quality - 1);
  }
  
    // Apply skill bonus (same formula as vanilla: 1 + skill * 0.005)
         if (_skills != null)
                {
           float skillLevel = _skills.GetSkillLevel(hit.m_skill);
             float skillBonus = 1f + (skillLevel * 0.005f);
   hit.m_damage.Modify(skillBonus);
       }
       
                // Apply progression attribute bonuses (Strength for melee, Intelligence for magic)
                var progression = _companion?.GetProgression();
                if (progression != null)
                {
                    float attrMultiplier = progression.GetCombinedDamageMultiplier(hit.m_skill, IsWeaponMagic());
                    hit.m_damage.Modify(attrMultiplier);
                }
                
                // Apply blood magic damage multipliers if applicable
                if (DamageMultiplierByTotalHealthMissing > 0 && attacker != null)
                {
                    float missingHealthPercent = 1f - attacker.GetHealthPercentage();
                    float bloodBonus = 1f + (missingHealthPercent * DamageMultiplierByTotalHealthMissing);
                    hit.m_damage.Modify(bloodBonus);
                }
                
                if (DamageMultiplierPerMissingHP > 0 && attacker != null)
                {
                    float missingHealth = attacker.GetMaxHealth() - attacker.GetHealth();
                    float bloodBonus = 1f + (missingHealth * DamageMultiplierPerMissingHP);
                    hit.m_damage.Modify(bloodBonus);
                }
      
         // Apply damage multiplier (for secondary attacks, etc.)
       if (damageMultiplier != 1f)
         {
         hit.m_damage.Modify(damageMultiplier);
        }
                
                // Set all combat properties from weapon data
   hit.m_pushForce = AttackForce * ForceMultiplier;
 hit.m_backstabBonus = BackstabBonus;
      hit.m_staggerMultiplier = StaggerMultiplier;
      hit.m_blockable = WeaponShared.m_blockable;
                hit.m_dodgeable = WeaponShared.m_dodgeable;
             hit.m_statusEffectHash = AttackStatusEffect?.NameHash() ?? 0;
             
                // Apply status effect chance
                if (AttackStatusEffect != null && AttackStatusEffectChance < 1f)
                {
                    // Only apply status effect based on chance
                    if (UnityEngine.Random.value > AttackStatusEffectChance)
                    {
                        hit.m_statusEffectHash = 0;
                    }
                }
       }
            else
        {
                // Unarmed
         hit.m_damage.m_blunt = 5f * damageMultiplier;
                hit.m_pushForce = 30f;
                hit.m_backstabBonus = 3f;
                hit.m_staggerMultiplier = 1f;
            }
            
    return hit;
     }
        
        #endregion
        
      // ============================================
        // PRIVATE - Data Extraction
        // ============================================
        
        #region Data Extraction
        
        private void ExtractWeaponData(ItemDrop.ItemData weapon)
 {
            if (weapon?.m_shared == null) return;
    
            WeaponItem = weapon;
         WeaponShared = weapon.m_shared;
            
 // Get prefab
  if (weapon.m_dropPrefab != null)
            {
                WeaponPrefab = weapon.m_dropPrefab;
     WeaponItemDrop = WeaponPrefab.GetComponent<ItemDrop>();
            }
        else if (!string.IsNullOrEmpty(weapon.m_shared.m_name) && ObjectDB.instance != null)
    {
    WeaponPrefab = ObjectDB.instance.GetItemPrefab(weapon.m_shared.m_name);
           if (WeaponPrefab != null)
             {
      WeaponItemDrop = WeaponPrefab.GetComponent<ItemDrop>();
                }
            }
          
        var shared = WeaponShared;
 
            // Animation state and skill
            WeaponAnimationState = shared.m_animationState;
       WeaponSkillType = shared.m_skillType;
       
            // Tool tier - CRITICAL for mining/chopping with pickaxes and axes
            ToolTier = shared.m_toolTier;

            // AI settings
      AIAttackRange = shared.m_aiAttackRange > 0 ? shared.m_aiAttackRange : 2.5f;
      AIAttackRangeMin = shared.m_aiAttackRangeMin;
            AIAttackInterval = shared.m_aiAttackInterval > 0 ? shared.m_aiAttackInterval : 2f;
      AIAttackMaxAngle = shared.m_aiAttackMaxAngle > 0 ? shared.m_aiAttackMaxAngle : 5f;
  
            // Damage data
     WeaponDamage = shared.m_damages;
        WeaponDamagePerLevel = shared.m_damagesPerLevel;
            AttackForce = shared.m_attackForce;
     BackstabBonus = shared.m_backstabBonus;
        AttackStatusEffect = shared.m_attackStatusEffect;
  AttackStatusEffectChance = shared.m_attackStatusEffectChance;
            
            // Primary attack data
         if (shared.m_attack != null)
    {
    PrimaryAttack = shared.m_attack;
                AttackAnimation = shared.m_attack.m_attackAnimation ?? "";
    AttackType = shared.m_attack.m_attackType;
     AttackStamina = shared.m_attack.m_attackStamina;
                AttackEitr = shared.m_attack.m_attackEitr;
                AttackHealth = shared.m_attack.m_attackHealth;
  AttackChainLevels = shared.m_attack.m_attackChainLevels > 0 ? shared.m_attack.m_attackChainLevels : 1;
            AttackRandomAnimations = shared.m_attack.m_attackRandomAnimations > 0 ? shared.m_attack.m_attackRandomAnimations : 1;
                
                // Combat modifiers - CRITICAL for proper damage application
                StaggerMultiplier = shared.m_attack.m_staggerMultiplier;
                ForceMultiplier = shared.m_attack.m_forceMultiplier;
                DamageMultiplierPerMissingHP = shared.m_attack.m_damageMultiplierPerMissingHP;
                DamageMultiplierByTotalHealthMissing = shared.m_attack.m_damageMultiplierByTotalHealthMissing;
                LowerDamagePerHit = shared.m_attack.m_lowerDamagePerHit ? 0.75f : 1f;  // Boolean - reduces damage if true
                HitTerrain = shared.m_attack.m_hitTerrain;
                HitFriendly = shared.m_attack.m_hitFriendly;
                AttackHeight = shared.m_attack.m_attackHeight;
                AttackOffset = shared.m_attack.m_attackOffset;
                
                
                // Spawn on hit data
                SpawnOnHit = shared.m_attack.m_spawnOnHit;
                SpawnOnHitChance = shared.m_attack.m_spawnOnHitChance;
                SpawnOnHitTerrain = shared.m_spawnOnHitTerrain;  // This is on SharedData, not Attack
                
                
                // Extract attack speed factor if available
                AttackSpeed = shared.m_attack.m_speedFactor > 0 ? shared.m_attack.m_speedFactor : 1f;
                
                // Calculate attack duration from animation state and speed
                float baseAnimDuration = GetBaseAnimationDuration(shared.m_animationState);
                AttackDuration = baseAnimDuration / AttackSpeed;
                
                // Estimate hit timing based on attack type
                // Most melee attacks hit around 30-50% through the animation
                AttackHitStartTick = GetEstimatedHitStartForAttackType(shared.m_attack.m_attackType, shared.m_animationState);
                AttackHitStopTick = AttackHitStartTick + 0.15f; // Hit window is ~15% of animation
       
       // For ranged weapons (bow/crossbow), use AIAttackRange as the primary range
        // m_attackRange is typically 0 or very small for ranged weapons
   bool isRangedWeapon = shared.m_attack.m_bowDraw || 
    shared.m_attack.m_requiresReload ||
        shared.m_skillType == Skills.SkillType.Bows ||
          shared.m_skillType == Skills.SkillType.Crossbows;
    
        if (isRangedWeapon)
                {
   // Ranged weapons: use AI attack range, default to 25 if not set
             AttackRange = AIAttackRange > 5f ? AIAttackRange : 25f;
                }
 else if (shared.m_attack.m_attackRange > 0)
                {
        // Melee weapons: use attack range from the attack data
      AttackRange = shared.m_attack.m_attackRange;
     }
    else
            {
            // Fallback: use AI attack range or default
AttackRange = AIAttackRange > 0 ? AIAttackRange : 2.5f;
        }
        
     if (shared.m_attack.m_attackAngle > 0)
      {
      AttackAngle = shared.m_attack.m_attackAngle;
             }
                
                // Bow data
  IsBowDraw = shared.m_attack.m_bowDraw;
            DrawDurationMin = shared.m_attack.m_drawDurationMin > 0 ? shared.m_attack.m_drawDurationMin : 1.5f;
                DrawStaminaDrain = shared.m_attack.m_drawStaminaDrain;
                DrawEitrDrain = shared.m_attack.m_drawEitrDrain;
         DrawAnimationState = shared.m_attack.m_drawAnimationState ?? "";
             
                // Crossbow data
   RequiresReload = shared.m_attack.m_requiresReload;
                ReloadTime = shared.m_attack.m_reloadTime > 0 ? shared.m_attack.m_reloadTime : 2f;
       ReloadAnimation = shared.m_attack.m_reloadAnimation ?? "";
        
   // Projectile data
ProjectileVelocity = shared.m_attack.m_projectileVel > 0 ? shared.m_attack.m_projectileVel : 50f;
                ProjectileVelocityMin = shared.m_attack.m_projectileVelMin;
                ProjectileAccuracy = shared.m_attack.m_projectileAccuracy;
                ProjectileAccuracyMin = shared.m_attack.m_projectileAccuracyMin;
                LaunchAngle = shared.m_attack.m_launchAngle > 0;  // True if projectile launches at an angle
       AttackProjectile = shared.m_attack.m_attackProjectile;
          }
 
            // Secondary attack
        SecondaryAttack = shared.m_secondaryAttack;
            
            if (VerboseLogging)
   {
        Debug.Log($"[CompanionEquipmentData] Weapon loaded: {shared.m_name}, " +
                    $"AnimState: {WeaponAnimationState}, Skill: {WeaponSkillType}, ToolTier: {ToolTier}, " +
                    $"Range: {AttackRange:F1}, Damage: {WeaponDamage.GetTotalDamage():F1}, " +
                    $"Stamina: {AttackStamina:F0}, Stagger: {StaggerMultiplier:F2}");
            }
        }
        
        private void ExtractShieldData(ItemDrop.ItemData shield)
        {
     if (shield?.m_shared == null) return;
            
ShieldItem = shield;
            ShieldShared = shield.m_shared;
       
     var shared = ShieldShared;
            
            ShieldBlockPower = shared.m_blockPower;
     ShieldBlockPowerPerLevel = shared.m_blockPowerPerLevel;
 ShieldDeflectionForce = shared.m_deflectionForce;
 ShieldTimedBlockBonus = shared.m_timedBlockBonus > 0 ? shared.m_timedBlockBonus : 1f;
      ShieldPerfectBlockEffect = shared.m_perfectBlockStatusEffect;
 ShieldDamageModifiers = shared.m_damageModifiers ?? new List<HitData.DamageModPair>();
     
    if (VerboseLogging)
      {
          Debug.Log($"[CompanionEquipmentData] Shield loaded: {shared.m_name}, " +
 $"BlockPower: {ShieldBlockPower}, ParryBonus: {ShieldTimedBlockBonus}x");
        }
        }
        
        private void ExtractArmorPiece(CompanionInventory.EquipmentSlot invSlot, ArmorSlot armorSlot)
   {
var item = _inventory.GetEquippedItem(invSlot);
   if (item?.m_shared == null) return;
            
            var shared = item.m_shared;
            
        var armorData = new CachedArmorData
     {
    Item = item,
     Shared = shared,
   Slot = armorSlot,
           
        // Stats
       Armor = shared.m_armor,
                ArmorPerLevel = shared.m_armorPerLevel,
      MovementModifier = shared.m_movementModifier,
  EitrRegenModifier = shared.m_eitrRegenModifier,
      
          // Stamina modifiers
  JumpStaminaModifier = shared.m_jumpStaminaModifier,
      AttackStaminaModifier = shared.m_attackStaminaModifier,
 BlockStaminaModifier = shared.m_blockStaminaModifier,
                DodgeStaminaModifier = shared.m_dodgeStaminaModifier,
            SwimStaminaModifier = shared.m_swimStaminaModifier,
SneakStaminaModifier = shared.m_sneakStaminaModifier,
                RunStaminaModifier = shared.m_runStaminaModifier,
        
      // Damage modifiers
         DamageModifiers = shared.m_damageModifiers ?? new List<HitData.DamageModPair>(),
       
          // Set info
 SetName = shared.m_setName,
           SetSize = shared.m_setSize,
           SetStatusEffect = shared.m_setStatusEffect,
           
          // Equipment effect
                EquipStatusEffect = shared.m_equipStatusEffect
 };
   
            // Calculate actual armor with quality
            if (item.m_quality > 1)
   {
      armorData.Armor += armorData.ArmorPerLevel * (item.m_quality - 1);
     }
            
 ArmorPieces[armorSlot] = armorData;
    }
        
        private void ClearWeaponData()
        {
      WeaponItem = null;
       WeaponShared = null;
       WeaponPrefab = null;
        WeaponItemDrop = null;
            PrimaryAttack = null;
       SecondaryAttack = null;
            AttackAnimation = "";
            AttackType = Attack.AttackType.Horizontal;
   AttackRange = 2.5f;
            AttackAngle = 90f;
        AttackStamina = 20f;
            AttackEitr = 0f;
            AttackHealth = 0f;
    AttackChainLevels = 1;
     AttackRandomAnimations = 1;
     
            // Reset tool tier
            ToolTier = 0;
            
            // Reset combat modifiers
            StaggerMultiplier = 1f;
            ForceMultiplier = 1f;
            DamageMultiplierPerMissingHP = 0f;
            DamageMultiplierByTotalHealthMissing = 0f;
            LowerDamagePerHit = 1f;
            HitTerrain = true;
            HitFriendly = false;
            AttackHeight = 0.6f;
            AttackOffset = 0f;
            
            // Reset spawn on hit
            SpawnOnHit = null;
            SpawnOnHitTerrain = null;
            SpawnOnHitChance = 1f;
            
            // Reset timing data
            AttackHitStartTick = 0.3f;
            AttackHitStopTick = 0.5f;
            AttackSpeed = 1f;
            AttackDuration = 1f;
            
            AIAttackRange = 2.5f;
AIAttackRangeMin = 0f;
            AIAttackInterval = 2f;
            AIAttackMaxAngle = 5f;
 IsBowDraw = false;
            DrawDurationMin = 1.5f;
            DrawStaminaDrain = 0f;
            DrawEitrDrain = 0f;
            DrawAnimationState = "";
            RequiresReload = false;
            ReloadTime = 2f;
            ReloadAnimation = "";
    ProjectileVelocity = 50f;
            ProjectileVelocityMin = 2f;
            ProjectileAccuracy = 1f;
            ProjectileAccuracyMin = 20f;
            LaunchAngle = false;
     AttackProjectile = null;
  WeaponDamage = default;
            WeaponDamagePerLevel = default;
            AttackForce = 30f;
    BackstabBonus = 4f;
      AttackStatusEffect = null;
    AttackStatusEffectChance = 1f;
      WeaponAnimationState = ItemDrop.ItemData.AnimationState.Unarmed;
         WeaponSkillType = Skills.SkillType.Unarmed;
        }
      
        private void ClearShieldData()
        {
         ShieldItem = null;
            ShieldShared = null;
      ShieldBlockPower = 0f;
ShieldBlockPowerPerLevel = 0f;
            ShieldDeflectionForce = 0f;
            ShieldTimedBlockBonus = 1.5f;
            ShieldPerfectBlockEffect = null;
            ShieldDamageModifiers.Clear();
        }
        
        #endregion
        
   // ============================================
     // PRIVATE - Stat Calculation
        // ============================================
  
     #region Stat Calculation
        
     private void RecalculateAggregatedStats()
        {
     // Reset aggregated stats
   TotalArmor = 0f;
            TotalMovementModifier = 0f;
            TotalEitrRegenModifier = 0f;
            TotalJumpStaminaModifier = 0f;
            TotalAttackStaminaModifier = 0f;
    TotalBlockStaminaModifier = 0f;
            TotalDodgeStaminaModifier = 0f;
            TotalSwimStaminaModifier = 0f;
   TotalSneakStaminaModifier = 0f;
            TotalRunStaminaModifier = 0f;
    DamageModifiers.Clear();
  
            // Aggregate from armor pieces
        foreach (var armor in ArmorPieces.Values)
            {
                TotalArmor += armor.Armor;
       TotalMovementModifier += armor.MovementModifier;
              TotalEitrRegenModifier += armor.EitrRegenModifier;
             TotalJumpStaminaModifier += armor.JumpStaminaModifier;
      TotalAttackStaminaModifier += armor.AttackStaminaModifier;
          TotalBlockStaminaModifier += armor.BlockStaminaModifier;
                TotalDodgeStaminaModifier += armor.DodgeStaminaModifier;
         TotalSwimStaminaModifier += armor.SwimStaminaModifier;
       TotalSneakStaminaModifier += armor.SneakStaminaModifier;
        TotalRunStaminaModifier += armor.RunStaminaModifier;
          
            // Aggregate damage modifiers
          foreach (var mod in armor.DamageModifiers)
   {
          if (DamageModifiers.ContainsKey(mod.m_type))
     {
         // Take the better modifier
            if (mod.m_modifier < DamageModifiers[mod.m_type])
{
             DamageModifiers[mod.m_type] = mod.m_modifier;
 }
            }
  else
    {
            DamageModifiers[mod.m_type] = mod.m_modifier;
          }
          }
            }
   
            // Add shield damage modifiers
  foreach (var mod in ShieldDamageModifiers)
        {
    if (DamageModifiers.ContainsKey(mod.m_type))
    {
          if (mod.m_modifier < DamageModifiers[mod.m_type])
  {
 DamageModifiers[mod.m_type] = mod.m_modifier;
 }
      }
    else
            {
     DamageModifiers[mod.m_type] = mod.m_modifier;
           }
            }

            // CRITICAL: Push the calculated armor into the Character's m_armorSkin field so
            // Valheim's own Character.Damage() path (which calls GetBodyArmor()) actually
            // reduces incoming damage. Without this, companions have 0 effective armor
            // even when the stats screen shows a positive value because our TotalArmor is
            // only tracked internally and never seen by the native damage pipeline.
            // We use m_armorSkin rather than equipping items on Humanoid slots because that
            // would require full item-slot management; m_armorSkin is additive with any
            // slot-equipped items so there is no double-counting.
            var character = GetComponent<Character>();
            if (character != null)
            {
                try
                {
                    var armorField = typeof(Character).GetField("m_armorSkin",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    armorField?.SetValue(character, TotalArmor);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionEquipmentData] Could not set m_armorSkin: {ex.Message}");
                }
            }
        }
    
        private void RefreshSetBonuses()
        {
    ActiveSets.Clear();
       ActiveSetEffects.Clear();
            
      // Count pieces per set
   foreach (var armor in ArmorPieces.Values)
         {
  if (string.IsNullOrEmpty(armor.SetName)) continue;
                
         if (!ActiveSets.ContainsKey(armor.SetName))
   {
            ActiveSets[armor.SetName] = new SetBonusInfo
            {
               SetName = armor.SetName,
             RequiredPieces = armor.SetSize,
         EquippedPieces = 0,
    SetEffect = armor.SetStatusEffect
      };
       }
  
         ActiveSets[armor.SetName].EquippedPieces++;
            }
  
       // Activate set bonuses where we have enough pieces
   foreach (var set in ActiveSets.Values)
 {
                if (set.EquippedPieces >= set.RequiredPieces && set.SetEffect != null)
    {
                    ActiveSetEffects.Add(set.SetEffect);
   
       if (VerboseLogging)
       {
         Debug.Log($"[CompanionEquipmentData] Set bonus activated: {set.SetName} ({set.EquippedPieces}/{set.RequiredPieces})");
         }
             }
            }
        }
      
private void RefreshEquipmentStatusEffects()
        {
            EquipmentStatusEffects.Clear();
            
       // Add equipment status effects from armor
            foreach (var armor in ArmorPieces.Values)
    {
                if (armor.EquipStatusEffect != null)
         {
         EquipmentStatusEffects.Add(armor.EquipStatusEffect);
    }
            }
   
 // Add weapon equipment effect if any
            if (WeaponShared?.m_equipStatusEffect != null)
          {
       EquipmentStatusEffects.Add(WeaponShared.m_equipStatusEffect);
     }
         
            // Add shield equipment effect if any
 if (ShieldShared?.m_equipStatusEffect != null)
     {
                EquipmentStatusEffects.Add(ShieldShared.m_equipStatusEffect);
   }
        }
      
        private void RecalculateFoodStats()
        {
    FoodHealthBonus = 0f;
        FoodStaminaBonus = 0f;
  FoodEitrBonus = 0f;
        FoodHealthRegen = 0f;
      
  foreach (var food in ActiveFoods)
      {
    // Calculate remaining percentage
           float elapsed = Time.time - food.TimeConsumed;
    float remaining = Mathf.Clamp01(1f - (elapsed / food.Duration));
      
         // Food effects decay over time (like player)
 FoodHealthBonus += food.Health * remaining;
      FoodStaminaBonus += food.Stamina * remaining;
    FoodEitrBonus += food.Eitr * remaining;
                FoodHealthRegen += food.HealthRegen;
     }
        }
        
        private float GetEquipmentHealthBonus()
        {
            float bonus = 0f;
 foreach (var effect in EquipmentStatusEffects)
            {
   if (effect is SE_Stats seStats)
 {
   bonus += seStats.m_addMaxCarryWeight; // This is actually health in some items
}
  }
      return bonus;
    }
        
        private float GetEquipmentStaminaBonus()
        {
            return 0f; // Most equipment doesn't add stamina directly
        }
        
        private float GetEquipmentEitrBonus()
        {
            return 0f; // Most equipment doesn't add eitr directly
        }
        
      private float GetEquipmentHealthRegen()
        {
     float bonus = 0f;
      foreach (var effect in EquipmentStatusEffects)
    {
                if (effect is SE_Stats seStats && seStats.m_healthRegenMultiplier != 1f)
  {
bonus += (seStats.m_healthRegenMultiplier - 1f);
                }
}
            return bonus;
      }
        
        private float GetEquipmentSpeedBonus()
     {
          float bonus = 0f;
            foreach (var effect in EquipmentStatusEffects)
    {
         if (effect is SE_Stats seStats)
           {
     bonus += seStats.m_speedModifier;
  }
        }
    return bonus;
        }
        
        private float GetProgressionSpeedBonus()
        {
            var progression = _companion?.GetProgression();
            if (progression == null) return 0f;
            
            // Speed attribute gives 0.5% bonus per point
            return (progression.GetSpeedMultiplier() - 1f);
        }
        
        #endregion
   
    // ============================================
        // HELPERS
     // ============================================
    
        #region Helpers
   
        private bool IsBowItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow ||
                   item.m_shared.m_skillType == Skills.SkillType.Bows;
        }
        
        /// <summary>
        /// Checks if an item is a shield.
        /// Shields should NOT be treated as weapons.
        /// </summary>
        private bool IsShieldItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
        }
        
        /// <summary>
        /// Gets the base animation duration for a weapon type.
        /// This is used with m_speedFactor to calculate actual animation time.
        /// </summary>
        private float GetBaseAnimationDuration(ItemDrop.ItemData.AnimationState animState)
        {
            return animState switch
            {
                ItemDrop.ItemData.AnimationState.Unarmed => 0.7f,
                ItemDrop.ItemData.AnimationState.OneHanded => 0.8f,
                ItemDrop.ItemData.AnimationState.TwoHandedClub => 1.8f,  // Sledge is slow
                ItemDrop.ItemData.AnimationState.TwoHandedAxe => 1.4f,
                ItemDrop.ItemData.AnimationState.Greatsword => 1.2f,
                ItemDrop.ItemData.AnimationState.Atgeir => 1.3f,
                ItemDrop.ItemData.AnimationState.Knives => 0.5f,
                ItemDrop.ItemData.AnimationState.DualAxes => 1.0f,
                ItemDrop.ItemData.AnimationState.Scythe => 1.4f,
                ItemDrop.ItemData.AnimationState.Bow => 1.5f,
                ItemDrop.ItemData.AnimationState.Crossbow => 2.0f,
                ItemDrop.ItemData.AnimationState.Staves => 1.0f,
                ItemDrop.ItemData.AnimationState.MagicItem => 0.8f,
                ItemDrop.ItemData.AnimationState.Torch => 0.8f,
                _ => 1.0f
            };
        }
        
        /// <summary>
        /// Estimates when during the animation the hit occurs (0-1 fraction).
        /// This is based on typical Valheim weapon animations.
        /// </summary>
        private float GetEstimatedHitStartForAttackType(Attack.AttackType attackType, ItemDrop.ItemData.AnimationState animState)
        {
            // Different attack types hit at different points in the animation
            // Horizontal swings hit earlier, overhead attacks hit later
            float baseHitTiming = attackType switch
            {
                Attack.AttackType.Horizontal => 0.35f,   // Side swings hit mid-swing
                Attack.AttackType.Vertical => 0.45f,     // Overhead attacks hit later
                Attack.AttackType.Projectile => 0.1f,    // Projectiles fire early
                Attack.AttackType.None => 0.35f,
                _ => 0.35f
            };
            
            // Adjust for weapon type - heavier weapons wind up longer
            float weaponAdjustment = animState switch
            {
                ItemDrop.ItemData.AnimationState.Knives => -0.1f,      // Knives are quick
                ItemDrop.ItemData.AnimationState.TwoHandedClub => 0.15f, // Sledge winds up long
                ItemDrop.ItemData.AnimationState.TwoHandedAxe => 0.1f,
                ItemDrop.ItemData.AnimationState.Greatsword => 0.05f,
                ItemDrop.ItemData.AnimationState.Atgeir => 0.05f,
                _ => 0f
            };
            
            return Mathf.Clamp(baseHitTiming + weaponAdjustment, 0.1f, 0.7f);
        }
  
    private void LogEquipmentSummary()
 {
         Debug.Log($"[CompanionEquipmentData] === Equipment Summary ===");
 Debug.Log($"  Weapon: {WeaponShared?.m_name ?? "None"} ({WeaponAnimationState})");
    Debug.Log($"  Shield: {ShieldShared?.m_name ?? "None"} (Block: {ShieldBlockPower})");
            Debug.Log($"  Total Armor: {TotalArmor:F1}");
      Debug.Log($"  Movement Mod: {TotalMovementModifier:F2}");
Debug.Log($"  Active Sets: {ActiveSetEffects.Count}");
   Debug.Log($"  Active Foods: {ActiveFoods.Count}");
         Debug.Log($"  Max Health: {MaxHealth:F0}, Max Stamina: {MaxStamina:F0}");
        }
        
        /// <summary>
        /// Returns true if the current weapon uses magic (staff/elemental/blood).
        /// </summary>
        private bool IsWeaponMagic()
        {
            if (WeaponSkillType == Skills.SkillType.ElementalMagic) return true;
            if (WeaponSkillType == Skills.SkillType.BloodMagic) return true;
            if (WeaponAnimationState == ItemDrop.ItemData.AnimationState.Staves) return true;
            if (WeaponAnimationState == ItemDrop.ItemData.AnimationState.MagicItem) return true;
            return false;
        }
        
   #endregion
        
        // ============================================
        // DATA CLASSES
        // ============================================
        
        public enum ArmorSlot
        {
  Helmet,
   Chest,
       Legs,
            Shoulder,
            Utility
     }
    
        public class CachedArmorData
      {
  public ItemDrop.ItemData Item;
          public ItemDrop.ItemData.SharedData Shared;
            public ArmorSlot Slot;
            
  public float Armor;
     public float ArmorPerLevel;
         public float MovementModifier;
            public float EitrRegenModifier;
      
  public float JumpStaminaModifier;
        public float AttackStaminaModifier;
 public float BlockStaminaModifier;
    public float DodgeStaminaModifier;
 public float SwimStaminaModifier;
   public float SneakStaminaModifier;
      public float RunStaminaModifier;
   
            public List<HitData.DamageModPair> DamageModifiers;
          
    public string SetName;
    public int SetSize;
            public StatusEffect SetStatusEffect;
            public StatusEffect EquipStatusEffect;
        }
        
     public class SetBonusInfo
        {
          public string SetName;
        public int RequiredPieces;
       public int EquippedPieces;
        public StatusEffect SetEffect;
            
    public bool IsActive => EquippedPieces >= RequiredPieces;
        }
        
        public class FoodData
      {
          public string ItemName;
            public float Health;
  public float Stamina;
      public float Eitr;
            public float Duration;
    public float HealthRegen;
    public float TimeConsumed;
   public StatusEffect StatusEffect;
          
        public float RemainingTime => Mathf.Max(0f, Duration - (Time.time - TimeConsumed));
  public float RemainingPercent => Mathf.Clamp01(RemainingTime / Duration);
        }
    }
}
