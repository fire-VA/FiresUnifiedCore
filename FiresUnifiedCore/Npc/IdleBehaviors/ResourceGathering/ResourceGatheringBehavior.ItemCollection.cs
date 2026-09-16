using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Item pickup, chest deposits, and inventory management.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        /// <summary>
        /// Handles interacting with Pickables (berries, mushrooms, etc.)
        /// </summary>
        private bool UpdateInteracting()
        {
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                _resourcesGathered++;
                TryFindNextResource();
                return false;
            }
            
            HolsterAllWeapons();
            
            if (_targetResource.Pickable != null)
            {
                var pickable = _targetResource.Pickable;
                
                if (!pickable.CanBePicked())
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} - Pickable not available, skipping (NOT attacking)");
                    TryFindNextResource();
                    return false;
                }
                
                FaceTarget(_targetResource.InteractionPosition);
                
                if (_zanim != null)
                {
                    _zanim.SetTrigger("interact");
                }
                
                bool success = pickable.Interact(_humanoid, false, false);
                
                if (success)
                {
                    _resourcesGathered++;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} picked {_targetResource.Name}");
                }
                
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            if (_targetResource.PickableItem != null)
            {
                var pickableItem = _targetResource.PickableItem;
                
                FaceTarget(_targetResource.InteractionPosition);
                
                if (_zanim != null)
                {
                    _zanim.SetTrigger("interact");
                }
                
                bool success = pickableItem.Interact(_humanoid, false, false);
                
                if (success)
                {
                    _resourcesGathered++;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} picked up {_targetResource.Name}");
                }
                
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} - target is not pickable in Interacting phase, skipping");
            TryFindNextResource();
            return false;
        }
        
        /// <summary>
        /// Waits briefly for drops to spawn and be picked up.
        /// Also checks if inventory needs to be offloaded.
        /// After processing tree parts, checks for remaining logs/stumps before moving on.
        /// </summary>
        private bool UpdateWaitingForDrops()
        {
            if (Time.time - _phaseStartTime < PostDestroyWait)
            {
                return false;
            }
            
            // CRITICAL: After processing tree parts, check for remaining logs/stumps
            // This ensures idle companions fully clear trees including stumps and plant saplings
            if (_wasTargetingTree && _lastTreePosition != Vector3.zero)
            {
                // First priority: Find any remaining logs from the felled tree
                var remainingLogs = FindAllNearbyLogs(_lastTreePosition);
                if (remainingLogs.Count > 0)
                {
                    ResourceDataHelper.ResourceData closestLog = null;
                    float closestDist = float.MaxValue;
                    foreach (var log in remainingLogs)
                    {
                        float dist = Vector3.Distance(Transform.position, log.InteractionPosition);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestLog = log;
                        }
                    }
                    
                    if (closestLog != null)
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} found remaining log after WaitingForDrops: {closestLog.Name}");
                        
                        _targetResource = closestLog;
                        _targetPosition = closestLog.InteractionPosition;
                        _consecutiveNoColliderHits = 0;
                        _aoeDamageAttempts = 0;
                        // Keep _wasTargetingTree = true so we continue checking
                        SetPhase(GatherPhase.MovingToResource);
                        MoveToPosition(_targetPosition);
                        return false;
                    }
                }
                
                // Second priority: Find stump to clear (and plant sapling)
                var stump = FindNearbyStump(_lastTreePosition, StumpSearchRadius);
                if (stump != null)
                {
                    var stumpData = ResourceDataHelper.GetResourceData(stump);
                    if (stumpData != null && stumpData.IsValid)
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} found stump to clear after WaitingForDrops: {stump.name}");
                        
                        _targetResource = stumpData;
                        _targetPosition = stumpData.InteractionPosition;
                        _consecutiveNoColliderHits = 0;
                        // Keep _wasTargetingTree = true so stump destruction triggers sapling planting
                        SetPhase(GatherPhase.MovingToResource);
                        MoveToPosition(_targetPosition);
                        return false;
                    }
                }
                
                // No more tree parts to process - clear the flag
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} finished processing tree - no more logs or stumps found");
                
                _wasTargetingTree = false;
                _lastTreePosition = Vector3.zero;
            }
            
            if (IsInventoryNearFull())
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} inventory near full, looking for chests to deposit");
                
                // Use ResourceAccessService to refresh chest cache
                if (_resources != null)
                {
                    _resources.RefreshNearbyChests(true);
                    _nearbyChests = new List<Container>(_resources.NearbyChests);
                }
                else
                {
                    _nearbyChests = ChestHelper.FindNearbyChests(Transform.position, CHEST_SEARCH_RADIUS);
                }
                
                if (_nearbyChests.Count > 0)
                {
                    _targetChest = null;
                    float closestDist = float.MaxValue;
                    foreach (var chest in _nearbyChests)
                    {
                        if (chest == null) continue;
                        float dist = Vector3.Distance(Transform.position, chest.transform.position);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            _targetChest = chest;
                        }
                    }
                    
                    if (_targetChest != null)
                    {
                        _chestPosition = _targetChest.transform.position;
                        SetPhase(GatherPhase.MovingToChest);
                        MoveToPosition(_chestPosition);
                        return false;
                    }
                }
                
                var owner = Companion.GetOwner();
                if (owner != null && owner == Player.m_localPlayer)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"{Companion.GetDisplayName()}'s inventory is full - no nearby chests found");
                }
            }
            
            TryFindNextResource();
            return false;
        }
        
        private bool UpdateMovingToChest()
        {
            if (_targetChest == null)
            {
                TryFindNextResource();
                return false;
            }
            
            float dist = Vector3.Distance(Transform.position, _chestPosition);
            
            if (dist < 2f)
            {
                StopMovement();
                SetPhase(GatherPhase.DepositingToChests);
                return false;
            }
            
            if (Time.time - _phaseStartTime > 20f)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} couldn't reach chest - continuing");
                TryFindNextResource();
            }
            
            return false;
        }
        
        private bool UpdateDepositingToChests()
        {
            StopMovement();
            
            if (_targetChest != null)
            {
                FaceTarget(_chestPosition);
            }
            
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
            
            DepositToChests();
            
            if (Time.time - _phaseStartTime > 1.5f)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} deposited items to chest, continuing gathering");
                
                TryFindNextResource();
            }
            
            return false;
        }
        
        private bool IsInventoryNearFull()
        {
            // Use ResourceAccessService if available
            if (_resources != null)
            {
                var status = _resources.GetInventoryStatus();
                return status.IsNearlyFull || status.IsFull || status.IsOverweight;
            }
            
            // Fallback to manual check
            if (_inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            float maxWeight = _inventory.GetMaxCarryWeight();
            float currentWeight = storageInv.GetTotalWeight();
            if (maxWeight > 0 && currentWeight / maxWeight >= InventoryFullThreshold)
            {
                return true;
            }
            
            int totalSlots = storageInv.GetWidth() * storageInv.GetHeight();
            int emptySlots = storageInv.GetEmptySlots();
            int usedSlots = totalSlots - emptySlots;
            if (totalSlots > 0 && (float)usedSlots / totalSlots >= InventoryFullThreshold)
            {
                return true;
            }
            
            return false;
        }
        
        private void DepositToChests()
        {
            if (_inventory == null || _nearbyChests.Count == 0) return;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;
            
            int totalDeposited = 0;
            
            var itemsToDeposit = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
            
            foreach (var item in itemsToDeposit)
            {
                if (item == null) continue;
                
                string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                
                bool isResource = prefabName.Contains("wood") || 
                                  prefabName.Contains("stone") || 
                                  prefabName.Contains("ore") ||
                                  prefabName.Contains("flint") ||
                                  prefabName.Contains("coal") ||
                                  prefabName.Contains("resin") ||
                                  prefabName.Contains("leather") ||
                                  prefabName.Contains("trophy") ||
                                  prefabName.Contains("feather") ||
                                  prefabName.Contains("bone") ||
                                  prefabName.Contains("guck") ||
                                  prefabName.Contains("surtling") ||
                                  prefabName.Contains("copper") ||
                                  prefabName.Contains("tin") ||
                                  prefabName.Contains("iron") ||
                                  prefabName.Contains("silver") ||
                                  prefabName.Contains("blackmetal") ||
                                  prefabName.Contains("flametal");
                
                if (isResource)
                {
                    int deposited = ChestHelper.DepositItemType(_nearbyChests, storageInv, prefabName);
                    totalDeposited += deposited;
                }
            }
            
            if (totalDeposited > 0)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} deposited {totalDeposited} items to chests");
                
                // Fire event for cross-cutting concerns
                CompanionEvents.FireItemDeposited(Companion, "resources", totalDeposited);
            }
            
            _inventory.SaveToZDO();
        }
        
        /// <summary>
        /// Holsters all weapons from hands to back slots.
        /// </summary>
        private void HolsterAllWeapons()
        {
            if (_inventory == null) return;
            
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && rightHand.IsWeapon())
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, rightHand);
                }
                else
                {
                    _inventory.GetStorageInventory()?.AddItem(rightHand);
                }
            }
            
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null)
            {
                var itemType = leftHand.m_shared?.m_itemType ?? ItemDrop.ItemData.ItemType.None;
                bool shouldHolster = itemType == ItemDrop.ItemData.ItemType.Bow ||
                                     itemType == ItemDrop.ItemData.ItemType.Shield ||
                                     itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                                     itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
                
                if (shouldHolster)
                {
                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                    var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                    if (leftBack == null)
                    {
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHand);
                    }
                    else
                    {
                        _inventory.GetStorageInventory()?.AddItem(leftHand);
                    }
                }
            }
            
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} holstered weapons for pickable interaction");
        }
    }
}
