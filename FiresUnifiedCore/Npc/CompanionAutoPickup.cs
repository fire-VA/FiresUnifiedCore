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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
        // Drops refused for a full storage are left alone until the next clear (no magnet, no retry each frame).
        private readonly HashSet<int> _refusedDrops = new HashSet<int>();
        private const float LogClearInterval = 60f; // Clear logged items every 60 seconds
        // At most one ownership request per drop every 10 s, to prevent a pickup loop.
        // The cause was an owner ping-pong between two pickers when one couldn't add the item; the drop is left alone between asks.
        private readonly Dictionary<int, float> _ownRequests = new Dictionary<int, float>();
        private const float OwnRequestBackoff = 10f;

        private const float VerboseScanInterval = 5f, VerboseScanRadiusMultiplier = 2f;
        private const float PickupReachDistance = 0.3f;        // closer than this the item is taken; farther it is pulled in
        private const float MagnetSpeed = 15f;                 // metres per second
        private const float FlyingUpSpeed = 4f;                // an item rising faster than this is never pulled
        private const float MaxPullHeightAboveItem = 3f;       // never pull toward a point higher than this above the item
        private const float MaxItemHeightAboveCenter = 1.5f;   // never pull an item already above the companion's head
        private const float MagnetLogInterval = 2f, GateLogInterval = 10f;
        private const float InventoryFullMessageCooldown = 30f;
        private const float MinPickupRadius = 0.5f, MaxPickupRadius = 10f;
        private const int SummaryMaxLength = 100, SummaryTruncateAt = 97;

        private void RequestOwnPaced(ItemDrop itemDrop)
        {
            int id = itemDrop.GetInstanceID();
            if (_ownRequests.TryGetValue(id, out float last) && Time.time - last < OwnRequestBackoff) return;
            if (_ownRequests.ContainsKey(id))
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAutoPickup] {_companion?.companionName} still can't pick up {itemDrop.name} after an ownership request; asking again (every {OwnRequestBackoff:0} s)");
            _ownRequests[id] = Time.time;
            itemDrop.RequestOwn();
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
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
                _refusedDrops.Clear();
                _ownRequests.Clear();
            }
            
            // Periodic scan logging
            if (VerboseLogging && Time.time - _lastScanTime > VerboseScanInterval)
            {
                _lastScanTime = Time.time;
                
                // Also do a non-layer-filtered scan to see ALL nearby items
                var allColliders = Physics.OverlapSphere(center, pickupRadius * VerboseScanRadiusMultiplier);
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
                    Debug.Log($"[CompanionAutoPickup] {_companion?.companionName} found {itemCount} ItemDrops within {pickupRadius * VerboseScanRadiusMultiplier}m (layer mask = {_autoPickupMask})");
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
                if (_refusedDrops.Contains(itemDrop.GetInstanceID())) continue;

                // Items the player dropped from their own inventory are for the companion.
                // Vanilla clears m_autoPickup on those (OnPlayerDrop); our postfix also stamps the ZDO.
                bool playerDropped = IsPlayerDropped(itemDrop);
                if (!itemDrop.m_autoPickup && !playerDropped) continue;
                
                // Don't pickup placed pieces
                if (itemDrop.IsPiece()) continue;
                if (FiresCore.Storage.HopperChest.LeaveForHopper(itemDrop, _companion?.companionName ?? "?")) continue;   // a hopper's item
                
                // Check if item's ZNetView is valid
                ZNetView itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                // Ownership is asked for only after the yield, type, capacity and weight checks pass,
                // so a full companion never pulls a drop away from a player (or another picker) it can't take.
                bool needOwn = !itemDrop.CanPickup();
                
                // Check if stuck in tar
                if (itemDrop.InTar()) continue;

                // YIELD TO PLAYER: don't fight the player's own auto-pickup magnet.
                // If the local player is within their own pickup radius of the item,
                // OR is closer to the item than we are, leave it for them.  Otherwise
                // both magnets pull the item back and forth between us and the owner.
                // Vanilla CanPickup() (needOwn) still enforces the 0.5 s just-dropped delay: no pickup until it passes.
                if (playerDropped)
                    LogPickupGate(itemDrop, true);
                else if (ShouldYieldToPlayer(itemDrop.transform.position, center))
                {
                    LogPickupGate(itemDrop, false);
                    continue;
                }

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
                    if (_refusedDrops.Add(itemDrop.GetInstanceID()))
                        FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName} pickup refused: storage full ({itemData.m_shared.m_name} x{itemData.m_stack} stays on the ground)");
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
                
                if (needOwn) { RequestOwnPaced(itemDrop); continue; }

                // Calculate distance
                float distance = Vector3.Distance(itemDrop.transform.position, center);
                
                if (distance <= pickupRadius)
                {
                    // Close enough - do the pickup
                    if (distance < PickupReachDistance)
                    {
                        // Actually pick up the item
                        PerformPickup(itemDrop, itemData, floatingDummy);
                    }
                    else
                    {
                        // "Magnet" effect - move item toward companion
                        if (!MagnetStep(itemDrop, center, dt, out Vector3 movement)) continue;
                        
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
        
        // Loot could fly up out of a shield bubble: the magnet moved a
        // physics item's transform straight at a point 1 m up inside the companion's own collider; an item that is not picked up there can
        // be pushed out by physics at speed. Now each step zeroes the item's velocity, never pulls an item that is already above the
        // companion's head, and logs the pulled item (Debug, per item per 2 s) and any pulled item seen flying up (> 4 m/s up).
        private readonly Dictionary<int, float> _magnetSaid = new Dictionary<int, float>();
        private bool MagnetStep(ItemDrop itemDrop, Vector3 center, float dt, out Vector3 movement)
        {
            movement = Vector3.zero;
            var rb = itemDrop.GetComponent<Rigidbody>();
            Vector3 velocity = rb != null ? rb.linearVelocity : Vector3.zero;
            int id = itemDrop.GetInstanceID();
            var itemView = itemDrop.GetComponent<ZNetView>();
            string owner = itemView != null && itemView.IsValid() ? (itemView.IsOwner() ? "here" : itemView.GetZDO().GetOwner().ToString()) : "?";
            if (velocity.y > FlyingUpSpeed)
                FiresCore.Logging.FiresLogger.LogWarning($"[LootMagnet] {_companion?.companionName}: {itemDrop.m_itemData?.m_shared?.m_name ?? itemDrop.name} is flying up ({velocity.y:0.0} m/s) at ({itemDrop.transform.position.x:0.0}, {itemDrop.transform.position.y:0.0}, {itemDrop.transform.position.z:0.0}), owner {owner}; not pulled, velocity zeroed");
            // A pull toward a target high above sends loot straight up - never pull toward a point > 3 m above the item
            if (center.y - itemDrop.transform.position.y > MaxPullHeightAboveItem)
            {
                if (!_magnetSaid.TryGetValue(-id, out float lastTooHighLog) || Time.time - lastTooHighLog > MagnetLogInterval)
                {
                    _magnetSaid[-id] = Time.time;
                    FiresCore.Logging.FiresLogger.LogWarning($"[LootMagnet] {_companion?.companionName}: pull target ({center.x:0.0}, {center.y:0.0}, {center.z:0.0}) is {center.y - itemDrop.transform.position.y:0.0} m above {itemDrop.m_itemData?.m_shared?.m_name ?? itemDrop.name} at y {itemDrop.transform.position.y:0.0} (companion on ground {(_companion != null && _companion.GetComponent<Character>() is Character cc ? cc.IsOnGround().ToString() : "?")}); not pulled");
                }
                if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
                return false;
            }
            if (itemDrop.transform.position.y > center.y + MaxItemHeightAboveCenter || velocity.y > FlyingUpSpeed)
            {
                if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
                return false;
            }
            Vector3 direction = Vector3.Normalize(center - itemDrop.transform.position);
            movement = direction * MagnetSpeed * dt;
            itemDrop.transform.position += movement;
            if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
            if (!_magnetSaid.TryGetValue(id, out float lastPullLog) || Time.time - lastPullLog > MagnetLogInterval)
            {
                _magnetSaid[id] = Time.time;
                FiresCore.Logging.FiresLogger.LogDebug($"[LootMagnet] {_companion?.companionName} pulls {itemDrop.m_itemData?.m_shared?.m_name ?? itemDrop.name}: {Vector3.Distance(itemDrop.transform.position, center):0.0} m, item y {itemDrop.transform.position.y:0.0} vs center {center.y:0.0}, velocity {velocity.magnitude:0.0} (up {velocity.y:0.0}), owner {owner}");
            }
            return true;
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
                if (_refusedDrops.Contains(itemDrop.GetInstanceID())) continue;
                
                // Same checks as main loop
                bool playerDropped = IsPlayerDropped(itemDrop);
                if (!itemDrop.m_autoPickup && !playerDropped) continue;
                if (itemDrop.IsPiece()) continue;
                if (FiresCore.Storage.HopperChest.LeaveForHopper(itemDrop, _companion?.companionName ?? "?")) continue;   // a hopper's item
                
                ZNetView itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                bool needOwn = !itemDrop.CanPickup();   // asked for after the checks below
                
                if (itemDrop.InTar()) continue;

                // YIELD TO PLAYER: same rule as the primary scan — don't fight
                // the player's own auto-pickup magnet on the same drop.
                if (playerDropped)
                    LogPickupGate(itemDrop, true);
                else if (ShouldYieldToPlayer(itemDrop.transform.position, center))
                {
                    LogPickupGate(itemDrop, false);
                    continue;
                }

                itemDrop.Load();
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                if (!ShouldPickupItemType(itemData)) continue;
                if (CompanionPickupGate.Blocked(itemDrop.transform.position, _companion != null ? _companion.ownerPlayerId : 0L, _companion?.companionName, itemData.m_shared.m_name)) continue;
                
                Inventory inv = _inventory.GetStorageInventory();
                if (inv == null) continue;
                
                if (!inv.CanAddItem(itemData, itemData.m_stack)) continue;
                
                float maxWeight = _inventory.GetMaxCarryWeight();
                float currentWeight = inv.GetTotalWeight();
                float itemWeight = itemData.GetWeight();
                if (currentWeight + itemWeight > maxWeight) continue;
                
                if (needOwn) { RequestOwnPaced(itemDrop); continue; }

                float distance = Vector3.Distance(itemDrop.transform.position, center);
                
                if (distance <= pickupRadius)
                {
                    if (distance < PickupReachDistance)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAutoPickup] FALLBACK picking up {itemData.m_shared.m_name} (layer {itemLayer})");
                        PerformPickup(itemDrop, itemData, null);
                    }
                    else
                    {
                        if (!MagnetStep(itemDrop, center, dt, out Vector3 movement)) continue;
                    }
                }
            }
        }

        internal const string PlayerDroppedZdoKey = "fires_player_dropped";
        private static readonly Dictionary<string, float> _gateLogTimes = new Dictionary<string, float>();

        /// <summary>True when a player dropped this item from their inventory (ZDO stamp from the OnPlayerDrop postfix).</summary>
        internal static bool IsPlayerDropped(ItemDrop itemDrop)
        {
            if (itemDrop == null) return false;
            ZNetView nv = itemDrop.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return false;
            ZDO zdo = nv.GetZDO();
            return zdo != null && zdo.GetBool(PlayerDroppedZdoKey, false);
        }

        private static void LogPickupGate(ItemDrop itemDrop, bool allowed)
        {
            string name = itemDrop?.m_itemData?.m_shared?.m_name ?? itemDrop?.name ?? "unknown";
            string key = (allowed ? "a:" : "s:") + name;
            if (_gateLogTimes.TryGetValue(key, out float last) && Time.time - last < GateLogInterval) return;
            _gateLogTimes[key] = Time.time;
            if (allowed)
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionPickup] player-dropped {name}: allowed");
            else
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionPickup] {name} near the player: skipped (guard)");
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
                
                // Storage rows only (CompanionGridLayout). Refuse up front when storage cannot take the whole stack,
                // so the drop stays on the ground (and is remembered as refused) instead of a partial add.
                bool companionBag = CompanionGridLayout.IsCompanionBag(inv);
                if (companionBag && !inv.CanAddItem(itemData, stack))
                {
                    if (_refusedDrops.Add(itemDrop.GetInstanceID()))
                        FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName} pickup refused: storage full ({itemData.m_shared.m_name} x{stack} stays on the ground)");
                    return;
                }

                // To prevent item loss: add a COPY, count what landed, destroy the drop only when all of it did.
                // TakeDropVerified is the only place that destroys the drop; it logs "picked up ... -> storage" / "pickup refused".
                int taken = FiresCore.Npc.IdleBehaviors.ChestHelper.TakeDropVerified(itemDrop, inv, _companion?.companionName ?? "companion");
                if (taken <= 0)
                {
                    _refusedDrops.Add(itemDrop.GetInstanceID());
                    return;
                }
                stack = taken;

                // Anything of this item that landed outside the storage rows moves into a free storage cell.
                if (companionBag)
                {
                    foreach (var it in inv.GetAllItems())
                    {
                        if (it?.m_shared == null || it.m_shared.m_name != itemData.m_shared.m_name || CompanionGridLayout.IsStorage(it.m_gridPos)) continue;
                        Vector2i free = CompanionGridLayout.FirstFreeStorageCell(inv);
                        if (free.x < 0) break;
                        FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName} moved {it.m_shared.m_name} x{it.m_stack} ({it.m_gridPos.x},{it.m_gridPos.y}) -> storage ({free.x},{free.y})");
                        it.m_gridPos = free;
                    }
                }
                else
                {
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] {_companion?.companionName} pickup went to a bag that is not registered as a companion bag");
                }
                {
                    // Save inventory to ZDO
                    _inventory.SaveToZDO();

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
                _inventoryFullMessageCooldown = Time.time + InventoryFullMessageCooldown; // Don't spam
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
                if (summary.Length > SummaryMaxLength)
                    summary = summary.Substring(0, SummaryTruncateAt) + "...";
                    
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
            pickupRadius = Mathf.Clamp(radius, MinPickupRadius, MaxPickupRadius);
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

    /// <summary>
    /// Vanilla Humanoid.DropItem calls ItemDrop.OnPlayerDrop only when IsPlayer(); it just clears the local,
    /// unsynced m_autoPickup. Stamp the drop's ZDO so companions (on any peer) can tell a deliberate player drop.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.OnPlayerDrop))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionPlayerDropStampPatch
    {
        private static void Postfix(ItemDrop __instance)
        {
            if (__instance == null) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            nv.GetZDO()?.Set(CompanionAutoPickup.PlayerDroppedZdoKey, true);
        }
    }
}
