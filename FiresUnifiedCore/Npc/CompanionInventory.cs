using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

using FiresCore.Logging;
namespace FiresCore.Npc
{
    /// <summary>
    /// Inventory system for companion NPCs.
    /// Supports equipment slots AND general storage grid like a container.
    /// Equipment provides combat stats and visual representation.
    /// Storage allows companions to carry items for the player.
    /// </summary>
    public class CompanionInventory : MonoBehaviour
    {
        [Header("Inventory Settings")]
        // Storage grid: 6 columns x 4 rows = 24 slots.
        //
        // The companion inventory UI displays a main 4x5 storage grid plus Food
        // and Ammo rows that all share this single backing Inventory.  The old
        // 6x3 = 18 backing was smaller than the visible UI: the trailing visual
        // cells looked empty but had no backing slot, so dragging an item there
        // returned false from Inventory.AddItem and the player's item silently
        // disappeared.  24 slots covers the visible grid with headroom.
        //
        // Dimension changes are SAFE for existing saves: vanilla
        // ZPackage Inventory.Load preserves each item's stored grid position,
        // and any (x,y) valid in the old 6x3 box is still valid in 6x4.  Loading
        // an old save just leaves the new bottom row empty.
        public int inventoryWidth = 6;
        public int inventoryHeight = 4;
        public float maxCarryWeight = 150f;

        [Header("Equipment Bonuses")]
        public float bonusHealth = 0f;
        public float bonusArmor = 0f;
        public float bonusMovementSpeed = 0f;

     // Equipment prefab names for persistence (stored separately from ItemData)
        // This mirrors how NpcController stores equipment
        private string _equipHelmet = "";
        private string _equipChest = "";
      private string _equipLegs = "";
        private string _equipShoulder = "";
   private string _equipUtility = "";
    private string _equipRightHand = "";
        private string _equipLeftHand = "";
        private string _equipRightBack = "";
        private string _equipLeftBack = "";
      
        // Quality values for equipped items
        private int _equipHelmetQuality = 1;
        private int _equipChestQuality = 1;
        private int _equipLegsQuality = 1;
        private int _equipShoulderQuality = 1;
        private int _equipUtilityQuality = 1;
   private int _equipRightHandQuality = 1;
        private int _equipLeftHandQuality = 1;
        private int _equipRightBackQuality = 1;
        private int _equipLeftBackQuality = 1;

        private ZNetView _nview;
        private Inventory _inventory; // General storage inventory
  private CompanionController _companion;
        private CompanionCombat _combat;
     private CompanionEquipmentData _equipmentData;
        private NpcVisEquipment _visEquipment;
        private bool _initialized = false;
        
        // Flag to prevent saving during initial load/restore
        private bool _isLoadingFromZDO = false;
        private bool _hasCompletedInitialLoad = false;
        
        // Multiplayer sync - track last equipment state for change detection
        private float _lastRemoteSyncCheck = 0f;
        private const float REMOTE_SYNC_INTERVAL = 2f; // Check every 2 seconds for remote changes
        private int _lastEquipmentHash = 0; // Hash of equipment state for change detection

        // Equipment slots matching player equipment
        private Dictionary<EquipmentSlot, ItemDrop.ItemData> _equippedItems = new Dictionary<EquipmentSlot, ItemDrop.ItemData>();

        // Events for equipment changes
        public event Action<EquipmentSlot, ItemDrop.ItemData> OnEquipmentChanged;
        public event Action OnInventoryChanged;

  public enum EquipmentSlot
        {
      Helmet,
      Chest,
      Legs,
      Shoulder,
      Utility,
      RightHand,
      LeftHand,
      RightBack,
      LeftBack
    }

      #region Unity Lifecycle

        private void Awake()
   {
        _nview = GetComponent<ZNetView>();
  _companion = GetComponent<CompanionController>();
         
    // Initialize inventory in Awake so it's ready when LoadFromZDO is called
     InitializeInventory();
        }

        private void Start()
     {
   _combat = GetComponent<CompanionCombat>();
   _equipmentData = GetComponent<CompanionEquipmentData>();
   
         // Get or create the visual equipment component (same as NpcController uses)
       _visEquipment = GetComponent<NpcVisEquipment>();
          if (_visEquipment == null)
   {
      _visEquipment = gameObject.AddComponent<NpcVisEquipment>();
          }
          
            // For non-owner clients, load from ZDO on start to get initial equipment
            // This ensures other players see the correct visuals
            if (_nview != null && _nview.IsValid() && !_nview.IsOwner())
            {
                // Delay to ensure ZDO is synced
                Invoke(nameof(LoadFromZDOIfNeeded), 1.0f);
            }
        }
        
        /// <summary>
        /// Periodically checks for ZDO equipment changes on non-owner clients.
        /// This ensures multiplayer visual sync when zone owner changes equipment.
        /// </summary>
        private void Update()
        {
            if (_nview == null || !_nview.IsValid()) return;

            // Owner: tick the debounced vault auto-save so picked-up items survive
            // logout/login.
            if (_nview.IsOwner())
            {
                TickVaultAutoSave();
                return;
            }

            // Non-owner clients: periodic sync check for equipment changes.
            _lastRemoteSyncCheck += Time.deltaTime;
            if (_lastRemoteSyncCheck >= REMOTE_SYNC_INTERVAL)
            {
                _lastRemoteSyncCheck = 0f;
                CheckForRemoteEquipmentChanges();
            }
        }
        
        /// <summary>
        /// Loads from ZDO if we haven't done initial load yet.
        /// Used by non-owner clients to get initial equipment state.
        /// </summary>
        private void LoadFromZDOIfNeeded()
        {
            if (_hasCompletedInitialLoad) return;
            LoadFromZDO();
        }
        
        /// <summary>
        /// Checks if equipment in ZDO has changed and updates visuals if needed.
        /// This ensures non-owner clients see equipment changes.
        /// </summary>
        private void CheckForRemoteEquipmentChanges()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            
            // Calculate a hash of current equipment state in ZDO
            int currentHash = CalculateEquipmentHash(zdo);
            
            if (currentHash != _lastEquipmentHash)
            {
                // Only log for tamed companions to reduce log spam
                if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
                    Debug.Log($"[CompanionInventory] Remote equipment change detected (hash {_lastEquipmentHash} -> {currentHash}), syncing...");
                _lastEquipmentHash = currentHash;
                
                // Reload from ZDO
                LoadFromZDO();
            }
        }
        
        /// <summary>
        /// Calculates a hash of the equipment state for change detection.
        /// </summary>
        private int CalculateEquipmentHash(ZDO zdo)
        {
            int hash = 17;

            // Hash the packed equipment field if present, else fall back to the
            // legacy per-slot fields. Either way we get a stable signature for the
            // current equipment state to detect changes.
            string packed = zdo.GetString("companion_equipment", "");
            if (!string.IsNullOrEmpty(packed))
            {
                hash = hash * 31 + packed.GetHashCode();
            }
            else
            {
                hash = hash * 31 + zdo.GetString("companion_equip_helmet", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_chest", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_legs", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_shoulder", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_utility", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_righthand", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_lefthand", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_rightback", "").GetHashCode();
                hash = hash * 31 + zdo.GetString("companion_equip_leftback", "").GetHashCode();
            }

            // Also include appearance data for visual sync
            hash = hash * 31 + zdo.GetString("companion_hair", "").GetHashCode();
            hash = hash * 31 + zdo.GetString("companion_beard", "").GetHashCode();
            hash = hash * 31 + (zdo.GetBool("companion_isfemale", false) ? 1 : 0);

            return hash;
        }

        #endregion

        #region Initialization

        private void InitializeInventory()
        {
 if (_initialized) return;
            
    // Create inventory with custom size (storage grid)
   string inventoryName = $"Companion_{_companion?.companionName ?? "Unknown"}";
   _inventory = new Inventory(inventoryName, null, inventoryWidth, inventoryHeight);

    // Hook into inventory change events
            _inventory.m_onChanged += OnStorageInventoryChanged;

       _initialized = true;
       // Only log inventory initialization for tamed companions when verbose is enabled
       if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
           Debug.Log($"[CompanionInventory] Initialized inventory: {inventoryWidth}x{inventoryHeight}");
    }
        
     /// <summary>
        /// Ensures the inventory is initialized. Call this before any operations that need the inventory.
      /// </summary>
        private void EnsureInitialized()
        {
            if (!_initialized || _inventory == null)
 {
           InitializeInventory();
}
        }

  private void OnStorageInventoryChanged()
       {
  // Don't trigger saves during initial load
          if (_isLoadingFromZDO) return;

            SaveToZDO();
            OnInventoryChanged?.Invoke();

            // Mark dirty for cross-session vault persistence.
            // Vault save is debounced (see TickVaultAutoSave) so rapid pickups
            // during gathering don't hammer the vault every frame, but the data
            // never sits unsaved long enough to be lost on a logout.
            _vaultDirtySinceTime = Time.unscaledTime;
    }

       // ?? Vault auto-save (debounced) ??????????????????????????????????????
       // ZDO saves persist with the world but companions are restored from the
       // VAULT on logout/login and on respawn.  If we only update ZDO, anything
       // picked up since the last explicit vault save is lost across a session.
       private float _vaultDirtySinceTime = -1f;
       private float _lastVaultSaveTime;
       private const float VAULT_SAVE_DEBOUNCE = 2.0f;   // wait this long after last change
       private const float VAULT_SAVE_MAX_DELAY = 8.0f;  // never wait longer than this
       private const float VAULT_SAVE_MIN_INTERVAL = 1.5f; // never save more often than this

       private void TickVaultAutoSave()
       {
           if (_vaultDirtySinceTime < 0f) return;
           if (_companion == null || !_companion.isTamed) return;
           if (_isLoadingFromZDO) return;

           float now = Time.unscaledTime;
           float dirtyFor = now - _vaultDirtySinceTime;
           float sinceLastSave = now - _lastVaultSaveTime;

           // Save when nothing has changed for DEBOUNCE seconds OR we've been
           // dirty for MAX_DELAY (so a stream of constant changes still flushes).
           bool quiet  = dirtyFor >= VAULT_SAVE_DEBOUNCE;
           bool overdue = dirtyFor >= VAULT_SAVE_MAX_DELAY;
           if (!quiet && !overdue) return;
           if (sinceLastSave < VAULT_SAVE_MIN_INTERVAL) return;

           try
           {
               _companion.SaveToZDO();
               _companion.SaveCompanionToVault();
               _lastVaultSaveTime = now;
               _vaultDirtySinceTime = -1f;
           }
           catch (Exception ex)
           {
               Debug.LogWarning($"[CompanionInventory] Auto-save to vault failed: {ex.Message}");
               // back off so we don't loop on the same exception
               _vaultDirtySinceTime = now;
               _lastVaultSaveTime = now;
           }
       }

       private void OnDisable()
       {
           // Final flush so a logout / scene change can't drop pending changes.
           if (_vaultDirtySinceTime >= 0f && _companion != null && _companion.isTamed && !_isLoadingFromZDO)
           {
               try
               {
                   _companion.SaveToZDO();
                   _companion.SaveCompanionToVault();
               }
               catch (Exception ex)
               {
                   Debug.LogWarning($"[CompanionInventory] Final flush failed: {ex.Message}");
               }
               _vaultDirtySinceTime = -1f;
           }
       }

        #endregion

        #region Storage Inventory Access

        /// <summary>
        /// Gets the underlying Valheim Inventory object for storage.
        /// This can be used with InventoryGrid for container-like UI.
        /// </summary>
      public Inventory GetStorageInventory()
        {
EnsureInitialized();
            return _inventory;
        }

/// <summary>
        /// Alias for GetStorageInventory for backwards compatibility.
        /// </summary>
        public Inventory GetInventory()
        {
  EnsureInitialized();
 return _inventory;
     }

        /// <summary>
/// Gets the total weight of all items (equipped + storage).
        /// </summary>
 public float GetTotalWeight()
{
     EnsureInitialized();
            float weight = _inventory?.GetTotalWeight() ?? 0f;
   
            // Add equipped item weights
          foreach (var item in _equippedItems.Values)
  {
       if (item != null)
  {
     weight += item.GetWeight();
          }
       }
  
 return weight;
        }

        /// <summary>
        /// Gets the maximum carry weight for this companion.
        /// Includes:
        /// - Base carry weight (150)
        /// - Level bonus (+1 per level, up to +100 at max level)
        /// - Strength attribute bonus (+5 per point)
        /// - Endurance attribute bonus (+3 per point)
        /// - Utility item bonus (e.g., Megingjord adds +150)
        /// </summary>
        public float GetMaxCarryWeight()
        {
            float baseWeight = maxCarryWeight; // Default 150
            float levelBonus = 0f;
            float strengthBonus = 0f;
            float enduranceBonus = 0f;
            float utilityBonus = 0f;
            
            // Level-based bonus: +1 carry capacity per level (up to +100 at level 100)
            var progression = GetComponent<CompanionProgression>();
            if (progression != null)
            {
                int level = progression.Level;
                levelBonus = level; // +1 per level
                
                // Strength attribute bonus: +5 carry capacity per Strength point
                int strength = progression.GetAttributeValue(CompanionProgression.AttributeType.Strength);
                strengthBonus = strength * 5f;
                
                // Endurance attribute bonus: +3 carry capacity per Endurance point
                int endurance = progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                enduranceBonus = endurance * 3f;
            }
            
            // Utility slot item bonus (check for Megingjord and similar items)
            var utilityItem = GetEquippedItem(EquipmentSlot.Utility);
            if (utilityItem != null)
            {
                // Megingjord and similar items have m_equipStatusEffect that provides carry bonus
                // The SE_Stats effect has m_addMaxCarryWeight field
                var statusEffect = utilityItem.m_shared.m_equipStatusEffect;
                if (statusEffect != null && statusEffect is SE_Stats seStats)
                {
                    utilityBonus = seStats.m_addMaxCarryWeight;
                }
                
                // Alternative: Check by prefab name for known utility items
                string prefabName = _equipUtility?.ToLower() ?? "";
                if (prefabName.Contains("beltstrength") || prefabName.Contains("megingjord"))
                {
                    // Megingjord adds +150 carry weight
                    // Only add if not already added via status effect
                    if (statusEffect == null || !(statusEffect is SE_Stats) || utilityBonus == 0f)
                    {
                        utilityBonus = 150f;
                    }
                }
            }
            
            float total = baseWeight + levelBonus + strengthBonus + enduranceBonus + utilityBonus;
            return total;
        }
        
        /// <summary>
        /// Gets a detailed breakdown of carry weight calculation for debugging.
        /// </summary>
        public string GetCarryWeightBreakdown()
        {
            float baseWeight = maxCarryWeight;
            float levelBonus = 0f;
            float strengthBonus = 0f;
            float enduranceBonus = 0f;
            float utilityBonus = 0f;
            int level = 0;
            int strength = 0;
            int endurance = 0;
            string utilityName = "None";
            
            var progression = GetComponent<CompanionProgression>();
            if (progression != null)
            {
                level = progression.Level;
                levelBonus = level;
                strength = progression.GetAttributeValue(CompanionProgression.AttributeType.Strength);
                strengthBonus = strength * 5f;
                endurance = progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                enduranceBonus = endurance * 3f;
            }
            
            var utilityItem = GetEquippedItem(EquipmentSlot.Utility);
            if (utilityItem != null)
            {
                utilityName = utilityItem.m_shared.m_name ?? _equipUtility ?? "Unknown";
                var statusEffect = utilityItem.m_shared.m_equipStatusEffect;
                if (statusEffect != null && statusEffect is SE_Stats seStats)
                {
                    utilityBonus = seStats.m_addMaxCarryWeight;
                }
                string prefabName = _equipUtility?.ToLower() ?? "";
                if ((prefabName.Contains("beltstrength") || prefabName.Contains("megingjord")) && utilityBonus == 0f)
                {
                    utilityBonus = 150f;
                }
            }
            
            float total = baseWeight + levelBonus + strengthBonus + enduranceBonus + utilityBonus;
            
            return $"Carry Weight: {total:F0}\n" +
                   $"  Base: {baseWeight:F0}\n" +
                   $"  Level {level}: +{levelBonus:F0}\n" +
                   $"  Strength ({strength}): +{strengthBonus:F0}\n" +
                   $"  Endurance ({endurance}): +{enduranceBonus:F0}\n" +
                   $"  Utility ({utilityName}): +{utilityBonus:F0}";
        }

        /// <summary>
        /// Checks if the inventory has room for an item.
  /// </summary>
        public bool CanAddItem(ItemDrop.ItemData item, int amount = 1)
{
        EnsureInitialized();
         if (_inventory == null || item == null) return false;
         
 // Check weight limit
  float newWeight = GetTotalWeight() + (item.GetWeight() * amount);
        if (newWeight > maxCarryWeight) return false;
   
            return _inventory.CanAddItem(item, amount);
  }

        /// <summary>
        /// Adds an item to the storage inventory.
        /// </summary>
        public bool AddItem(ItemDrop.ItemData item)
        {
       EnsureInitialized();
    if (_inventory == null || item == null) return false;

         bool added = _inventory.AddItem(item);
         if (added && !_isLoadingFromZDO)
          {
       SaveToZDO();
   }
   return added;
        }

        /// <summary>
    /// Removes an item from the storage inventory.
        /// </summary>
     public bool RemoveItem(ItemDrop.ItemData item)
 {
        EnsureInitialized();
 if (_inventory == null || item == null) return false;

   bool removed = _inventory.RemoveItem(item);
  if (removed && !_isLoadingFromZDO)
       {
     SaveToZDO();
          }
    return removed;
        }

    /// <summary>
        /// Gets all items in the storage inventory.
        /// </summary>
        public List<ItemDrop.ItemData> GetAllStorageItems()
        {
            EnsureInitialized();
      return _inventory?.GetAllItems() ?? new List<ItemDrop.ItemData>();
        }

        #endregion

   #region Equipment

        /// <summary>
        /// Equips an item to the specified slot.
      /// Triggers stat recalculation and visual updates.
        /// Note: Does NOT automatically save to ZDO - call TriggerSaveToZDO() when appropriate.
    /// </summary>
  public bool EquipItem(EquipmentSlot slot, ItemDrop.ItemData item)
  {
      if (item == null) return false;
      
      // CRITICAL: Skip equipment on ghost/preview objects (hammer placement preview)
      // These don't have valid ZNetViews and will cause errors
      if (IsGhostPreview()) return false;

       // Unequip current item in slot first
            UnequipSlot(slot);

            // Store equipped item
    _equippedItems[slot] = item;
    
  // Get the proper prefab name for persistence
      string prefabName = GetItemPrefabName(item);
      
     // Store prefab name for persistence
      SetEquipmentPrefabName(slot, prefabName, item.m_quality);

            // Recalculate stats
          RecalculateEquipmentBonuses();

        // Notify equipment data system to refresh
  _equipmentData?.RefreshAllEquipmentData();

        // Fire event
           OnEquipmentChanged?.Invoke(slot, item);

     // Update visual equipment
        ApplyVisualEquipment();
        
            // Notify archetype controller of equipment change
            var archetypeController = GetComponent<Archetypes.ArchetypeController>();
            archetypeController?.ForceReevaluate();

            return true;
    }

        /// <summary>
        /// Equips an item silently without triggering visual updates or saves.
        /// Use this when batch-equipping items, then call RecalculateEquipmentBonusesPublic() and ApplyVisualEquipment() manually.
        /// </summary>
   public bool EquipItemSilent(EquipmentSlot slot, ItemDrop.ItemData item)
        {
            if (item == null) return false;
            
            // CRITICAL: Skip equipment on ghost/preview objects (hammer placement preview)
            if (IsGhostPreview()) return false;
            
            // WEAPON COMBINATION VALIDATION:
            // Never allow bow + right-hand weapon at the same time
            // Bows are two-handed and require both hands
            if (!ValidateWeaponCombination(slot, item))
            {
                Debug.LogWarning($"[CompanionInventory] Invalid weapon combination: cannot equip {item.m_shared?.m_name} to {slot}");
                return false;
            }

            // Store equipped item
            _equippedItems[slot] = item;
    
            // Get the proper prefab name for persistence
   string prefabName = GetItemPrefabName(item);
      
            // Store prefab name for persistence
   SetEquipmentPrefabName(slot, prefabName, item.m_quality);

            return true;
        }
        
        /// <summary>
        /// Validates that equipping an item to a slot doesn't create an invalid weapon combination.
        /// If a conflict exists, automatically holsters the conflicting weapon.
        /// Rules:
        /// - Bow in left hand = cannot have weapon in right hand (bows are two-handed)
        /// - Weapon in right hand = cannot have bow in left hand
        /// - Allowed: one-handed weapon + shield, one-handed weapon + torch
        /// </summary>
        private bool ValidateWeaponCombination(EquipmentSlot targetSlot, ItemDrop.ItemData itemToEquip)
        {
            if (itemToEquip?.m_shared == null) return false;
            
            var itemType = itemToEquip.m_shared.m_itemType;
            
            // Check if we're trying to equip a bow to left hand
            if (targetSlot == EquipmentSlot.LeftHand && itemType == ItemDrop.ItemData.ItemType.Bow)
            {
                // Must not have a weapon in right hand - auto-holster if there is one
                var rightHand = GetEquippedItem(EquipmentSlot.RightHand);
                if (rightHand != null && rightHand.IsWeapon())
                {
                    // Only log for tamed companions when verbose is enabled
                    if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
                        Debug.Log($"[CompanionInventory] Auto-holstering {rightHand.m_shared?.m_name} to equip bow");
                    
                    // Unequip from right hand
                    UnequipSlotSilent(EquipmentSlot.RightHand);
                    
                    // Try to put in right back slot
                    var rightBack = GetEquippedItem(EquipmentSlot.RightBack);
                    if (rightBack == null)
                    {
                        EquipItemSilent(EquipmentSlot.RightBack, rightHand);
                    }
                    else
                    {
                        // Put in storage if back slot is occupied
                        _inventory?.AddItem(rightHand);
                    }
                }
            }
            
            // Check if we're trying to equip a weapon to right hand
            if (targetSlot == EquipmentSlot.RightHand && itemToEquip.IsWeapon())
            {
                // Must not have a bow in left hand - auto-holster if there is one
                var leftHand = GetEquippedItem(EquipmentSlot.LeftHand);
                if (leftHand != null && leftHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                {
                    // Only log for tamed companions when verbose is enabled
                    if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
                        Debug.Log($"[CompanionInventory] Auto-holstering bow {leftHand.m_shared?.m_name} to equip {itemToEquip.m_shared?.m_name}");
                    
                    // Unequip from left hand
                    UnequipSlotSilent(EquipmentSlot.LeftHand);
                    
                    // Try to put in left back slot
                    var leftBack = GetEquippedItem(EquipmentSlot.LeftBack);
                    if (leftBack == null)
                    {
                        EquipItemSilent(EquipmentSlot.LeftBack, leftHand);
                    }
                    else
                    {
                        // Put in storage if back slot is occupied
                        _inventory?.AddItem(leftHand);
                    }
                }
            }
            
            // Two-handed weapons require clearing both hands
            if (itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon || 
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
            {
                // These should typically go in right hand or left hand respectively
                // and prevent anything in the other hand - but that's handled by the callers
            }
            
            return true;
        }

     /// <summary>
/// Unequips the item from the specified slot.
    /// Returns the unequipped item (or null if slot was empty).
        /// Note: Does NOT automatically save to ZDO - call TriggerSaveToZDO() when appropriate.
   /// </summary>
      public ItemDrop.ItemData UnequipSlot(EquipmentSlot slot)
        {
  if (!_equippedItems.TryGetValue(slot, out var item))
           return null;

      _equippedItems.Remove(slot);

  // Clear prefab name
    SetEquipmentPrefabName(slot, "", 1);

 // Recalculate stats
         RecalculateEquipmentBonuses();

         // Notify equipment data system to refresh
         _equipmentData?.RefreshAllEquipmentData();

// Fire event
       OnEquipmentChanged?.Invoke(slot, null);

         // Update visual equipment
         ApplyVisualEquipment();

            return item;
 }

        /// <summary>
        /// Unequips a slot silently without triggering visual updates or saves.
        /// Use this when batch-unequipping items.
        /// </summary>
   public ItemDrop.ItemData UnequipSlotSilent(EquipmentSlot slot)
 {
         if (!_equippedItems.TryGetValue(slot, out var item))
        return null;

 _equippedItems.Remove(slot);
 
            // Clear prefab name
    SetEquipmentPrefabName(slot, "", 1);

            return item;
   }
    /// <summary>
        /// Gets the equipped item for a slot.
        /// </summary>
 public ItemDrop.ItemData GetEquippedItem(EquipmentSlot slot)
      {
        _equippedItems.TryGetValue(slot, out var item);
            return item;
        }

        /// <summary>
 /// Gets all equipped items.
        /// </summary>
    public Dictionary<EquipmentSlot, ItemDrop.ItemData> GetAllEquipped()
        {
    return new Dictionary<EquipmentSlot, ItemDrop.ItemData>(_equippedItems);
    }

        /// <summary>
        /// Checks if an item type can be equipped to a slot.
        /// </summary>
        public static bool CanEquipToSlot(ItemDrop.ItemData item, EquipmentSlot slot)
        {
            if (item == null) return false;

   var itemType = item.m_shared.m_itemType;

switch (slot)
            {
     case EquipmentSlot.Helmet:
         return itemType == ItemDrop.ItemData.ItemType.Helmet;
       case EquipmentSlot.Chest:
      return itemType == ItemDrop.ItemData.ItemType.Chest;
      case EquipmentSlot.Legs:
          return itemType == ItemDrop.ItemData.ItemType.Legs;
    case EquipmentSlot.Shoulder:
         return itemType == ItemDrop.ItemData.ItemType.Shoulder;
                case EquipmentSlot.Utility:
         return itemType == ItemDrop.ItemData.ItemType.Utility;
           case EquipmentSlot.RightHand:
         // Right hand: One-handed weapons, two-handed weapons (not left-type), tools, torches
      // NOTE: Bows do NOT go in right hand - they go in left hand!
       return itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
        itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
   itemType == ItemDrop.ItemData.ItemType.Tool ||
    itemType == ItemDrop.ItemData.ItemType.Torch;
      case EquipmentSlot.LeftHand:
    // Left hand: Shields, torches, one-handed weapons, BOWS, and TwoHandedWeaponLeft
         return itemType == ItemDrop.ItemData.ItemType.Shield ||
             itemType == ItemDrop.ItemData.ItemType.Torch ||
    itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
   itemType == ItemDrop.ItemData.ItemType.Bow ||  // Bows go in left hand!
   itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
          case EquipmentSlot.RightBack:
      case EquipmentSlot.LeftBack:
        return itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
           itemType == ItemDrop.ItemData.ItemType.Bow ||
      itemType == ItemDrop.ItemData.ItemType.Shield;
        default:
  return false;
            }
        }

   /// <summary>
        /// Gets the appropriate equipment slot for an item type.
      /// </summary>
     public static EquipmentSlot? GetSlotForItem(ItemDrop.ItemData item)
 {
            if (item == null) return null;

            switch (item.m_shared.m_itemType)
        {
      case ItemDrop.ItemData.ItemType.Helmet:
    return EquipmentSlot.Helmet;
  case ItemDrop.ItemData.ItemType.Chest:
          return EquipmentSlot.Chest;
    case ItemDrop.ItemData.ItemType.Legs:
     return EquipmentSlot.Legs;
    case ItemDrop.ItemData.ItemType.Shoulder:
     return EquipmentSlot.Shoulder;
case ItemDrop.ItemData.ItemType.Utility:
              return EquipmentSlot.Utility;
      case ItemDrop.ItemData.ItemType.OneHandedWeapon:
    case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
  case ItemDrop.ItemData.ItemType.Tool:
        case ItemDrop.ItemData.ItemType.Torch:
      return EquipmentSlot.RightHand;
        case ItemDrop.ItemData.ItemType.Bow:
    case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
         return EquipmentSlot.LeftHand;  // Bows go in left hand!
                case ItemDrop.ItemData.ItemType.Shield:
    return EquipmentSlot.LeftHand;
    default:
       return null;
            }
        }
        
  /// <summary>
        /// Sets the equipment prefab name for a slot (used for persistence).
  /// </summary>
     private void SetEquipmentPrefabName(EquipmentSlot slot, string prefabName, int quality)
        {
  switch (slot)
   {
  case EquipmentSlot.Helmet: _equipHelmet = prefabName; _equipHelmetQuality = quality; break;
         case EquipmentSlot.Chest: _equipChest = prefabName; _equipChestQuality = quality; break;
       case EquipmentSlot.Legs: _equipLegs = prefabName; _equipLegsQuality = quality; break;
            case EquipmentSlot.Shoulder: _equipShoulder = prefabName; _equipShoulderQuality = quality; break;
                case EquipmentSlot.Utility: _equipUtility = prefabName; _equipUtilityQuality = quality; break;
           case EquipmentSlot.RightHand: _equipRightHand = prefabName; _equipRightHandQuality = quality; break;
    case EquipmentSlot.LeftHand: _equipLeftHand = prefabName; _equipLeftHandQuality = quality; break;
   case EquipmentSlot.RightBack: _equipRightBack = prefabName; _equipRightBackQuality = quality; break;
          case EquipmentSlot.LeftBack: _equipLeftBack = prefabName; _equipLeftBackQuality = quality; break;
            }
        }

 /// <summary>
        /// Gets the equipment prefab name for a slot.
        /// </summary>
        private string GetEquipmentPrefabName(EquipmentSlot slot)
        {
            return slot switch
   {
      EquipmentSlot.Helmet => _equipHelmet,
      EquipmentSlot.Chest => _equipChest,
      EquipmentSlot.Legs => _equipLegs,
      EquipmentSlot.Shoulder => _equipShoulder,
      EquipmentSlot.Utility => _equipUtility,
      EquipmentSlot.RightHand => _equipRightHand,
      EquipmentSlot.LeftHand => _equipLeftHand,
      EquipmentSlot.RightBack => _equipRightBack,
      EquipmentSlot.LeftBack => _equipLeftBack,
            _ => ""
   };
        }
        
        /// <summary>
        /// Gets the equipment quality for a slot.
        /// </summary>
        private int GetEquipmentQuality(EquipmentSlot slot)
        {
  return slot switch
     {
     EquipmentSlot.Helmet => _equipHelmetQuality,
     EquipmentSlot.Chest => _equipChestQuality,
     EquipmentSlot.Legs => _equipLegsQuality,
     EquipmentSlot.Shoulder => _equipShoulderQuality,
     EquipmentSlot.Utility => _equipUtilityQuality,
     EquipmentSlot.RightHand => _equipRightHandQuality,
     EquipmentSlot.LeftHand => _equipLeftHandQuality,
     EquipmentSlot.RightBack => _equipRightBackQuality,
     EquipmentSlot.LeftBack => _equipLeftBackQuality,
     _ => 1
   };
        }

   /// <summary>
        /// Public accessor for getting the equipment prefab name for a slot.
        /// Used by CompanionController for vault persistence.
        /// </summary>
        public string GetEquipmentPrefabNamePublic(EquipmentSlot slot)
 {
        return GetEquipmentPrefabName(slot);
        }
        
        /// <summary>
        /// Public accessor for getting the equipment quality for a slot.
        /// Used by CompanionController for vault persistence.
        /// </summary>
        public int GetEquipmentQualityPublic(EquipmentSlot slot)
        {
            return GetEquipmentQuality(slot);
        }
        
        /// <summary>
        /// Updates the quality of an equipped item and persists the change.
        /// This is the proper way to upgrade an item's quality - it updates both
        /// the ItemData object and the stored quality value, then saves to ZDO.
        /// </summary>
        /// <param name="item">The item to update (must be in _equippedItems)</param>
        /// <param name="newQuality">The new quality level</param>
        /// <returns>True if the item was found and updated</returns>
        public bool UpdateItemQuality(ItemDrop.ItemData item, int newQuality)
        {
            if (item == null) return false;
            
            // Find which slot this item is in
            EquipmentSlot? foundSlot = null;
            foreach (var kvp in _equippedItems)
            {
                if (kvp.Value == item)
                {
                    foundSlot = kvp.Key;
                    break;
                }
            }
            
            if (foundSlot == null)
            {
                Debug.LogWarning($"[CompanionInventory] UpdateItemQuality: Item not found in equipped items");
                return false;
            }
            
            // Update the ItemData quality
            item.m_quality = newQuality;
            
            // Update the stored quality value for persistence
            SetEquipmentQuality(foundSlot.Value, newQuality);
            
            // Recalculate bonuses since quality affects armor/damage
            RecalculateEquipmentBonuses();
            
            // Save to ZDO to persist the change
            SaveToZDO();
            
            // CRITICAL: Also save to VAULT via CompanionController
            // This ensures the quality change persists across logout/login
            // The ZDO save alone is not enough - companions are destroyed on logout
            // and restored from vault on login, so vault is the source of truth
            if (_companion != null)
            {
                _companion.SaveToZDO();
                _companion.SaveCompanionToVault();
            }
            
            Debug.Log($"[CompanionInventory] Updated {foundSlot.Value} quality to {newQuality} and saved to ZDO + Vault");
            
            return true;
        }
        
        /// <summary>
        /// Sets the quality value for an equipment slot (used internally for persistence).
        /// </summary>
        private void SetEquipmentQuality(EquipmentSlot slot, int quality)
        {
            switch (slot)
            {
                case EquipmentSlot.Helmet: _equipHelmetQuality = quality; break;
                case EquipmentSlot.Chest: _equipChestQuality = quality; break;
                case EquipmentSlot.Legs: _equipLegsQuality = quality; break;
                case EquipmentSlot.Shoulder: _equipShoulderQuality = quality; break;
                case EquipmentSlot.Utility: _equipUtilityQuality = quality; break;
                case EquipmentSlot.RightHand: _equipRightHandQuality = quality; break;
                case EquipmentSlot.LeftHand: _equipLeftHandQuality = quality; break;
                case EquipmentSlot.RightBack: _equipRightBackQuality = quality; break;
                case EquipmentSlot.LeftBack: _equipLeftBackQuality = quality; break;
            }
        }

  /// <summary>
  /// Checks if any equipment has been loaded (by prefab name).
        /// Used to determine if vault restore is needed.
        /// </summary>
        public bool HasAnyEquipmentLoaded()
 {
            return !string.IsNullOrEmpty(_equipHelmet) ||
         !string.IsNullOrEmpty(_equipChest) ||
         !string.IsNullOrEmpty(_equipLegs) ||
         !string.IsNullOrEmpty(_equipShoulder) ||
         !string.IsNullOrEmpty(_equipUtility) ||
         !string.IsNullOrEmpty(_equipRightHand) ||
         !string.IsNullOrEmpty(_equipLeftHand) ||
         !string.IsNullOrEmpty(_equipRightBack) ||
         !string.IsNullOrEmpty(_equipLeftBack);
}

        /// <summary>
        /// Alias for HasAnyEquipmentLoaded for compatibility.
        /// </summary>
        public bool HasAnyEquipment() => HasAnyEquipmentLoaded();

        /// <summary>
        /// Clears all equipped items from all slots.
        /// </summary>
        public void ClearAllEquipment()
        {
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                UnequipSlot(slot);
            }
            
            // Clear prefab name strings
            _equipHelmet = "";
            _equipChest = "";
            _equipLegs = "";
            _equipShoulder = "";
            _equipUtility = "";
            _equipRightHand = "";
            _equipLeftHand = "";
            _equipRightBack = "";
            _equipLeftBack = "";
            
            // Reset qualities
            _equipHelmetQuality = 1;
            _equipChestQuality = 1;
            _equipLegsQuality = 1;
            _equipShoulderQuality = 1;
            _equipUtilityQuality = 1;
            _equipRightHandQuality = 1;
            _equipLeftHandQuality = 1;
            _equipRightBackQuality = 1;
            _equipLeftBackQuality = 1;
            
            // Update visuals
            ApplyVisualEquipment();
        }

        /// <summary>
        /// Equips an item by prefab name and quality.
        /// </summary>
        public bool EquipItem(EquipmentSlot slot, string prefabName, int quality = 1)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            
            // CRITICAL: Skip equipment on ghost/preview objects (hammer placement preview)
            if (IsGhostPreview()) return false;
            
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null)
                {
                    Debug.LogWarning($"[CompanionInventory] Prefab not found: {prefabName}");
                    return false;
                }
                
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData == null)
                {
                    Debug.LogWarning($"[CompanionInventory] No ItemDrop on prefab: {prefabName}");
                    return false;
                }
                
                // Clone the item data
                var itemData = itemDrop.m_itemData.Clone();
                itemData.m_quality = Mathf.Max(1, quality);
                itemData.m_durability = itemData.GetMaxDurability();
                
                // CRITICAL: Set m_dropPrefab so GetItemPrefabName() and Inventory.Save() work correctly
                // Without this, Clone() may not preserve the prefab reference, causing persistence issues.
                itemData.m_dropPrefab = prefab;
                
                // Equip the item
                return EquipItem(slot, itemData);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInventory] Failed to equip {prefabName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Loads a weapon into a hand slot as real combat ItemData WITHOUT touching the visual equipment path
        /// (no ApplyVisualEquipment, no double-paint). Static-NPC gear is otherwise visual-only via
        /// NpcVisEquipment, which leaves CompanionEquipmentData.WeaponItem null — so the combat system can't
        /// attack or weapon-swap. This populates the slot the combat code reads, leaving visuals untouched.
        /// </summary>
        public bool LoadCombatWeaponSilent(EquipmentSlot slot, string prefabName, int quality = 1)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (IsGhostPreview()) return false;
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                var itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (itemDrop?.m_itemData == null) return false;
                var itemData = itemDrop.m_itemData.Clone();
                itemData.m_quality = Mathf.Max(1, quality);
                itemData.m_durability = itemData.GetMaxDurability();
                itemData.m_dropPrefab = prefab;
                if (!EquipItemSilent(slot, itemData)) return false;

                // EquipItemSilent stores the item but SKIPS the combat-data refresh — without this the combat
                // system keeps WeaponAnimationState=Unarmed and swings with fists for fist damage. Refreshing
                // resolves WeaponItem/WeaponShared/WeaponAnimationState and fires OnEquipmentChanged, so
                // CompanionCombat switches to the armed weapon behavior and uses the weapon's damage.
                _equipmentData?.RefreshAllEquipmentData();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInventory] LoadCombatWeaponSilent {prefabName} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores equipment from vault data.
        /// Sets the prefab name and recreates the ItemData.
        /// </summary>
        public void RestoreEquipmentFromVault(EquipmentSlot slot, string prefabName, int quality)
        {
   if (string.IsNullOrEmpty(prefabName)) return;
   
            // CRITICAL: Skip equipment on ghost/preview objects (hammer placement preview)
            if (IsGhostPreview()) return;

          // Set the prefab name for persistence
            SetEquipmentPrefabName(slot, prefabName, quality);

    // Recreate the ItemData
            LoadEquipmentSlotFromPrefab(slot, prefabName, quality);

     if (FiresLogger.VerboseEnabled)
         Debug.Log($"[CompanionInventory] Restored {slot} = {prefabName} (quality {quality}) from vault");
        }

        /// <summary>
        /// Public accessor for recalculating equipment bonuses.
        /// Used after vault restore.
        /// </summary>
        public void RecalculateEquipmentBonusesPublic()
        {
     RecalculateEquipmentBonuses();
    }
        
        /// <summary>
        /// Gets the total armor from all equipped items.
   /// </summary>
        public float GetTotalArmor() => bonusArmor;

        /// <summary>
/// Gets the movement speed modifier from equipment.
        /// </summary>
        public float GetMovementSpeedModifier() => bonusMovementSpeed;

        /// <summary>
        /// Gets a summary of all equipment stats.
        /// </summary>
        public string GetEquipmentStatsSummary()
        {
            var lines = new List<string>();

         if (bonusArmor > 0)
    lines.Add($"Armor: {bonusArmor:F0}");

            if (bonusMovementSpeed != 0)
                lines.Add($"Speed: {bonusMovementSpeed * 100:F0}%");

            var weapon = GetEquippedItem(EquipmentSlot.RightHand);
       if (weapon != null)
       {
       lines.Add($"Weapon: {weapon.m_shared.m_name}");

        var damages = weapon.m_shared.m_damages;
     var totalDamage = damages.m_damage + damages.m_blunt + damages.m_slash +
 damages.m_pierce + damages.m_fire + damages.m_frost +
        damages.m_lightning + damages.m_poison + damages.m_spirit;
             if (totalDamage > 0)
   lines.Add($"  Damage: {totalDamage:F0}");
    }

   return lines.Count > 0 ? string.Join("\n", lines) : "No equipment";
      }

        #endregion

    #region Persistence

        public void LoadFromZDO()
        {
      var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            // Make sure inventory is initialized before loading
         EnsureInitialized();
            
        // Set flag to prevent saves during load
     _isLoadingFromZDO = true;

            try
        {
     // Load storage inventory data
string inventoryData = zdo.GetString("companion_inventory", "");
   if (!string.IsNullOrEmpty(inventoryData))
          {
        var pkg = new ZPackage(inventoryData);
         _inventory.Load(pkg);
      if (FiresLogger.VerboseEnabled)
          Debug.Log($"[CompanionInventory] Loaded storage inventory with {_inventory.GetAllItems().Count} items");
     }

                // Try the new packed equipment field first (single base64 ZPackage with
                // all 9 slots). Falls back to the legacy per-slot fields below if the
                // packed field is missing ï¿½ that path will auto-migrate on next save.
                bool loadedFromPacked = false;
                string packedEquip = zdo.GetString("companion_equipment", "");
                if (!string.IsNullOrEmpty(packedEquip))
                {
                    try
                    {
                        var eqPkg = new ZPackage(packedEquip);
                        int version = eqPkg.ReadInt(); // currently 1
                        _equipHelmet     = eqPkg.ReadString(); _equipHelmetQuality     = eqPkg.ReadInt();
                        _equipChest      = eqPkg.ReadString(); _equipChestQuality      = eqPkg.ReadInt();
                        _equipLegs       = eqPkg.ReadString(); _equipLegsQuality       = eqPkg.ReadInt();
                        _equipShoulder   = eqPkg.ReadString(); _equipShoulderQuality   = eqPkg.ReadInt();
                        _equipUtility    = eqPkg.ReadString(); _equipUtilityQuality    = eqPkg.ReadInt();
                        _equipRightHand  = eqPkg.ReadString(); _equipRightHandQuality  = eqPkg.ReadInt();
                        _equipLeftHand   = eqPkg.ReadString(); _equipLeftHandQuality   = eqPkg.ReadInt();
                        _equipRightBack  = eqPkg.ReadString(); _equipRightBackQuality  = eqPkg.ReadInt();
                        _equipLeftBack   = eqPkg.ReadString(); _equipLeftBackQuality   = eqPkg.ReadInt();
                        loadedFromPacked = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionInventory] Packed equipment field corrupt, falling back to legacy fields: {ex.Message}");
                    }
                }

                if (!loadedFromPacked)
                {
                    // Legacy path ï¿½ read 18 per-slot fields. Next SaveToZDO will migrate
                    // them into the packed field and remove the legacy keys automatically.
       _equipHelmet = zdo.GetString("companion_equip_helmet", "");
       _equipChest = zdo.GetString("companion_equip_chest", "");
       _equipLegs = zdo.GetString("companion_equip_legs", "");
       _equipShoulder = zdo.GetString("companion_equip_shoulder", "");
       _equipUtility = zdo.GetString("companion_equip_utility", "");
       _equipRightHand = zdo.GetString("companion_equip_righthand", "");
       _equipLeftHand = zdo.GetString("companion_equip_lefthand", "");
       _equipRightBack = zdo.GetString("companion_equip_rightback", "");
       _equipLeftBack = zdo.GetString("companion_equip_leftback", "");

       // Load quality values
          _equipHelmetQuality = zdo.GetInt("companion_equip_helmet_quality", 1);
          _equipChestQuality = zdo.GetInt("companion_equip_chest_quality", 1);
          _equipLegsQuality = zdo.GetInt("companion_equip_legs_quality", 1);
          _equipShoulderQuality = zdo.GetInt("companion_equip_shoulder_quality", 1);
          _equipUtilityQuality = zdo.GetInt("companion_equip_utility_quality", 1);
          _equipRightHandQuality = zdo.GetInt("companion_equip_righthand_quality", 1);
          _equipLeftHandQuality = zdo.GetInt("companion_equip_lefthand_quality", 1);
          _equipRightBackQuality = zdo.GetInt("companion_equip_rightback_quality", 1);
          _equipLeftBackQuality = zdo.GetInt("companion_equip_leftback_quality", 1);
                }

       // Log what we loaded - only for tamed companions when verbose is enabled
         if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
             Debug.Log($"[CompanionInventory] Loaded equipment prefabs - Helmet:{_equipHelmet}, Chest:{_equipChest}, Legs:{_equipLegs}, RightHand:{_equipRightHand}");

            // Recreate ItemData from prefab names (only if ObjectDB is ready)
       _equippedItems.Clear();
                if (ObjectDB.instance != null)
                {
    LoadEquipmentSlotFromPrefab(EquipmentSlot.Helmet, _equipHelmet, _equipHelmetQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.Chest, _equipChest, _equipChestQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.Legs, _equipLegs, _equipLegsQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.Shoulder, _equipShoulder, _equipShoulderQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.Utility, _equipUtility, _equipUtilityQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.RightHand, _equipRightHand, _equipRightHandQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.LeftHand, _equipLeftHand, _equipLeftHandQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.RightBack, _equipRightBack, _equipRightBackQuality);
    LoadEquipmentSlotFromPrefab(EquipmentSlot.LeftBack, _equipLeftBack, _equipLeftBackQuality);
         }

 // Recalculate bonuses after loading
      RecalculateEquipmentBonuses();

          // Apply visual equipment after NpcVisEquipment has had time to initialize
          // (its DelayedInitialize runs at 2s, so 2.5s is the earliest safe window).
          // A single one-shot retry is scheduled inside ApplyVisualEquipment if
          // VisEquipment is still null at that point ï¿½ no need for 3 staggered calls.
    if (HasAnyEquipment())
          {
         Invoke(nameof(ApplyVisualEquipment), 2.5f);
 }

          // Only log loaded inventory for tamed companions when verbose is enabled
          if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
              Debug.Log($"[CompanionInventory] Loaded inventory with {_inventory.GetAllItems().Count} items, {_equippedItems.Count} equipped");
     
     // Mark initial load as complete
           _hasCompletedInitialLoad = true;
           
           // Update the equipment hash for change detection
           _lastEquipmentHash = CalculateEquipmentHash(zdo);
     }
            catch (Exception ex)
          {
    Debug.LogWarning($"[CompanionInventory] Failed to load: {ex.Message}");
            }
            finally
            {
    // Clear the loading flag
  _isLoadingFromZDO = false;
            }
        }
        
        /// <summary>
        /// Third attempt at applying visual equipment - called even later to catch edge cases.
        /// </summary>
        private void ApplyVisualEquipmentFinal()
        {
            // Kept for any in-flight Invoke() calls scheduled before this version loaded.
            // Delegates to the main method which now contains its own retry logic.
            ApplyVisualEquipment();
        }

        /// <summary>
        /// Loads an equipment slot from a prefab name.
        /// Creates ItemData from the prefab.
   /// </summary>
private void LoadEquipmentSlotFromPrefab(EquipmentSlot slot, string prefabName, int quality)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
  if (ObjectDB.instance == null) return;

   var itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
  if (itemPrefab != null)
            {
  var itemDrop = itemPrefab.GetComponent<ItemDrop>();
       if (itemDrop != null && itemDrop.m_itemData != null)
{
     // Validate that item has valid icons before using
     if (itemDrop.m_itemData.m_shared?.m_icons == null || itemDrop.m_itemData.m_shared.m_icons.Length == 0)
     {
         Debug.LogWarning($"[CompanionInventory] Skipping {prefabName} for {slot} - no valid icons");
         return;
     }
     
     var itemData = itemDrop.m_itemData.Clone();
           itemData.m_quality = quality;
          itemData.m_dropPrefab = itemPrefab; // Important for visual equipment
          
     // CRITICAL: Clamp m_variant to prevent IndexOutOfRangeException in GetIcon()
     if (itemData.m_shared?.m_icons != null && itemData.m_shared.m_icons.Length > 0)
     {
         itemData.m_variant = Mathf.Clamp(itemData.m_variant, 0, itemData.m_shared.m_icons.Length - 1);
     }
     else
     {
         itemData.m_variant = 0;
     }
     
      _equippedItems[slot] = itemData;

  if (FiresLogger.VerboseEnabled)
      Debug.Log($"[CompanionInventory] Loaded equipment: {slot} = {prefabName} (quality {quality})");
      }
   }
  else
        {
   Debug.LogWarning($"[CompanionInventory] Could not find prefab for {slot}: {prefabName}");
            }
        }
        
        
        /// <summary>
      /// Gets the actual prefab name from an item.
   /// This handles cases where m_dropPrefab might be null.
        /// </summary>
        private string GetItemPrefabName(ItemDrop.ItemData item)
        {
            if (item == null) return "";

        // First try: Use the drop prefab name directly
         if (item.m_dropPrefab != null)
         {
        string name = item.m_dropPrefab.name;
            if (name.EndsWith("(Clone)"))
           name = name.Substring(0, name.Length - 7).Trim();
     return name;
            }

      // Second try: Look up the item in ObjectDB by its shared data
            if (ObjectDB.instance != null && item.m_shared != null)
      {
     foreach (var prefab in ObjectDB.instance.m_items)
     {
   if (prefab == null) continue;
   var itemDrop = prefab.GetComponent<ItemDrop>();
        if (itemDrop == null || itemDrop.m_itemData?.m_shared == null) continue;

           if (itemDrop.m_itemData.m_shared.m_name == item.m_shared.m_name)
           {
 return prefab.name;
               }
    }
            }

            // Third try: Try looking up by the localized name directly in ObjectDB
      if (item.m_shared?.m_name != null && ObjectDB.instance != null)
            {
    var itemPrefab = ObjectDB.instance.GetItemPrefab(item.m_shared.m_name);
       if (itemPrefab != null)
{
            return itemPrefab.name;
       }
      }

      Debug.LogWarning($"[CompanionInventory] Could not determine prefab name for item: {item.m_shared?.m_name ?? "Unknown"}");
        return "";
        }

        /// <summary>
/// Recalculates all equipment bonuses from equipped items.
        /// </summary>
        private void RecalculateEquipmentBonuses()
      {
    bonusHealth = 0f;
          bonusArmor = 0f;
            bonusMovementSpeed = 0f;

 foreach (var item in _equippedItems.Values)
            {
    if (item?.m_shared == null) continue;

         var shared = item.m_shared;

       // Armor value
 bonusArmor += shared.m_armor;
                if (item.m_quality > 1)
                {
          bonusArmor += shared.m_armorPerLevel * (item.m_quality - 1);
         }

         // Movement modifier
       bonusMovementSpeed += shared.m_movementModifier;
       }

     // Only log equipment bonuses for tamed companions when verbose is enabled
     if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
         Debug.Log($"[CompanionInventory] Equipment bonuses - Armor: {bonusArmor:F1}, Speed: {bonusMovementSpeed:F2}");
        }

    /// <summary>
        /// Applies visual equipment using the same system as NpcController.
        /// This is optimized to only update slots that have changed.
    /// </summary>
        public void ApplyVisualEquipment()
        {
            // Static NPCs don't use the companion inventory — their gear lives directly in the VisEquipment
            // ZDO, set from the dressing room's equip fields via NpcVisEquipment. Their CompanionInventory is
            // empty, so applying it here would strip the gear (this is what made a patrolling static NPC walk
            // off naked once it ran companion behaviors like weapon-holstering). The static path owns visuals.
            var staticModule = GetComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>();
            if (staticModule != null && staticModule.isStaticPlacement)
                return;

            // CRITICAL: Validate weapon combinations before applying visuals
            // This catches any invalid states that might have slipped through
            ValidateAndFixWeaponCombinations();

            if (_visEquipment == null)
            {
                _visEquipment = GetComponent<NpcVisEquipment>();
                if (_visEquipment == null)
                    _visEquipment = gameObject.AddComponent<NpcVisEquipment>();
                _visEquipment.ForceReinitialize();
            }

            if (_visEquipment.VisEquipment == null)
            {
                _visEquipment.ForceReinitialize();

                // Still not ready ï¿½ schedule one final retry in 2s and bail.
                // This handles the rare edge case where NpcVisEquipment hasn't
                // finished its own DelayedInitialize yet at the 2.5s mark.
                if (_visEquipment.VisEquipment == null)
                {
                    if (!IsInvoking(nameof(ApplyVisualEquipment)))
                        Invoke(nameof(ApplyVisualEquipment), 2f);
                    return;
                }
            }

            var visualEquipment = GetEquipmentDataForVisuals();
            _visEquipment.ApplyEquipment(visualEquipment);

            var weaponScaler = GetComponent<CompanionWeaponScaler>();
            weaponScaler?.ClearTrackedInstances();
        }
        
        /// <summary>
        /// Validates current weapon combinations and fixes any invalid states.
        /// Called before applying visuals to ensure we never show impossible combinations.
        /// </summary>
        public void ValidateAndFixWeaponCombinations()
        {
            var leftHand = GetEquippedItem(EquipmentSlot.LeftHand);
            var rightHand = GetEquippedItem(EquipmentSlot.RightHand);
            
            // Check for bow + right-hand weapon (invalid)
            bool leftIsBow = leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow;
            bool rightIsWeapon = rightHand != null && rightHand.IsWeapon();
            
            if (leftIsBow && rightIsWeapon)
            {
                Debug.LogWarning($"[CompanionInventory] Invalid combo detected: bow + weapon. Moving weapon to back.");
                
                // Move the right-hand weapon to back
                UnequipSlotSilent(EquipmentSlot.RightHand);
                
                var rightBack = GetEquippedItem(EquipmentSlot.RightBack);
                if (rightBack == null)
                {
                    EquipItemSilent(EquipmentSlot.RightBack, rightHand);
                }
                else
                {
                    // Put in storage
                    var storage = GetStorageInventory();
                    storage?.AddItem(rightHand);
                }
            }
        }

        /// <summary>
        /// Converts equipped items to prefab name dictionary for NpcVisEquipment.
        /// CRITICAL: When a weapon is in hand, it should NOT also show on back.
        /// VisEquipment handles this naturally - items in RightHand/LeftHand are drawn,
        /// while items in RightBack/LeftBack are holstered on back.
        /// We need to ensure we don't put the same item in both hand and ANY back slot.
        /// 
        /// ALSO CRITICAL: This method now clears stale back slot data from the internal
        /// prefab name fields to prevent the visual system from showing duplicate weapons.
        /// </summary>
        public Dictionary<CompanionInventory.EquipmentSlot, string> GetEquipmentDataForVisuals()
        {
            var equipment = new Dictionary<CompanionInventory.EquipmentSlot, string>();

            // Armor slots - always show
            equipment[CompanionInventory.EquipmentSlot.Helmet] = _equipHelmet ?? "";
            equipment[CompanionInventory.EquipmentSlot.Chest] = _equipChest ?? "";
            equipment[CompanionInventory.EquipmentSlot.Legs] = _equipLegs ?? "";
            equipment[CompanionInventory.EquipmentSlot.Shoulder] = _equipShoulder ?? "";
            equipment[CompanionInventory.EquipmentSlot.Utility] = _equipUtility ?? "";
            
            // Weapon slots - hand takes priority over back for the same weapon
            string rightHand = _equipRightHand ?? "";
            string leftHand = _equipLeftHand ?? "";
            string rightBack = _equipRightBack ?? "";
            string leftBack = _equipLeftBack ?? "";
            
            // Helper to normalize prefab names for comparison (strip (Clone) suffix)
            string NormalizeName(string name)
            {
                if (string.IsNullOrEmpty(name)) return "";
                if (name.EndsWith("(Clone)"))
                    return name.Substring(0, name.Length - 7);
                return name;
            }
            
            string rightHandNorm = NormalizeName(rightHand);
            string leftHandNorm = NormalizeName(leftHand);
            string rightBackNorm = NormalizeName(rightBack);
            string leftBackNorm = NormalizeName(leftBack);
            
            // Track if we need to clear any back slot prefab names
            bool needsSave = false;
            
            // If a weapon is in either hand, clear it from BOTH back slots to prevent double-showing
            // This handles cases like bow in LeftHand matching an old entry in RightBack
            if (!string.IsNullOrEmpty(rightHandNorm))
            {
                if (rightBackNorm == rightHandNorm)
                {
                    rightBack = "";
                    // CRITICAL: Also clear the internal field to prevent persistence issues
                    if (!string.IsNullOrEmpty(_equipRightBack) && NormalizeName(_equipRightBack) == rightHandNorm)
                    {
                        _equipRightBack = "";
                        needsSave = true;
                    }
                }
                if (leftBackNorm == rightHandNorm)
                {
                    leftBack = "";
                    if (!string.IsNullOrEmpty(_equipLeftBack) && NormalizeName(_equipLeftBack) == rightHandNorm)
                    {
                        _equipLeftBack = "";
                        needsSave = true;
                    }
                }
            }
            if (!string.IsNullOrEmpty(leftHandNorm))
            {
                if (rightBackNorm == leftHandNorm)
                {
                    rightBack = "";
                    if (!string.IsNullOrEmpty(_equipRightBack) && NormalizeName(_equipRightBack) == leftHandNorm)
                    {
                        _equipRightBack = "";
                        needsSave = true;
                    }
                }
                if (leftBackNorm == leftHandNorm)
                {
                    leftBack = "";
                    if (!string.IsNullOrEmpty(_equipLeftBack) && NormalizeName(_equipLeftBack) == leftHandNorm)
                    {
                        _equipLeftBack = "";
                        needsSave = true;
                    }
                }
            }
            
            // ALSO check for duplicates between back slots themselves
            // Sometimes the same weapon ends up in both RightBack and LeftBack
            if (!string.IsNullOrEmpty(rightBackNorm) && rightBackNorm == leftBackNorm)
            {
                // Keep only one - prefer the correct slot based on weapon type
                // We'll check _equippedItems to see which one has the actual item
                var rightBackItem = GetEquippedItem(EquipmentSlot.RightBack);
                var leftBackItem = GetEquippedItem(EquipmentSlot.LeftBack);
                
                // If both have items, clear one based on weapon type (ranged goes left, melee goes right)
                if (rightBackItem != null && leftBackItem == null)
                {
                    leftBack = "";
                    _equipLeftBack = "";
                    needsSave = true;
                }
                else if (leftBackItem != null && rightBackItem == null)
                {
                    rightBack = "";
                    _equipRightBack = "";
                    needsSave = true;
                }
                else if (rightBackItem != null && leftBackItem != null)
                {
                    // Both have items with same name - this is a bug, clear rightBack
                    rightBack = "";
                    _equipRightBack = "";
                    _equippedItems.Remove(EquipmentSlot.RightBack);
                    needsSave = true;
                }
            }
            
            // Save if we made corrections to prevent future issues
            if (needsSave && !_isLoadingFromZDO)
            {
                SaveToZDO();
            }
            
            equipment[CompanionInventory.EquipmentSlot.RightHand] = rightHand;
            equipment[CompanionInventory.EquipmentSlot.LeftHand] = leftHand;
            equipment[CompanionInventory.EquipmentSlot.RightBack] = rightBack;
            equipment[CompanionInventory.EquipmentSlot.LeftBack] = leftBack;

            return equipment;
        }
        
        /// <summary>
        /// Gets all equipment prefab names and qualities for vault persistence.
        /// This returns data from the prefab name fields, not from _equippedItems.
        /// </summary>
   public Dictionary<EquipmentSlot, (string prefab, int quality)> GetAllEquipmentForVault()
        {
    var result = new Dictionary<EquipmentSlot, (string prefab, int quality)>();
   
            if (!string.IsNullOrEmpty(_equipHelmet))
            result[EquipmentSlot.Helmet] = (_equipHelmet, _equipHelmetQuality);
            if (!string.IsNullOrEmpty(_equipChest))
            result[EquipmentSlot.Chest] = (_equipChest, _equipChestQuality);
            if (!string.IsNullOrEmpty(_equipLegs))
            result[EquipmentSlot.Legs] = (_equipLegs, _equipLegsQuality);
            if (!string.IsNullOrEmpty(_equipShoulder))
            result[EquipmentSlot.Shoulder] = (_equipShoulder, _equipShoulderQuality);
            if (!string.IsNullOrEmpty(_equipUtility))
            result[EquipmentSlot.Utility] = (_equipUtility, _equipUtilityQuality);
            if (!string.IsNullOrEmpty(_equipRightHand))
            result[EquipmentSlot.RightHand] = (_equipRightHand, _equipRightHandQuality);
            if (!string.IsNullOrEmpty(_equipLeftHand))
            result[EquipmentSlot.LeftHand] = (_equipLeftHand, _equipLeftHandQuality);
            if (!string.IsNullOrEmpty(_equipRightBack))
            result[EquipmentSlot.RightBack] = (_equipRightBack, _equipRightBackQuality);
            if (!string.IsNullOrEmpty(_equipLeftBack))
            result[EquipmentSlot.LeftBack] = (_equipLeftBack, _equipLeftBackQuality);
           
     return result;
     }

        #endregion

        #region Persistence

        public void SaveToZDO()
        {
            // Don't save during initial load
          if (_isLoadingFromZDO)
 {
      return;
            }

            // Skip during local player respawn / loading screen â€” companion-inventory
            // ZDO writes during IsTeleporting=true deadlock the zone stream.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

   var zdo = _nview?.GetZDO();
        if (zdo == null) return;

            try
            {
         // Save storage inventory data
     {
      var pkg = new ZPackage();
                _inventory.Save(pkg);
             zdo.Set("companion_inventory", pkg.GetBase64());
           }

                // Save all equipment slots packed into ONE base64-encoded ZPackage field
                // (replaces 18 individual ZDO fields ï¿½ 9 prefab strings + 9 quality ints ï¿½
                // which were pushing companion ZDOs over Valheim's "Writing a lot of data;
                // N items" warning threshold during world saves).
                {
                    var eqPkg = new ZPackage();
                    eqPkg.Write(1); // version, for future schema changes
                    eqPkg.Write(_equipHelmet ?? "");      eqPkg.Write(_equipHelmetQuality);
                    eqPkg.Write(_equipChest ?? "");       eqPkg.Write(_equipChestQuality);
                    eqPkg.Write(_equipLegs ?? "");        eqPkg.Write(_equipLegsQuality);
                    eqPkg.Write(_equipShoulder ?? "");    eqPkg.Write(_equipShoulderQuality);
                    eqPkg.Write(_equipUtility ?? "");     eqPkg.Write(_equipUtilityQuality);
                    eqPkg.Write(_equipRightHand ?? "");   eqPkg.Write(_equipRightHandQuality);
                    eqPkg.Write(_equipLeftHand ?? "");    eqPkg.Write(_equipLeftHandQuality);
                    eqPkg.Write(_equipRightBack ?? "");   eqPkg.Write(_equipRightBackQuality);
                    eqPkg.Write(_equipLeftBack ?? "");    eqPkg.Write(_equipLeftBackQuality);
                    zdo.Set("companion_equipment", eqPkg.GetBase64());
                }

                // Strip the legacy per-slot QUALITY ints so previously-saved companion
                // ZDOs shed their bloat on the next save. RemoveInt is a no-op if the key
                // isn't present, so this is safe for fresh ZDOs.
                //
                // NOTE: Valheim's ZDOExtraData exposes no RemoveString, so the 9 legacy
                // string fields (companion_equip_*) on pre-migration companions will
                // persist as dead keys until that companion is destroyed and re-spawned.
                // Fresh companions never write them so they don't accumulate.
                zdo.RemoveInt("companion_equip_helmet_quality");
                zdo.RemoveInt("companion_equip_chest_quality");
                zdo.RemoveInt("companion_equip_legs_quality");
                zdo.RemoveInt("companion_equip_shoulder_quality");
                zdo.RemoveInt("companion_equip_utility_quality");
                zdo.RemoveInt("companion_equip_righthand_quality");
                zdo.RemoveInt("companion_equip_lefthand_quality");
                zdo.RemoveInt("companion_equip_rightback_quality");
                zdo.RemoveInt("companion_equip_leftback_quality");
            }
         catch (Exception ex)
          {
     Debug.LogWarning($"[CompanionInventory] Failed to save: {ex.Message}");
 }
        }

        /// <summary>
        /// Triggers a save to ZDO for the inventory and equipment data.
        /// </summary>
        public void TriggerSaveToZDO()
     {
         SaveToZDO();
        }
        
        #endregion
        
        #region Inventory Status Checks
        
        /// <summary>
        /// Detailed status of companion inventory capacity.
        /// This is the SINGLE SOURCE OF TRUTH for inventory fullness checks.
        /// All other systems should use GetInventoryStatus() instead of duplicating this logic.
        /// </summary>
        public class InventoryStatus
        {
            /// <summary>Cannot add any more items (no slots or at weight limit)</summary>
            public bool IsFull;
            
            /// <summary>Getting full (>80% weight or fewer than 3 slots)</summary>
            public bool IsNearlyFull;
            
            /// <summary>Over carry weight limit</summary>
            public bool IsOverweight;
            
            /// <summary>Current total weight of all items</summary>
            public float CurrentWeight;
            
            /// <summary>Maximum carry weight capacity</summary>
            public float MaxWeight;
            
            /// <summary>Percentage of weight capacity used (0.0 to 1.0+)</summary>
            public float WeightPercent;
            
            /// <summary>Number of empty inventory slots</summary>
            public int FreeSlots;
            
            /// <summary>Total number of inventory slots</summary>
            public int TotalSlots;
            
            /// <summary>Human-readable reason for current status</summary>
            public string Reason;
            
            /// <summary>True if companion should deposit items before gathering more</summary>
            public bool NeedsDeposit => IsFull || IsNearlyFull || IsOverweight;
            
            /// <summary>True if companion can pick up at least one more item</summary>
            public bool CanPickUpMore => !IsFull && !IsOverweight;
            
            /// <summary>Gets the weight capacity remaining (can be negative if overweight)</summary>
            public float RemainingWeightCapacity => MaxWeight - CurrentWeight;
        }
        
        /// <summary>
        /// Gets the detailed inventory status for this companion.
        /// This is the SINGLE SOURCE OF TRUTH - all inventory checks should go through here.
        /// 
        /// Use this for:
        /// - Deciding whether to start gathering behaviors
        /// - Deciding whether to deposit items first
        /// - Checking if companion can pick up an item
        /// - UI displays of inventory capacity
        /// </summary>
        public InventoryStatus GetInventoryStatus()
        {
            EnsureInitialized();
            
            var status = new InventoryStatus();
            
            if (_inventory == null)
            {
                status.IsFull = true;
                status.Reason = "No inventory";
                return status;
            }
            
            // Weight calculations
            status.MaxWeight = GetMaxCarryWeight();
            status.CurrentWeight = GetTotalWeight();
            status.WeightPercent = status.MaxWeight > 0 ? status.CurrentWeight / status.MaxWeight : 1f;
            status.IsOverweight = status.CurrentWeight > status.MaxWeight;
            
            // Slot calculations
            status.TotalSlots = _inventory.GetWidth() * _inventory.GetHeight();
            status.FreeSlots = _inventory.GetEmptySlots();
            
            // Determine status based on thresholds
            if (status.IsOverweight)
            {
                status.IsFull = true;
                status.IsNearlyFull = true;
                status.Reason = $"Overweight ({status.CurrentWeight:F0}/{status.MaxWeight:F0})";
            }
            else if (status.FreeSlots == 0)
            {
                status.IsFull = true;
                status.IsNearlyFull = true;
                status.Reason = "No empty slots";
            }
            else if (status.WeightPercent >= 0.95f)
            {
                status.IsFull = true;
                status.IsNearlyFull = true;
                status.Reason = $"Almost at weight limit ({status.WeightPercent:P0})";
            }
            else if (status.WeightPercent >= 0.80f || status.FreeSlots < 3)
            {
                status.IsNearlyFull = true;
                status.Reason = status.WeightPercent >= 0.80f 
                    ? $"Heavy ({status.WeightPercent:P0})" 
                    : $"Few slots left ({status.FreeSlots})";
            }
            else
            {
                status.Reason = $"{status.FreeSlots} slots free ({status.WeightPercent:P0} weight)";
            }
            
            return status;
        }
        
        /// <summary>
        /// Quick check if inventory is full or nearly full (needs deposit).
        /// Convenience method that wraps GetInventoryStatus().
        /// </summary>
        public bool IsInventoryFullOrNearlyFull()
        {
            return GetInventoryStatus().NeedsDeposit;
        }
        
        /// <summary>
        /// Quick check if companion can pick up more items.
        /// Optionally considers the weight of an item to be picked up.
        /// </summary>
        /// <param name="estimatedItemWeight">Weight of item being considered (default 1.0)</param>
        public bool CanPickUpMoreItems(float estimatedItemWeight = 1f)
        {
            EnsureInitialized();
            if (_inventory == null) return false;
            
            // Check weight capacity
            float newWeight = GetTotalWeight() + estimatedItemWeight;
            if (newWeight > GetMaxCarryWeight()) return false;
            
            // Check slot availability
            if (_inventory.GetEmptySlots() == 0) return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if the companion should prioritize depositing items.
        /// Returns true if inventory needs deposit AND there are depositable items.
        /// </summary>
        public bool ShouldPrioritizeDeposit()
        {
            var status = GetInventoryStatus();
            if (!status.NeedsDeposit) return false;
            
            // Check if we have anything TO deposit (non-equipment, non-food)
            var depositableItems = GetDepositableItems();
            return depositableItems.Count > 0;
        }
        
        /// <summary>
        /// Gets items that should be deposited to chests.
        /// Excludes: equipped items, food, weapons, armor, tools.
        /// This is the canonical list of "depositable" items.
        /// </summary>
        public List<ItemDrop.ItemData> GetDepositableItems()
        {
            EnsureInitialized();
            var depositable = new List<ItemDrop.ItemData>();
            
            if (_inventory == null) return depositable;
            
            foreach (var item in _inventory.GetAllItems())
            {
                if (item == null) continue;
                
                // Skip food (companions need this)
                if (item.m_shared.m_food > 0) continue;
                
                // Skip weapons
                if (item.IsWeapon()) continue;
                
                // Skip equipment/armor/tools
                var itemType = item.m_shared.m_itemType;
                if (itemType == ItemDrop.ItemData.ItemType.Helmet ||
                    itemType == ItemDrop.ItemData.ItemType.Chest ||
                    itemType == ItemDrop.ItemData.ItemType.Legs ||
                    itemType == ItemDrop.ItemData.ItemType.Shoulder ||
                    itemType == ItemDrop.ItemData.ItemType.Utility ||
                    itemType == ItemDrop.ItemData.ItemType.Shield ||
                    itemType == ItemDrop.ItemData.ItemType.Bow ||
                    itemType == ItemDrop.ItemData.ItemType.Tool)
                {
                    continue;
                }
                
                depositable.Add(item);
            }
            
            return depositable;
        }
        
        #endregion
        
        #region Ghost Preview Check
        
        /// <summary>
        /// Checks if this object is a ghost/preview object (hammer placement preview).
        /// Ghost objects don't have valid ZNetViews and shouldn't have equipment operations performed.
        /// </summary>
        private bool IsGhostPreview()
        {
            // Check for ghost indicator in name
            if (gameObject.name.Contains("(Clone)") && gameObject.name.Contains("ghost"))
                return true;
            
            // Check if ZNetView is invalid (ghosts either have no ZNetView or an invalid one)
            if (_nview == null)
                return true;
            
            // Check if ZDO is null (ghosts don't have ZDOs)
            if (!_nview.IsValid() || _nview.GetZDO() == null)
                return true;
            
            return false;
        }
        #endregion
    }
}
