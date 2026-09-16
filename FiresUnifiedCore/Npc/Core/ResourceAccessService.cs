using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Centralized service for accessing resources from companion inventory and nearby chests.
    /// Provides a unified API for all behaviors that need to find, pull, or deposit items.
    /// 
    /// USAGE:
    /// 1. Create an instance with the companion's inventory and position
    /// 2. Call RefreshNearbyChests() to scan for containers
    /// 3. Use Has/Get/Pull/Deposit methods for resource operations
    /// 
    /// This eliminates duplicate chest-finding and item-checking code across behaviors.
    /// </summary>
    public class ResourceAccessService
    {
        #region Fields
        
        private readonly CompanionInventory _inventory;
        private readonly Func<Vector3> _positionGetter;
        private List<Container> _nearbyChests = new List<Container>();
        private float _lastChestScanTime = 0f;
        private Vector3 _lastChestScanPosition = Vector3.zero;
        
        /// <summary>
        /// How often to re-scan for chests (prevents spam).
        /// </summary>
        private const float ChestScanCooldown = 5f;
        
        /// <summary>
        /// Distance threshold before forcing a chest rescan.
        /// </summary>
        private const float PositionChangeThreshold = 5f;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// The companion's storage inventory.
        /// </summary>
        public Inventory StorageInventory => _inventory?.GetStorageInventory();
        
        /// <summary>
        /// The current search position.
        /// </summary>
        public Vector3 CurrentPosition => _positionGetter?.Invoke() ?? Vector3.zero;
        
        /// <summary>
        /// List of nearby chests (from last scan).
        /// </summary>
        public IReadOnlyList<Container> NearbyChests => _nearbyChests;
        
        /// <summary>
        /// Number of nearby chests found.
        /// </summary>
        public int ChestCount => _nearbyChests.Count;
        
        /// <summary>
        /// Whether we have any nearby chests.
        /// </summary>
        public bool HasNearbyChests => _nearbyChests.Count > 0;
        
        /// <summary>
        /// The search radius used for chest discovery.
        /// </summary>
        public float SearchRadius { get; set; }
        
        /// <summary>
        /// Enable verbose logging for debugging.
        /// </summary>
        public bool VerboseLogging { get; set; } = false;
        
        /// <summary>
        /// Behavior name for logging.
        /// </summary>
        public string BehaviorName { get; set; } = "ResourceAccess";
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// Creates a new ResourceAccessService.
        /// </summary>
        /// <param name="inventory">The companion's inventory component</param>
        /// <param name="positionGetter">Function to get current position (usually () => transform.position)</param>
        /// <param name="searchRadius">Radius to search for chests (default: CompanionSettings.ChestSearchRadius)</param>
        public ResourceAccessService(CompanionInventory inventory, Func<Vector3> positionGetter, float searchRadius = -1f)
        {
            _inventory = inventory;
            _positionGetter = positionGetter;
            SearchRadius = searchRadius > 0 ? searchRadius : CompanionSettings.ChestSearchRadius;
        }
        
        #endregion
        
        #region Chest Discovery
        
        /// <summary>
        /// Refreshes the list of nearby chests from the companion's current position.
        /// Uses ChestSearchRadius (default 50m) to find chests.
        /// Automatically throttled to prevent spam.
        /// </summary>
        /// <param name="force">Force refresh even if recently scanned</param>
        /// <returns>Number of chests found</returns>
        public int RefreshNearbyChests(bool force = false)
        {
            Vector3 currentPos = CurrentPosition;
            float timeSinceLastScan = Time.time - _lastChestScanTime;
            float distanceMoved = Vector3.Distance(currentPos, _lastChestScanPosition);
            
            // Skip if recently scanned and haven't moved much
            if (!force && timeSinceLastScan < ChestScanCooldown && distanceMoved < PositionChangeThreshold)
            {
                return _nearbyChests.Count;
            }
            
            _nearbyChests = IdleBehaviors.ChestHelper.FindNearbyChests(currentPos, SearchRadius);
            _lastChestScanTime = Time.time;
            _lastChestScanPosition = currentPos;
            
            if (VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] Found {_nearbyChests.Count} chests within {SearchRadius}m");
            }
            
            return _nearbyChests.Count;
        }
        
        /// <summary>
        /// Refreshes the list of nearby chests from a specific position.
        /// Use this when the companion needs to search for chests near a TARGET (fire, tree, etc.)
        /// rather than near themselves.
        /// </summary>
        /// <param name="searchPosition">Position to search from</param>
        /// <param name="force">Force refresh even if recently scanned</param>
        /// <returns>Number of chests found</returns>
        public int RefreshNearbyChests(Vector3 searchPosition, bool force = false)
        {
            float timeSinceLastScan = Time.time - _lastChestScanTime;
            float distanceMoved = Vector3.Distance(searchPosition, _lastChestScanPosition);
            
            // Skip if recently scanned and search position hasn't changed much
            if (!force && timeSinceLastScan < ChestScanCooldown && distanceMoved < PositionChangeThreshold)
            {
                return _nearbyChests.Count;
            }
            
            _nearbyChests = IdleBehaviors.ChestHelper.FindNearbyChests(searchPosition, SearchRadius);
            _lastChestScanTime = Time.time;
            _lastChestScanPosition = searchPosition;
            
            if (VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] Found {_nearbyChests.Count} chests within {SearchRadius}m of {searchPosition}");
            }
            
            return _nearbyChests.Count;
        }
        
        /// <summary>
        /// Sets a specific list of chests to use (bypasses scanning).
        /// </summary>
        public void SetNearbyChests(List<Container> chests)
        {
            _nearbyChests = chests ?? new List<Container>();
            _lastChestScanTime = Time.time;
            _lastChestScanPosition = CurrentPosition;
        }
        
        #endregion
        
        #region Item Checking - Inventory
        
        /// <summary>
        /// Checks if the companion has an item in their inventory.
        /// </summary>
        public bool HasItemInInventory(string prefabName)
        {
            var storage = StorageInventory;
            if (storage == null) return false;
            
            foreach (var item in storage.GetAllItems())
            {
                if (item == null) continue;
                string dropName = item.m_dropPrefab?.name ?? "";
                if (dropName.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if the companion has any of the specified items in inventory.
        /// </summary>
        public bool HasAnyItemInInventory(params string[] prefabNames)
        {
            foreach (string name in prefabNames)
            {
                if (HasItemInInventory(name)) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Gets the count of an item in the companion's inventory.
        /// </summary>
        public int GetItemCountInInventory(string prefabName)
        {
            var storage = StorageInventory;
            if (storage == null) return 0;
            
            int count = 0;
            foreach (var item in storage.GetAllItems())
            {
                if (item == null) continue;
                string dropName = item.m_dropPrefab?.name ?? "";
                if (dropName.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    count += item.m_stack;
            }
            
            return count;
        }
        
        #endregion
        
        #region Item Checking - Chests
        
        /// <summary>
        /// Checks if nearby chests have an item.
        /// </summary>
        public bool HasItemInChests(string prefabName)
        {
            return IdleBehaviors.ChestHelper.ChestsHaveItem(_nearbyChests, prefabName);
        }
        
        /// <summary>
        /// Checks if nearby chests have any of the specified items.
        /// </summary>
        public bool HasAnyItemInChests(params string[] prefabNames)
        {
            foreach (string name in prefabNames)
            {
                if (HasItemInChests(name)) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Gets the count of an item in nearby chests.
        /// </summary>
        public int GetItemCountInChests(string prefabName)
        {
            return IdleBehaviors.ChestHelper.CountItemInChests(_nearbyChests, prefabName);
        }
        
        #endregion
        
        #region Item Checking - Combined
        
        /// <summary>
        /// Checks if an item exists in inventory OR nearby chests.
        /// </summary>
        public bool HasItem(string prefabName)
        {
            return HasItemInInventory(prefabName) || HasItemInChests(prefabName);
        }
        
        /// <summary>
        /// Checks if any of the specified items exist in inventory OR chests.
        /// </summary>
        public bool HasAnyItem(params string[] prefabNames)
        {
            foreach (string name in prefabNames)
            {
                if (HasItem(name)) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Gets total count of an item (inventory + chests).
        /// </summary>
        public int GetTotalItemCount(string prefabName)
        {
            return GetItemCountInInventory(prefabName) + GetItemCountInChests(prefabName);
        }
        
        #endregion
        
        #region Item Operations - Pull from Chests
        
        /// <summary>
        /// Pulls items from nearby chests into the companion's inventory.
        /// </summary>
        /// <param name="prefabName">The item prefab name to pull</param>
        /// <param name="maxAmount">Maximum amount to pull</param>
        /// <returns>Number of items actually pulled</returns>
        public int PullItemFromChests(string prefabName, int maxAmount)
        {
            var storage = StorageInventory;
            if (storage == null) return 0;
            
            int pulled = IdleBehaviors.ChestHelper.PullItemsByPrefabName(_nearbyChests, storage, prefabName, maxAmount);
            
            if (pulled > 0)
            {
                _inventory?.SaveToZDO();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[{BehaviorName}] Pulled {pulled}x {prefabName} from chests");
                }
            }
            
            return pulled;
        }
        
        /// <summary>
        /// Pulls the first available item from a list of prefab names.
        /// </summary>
        /// <param name="maxAmount">Maximum amount to pull</param>
        /// <param name="prefabNames">List of item prefab names to try (in order)</param>
        /// <returns>Tuple of (prefabName pulled, amount pulled)</returns>
        public (string prefabName, int amount) PullFirstAvailableItem(int maxAmount, params string[] prefabNames)
        {
            foreach (string name in prefabNames)
            {
                if (HasItemInChests(name))
                {
                    int pulled = PullItemFromChests(name, maxAmount);
                    if (pulled > 0)
                    {
                        return (name, pulled);
                    }
                }
            }
            return (null, 0);
        }
        
        #endregion
        
        #region Item Operations - Deposit to Chests
        
        /// <summary>
        /// Deposits a specific item type to nearby chests.
        /// </summary>
        /// <param name="prefabName">Item prefab name to deposit</param>
        /// <returns>Number of items deposited</returns>
        public int DepositItemToChests(string prefabName)
        {
            var storage = StorageInventory;
            if (storage == null) return 0;
            
            int deposited = IdleBehaviors.ChestHelper.DepositItemType(_nearbyChests, storage, prefabName);
            
            if (deposited > 0)
            {
                _inventory?.SaveToZDO();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[{BehaviorName}] Deposited {deposited}x {prefabName} to chests");
                }
            }
            
            return deposited;
        }
        
        /// <summary>
        /// Deposits all depositable items to nearby chests using smart stacking.
        /// </summary>
        /// <returns>Total items deposited</returns>
        public int SmartDepositAll()
        {
            if (_inventory == null) return 0;
            
            var result = IdleBehaviors.ChestHelper.SmartDeposit(_inventory, CurrentPosition, SearchRadius, _nearbyChests);
            
            if (result.Success && VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] {result.Message}");
            }
            
            return result.ItemsDeposited;
        }
        
        #endregion
        
        #region Inventory Status
        
        /// <summary>
        /// Gets detailed inventory status.
        /// </summary>
        public IdleBehaviors.ChestHelper.InventoryStatus GetInventoryStatus()
        {
            return IdleBehaviors.ChestHelper.CheckInventoryStatus(_inventory);
        }
        
        /// <summary>
        /// Checks if inventory is full or nearly full.
        /// </summary>
        public bool IsInventoryFullOrNearlyFull()
        {
            return IdleBehaviors.ChestHelper.IsInventoryFullOrNearlyFull(_inventory);
        }
        
        /// <summary>
        /// Checks if inventory can pick up more items.
        /// </summary>
        public bool CanPickUpMore(float estimatedItemWeight = 1f)
        {
            return IdleBehaviors.ChestHelper.CanPickUpMore(_inventory, estimatedItemWeight);
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Logs the current state for debugging.
        /// </summary>
        public void LogState(string context = "")
        {
            var status = GetInventoryStatus();
            Debug.Log($"[{BehaviorName}] {context} - Chests: {ChestCount}, Inv: {status.FreeSlots}/{status.TotalSlots} slots, {status.WeightPercent:F0}% weight");
        }
        
        /// <summary>
        /// Logs inventory contents for debugging.
        /// </summary>
        public void LogInventoryContents(string context = "")
        {
            var storage = StorageInventory;
            if (storage == null)
            {
                Debug.Log($"[{BehaviorName}] {context} - No inventory");
                return;
            }
            
            var items = storage.GetAllItems();
            Debug.Log($"[{BehaviorName}] {context} - Inventory has {items.Count} item stacks:");
            foreach (var item in items)
            {
                if (item != null)
                {
                    string dropName = item.m_dropPrefab?.name ?? "?";
                    Debug.Log($"  - {item.m_shared?.m_name} (prefab: {dropName}) x{item.m_stack}");
                }
            }
        }
        
        #endregion
    }
}
