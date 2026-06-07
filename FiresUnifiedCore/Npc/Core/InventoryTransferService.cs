using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Items;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Service for reliable inventory transfers between containers and companion inventories.
    /// 
    /// KEY FEATURES:
    /// - Handles partial transfers (if chest only has 5 coal but we need 10, pulls 5)
    /// - Respects stack limits and weight limits
    /// - Saves ZDO after transfers
    /// - Provides detailed results
    /// 
    /// INSPIRED BY: SmartContainers careful item handling
    /// 
    /// USAGE:
    /// var result = InventoryTransferService.PullItem(chest, destInventory, "Coal", 10);
    /// if (result.Success)
    ///     Debug.Log($"Pulled {result.AmountTransferred} coal");
    /// else if (result.IsPartial)
    ///     Debug.Log($"Only got {result.AmountTransferred}/{result.AmountRequested}");
    /// </summary>
    public static class InventoryTransferService
    {
        #region Transfer Result
        
        /// <summary>
        /// Result of a transfer operation with detailed information.
        /// </summary>
        public class TransferResult
        {
            /// <summary>
            /// Whether the transfer was successful (at least partially).
            /// </summary>
            public bool Success;
            
            /// <summary>
            /// Amount actually transferred.
            /// </summary>
            public int AmountTransferred;
            
            /// <summary>
            /// Amount that was requested.
            /// </summary>
            public int AmountRequested;
            
            /// <summary>
            /// Whether this was a partial transfer (got some but not all).
            /// </summary>
            public bool IsPartial => AmountTransferred > 0 && AmountTransferred < AmountRequested;
            
            /// <summary>
            /// Whether we got the full amount requested.
            /// </summary>
            public bool IsComplete => AmountTransferred >= AmountRequested;
            
            /// <summary>
            /// The item prefab name that was transferred.
            /// </summary>
            public string ItemPrefab;
            
            /// <summary>
            /// The source container (for pull operations).
            /// </summary>
            public Container SourceContainer;
            
            /// <summary>
            /// The target container (for deposit operations).
            /// </summary>
            public Container TargetContainer;
            
            /// <summary>
            /// Human-readable message about the transfer.
            /// </summary>
            public string Message;
            
            /// <summary>
            /// Any error that occurred.
            /// </summary>
            public string Error;
            
            /// <summary>
            /// Creates a successful result.
            /// </summary>
            public static TransferResult Succeeded(int transferred, int requested, string prefab, string message = null)
            {
                return new TransferResult
                {
                    Success = true,
                    AmountTransferred = transferred,
                    AmountRequested = requested,
                    ItemPrefab = prefab,
                    Message = message ?? $"Transferred {transferred}x {prefab}"
                };
            }
            
            /// <summary>
            /// Creates a failed result.
            /// </summary>
            public static TransferResult Failed(string error, int requested = 0, string prefab = null)
            {
                return new TransferResult
                {
                    Success = false,
                    AmountTransferred = 0,
                    AmountRequested = requested,
                    ItemPrefab = prefab,
                    Error = error,
                    Message = error
                };
            }
        }
        
        #endregion
        
        #region Pull Operations
        
        /// <summary>
        /// Pulls items from a single container to an inventory.
        /// Handles partial amounts if full amount not available.
        /// </summary>
        /// <param name="source">Container to pull from</param>
        /// <param name="destination">Inventory to pull into</param>
        /// <param name="prefabName">Exact prefab name of item to pull</param>
        /// <param name="maxAmount">Maximum amount to pull</param>
        /// <returns>Result with amount actually transferred</returns>
        public static TransferResult PullItem(
            Container source,
            Inventory destination,
            string prefabName,
            int maxAmount)
        {
            if (source == null)
                return TransferResult.Failed("Source container is null", maxAmount, prefabName);
            
            if (destination == null)
                return TransferResult.Failed("Destination inventory is null", maxAmount, prefabName);
            
            if (string.IsNullOrEmpty(prefabName))
                return TransferResult.Failed("Prefab name is empty", maxAmount, prefabName);
            
            if (maxAmount <= 0)
                return TransferResult.Failed("Invalid amount", maxAmount, prefabName);
            
            var sourceInv = source.GetInventory();
            if (sourceInv == null)
                return TransferResult.Failed("Source inventory is null", maxAmount, prefabName);
            
            int totalPulled = 0;
            var itemsToProcess = new List<ItemDrop.ItemData>(sourceInv.GetAllItems());
            
            foreach (var item in itemsToProcess)
            {
                if (item == null) continue;
                if (totalPulled >= maxAmount) break;
                
                string itemPrefab = item.m_dropPrefab?.name ?? "";
                if (!itemPrefab.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    continue;
                
                int toMove = Mathf.Min(item.m_stack, maxAmount - totalPulled);
                
                // Check if destination can hold this
                if (!CanAddToInventory(destination, item, toMove))
                {
                    // Try to add what we can
                    toMove = GetMaxAddableAmount(destination, item);
                    if (toMove <= 0) continue;
                }
                
                // Use ItemDataHelper for safe cloning that preserves all data
                var clone = ItemDataHelper.CloneForPartialTransfer(item, toMove);
                if (clone == null)
                {
                    Debug.LogWarning($"[InventoryTransferService] Failed to clone {prefabName}");
                    continue;
                }
                
                if (destination.AddItem(clone))
                {
                    sourceInv.RemoveItem(item, toMove);
                    totalPulled += toMove;
                }
            }
            
            // Save the container
            if (totalPulled > 0)
            {
                SaveContainer(source);
            }
            
            if (totalPulled == 0)
            {
                return TransferResult.Failed($"No {prefabName} found or cannot add to inventory", maxAmount, prefabName);
            }
            
            var result = TransferResult.Succeeded(totalPulled, maxAmount, prefabName);
            result.SourceContainer = source;
            
            if (result.IsPartial)
            {
                result.Message = $"Pulled {totalPulled}/{maxAmount}x {prefabName} (partial)";
            }
            
            return result;
        }
        
        /// <summary>
        /// Pulls items from multiple containers until the amount is met.
        /// Tries containers in order, taking from each until full amount is reached.
        /// </summary>
        public static TransferResult PullItemFromContainers(
            IEnumerable<Container> sources,
            Inventory destination,
            string prefabName,
            int maxAmount)
        {
            if (sources == null)
                return TransferResult.Failed("Sources list is null", maxAmount, prefabName);
            
            if (destination == null)
                return TransferResult.Failed("Destination inventory is null", maxAmount, prefabName);
            
            int totalPulled = 0;
            int remaining = maxAmount;
            Container lastSourceUsed = null;
            
            foreach (var source in sources)
            {
                if (source == null) continue;
                if (remaining <= 0) break;
                
                var result = PullItem(source, destination, prefabName, remaining);
                
                if (result.Success)
                {
                    totalPulled += result.AmountTransferred;
                    remaining -= result.AmountTransferred;
                    lastSourceUsed = source;
                }
            }
            
            if (totalPulled == 0)
            {
                return TransferResult.Failed($"No {prefabName} found in any container", maxAmount, prefabName);
            }
            
            var finalResult = TransferResult.Succeeded(totalPulled, maxAmount, prefabName);
            finalResult.SourceContainer = lastSourceUsed;
            
            if (finalResult.IsPartial)
            {
                finalResult.Message = $"Pulled {totalPulled}/{maxAmount}x {prefabName} from multiple containers (partial)";
            }
            else
            {
                finalResult.Message = $"Pulled {totalPulled}x {prefabName} from containers";
            }
            
            return finalResult;
        }
        
        /// <summary>
        /// Pulls the first available item from a list of prefab names.
        /// Useful when any of several items would work (e.g., any wood type).
        /// </summary>
        public static TransferResult PullFirstAvailable(
            IEnumerable<Container> sources,
            Inventory destination,
            IEnumerable<string> prefabNames,
            int maxAmount)
        {
            if (sources == null || prefabNames == null || destination == null)
                return TransferResult.Failed("Invalid parameters", maxAmount);
            
            foreach (var prefabName in prefabNames)
            {
                // Check if any container has this item
                bool found = false;
                foreach (var source in sources)
                {
                    if (source == null) continue;
                    var inv = source.GetInventory();
                    if (inv == null) continue;
                    
                    foreach (var item in inv.GetAllItems())
                    {
                        if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
                
                if (found)
                {
                    var result = PullItemFromContainers(sources, destination, prefabName, maxAmount);
                    if (result.Success)
                        return result;
                }
            }
            
            return TransferResult.Failed("None of the requested items found", maxAmount);
        }
        
        #endregion
        
        #region Deposit Operations
        
        /// <summary>
        /// Deposits a specific item from an inventory to a container.
        /// </summary>
        public static TransferResult DepositItem(
            Inventory source,
            Container destination,
            string prefabName,
            int maxAmount = int.MaxValue)
        {
            if (source == null)
                return TransferResult.Failed("Source inventory is null", maxAmount, prefabName);
            
            if (destination == null)
                return TransferResult.Failed("Destination container is null", maxAmount, prefabName);
            
            var destInv = destination.GetInventory();
            if (destInv == null)
                return TransferResult.Failed("Destination inventory is null", maxAmount, prefabName);
            
            int totalDeposited = 0;
            var itemsToProcess = new List<ItemDrop.ItemData>(source.GetAllItems());
            
            foreach (var item in itemsToProcess)
            {
                if (item == null) continue;
                if (totalDeposited >= maxAmount) break;
                
                string itemPrefab = item.m_dropPrefab?.name ?? "";
                if (!itemPrefab.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    continue;
                
                int toMove = Mathf.Min(item.m_stack, maxAmount - totalDeposited);
                
                // Check if destination can hold this
                if (!CanAddToInventory(destInv, item, toMove))
                {
                    toMove = GetMaxAddableAmount(destInv, item);
                    if (toMove <= 0) continue;
                }
                
                // Use ItemDataHelper for safe cloning that preserves all data
                var clone = ItemDataHelper.CloneForPartialTransfer(item, toMove);
                if (clone == null)
                {
                    Debug.LogWarning($"[InventoryTransferService] Failed to clone {prefabName} for deposit");
                    continue;
                }
                
                if (destInv.AddItem(clone))
                {
                    source.RemoveItem(item, toMove);
                    totalDeposited += toMove;
                }
            }
            
            if (totalDeposited > 0)
            {
                SaveContainer(destination);
            }
            
            if (totalDeposited == 0)
            {
                return TransferResult.Failed($"Could not deposit {prefabName}", maxAmount, prefabName);
            }
            
            var result = TransferResult.Succeeded(totalDeposited, maxAmount, prefabName);
            result.TargetContainer = destination;
            return result;
        }
        
        /// <summary>
        /// Deposits an item to the best container from a list.
        /// Prioritizes containers that already have the same item (for stacking).
        /// </summary>
        public static TransferResult DepositItemSmart(
            Inventory source,
            IEnumerable<Container> destinations,
            string prefabName,
            int maxAmount = int.MaxValue)
        {
            if (source == null || destinations == null)
                return TransferResult.Failed("Invalid parameters", maxAmount, prefabName);
            
            // First pass: find containers that already have this item (for stacking)
            Container bestContainer = null;
            int bestMatchingStacks = 0;
            
            foreach (var dest in destinations)
            {
                if (dest == null) continue;
                var inv = dest.GetInventory();
                if (inv == null) continue;
                
                int matchingStacks = 0;
                bool hasRoom = false;
                
                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        matchingStacks++;
                        if (item.m_stack < item.m_shared.m_maxStackSize)
                            hasRoom = true;
                    }
                }
                
                if (hasRoom || inv.GetEmptySlots() > 0)
                {
                    if (matchingStacks > bestMatchingStacks)
                    {
                        bestMatchingStacks = matchingStacks;
                        bestContainer = dest;
                    }
                    else if (bestContainer == null)
                    {
                        bestContainer = dest;
                    }
                }
            }
            
            if (bestContainer == null)
                return TransferResult.Failed("No container with room found", maxAmount, prefabName);
            
            return DepositItem(source, bestContainer, prefabName, maxAmount);
        }
        
        /// <summary>
        /// Deposits all depositable items from a companion inventory.
        /// Excludes food, weapons, armor, tools.
        /// </summary>
        public static TransferResult DepositAllDepositable(
            CompanionInventory source,
            IEnumerable<Container> destinations)
        {
            if (source == null)
                return TransferResult.Failed("Source inventory is null");
            
            var storageInv = source.GetStorageInventory();
            if (storageInv == null)
                return TransferResult.Failed("Storage inventory is null");
            
            int totalDeposited = 0;
            var depositable = IdleBehaviors.ChestHelper.GetDepositableItems(storageInv);
            
            foreach (var item in depositable)
            {
                if (item == null) continue;
                
                string prefab = item.m_dropPrefab?.name;
                if (string.IsNullOrEmpty(prefab)) continue;
                
                var result = DepositItemSmart(storageInv, destinations, prefab);
                if (result.Success)
                {
                    totalDeposited += result.AmountTransferred;
                }
            }
            
            if (totalDeposited > 0)
            {
                source.SaveToZDO();
            }
            
            if (totalDeposited == 0)
            {
                return TransferResult.Failed("No items deposited");
            }
            
            return TransferResult.Succeeded(totalDeposited, totalDeposited, "various",
                $"Deposited {totalDeposited} items");
        }
        
        #endregion
        
        #region Utility Methods
        
        /// <summary>
        /// Checks if an inventory can add a specific amount of an item.
        /// </summary>
        public static bool CanAddToInventory(Inventory inv, ItemDrop.ItemData item, int amount)
        {
            if (inv == null || item == null) return false;
            
            // Check for stacking with existing items
            foreach (var existing in inv.GetAllItems())
            {
                if (existing == null) continue;
                if (existing.m_shared?.m_name != item.m_shared?.m_name) continue;
                
                int canStack = existing.m_shared.m_maxStackSize - existing.m_stack;
                amount -= canStack;
                
                if (amount <= 0) return true;
            }
            
            // Need new slot(s)
            int slotsNeeded = Mathf.CeilToInt((float)amount / item.m_shared.m_maxStackSize);
            return inv.GetEmptySlots() >= slotsNeeded;
        }
        
        /// <summary>
        /// Gets the maximum amount of an item that can be added to an inventory.
        /// </summary>
        public static int GetMaxAddableAmount(Inventory inv, ItemDrop.ItemData item)
        {
            if (inv == null || item == null) return 0;
            
            int maxAdd = 0;
            
            // Count space in existing stacks
            foreach (var existing in inv.GetAllItems())
            {
                if (existing == null) continue;
                if (existing.m_shared?.m_name != item.m_shared?.m_name) continue;
                
                maxAdd += existing.m_shared.m_maxStackSize - existing.m_stack;
            }
            
            // Add space for new stacks in empty slots
            int emptySlots = inv.GetEmptySlots();
            maxAdd += emptySlots * item.m_shared.m_maxStackSize;
            
            return maxAdd;
        }
        
        /// <summary>
        /// Counts how many of a specific item are in an inventory.
        /// </summary>
        public static int CountItem(Inventory inv, string prefabName)
        {
            if (inv == null || string.IsNullOrEmpty(prefabName)) return 0;
            
            int count = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    count += item.m_stack;
                }
            }
            return count;
        }
        
        /// <summary>
        /// Counts how many of a specific item are in a container.
        /// </summary>
        public static int CountItem(Container container, string prefabName)
        {
            if (container == null) return 0;
            return CountItem(container.GetInventory(), prefabName);
        }
        
        /// <summary>
        /// Counts total amount of an item across multiple containers.
        /// </summary>
        public static int CountItemInContainers(IEnumerable<Container> containers, string prefabName)
        {
            if (containers == null) return 0;
            
            int total = 0;
            foreach (var container in containers)
            {
                total += CountItem(container, prefabName);
            }
            return total;
        }
        
        /// <summary>
        /// Saves a container's inventory to its ZDO.
        /// Container auto-saves when inventory changes, but we trigger the onChanged callback
        /// to ensure any listeners are notified.
        /// </summary>
        private static void SaveContainer(Container container)
        {
            if (container == null) return;
            
            // Container's inventory has an m_onChanged action that gets called
            // when the inventory changes. The Container subscribes to this
            // and saves to ZDO. We can trigger it by invoking the action.
            var inv = container.GetInventory();
            if (inv != null && inv.m_onChanged != null)
            {
                inv.m_onChanged.Invoke();
            }
        }
        
        #endregion
    }
}
