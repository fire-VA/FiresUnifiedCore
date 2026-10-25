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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    public class CompanionInventory : MonoBehaviour
    {
        private const float CarryWeightPerStrengthPoint = 5f;
        private const float CarryWeightPerEndurancePoint = 3f;
        private const float MegingjordCarryWeightBonus = 150f;
        private const float VisualEquipmentApplyDelay = 2.5f;
        private const float FullWeightFraction = 0.95f;
        private const float NearlyFullWeightFraction = 0.80f;
        private const int NearlyFullFreeSlotThreshold = 3;

        [Header("Inventory Settings")]
        // The backing inventory holds every visible UI cell (storage plus the food and ammo rows); a smaller one silently
        // ate items dropped onto unbacked cells. Growing it is save-safe because items keep their stored grid positions.
        public int inventoryWidth = CompanionGridLayout.Width;   // Fixed 8 x 11 VA-style grid (CompanionGridLayout)
        public int inventoryHeight = CompanionGridLayout.Height;
        public float maxCarryWeight = 150f;

        [Header("Equipment Bonuses")]
        public float bonusHealth = 0f;
        public float bonusArmor = 0f;
        public float bonusMovementSpeed = 0f;

        // No separate equipment record any more. The bag is laid out like VA Inventory's player inventory
        // (CompanionGridLayout): a worn item is the item in its equip cell, equipped on the Humanoid by vanilla EquipItem
        // (Reconcile keeps the two together). Durability, quality and stack live on the ItemData and save with the bag.
        private const float DurabilityUnset = -1f;
        private const string GridVersionKey = "companion_grid_v";
        // V2 = the 8 x 18 grid with every player slot (v1 cells unchanged: an old save loads in place, the new cells empty)
        private const int GridVersion = 2;

        // Reconcile state: re-entrancy guard, a retry when vanilla refused an equip (attacking, dodging, broken), and the
        // legacy vault equipment queued until the bag that follows it has loaded.
        private int _reconcileDepth;
        private bool _reconcileAgain;
        private bool _reconcileRetry;
        private float _nextReconcileRetry;
        private bool _loggedDimensions;
        // Legacy vault equipment is only COLLECTED here; CommitLegacyVaultEquipment places it synchronously right
        // after the vault's bag Load (no timed queue, so no window in which an emptied cell is re-filled with a copy).
        private readonly List<(EquipmentSlot slot, string prefab, int quality, int stack)> _pendingLegacyEquip = new List<(EquipmentSlot, string, int, int)>();
        private const string LegacyVaultMigratedKey = "companion_legacy_vault_migrated";

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
        private const float RemoteSyncInterval = 2f; // Check every 2 seconds for remote changes
        private int _lastEquipmentHash = 0; // Hash of equipment state for change detection


        // Events for equipment changes
        public event Action<EquipmentSlot, ItemDrop.ItemData> OnEquipmentChanged;
        public event Action OnInventoryChanged;

        private static readonly System.Reflection.FieldInfo s_dragInventoryField = HarmonyLib.AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private bool _reloadDeferred;
        // True while the local cursor holds an item taken from this companion's bag: a reload now would swap the ItemData
        // under the cursor and vanilla would drop the drag.
        private bool CursorHoldsFromThisBag()
            => InventoryGui.instance != null && _inventory != null && ReferenceEquals(s_dragInventoryField?.GetValue(InventoryGui.instance), _inventory);

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
      LeftBack,
      Trinket   // Vanilla m_trinketItem; append-only (vault saves parse names)
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
            if (_reloadDeferred && !CursorHoldsFromThisBag()) LoadFromZDO();   // the drag ended: the reload deferred for it runs now

            // Vanilla refused an equip (attacking, dodging, broken): retry shortly; the item never bounces.
            if (_reconcileRetry && Time.time >= _nextReconcileRetry)
            {
                _nextReconcileRetry = Time.time + 0.5f;
                Reconcile("retry");
            }

            // Every peer: re-apply the look when the barber (or anything) changes it in the ZDO.
            _lastAppearanceCheck += Time.deltaTime;
            if (_lastAppearanceCheck >= RemoteSyncInterval)
            {
                _lastAppearanceCheck = 0f;
                CheckForAppearanceChanges();
            }

            // Owner: tick the debounced vault auto-save so picked-up items survive
            // logout/login.
            if (_nview.IsOwner())
            {
                TickVaultAutoSave();
                return;
            }

            // Non-owner clients: periodic sync check for equipment changes.
            _lastRemoteSyncCheck += Time.deltaTime;
            if (_lastRemoteSyncCheck >= RemoteSyncInterval)
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
            // Worn gear lives in the bag (grid cells), so the bag itself is the equipment signature.
            hash = hash * 31 + zdo.GetString("companion_inventory", "").GetHashCode();
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
            hash = hash * 31 + CalculateAppearanceHash(zdo);

            return hash;
        }

        // The look alone (barber changes: styles, colours, model, scale). Checked on every peer, owner included.
        private static int CalculateAppearanceHash(ZDO zdo)
        {
            int hash = 17;
            hash = hash * 31 + zdo.GetString("companion_hair", "").GetHashCode();
            hash = hash * 31 + zdo.GetString("companion_beard", "").GetHashCode();
            hash = hash * 31 + (zdo.GetBool("companion_isfemale", false) ? 1 : 0);
            hash = hash * 31 + zdo.GetInt(ZDOVars.s_modelIndex, 0);
            hash = hash * 31 + zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one).GetHashCode();
            hash = hash * 31 + zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one).GetHashCode();
            hash = hash * 31 + zdo.GetFloat("companion_scale", 1f).GetHashCode();
            hash = hash * 31 + zdo.GetVec3(CompanionAppearanceService.BeardColorKey, Vector3.zero).GetHashCode();
            return hash;
        }

        private float _lastAppearanceCheck;
        private int _lastAppearanceHash;
        private bool _appearanceHashSeeded;

        private void CheckForAppearanceChanges()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            int h = CalculateAppearanceHash(zdo);
            if (!_appearanceHashSeeded) { _appearanceHashSeeded = true; _lastAppearanceHash = h; return; }
            if (h == _lastAppearanceHash) return;
            _lastAppearanceHash = h;
            FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName}: look changed in its ZDO, re-applying");
            GetComponent<CompanionRandomLoadout>()?.RefreshAppearanceFromZDO();
            if (_nview.IsOwner()) MarkVaultDirty();
        }

        /// <summary>Queue the debounced vault save (the barber's new look survives a respawn).</summary>
        public void MarkVaultDirty() => _vaultDirtySinceTime = Time.unscaledTime;

        #endregion

        #region Initialization

        private void InitializeInventory()
        {
 if (_initialized) return;
            
    // Create inventory with custom size (storage grid)
   string inventoryName = $"Companion_{_companion?.companionName ?? "Unknown"}";
            // The storage IS the Humanoid's own bag, so vanilla's UseItem, Pickup, CookingStation,
            // fishing float and door-key paths (all Humanoid.GetInventory()) act on it directly. Humanoid's field initializer
            // made it before any Awake; it is sized to the companion grid here, while still empty (GiveDefaultItems is
            // skipped for companions below, so nothing lands in it before LoadFromZDO).
            // A prefab may carry the old serialized 6-wide value; the companion grid is 8 x 4 (32 slots)
            // Fixed 8 x 11 (CompanionGridLayout). Growing never moves an item (each keeps its saved grid position), so a
            // bag that already holds items is grown too; it is never shrunk.
            inventoryWidth = CompanionGridLayout.Width;
            inventoryHeight = CompanionGridLayout.Height;
            Humanoid body = GetComponent<Humanoid>();
            _inventory = body != null ? body.GetInventory() : null;
            if (_inventory == null)
                _inventory = new Inventory(inventoryName, null, inventoryWidth, inventoryHeight);
            else
            {
                if (_inventory.NrOfItems() == 0) _inventory.m_name = inventoryName;
                else FiresLogger.LogDebug($"[CompanionInventory] {inventoryName}: the Humanoid bag already holds {_inventory.NrOfItems()} item(s) at init; growing {_inventory.GetWidth()}x{_inventory.GetHeight()} in place");
                if (_inventory.m_width < inventoryWidth) _inventory.m_width = inventoryWidth;
                if (_inventory.m_height < inventoryHeight) _inventory.m_height = inventoryHeight;
            }

    // Hook into inventory change events
            _inventory.m_onChanged += OnStorageInventoryChanged;
            CompanionGridLayout.Register(_inventory, this);

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

            Reconcile("bag changed");

            SaveToZDO();
            OnInventoryChanged?.Invoke();

            // Mark dirty for cross-session vault persistence.
            // Vault save is debounced (see TickVaultAutoSave) so rapid pickups
            // during gathering don't hammer the vault every frame, but the data
            // never sits unsaved long enough to be lost on a logout.
            _vaultDirtySinceTime = Time.unscaledTime;
    }

       // Vault auto-save (debounced)
       // ZDO saves persist with the world but companions are restored from the
       // VAULT on logout/login and on respawn.  If we only update ZDO, anything
       // picked up since the last explicit vault save is lost across a session.
       private float _vaultDirtySinceTime = -1f;
       private float _lastVaultSaveTime;
       private const float VaultSaveDebounce = 2.0f;   // wait this long after last change
       private const float VaultSaveMaxDelay = 8.0f;  // never wait longer than this
       private const float VaultSaveMinInterval = 1.5f; // never save more often than this

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
           bool quiet  = dirtyFor >= VaultSaveDebounce;
           bool overdue = dirtyFor >= VaultSaveMaxDelay;
           if (!quiet && !overdue) return;
           if (sinceLastSave < VaultSaveMinInterval) return;

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
        /// <summary>True once the storage has been filled from the ZDO (LoadFromZDO, after Awake). Until then a freshly
        /// re-created companion's storage reads empty.</summary>
        public bool StorageLoaded => _hasCompletedInitialLoad;

      public Inventory GetStorageInventory()
        {
EnsureInitialized();
            return _inventory;
        }

        /// <summary>The first carried ammo that fits the weapon (arrows for bows, bolts for crossbows), like vanilla's ammo pick.</summary>
        public ItemDrop.ItemData FindAmmoFor(ItemDrop.ItemData weapon)
        {
            string ammoType = weapon?.m_shared?.m_ammoType;
            if (string.IsNullOrEmpty(ammoType)) return null;
            var pick = GetStorageInventory()?.GetAllItems().FirstOrDefault(item =>
                item.m_shared.m_ammoType == ammoType
                && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable)
                && item.m_shared.m_attack?.m_attackProjectile != null);
            // The Ammo pouch stack first when it fits the weapon.
            return CompanionPouchRules.PreferPouchAmmo(this, weapon, pick, _companion?.companionName);
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
            // Worn and pouch items are in the bag, so the bag's weight already counts them.
            float weight = _inventory?.GetTotalWeight() ?? 0f;
  
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
                strengthBonus = strength * CarryWeightPerStrengthPoint;
                
                // Endurance attribute bonus: +3 carry capacity per Endurance point
                int endurance = progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                enduranceBonus = endurance * CarryWeightPerEndurancePoint;
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
                string prefabName = GetItemPrefabName(utilityItem).ToLower();
                if (prefabName.Contains("beltstrength") || prefabName.Contains("megingjord"))
                {
                    // Megingjord adds +150 carry weight
                    // Only add if not already added via status effect
                    if (statusEffect == null || !(statusEffect is SE_Stats) || utilityBonus == 0f)
                    {
                        utilityBonus = MegingjordCarryWeightBonus;
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
                strengthBonus = strength * CarryWeightPerStrengthPoint;
                endurance = progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                enduranceBonus = endurance * CarryWeightPerEndurancePoint;
            }
            
            var utilityItem = GetEquippedItem(EquipmentSlot.Utility);
            if (utilityItem != null)
            {
                utilityName = utilityItem.m_shared.m_name ?? "Unknown";
                var statusEffect = utilityItem.m_shared.m_equipStatusEffect;
                if (statusEffect != null && statusEffect is SE_Stats seStats)
                {
                    utilityBonus = seStats.m_addMaxCarryWeight;
                }
                string prefabName = GetItemPrefabName(utilityItem).ToLower();
                if ((prefabName.Contains("beltstrength") || prefabName.Contains("megingjord")) && utilityBonus == 0f)
                {
                    utilityBonus = MegingjordCarryWeightBonus;
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

         // The same ItemData twice in the bag would be a dupe (worn and pouch items live in the bag now).
         if (_inventory.ContainsItem(item)) return true;
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
        public List<ItemDrop.ItemData> GetAllStorageItems() => GetStorageItems();

        /// <summary>The items in the STORAGE rows only (rows 0-3): never worn gear, never pouch stacks. Deposits,
        /// hauling, free-space checks and "what do I carry" readers use this.</summary>
        public List<ItemDrop.ItemData> GetStorageItems()
        {
            EnsureInitialized();
            var list = new List<ItemDrop.ItemData>();
            if (_inventory == null) return list;
            foreach (var it in _inventory.GetAllItems())
                if (it != null && CompanionGridLayout.IsStorage(it.m_gridPos)) list.Add(it);
            return list;
        }

        /// <summary>What the companion can reach to use, as the player uses its whole bag: the storage rows plus the Misc rows and
        /// the worn bag's rows (never worn gear, never pouches: those are read through their own API). Add-only.</summary>
        public List<ItemDrop.ItemData> GetCarriedItems()
        {
            var list = GetStorageItems();
            if (_inventory == null) return list;
            foreach (var name in new[] { "Misc", "BackpackStorage" })
                foreach (var it in GetSection(name)) if (it != null) list.Add(it);
            return list;
        }

        /// <summary>The items in the equipment cells (armour, hands, back).</summary>
        public List<ItemDrop.ItemData> GetWornItems()
        {
            EnsureInitialized();
            var list = new List<ItemDrop.ItemData>();
            if (_inventory == null) return list;
            foreach (var it in _inventory.GetAllItems())
                if (it != null && CompanionGridLayout.TryGetEquipSlot(it.m_gridPos, out _)) list.Add(it);
            return list;
        }

        /// <summary>The stacks in one pouch row, in cell order (null for an empty cell).</summary>
        public ItemDrop.ItemData[] GetPouchItems(CompanionPouchRules.PouchKind kind)
        {
            EnsureInitialized();
            int n = CompanionGridLayout.PouchCapacity(kind);
            var cells = new ItemDrop.ItemData[n];
            if (_inventory == null) return cells;
            for (int i = 0; i < n; i++)
            {
                var p = CompanionGridLayout.PouchCell(kind, i);
                cells[i] = _inventory.GetItemAt(p.x, p.y);
            }
            return cells;
        }

        // ---- Sections (companions get every player slot but the trash can, identical to the player) ----------------
        // Keyed by VAInventory's Slots ids (CompanionGridLayout.Sections). FiresCompanions' CompanionPanelApi wraps these. Add-only.

        /// <summary>Raised with the section name after Put / Take changed it.</summary>
        public event Action<string> SectionChanged;

        /// <summary>The sections this companion has right now (Misc only while VAInventory gives the player Misc rows; BackpackStorage
        /// only while a backpack is in the Backpack cell).</summary>
        public IReadOnlyList<string> SectionNames
        {
            get
            {
                var list = new List<string>();
                foreach (var s in CompanionGridLayout.Sections) if (ActiveCount(s) > 0) list.Add(s.Name);
                return list;
            }
        }

        /// <summary>The active cell count of a section: its full table, except Misc (the player's Misc cells, from VAInventory) and
        /// BackpackStorage (the equipped backpack's capacity, as VA_Backpacks gives the player).</summary>
        public int ActiveCount(CompanionGridLayout.Section s)
        {
            if (s == null) return 0;
            if (s.Name == "Tools") return CompanionGridLayout.PouchCapacity(CompanionPouchRules.PouchKind.Tool);
            if (s.Name == "Ammo") return CompanionGridLayout.PouchCapacity(CompanionPouchRules.PouchKind.Ammo);
            if (s.Name == "Food") return CompanionGridLayout.PouchCapacity(CompanionPouchRules.PouchKind.Food);
            if (s.Name == "Misc") return Mathf.Clamp(FiresSlotCatalog.CountOf("Misc", CompanionGridLayout.Width), 0, s.Cells.Length);
            if (s.Name == "BackpackStorage")
            {
                var bp = CompanionGridLayout.FindSection("Backpack");
                var pack = bp != null && _inventory != null ? _inventory.GetItemAt(bp.Cells[0].x, bp.Cells[0].y) : null;
                return Mathf.Clamp(FiresSlotCatalog.BagCells(pack), 0, s.Cells.Length);
            }
            return s.Cells.Length;
        }

        /// <summary>The section's active cells in order (null = empty); an empty array for an unknown or inactive section.</summary>
        public ItemDrop.ItemData[] GetSection(string section)
        {
            EnsureInitialized();
            var s = CompanionGridLayout.FindSection(section);
            int n = ActiveCount(s);
            var cells = new ItemDrop.ItemData[n];
            if (_inventory == null) return cells;
            for (int i = 0; i < n; i++) cells[i] = _inventory.GetItemAt(s.Cells[i].x, s.Cells[i].y);
            return cells;
        }

        /// <summary>Whether <paramref name="item"/> may go into the section's cell: an active, empty cell whose rule takes it.</summary>
        public bool CanPut(string section, int index, ItemDrop.ItemData item)
        {
            EnsureInitialized();
            var s = CompanionGridLayout.FindSection(section);
            if (s == null || item?.m_shared == null || _inventory == null || index < 0 || index >= ActiveCount(s)) return false;
            var cell = s.Cells[index];
            var occupant = _inventory.GetItemAt(cell.x, cell.y);
            if (occupant != null && occupant != item) return false;
            return CompanionGridLayout.Accepts(cell, item);
        }

        /// <summary>Puts the item (from storage, another section, or outside the bag) into the cell; Reconcile then equips / shows it.
        /// False = refused (cell taken, inactive, or the rule says no); the item is untouched then.</summary>
        public bool Put(string section, int index, ItemDrop.ItemData item)
        {
            if (!CanPut(section, index, item)) return false;
            var s = CompanionGridLayout.FindSection(section);
            var cell = s.Cells[index];
            if (item.m_gridPos.x == cell.x && item.m_gridPos.y == cell.y && _inventory.ContainsItem(item)) return true;
            _reconcileDepth++;
            try { SetCellRaw(item, cell); }
            finally { _reconcileDepth--; }
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: {GetItemPrefabName(item)} -> {section}[{index}] ({cell.x},{cell.y})");
            Reconcile($"put {section}[{index}]");
            try { SectionChanged?.Invoke(section); } catch (Exception ex) { FiresLogger.LogWarning($"[CompanionInventory] a SectionChanged handler threw: {ex.Message}"); }
            return true;
        }

        /// <summary>Moves the cell's item back to storage; null when the cell is empty or storage has no room (the item stays).</summary>
        public ItemDrop.ItemData Take(string section, int index)
        {
            EnsureInitialized();
            var s = CompanionGridLayout.FindSection(section);
            if (s == null || _inventory == null || index < 0 || index >= s.Cells.Length) return null;
            var cell = s.Cells[index];
            var item = _inventory.GetItemAt(cell.x, cell.y);
            if (item == null) return null;
            if (CompanionGridLayout.FirstFreeStorageCell(_inventory).x < 0) return null;
            var h = GetComponent<Humanoid>();
            if (h != null && item.m_equipped) h.UnequipItem(item, false);
            if (!MoveToStorageRaw(item, $"take {section}[{index}]")) return null;
            Reconcile($"take {section}[{index}]");
            try { SectionChanged?.Invoke(section); } catch (Exception ex) { FiresLogger.LogWarning($"[CompanionInventory] a SectionChanged handler threw: {ex.Message}"); }
            return item;
        }

        // ---- Bag: the SAME store as the player's (VA_Backpacks keeps a bag's items on the bag item as a base64 ZPackage of an
        // Inventory("Backpack", cols, rows) under m_customData["BackpackInventory"]; VAInventory BackpackStorageSync unpacks it into the
        // player's grid while worn and packs it back on unequip). Here: unpacked into BackpackStorage while the bag sits in the Backpack
        // cell, packed back onto the bag the moment it leaves, so the bag carries its contents to the player or a chest. Nothing is
        // ever deleted: an item with no bag cell goes to storage, else drops at the companion's feet.
        private ItemDrop.ItemData _bagItem;

        // ---- Jewelry: the SAME stats as a player's (FLO 0.1.9 FloJewelryApi.Recompute(wearer, ring, ringRight, necklace), its
        // per-wearer pipeline: max health share, SE_FiresCrafting, resists). On the ZDO owner, when the three cells change and once after load.
        private static bool s_floResolved;
        private static System.Reflection.MethodInfo s_floRecompute, s_floTotals;
        private string _jewelrySig;

        private void SyncJewelry(string why)
        {
            if (_inventory == null || Body == null) return;
            var nv = GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            ItemDrop.ItemData At(string s) { var sec = CompanionGridLayout.FindSection(s); return sec != null ? _inventory.GetItemAt(sec.Cells[0].x, sec.Cells[0].y) : null; }
            var ring = At("Ring"); var ringR = At("RingRight"); var neck = At("Necklace");
            string sig = $"{ring?.GetHashCode() ?? 0}|{ringR?.GetHashCode() ?? 0}|{neck?.GetHashCode() ?? 0}";
            if (sig == _jewelrySig) return;
            _jewelrySig = sig;
            if (!s_floResolved)
            {
                s_floResolved = true;
                var t = Type.GetType("FiresLootOverhaul.Api.FloJewelryApi, FiresLootOverhaul");
                s_floRecompute = t?.GetMethod("Recompute", new[] { typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData) });
                s_floTotals = Type.GetType("FiresLootOverhaul.FloApi, FiresLootOverhaul")?.GetMethod("GetTotals", new[] { typeof(Humanoid) });
                FiresLogger.LogDebug($"[CompanionInventory] FLO jewelry API: Recompute {s_floRecompute != null}, GetTotals {s_floTotals != null}");
            }
            if (s_floRecompute == null) return;
            try
            {
                s_floRecompute.Invoke(null, new object[] { Body, ring, ringR, neck });
                GetComponent<CompanionStats>()?.RecalculateMaxStats();   // picks up FLO's stamina totals
                FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: jewelry recomputed (ring {(ring != null ? GetItemPrefabName(ring) : "-")}, right {(ringR != null ? GetItemPrefabName(ringR) : "-")}, necklace {(neck != null ? GetItemPrefabName(neck) : "-")}; {why})");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[CompanionInventory] {CompanionNameForLog()}: FLO jewelry recompute threw: {(ex.InnerException ?? ex).Message}"); }
        }

        /// <summary>FLO's jewelry stamina bonuses for this companion: (flat max stamina, regen %); (0, 0) without FLO. Add-only.</summary>
        public Vector2 JewelryStaminaBonus()
        {
            if (s_floTotals == null || Body == null) return Vector2.zero;
            try { var a = s_floTotals.Invoke(null, new object[] { Body }) as float[]; return a != null && a.Length > 7 ? new Vector2(a[6], a[7]) : Vector2.zero; }
            catch { return Vector2.zero; }
        }

        // VABackpacks 1.0.78: the store is read/written ONLY through its public BackpackApi (format stays inside VABackpacks),
        // and the worn-bag visual is set with BackpackApi.SetWornBag(Humanoid, prefab). Exact Type.GetType; absent mod = no bags exist.
        private static bool s_bpResolved;
        private static System.Reflection.MethodInfo s_bpRead, s_bpWrite, s_bpWorn;
        private static void ResolveBackpackApi()
        {
            if (s_bpResolved) return;
            s_bpResolved = true;
            var t = Type.GetType("VerdantsAscent.Managers.BackpackApi, VABackpacks");
            if (t == null) { FiresLogger.LogDebug("[CompanionInventory] VABackpacks BackpackApi not found: companion bags hold no store"); return; }
            s_bpRead = t.GetMethod("ReadStorage", new[] { typeof(ItemDrop.ItemData) });
            s_bpWrite = t.GetMethod("WriteStorage", new[] { typeof(ItemDrop.ItemData), typeof(string) });
            s_bpWorn = t.GetMethod("SetWornBag", new[] { typeof(Humanoid), typeof(string) });
            FiresLogger.LogDebug($"[CompanionInventory] VABackpacks BackpackApi: ReadStorage {s_bpRead != null}, WriteStorage {s_bpWrite != null}, SetWornBag {s_bpWorn != null}");
        }
        private static string ReadBagStore(ItemDrop.ItemData bag) { ResolveBackpackApi(); try { return s_bpRead != null ? (string)s_bpRead.Invoke(null, new object[] { bag }) ?? "" : ""; } catch { return ""; } }
        private static bool WriteBagStore(ItemDrop.ItemData bag, string b64) { ResolveBackpackApi(); if (s_bpWrite == null) return false; try { s_bpWrite.Invoke(null, new object[] { bag, b64 }); return true; } catch { return false; } }
        private void SetWornBagVisual(ItemDrop.ItemData bag)
        {
            ResolveBackpackApi();
            if (s_bpWorn == null || Body == null) return;
            try { s_bpWorn.Invoke(null, new object[] { Body, bag != null ? GetItemPrefabName(bag) : "" }); } catch { }
        }

        private void SyncBag(string why)
        {
            if (_inventory == null) return;
            var bp = CompanionGridLayout.FindSection("Backpack");
            var now = bp != null ? _inventory.GetItemAt(bp.Cells[0].x, bp.Cells[0].y) : null;
            if (now == _bagItem) return;
            _reconcileDepth++;
            try
            {
                if (_bagItem != null) PackBag(_bagItem, why);
                _bagItem = now;
                if (now != null) UnpackBag(now, why);
                SetWornBagVisual(now);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[CompanionInventory] {CompanionNameForLog()}: bag sync failed ({ex.Message}); items left where they are"); }
            finally { _reconcileDepth--; }
        }

        private void PackBag(ItemDrop.ItemData bag, string why)
        {
            var sec = CompanionGridLayout.FindSection("BackpackStorage");
            var size = FiresSlotCatalog.BagSize(bag);
            int cols = Mathf.Max(1, size.x);
            var collected = new List<ItemDrop.ItemData>();
            for (int i = 0; i < sec.Cells.Length; i++)
            {
                var it = _inventory.GetItemAt(sec.Cells[i].x, sec.Cells[i].y);
                if (it != null) collected.Add(it);
            }
            if (collected.Count == 0) { WriteBagStore(bag, ""); FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: bag {GetItemPrefabName(bag)} packed empty ({why})"); return; }
            int rows = Mathf.Max(1, size.y);
            var tmp = new Inventory("Backpack", null, Mathf.Max(cols, CompanionGridLayout.Width), Mathf.Max(rows, CompanionGridLayout.BagMaxRows));
            var list = tmp.GetAllItems();
            var homes = new List<Vector2i>();
            foreach (var it in collected)
            {
                CompanionGridLayout.TryGetSection(it.m_gridPos, out _, out int idx);
                homes.Add(it.m_gridPos);
                it.m_gridPos = new Vector2i(idx % cols, idx / cols);
                list.Add(it);
            }
            var pkg = new ZPackage();
            tmp.Save(pkg);
            for (int i = 0; i < collected.Count; i++) collected[i].m_gridPos = homes[i];
            if (!WriteBagStore(bag, pkg.GetBase64())) { FiresLogger.LogWarning($"[CompanionInventory] {CompanionNameForLog()}: bag store write failed; the {collected.Count} item(s) stay in the companion bag rows"); return; }
            foreach (var it in collected) _inventory.RemoveItem(it);
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: bag {GetItemPrefabName(bag)} packed {collected.Count} item(s) ({why})");
        }

        private void UnpackBag(ItemDrop.ItemData bag, string why)
        {
            string b64 = ReadBagStore(bag);
            if (string.IsNullOrEmpty(b64)) return;
            var tmp = new Inventory("Backpack", null, 32, 32);   // as wide/tall as any bag: Inventory.Load refuses x >= width
            try { tmp.Load(new ZPackage(b64)); }
            catch (Exception ex) { FiresLogger.LogWarning($"[CompanionInventory] {CompanionNameForLog()}: bag {GetItemPrefabName(bag)} store unreadable ({ex.Message}); left on the bag"); return; }
            WriteBagStore(bag, "");
            var sec = CompanionGridLayout.FindSection("BackpackStorage");
            int active = ActiveCount(sec);
            int cols = Mathf.Max(1, FiresSlotCatalog.BagSize(bag).x);
            int inBag = 0, toStorage = 0, dropped = 0;
            foreach (var it in new List<ItemDrop.ItemData>(tmp.GetAllItems()))
            {
                int idx = it.m_gridPos.y * cols + it.m_gridPos.x;
                if (idx < 0 || idx >= active || _inventory.GetItemAt(sec.Cells[idx].x, sec.Cells[idx].y) != null)
                {
                    idx = -1;
                    for (int i = 0; i < active; i++) if (_inventory.GetItemAt(sec.Cells[i].x, sec.Cells[i].y) == null) { idx = i; break; }
                }
                if (idx >= 0) { SetCellRaw(it, sec.Cells[idx]); inBag++; continue; }
                var free = CompanionGridLayout.FirstFreeStorageCell(_inventory);
                if (free.x >= 0) { SetCellRaw(it, free); toStorage++; continue; }
                ItemDrop.DropItem(it, it.m_stack, transform.position + transform.forward * 0.5f + Vector3.up, Quaternion.identity);
                dropped++;
            }
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: bag {GetItemPrefabName(bag)} unpacked {inBag} into the bag, {toStorage} to storage, {dropped} dropped ({why})");
        }

        /// <summary>Storage items plus the hand cells (weapon pickers choose from what is carried and what is held).</summary>
        public List<ItemDrop.ItemData> GetStorageAndHandItems()
        {
            var list = GetStorageItems();
            var r = GetEquippedItem(EquipmentSlot.RightHand);
            var l = GetEquippedItem(EquipmentSlot.LeftHand);
            if (r != null) list.Add(r);
            if (l != null && l != r) list.Add(l);
            return list;
        }

        // ---- ONE tool lookup for every chore and the command selector: hands (equip cells), then the Tools pouch, then
        // storage. The first place holding a fitting tool wins (best tool within it). ----

        private static readonly EquipmentSlot[] ToolHandSlots = { EquipmentSlot.RightHand, EquipmentSlot.LeftHand, EquipmentSlot.RightBack, EquipmentSlot.LeftBack };

        /// <summary>
        /// The tool for <paramref name="tool"/> work of at least <paramref name="minTier"/>: hands/equip cells first, then the
        /// Tools pouch cells, then the storage rows. <paramref name="where"/> is "hands", "pouch" or "storage" ("" when none);
        /// <paramref name="bestTierAnywhere"/> is the best tier of that tool kind found anywhere (-1: none at all), so callers can say
        /// "no axe" vs "my axe is too weak". Logs "[Tools] name task: ..." at Debug (rate-limited).
        /// </summary>
        public ItemDrop.ItemData FindTool(FiresCore.Npc.IdleBehaviors.ResourceDataHelper.ToolType tool, int minTier, out string where, out int bestTierAnywhere, string task = null)
        {
            EnsureInitialized();
            where = "";
            bestTierAnywhere = -1;
            if (_inventory == null) return null;
            if (tool == FiresCore.Npc.IdleBehaviors.ResourceDataHelper.ToolType.None) { where = "none needed"; return null; }

            var hands = new List<ItemDrop.ItemData>();
            foreach (var s in ToolHandSlots) { var it = GetEquippedItem(s); if (it != null && !hands.Contains(it)) hands.Add(it); }
            var pouch = new List<ItemDrop.ItemData>();
            foreach (var it in GetPouchItems(CompanionPouchRules.PouchKind.Tool)) if (it != null) pouch.Add(it);
            var storage = GetStorageItems();

            ItemDrop.ItemData found = null;
            foreach (var (list, name) in new[] { (hands, "hands"), (pouch, "pouch"), (storage, "storage") })
            {
                ItemDrop.ItemData best = null;
                foreach (var it in list)
                {
                    if (!FiresCore.Npc.IdleBehaviors.ResourceDataHelper.IsToolAppropriate(it, tool, 0)) continue;
                    bestTierAnywhere = Mathf.Max(bestTierAnywhere, it.m_shared.m_toolTier);
                    if (it.m_shared.m_toolTier < minTier) continue;
                    if (FiresCore.Npc.IdleBehaviors.ResourceDataHelper.IsBetterTool(it, best, tool)) best = it;
                }
                if (best != null && found == null) { found = best; where = name; }
            }

            string kind = tool.ToString().ToLowerInvariant();
            string t = string.IsNullOrEmpty(task) ? kind : task;
            if (found != null)
                LogTools(t, $"{kind} {GetItemPrefabName(found)} from {where}");
            else if (bestTierAnywhere < 0)
                LogTools(t, $"no {kind} (checked hands/pouch/storage)");
            else
                LogTools(t, $"{kind} tier {bestTierAnywhere} < needed {minTier}");
            return found;
        }

        public ItemDrop.ItemData FindTool(FiresCore.Npc.IdleBehaviors.ResourceDataHelper.ToolType tool, int minTier, string task = null) =>
            FindTool(tool, minTier, out _, out _, task);

        /// <summary>The player-facing reason a tool lookup failed: "no axe", "my axe is too weak for this tree", or null.</summary>
        public static string ToolRefusal(FiresCore.Npc.IdleBehaviors.ResourceDataHelper.ToolType tool, int bestTierAnywhere, string targetNoun)
        {
            string kind = tool.ToString().ToLowerInvariant();
            return bestTierAnywhere < 0 ? $"no {kind}" : $"my {kind} is too weak for this {targetNoun}";
        }

        /// <summary>Puts a FindTool result in the RIGHT-hand cell (from an equip cell, the pouch or storage; what the hand
        /// held goes to the back or storage, ResourceDataHelper.TryEquipInRightHand), then Reconcile equips it. False when no room.</summary>
        public bool EquipToolInHand(ItemDrop.ItemData tool, string task = null)
        {
            if (tool == null || _inventory == null) return false;
            if (GetEquippedItem(EquipmentSlot.RightHand) == tool) return true;
            EquipmentSlot? slot = null;
            if (_inventory.ContainsItem(tool) && CompanionGridLayout.TryGetEquipSlot(tool.m_gridPos, out var s)) slot = s;
            if (!FiresCore.Npc.IdleBehaviors.ResourceDataHelper.TryEquipInRightHand(this, tool, slot, holsterWeaponOnBack: true))
            {
                LogTools(task ?? "tool", $"no room to free the hand for {GetItemPrefabName(tool)}");
                return false;
            }
            Reconcile($"equip tool for {task ?? "task"}");
            RecalculateEquipmentBonusesPublic();
            ApplyVisualEquipment();
            SaveToZDO();
            LogTools(task ?? "tool", $"{GetItemPrefabName(tool)} -> right hand");
            return true;
        }

        private static readonly Dictionary<string, (string Line, float Time)> s_toolLog = new Dictionary<string, (string, float)>();

        private void LogTools(string task, string what)
        {
            string line = $"[Tools] {CompanionNameForLog()} {task}: {what}";
            string key = GetInstanceID() + "|" + task;
            float now = Time.time;
            if (s_toolLog.TryGetValue(key, out var last) && last.Line == line && now - last.Time < 10f) return;
            if (s_toolLog.Count > 256) s_toolLog.Clear();
            s_toolLog[key] = (line, now);
            FiresLogger.LogDebug(line);
        }

        /// <summary>True when the item sits in one of the equipment cells (what the panel shows in its equip column).</summary>
        public bool IsInEquipmentRecord(ItemDrop.ItemData item) => IsWorn(item);

        /// <summary>True when the item sits in an equipment cell of this bag (reference check).</summary>
        public bool IsWorn(ItemDrop.ItemData item)
        {
            if (item == null || _inventory == null || !_inventory.ContainsItem(item)) return false;
            return CompanionGridLayout.TryGetEquipSlot(item.m_gridPos, out _);
        }

        // ---- Storage-only material helpers. Consumers (cooking, smelting, fuel, seeds, bait, crafting, repair,
        // building, feeding, payment) use these so a worn piece or a pouch stack is never spent by a name-based read. ----

        /// <summary>Total stack of items with this shared name in the STORAGE rows only.</summary>
        public int CountStorage(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int n = 0;
            foreach (var it in GetStorageItems())
                if (it.m_shared != null && it.m_shared.m_name == name) n += it.m_stack;
            return n;
        }

        /// <summary>True when the STORAGE rows hold at least <paramref name="amount"/> of this shared name.</summary>
        public bool HaveInStorage(string name, int amount = 1) => CountStorage(name) >= Mathf.Max(1, amount);

        /// <summary>Removes up to <paramref name="amount"/> of this shared name from the STORAGE rows only (smallest stacks
        /// first), through vanilla Inventory.RemoveItem(item, amount) so Changed fires. Returns the amount removed.</summary>
        public int RemoveFromStorage(string name, int amount)
        {
            if (string.IsNullOrEmpty(name) || amount <= 0 || _inventory == null) return 0;
            var stacks = GetStorageItems().Where(it => it.m_shared != null && it.m_shared.m_name == name).OrderBy(it => it.m_stack).ToList();
            int removed = 0;
            foreach (var it in stacks)
            {
                if (removed >= amount) break;
                int take = Mathf.Min(it.m_stack, amount - removed);
                if (_inventory.RemoveItem(it, take)) removed += take;
            }
            return removed;
        }

        /// <summary>Storage-only count for any bag: a companion bag counts its storage rows, any other inventory counts all.</summary>
        public static int CountStorage(Inventory bag, string name)
        {
            if (bag == null) return 0;
            var owner = CompanionGridLayout.OwnerOf(bag);
            return owner != null ? owner.CountStorage(name) : bag.CountItems(name);
        }

        /// <summary>Storage-only have-check for any bag (vanilla for non-companion inventories).</summary>
        public static bool HaveInStorage(Inventory bag, string name, int amount = 1) => CountStorage(bag, name) >= Mathf.Max(1, amount);

        /// <summary>Storage-only remove for any bag (vanilla RemoveItem(name, amount) for non-companion inventories).</summary>
        public static void RemoveFromStorage(Inventory bag, string name, int amount)
        {
            if (bag == null) return;
            var owner = CompanionGridLayout.OwnerOf(bag);
            if (owner != null) owner.RemoveFromStorage(name, amount);
            else bag.RemoveItem(name, amount);
        }

        /// <summary>Storage-only item list for any bag (all items for non-companion inventories).</summary>
        public static List<ItemDrop.ItemData> StorageItemsOf(Inventory bag)
        {
            if (bag == null) return new List<ItemDrop.ItemData>();
            var owner = CompanionGridLayout.OwnerOf(bag);
            return owner != null ? owner.GetStorageItems() : bag.GetAllItems();
        }

        /// <summary>Free STORAGE cells (rows 0-3).</summary>
        public int GetFreeStorageSlots()
        {
            EnsureInitialized();
            return CompanionGridLayout.FreeStorageCells(_inventory);
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

            // Move the item into the slot's cell (the old occupant goes to storage); Reconcile equips it.
            if (!PlaceInSlot(slot, item, "EquipItem")) return false;

            // Recalculate stats
          RecalculateEquipmentBonuses();

        // Notify equipment data system to refresh
  _equipmentData?.RefreshAllEquipmentData();

        // Raise event
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

            // Move the item into the slot's cell (the old occupant goes to storage); Reconcile equips it.
            return PlaceInSlot(slot, item, "EquipItemSilent");
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
            var item = TakeOutOfSlot(slot, "UnequipSlot");
            if (item == null) return null;

 // Recalculate stats
         RecalculateEquipmentBonuses();

         // Notify equipment data system to refresh
         _equipmentData?.RefreshAllEquipmentData();

// Raise event
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
            return TakeOutOfSlot(slot, "UnequipSlotSilent");
   }

        // ================= The VA-style grid (CompanionGridLayout) ========================

        private Humanoid _body;
        private Humanoid Body => _body != null ? _body : (_body = GetComponent<Humanoid>());

        /// <summary>The vanilla Humanoid field for a slot (null for the back slots, which vanilla has no field for).</summary>
        internal static ItemDrop.ItemData HumanoidField(Humanoid h, EquipmentSlot slot)
        {
            if (h == null) return null;
            switch (slot)
            {
                case EquipmentSlot.Helmet: return h.m_helmetItem;
                case EquipmentSlot.Chest: return h.m_chestItem;
                case EquipmentSlot.Legs: return h.m_legItem;
                case EquipmentSlot.Shoulder: return h.m_shoulderItem;
                case EquipmentSlot.Utility: return h.m_utilityItem;
                case EquipmentSlot.RightHand: return h.m_rightItem;
                case EquipmentSlot.LeftHand: return h.m_leftItem;
                case EquipmentSlot.Trinket: return h.m_trinketItem;
                default: return null;
            }
        }

        /// <summary>Which vanilla field holds the item, or null.</summary>
        internal static EquipmentSlot? FieldSlotOf(Humanoid h, ItemDrop.ItemData item)
        {
            if (h == null || item == null) return null;
            if (h.m_helmetItem == item) return EquipmentSlot.Helmet;
            if (h.m_chestItem == item) return EquipmentSlot.Chest;
            if (h.m_legItem == item) return EquipmentSlot.Legs;
            if (h.m_shoulderItem == item) return EquipmentSlot.Shoulder;
            if (h.m_utilityItem == item) return EquipmentSlot.Utility;
            if (h.m_rightItem == item) return EquipmentSlot.RightHand;
            if (h.m_leftItem == item) return EquipmentSlot.LeftHand;
            if (h.m_trinketItem == item) return EquipmentSlot.Trinket;
            return null;
        }

        // Equipped or hidden by vanilla (HideHandItems keeps the item in m_hidden*Item): either way not to be re-equipped.
        private static bool HeldByHumanoid(Humanoid h, ItemDrop.ItemData item) =>
            h != null && item != null && (h.IsItemEquiped(item) || h.m_hiddenRightItem == item || h.m_hiddenLeftItem == item);

        internal bool InReconcile => _reconcileDepth > 0;

        /// <summary>Moves an item of this bag to <paramref name="pos"/> without a Changed (the caller raises one).</summary>
        private void SetCellRaw(ItemDrop.ItemData item, Vector2i pos)
        {
            if (!_inventory.ContainsItem(item)) _inventory.m_inventory.Add(item);
            item.m_gridPos = pos;
        }

        /// <summary>Moves a bag item to the first free storage cell (the spare row when storage is full). False when the bag
        /// has no free cell at all; the item then stays where it is.</summary>
        private bool MoveToStorageRaw(ItemDrop.ItemData item, string reason)
        {
            var pos = CompanionGridLayout.FirstFreeStorageCell(_inventory);
            if (pos.x < 0)
            {
                int spareRow = CompanionGridLayout.SpareRow;
                for (int x = 0; x < CompanionGridLayout.Width && pos.x < 0; x++)
                    if (_inventory.GetItemAt(x, spareRow) == null) pos = new Vector2i(x, spareRow);
            }
            if (pos.x < 0)
            {
                FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: no free cell for {GetItemPrefabName(item)} ({reason}); left at ({item.m_gridPos.x},{item.m_gridPos.y})");
                return false;
            }
            bool wasInBag = _inventory.ContainsItem(item);
            Vector2i from = item.m_gridPos;
            if (!CompanionGridLayout.TryGetEquipSlot(pos, out _)) item.m_equipped = false;
            SetCellRaw(item, pos);
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: {GetItemPrefabName(item)} -> storage ({pos.x},{pos.y}){(wasInBag ? $" from ({from.x},{from.y})" : "")}, {reason}");
            return true;
        }

        /// <summary>Equipping on the grid: the item goes into the slot's cell (from storage, a pouch, another cell, or from
        /// outside the bag), the old occupant goes to storage, then Reconcile equips it through vanilla.</summary>
        private bool PlaceInSlot(EquipmentSlot slot, ItemDrop.ItemData item, string reason)
        {
            EnsureInitialized();
            if (item?.m_shared == null || _inventory == null) return false;
            var cell = CompanionGridLayout.CellFor(slot);
            if (!CompanionGridLayout.AcceptsSlot(slot, item))
            {
                FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: {GetItemPrefabName(item)} does not fit {slot} ({reason}); kept in storage");
                if (!_inventory.ContainsItem(item)) _inventory.AddItem(item);
                return false;
            }
            var occupant = _inventory.GetItemAt(cell.x, cell.y);
            if (occupant == item) { Reconcile(reason); return true; }
            _reconcileDepth++;
            try
            {
                if (occupant != null)
                {
                    if (HeldByHumanoid(Body, occupant)) Body.UnequipItem(occupant, false);
                    if (!MoveToStorageRaw(occupant, $"replaced in {slot} ({reason})")) return false;
                }
                // An item moving into a back cell must not stay in a hand.
                if (CompanionGridLayout.IsBackSlot(slot) && HeldByHumanoid(Body, item)) Body.UnequipItem(item, false);
                SetCellRaw(item, cell);
            }
            finally { _reconcileDepth--; }
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: {GetItemPrefabName(item)} -> {slot} cell ({cell.x},{cell.y}), {reason}");
            _inventory.Changed();   // OnStorageInventoryChanged -> Reconcile -> vanilla EquipItem
            return true;
        }

        /// <summary>Unequipping on the grid: vanilla UnequipItem, then the item leaves the bag (the old contract: callers put it
        /// back into storage, into another slot, or hand it on).</summary>
        private ItemDrop.ItemData TakeOutOfSlot(EquipmentSlot slot, string reason)
        {
            EnsureInitialized();
            var item = GetEquippedItem(slot);
            if (item == null) return null;
            LogUnequip(slot, item, reason, 3);
            _reconcileDepth++;
            try
            {
                // A NullReferenceException in VisEquipment.SetChestItem (from Humanoid.UnequipItem here): the only
                // null SetChestItem can hit is VisEquipment.m_nview, set in its Awake, so the body was inactive (unloading / mid-teleport)
                // and its VisEquipment never woke. UnequipItem has already cleared the Humanoid's slot when SetupVisEquipment throws, so
                // the visual refresh is skipped and logged; it re-applies when the body is active again.
                if (HeldByHumanoid(Body, item))
                {
                    try { Body.UnequipItem(item, false); }
                    catch (NullReferenceException) when (Body.m_visEquipment != null && Body.m_visEquipment.m_nview == null)
                    {
                        FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: unequip {slot} ({reason}) skipped the visual refresh: VisEquipment.m_nview null "
                            + $"(body active {Body.gameObject.activeInHierarchy}, ZNetView {(Body.m_nview != null && Body.m_nview.IsValid() ? "valid" : "invalid")})");
                    }
                }
                item.m_equipped = false;
                _inventory.m_inventory.Remove(item);
            }
            finally { _reconcileDepth--; }
            _inventory.Changed();
            return item;
        }

        /// <summary>
        /// Keeps the grid and the Humanoid together (VA Inventory's rule). Runs on every bag change, at load and on a
        /// short retry tick. (1) an item a cell does not accept goes to storage; (2) an item vanilla has equipped that is not in
        /// its equip cell is unequipped; (3) an item in a hand/armour cell that vanilla has not equipped is equipped with
        /// Humanoid.EquipItem; a refusal (attacking, dodging, broken) is retried, never bounced; an item vanilla put in the
        /// other hand moves to that hand's cell; anything vanilla unequipped as a side effect goes to storage. Re-entrant
        /// calls are folded into one more pass.
        /// </summary>
        public void Reconcile(string why = "reconcile")
        {
            if (_inventory == null || _isLoadingFromZDO) return;
            if (_reconcileDepth > 0) { _reconcileAgain = true; return; }
            var h = Body;
            if (h == null || IsGhostPreview()) return;

            SyncBag(why);
            SyncJewelry(why);
            bool moved = false, gearChanged = false;
            _reconcileRetry = false;
            for (int pass = 0; pass < 3; pass++)
            {
                _reconcileAgain = false;
                _reconcileDepth++;
                try
                {
                    var items = new List<ItemDrop.ItemData>(_inventory.GetAllItems());

                    // (1) invalid items in equip/pouch/spare cells, or outside the grid, go to storage
                    foreach (var it in items)
                    {
                        if (it?.m_shared == null) continue;
                        var p = it.m_gridPos;
                        bool spare = p.y == CompanionGridLayout.SpareRow && p.x >= 0 && p.x < CompanionGridLayout.Width;
                        if (CompanionGridLayout.IsStorage(p) || spare || CompanionGridLayout.Accepts(p, it) || CompanionGridLayout.IsUndecided(p)) continue;
                        if (HeldByHumanoid(h, it)) { h.UnequipItem(it, false); gearChanged = true; }
                        if (MoveToStorageRaw(it, $"invalid for cell ({p.x},{p.y}), reconcile")) moved = true;
                    }

                    // (2) equipped by vanilla but not in its equip cell (or not in the bag at all): unequip
                    foreach (EquipmentSlot s in CompanionGridLayout.Slots)
                    {
                        if (CompanionGridLayout.IsBackSlot(s)) continue;
                        var f = HumanoidField(h, s);
                        if (f == null) continue;
                        bool inCell = _inventory.ContainsItem(f) && CompanionGridLayout.TryGetEquipSlot(f.m_gridPos, out var cs) && cs == s;
                        if (inCell) continue;
                        h.UnequipItem(f, false);
                        gearChanged = true;
                        FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: unequip {s} {GetItemPrefabName(f)}, reconcile (not in an equip cell)");
                    }

                    // stale flags (vanilla saves m_equipped; a moved/cloned stack keeps it)
                    foreach (var it in items)
                        if (it != null && it.m_equipped && !HeldByHumanoid(h, it)) it.m_equipped = false;

                    // (3) items in hand/armour cells that vanilla has not equipped
                    foreach (EquipmentSlot s in CompanionGridLayout.Slots)
                    {
                        if (CompanionGridLayout.IsBackSlot(s)) continue;
                        var c = CompanionGridLayout.CellFor(s);
                        var it = _inventory.GetItemAt(c.x, c.y);
                        if (it == null || HeldByHumanoid(h, it)) continue;

                        var before = new List<ItemDrop.ItemData>();
                        foreach (EquipmentSlot o in CompanionGridLayout.Slots)
                        {
                            if (CompanionGridLayout.IsBackSlot(o) || o == s) continue;
                            var oc = CompanionGridLayout.CellFor(o);
                            var other = _inventory.GetItemAt(oc.x, oc.y);
                            if (other != null && HeldByHumanoid(h, other)) before.Add(other);
                        }

                        bool ok = false;
                        try { ok = h.EquipItem(it, false); } catch (Exception e) { FiresLogger.LogDebug($"[CompanionInventory] EquipItem threw: {e.Message}"); }
                        if (!ok)
                        {
                            _reconcileRetry = true;
                            LogRefusal(s, it);
                            continue;
                        }
                        gearChanged = true;
                        FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: equip {s} {GetItemPrefabName(it)}, reconcile ({why})");

                        // vanilla chose the other hand (torch, one-hander): follow it
                        var landed = FieldSlotOf(h, it);
                        if (landed.HasValue && landed.Value != s)
                        {
                            var lc = CompanionGridLayout.CellFor(landed.Value);
                            var occ = _inventory.GetItemAt(lc.x, lc.y);
                            if (occ != null && occ != it) MoveToStorageRaw(occ, $"replaced by {GetItemPrefabName(it)} in {landed.Value}, reconcile");
                            SetCellRaw(it, lc);
                            moved = true;
                            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: {GetItemPrefabName(it)} moved {s} -> {landed.Value} (vanilla's hand), reconcile");
                        }

                        // vanilla unequipped something else to make room (two-hander vs shield): it goes to storage
                        foreach (var other in before)
                        {
                            if (HeldByHumanoid(h, other)) continue;
                            if (MoveToStorageRaw(other, $"displaced by {GetItemPrefabName(it)}, reconcile")) moved = true;
                        }
                    }
                }
                finally { _reconcileDepth--; }
                if (!_reconcileAgain) break;
            }

            if (moved) _inventory.Changed();   // runs Reconcile once more (converges: nothing left to move)
            if (gearChanged)
            {
                RecalculateEquipmentBonuses();
                _equipmentData?.RefreshAllEquipmentData();
                OnEquipmentChanged?.Invoke(EquipmentSlot.RightHand, GetEquippedItem(EquipmentSlot.RightHand));
                ApplyVisualEquipment();
            }
            // Back cells have no vanilla field, so a back-cell change never set gearChanged; redraw them here
            if (BackCellsHash() != _lastBackCellsHash) RedrawBackVisuals($"back cells changed, {why}");
            SyncPet(why);
        }

        // The Pet cell (CompanionGridLayout.PetCell) is never vanilla-equipped; its item drives PetBridge.SetPet instead.
        private string _lastPetRefused;

        /// <summary>The item in the Pet cell, or null.</summary>
        public ItemDrop.ItemData GetPetItem()
        {
            EnsureInitialized();
            if (_inventory == null) return null;
            var c = CompanionGridLayout.PetCell;
            return _inventory.GetItemAt(c.x, c.y);
        }

        private void SyncPet(string why)
        {
            if (!FiresCore.Bridge.PetBridge.IsAvailable || _inventory == null) return;
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            var h = Body;
            if (h == null) return;
            var item = GetPetItem();
            string want = item?.m_dropPrefab?.name ?? "";
            string have;
            try { have = FiresCore.Bridge.PetBridge.GetPet(h) ?? ""; }
            catch (Exception e) { FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet: GetPet threw {e.Message}"); return; }
            bool wantNone = want.Length == 0;
            SyncPetLevel(h, item, why);
            if (wantNone ? have.Length == 0 : have.Length != 0 && _lastPetApplied == want) return;
            if (_lastPetRefused == want) return;
            string result;
            try { result = FiresCore.Bridge.PetBridge.SetPet(h, want); }
            catch (Exception e) { result = $"threw {e.Message}"; }
            if (result == null) { _lastPetApplied = want; _lastPetRefused = null; }
            else _lastPetRefused = want;
            FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet: {(wantNone ? "none" : want)} ({result ?? "ok"}, {why})");
        }

        private string _lastPetApplied;

        // The pet buff for companions. RPGmaker's PetProgressApi (looked up by name; Core never references RPGmaker)
        // takes the owner's pet level; level 0 = no buff. Sent whenever the Pet cell item's level differs from the last one sent,
        // so a load, a swap or a removal (level 0) all reach it.
        private int _lastPetLevel = -1;
        private static bool s_progressLooked;
        private static System.Reflection.MethodInfo s_setOwnerLevel, s_levelOf;

        private void SyncPetLevel(Character owner, ItemDrop.ItemData item, string why)
        {
            if (!s_progressLooked)
            {
                s_progressLooked = true;
                var api = FiresCore.Pieces.BuildMenuPages.TypeIn("FiresRPGmaker", "FiresRPGmaker.Modules.Pets.Progress.PetProgressApi");   // No full scan
                s_setOwnerLevel = api != null ? HarmonyLib.AccessTools.Method(api, "SetOwnerLevel", new[] { typeof(Character), typeof(int) }) : null;
                s_levelOf = api != null ? HarmonyLib.AccessTools.Method(api, "LevelOf", new[] { typeof(ItemDrop.ItemData) }) : null;
                FiresLogger.LogDebug($"[Companion] pet progress API: {(s_setOwnerLevel != null && s_levelOf != null ? "found" : "not found (pet buffs off for companions)")}");
            }
            if (s_setOwnerLevel == null || s_levelOf == null) return;
            int level = 0;
            try { if (item != null) level = (int)s_levelOf.Invoke(null, new object[] { item }); }
            catch (Exception e) { FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet level: LevelOf threw {e.InnerException?.Message ?? e.Message}"); return; }
            if (level == _lastPetLevel) return;
            try { s_setOwnerLevel.Invoke(null, new object[] { owner, level }); _lastPetLevel = level; }
            catch (Exception e) { FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet level: SetOwnerLevel threw {e.InnerException?.Message ?? e.Message}"); return; }
            FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet level {level} ({why})");
        }

        /// <summary>Clears the pet (death path). No-op without a pet mod or off the ZDO owner.</summary>
        public void ClearPet(string why)
        {
            if (!FiresCore.Bridge.PetBridge.IsAvailable || _nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            var h = Body;
            if (h == null) return;
            string result;
            try { result = FiresCore.Bridge.PetBridge.SetPet(h, ""); }
            catch (Exception e) { result = $"threw {e.Message}"; }
            _lastPetApplied = null;
            _lastPetRefused = null;
            SyncPetLevel(h, null, why);
            FiresLogger.LogDebug($"[Companion] {CompanionNameForLog()} pet: none ({result ?? "ok"}, {why})");
        }

        private float _lastRefusalLog;
        private void LogRefusal(EquipmentSlot s, ItemDrop.ItemData it)
        {
            if (Time.time - _lastRefusalLog < 2f) return;
            _lastRefusalLog = Time.time;
            var h = Body;
            string why = h == null ? "no body" : h.InAttack() ? "attacking" : h.InDodge() ? "dodging"
                : (it.m_shared.m_useDurability && it.m_durability <= 0f) ? "broken" : !_inventory.ContainsItem(it) ? "not in bag" : "vanilla refused";
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: equip {s} {GetItemPrefabName(it)} refused ({why}), reconcile retries");
        }

        /// <summary>A vanilla Humanoid.EquipItem from elsewhere (AI swap, FireScare torch): the item moves into the cell of
        /// the field vanilla put it in, the old occupant to storage (VA's Humanoid_EquipItem_AutoMoveToSlot).</summary>
        internal void OnVanillaEquipped(ItemDrop.ItemData item)
        {
            if (_reconcileDepth > 0 || _isLoadingFromZDO || item == null || _inventory == null) return;
            var slot = FieldSlotOf(Body, item);
            if (slot == null) return;
            var cell = CompanionGridLayout.CellFor(slot.Value);
            if (_inventory.ContainsItem(item) && item.m_gridPos.x == cell.x && item.m_gridPos.y == cell.y) return;
            _reconcileDepth++;
            try
            {
                var occ = _inventory.GetItemAt(cell.x, cell.y);
                if (occ != null && occ != item)
                {
                    if (HeldByHumanoid(Body, occ)) Body.UnequipItem(occ, false);
                    MoveToStorageRaw(occ, $"replaced by vanilla equip of {GetItemPrefabName(item)}");
                }
                SetCellRaw(item, cell);
            }
            finally { _reconcileDepth--; }
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: vanilla equip {slot.Value} {GetItemPrefabName(item)} -> its cell ({cell.x},{cell.y})");
            _inventory.Changed();
        }

        /// <summary>A vanilla Humanoid.UnequipItem from elsewhere: an item in a hand/armour cell goes back to storage
        /// (VA's UnequipItem_ReturnToInventory).</summary>
        internal void OnVanillaUnequipped(ItemDrop.ItemData item)
        {
            if (_reconcileDepth > 0 || _isLoadingFromZDO || item == null || _inventory == null) return;
            if (HeldByHumanoid(Body, item) || !_inventory.ContainsItem(item)) return;
            if (!CompanionGridLayout.TryGetEquipSlot(item.m_gridPos, out var slot) || CompanionGridLayout.IsBackSlot(slot)) return;
            _reconcileDepth++;
            bool moved;
            try { moved = MoveToStorageRaw(item, $"vanilla unequip from {slot}"); }
            finally { _reconcileDepth--; }
            if (moved) _inventory.Changed();
        }

        /// <summary>
        /// Per equip slot, the cell item vs the Humanoid's equipped item vs what VisEquipment draws. Logs
        /// "[EquipAudit] name: 0 mismatches" or one line per mismatching slot. Returns the mismatch count.
        /// </summary>
        public int AuditEquipment(string context = "")
        {
            EnsureInitialized();
            var h = Body;
            var vis = h != null ? h.m_visEquipment : GetComponent<VisEquipment>();
            string name = CompanionNameForLog();
            int bad = 0;
            foreach (EquipmentSlot s in CompanionGridLayout.Slots)
            {
                var c = CompanionGridLayout.CellFor(s);
                var cellItem = _inventory?.GetItemAt(c.x, c.y);
                string cellName = cellItem != null ? GetItemPrefabName(cellItem) : "";
                bool back = CompanionGridLayout.IsBackSlot(s);
                var field = back ? null : HumanoidField(h, s);
                string fieldName = back ? cellName : (field != null ? GetItemPrefabName(field) : "");
                int drawn = DrawnHash(vis, s);
                int want = string.IsNullOrEmpty(cellName) ? 0 : cellName.GetStableHashCode();
                bool fieldOk = back || field == cellItem;
                bool drawOk = vis == null || drawn == want || (drawn == -1);
                if (fieldOk && drawOk) continue;
                bad++;
                string drawnName = drawn == 0 ? "-" : drawn == want ? cellName : $"#{drawn}";
                FiresLogger.LogDebug($"[EquipAudit] {name} {s}: cell {(cellName == "" ? "-" : cellName)} / humanoid {(back ? "n/a" : fieldName == "" ? "-" : fieldName)} / drawn {drawnName}{(string.IsNullOrEmpty(context) ? "" : $" ({context})")}");
            }
            if (bad == 0) FiresLogger.LogDebug($"[EquipAudit] {name}: 0 mismatches{(string.IsNullOrEmpty(context) ? "" : $" ({context})")}");
            return bad;
        }

        private static int DrawnHash(VisEquipment v, EquipmentSlot s)
        {
            if (v == null) return 0;
            switch (s)
            {
                case EquipmentSlot.Helmet: return v.m_currentHelmetItemHash;
                case EquipmentSlot.Chest: return v.m_currentChestItemHash;
                case EquipmentSlot.Legs: return v.m_currentLegItemHash;
                case EquipmentSlot.Shoulder: return v.m_currentShoulderItemHash;
                case EquipmentSlot.Utility: return v.m_currentUtilityItemHash;
                case EquipmentSlot.RightHand: return v.m_currentRightItemHash;
                case EquipmentSlot.LeftHand: return v.m_currentLeftItemHash;
                case EquipmentSlot.RightBack: return v.m_currentRightBackItemHash;
                case EquipmentSlot.LeftBack: return v.m_currentLeftBackItemHash;
                default: return 0;
            }
        }

        /// <summary>After vanilla's SetupVisEquipment (which writes the back items from its hidden-hand fields), the
        /// companion's back cells are drawn again, so the back weapons do not vanish on every equip.</summary>
        internal void RedrawBackItems(VisEquipment vis)
        {
            if (vis == null || _inventory == null) return;
            // Not awake yet -> draw nothing and leave _lastBackCellsHash as it was, so Update's change check redraws
            // once the body is ready (setting it here used to mark the back as drawn when the setters had thrown).
            if (!VisEquipmentCompat.IsReady(vis)) return;
            var h = Body;
            var rc = CompanionGridLayout.CellFor(EquipmentSlot.RightBack);
            var lc = CompanionGridLayout.CellFor(EquipmentSlot.LeftBack);
            var r = _inventory.GetItemAt(rc.x, rc.y);
            var l = _inventory.GetItemAt(lc.x, lc.y);
            // Vanilla's hidden (sheathed) hand items are drawn on the back by SetupVisEquipment, and they skipped the clear
            // below. One that no longer sits in an equip cell is stale (Unequip all left the bow drawn on the back): drop it.
            if (h != null)
            {
                if (IsStaleHidden(h.m_hiddenRightItem)) { FiresLogger.LogDebug($"[CompanionInventory] stale hidden right item {GetItemPrefabName(h.m_hiddenRightItem)} cleared on '{CompanionNameForLog()}'"); h.m_hiddenRightItem.m_equipped = false; h.m_hiddenRightItem = null; }
                if (IsStaleHidden(h.m_hiddenLeftItem)) { FiresLogger.LogDebug($"[CompanionInventory] stale hidden left item {GetItemPrefabName(h.m_hiddenLeftItem)} cleared on '{CompanionNameForLog()}'"); h.m_hiddenLeftItem.m_equipped = false; h.m_hiddenLeftItem = null; }
            }
            // An EMPTY back cell clears its attachment too (Unequip all left a ghost bow drawn on the back)
            if (h != null && h.m_hiddenRightItem == null) VisEquipmentCompat.SetRightBackItem(vis, r != null ? GetItemPrefabName(r) : "");
            if (h != null && h.m_hiddenLeftItem == null) VisEquipmentCompat.SetLeftBackItem(vis, l != null ? GetItemPrefabName(l) : "", l != null ? l.m_variant : 0);
            _lastBackCellsHash = BackCellsHash();
        }

        private int _lastBackCellsHash;

        private bool IsStaleHidden(ItemDrop.ItemData it)
        {
            if (it == null || _inventory == null) return false;
            if (!_inventory.ContainsItem(it)) return true;
            return !CompanionGridLayout.TryGetEquipSlot(it.m_gridPos, out _);
        }

        private int BackCellsHash()
        {
            if (_inventory == null) return 0;
            var rc = CompanionGridLayout.CellFor(EquipmentSlot.RightBack);
            var lc = CompanionGridLayout.CellFor(EquipmentSlot.LeftBack);
            var r = _inventory.GetItemAt(rc.x, rc.y);
            var l = _inventory.GetItemAt(lc.x, lc.y);
            return ((r != null ? GetItemPrefabName(r).GetStableHashCode() : 0) * 31) ^ (l != null ? GetItemPrefabName(l).GetStableHashCode() + l.m_variant : 0);
        }

        /// <summary>Clears and redraws the back attachments from the back cells (no vanilla field drives them).</summary>
        public void RedrawBackVisuals(string why)
        {
            var h = Body;
            var vis = h != null ? h.m_visEquipment : GetComponent<VisEquipment>();
            if (vis == null) return;
            RedrawBackItems(vis);
            FiresLogger.LogDebug($"[CompanionInventory] back visuals cleared/redrawn ({why}) on '{CompanionNameForLog()}'");
        }

        /// <summary>Moves EVERY item in the 9 equip cells (back cells included) into storage rows. Refuses (returns -1,
        /// nothing moved) when storage has too few free cells; never drops or deletes. Returns the moved count.</summary>
        public int UnequipAllToStorage()
        {
            EnsureInitialized();
            if (_inventory == null) return -1;
            var h = Body;
            var items = new List<ItemDrop.ItemData>();
            foreach (EquipmentSlot s in CompanionGridLayout.Slots)
            {
                var c = CompanionGridLayout.CellFor(s);
                var it = _inventory.GetItemAt(c.x, c.y);
                if (it != null && !items.Contains(it)) items.Add(it);
            }
            // The Pet cell empties too; the Reconcile below then clears the pet through PetBridge
            var petItem = GetPetItem();
            if (petItem != null && !items.Contains(petItem)) items.Add(petItem);
            if (items.Count > CompanionGridLayout.FreeStorageCells(_inventory))
            {
                FiresLogger.LogDebug($"[CompanionInventory] unequip all refused on '{CompanionNameForLog()}': {items.Count} item(s), {CompanionGridLayout.FreeStorageCells(_inventory)} free storage cell(s)");
                return -1;
            }
            int moved = 0;
            _reconcileDepth++;
            try
            {
                foreach (var it in items)
                {
                    if (h != null && HeldByHumanoid(h, it)) h.UnequipItem(it, false);
                    if (CompanionGridLayout.FirstFreeStorageCell(_inventory).x < 0) break;
                    if (MoveToStorageRaw(it, "unequip all")) moved++;
                }
            }
            finally { _reconcileDepth--; }
            _inventory.Changed();
            Reconcile("unequip all");
            ApplyVisualEquipment();
            RedrawBackVisuals("unequip all");
            return moved;
        }

        private static readonly Dictionary<string, float> _unequipLogTimes = new Dictionary<string, float>();

        internal string CompanionNameForLog()
        {
            var ch = GetComponent<Character>();
            string n = ch != null ? ch.GetHoverName() : null;
            return string.IsNullOrEmpty(n) ? gameObject.name : n;
        }

        /// <summary>Debug line for every companion unequip: slot, item, caller, companion. Rate-limited per
        /// (companion, slot, item, caller) to one line a second, since holstering/combat paths run in update loops.</summary>
        internal void LogUnequip(EquipmentSlot slot, ItemDrop.ItemData item, string path, int frame = 2)
        {
            if (!FiresLogger.DebugEnabled) return;
            string caller = "?";
            try
            {
                var m = new System.Diagnostics.StackFrame(frame, false).GetMethod();
                if (m != null) caller = (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
            }
            catch { }
            string itemName = item == null ? "<none>" : (item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name);
            string key = GetInstanceID() + "|" + slot + "|" + itemName + "|" + caller;
            float now = Time.unscaledTime;
            if (_unequipLogTimes.TryGetValue(key, out float last) && now - last < 1f) return;
            if (_unequipLogTimes.Count > 512) _unequipLogTimes.Clear();
            _unequipLogTimes[key] = now;
            FiresLogger.LogDebug($"[CompanionInventory] unequip {slot}: {itemName} on '{CompanionNameForLog()}' via {path}, caller {caller}");
        }

        /// <summary>
        /// Gets the equipped item for a slot: the item in the slot's grid cell.
        /// </summary>
        public ItemDrop.ItemData GetEquippedItem(EquipmentSlot slot)
        {
            EnsureInitialized();
            if (_inventory == null) return null;
            var c = CompanionGridLayout.CellFor(slot);
            return _inventory.GetItemAt(c.x, c.y);
        }

        /// <summary>
        /// Gets all equipped items (the occupied equipment cells).
        /// </summary>
        public Dictionary<EquipmentSlot, ItemDrop.ItemData> GetAllEquipped()
        {
            var d = new Dictionary<EquipmentSlot, ItemDrop.ItemData>();
            foreach (EquipmentSlot s in CompanionGridLayout.Slots)
            {
                var it = GetEquippedItem(s);
                if (it != null) d[s] = it;
            }
            return d;
        }

        // Read-only view kept for the older readers in this file.
        private Dictionary<EquipmentSlot, ItemDrop.ItemData> _equippedItems => GetAllEquipped();

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
                case EquipmentSlot.Trinket:
                    return itemType == ItemDrop.ItemData.ItemType.Trinket;
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
                case ItemDrop.ItemData.ItemType.Trinket:
                    return EquipmentSlot.Trinket;
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
        /// <summary>
        /// Gets the equipment prefab name for a slot (of the item in the slot's cell; "" when empty).
        /// </summary>
        private string GetEquipmentPrefabName(EquipmentSlot slot)
        {
            var item = GetEquippedItem(slot);
            return item != null ? GetItemPrefabName(item) : "";
        }

        /// <summary>
        /// The stack of the item in the slot's cell (1 when empty).
        /// </summary>
        private int GetEquipmentStack(EquipmentSlot slot)
        {
            var item = GetEquippedItem(slot);
            return item != null ? Mathf.Max(1, item.m_stack) : 1;
        }

        /// <summary>The equipped item's durability, or <see cref="DurabilityUnset"/> when the slot is empty or the
        /// item doesn't wear. Gear wears in combat (CompanionStatHooks) and is repaired at a workbench, so it persists.</summary>
        private float GetEquipmentDurability(EquipmentSlot slot)
        {
            var item = GetEquippedItem(slot);
            return item != null && item.m_shared != null && item.m_shared.m_useDurability ? item.m_durability : DurabilityUnset;
        }

        /// <summary>
        /// Updates the persisted stack count for an equipped slot (consumable throwables deplete per
        /// attack). Clamps to 1 — depleting the last one goes through UnequipSlot, not a zero stack.
        /// Caller persists via TriggerSaveToZDO.
        /// </summary>
        public void SetEquippedStack(EquipmentSlot slot, int stack)
        {
            var item = GetEquippedItem(slot);
            if (item == null) return;
            item.m_stack = Mathf.Max(1, stack);
        }

        public int GetEquipmentStackPublic(EquipmentSlot slot)
        {
            return GetEquipmentStack(slot);
        }

        private int GetEquipmentQuality(EquipmentSlot slot)
        {
            var item = GetEquippedItem(slot);
            return item != null ? Mathf.Max(1, item.m_quality) : 1;
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
  /// Checks if any equipment cell is occupied.
        /// Used to determine if vault restore is needed.
        /// </summary>
        public bool HasAnyEquipmentLoaded() => GetAllEquipped().Count > 0;

        /// <summary>
        /// Alias for HasAnyEquipmentLoaded for compatibility.
        /// </summary>
        public bool HasAnyEquipment() => HasAnyEquipmentLoaded();

        /// <summary>
        /// Clears all equipped items from all slots.
        /// </summary>
        public void ClearAllEquipment()
        {
            // Each slot's item leaves the bag (as before, when the record dropped it).
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                UnequipSlot(slot);
            }

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
        /// Loads a fully-specified ItemData (real quality, durability, crafter, customData, worldLevel, variant)
        /// into a slot as combat data WITHOUT the visual path. Used so a static NPC fights with the EXACT
        /// weapon the player equipped — see SerializeItemData/DeserializeItemData for the persisted blob.
        /// </summary>
        public bool LoadCombatItemSilent(EquipmentSlot slot, ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (IsGhostPreview()) return false;
            if (!EquipItemSilent(slot, item)) return false;
            _equipmentData?.RefreshAllEquipmentData();   // resolve WeaponItem/anim-state so combat uses the real stats
            return true;
        }

        /// <summary>Serializes a single ItemData to a base64 blob via Valheim's own Inventory format (version- and
        /// mod-field-safe). Returns "" on failure. Pair with <see cref="DeserializeItemData"/>.</summary>
        public static string SerializeItemData(ItemDrop.ItemData item)
        {
            if (item == null || item.m_dropPrefab == null) return "";
            try
            {
                var tmp = new Inventory("t", null, 1, 1);
                tmp.AddItem(item.Clone());
                var pkg = new ZPackage();
                tmp.Save(pkg);
                return pkg.GetBase64();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInventory] SerializeItemData failed: {ex.Message}");
                return "";
            }
        }

        /// <summary>Reconstructs an ItemData from a <see cref="SerializeItemData"/> blob (m_shared resolved from
        /// ObjectDB). Returns null on failure.</summary>
        public static ItemDrop.ItemData DeserializeItemData(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                var tmp = new Inventory("t", null, 1, 1);
                tmp.Load(new ZPackage(base64));
                var items = tmp.GetAllItems();
                return (items != null && items.Count > 0) ? items[0] : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInventory] DeserializeItemData failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores equipment from vault data.
        /// Sets the prefab name and recreates the ItemData.
        /// </summary>
        public void RestoreEquipmentFromVault(EquipmentSlot slot, string prefabName, int quality, int stack = 1)
        {
   if (string.IsNullOrEmpty(prefabName)) return;

            // CRITICAL: Skip equipment on ghost/preview objects (hammer placement preview)
            if (IsGhostPreview()) return;

            // New vault saves carry no equipment list (worn gear is in the saved bag), so this only runs for an
            // older vault entry. Nothing is placed here: the entry is collected and the vault restore calls
            // CommitLegacyVaultEquipment in the same call, right after its bag Load.
            _pendingLegacyEquip.RemoveAll(p => p.slot == slot);
            _pendingLegacyEquip.Add((slot, prefabName, quality, stack));
            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: collected legacy vault equipment {slot} = {prefabName} (quality {quality}, stack {stack})");
        }

        /// <summary>
        /// Places the legacy vault equipment collected by RestoreEquipmentFromVault. Call it synchronously right
        /// after the vault's bag Load (or with no Load when the vault had no bag). Atomic and idempotent: the companion ZDO is
        /// marked migrated BEFORE anything is placed, so a second run is a no-op; a piece whose prefab+quality is already
        /// anywhere in the bag (storage, pouch or equip cells) is never re-created.
        /// </summary>
        public void CommitLegacyVaultEquipment(string source)
        {
            if (_pendingLegacyEquip.Count == 0) return;
            var pending = new List<(EquipmentSlot slot, string prefab, int quality, int stack)>(_pendingLegacyEquip);
            _pendingLegacyEquip.Clear();
            EnsureInitialized();
            if (_inventory == null || IsGhostPreview()) return;

            var zdo = _nview != null ? _nview.GetZDO() : null;
            if (zdo != null)
            {
                if (zdo.GetInt(LegacyVaultMigratedKey, 0) != 0)
                {
                    FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: legacy vault equipment ({pending.Count} piece(s), {source}) skipped: companion already migrated");
                    return;
                }
                if (_nview.IsOwner()) zdo.Set(LegacyVaultMigratedKey, 1);
            }

            int placed = 0;
            _reconcileDepth++;
            try
            {
                foreach (var p in pending)
                {
                    var cell = CompanionGridLayout.CellFor(p.slot);
                    var match = FindBagItem(p.prefab, p.quality);
                    if (match != null)
                    {
                        // already in the bag: use it; only a storage copy is moved into an empty equip cell
                        if (_inventory.GetItemAt(cell.x, cell.y) == null && CompanionGridLayout.IsStorage(match.m_gridPos))
                        {
                            SetCellRaw(match, cell);
                            placed++;
                            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: legacy vault {p.slot} = {p.prefab} q{p.quality} placed (found in bag), {source}");
                        }
                        else
                            FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: legacy vault {p.slot} = {p.prefab} q{p.quality} left at ({match.m_gridPos.x},{match.m_gridPos.y}) (found in bag), {source}");
                        continue;
                    }
                    var occ = _inventory.GetItemAt(cell.x, cell.y);
                    if (occ != null && !MoveToStorageRaw(occ, "legacy vault equipment owns this cell")) continue;
                    var made = CreateItemFromPrefab(p.prefab, p.quality, p.stack, DurabilityUnset);
                    if (made == null) continue;
                    SetCellRaw(made, cell);
                    placed++;
                    FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: legacy vault {p.slot} = {p.prefab} q{p.quality} placed (re-created), {source}");
                }
            }
            finally { _reconcileDepth--; }
            if (placed > 0)
            {
                Reconcile("legacy vault equipment");
                RecalculateEquipmentBonuses();
                SaveToZDO();
            }
        }

        /// <summary>Any item of this prefab + quality anywhere in the bag (storage, pouch, equip, spare), or null.</summary>
        private ItemDrop.ItemData FindBagItem(string prefab, int quality)
        {
            if (_inventory == null || string.IsNullOrEmpty(prefab)) return null;
            foreach (var it in _inventory.GetAllItems())
                if (it != null && it.m_quality == quality && GetItemPrefabName(it) == prefab) return it;
            return null;
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
            if (CursorHoldsFromThisBag())
            {
                if (!_reloadDeferred) FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()} reload from ZDO deferred: the cursor holds an item from this bag");
                _reloadDeferred = true;
                return;
            }
            _reloadDeferred = false;
            { string st = new System.Diagnostics.StackTrace(1, false).ToString().Replace(System.Environment.NewLine, " <- "); FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()} reload from ZDO (owner {(_nview != null && _nview.IsOwner())}; from {st.Substring(0, Math.Min(300, st.Length))})"); }

            // Make sure inventory is initialized before loading
         EnsureInitialized();
            
        // Set flag to prevent saves during load
     _isLoadingFromZDO = true;

            try
        {
                // The bag is about to be replaced by new ItemData objects; vanilla must let go of the old ones first
                // (local only; nothing is saved while _isLoadingFromZDO).
                var body = Body;
                if (body != null)
                {
                    _reconcileDepth++;
                    try { body.UnequipAllItems(); } catch (Exception e) { FiresLogger.LogDebug($"[CompanionInventory] UnequipAllItems before load: {e.Message}"); }
                    finally { _reconcileDepth--; }
                }
                // Grow to 8 x 11 before Load so saved pouch/equip positions are valid (old 6x4 / 8x4 positions all fit).
                if (_inventory.m_width < CompanionGridLayout.Width) _inventory.m_width = CompanionGridLayout.Width;
                if (_inventory.m_height < CompanionGridLayout.Height) _inventory.m_height = CompanionGridLayout.Height;
                if (!_loggedDimensions)
                {
                    _loggedDimensions = true;
                    FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()} storage {_inventory.GetWidth()}x{_inventory.GetHeight()} = {_inventory.GetWidth() * _inventory.GetHeight()} (rows 0-3 storage 32, rows 4-6 pouches Tool 4 / Ammo 3 / Food 3, rows 7-9 equipment, row 10 spare)");
                }

     // Load storage inventory data
string inventoryData = zdo.GetString("companion_inventory", "");
   if (!string.IsNullOrEmpty(inventoryData))
          {
        var pkg = new ZPackage(inventoryData);
         _inventory.Load(pkg);
      if (FiresLogger.VerboseEnabled)
          Debug.Log($"[CompanionInventory] Loaded storage inventory with {_inventory.GetAllItems().Count} items");
     }

                // Migration:A save from before the grid kept worn gear in a separate record (the packed
                // "companion_equipment" field or the older per-slot keys), outside the bag. Place those pieces into their
                // slot cells (an adopted copy left in storage with m_equipped is used when found, else re-created
                // from prefab + quality as the old load did), then blank the old keys. Runs once per companion ZDO.
                if (!_hasCompletedInitialLoad && zdo.GetInt(GridVersionKey, 0) < 1 && ObjectDB.instance != null)
                    MigrateLegacyEquipment(zdo);

                // Items whose saved cell is not valid for them (old saves used rows past the storage for other things)
                // go to storage in the Reconcile after this load.

 // Recalculate bonuses after loading
      RecalculateEquipmentBonuses();

          // Apply visual equipment after NpcVisEquipment has had time to initialize
          // (its DelayedInitialize runs at 2s, so 2.5s is the earliest safe window).
          // A single one-shot retry is scheduled inside ApplyVisualEquipment if
          // VisEquipment is still null at that point - no need for 3 staggered calls.
    if (HasAnyEquipment())
          {
         Invoke(nameof(ApplyVisualEquipment), VisualEquipmentApplyDelay);
 }

          // Only log loaded inventory for tamed companions when verbose is enabled
          if (_companion?.isTamed == true && FiresLogger.VerboseEnabled)
              Debug.Log($"[CompanionInventory] Loaded inventory with {_inventory.GetAllItems().Count} items, {_equippedItems.Count} equipped");
     
     // Mark initial load as complete
           _hasCompletedInitialLoad = true;
           // The storage is filled here, after Awake; at Debug.
           FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName}: storage loaded from its ZDO, {_inventory.GetAllItems().Count} item(s), owner {(_nview != null && _nview.IsValid() && _nview.IsOwner())}");
           
           // Update the equipment hash for change detection
           _lastEquipmentHash = CalculateEquipmentHash(zdo);

           // A remote copy picked its archetype 0.5 s after Start from an inventory it hadn't loaded yet (MONK, "unarmed"),
           // and only an equip on this machine re-ran the pick.
           if (_nview != null && !_nview.IsOwner())
               GetComponent<Archetypes.ArchetypeController>()?.ForceReevaluate();
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

            // Equip what sits in the equip cells, send invalid cell items to storage, clear stale flags.
            Reconcile("load");
        }

        /// <summary>The old per-slot equipment record (packed field or legacy keys) as a list.</summary>
        private static List<(EquipmentSlot slot, string prefab, int quality, int stack, float durability)> ReadLegacyEquipment(ZDO zdo)
        {
            var list = new List<(EquipmentSlot, string, int, int, float)>();
            var order = new[] { EquipmentSlot.Helmet, EquipmentSlot.Chest, EquipmentSlot.Legs, EquipmentSlot.Shoulder, EquipmentSlot.Utility,
                                EquipmentSlot.RightHand, EquipmentSlot.LeftHand, EquipmentSlot.RightBack, EquipmentSlot.LeftBack };
            string packed = zdo.GetString("companion_equipment", "");
            if (!string.IsNullOrEmpty(packed))
            {
                try
                {
                    var pkg = new ZPackage(packed);
                    int version = pkg.ReadInt(); // 1 = name+quality, 2 = +stack, 3 = +durability
                    foreach (var s in order)
                    {
                        string name = pkg.ReadString();
                        int quality = pkg.ReadInt();
                        int stack = version >= 2 ? pkg.ReadInt() : 1;
                        float durability = version >= 3 ? pkg.ReadSingle() : DurabilityUnset;
                        if (!string.IsNullOrEmpty(name)) list.Add((s, name, quality, stack, durability));
                    }
                    return list;
                }
                catch (Exception ex)
                {
                    FiresLogger.LogDebug($"[CompanionInventory] old packed equipment field unreadable, trying the per-slot keys: {ex.Message}");
                    list.Clear();
                }
            }
            string[] keys = { "helmet", "chest", "legs", "shoulder", "utility", "righthand", "lefthand", "rightback", "leftback" };
            for (int i = 0; i < order.Length; i++)
            {
                string name = zdo.GetString("companion_equip_" + keys[i], "");
                if (!string.IsNullOrEmpty(name))
                    list.Add((order[i], name, zdo.GetInt("companion_equip_" + keys[i] + "_quality", 1), 1, DurabilityUnset));
            }
            return list;
        }

        private void MigrateLegacyEquipment(ZDO zdo)
        {
            var old = ReadLegacyEquipment(zdo);
            // Mark migrated BEFORE placing anything, so a second run (re-entry, a second load) is a no-op.
            bool owner = _nview != null && _nview.IsOwner();
            if (owner) zdo.Set(GridVersionKey, GridVersion);
            int placed = 0, fromStorage = 0;
            foreach (var e in old)
            {
                var cell = CompanionGridLayout.CellFor(e.slot);
                var occ = _inventory.GetItemAt(cell.x, cell.y);
                if (occ != null && GetItemPrefabName(occ) == e.prefab) continue;   // already there

                // Any item of this prefab+quality anywhere in the bag (storage, pouch, equip) is used, never re-created
                ItemDrop.ItemData found = FindBagItem(e.prefab, e.quality);
                if (found != null)
                {
                    if (!CompanionGridLayout.IsStorage(found.m_gridPos))
                    {
                        FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: migration {e.slot} = {e.prefab} q{e.quality} left at ({found.m_gridPos.x},{found.m_gridPos.y}) (found in bag)");
                        continue;
                    }
                    if (occ != null && !MoveToStorageRaw(occ, "migration: old record owns this cell")) continue;
                    SetCellRaw(found, cell);
                    fromStorage++;
                    FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: migration {e.slot} = {e.prefab} q{e.quality} placed (found in bag)");
                }
                else
                {
                    if (occ != null && !MoveToStorageRaw(occ, "migration: old record owns this cell")) continue;
                    var made = CreateItemFromPrefab(e.prefab, e.quality, e.stack, e.durability);
                    if (made == null) continue;
                    SetCellRaw(made, cell);
                    FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: migration {e.slot} = {e.prefab} q{e.quality} placed (re-created)");
                }
                placed++;
            }
            if (owner)
            {
                zdo.Set("companion_equipment", "");
                foreach (var k in new[] { "helmet", "chest", "legs", "shoulder", "utility", "righthand", "lefthand", "rightback", "leftback" })
                {
                    zdo.Set("companion_equip_" + k, "");
                    zdo.RemoveInt("companion_equip_" + k + "_quality");
                }
                zdo.Set(GridVersionKey, GridVersion);
            }
            if (old.Count > 0 || placed > 0)
                FiresLogger.LogDebug($"[CompanionInventory] {CompanionNameForLog()}: migrated the old equipment record into the grid: {placed} of {old.Count} piece(s) placed ({fromStorage} from storage), old keys blanked");
        }

        /// <summary>A fresh ItemData from an item prefab (quality, stack, durability), or null.</summary>
        private ItemDrop.ItemData CreateItemFromPrefab(string prefabName, int quality, int stack, float durability)
        {
            if (string.IsNullOrEmpty(prefabName) || ObjectDB.instance == null) return null;
            var itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
            var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (itemDrop?.m_itemData?.m_shared == null)
            {
                Debug.LogWarning($"[CompanionInventory] Could not find prefab {prefabName}");
                return null;
            }
            var itemData = itemDrop.m_itemData.Clone();
            itemData.m_quality = Mathf.Max(1, quality);
            itemData.m_stack = Mathf.Max(1, stack);
            itemData.m_dropPrefab = itemPrefab;
            itemData.m_durability = durability >= 0f ? durability : itemData.GetMaxDurability();
            itemData.m_equipped = false;
            var icons = itemData.m_shared.m_icons;
            itemData.m_variant = icons != null && icons.Length > 0 ? Mathf.Clamp(itemData.m_variant, 0, icons.Length - 1) : 0;
            return itemData;
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
private void LoadEquipmentSlotFromPrefab(EquipmentSlot slot, string prefabName, int quality, int stack = 1)
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
           itemData.m_stack = Mathf.Max(1, stack);
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
     
      // Into the slot's grid cell (old occupant to storage); Reconcile equips it.
      itemData.m_equipped = false;
      PlaceInSlot(slot, itemData, "LoadEquipmentSlotFromPrefab");

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

                // Still not ready - schedule one final retry in 2s and bail.
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

            // ONE source for the drawing. Armour and hands come from the Humanoid's own equipped fields (what vanilla's
            // SetupVisEquipment draws too, so the two writers always agree); the back slots, which vanilla has no field for,
            // come from the back cells.
            var h = Body;
            string F(EquipmentSlot s) { var it = h != null ? HumanoidField(h, s) : GetEquippedItem(s); return it != null ? GetItemPrefabName(it) : ""; }
            equipment[CompanionInventory.EquipmentSlot.Helmet] = F(EquipmentSlot.Helmet);
            equipment[CompanionInventory.EquipmentSlot.Chest] = F(EquipmentSlot.Chest);
            equipment[CompanionInventory.EquipmentSlot.Legs] = F(EquipmentSlot.Legs);
            equipment[CompanionInventory.EquipmentSlot.Shoulder] = F(EquipmentSlot.Shoulder);
            equipment[CompanionInventory.EquipmentSlot.Utility] = F(EquipmentSlot.Utility);
            equipment[CompanionInventory.EquipmentSlot.Trinket] = F(EquipmentSlot.Trinket);

            string rightHand = F(EquipmentSlot.RightHand);
            string leftHand = F(EquipmentSlot.LeftHand);
            string rightBack = GetEquipmentPrefabName(EquipmentSlot.RightBack);
            string leftBack = GetEquipmentPrefabName(EquipmentSlot.LeftBack);
            equipment[CompanionInventory.EquipmentSlot.RightHand] = rightHand;
            equipment[CompanionInventory.EquipmentSlot.LeftHand] = leftHand;
            equipment[CompanionInventory.EquipmentSlot.RightBack] = rightBack;
            equipment[CompanionInventory.EquipmentSlot.LeftBack] = leftBack;
            return equipment;
        }

        /// <summary>
        /// Equipment list for the vault: empty. Worn gear sits in the bag's equip cells and is saved with the bag
        /// (StorageInventoryData); writing it here too would restore every piece twice.
        /// </summary>
        public Dictionary<EquipmentSlot, (string prefab, int quality, int stack)> GetAllEquipmentForVault()
        {
            return new Dictionary<EquipmentSlot, (string prefab, int quality, int stack)>();
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

            // Skip during local player respawn / loading screen — companion-inventory
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

                // The bag (with grid positions) IS the equipment save; no separate record is written. The grid
                // version marks this ZDO as migrated so an old record is never applied again.
                // Only after the first load, so an early save can never skip the migration of an old record.
                if (_hasCompletedInitialLoad && zdo.GetInt(GridVersionKey, 0) < GridVersion) zdo.Set(GridVersionKey, GridVersion);

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
            // STORAGE rows only (pouch, equipment and spare cells are never free space for hauling).
            status.TotalSlots = CompanionGridLayout.StorageCells;
            status.FreeSlots = CompanionGridLayout.FreeStorageCells(_inventory);
            
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
            else if (status.WeightPercent >= FullWeightFraction)
            {
                status.IsFull = true;
                status.IsNearlyFull = true;
                status.Reason = $"Almost at weight limit ({status.WeightPercent:P0})";
            }
            else if (status.WeightPercent >= NearlyFullWeightFraction || status.FreeSlots < NearlyFullFreeSlotThreshold)
            {
                status.IsNearlyFull = true;
                status.Reason = status.WeightPercent >= NearlyFullWeightFraction 
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
            if (CompanionGridLayout.FreeStorageCells(_inventory) == 0) return false;
            
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
            
            // Storage rows only; worn and pouch items are never deposited.
            foreach (var item in GetStorageItems())
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

    /// <summary>
    /// Vanilla Humanoid.Start gives every non-player its prefab's default items (GiveDefaultItems,
    /// on every peer, every spawn). A companion's Humanoid bag is its persisted storage now, so those items would pile up on
    /// each load (a lootable dupe) or, landing before LoadFromZDO, fire the save and overwrite the stored items. A
    /// companion's kit comes from CompanionInventory, so it gets none.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GiveDefaultItems))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionNoDefaultItems
    {
        private static bool Prefix(Humanoid __instance) => __instance == null || __instance.GetComponent<CompanionInventory>() == null;
    }

    /// <summary>A vanilla Humanoid.EquipItem on a companion from elsewhere (AI swap,
    /// FireScare torch) moves the item into the cell of the field vanilla put it in, the old occupant to storage (VA's
    /// Humanoid_EquipItem_AutoMoveToSlot). Companions only; Reconcile's own equips are skipped.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionEquipMovesToSlot
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || __instance == null || item == null || __instance is Player) return;
            var ci = __instance.GetComponent<CompanionInventory>();
            if (ci == null || ci.InReconcile) return;
            try { ci.OnVanillaEquipped(item); }
            catch (System.Exception e) { FiresLogger.LogDebug($"[CompanionInventory] vanilla equip -> slot failed: {e.Message}"); }
        }
    }

    /// <summary>A vanilla Humanoid.UnequipItem on a companion from elsewhere sends an item in a hand/armour cell back
    /// to storage (VA's UnequipItem_ReturnToInventory). Companions only.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionUnequipReturnsToStorage
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (__instance == null || item == null || __instance is Player) return;
            var ci = __instance.GetComponent<CompanionInventory>();
            if (ci == null || ci.InReconcile) return;
            try { ci.OnVanillaUnequipped(item); }
            catch (System.Exception e) { FiresLogger.LogDebug($"[CompanionInventory] vanilla unequip -> storage failed: {e.Message}"); }
        }
    }

    /// <summary>Vanilla SetupVisEquipment writes the back items from its hidden-hand fields (empty for companions);
    /// the companion's back cells are drawn again right after, so only the grid decides what hangs on the back.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.SetupVisEquipment))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionRedrawBackItems
    {
        private static void Postfix(Humanoid __instance, VisEquipment visEq, bool isRagdoll)
        {
            if (isRagdoll || __instance == null || visEq == null || __instance is Player) return;
            var ci = __instance.GetComponent<CompanionInventory>();
            if (ci == null) return;
            try { ci.RedrawBackItems(visEq); }
            catch (System.Exception e) { FiresLogger.LogDebug($"[CompanionInventory] back redraw failed: {e.Message}"); }
        }
    }

    /// <summary>Debug line for a vanilla Humanoid.UnequipItem on a companion (slot, item, caller, companion).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionLogVanillaUnequip
    {
        private static void Prefix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (__instance == null || item == null || __instance is Player || !FiresLogger.DebugEnabled) return;
            var ci = __instance.GetComponent<CompanionInventory>();
            if (ci == null || !__instance.IsItemEquiped(item)) return;
            var slot = CompanionInventory.GetSlotForItem(item);
            if (slot == null) return;
            ci.LogUnequip(slot.Value, item, "Humanoid.UnequipItem", 3);
        }
    }
}