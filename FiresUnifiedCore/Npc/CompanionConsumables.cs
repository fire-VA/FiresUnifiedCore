using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc
{
    /// <summary>
    /// Handles automatic consumption of food and healing items for companion NPCs.
    /// Companions will eat food for buffs and use healing items when low on health.
    /// Food now provides immediate health gain in addition to buffs.
    /// </summary>
    public class CompanionConsumables : MonoBehaviour
    {
   [Header("Consumption Settings")]
        public float healthThresholdForHealing = 0.5f;  // Use healing items below 50% health
 public float foodCheckInterval = 5f;      // Check for food needs every 5 seconds
    public float healingCheckInterval = 2f;  // Check for healing needs every 2 seconds
        public int maxFoodItems = 3; // Max food buffs at once (like player)

        [Header("Debug")]
        public static bool VerboseLogging = false;

        private CompanionController _companion;
        private CompanionInventory _inventory;
        private CompanionStats _stats;
        private Character _character;
        private ZNetView _nview;

        // Active food effects
        private List<FoodEffect> _activeFoodEffects = new List<FoodEffect>();
    
        // Timing
        private float _lastFoodCheck;
        private float _lastHealingCheck;
        private float _lastDecayTick;

        // Vanilla Player.UpdateFood: each food's health/stamina/eitr is scaled by Pow(time left / burn time, 0.3).
        private const float FoodDecayExponent = 0.3f;
        private const float FoodDecayRecalcSeconds = 1f;
        
        // Rate-limited logging for GetFoodHealthBonus
        private float _lastFoodBonusLogTime;
        private const float FoodBonusLogInterval = 5f;

        // Cached stats from food
        private float _foodHealthBonus;
        private float _foodStaminaBonus;
        private float _foodEitrBonus;
     private float _foodHealthRegen;
      
        // Base character stats
        private float _baseMaxHealth = 100f;

    #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
            _stats = GetComponent<CompanionStats>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }

        private void Start()
        {
            // Cache base max health - use a consistent default for companions
            // The prefab should be configured with 200 HP base
            // We'll get the actual value from CompanionStats if available
            if (_stats != null && _stats.baseMaxHealth >= 50)
            {
                _baseMaxHealth = _stats.baseMaxHealth;
            }
            else if (_character != null)
            {
                float charMax = _character.GetMaxHealth();
                // Use character max if reasonable, otherwise default to 200
                _baseMaxHealth = charMax >= 50 ? charMax : 200f;
            }
            else
            {
                _baseMaxHealth = 200f;
            }
            
            if (VerboseLogging)
                Debug.Log($"[CompanionConsumables] Base max health: {_baseMaxHealth}");
            
            // Defer loading to ensure ZDO is ready
            StartCoroutine(DeferredLoad());
        }
        
        private System.Collections.IEnumerator DeferredLoad()
        {
            // Wait one frame for all components to initialize
            yield return null;
            
            // Wait another frame to ensure ZDO data is available
            yield return null;
            
            // Load food effects from ZDO (will also recalculate bonuses and update max health)
            LoadFromZDO();
            
            if (VerboseLogging && _activeFoodEffects.Count > 0)
            {
                Debug.Log($"[CompanionConsumables] Loaded {_activeFoodEffects.Count} food effect(s) from ZDO");
            }
        }

        private void Update()
        {
   if (!_companion?.isTamed ?? true) return;
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
if (_companion.isDefeated) return;

   float time = Time.time;

     // Update active food effects
         UpdateFoodEffects(time);

       // Check for food consumption
            if (time - _lastFoodCheck >= foodCheckInterval)
            {
           _lastFoodCheck = time;
       TryConsumeFood();
       }

  // Check for healing consumption
  if (time - _lastHealingCheck >= healingCheckInterval)
            {
       _lastHealingCheck = time;
     TryConsumeHealing();
  }
        }

        #endregion

        #region Food Consumption

        private void TryConsumeFood()
        {
            if (_inventory == null) return;

  // Get storage inventory
            var storageInv = _inventory.GetStorageInventory();
      if (storageInv == null) return;

            // Find food items in storage
         var items = storageInv.GetAllItems();
          foreach (var item in items.ToList())
      {
       if (!IsFood(item)) continue;
                if (!CanEat(item)) continue;

        // Consume the food
                ConsumeFood(item, storageInv);
      }
        }

        /// <summary>Vanilla Player.CanEat: the same food again only past half its time, otherwise a free slot or a
        /// food that is itself past half.</summary>
        private bool CanEat(ItemDrop.ItemData item)
        {
            foreach (var effect in _activeFoodEffects)
            {
                if (effect.FoodName == item.m_shared.m_name) return CanEatAgain(effect);
            }
            foreach (var effect in _activeFoodEffects)
            {
                if (CanEatAgain(effect)) return true;
            }
            return _activeFoodEffects.Count < maxFoodItems;
        }

        private static bool CanEatAgain(FoodEffect effect) => effect.Duration > 0f && effect.TimeRemaining < effect.Duration / 2f;

        /// <summary>Vanilla Player.GetMostDepletedFood: the emptiest food that may be replaced.</summary>
        private FoodEffect MostDepletedFood()
        {
            FoodEffect depleted = null;
            foreach (var effect in _activeFoodEffects)
            {
                if (CanEatAgain(effect) && (depleted == null || effect.TimeRemaining < depleted.TimeRemaining)) depleted = effect;
            }
            return depleted;
        }

        private bool ConsumeFood(ItemDrop.ItemData item, Inventory storageInv)
        {
   if (item == null) return false;

            var shared = item.m_shared;
    if (shared == null) return false;

       // Create food effect
  var effect = new FoodEffect
            {
FoodName = shared.m_name,
    HealthBonus = shared.m_food,
  StaminaBonus = shared.m_foodStamina,
    EitrBonus = shared.m_foodEitr,
         HealthRegen = shared.m_foodRegen,
        Duration = shared.m_foodBurnTime,
                TimeRemaining = shared.m_foodBurnTime,
        StartTime = Time.time
            };

            // Vanilla: eating the same food restarts it, and a full belly drops the emptiest food first.
            var existing = _activeFoodEffects.Find(active => active.FoodName == shared.m_name);
            if (existing != null) _activeFoodEffects.Remove(existing);
            else if (_activeFoodEffects.Count >= maxFoodItems)
            {
                var depleted = MostDepletedFood();
                if (depleted == null) return false;
                _activeFoodEffects.Remove(depleted);
            }

          _activeFoodEffects.Add(effect);
       RecalculateFoodBonuses();

          // IMMEDIATE HEALTH GAIN from eating food
            if (_character != null && effect.HealthBonus > 0)
{
           // Food gives immediate health equal to a portion of its health bonus
       float immediateHeal = effect.HealthBonus * 0.25f;  // 25% of food health as immediate heal
        _character.Heal(immediateHeal, true);
      
     if (VerboseLogging)
 Debug.Log($"[CompanionConsumables] Immediate heal from food: {immediateHeal:F0} HP");
        }

   // Update max health based on food
            UpdateMaxHealth();

    // Remove one from stack
       if (item.m_stack > 1)
  {
   item.m_stack--;
     }
        else
    {
      storageInv.RemoveItem(item);
        }

   // Save inventory changes
            _inventory.SaveToZDO();

        if (VerboseLogging)
          {
      string foodName = Localization.instance?.Localize(shared.m_name) ?? shared.m_name;
 Debug.Log($"[CompanionConsumables] {_companion.companionName} ate {foodName}:");
        Debug.Log($"  - Health Bonus: +{effect.HealthBonus:F0}");
        Debug.Log($"  - Stamina Bonus: +{effect.StaminaBonus:F0}");
 Debug.Log($"  - Health Regen: {effect.HealthRegen:F1}/tick");
       Debug.Log($"  - Duration: {effect.Duration:F0}s");
         Debug.Log($"  - New Max Health: {GetMaxHealth():F0}");
   }

            // Show feedback
            ShowConsumptionEffect(item);

            return true;
        }

        private void UpdateFoodEffects(float currentTime)
     {
    bool changed = false;

    for (int i = _activeFoodEffects.Count - 1; i >= 0; i--)
       {
     var effect = _activeFoodEffects[i];
      float elapsed = currentTime - effect.StartTime;
     effect.TimeRemaining = effect.Duration - elapsed;

                if (effect.TimeRemaining <= 0)
 {
         _activeFoodEffects.RemoveAt(i);
            changed = true;

     if (VerboseLogging)
             {
           string foodName = Localization.instance?.Localize(effect.FoodName) ?? effect.FoodName;
            Debug.Log($"[CompanionConsumables] {_companion.companionName}'s {foodName} effect expired");
             }
                }
            }

      if (changed)
         {
     RecalculateFoodBonuses();
           UpdateMaxHealth();
     }
            else if (_activeFoodEffects.Count > 0 && currentTime - _lastDecayTick >= FoodDecayRecalcSeconds)
            {
                _lastDecayTick = currentTime;
                float health = _foodHealthBonus, stamina = _foodStaminaBonus, eitr = _foodEitrBonus;
                RecalculateFoodBonuses();
                if (Mathf.Abs(health - _foodHealthBonus) >= 1f || Mathf.Abs(stamina - _foodStaminaBonus) >= 1f || Mathf.Abs(eitr - _foodEitrBonus) >= 1f)
                    UpdateMaxHealth();
            }
            // Food regen is applied by CompanionStats' regeneration, at vanilla's rate.
     }

     private void RecalculateFoodBonuses()
        {
 _foodHealthBonus = 0f;
            _foodStaminaBonus = 0f;
    _foodEitrBonus = 0f;
       _foodHealthRegen = 0f;

  foreach (var effect in _activeFoodEffects)
         {
            float strength = effect.Duration > 0f ? Mathf.Pow(Mathf.Clamp01(effect.TimeRemaining / effect.Duration), FoodDecayExponent) : 1f;
        _foodHealthBonus += effect.HealthBonus * strength;
       _foodStaminaBonus += effect.StaminaBonus * strength;
         _foodEitrBonus += effect.EitrBonus * strength;
            _foodHealthRegen += effect.HealthRegen;
   }

     if (VerboseLogging && _activeFoodEffects.Count > 0)
     {
       Debug.Log($"[CompanionConsumables] Food bonuses recalculated:");
    Debug.Log($"  - Total HP Bonus: +{_foodHealthBonus:F0}");
        Debug.Log($"  - Total Stamina Bonus: +{_foodStaminaBonus:F0}");
      Debug.Log($"  - Total Regen: {_foodHealthRegen:F1} per {FoodRegenIntervalSeconds:F0}s");
       }
     }

        /// <summary>
        /// Updates the character's max health based on food bonuses.
        /// Now notifies CompanionStats to recalculate (which includes attribute bonuses).
        /// </summary>
        private void UpdateMaxHealth()
  {
            // Notify CompanionStats to recalculate - it will include food bonuses
            if (_stats != null)
            {
                _stats.RecalculateMaxStats();
                return;
            }
            
            // Fallback if no CompanionStats (shouldn't happen for tamed companions)
            if (_character == null) return;

            float newMaxHealth = _baseMaxHealth + _foodHealthBonus;
    
     // Only update if actually changed
   float currentMax = _character.GetMaxHealth();
         if (Mathf.Abs(currentMax - newMaxHealth) > 0.5f)
            {
      _character.SetMaxHealth(newMaxHealth);
           
                if (VerboseLogging)
        Debug.Log($"[CompanionConsumables] Max health updated: {currentMax:F0} -> {newMaxHealth:F0}");
    }
        }

  #endregion

        #region Healing Consumption

        // What a mead is worth right now. Healing while hurt outranks resisting what is hurting the companion,
        // which outranks topping up stamina or eitr, which outranks an attack buff in a fight.
        private const float HealPriority = 4000f;
        private const float ResistPriority = 3000f;
        private const float PoolPriority = 2000f;
        private const float RegenPriority = 1000f;
        private const float AttackBuffPriority = 500f;
        private const float StaminaThresholdForMead = 0.3f;
        private const float EitrThresholdForMead = 0.3f;
        private const float RecentDamageSeconds = 10f;

        private void TryConsumeHealing()
        {
            if (_inventory == null || _character == null) return;

            var storageInv = _inventory.GetStorageInventory();
            var seman = _character.GetSEMan();
            if (storageInv == null || seman == null) return;

            ItemDrop.ItemData best = null;
            float bestScore = 0f;
            foreach (var item in storageInv.GetAllItems())
            {
                if (!(ConsumeEffect(item) is SE_Stats effect) || !CanConsume(seman, effect)) continue;
                float score = MeadScore(effect);
                if (score > bestScore)
                {
                    best = item;
                    bestScore = score;
                }
            }
            if (best != null) ConsumeHealingItem(best, storageInv, seman);
        }

        /// <summary>
        /// What this mead is worth to the companion right now; 0 means leave it in the bag. Read from the effect's own
        /// values, so any mead works: healing when hurt, a resistance against what is hurting it, stamina or eitr when
        /// low, and an attack buff while fighting.
        /// </summary>
        private float MeadScore(SE_Stats effect)
        {
            float score = 0f;

            if (_character.GetHealthPercentage() < healthThresholdForHealing)
            {
                float heal = effect.m_healthUpFront + effect.m_healthOverTime;
                if (heal > 0f) score += HealPriority + heal;
                else if (effect.m_healthRegenMultiplier > 1f) score += RegenPriority;
            }

            if (_stats != null && _stats.MaxStamina > 0f && _stats.CurrentStamina / _stats.MaxStamina < StaminaThresholdForMead)
            {
                if (effect.m_staminaUpFront + effect.m_staminaOverTime > 0f) score += PoolPriority;
                else if (effect.m_staminaRegenMultiplier > 1f) score += RegenPriority;
            }

            if (_stats != null && _stats.MaxEitr > 0f && _stats.CurrentEitr / _stats.MaxEitr < EitrThresholdForMead)
            {
                if (effect.m_eitrUpFront + effect.m_eitrOverTime > 0f) score += PoolPriority;
                else if (effect.m_eitrRegenMultiplier > 1f) score += RegenPriority;
            }

            if (effect.m_mods != null)
            {
                foreach (var mod in effect.m_mods)
                {
                    bool resists = mod.m_modifier == HitData.DamageModifier.Resistant
                        || mod.m_modifier == HitData.DamageModifier.VeryResistant
                        || mod.m_modifier == HitData.DamageModifier.Immune;
                    if (resists && IsThreatenedBy(mod.m_type)) score += ResistPriority;
                }
            }

            bool inCombat = _companion?.GetCombatMovement()?.IsInCombat ?? false;
            if (inCombat && (effect.m_damageModifier > 1f || effect.m_modifyAttackSkill != Skills.SkillType.None)) score += AttackBuffPriority;

            return score;
        }

        /// <summary>True when the companion carries that damage type as a debuff or took it in the last few seconds.</summary>
        private bool IsThreatenedBy(HitData.DamageType type)
        {
            var seman = _character.GetSEMan();
            if (seman != null)
            {
                if (type == HitData.DamageType.Fire && seman.HaveStatusEffect(SEMan.s_statusEffectBurning)) return true;
                if (type == HitData.DamageType.Frost && seman.HaveStatusEffect(SEMan.s_statusEffectFrost)) return true;
                if (type == HitData.DamageType.Poison && seman.HaveStatusEffect(SEMan.s_statusEffectPoison)) return true;
            }

            var combat = _companion?.GetComponent<CompanionCombat>();
            if (combat == null || combat.LastDamageTakenTime < 0f || Time.time - combat.LastDamageTakenTime > RecentDamageSeconds) return false;
            var taken = combat.LastDamageTaken;
            switch (type)
            {
                case HitData.DamageType.Blunt: return taken.m_blunt > 0f;
                case HitData.DamageType.Slash: return taken.m_slash > 0f;
                case HitData.DamageType.Pierce: return taken.m_pierce > 0f;
                case HitData.DamageType.Fire: return taken.m_fire > 0f;
                case HitData.DamageType.Frost: return taken.m_frost > 0f;
                case HitData.DamageType.Lightning: return taken.m_lightning > 0f;
                case HitData.DamageType.Poison: return taken.m_poison > 0f;
                case HitData.DamageType.Spirit: return taken.m_spirit > 0f;
                default: return false;
            }
        }

        /// <summary>Drinks a mead the way a player does: its status effect goes on the companion's SEMan.</summary>
        private void ConsumeHealingItem(ItemDrop.ItemData item, Inventory storageInv, SEMan seman)
        {
            seman.AddStatusEffect(item.m_shared.m_consumeStatusEffect, true);

            if (item.m_stack > 1) item.m_stack--;
            else storageInv.RemoveItem(item);
            _inventory.SaveToZDO();

            if (VerboseLogging)
                Debug.Log($"[CompanionConsumables] {_companion.companionName} drank {Localization.instance?.Localize(item.m_shared.m_name) ?? item.m_shared.m_name}");
            ShowConsumptionEffect(item);
        }

 #endregion

        #region Item Classification

     private bool IsFood(ItemDrop.ItemData item)
   {
            if (item?.m_shared == null) return false;
    
     var shared = item.m_shared;
  
            // Check if it's a consumable with food stats
     if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
     
// Has food values (health, stamina, or eitr bonus)
       return shared.m_food > 0 || shared.m_foodStamina > 0 || shared.m_foodEitr > 0;
        }

        /// <summary>A non-food consumable's status effect (meads); null for anything else.</summary>
        private StatusEffect ConsumeEffect(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable || IsFood(item)) return null;
            return item.m_shared.m_consumeStatusEffect;
        }

        /// <summary>Vanilla Player.CanConsumeItem: not while the same effect, or one of its category, is active.</summary>
        private static bool CanConsume(SEMan seman, StatusEffect effect) =>
            !seman.HaveStatusEffect(effect.NameHash()) && !seman.HaveStatusEffectCategory(effect.m_category);

     #endregion

        #region Stats API

        /// <summary>
        /// Gets the health bonus from active food effects.
        /// </summary>
        public float GetFoodHealthBonus()
        {
            // Rate-limited logging - only log once when first called, then every 5 seconds
            if (VerboseLogging && _foodHealthBonus > 0 && Time.time - _lastFoodBonusLogTime >= FoodBonusLogInterval)
            {
                _lastFoodBonusLogTime = Time.time;
                Debug.Log($"[CompanionConsumables] GetFoodHealthBonus: returning {_foodHealthBonus:F0} (from {_activeFoodEffects.Count} active effects)");
            }
            return _foodHealthBonus;
        }

        /// <summary>
        /// Gets the stamina bonus from active food effects.
    /// </summary>
  public float GetFoodStaminaBonus() => _foodStaminaBonus;

        /// <summary>
  /// Gets the eitr bonus from active food effects.
        /// </summary>
        public float GetFoodEitrBonus() => _foodEitrBonus;

 /// <summary>
   /// Gets the health regen rate from active food effects.
    /// </summary>
        public float GetFoodHealthRegen() => _foodHealthRegen;

        /// <summary>Vanilla heals the food regen total once every this many seconds (Player.UpdateFood).</summary>
        public const float FoodRegenIntervalSeconds = 10f;

        /// <summary>Food regeneration in health per second.</summary>
        public float GetFoodHealthRegenPerSecond() => _foodHealthRegen / FoodRegenIntervalSeconds;

        /// <summary>
        /// Gets the base max health (without food bonuses).
        /// </summary>
      public float GetBaseMaxHealth() => _baseMaxHealth;

        /// <summary>
        /// Gets the max health including food bonuses.
   /// </summary>
  public float GetMaxHealth()
        {
            return _baseMaxHealth + _foodHealthBonus;
        }

        /// <summary>
        /// Gets the current health of the companion.
   /// </summary>
        public float GetCurrentHealth()
   {
            return _character?.GetHealth() ?? 0f;
  }

        /// <summary>
        /// Gets the current health percentage (0-1).
        /// </summary>
     public float GetHealthPercentage()
        {
     if (_character == null) return 1f;
            float maxHealth = GetMaxHealth();
         if (maxHealth <= 0) return 1f;
         return Mathf.Clamp01(_character.GetHealth() / maxHealth);
        }

        /// <summary>
        /// Gets a list of active food effects.
        /// </summary>
     public List<FoodEffect> GetActiveFoodEffects() => new List<FoodEffect>(_activeFoodEffects);

        /// <summary>
     /// Gets the number of active food effects.
      /// </summary>
   public int GetActiveFoodCount() => _activeFoodEffects.Count;

        #endregion

        #region Visual Feedback

        private const string EatSound = "sfx_eat";

        /// <summary>The player's eating sound (Player.m_consumeItemEffects is sfx_eat only; vanilla has no eating visual).
        /// Owner-only code, so one networked copy, and only near a player.</summary>
        private void ShowConsumptionEffect(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return;
            Archetypes.AbilityFXManager.SpawnSound(EatSound, transform.position + Vector3.up * 1.5f);
        }

        #endregion

        #region Persistence

public void SaveToZDO()
        {
        // Skip during local player respawn / loading-screen — ZDO writes during
        // IsTeleporting=true deadlock the zone stream.
        if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

        var zdo = _nview?.GetZDO();
            if (zdo == null) return;

         try
   {
             var foodData = new List<string>();
    foreach (var effect in _activeFoodEffects)
       {
          foodData.Add($"{effect.FoodName}:{effect.HealthBonus}:{effect.StaminaBonus}:{effect.EitrBonus}:{effect.HealthRegen}:{effect.Duration}:{effect.TimeRemaining}");
     }

            zdo.Set("companion_food_effects", string.Join(";", foodData));
        }
            catch (System.Exception ex)
       {
        Debug.LogWarning($"[CompanionConsumables] Failed to save: {ex.Message}");
            }
        }

  public void LoadFromZDO()
        {
    var zdo = _nview?.GetZDO();
            if (zdo == null) return;

   try
    {
         _activeFoodEffects.Clear();

      string foodData = zdo.GetString("companion_food_effects", "");
         if (string.IsNullOrEmpty(foodData)) return;

     var parts = foodData.Split(';');
                float currentTime = Time.time;

    foreach (var part in parts)
    {
           if (string.IsNullOrEmpty(part)) continue;

        var values = part.Split(':');
         if (values.Length < 7) continue;

   var effect = new FoodEffect
         {
          FoodName = values[0],
   HealthBonus = float.Parse(values[1]),
        StaminaBonus = float.Parse(values[2]),
                EitrBonus = float.Parse(values[3]),
           HealthRegen = float.Parse(values[4]),
 Duration = float.Parse(values[5]),
     TimeRemaining = float.Parse(values[6]),
       StartTime = currentTime - (float.Parse(values[5]) - float.Parse(values[6]))
           };

          if (effect.TimeRemaining > 0)
          {
             _activeFoodEffects.Add(effect);
   }
   }

        RecalculateFoodBonuses();
     UpdateMaxHealth();
       }
       catch (System.Exception ex)
            {
  Debug.LogWarning($"[CompanionConsumables] Failed to load: {ex.Message}");
          }
        }

     #endregion

        #region Helper Types

    /// <summary>
      /// Represents an active food effect on the companion.
     /// </summary>
    public class FoodEffect
        {
         public string FoodName;
      public float HealthBonus;
       public float StaminaBonus;
  public float EitrBonus;
      public float HealthRegen;
 public float Duration;
   public float TimeRemaining;
            public float StartTime;
        }

        #endregion
    }
}
