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
        private float _lastRegenTick;
        
        // Rate-limited logging for GetFoodHealthBonus
        private float _lastFoodBonusLogTime;
        private const float FOOD_BONUS_LOG_INTERVAL = 5f;

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

            // Check if we need more food (under max food items)
   if (_activeFoodEffects.Count >= maxFoodItems) return;

  // Get storage inventory
            var storageInv = _inventory.GetStorageInventory();
      if (storageInv == null) return;

            // Find food items in storage
         var items = storageInv.GetAllItems();
          foreach (var item in items.ToList())
      {
       if (!IsFood(item)) continue;

   // Check if we already have this food type active
         if (_activeFoodEffects.Any(f => f.FoodName == item.m_shared.m_name)) continue;

        // Consume the food
        if (ConsumeFood(item, storageInv))
   {
    if (_activeFoodEffects.Count >= maxFoodItems) break;
          }
      }
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

            // Apply health regen from food (every 1 second)
   if (currentTime - _lastRegenTick >= 1f)
{
     _lastRegenTick = currentTime;
     ApplyFoodHealthRegen();
         }
     }

     private void RecalculateFoodBonuses()
        {
 _foodHealthBonus = 0f;
            _foodStaminaBonus = 0f;
    _foodEitrBonus = 0f;
       _foodHealthRegen = 0f;

  foreach (var effect in _activeFoodEffects)
         {
        _foodHealthBonus += effect.HealthBonus;
       _foodStaminaBonus += effect.StaminaBonus;
         _foodEitrBonus += effect.EitrBonus;
            _foodHealthRegen += effect.HealthRegen;
   }

     if (VerboseLogging && _activeFoodEffects.Count > 0)
     {
       Debug.Log($"[CompanionConsumables] Food bonuses recalculated:");
    Debug.Log($"  - Total HP Bonus: +{_foodHealthBonus:F0}");
        Debug.Log($"  - Total Stamina Bonus: +{_foodStaminaBonus:F0}");
      Debug.Log($"  - Total Regen: {_foodHealthRegen:F1}/s");
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

        private void ApplyFoodHealthRegen()
      {
         if (_character == null || _foodHealthRegen <= 0) return;

  float currentHealth = _character.GetHealth();
            float maxHealth = GetMaxHealth();
        
        // Only heal if not at max health
     if (currentHealth < maxHealth)
        {
      // Regen is per tick (1 second)
          _character.Heal(_foodHealthRegen, false);  // false = don't show text spam
     }
        }

  #endregion

        #region Healing Consumption

        private void TryConsumeHealing()
 {
            if (_inventory == null || _character == null) return;

   // Check if health is below threshold
         float healthPercent = _character.GetHealthPercentage();
    if (healthPercent >= healthThresholdForHealing) return;

      // Get storage inventory
     var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;

    // Find healing items in storage (prioritize by healing amount)
   var items = storageInv.GetAllItems();
         var healingItems = items
     .Where(IsHealingItem)
           .OrderByDescending(GetHealingAmount)
      .ToList();

   foreach (var item in healingItems)
       {
         if (ConsumeHealingItem(item, storageInv))
      {
          break; // Only consume one healing item at a time
             }
     }
        }

        private bool ConsumeHealingItem(ItemDrop.ItemData item, Inventory storageInv)
        {
            if (item == null || _character == null) return false;

 var shared = item.m_shared;
  if (shared == null) return false;

          float healAmount = GetHealingAmount(item);
            if (healAmount <= 0) return false;

     // Apply healing
   _character.Heal(healAmount, true);

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
 string itemName = Localization.instance?.Localize(shared.m_name) ?? shared.m_name;
       Debug.Log($"[CompanionConsumables] {_companion.companionName} used {itemName} - Healed {healAmount:F0} HP");
            }

       // Show feedback
        ShowConsumptionEffect(item);

       return true;
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

        private bool IsHealingItem(ItemDrop.ItemData item)
      {
          if (item?.m_shared == null) return false;
      
            var shared = item.m_shared;
       
   // Check if it's a consumable
   if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
          
            // Has healing effect (status effect that heals)
            if (shared.m_consumeStatusEffect != null)
            {
    var effect = shared.m_consumeStatusEffect;
        if (effect is SE_Stats seStats)
                {
 if (seStats.m_healthRegenMultiplier > 1f) return true;
        }
       
           string effectName = effect.name.ToLower();
       if (effectName.Contains("heal") || effectName.Contains("health") || effectName.Contains("potion"))
       {
return true;
       }
    }

       // Check item name patterns
            string itemName = shared.m_name.ToLower();
   if (itemName.Contains("heal") || itemName.Contains("health") || itemName.Contains("medkit") || 
              itemName.Contains("bandage") || itemName.Contains("potion"))
   {
     // But not food items
      if (shared.m_food <= 0 && shared.m_foodStamina <= 0)
       {
         return true;
                }
}

      return false;
        }

        private float GetHealingAmount(ItemDrop.ItemData item)
     {
            if (item?.m_shared == null) return 0f;

            var shared = item.m_shared;
  float healing = 0f;

    // Check consume status effect
            if (shared.m_consumeStatusEffect != null)
      {
          var effect = shared.m_consumeStatusEffect;
           if (effect is SE_Stats seStats)
    {
 if (seStats.m_healthRegenMultiplier > 1f)
           {
             healing += (seStats.m_healthRegenMultiplier - 1f) * 50f;
  }
       }
  }

            if (healing <= 0)
       {
        healing = 25f + (item.m_quality * 10f);
       }

            return healing;
        }

     #endregion

        #region Stats API

        /// <summary>
        /// Gets the health bonus from active food effects.
        /// </summary>
        public float GetFoodHealthBonus()
        {
            // Rate-limited logging - only log once when first called, then every 5 seconds
            if (VerboseLogging && _foodHealthBonus > 0 && Time.time - _lastFoodBonusLogTime >= FOOD_BONUS_LOG_INTERVAL)
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

        private void ShowConsumptionEffect(ItemDrop.ItemData item)
        {
      if (item?.m_shared == null) return;

       var pos = transform.position + Vector3.up * 1.5f;

   try
   {
       var effectPrefab = ZNetScene.instance?.GetPrefab("vfx_creature_eat");
   if (effectPrefab != null)
         {
           Instantiate(effectPrefab, pos, Quaternion.identity);
            }

        var soundPrefab = ZNetScene.instance?.GetPrefab("sfx_creature_eat");
         if (soundPrefab != null)
        {
     Instantiate(soundPrefab, pos, Quaternion.identity);
      }
      }
  catch
         {
         // Effects are optional
            }
        }

        #endregion

        #region Persistence

public void SaveToZDO()
        {
        // Skip during local player respawn / loading-screen â€” ZDO writes during
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
