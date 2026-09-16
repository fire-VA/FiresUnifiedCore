using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc
{
    /// <summary>
    /// Vanilla-style auto pickup for companions each FixedUpdate: an overlap sphere at the vanilla pickup range,
    /// honoring m_autoPickup, weight and space, pulling items in before taking them, claiming ownership first, and
    /// saying so when the inventory is full.
    /// </summary>
    public class CompanionAutoPickup : MonoBehaviour
    {
        #region Settings
        
        [Header("Pickup Settings")]
        [Tooltip("Enable automatic item pickup")]
        public bool enableAutoPickup = true;
        
        [Tooltip("Radius to scan for items (vanilla player is 2m, using 3m for better coverage)")]
        public float pickupRadius = 3f;
        
        [Header("Filtering")]
        [Tooltip("Auto-pickup trophies")]
        public bool pickupTrophies = true;
        
        [Tooltip("Auto-pickup materials")]
        public bool pickupMaterials = true;
        
        [Tooltip("Auto-pickup consumables (food, potions)")]
        public bool pickupConsumables = true;
        
        [Tooltip("Auto-pickup equipment (weapons, armor)")]
        public bool pickupEquipment = false; // Off by default - player may want these
        
        [Tooltip("Auto-pickup miscellaneous items")]
        public bool pickupMisc = true;
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionInventory _inventory;
        private Character _character;
        private ZNetView _nview;
        
        private bool _inventoryFullMessageShown;
        private float _inventoryFullMessageCooldown;
        
        // Mask for item layer - same as vanilla
        private int _autoPickupMask;
        
        public static bool VerboseLogging = false; // Disable by default to reduce log spam
        
        // Stats for debugging and reporting
        private int _pickupsThisSession = 0;
        private float _lastScanTime = 0f;
        
        // Throttle magnetizing logs
        private float _lastMagnetizeLogTime = 0f;
        private const float MagnetizeLogInterval = 5f; // Only log magnetizing every 5 seconds
        
        // Tracking for gathering sessions
        private Dictionary<string, int> _gatheringSessionItems = new Dictionary<string, int>();
        private bool _isGatheringSession = false;
        
        // Throttle logging - track items we've already warned about
        private HashSet<string> _loggedWeightLimitItems = new HashSet<string>();
        private HashSet<string> _loggedTypeFilterItems = new HashSet<string>();
        private HashSet<string> _loggedCapacityItems = new HashSet<string>();
        private float _lastLogClearTime = 0f;
        private const float LogClearInterval = 60f; // Clear logged items every 60 seconds
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            
            // Same mask as Player uses
            _autoPickupMask = LayerMask.GetMask("item");
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionAutoPickup] Initialized for {_companion?.companionName ?? "unknown"}, itemLayer mask = {_autoPickupMask}");
            }
        }
        
        private void FixedUpdate()
        {
            if (!enableAutoPickup) return;
            if (_companion == null || !_companion.isTamed) return;
            if (_inventory == null) return;

            // Player can disable proximity loot pickup from the radial menu.
            if (!CompanionBehaviorToggles.IsLootEnabled(_companion)) return;

            // Only run on the owner
            if (_nview != null && !_nview.IsOwner()) return;

            // Reset inventory full message after cooldown
            if (_inventoryFullMessageShown && Time.time > _inventoryFullMessageCooldown)
            {
                _inventoryFullMessageShown = false;
            }
            
            // Run auto-pickup like vanilla
            AutoPickup(Time.fixedDeltaTime);
        }
        
        #endregion
        
        #region Pickup Logic - Mirrors Player.AutoPickup()
        
        /// <summary>
        /// Auto-pickup implementation that mirrors Valheim's Player.AutoPickup().
        /// </summary>
        private void AutoPickup(float dt)
        {
            // Get center point (slightly above ground like vanilla)
            Vector3 center = transform.position + Vector3.up;
            
            // Periodically clear logged items to allow re-logging
            if (Time.time - _lastLogClearTime > LogClearInterval)
            {
                _lastLogClearTime = Time.time;
                _loggedWeightLimitItems.Clear();
                _loggedTypeFilterItems.Clear();
                _loggedCapacityItems.Clear();
            }
            
            // Periodic scan logging
            if (VerboseLogging && Time.time - _lastScanTime > 5f)
            {
                _lastScanTime = Time.time;
                
                // Also do a non-layer-filtered scan to see ALL nearby items
                var allColliders = Physics.OverlapSphere(center, pickupRadius * 2f);
                int itemCount = 0;
                foreach (var overlap in allColliders)
                {
                    var item = overlap.GetComponent<ItemDrop>() ?? overlap.GetComponentInParent<ItemDrop>();
                    if (item != null)
                    {
                        itemCount++;
                        float dist = Vector3.Distance(item.transform.position, center);
                        int layer = item.gameObject.layer;
                        string layerName = LayerMask.LayerToName(layer);
                        Debug.Log($"[CompanionAutoPickup] Found ItemDrop: {item.m_itemData?.m_shared?.m_name ?? item.name} at {dist:F1}m, layer={layer} ({layerName}), autoPickup={item.m_autoPickup}, canPickup={item.CanPickup()}");
                    }
                }
                
                if (itemCount > 0)
                {
                    Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} found {itemCount} ItemDrops within {pickupRadius * 2f}m (layer mask = {_autoPickupMask})");
                }
            }
            
            // Use OverlapSphere like vanilla
            foreach (Collider collider in Physics.OverlapSphere(center, pickupRadius, _autoPickupMask))
            {
                if (collider.attachedRigidbody == null) continue;
                
                // Get ItemDrop from rigidbody (like vanilla)
                ItemDrop itemDrop = collider.attachedRigidbody.GetComponent<ItemDrop>();
                
                // Handle FloatingTerrainDummy (items on terrain)
                FloatingTerrainDummy floatingDummy = null;
                if (itemDrop == null)
                {
                    floatingDummy = collider.attachedRigidbody.gameObject.GetComponent<FloatingTerrainDummy>();
                    if (floatingDummy != null && floatingDummy.m_parent != null)
                    {
                        itemDrop = floatingDummy.m_parent.gameObject.GetComponent<ItemDrop>();
                    }
                }
                
                if (itemDrop == null) continue;
                
                // Check if item has auto-pickup enabled
                if (!itemDrop.m_autoPickup) continue;
                
                // Don't pickup placed pieces
                if (itemDrop.IsPiece()) continue;
                
                // Check if item's ZNetView is valid
                ZNetView itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                // Check if we can pickup (handles timing, etc.)
                if (!itemDrop.CanPickup())
                {
                    // Request ownership so we can pick it up next frame
                    itemDrop.RequestOwn();
                    continue;
                }
                
                // Check if stuck in tar
                if (itemDrop.InTar()) continue;

                // YIELD TO PLAYER: don't fight the player's own auto-pickup magnet.
                // If the local player is within their own pickup radius of the item,
                // OR is closer to the item than we are, leave it for them.  Otherwise
                // both magnets pull the item back and forth between us and the owner.
                if (ShouldYieldToPlayer(itemDrop.transform.position, center))
                    continue;

                // Load item data
                itemDrop.Load();
                
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                // Check if we want to pick up this type of item
                if (!ShouldPickupItemType(itemData))
                {
                    // Throttle logging - only log once per item type
                    string itemKey = itemData.m_shared.m_name ?? "unknown";
                    if (VerboseLogging && !_loggedTypeFilterItems.Contains(itemKey))
                    {
                        _loggedTypeFilterItems.Add(itemKey);
                        Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} skipping {itemData.m_shared.m_name} - type filter");
                    }
                    continue;
                }
                
                // Check inventory capacity
                Inventory inv = _inventory.GetStorageInventory();
                if (inv == null) continue;
                
                if (!inv.CanAddItem(itemData, itemData.m_stack))
                {
                    // Throttle logging - only log once per item type
                    string itemKey = itemData.m_shared.m_name ?? "unknown";
                    if (VerboseLogging && !_loggedCapacityItems.Contains(itemKey))
                    {
                        _loggedCapacityItems.Add(itemKey);
                        Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} can't add {itemData.m_shared.m_name} - inventory capacity");
                    }
                    ShowInventoryFullMessage();
                    continue;
                }
                
                // Check weight limit
                float maxWeight = _inventory.GetMaxCarryWeight();
                float currentWeight = inv.GetTotalWeight();
                float itemWeight = itemData.GetWeight();
                
                if (currentWeight + itemWeight > maxWeight)
                {
                    // Throttle logging - only log once per item type
                    string itemKey = itemData.m_shared.m_name ?? "unknown";
                    if (VerboseLogging && !_loggedWeightLimitItems.Contains(itemKey))
                    {
                        _loggedWeightLimitItems.Add(itemKey);
                        Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} can't add {itemData.m_shared.m_name} - weight limit ({currentWeight + itemWeight:F1} > {maxWeight:F1})");
                    }
                    ShowInventoryFullMessage();
                    continue;
                }
                
                // Calculate distance
                float distance = Vector3.Distance(itemDrop.transform.position, center);
                
                if (distance <= pickupRadius)
                {
                    // Close enough - do the pickup
                    if (distance < 0.3f)
                    {
                        // Actually pick up the item
                        PerformPickup(itemDrop, itemData, floatingDummy);
                    }
                    else
                    {
                        // "Magnet" effect - move item toward companion
                        Vector3 direction = Vector3.Normalize(center - itemDrop.transform.position);
                        Vector3 movement = direction * 15f * dt;
                        
                        itemDrop.transform.position += movement;
                        
                        // Also move the floating dummy if present
                        if (floatingDummy != null)
                        {
                            floatingDummy.transform.position += movement;
                        }
                        
                        // Throttle magnetizing logs to reduce spam
                        if (VerboseLogging && Time.time - _lastMagnetizeLogTime > MagnetizeLogInterval)
                        {
                            _lastMagnetizeLogTime = Time.time;
                            Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} magnetizing items...");
                        }
                    }
                }
            }
            
            // FALLBACK: Also scan without layer mask in case items are on unexpected layer
            // This is a backup that runs less frequently
            FallbackPickupScan(center, dt);
        }
        
        /// <summary>
        /// Fallback pickup scan that doesn't rely on layer mask.
        /// Catches items that might be on unexpected layers.
        /// </summary>
        private void FallbackPickupScan(Vector3 center, float dt)
        {
            // Find all ItemDrop components directly via FindObjectsOfType is too expensive
            // Instead, do a non-layer-filtered overlap and check for ItemDrop component
            
            foreach (Collider collider in Physics.OverlapSphere(center, pickupRadius))
            {
                if (collider == null) continue;
                
                // Try to find ItemDrop on this object or parent
                ItemDrop itemDrop = collider.GetComponent<ItemDrop>() ?? collider.GetComponentInParent<ItemDrop>();
                if (itemDrop == null) continue;
                
                // Skip if we already processed via layer mask
                int itemLayer = itemDrop.gameObject.layer;
                if (((_autoPickupMask >> itemLayer) & 1) == 1) continue; // Already covered by layer mask
                
                // Same checks as main loop
                if (!itemDrop.m_autoPickup) continue;
                if (itemDrop.IsPiece()) continue;
                
                ZNetView itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                if (!itemDrop.CanPickup())
                {
                    itemDrop.RequestOwn();
                    continue;
                }
                
                if (itemDrop.InTar()) continue;

                // YIELD TO PLAYER: same rule as the primary scan — don't fight
                // the player's own auto-pickup magnet on the same drop.
                if (ShouldYieldToPlayer(itemDrop.transform.position, center))
                    continue;

                itemDrop.Load();
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                if (!ShouldPickupItemType(itemData)) continue;
                
                Inventory inv = _inventory.GetStorageInventory();
                if (inv == null) continue;
                
                if (!inv.CanAddItem(itemData, itemData.m_stack)) continue;
                
                float maxWeight = _inventory.GetMaxCarryWeight();
                float currentWeight = inv.GetTotalWeight();
                float itemWeight = itemData.GetWeight();
                if (currentWeight + itemWeight > maxWeight) continue;
                
                float distance = Vector3.Distance(itemDrop.transform.position, center);
                
                if (distance <= pickupRadius)
                {
                    if (distance < 0.3f)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAutoPickup] FALLBACK picking up {itemData.m_shared.m_name} (layer {itemLayer})");
                        PerformPickup(itemDrop, itemData, null);
                    }
                    else
                    {
                        Vector3 direction = Vector3.Normalize(center - itemDrop.transform.position);
                        Vector3 movement = direction * 15f * dt;
                        itemDrop.transform.position += movement;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true when the local player is close enough to this item drop that
        /// our own auto-pickup magnet would tug-of-war with theirs.  In that case the
        /// companion should yield — the player's pickup wins.
        ///
        /// Yield conditions:
        ///   * player is within their own pickup radius of the item, OR
        ///   * player is closer to the item than we are.
        /// </summary>
        private bool ShouldYieldToPlayer(Vector3 itemPos, Vector3 selfCenter)
        {
            // Vanilla Player.AutoPickup uses a ~2m radius.  Use slightly more so
            // we yield even when the player is right at the edge of their magnet.
            const float PlayerAutopickRadius = 2.5f;

            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return false;

            // Only yield to players that own this companion (or any local player —
            // that's the one whose auto-pickup is actually running on this client).
            Vector3 playerPos = localPlayer.transform.position + Vector3.up;

            float playerToItem = Vector3.Distance(playerPos, itemPos);
            if (playerToItem <= PlayerAutopickRadius)
                return true;

            float selfToItem = Vector3.Distance(selfCenter, itemPos);
            if (playerToItem < selfToItem)
                return true;

            return false;
        }
        
        /// <summary>
        /// Actually performs the pickup - adds to inventory and destroys world item.
        /// </summary>
        private void PerformPickup(ItemDrop itemDrop, ItemDrop.ItemData itemData, FloatingTerrainDummy floatingDummy)
        {
            try
            {
                Inventory inv = _inventory.GetStorageInventory();
                if (inv == null) return;
                
                // Store stack count before pickup
                int stack = itemData.m_stack;
                
                // Add to inventory (this clones the item internally)
                if (inv.AddItem(itemData))
                {
                    // Save inventory to ZDO
                    _inventory.SaveToZDO();
                    
                    // Destroy the world item
                    ZNetScene.instance?.Destroy(itemDrop.gameObject);
                    
                    _pickupsThisSession++;
                    
                    // Track for gathering session reporting
                    if (_isGatheringSession)
                    {
                        string itemName = Localization.instance?.Localize(itemData.m_shared.m_name) ?? itemData.m_shared.m_name;
                        if (_gatheringSessionItems.ContainsKey(itemName))
                            _gatheringSessionItems[itemName] += stack;
                        else
                            _gatheringSessionItems[itemName] = stack;
                    }
                    
                    // Only log pickups when verbose is enabled - use gathering session summary instead
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} picked up {itemData.m_shared.m_name} x{stack} (total pickups: {_pickupsThisSession})");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CompanionAutoPickup] Failed to pickup {itemData?.m_shared?.m_name}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Checks if we should auto-pickup this type of item based on settings.
        /// </summary>
        private bool ShouldPickupItemType(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            
            var itemType = item.m_shared.m_itemType;
            string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            // Trophies
            if (itemType == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy"))
            {
                return pickupTrophies;
            }
            
            // Materials
            if (itemType == ItemDrop.ItemData.ItemType.Material)
            {
                return pickupMaterials;
            }
            
            // Consumables (food, potions, etc.)
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                return pickupConsumables;
            }
            
            // Equipment (weapons, armor, shields)
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Helmet ||
                itemType == ItemDrop.ItemData.ItemType.Chest ||
                itemType == ItemDrop.ItemData.ItemType.Legs ||
                itemType == ItemDrop.ItemData.ItemType.Shoulder ||
                itemType == ItemDrop.ItemData.ItemType.Utility)
            {
                return pickupEquipment;
            }
            
            // Tools
            if (itemType == ItemDrop.ItemData.ItemType.Tool)
            {
                return pickupMaterials; // Group with materials
            }
            
            // Misc (ammo, etc.)
            if (itemType == ItemDrop.ItemData.ItemType.Ammo ||
                itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable)
            {
                return pickupMaterials; // Ammo is useful
            }
            
            // Default to misc setting
            return pickupMisc;
        }
        
        /// <summary>
        /// Shows a message that the companion's inventory is full.
        /// Rate-limited to prevent spam.
        /// </summary>
        private void ShowInventoryFullMessage()
        {
            if (_inventoryFullMessageShown) return;
            
            // Only show to local player who owns this companion
            var owner = _companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                string companionName = _companion?.GetDisplayName() ?? "Companion";
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                    $"{companionName}'s inventory is full!");
                
                _inventoryFullMessageShown = true;
                _inventoryFullMessageCooldown = Time.time + 30f; // Don't spam - wait 30 seconds
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Starts tracking items picked up during a gathering session.
        /// Call this when resource gathering begins.
        /// </summary>
        public void StartGatheringSession()
        {
            _isGatheringSession = true;
            _gatheringSessionItems.Clear();
            
            if (VerboseLogging)
                Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} started gathering session");
        }
        
        /// <summary>
        /// Ends the gathering session and returns a summary of items collected.
        /// Always logs a summary when items were collected (useful feedback for player).
        /// </summary>
        public Dictionary<string, int> EndGatheringSession()
        {
            _isGatheringSession = false;
            var result = new Dictionary<string, int>(_gatheringSessionItems);
            
            // Always log a summary if items were collected (this is useful feedback)
            if (result.Count > 0)
            {
                int totalItems = 0;
                foreach (var kvp in result)
                    totalItems += kvp.Value;
                    
                // Create a brief summary
                string summary = string.Join(", ", result.Select(kvp => $"{kvp.Key} x{kvp.Value}"));
                if (summary.Length > 100)
                    summary = summary.Substring(0, 97) + "...";
                    
                Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} collected {totalItems} items: {summary}");
            }
            
            _gatheringSessionItems.Clear();
            return result;
        }
        
        /// <summary>
        /// Gets the current gathering session items without ending the session.
        /// </summary>
        public Dictionary<string, int> GetGatheringSessionItems()
        {
            return new Dictionary<string, int>(_gatheringSessionItems);
        }
        
        /// <summary>
        /// Toggles auto-pickup on/off.
        /// </summary>
        public void SetAutoPickupEnabled(bool enabled)
        {
            enableAutoPickup = enabled;
        }
        
        /// <summary>
        /// Sets the pickup radius.
        /// </summary>
        public void SetPickupRadius(float radius)
        {
            pickupRadius = Mathf.Clamp(radius, 0.5f, 10f);
        }
        
        /// <summary>
        /// Forces an immediate pickup check.
        /// </summary>
        public void ForcePickupCheck()
        {
            AutoPickup(Time.fixedDeltaTime);
        }
        
        #endregion
    }
}
