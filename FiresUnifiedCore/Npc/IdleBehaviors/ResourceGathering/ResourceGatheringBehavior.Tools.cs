using UnityEngine;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Tool management, equipping, and validation.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        private const float CHEST_INTERACTION_DISTANCE = 1.5f;  // TIGHTENED: Force companion to walk to front of chest
        
        /// <summary>
        /// Checks if companion has the appropriate tool equipped, in their own inventory, OR in nearby chests.
        /// This method now checks ALL sources since we can pull tools from chests.
        /// </summary>
        private bool HasToolEquippedOrInInventory(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null || !resource.RequiresCombat) return true;
            if (resource.RequiredTool == ResourceDataHelper.ToolType.None) return true;
            
            // Only log verbosely to reduce spam
            bool shouldLog = VerboseLogging || CompanionIdleBehavior.VerboseLogging;
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolEquippedOrInInventory: checking for {resource.RequiredTool} tier>={resource.MinToolTier} to gather {resource.Name}");
            
            // Check currently equipped weapon
            var weapon = GetEquippedWeaponOrTool();
            if (weapon != null)
            {
                bool isAppropriate = ResourceDataHelper.IsToolAppropriate(weapon, resource.RequiredTool, resource.MinToolTier);
                
                if (shouldLog)
                {
                    string weaponPrefab = weapon.m_dropPrefab?.name ?? "null";
                    Debug.Log($"[ResourceGathering]   Equipped weapon: prefab={weaponPrefab}, appropriate={isAppropriate}");
                }
                
                if (isAppropriate)
                {
                    if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND appropriate tool equipped");
                    return true;
                }
            }
            
            // Check equipment slots
            if (_inventory != null)
            {
                var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                if (rightHand != null && ResourceDataHelper.IsToolAppropriate(rightHand, resource.RequiredTool, resource.MinToolTier))
                {
                    if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND in RightHand slot");
                    return true;
                }
                
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack != null && ResourceDataHelper.IsToolAppropriate(rightBack, resource.RequiredTool, resource.MinToolTier))
                {
                    if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND in RightBack slot");
                    return true;
                }
                
                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (leftBack != null && ResourceDataHelper.IsToolAppropriate(leftBack, resource.RequiredTool, resource.MinToolTier))
                {
                    if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND in LeftBack slot");
                    return true;
                }
                
                // Check storage inventory
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                    {
                        if (ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier))
                        {
                            if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND in storage: {item.m_dropPrefab?.name}");
                            return true;
                        }
                    }
                }
            }
            
            // CRITICAL FIX: Also check nearby chests - we can pull tools from there!
            // This allows the behavior to start if there's a tool available ANYWHERE
            if (TryFindToolInNearbyChests(resource))
            {
                if (shouldLog) Debug.Log($"[ResourceGathering]   FOUND tool in nearby chest - will pull before gathering");
                return true;
            }
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering]   NO appropriate tool found in inventory/equipped/chests");
            
            return false;
        }
        
        /// <summary>
        /// Finds a tool in nearby chests and sets up _toolChest and _toolToPull.
        /// Returns true if a suitable tool was found.
        /// 
        /// CRITICAL: For commanded operations, we search from MULTIPLE positions:
        /// 1. Companion's current position (in case they're near chests)
        /// 2. Target resource position (in case chests are near the resource)
        /// 3. Home position (if staying, in case chests are at base)
        /// This ensures we find tools within 50m of ANY relevant location.
        /// </summary>
        private bool TryFindToolInNearbyChests(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null) return false;
            
            _requiredToolType = resource.RequiredTool;
            _requiredToolTier = resource.MinToolTier;
            
            float searchRadius = CompanionSettings.ChestSearchRadius; // 50m by default
            bool shouldLog = VerboseLogging || CompanionIdleBehavior.VerboseLogging;
            
            // Collect all search positions
            var searchPositions = new System.Collections.Generic.List<Vector3>();
            
            // 1. Always search from companion's current position
            searchPositions.Add(Transform.position);
            
            // 2. If we have a target position (commanded to gather), search from there too
            if (_targetPosition != Vector3.zero)
            {
                searchPositions.Add(_targetPosition);
            }
            
            // 3. If staying (has home), search from home position
            if (IdleBehavior?.HasHomePosition == true)
            {
                searchPositions.Add(IdleBehavior.HomePosition);
            }
            
            if (shouldLog)
            {
                Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: searching for {_requiredToolType} tier>={_requiredToolTier}");
                Debug.Log($"[ResourceGathering]   Search positions: {searchPositions.Count} locations, radius={searchRadius}m");
                for (int i = 0; i < searchPositions.Count; i++)
                    Debug.Log($"[ResourceGathering]   Position {i}: {searchPositions[i]}");
            }
            
            // Collect all unique chests from all search positions
            var allChests = new System.Collections.Generic.HashSet<Container>();
            foreach (var pos in searchPositions)
            {
                var chestsNearPos = ChestHelper.FindNearbyChests(pos, searchRadius);
                if (chestsNearPos != null)
                {
                    foreach (var chest in chestsNearPos)
                    {
                        if (chest != null)
                            allChests.Add(chest);
                    }
                }
            }
            
            if (allChests.Count == 0)
            {
                if (shouldLog)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: no chests found near any search position");
                return false;
            }
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: found {allChests.Count} unique chests to search");
            
            ItemDrop.ItemData bestTool = null;
            int bestTier = _requiredToolTier - 1;
            Container bestChest = null;
            
            foreach (var chest in allChests)
            {
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                foreach (var item in chestInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    if (!ResourceDataHelper.IsToolAppropriate(item, _requiredToolType, _requiredToolTier))
                        continue;
                    
                    int toolTier = item.m_shared?.m_toolTier ?? 0;
                    if (toolTier > bestTier)
                    {
                        bestTool = item;
                        bestTier = toolTier;
                        bestChest = chest;
                        
                        if (shouldLog)
                            Debug.Log($"[ResourceGathering]   Found candidate: {item.m_shared?.m_name} tier={toolTier} in chest at {chest.transform.position}");
                    }
                }
            }
            
            if (bestTool != null && bestChest != null)
            {
                _toolToPull = bestTool;
                _toolChest = bestChest;
                
                if (shouldLog)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: FOUND {bestTool.m_shared?.m_name} (tier {bestTier}) in chest at {bestChest.transform.position}");
                
                return true;
            }
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: no suitable {_requiredToolType} found in any chest");
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Moving to chest to retrieve required tool.
        /// </summary>
        private bool UpdateMovingToChestForTool()
        {
            if (_toolChest == null || _toolToPull == null)
            {
                // Chest or tool disappeared - abort
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} tool chest or tool no longer valid");
                SetPhase(GatherPhase.Complete);
                return true;
            }
            
            // Calculate proper interaction point in front of chest
            Vector3 interactionPoint = Core.InteractionPointHelper.GetContainerInteractionPoint(
                _toolChest, Transform.position, CHEST_INTERACTION_DISTANCE);
            
            float dist = Vector3.Distance(Transform.position, interactionPoint);
            
            if (dist <= Core.InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                StopMovement();
                SetPhase(GatherPhase.RetrievingToolFromChest);
                return false;
            }
            
            // Continue moving to chest interaction point
            MoveToPosition(interactionPoint);
            
            // Timeout check
            if (Time.time - _phaseStartTime > 30f)
            {
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} timeout walking to tool chest - trying instant pull");
                
                // Try instant pull as fallback
                if (TryInstantPullToolFromChest())
                {
                    EquipBestToolForResource(_targetResource);
                    SetPhase(GatherPhase.MovingToResource);
                    MoveToPosition(_targetPosition);
                    CompanionChatHelper.QuickMessages.GatheringResources(Companion, _targetResource.Name);
                }
                else
                {
                    CompanionChatHelper.QuickMessages.CantDoTask(Companion, $"I couldn't get the tool from the chest");
                    SetPhase(GatherPhase.Complete);
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Interacting with chest to retrieve tool.
        /// </summary>
        private bool UpdateRetrievingToolFromChest()
        {
            if (_toolChest == null)
            {
                SetPhase(GatherPhase.Complete);
                return true;
            }
            
            StopMovement();
            FaceTarget(_toolChest.transform.position);
            
            // Play interact animation at start
            if (Time.time - _phaseStartTime < 0.1f)
            {
                PlayInteractAnimation();
            }
            
            // Wait briefly for animation
            if (Time.time - _phaseStartTime < 0.5f)
            {
                return false;
            }
            
            // Pull the tool from chest
            bool success = TryInstantPullToolFromChest();
            
            if (success)
            {
                // Equip the tool
                EquipBestToolForResource(_targetResource);
                
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} retrieved and equipped tool, now heading to resource");
                
                // Now head to the resource
                SetPhase(GatherPhase.MovingToResource);
                MoveToPosition(_targetPosition);
                CompanionChatHelper.QuickMessages.GatheringResources(Companion, _targetResource?.Name ?? "resource");
            }
            else
            {
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} failed to retrieve tool from chest");
                CompanionChatHelper.QuickMessages.CantDoTask(Companion, $"I need a {_requiredToolType.ToString().ToLower()}");
                SetPhase(GatherPhase.Complete);
            }
            
            // Clear tool retrieval state
            _toolChest = null;
            _toolToPull = null;
            
            return false;
        }
        
        /// <summary>
        /// Instantly pulls the tool from chest (used after walking to chest, or as timeout fallback).
        /// </summary>
        private bool TryInstantPullToolFromChest()
        {
            if (_toolChest == null || _toolToPull == null || _inventory == null) return false;
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;
            
            var chestInv = _toolChest.GetInventory();
            if (chestInv == null) return false;
            
            // Verify tool is still in chest
            bool found = false;
            foreach (var item in chestInv.GetAllItems())
            {
                if (item == _toolToPull)
                {
                    found = true;
                    break;
                }
            }
            
            if (!found)
            {
                // Tool was removed from chest - try to find another
                if (TryFindToolInNearbyChests(_targetResource))
                {
                    // Re-verify we're at the right chest
                    if (_toolChest != null)
                    {
                        chestInv = _toolChest.GetInventory();
                        if (chestInv == null) return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            
            // Clone and transfer
            var toolClone = _toolToPull.Clone();
            chestInv.RemoveOneItem(_toolToPull);
            
            if (storage.AddItem(toolClone))
            {
                _inventory.SaveToZDO();
                
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} pulled {toolClone.m_shared?.m_name} from chest");
                
                // Fire event
                CompanionEvents.FireItemPulled(Companion, toolClone.m_dropPrefab?.name ?? "tool", 1);
                
                return true;
            }
            else
            {
                // Failed to add - return to chest
                chestInv.AddItem(_toolToPull);
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} couldn't add tool to storage - returned to chest");
                return false;
            }
        }
        
        /// <summary>
        /// Plays interact animation (used for chest opening).
        /// </summary>
        private void PlayInteractAnimation()
        {
            if (_animator != null)
            {
                _animator.SetTrigger("interact");
            }
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        /// <summary>
        /// Checks if companion has the right tool for a resource.
        /// Checks BOTH currently equipped AND storage inventory.
        /// </summary>
        private bool HasToolForResource(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null || !resource.RequiresCombat) return true;
            if (resource.RequiredTool == ResourceDataHelper.ToolType.None) return true;
            
            var weapon = GetEquippedWeaponOrTool();
            if (ResourceDataHelper.IsToolAppropriate(weapon, resource.RequiredTool, resource.MinToolTier))
            {
                return true;
            }
            
            if (_inventory != null)
            {
                var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                if (ResourceDataHelper.IsToolAppropriate(rightHand, resource.RequiredTool, resource.MinToolTier))
                    return true;
                
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (ResourceDataHelper.IsToolAppropriate(rightBack, resource.RequiredTool, resource.MinToolTier))
                    return true;
                
                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (ResourceDataHelper.IsToolAppropriate(leftBack, resource.RequiredTool, resource.MinToolTier))
                    return true;
                
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                    {
                        if (ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} has {item.m_shared?.m_name} in storage for {resource.Name}");
                            return true;
                        }
                    }
                }
            }
            
            // CRITICAL FIX: Search from multiple positions - companion, target resource, and home
            // This ensures we find tools within 50m of ANY relevant location
            var searchPositions = new System.Collections.Generic.List<Vector3>();
            searchPositions.Add(Transform.position);
            if (_targetPosition != Vector3.zero)
                searchPositions.Add(_targetPosition);
            if (IdleBehavior?.HasHomePosition == true)
                searchPositions.Add(IdleBehavior.HomePosition);
            
            // Collect all unique chests from all search positions
            var allChests = new System.Collections.Generic.HashSet<Container>();
            foreach (var pos in searchPositions)
            {
                var chestsNearPos = ChestHelper.FindNearbyChests(pos, CompanionSettings.ChestSearchRadius);
                if (chestsNearPos != null)
                {
                    foreach (var chest in chestsNearPos)
                    {
                        if (chest != null)
                            allChests.Add(chest);
                    }
                }
            }
            
            if (allChests.Count > 0)
            {
                string searchPattern = resource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe ? "pickaxe" : "axe";
                
                foreach (var chest in allChests)
                {
                    if (chest == null) continue;
                    var chestInv = chest.GetInventory();
                    if (chestInv == null) continue;
                    
                    foreach (var item in chestInv.GetAllItems())
                    {
                        if (item == null) continue;
                        if (ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} found {item.m_shared?.m_name} in nearby chest for {resource.Name}");
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets any equipped weapon or tool.
        /// </summary>
        private ItemDrop.ItemData GetEquippedWeaponOrTool()
        {
            if (_inventory == null) return null;
            
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null)
            {
                var itemType = rightHand.m_shared.m_itemType;
                if (itemType == ItemDrop.ItemData.ItemType.Tool ||
                    itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                    itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                    itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
                {
                    return rightHand;
                }
            }
            
            string prefabName = _inventory.GetEquipmentPrefabNamePublic(CompanionInventory.EquipmentSlot.RightHand);
            if (!string.IsNullOrEmpty(prefabName) && rightHand == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] RightHand has prefab '{prefabName}' but no ItemData - attempting to reload");
                
                if (ObjectDB.instance != null)
                {
                    var itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
                    if (itemPrefab != null)
                    {
                        var itemDrop = itemPrefab.GetComponent<ItemDrop>();
                        if (itemDrop?.m_itemData != null)
                        {
                            var itemData = itemDrop.m_itemData.Clone();
                            itemData.m_quality = _inventory.GetEquipmentQualityPublic(CompanionInventory.EquipmentSlot.RightHand);
                            
                            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, itemData);
                            
                            var itemType = itemData.m_shared.m_itemType;
                            if (itemType == ItemDrop.ItemData.ItemType.Tool ||
                                itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
                            {
                                if (VerboseLogging)
                                    Debug.Log($"[ResourceGathering] Reloaded {prefabName} into RightHand");
                                return itemData;
                            }
                        }
                    }
                }
            }
            
            if (VerboseLogging && rightHand != null)
            {
                Debug.Log($"[ResourceGathering] RightHand has {rightHand.m_shared?.m_name} (type: {rightHand.m_shared?.m_itemType}) - not a valid tool/weapon");
            }
            
            return null;
        }
        
        private void EquipBestToolForResource(ResourceDataHelper.ResourceData resource)
        {
            if (_inventory == null || resource == null) return;
            
            ItemDrop.ItemData bestTool = null;
            int bestTier = -1;
            CompanionInventory.EquipmentSlot? bestSlot = null;
            bool bestIsInStorage = false;
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} searching for {resource.RequiredTool} tier {resource.MinToolTier}...");
            
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && VerboseLogging)
                Debug.Log($"[ResourceGathering] RightHand: {rightHand.m_shared?.m_name}, toolTier={rightHand.m_shared?.m_toolTier}, appropriate={ResourceDataHelper.IsToolAppropriate(rightHand, resource.RequiredTool, resource.MinToolTier)}");
            
            if (ResourceDataHelper.IsToolAppropriate(rightHand, resource.RequiredTool, resource.MinToolTier))
            {
                if (rightHand.m_shared.m_toolTier > bestTier)
                {
                    bestTool = rightHand;
                    bestTier = rightHand.m_shared.m_toolTier;
                    bestSlot = CompanionInventory.EquipmentSlot.RightHand;
                    bestIsInStorage = false;
                }
            }
            
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (rightBack != null && VerboseLogging)
                Debug.Log($"[ResourceGathering] RightBack: {rightBack.m_shared?.m_name}, toolTier={rightBack.m_shared?.m_toolTier}, appropriate={ResourceDataHelper.IsToolAppropriate(rightBack, resource.RequiredTool, resource.MinToolTier)}");
            
            if (ResourceDataHelper.IsToolAppropriate(rightBack, resource.RequiredTool, resource.MinToolTier))
            {
                if (rightBack.m_shared.m_toolTier > bestTier)
                {
                    bestTool = rightBack;
                    bestTier = rightBack.m_shared.m_toolTier;
                    bestSlot = CompanionInventory.EquipmentSlot.RightBack;
                    bestIsInStorage = false;
                }
            }
            
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (leftBack != null && VerboseLogging)
                Debug.Log($"[ResourceGathering] LeftBack: {leftBack.m_shared?.m_name}, toolTier={leftBack.m_shared?.m_toolTier}, appropriate={ResourceDataHelper.IsToolAppropriate(leftBack, resource.RequiredTool, resource.MinToolTier)}");
            
            if (ResourceDataHelper.IsToolAppropriate(leftBack, resource.RequiredTool, resource.MinToolTier))
            {
                if (leftBack.m_shared.m_toolTier > bestTier)
                {
                    bestTool = leftBack;
                    bestTier = leftBack.m_shared.m_toolTier;
                    bestSlot = CompanionInventory.EquipmentSlot.LeftBack;
                    bestIsInStorage = false;
                }
            }
            
            var storage = _inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (VerboseLogging && item != null)
                    {
                        string itemName = item.m_shared?.m_name ?? "null";
                        int toolTier = item.m_shared?.m_toolTier ?? 0;
                        bool isAppropriate = ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier);
                        Debug.Log($"[ResourceGathering] Storage: {itemName}, toolTier={toolTier}, appropriate={isAppropriate}");
                    }
                    
                    if (ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier))
                    {
                        if (item.m_shared.m_toolTier > bestTier)
                        {
                            bestTool = item;
                            bestTier = item.m_shared.m_toolTier;
                            bestSlot = null;
                            bestIsInStorage = true;
                        }
                    }
                }
            }
            
            if (bestTool == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} has no appropriate tool for {resource.Name} (needs {resource.RequiredTool} tier {resource.MinToolTier})");
                return;
            }
            
            if (bestSlot == CompanionInventory.EquipmentSlot.RightHand)
            {
                HolsterLeftHandWeapon();
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} already has {bestTool.m_shared.m_name} equipped");
                return;
            }
            
            HolsterLeftHandWeapon();
            
            var currentRightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (currentRightHand != null && currentRightHand.IsWeapon())
            {
                var currentRightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (currentRightBack == null)
                {
                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, currentRightHand);
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] Moved {currentRightHand.m_shared?.m_name} to RightBack");
                }
                else
                {
                    _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                    storage?.AddItem(currentRightHand);
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] Moved {currentRightHand.m_shared?.m_name} to storage");
                }
            }
            else if (currentRightHand != null)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
            }
            
            if (bestIsInStorage)
            {
                storage?.RemoveItem(bestTool);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, bestTool);
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Equipped {bestTool.m_shared?.m_name} from storage");
            }
            else if (bestSlot == CompanionInventory.EquipmentSlot.RightBack)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, bestTool);
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Equipped {bestTool.m_shared?.m_name} from RightBack");
            }
            else if (bestSlot == CompanionInventory.EquipmentSlot.LeftBack)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftBack);
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, bestTool);
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Equipped {bestTool.m_shared?.m_name} from LeftBack");
            }
            
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} equipped {bestTool.m_shared.m_name} (tier {bestTier}) from {(bestIsInStorage ? "storage" : bestSlot.ToString())}");
        }
        
        private void HolsterLeftHandWeapon()
        {
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand == null)
            {
                string prefabName = _inventory.GetEquipmentPrefabNamePublic(CompanionInventory.EquipmentSlot.LeftHand);
                if (!string.IsNullOrEmpty(prefabName))
                {
                    if (ObjectDB.instance != null)
                    {
                        var itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
                        if (itemPrefab != null)
                        {
                            var itemDrop = itemPrefab.GetComponent<ItemDrop>();
                            if (itemDrop?.m_itemData != null)
                            {
                                leftHand = itemDrop.m_itemData.Clone();
                                leftHand.m_quality = _inventory.GetEquipmentQualityPublic(CompanionInventory.EquipmentSlot.LeftHand);
                                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, leftHand);
                                if (VerboseLogging)
                                    Debug.Log($"[ResourceGathering] Reloaded {prefabName} into LeftHand for holstering");
                            }
                        }
                    }
                }
                
                if (leftHand == null) return;
            }
            
            var itemType = leftHand.m_shared.m_itemType;
            bool shouldHolster = itemType == ItemDrop.ItemData.ItemType.Bow ||
                                 itemType == ItemDrop.ItemData.ItemType.Shield ||
                                 itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                                 itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
            
            if (!shouldHolster) return;
            
            _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
            
            var currentLeftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (currentLeftBack == null)
            {
                _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHand);
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Holstered {leftHand.m_shared?.m_name} to LeftBack");
            }
            else
            {
                var currentRightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (currentRightBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, leftHand);
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] Holstered {leftHand.m_shared?.m_name} to RightBack");
                }
                else
                {
                    var storage = _inventory.GetStorageInventory();
                    if (storage != null && storage.AddItem(leftHand))
                    {
                        if (VerboseLogging)
                            Debug.Log($"[ResourceGathering] Moved {leftHand.m_shared?.m_name} to storage");
                    }
                    else
                    {
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, leftHand);
                        Debug.LogWarning($"[ResourceGathering] Could not holster left hand weapon - no space");
                    }
                }
            }
            
            _inventory.ApplyVisualEquipment();
        }
        
        private bool TryEquipToolFromStorage(ResourceDataHelper.ToolType requiredTool, int minTier)
        {
            if (_inventory == null)
            {
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} TryEquipToolFromStorage: _inventory is null!");
                return false;
            }
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null)
            {
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} TryEquipToolFromStorage: storage inventory is null!");
                return false;
            }
            
            var allItems = storage.GetAllItems();
            Debug.Log($"[ResourceGathering] {Companion?.companionName} searching storage for {requiredTool} tier>={minTier}. Storage has {allItems.Count} items:");
            
            ItemDrop.ItemData bestTool = null;
            int bestTier = minTier - 1;
            
            foreach (var item in allItems)
            {
                if (item == null) continue;
                
                string itemName = item.m_shared?.m_name ?? "null";
                string prefabName = item.m_dropPrefab?.name ?? "null";
                int toolTier = item.m_shared?.m_toolTier ?? -1;
                bool isAppropriate = ResourceDataHelper.IsToolAppropriate(item, requiredTool, minTier);
                
                Debug.Log($"  - {itemName} (prefab:{prefabName}, tier:{toolTier}, appropriate:{isAppropriate})");
                
                if (isAppropriate)
                {
                    if (toolTier > bestTier)
                    {
                        bestTool = item;
                        bestTier = toolTier;
                    }
                }
            }
            
            if (bestTool == null)
            {
                Debug.Log($"[ResourceGathering] {Companion?.companionName} no appropriate tool found in storage for {requiredTool} tier>={minTier}");
                
                bestTool = TryGetToolFromNearbyChests(requiredTool, minTier, storage);
                
                if (bestTool == null)
                {
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} no tool found in nearby chests either");
                    return false;
                }
                
                Debug.Log($"[ResourceGathering] {Companion?.companionName} found {bestTool.m_shared?.m_name} in nearby chest!");
            }
            
            var currentRightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (currentRightHand != null)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack == null && currentRightHand.IsWeapon())
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, currentRightHand);
                }
                else
                {
                    storage.AddItem(currentRightHand);
                }
            }
            
            storage.RemoveItem(bestTool);
            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, bestTool);
            
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            return true;
        }
        
        // -----------------------------------------------------------------------
        // Tool crafting helpers
        // -----------------------------------------------------------------------

        private const string PICKAXE_PREFAB_ANTLER = "PickaxeAntler";
        private const string AXE_PREFAB_STONE = "AxeStone";
        private const string AXE_PREFAB_FLINT = "AxeFlint";
        private const float WORKBENCH_SEARCH_RADIUS_RG = 30f;

        /// <summary>Returns true if we have materials to craft any tool that would work for this resource.</summary>
        private bool CanCraftToolForResource(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null) return false;
            if (resource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe)
                return CanCraftPickaxe();
            if (resource.RequiredTool == ResourceDataHelper.ToolType.Axe)
                return CanCraftAxe();
            return false;
        }

        private bool CanCraftPickaxe()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            // Antler Pickaxe: 10 Wood + 1 HardAntler
            return CountItemsByPrefab(storage, "Wood") >= 10 &&
                   CountItemsByPrefab(storage, "HardAntler") >= 1;
        }

        private bool CanCraftAxe()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            // Stone Axe: 4 Stone + 3 Wood (simplest recipe, no workbench required)
            if (CountItemsByPrefab(storage, "Stone") >= 4 &&
                CountItemsByPrefab(storage, "Wood") >= 3)
                return true;
            // Flint Axe: 6 Flint + 4 Wood + 2 LeatherScraps (workbench level 1)
            if (CountItemsByPrefab(storage, "Flint") >= 6 &&
                CountItemsByPrefab(storage, "Wood") >= 4 &&
                CountItemsByPrefab(storage, "LeatherScraps") >= 2)
                return true;
            return false;
        }

        /// <summary>Tries to craft the most appropriate tool for the given resource.</summary>
        private bool TryCraftToolForResource(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null) return false;
            if (resource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe)
                return TryCraftPickaxe();
            if (resource.RequiredTool == ResourceDataHelper.ToolType.Axe)
                return TryCraftAxe();
            return false;
        }

        private bool TryCraftPickaxe()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;

            if (CountItemsByPrefab(storage, "Wood") >= 10 &&
                CountItemsByPrefab(storage, "HardAntler") >= 1)
            {
                ConsumeItems(storage, "Wood", 10);
                ConsumeItems(storage, "HardAntler", 1);
                return AddCraftedItem(storage, PICKAXE_PREFAB_ANTLER);
            }
            return false;
        }

        private bool TryCraftAxe()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;

            // Try Stone Axe first (cheapest recipe)
            if (CountItemsByPrefab(storage, "Stone") >= 4 &&
                CountItemsByPrefab(storage, "Wood") >= 3)
            {
                ConsumeItems(storage, "Stone", 4);
                ConsumeItems(storage, "Wood", 3);
                return AddCraftedItem(storage, AXE_PREFAB_STONE);
            }
            // Fallback: Flint Axe
            if (CountItemsByPrefab(storage, "Flint") >= 6 &&
                CountItemsByPrefab(storage, "Wood") >= 4 &&
                CountItemsByPrefab(storage, "LeatherScraps") >= 2)
            {
                ConsumeItems(storage, "Flint", 6);
                ConsumeItems(storage, "Wood", 4);
                ConsumeItems(storage, "LeatherScraps", 2);
                return AddCraftedItem(storage, AXE_PREFAB_FLINT);
            }
            return false;
        }

        private CraftingStation FindNearestWorkbench()
        {
            var colliders = Physics.OverlapSphere(Transform.position, WORKBENCH_SEARCH_RADIUS_RG);
            CraftingStation nearest = null;
            float nearestDist = float.MaxValue;
            var processed = new System.Collections.Generic.HashSet<CraftingStation>();

            foreach (var col in colliders)
            {
                if (col == null) continue;
                var station = col.GetComponent<CraftingStation>() ?? col.GetComponentInParent<CraftingStation>();
                if (station == null || processed.Contains(station)) continue;
                processed.Add(station);

                string stName = (station.m_name ?? "").ToLowerInvariant();
                string goName = (station.gameObject.name ?? "").ToLowerInvariant();
                if (!stName.Contains("workbench") && !goName.Contains("workbench")) continue;

                float dist = Vector3.Distance(Transform.position, station.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = station;
                }
            }

            return nearest;
        }

        private int CountItemsByPrefab(Inventory inv, string prefabName)
        {
            int count = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab?.name == prefabName)
                    count += item.m_stack;
            }
            return count;
        }

        private void ConsumeItems(Inventory inv, string prefabName, int amount)
        {
            int remaining = amount;
            var items = new System.Collections.Generic.List<ItemDrop.ItemData>(inv.GetAllItems());
            foreach (var item in items)
            {
                if (remaining <= 0) break;
                if (item?.m_dropPrefab?.name != prefabName) continue;
                int toRemove = Mathf.Min(remaining, item.m_stack);
                item.m_stack -= toRemove;
                remaining -= toRemove;
                if (item.m_stack <= 0)
                    inv.RemoveItem(item);
            }
        }

        private bool AddCraftedItem(Inventory inv, string prefabName)
        {
            if (ZNetScene.instance == null) return false;
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] crafted item prefab not found: {prefabName}");
                return false;
            }
            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null) return false;

            var newItem = itemDrop.m_itemData.Clone();
            newItem.m_stack = 1;
            if (!inv.AddItem(newItem))
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] no inventory space for crafted {prefabName}");
                return false;
            }

            _inventory?.SaveToZDO();
            Debug.Log($"[ResourceGathering] {Companion?.companionName} crafted {prefabName}");
            return true;
        }

        private ItemDrop.ItemData TryGetToolFromNearbyChests(ResourceDataHelper.ToolType requiredTool, int minTier, Inventory storage)
        {
            if (storage == null) return null;
            
            var position = IdleBehavior?.HomePosition ?? Transform.position;
            float searchRadius = CompanionSettings.ChestSearchRadius;
            
            var nearbyChests = ChestHelper.FindNearbyChests(position, searchRadius);
            if (nearbyChests == null || nearbyChests.Count == 0)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} no chests found within {searchRadius}m");
                return null;
            }
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} searching {nearbyChests.Count} nearby chests for {requiredTool}");
            
            string searchPattern = requiredTool == ResourceDataHelper.ToolType.Pickaxe ? "pickaxe" : "axe";
            
            ItemDrop.ItemData bestTool = null;
            int bestTier = minTier - 1;
            Container bestChest = null;
            
            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;
                
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                foreach (var item in chestInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    if (!ResourceDataHelper.IsToolAppropriate(item, requiredTool, minTier))
                        continue;
                    
                    int toolTier = item.m_shared?.m_toolTier ?? 0;
                    if (toolTier > bestTier)
                    {
                        bestTool = item;
                        bestTier = toolTier;
                        bestChest = chest;
                    }
                }
            }
            
            if (bestTool == null || bestChest == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} no {requiredTool} found in any nearby chest");
                return null;
            }
            
            var chestInventory = bestChest.GetInventory();
            if (chestInventory == null) return null;
            
            var toolClone = bestTool.Clone();
            
            chestInventory.RemoveOneItem(bestTool);
            
            if (storage.AddItem(toolClone))
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} pulled {toolClone.m_shared?.m_name} from chest");
                
                return toolClone;
            }
            else
            {
                chestInventory.AddItem(bestTool);
                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} failed to add tool to storage, returned to chest");
                return null;
            }
        }
    }
}
