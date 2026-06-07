using UnityEngine;
using FiresCore.Items;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Utility class for companion chest/container interactions.
    /// Provides common functionality for finding chests, pulling items, and depositing items.
    /// Handles proper stacking - deposits to chests that already have matching items.
    /// 
    /// NOTE: For finding chests, this class now uses ContainerRegistry when available
    /// for faster lookups. ContainerRegistry tracks containers globally via Harmony patches,
    /// avoiding the need to call Physics.OverlapSphere every time.
    /// 
    /// ITEM DATA HANDLING: Uses ItemDataHelper for safe item cloning that preserves
    /// all item properties (quality, durability, custom data, etc.)
    /// </summary>
    public static class ChestHelper
    {
        /// <summary>
        /// Default search radius - now reads from CompanionSettings for configurability.
        /// </summary>
        public static float DEFAULT_SEARCH_RADIUS => CompanionSettings.ChestSearchRadius;
        
        /// <summary>
        /// Search radius for deposit operations â€” reads from config via CompanionSettings.
        /// </summary>
        public static float DEPOSIT_SEARCH_RADIUS => CompanionSettings.ChestSearchRadius;
        
        /// <summary>
        /// Whether to use ContainerRegistry for faster chest lookups.
        /// Set to false to use the old Physics.OverlapSphere method.
        /// </summary>
        public static bool UseContainerRegistry = true;
        
        #region Inventory Fullness Detection
        
        /// <summary>
        /// Result of an inventory fullness check with details.
        /// NOTE: This class delegates to CompanionInventory.InventoryStatus which is the
        /// SINGLE SOURCE OF TRUTH for inventory checks. This wrapper exists for convenience
        /// and backwards compatibility.
        /// </summary>
        public class InventoryStatus
        {
            public bool IsFull;              // Cannot add any more items
            public bool IsNearlyFull;        // Getting full (>80% weight or <3 slots)
            public bool IsOverweight;        // Over carry weight limit
            public float CurrentWeight;
            public float MaxWeight;
            public float WeightPercent;
            public int FreeSlots;
            public int TotalSlots;
            public string Reason;            // Human-readable reason
            
            public bool NeedsDeposit => IsFull || IsNearlyFull || IsOverweight;
            
            /// <summary>
            /// Creates a ChestHelper.InventoryStatus from CompanionInventory.InventoryStatus.
            /// </summary>
            public static InventoryStatus FromCompanionStatus(CompanionInventory.InventoryStatus source)
            {
                if (source == null) return new InventoryStatus { IsFull = true, Reason = "No status" };
                
                return new InventoryStatus
                {
                    IsFull = source.IsFull,
                    IsNearlyFull = source.IsNearlyFull,
                    IsOverweight = source.IsOverweight,
                    CurrentWeight = source.CurrentWeight,
                    MaxWeight = source.MaxWeight,
                    WeightPercent = source.WeightPercent,
                    FreeSlots = source.FreeSlots,
                    TotalSlots = source.TotalSlots,
                    Reason = source.Reason
                };
            }
        }
        
        /// <summary>
        /// Checks the fullness status of a companion's inventory.
        /// Returns detailed info about weight, slots, and whether deposit is needed.
        /// 
        /// NOTE: This method delegates to CompanionInventory.GetInventoryStatus() which is
        /// the SINGLE SOURCE OF TRUTH. Use CompanionInventory directly when you have access.
        /// </summary>
        public static InventoryStatus CheckInventoryStatus(CompanionInventory inventory)
        {
            if (inventory == null)
            {
                return new InventoryStatus { IsFull = true, Reason = "No inventory" };
            }
            
            // Delegate to the single source of truth
            var sourceStatus = inventory.GetInventoryStatus();
            return InventoryStatus.FromCompanionStatus(sourceStatus);
        }
        
        /// <summary>
        /// Quick check if companion inventory is full or nearly full.
        /// Delegates to CompanionInventory.IsInventoryFullOrNearlyFull().
        /// </summary>
        public static bool IsInventoryFullOrNearlyFull(CompanionInventory inventory)
        {
            if (inventory == null) return true;
            return inventory.IsInventoryFullOrNearlyFull();
        }
        
        /// <summary>
        /// Quick check if companion can pick up more items.
        /// Delegates to CompanionInventory.CanPickUpMoreItems().
        /// </summary>
        public static bool CanPickUpMore(CompanionInventory inventory, float estimatedItemWeight = 1f)
        {
            if (inventory == null) return false;
            return inventory.CanPickUpMoreItems(estimatedItemWeight);
        }
        
        #endregion
        
        #region Smart Deposit Logic
        
        /// <summary>
        /// Result of a smart deposit operation.
        /// </summary>
        public class DepositResult
        {
            public bool Success;
            public int ItemsDeposited;
            public Container TargetChest;
            public Vector3 ChestPosition;
            public float DistanceToChest;
            public string Message;
        }
        
        /// <summary>
        /// Finds the best chest to deposit items to.
        /// Uses smart station-aware logic to place items near relevant crafting stations.
        /// Prioritizes:
        /// 1. Chests near relevant crafting stations (ores near smelters, etc.)
        /// 2. Chests that already have matching items (for stacking)
        /// 3. Closest reachable chest with room
        /// </summary>
        public static Container FindBestDepositChest(Vector3 position, List<Container> chests, Inventory sourceInventory)
        {
            if (chests == null || chests.Count == 0 || sourceInventory == null)
                return null;
            
            // Get list of items we want to deposit
            var itemsToDeposit = GetDepositableItems(sourceInventory);
            if (itemsToDeposit.Count == 0) return null;
            
            // Use smart storage for intelligent placement
            // Find the most common category in our items to deposit
            var categoryCounts = new Dictionary<SmartStorageOrganizer.ItemCategory, int>();
            foreach (var item in itemsToDeposit)
            {
                var category = SmartStorageOrganizer.GetItemCategory(item);
                if (!categoryCounts.ContainsKey(category))
                    categoryCounts[category] = 0;
                categoryCounts[category]++;
            }
            
            // Get the primary item to base our search on (most common category)
            ItemDrop.ItemData primaryItem = null;
            if (categoryCounts.Count > 0)
            {
                var primaryCategory = categoryCounts.OrderByDescending(kv => kv.Value).First().Key;
                primaryItem = itemsToDeposit.FirstOrDefault(i => 
                    SmartStorageOrganizer.GetItemCategory(i) == primaryCategory);
            }
            
            // Try smart storage recommendation first
            if (primaryItem != null)
            {
                var recommendation = SmartStorageOrganizer.FindBestChestForItem(
                    primaryItem, chests, position, stationSearchRadius: 25f);
                
                if (recommendation != null && recommendation.Chest != null)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ChestHelper] Smart deposit target: {recommendation.Reason}");
                    return recommendation.Chest;
                }
            }
            
            // Fallback to original logic
            Container bestChest = null;
            float bestScore = float.MaxValue;
            
            // Build a set of item prefab names for quick lookup
            var depositPrefabs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsToDeposit)
            {
                if (item?.m_dropPrefab != null)
                    depositPrefabs.Add(item.m_dropPrefab.name);
            }
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                // Check if chest has room
                if (containerInv.GetEmptySlots() == 0 && !HasStackSpace(containerInv, itemsToDeposit))
                    continue;
                
                float distance = Vector3.Distance(position, container.transform.position);
                
                // Calculate score: lower is better
                float score = distance;
                
                // Bonus for chests that already have matching items (better for stacking)
                int matchingStacks = CountMatchingStacks(containerInv, depositPrefabs);
                if (matchingStacks > 0)
                {
                    score -= matchingStacks * 5f; // Reduce score for each matching stack
                }
                
                if (score < bestScore)
                {
                    bestScore = score;
                    bestChest = container;
                }
            }
            
            return bestChest;
        }
        
        /// <summary>
        /// Finds the closest chest that has room for at least one item.
        /// </summary>
        public static Container FindClosestChestWithRoom(Vector3 position, float searchRadius = -1f)
        {
            if (searchRadius < 0) searchRadius = DEPOSIT_SEARCH_RADIUS;
            
            var chests = FindNearbyChests(position, searchRadius);
            
            Container closest = null;
            float closestDist = float.MaxValue;
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                // Must have at least one empty slot
                if (containerInv.GetEmptySlots() == 0) continue;
                
                float dist = Vector3.Distance(position, container.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = container;
                }
            }
            
            return closest;
        }
        
        /// <summary>
        /// Performs a smart deposit of all depositable items from a companion's inventory.
        /// - Excludes food, weapons, armor, and equipped items
        /// - Deposits to chests that already have matching stacks when possible
        /// - Returns detailed result
        /// </summary>
        public static DepositResult SmartDeposit(CompanionInventory companionInventory, Vector3 companionPosition, float searchRadius = -1f, List<Container> preFoundChests = null)
        {
            if (searchRadius < 0) searchRadius = DEPOSIT_SEARCH_RADIUS;
            
            var result = new DepositResult();
            
            if (companionInventory == null)
            {
                result.Message = "No inventory";
                return result;
            }
            
            var storageInv = companionInventory.GetStorageInventory();
            if (storageInv == null)
            {
                result.Message = "Storage unavailable";
                return result;
            }
            
            // Find chests if not provided
            var chests = preFoundChests ?? FindNearbyChests(companionPosition, searchRadius);
            if (chests.Count == 0)
            {
                result.Message = "No chests nearby";
                return result;
            }
            
            // Sort chests by distance
            chests = chests.OrderBy(c => Vector3.Distance(companionPosition, c.transform.position)).ToList();
            
            // Get items to deposit (excluding equipment, food, weapons)
            var itemsToDeposit = GetDepositableItems(storageInv);
            if (itemsToDeposit.Count == 0)
            {
                result.Message = "Nothing to deposit";
                return result;
            }
            
            // Find the best chest to start with
            result.TargetChest = FindBestDepositChest(companionPosition, chests, storageInv);
            if (result.TargetChest != null)
            {
                result.ChestPosition = result.TargetChest.transform.position;
                result.DistanceToChest = Vector3.Distance(companionPosition, result.ChestPosition);
            }
            
            // Deposit items
            int deposited = 0;
            foreach (var item in itemsToDeposit)
            {
                if (item == null) continue;
                
                // First: try to find a chest that already has this item type
                Container targetChest = FindChestWithItem(chests, item);
                
                // Second: try any chest with room
                if (targetChest == null)
                {
                    foreach (var chest in chests)
                    {
                        if (chest == null) continue;
                        var chestInv = chest.GetInventory();
                        if (chestInv != null && chestInv.CanAddItem(item))
                        {
                            targetChest = chest;
                            break;
                        }
                    }
                }
                
                if (targetChest != null)
                {
                    var chestInv = targetChest.GetInventory();
                    if (chestInv != null && chestInv.CanAddItem(item))
                    {
                        // Use ItemDataHelper for safe cloning that preserves all item data
                        var clone = ItemDataHelper.CloneItem(item);
                        if (clone == null) continue;
                        
                        chestInv.AddItem(clone);
                        storageInv.RemoveItem(item);
                        deposited++;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ChestHelper] Deposited {item.m_shared?.m_name} to chest");
                    }
                }
            }
            
            result.ItemsDeposited = deposited;
            result.Success = deposited > 0;
            result.Message = deposited > 0 ? $"Deposited {deposited} items" : "Could not deposit items";
            
            // Save inventory changes
            if (deposited > 0)
            {
                companionInventory.TriggerSaveToZDO();
            }
            
            return result;
        }
        
        /// <summary>
        /// Gets a list of items that should be deposited (excludes food, weapons, equipment).
        /// </summary>
        public static List<ItemDrop.ItemData> GetDepositableItems(Inventory inventory)
        {
            var depositable = new List<ItemDrop.ItemData>();
            
            if (inventory == null) return depositable;
            
            foreach (var item in inventory.GetAllItems())
            {
                if (item == null) continue;
                
                // Skip food
                if (item.m_shared.m_food > 0) continue;
                
                // Skip weapons
                if (item.IsWeapon()) continue;
                
                // Skip equipment/armor
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
        
        /// <summary>
        /// Checks if a container has stack space for any of the given items.
        /// </summary>
        private static bool HasStackSpace(Inventory containerInv, List<ItemDrop.ItemData> items)
        {
            foreach (var item in items)
            {
                if (item == null) continue;
                
                foreach (var containerItem in containerInv.GetAllItems())
                {
                    if (containerItem == null) continue;
                    
                    // Check if same item type and has stack space
                    if (containerItem.m_shared?.m_name == item.m_shared?.m_name &&
                        containerItem.m_stack < containerItem.m_shared.m_maxStackSize)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Counts how many stacks in the container match the given prefab names.
        /// </summary>
        private static int CountMatchingStacks(Inventory containerInv, HashSet<string> prefabNames)
        {
            int count = 0;
            
            foreach (var item in containerInv.GetAllItems())
            {
                if (item?.m_dropPrefab == null) continue;
                
                if (prefabNames.Contains(item.m_dropPrefab.name))
                {
                    count++;
                }
            }
            
            return count;
        }
        
        #endregion
        
        #region Chest Finding
        
        /// <summary>
        /// Finds all accessible containers within range of a position.
        /// Uses CompanionSettings.ChestSearchRadius as default if radius not specified.
        /// 
        /// When UseContainerRegistry is true (default), uses the global ContainerRegistry
        /// for faster lookups. Otherwise falls back to Physics.OverlapSphere.
        /// </summary>
        public static List<Container> FindNearbyChests(Vector3 position, float radius = -1f)
        {
            // Use configurable default if not specified
            if (radius < 0) radius = CompanionSettings.ChestSearchRadius;
            
            // Use ContainerRegistry for faster lookups if available
            if (UseContainerRegistry && ContainerRegistry.IsInitialized)
            {
                return ContainerRegistry.GetNearby(position, radius);
            }
            
            // Fallback to Physics.OverlapSphere
            return FindNearbyChestsPhysics(position, radius);
        }
        
        /// <summary>
        /// Finds nearby chests using Physics.OverlapSphere.
        /// This is the original method, kept as fallback.
        /// </summary>
        private static List<Container> FindNearbyChestsPhysics(Vector3 position, float radius)
        {
            var result = new List<Container>();
            
            var colliders = Physics.OverlapSphere(position, radius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var container = col.GetComponent<Container>() ?? col.GetComponentInParent<Container>();
                if (container == null) continue;
                
                // Only use accessible containers
                var nview = container.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                
                // Skip if already in list
                if (result.Contains(container)) continue;
                
                result.Add(container);
            }
            
            return result;
        }
        
        /// <summary>
        /// Pulls items of a specific type from nearby chests into an inventory.
        /// Matches by prefab name (case-insensitive contains match).
        /// </summary>
        /// <param name="chests">List of containers to search</param>
        /// <param name="destInventory">Destination inventory</param>
        /// <param name="itemNamePattern">Part of the item name to match (e.g., "Wood", "Coal", "CopperOre")</param>
        /// <param name="maxAmount">Maximum total amount to pull</param>
        /// <returns>Number of items pulled</returns>
        public static int PullItemsFromChests(List<Container> chests, Inventory destInventory, string itemNamePattern, int maxAmount)
        {
            if (chests == null || destInventory == null || string.IsNullOrEmpty(itemNamePattern)) 
                return 0;
            
            int totalPulled = 0;
            string pattern = itemNamePattern.ToLowerInvariant();
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                if (totalPulled >= maxAmount) break;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                // Get a copy of items to avoid modification during iteration
                var items = new List<ItemDrop.ItemData>(containerInv.GetAllItems());
                
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (totalPulled >= maxAmount) break;
                    
                    // Match by prefab name or shared name
                    string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    string sharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
                    
                    if (prefabName.Contains(pattern) || sharedName.Contains(pattern))
                    {
                        int toMove = Mathf.Min(item.m_stack, maxAmount - totalPulled);
                        
                        if (destInventory.CanAddItem(item, toMove))
                        {
                            // Use ItemDataHelper for safe cloning that preserves all item data
                            var clone = ItemDataHelper.CloneForPartialTransfer(item, toMove);
                            if (clone == null)
                            {
                                if (CompanionIdleBehavior.VerboseLogging)
                                    Debug.LogWarning($"[ChestHelper] Failed to clone {item.m_shared?.m_name}");
                                continue;
                            }
                            
                            if (destInventory.AddItem(clone))
                            {
                                containerInv.RemoveItem(item, toMove);
                                totalPulled += toMove;
                                
                                if (CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[ChestHelper] Pulled {toMove}x {item.m_shared?.m_name} from chest");
                            }
                        }
                    }
                }
            }
            
            return totalPulled;
        }
        
        /// <summary>
        /// Pulls items by exact prefab name from nearby chests.
        /// </summary>
        public static int PullItemsByPrefabName(List<Container> chests, Inventory destInventory, string prefabName, int maxAmount)
        {
            if (chests == null || destInventory == null || string.IsNullOrEmpty(prefabName)) 
                return 0;
            
            int totalPulled = 0;
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                if (totalPulled >= maxAmount) break;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                var items = new List<ItemDrop.ItemData>(containerInv.GetAllItems());
                
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (totalPulled >= maxAmount) break;
                    
                    string itemPrefab = item.m_dropPrefab?.name ?? "";
                    
                    if (itemPrefab.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        int toMove = Mathf.Min(item.m_stack, maxAmount - totalPulled);
                        
                        if (destInventory.CanAddItem(item, toMove))
                        {
                            // Use ItemDataHelper for safe cloning that preserves all item data
                            var clone = ItemDataHelper.CloneForPartialTransfer(item, toMove);
                            if (clone == null) continue;
                            
                            if (destInventory.AddItem(clone))
                            {
                                containerInv.RemoveItem(item, toMove);
                                totalPulled += toMove;
                            }
                        }
                    }
                }
            }
            
            return totalPulled;
        }
        
        /// <summary>
        /// Deposits items from an inventory to nearby chests.
        /// Prioritizes chests that already contain matching items (for proper stacking).
        /// </summary>
        /// <param name="chests">List of containers to deposit to</param>
        /// <param name="sourceInventory">Source inventory</param>
        /// <param name="itemFilter">Optional filter - only deposit items with names containing this. Null/empty = all items.</param>
        /// <returns>Number of items deposited</returns>
        public static int DepositToChests(List<Container> chests, Inventory sourceInventory, string itemFilter = null)
        {
            if (chests == null || sourceInventory == null) 
                return 0;
            
            int totalDeposited = 0;
            
            // Get items to deposit
            var itemsToDeposit = new List<ItemDrop.ItemData>(sourceInventory.GetAllItems());
            
            foreach (var item in itemsToDeposit)
            {
                if (item == null) continue;
                
                // Apply filter if specified
                if (!string.IsNullOrEmpty(itemFilter))
                {
                    string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    string sharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
                    string filter = itemFilter.ToLowerInvariant();
                    
                    if (!prefabName.Contains(filter) && !sharedName.Contains(filter))
                        continue;
                }
                
                // First pass: Try to find a chest that already has this item type (for stacking)
                Container targetChest = FindChestWithItem(chests, item);
                
                if (targetChest != null)
                {
                    var containerInv = targetChest.GetInventory();
                    if (containerInv != null && containerInv.CanAddItem(item))
                    {
                        // Use ItemDataHelper for safe cloning that preserves all item data
                        var clone = ItemDataHelper.CloneItem(item);
                        if (clone == null) continue;
                        
                        containerInv.AddItem(clone);
                        sourceInventory.RemoveItem(item);
                        totalDeposited++;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ChestHelper] Deposited {item.m_shared?.m_name} to chest (stacked)");
                        
                        continue;
                    }
                }
                
                // Second pass: Find any chest with room
                foreach (var container in chests)
                {
                    if (container == null) continue;
                    
                    var containerInv = container.GetInventory();
                    if (containerInv == null) continue;
                    
                    if (containerInv.CanAddItem(item))
                    {
                        // Use ItemDataHelper for safe cloning that preserves all item data
                        var clone = ItemDataHelper.CloneItem(item);
                        if (clone == null) continue;
                        
                        containerInv.AddItem(clone);
                        sourceInventory.RemoveItem(item);
                        totalDeposited++;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ChestHelper] Deposited {item.m_shared?.m_name} to chest");
                        
                        break;
                    }
                }
            }
            
            return totalDeposited;
        }
        
        /// <summary>
        /// Deposits a specific item type from inventory to nearby chests.
        /// Prioritizes chests that already have the item for stacking.
        /// </summary>
        public static int DepositItemType(List<Container> chests, Inventory sourceInventory, string itemNamePattern)
        {
            return DepositToChests(chests, sourceInventory, itemNamePattern);
        }
        
        /// <summary>
        /// Finds a chest that already contains items of the same type.
        /// Used for proper stacking behavior.
        /// </summary>
        public static Container FindChestWithItem(List<Container> chests, ItemDrop.ItemData item)
        {
            if (item == null) return null;
            
            string itemPrefab = item.m_dropPrefab?.name ?? "";
            string itemSharedName = item.m_shared?.m_name ?? "";
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var existingItem in containerInv.GetAllItems())
                {
                    if (existingItem == null) continue;
                    
                    string existingPrefab = existingItem.m_dropPrefab?.name ?? "";
                    string existingName = existingItem.m_shared?.m_name ?? "";
                    
                    // Match by prefab name or shared name
                    if ((!string.IsNullOrEmpty(itemPrefab) && itemPrefab.Equals(existingPrefab, System.StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(itemSharedName) && itemSharedName.Equals(existingName, System.StringComparison.OrdinalIgnoreCase)))
                    {
                        // Check if chest has room
                        if (containerInv.CanAddItem(item))
                        {
                            return container;
                        }
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Checks if any chest contains a specific item type.
        /// </summary>
        public static bool ChestsHaveItem(List<Container> chests, string itemNamePattern)
        {
            if (chests == null || chests.Count == 0 || string.IsNullOrEmpty(itemNamePattern)) 
                return false;
            
            string pattern = itemNamePattern.ToLowerInvariant();
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var item in containerInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    string sharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
                    
                    // CRITICAL FIX: Use both Contains (for partial matches) AND Equals (for exact matches)
                    // This ensures we find items like "CopperOre" when searching for "CopperOre"
                    // but also find "Wood" when searching for "wood"
                    if (prefabName.Equals(pattern, System.StringComparison.OrdinalIgnoreCase) ||
                        sharedName.Equals(pattern, System.StringComparison.OrdinalIgnoreCase) ||
                        prefabName.Contains(pattern) || 
                        sharedName.Contains(pattern))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the total count of a specific item type across all chests.
        /// </summary>
        public static int CountItemInChests(List<Container> chests, string itemNamePattern)
        {
            if (chests == null || string.IsNullOrEmpty(itemNamePattern)) 
                return 0;
            
            int total = 0;
            string pattern = itemNamePattern.ToLowerInvariant();
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var item in containerInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    string sharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
                    
                    if (prefabName.Contains(pattern) || sharedName.Contains(pattern))
                    {
                        total += item.m_stack;
                    }
                }
            }
            
            return total;
        }
        
        /// <summary>
        /// Common fuel item names used by fires/smelters.
        /// These are EXACT prefab names - use IsValidFuelItem() for checking.
        /// </summary>
        public static readonly string[] FuelItems = new[]
        {
            "Wood",
            "FineWood",
            "RoundLog",
            "Coal",
            "Resin",
            "ElderBark",
            "YggdrasilWood"
        };
        
        /// <summary>
        /// Item prefab name patterns that should NEVER be treated as fuel/wood.
        /// These are items that contain "wood" in their name but aren't actually fuel.
        /// </summary>
        public static readonly string[] ExcludedFromFuel = new[]
        {
            "Arrow",      // ArrowWood, ArrowFire, etc.
            "Shield",     // ShieldWood
            "Sword",      // SwordWood (if any)
            "Club",       // ClubWood (if any)
            "Spear",      // SpearWood (if any)
            "Bow",        // Bows made of wood
            "Atgeir",     // Atgeirs
            "Mace",       // Maces
            "Knife",      // Knives
            "Sledge",     // Sledges
            "Pickaxe",    // Pickaxes
            "Axe",        // Axes (tools)
            "Hoe",        // Hoes
            "Cultivator", // Cultivators
            "Hammer",     // Hammers
            "Torch",      // Torches (don't burn as fuel)
            "piece_",     // Building pieces
            "_sapling",   // Saplings
            "Beech_Sapling",
            "Birch_Sapling",
            "Oak_Sapling",
            "Pine_Sapling",
            "Fir_Sapling"
        };
        
        /// <summary>
        /// Checks if an item is valid fuel (wood, coal, etc.) - NOT an arrow, weapon, or tool.
        /// </summary>
        public static bool IsValidFuelItem(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            
            // First check exclusions - if it contains any excluded pattern, it's not fuel
            foreach (var excluded in ExcludedFromFuel)
            {
                if (prefabName.IndexOf(excluded, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            
            // Then check if it matches any valid fuel item (exact match)
            foreach (var fuel in FuelItems)
            {
                if (prefabName.Equals(fuel, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an item is valid wood for kilns - NOT an arrow, weapon, or tool.
        /// Kilns accept: Wood, RoundLog, FineWood, ElderBark, YggdrasilWood
        /// </summary>
        public static bool IsValidWoodForKiln(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            
            // First check exclusions - if it contains any excluded pattern, it's not valid
            foreach (var excluded in ExcludedFromFuel)
            {
                if (prefabName.IndexOf(excluded, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            
            // Valid wood types for kilns (exact match)
            string[] kilnWoodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
            foreach (var wood in kilnWoodTypes)
            {
                if (prefabName.Equals(wood, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Common ore item names used by smelters.
        /// </summary>
        public static readonly string[] OreItems = new[]
        {
            "CopperOre",
            "TinOre",
            "IronOre",
            "IronScrap",
            "SilverOre",
            "BlackMetalScrap",
            "FlametalOre",
            "FlametalOreNew",
            "BronzeScrap",
            "Chitin"
        };
        
        /// <summary>
        /// Searches all nearby chests for a specific item and returns the total count available.
        /// Useful for checking if there's enough material before starting an operation.
        /// </summary>
        public static int GetAvailableItemCount(List<Container> chests, string prefabName)
        {
            if (chests == null || string.IsNullOrEmpty(prefabName)) 
                return 0;
            
            int total = 0;
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var item in containerInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    string itemPrefab = item.m_dropPrefab?.name ?? "";
                    
                    if (itemPrefab.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        total += item.m_stack;
                    }
                }
            }
            
            return total;
        }
        
        /// <summary>
        /// Finds all unique item types in nearby chests.
        /// Useful for inventory organization and display.
        /// </summary>
        public static Dictionary<string, int> GetChestContents(List<Container> chests)
        {
            var contents = new Dictionary<string, int>();
            
            if (chests == null) return contents;
            
            foreach (var container in chests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var item in containerInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    string itemName = Localization.instance?.Localize(item.m_shared?.m_name) ?? item.m_shared?.m_name ?? "Unknown";
                    
                    if (contents.ContainsKey(itemName))
                        contents[itemName] += item.m_stack;
                    else
                        contents[itemName] = item.m_stack;
                }
            }
            
            return contents;
        }
        
        #endregion
        
        #region Chest Spawning
        
        /// <summary>
        /// Vertical offset when spawning a new chest above an existing one.
        /// </summary>
        private const float CHEST_STACK_HEIGHT = 1.5f;
        
        /// <summary>
        /// Maximum number of chests that can be stacked vertically.
        /// </summary>
        private const int MAX_CHEST_STACK_HEIGHT = 5;
        
        /// <summary>
        /// Finds or spawns a chest near the reference chest that has room for items.
        /// If all nearby chests are full, spawns a new chest above the reference chest.
        /// </summary>
        /// <param name="referenceChest">The chest the companion is currently at</param>
        /// <param name="searchRadius">Radius to search for other chests</param>
        /// <returns>A container with room, or null if spawning failed</returns>
        public static Container FindOrSpawnChestWithRoom(Container referenceChest, float searchRadius = -1f)
        {
            if (referenceChest == null) return null;
            if (searchRadius < 0) searchRadius = DEFAULT_SEARCH_RADIUS;
            
            Vector3 refPos = referenceChest.transform.position;
            
            // First: Try to find any nearby chest with room
            var nearbyChests = FindNearbyChests(refPos, searchRadius);
            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv != null && inv.GetEmptySlots() > 0)
                {
                    return chest;
                }
            }
            
            // No chests with room - try to spawn a new one above the reference chest
            return TrySpawnChestAbove(referenceChest);
        }
        
        /// <summary>
        /// Attempts to spawn a new chest above an existing one.
        /// Will find the topmost chest in a stack and spawn above it.
        /// </summary>
        private static Container TrySpawnChestAbove(Container baseChest)
        {
            if (baseChest == null) return null;
            
            // Get the prefab name of the reference chest
            var nview = baseChest.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return null;
            
            string prefabName = GetChestPrefabName(baseChest);
            if (string.IsNullOrEmpty(prefabName))
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestHelper] Could not determine chest prefab name for spawning");
                return null;
            }
            
            // Find the topmost chest in the stack
            Vector3 basePos = baseChest.transform.position;
            Container topmostChest = baseChest;
            Vector3 topmostPos = basePos;
            
            for (int i = 1; i <= MAX_CHEST_STACK_HEIGHT; i++)
            {
                Vector3 checkPos = basePos + Vector3.up * (CHEST_STACK_HEIGHT * i);
                Container chestAtPos = FindChestAtPosition(checkPos, 0.5f);
                
                if (chestAtPos != null)
                {
                    topmostChest = chestAtPos;
                    topmostPos = chestAtPos.transform.position;
                }
                else
                {
                    break; // Found empty spot
                }
            }
            
            // Calculate spawn position above topmost chest
            Vector3 spawnPos = topmostPos + Vector3.up * CHEST_STACK_HEIGHT;
            
            // Check if we're at max stack height
            float heightAboveBase = spawnPos.y - basePos.y;
            if (heightAboveBase > CHEST_STACK_HEIGHT * MAX_CHEST_STACK_HEIGHT)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestHelper] Max chest stack height reached at {basePos}");
                return null;
            }
            
            // Check if there's already a chest at the spawn position
            if (FindChestAtPosition(spawnPos, 0.5f) != null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestHelper] Chest already exists at spawn position {spawnPos}");
                return null;
            }
            
            // Get the prefab from ZNetScene
            GameObject prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                // Try common chest prefab names
                prefab = ZNetScene.instance?.GetPrefab("piece_chest_wood") 
                      ?? ZNetScene.instance?.GetPrefab("piece_chest");
            }
            
            if (prefab == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestHelper] Could not find chest prefab '{prefabName}' to spawn");
                return null;
            }
            
            // Spawn the new chest
            Quaternion rotation = topmostChest.transform.rotation;
            GameObject newChestObj = CompanionNetworkHelper.Spawn(prefab, spawnPos, rotation);
            
            if (newChestObj == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestHelper] Failed to instantiate chest prefab");
                return null;
            }
            
            // Get the Container component
            Container newChest = newChestObj.GetComponent<Container>();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestHelper] Spawned new chest '{prefabName}' at {spawnPos} (above existing stack)");
            
            return newChest;
        }
        
        /// <summary>
        /// Gets the prefab name for a chest container.
        /// </summary>
        private static string GetChestPrefabName(Container chest)
        {
            if (chest == null) return null;
            
            var nview = chest.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return null;
            
            // Try to get prefab hash from ZDO
            int prefabHash = nview.GetZDO()?.GetPrefab() ?? 0;
            if (prefabHash != 0)
            {
                // Look up prefab name from hash
                var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
                if (prefab != null)
                {
                    return prefab.name;
                }
            }
            
            // Fallback: try to extract from GameObject name
            string objName = chest.gameObject.name;
            if (objName.Contains("(Clone)"))
            {
                return objName.Replace("(Clone)", "").Trim();
            }
            
            return objName;
        }
        
        /// <summary>
        /// Finds a chest at a specific position (used for stack detection).
        /// </summary>
        private static Container FindChestAtPosition(Vector3 position, float tolerance)
        {
            var colliders = Physics.OverlapSphere(position, tolerance);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                var container = col.GetComponent<Container>() ?? col.GetComponentInParent<Container>();
                if (container != null)
                {
                    return container;
                }
            }
            return null;
        }
        
        #endregion
    }
}
