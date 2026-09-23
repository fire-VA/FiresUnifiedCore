using UnityEngine;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Tool management, equipping, and validation.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        private const float ChestInteractionDistance = 1.5f;  // TIGHTENED: Force companion to walk to front of chest
        
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
            
            bool shouldLog = VerboseLogging || CompanionIdleBehavior.VerboseLogging;
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: searching for {_requiredToolType} tier>={_requiredToolTier}");
            
            var allChests = CollectToolSearchChests();
            
            if (allChests.Count == 0)
            {
                if (shouldLog)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: no chests found near any search position");
                return false;
            }
            
            if (shouldLog)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: found {allChests.Count} unique chests to search");
            
            ItemDrop.ItemData bestTool = null;
            Container bestChest = null;
            
            foreach (var chest in allChests)
            {
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                foreach (var item in chestInv.GetAllItems())
                {
                    if (!ResourceDataHelper.IsToolAppropriate(item, _requiredToolType, _requiredToolTier) ||
                        !ResourceDataHelper.IsBetterTool(item, bestTool, _requiredToolType))
                        continue;
                    
                    bestTool = item;
                    bestChest = chest;
                        
                    if (shouldLog)
                        Debug.Log($"[ResourceGathering]   Found candidate: {item.m_shared?.m_name} tier={item.m_shared.m_toolTier} in chest at {chest.transform.position}");
                }
            }
            
            if (bestTool != null && bestChest != null)
            {
                _toolToPull = bestTool;
                _toolChest = bestChest;
                
                if (shouldLog)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} TryFindToolInNearbyChests: FOUND {bestTool.m_shared?.m_name} (tier {bestTool.m_shared.m_toolTier}) in chest at {bestChest.transform.position}");
                
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
                _toolChest, Transform.position, ChestInteractionDistance);
            
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
            
            foreach (var chest in CollectToolSearchChests())
            {
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;

                foreach (var item in chestInv.GetAllItems())
                {
                    if (ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier))
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion?.companionName} found {item.m_shared?.m_name} in nearby chest for {resource.Name}");
                        return true;
                    }
                }
            }

            return false;
        }

        private static readonly CompanionInventory.EquipmentSlot[] ToolCarrySlots =
        {
            CompanionInventory.EquipmentSlot.RightHand,
            CompanionInventory.EquipmentSlot.RightBack,
            CompanionInventory.EquipmentSlot.LeftBack
        };

        /// <summary>
        /// Chests within the chest search radius of the companion, the gather target and (when staying) home: a tool in
        /// reach of any of those places can be fetched.
        /// </summary>
        private System.Collections.Generic.HashSet<Container> CollectToolSearchChests()
        {
            var searchPositions = new System.Collections.Generic.List<Vector3> { Transform.position };
            if (_targetPosition != Vector3.zero)
                searchPositions.Add(_targetPosition);
            if (IdleBehavior?.HasHomePosition == true)
                searchPositions.Add(IdleBehavior.HomePosition);
            
            var chests = new System.Collections.Generic.HashSet<Container>();
            foreach (var position in searchPositions)
            {
                var chestsNearPosition = ChestHelper.FindNearbyChests(position, CompanionSettings.ChestSearchRadius);
                if (chestsNearPosition == null) continue;
                foreach (var chest in chestsNearPosition)
                {
                    if (chest != null)
                        chests.Add(chest);
                }
            }
            return chests;
        }
            
        private const int NoObtainableTool = ResourceDataHelper.NoToolTier;

        /// <summary>The best axe tier the companion holds, carries, can fetch or can craft now; trees above it are skipped.</summary>
        private int ReachableAxeTier() => Mathf.Max(GetBestObtainableToolTier(ResourceDataHelper.ToolType.Axe),
            ResourceDataHelper.BestCraftableToolTier(CraftableAxes, ResourceDataHelper.ToolType.Axe, _inventory?.GetStorageInventory(),
                Transform.position, WorkbenchSearchRadiusRg));

        /// <summary>Highest m_toolTier of a tool for the job the companion holds, carries or can fetch from a chest.</summary>
        private int GetBestObtainableToolTier(ResourceDataHelper.ToolType tool)
        {
            int bestTier = NoObtainableTool;
            void Consider(ItemDrop.ItemData item)
            {
                if (ResourceDataHelper.IsToolAppropriate(item, tool, 0) && item.m_shared.m_toolTier > bestTier)
                    bestTier = item.m_shared.m_toolTier;
            }
                    
            if (_inventory != null)
            {
                foreach (var slot in ToolCarrySlots)
                    Consider(_inventory.GetEquippedItem(slot));
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                        Consider(item);
                }
            }

            foreach (var chest in CollectToolSearchChests())
            {
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                foreach (var item in chestInv.GetAllItems())
                    Consider(item);
            }
            
            return bestTier;
        }
        
        /// <summary>
        /// Gets any equipped weapon or tool.
        /// </summary>
        private ItemDrop.ItemData GetEquippedWeaponOrTool()
        {
            if (_inventory == null) return null;
            
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && ResourceDataHelper.IsWieldedWeaponOrTool(rightHand))
            {
                return rightHand;
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
                            
                            if (ResourceDataHelper.IsWieldedWeaponOrTool(itemData))
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
            CompanionInventory.EquipmentSlot? bestSlot = null;
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} searching for {resource.RequiredTool} tier {resource.MinToolTier}...");
            
            foreach (var slot in ToolCarrySlots)
            {
                var equipped = _inventory.GetEquippedItem(slot);
                if (equipped != null && VerboseLogging)
                    Debug.Log($"[ResourceGathering] {slot}: {equipped.m_shared?.m_name}, toolTier={equipped.m_shared?.m_toolTier}, appropriate={ResourceDataHelper.IsToolAppropriate(equipped, resource.RequiredTool, resource.MinToolTier)}");
            
                if (ResourceDataHelper.IsToolAppropriate(equipped, resource.RequiredTool, resource.MinToolTier) &&
                    ResourceDataHelper.IsBetterTool(equipped, bestTool, resource.RequiredTool))
                {
                    bestTool = equipped;
                    bestSlot = slot;
                }
            }
            
            var storage = _inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    bool isAppropriate = ResourceDataHelper.IsToolAppropriate(item, resource.RequiredTool, resource.MinToolTier);
                    if (VerboseLogging && item != null)
                        Debug.Log($"[ResourceGathering] Storage: {item.m_shared?.m_name ?? "null"}, toolTier={item.m_shared?.m_toolTier ?? 0}, appropriate={isAppropriate}");
                    
                    if (isAppropriate && ResourceDataHelper.IsBetterTool(item, bestTool, resource.RequiredTool))
                    {
                        bestTool = item;
                        bestSlot = null;
                    }
                }
            }
            
            if (bestTool == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} has no appropriate tool for {resource.Name} (needs {resource.RequiredTool} tier {resource.MinToolTier})");
                return;
            }
            
            HolsterLeftHandWeapon();

            if (bestSlot == CompanionInventory.EquipmentSlot.RightHand)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} already has {bestTool.m_shared.m_name} equipped");
                return;
            }
            
            if (!ResourceDataHelper.TryEquipInRightHand(_inventory, bestTool, bestSlot, holsterWeaponOnBack: true))
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} has no room to put away {_inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand)?.m_shared?.m_name} - keeping it equipped");
                return;
            }
            
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} equipped {bestTool.m_shared.m_name} (tier {bestTool.m_shared.m_toolTier}) from {(bestSlot.HasValue ? bestSlot.ToString() : "storage")}");
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
            
            if (ResourceDataHelper.TryStowEquipped(_inventory, CompanionInventory.EquipmentSlot.LeftHand,
                    CompanionInventory.EquipmentSlot.LeftBack, CompanionInventory.EquipmentSlot.RightBack))
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Holstered {leftHand.m_shared?.m_name}");
            }
            else
            {
                Debug.LogWarning($"[ResourceGathering] Could not holster left hand weapon - no space");
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
            
            foreach (var item in allItems)
            {
                if (item == null) continue;
                
                string itemName = item.m_shared?.m_name ?? "null";
                string prefabName = item.m_dropPrefab?.name ?? "null";
                int toolTier = item.m_shared?.m_toolTier ?? -1;
                bool isAppropriate = ResourceDataHelper.IsToolAppropriate(item, requiredTool, minTier);
                
                Debug.Log($"  - {itemName} (prefab:{prefabName}, tier:{toolTier}, appropriate:{isAppropriate})");
                
                if (isAppropriate && ResourceDataHelper.IsBetterTool(item, bestTool, requiredTool))
                {
                    bestTool = item;
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
            
            if (!ResourceDataHelper.TryEquipInRightHand(_inventory, bestTool, null, holsterWeaponOnBack: true))
            {
                Debug.Log($"[ResourceGathering] {Companion?.companionName} has no room to put away {_inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand)?.m_shared?.m_name} - keeping it equipped");
                return false;
            }
            
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            return true;
        }
        
        // -----------------------------------------------------------------------
        // Tool crafting helpers
        // -----------------------------------------------------------------------

        private static readonly string[] CraftablePickaxes = { "PickaxeAntler" };
        private static readonly string[] CraftableAxes = { "AxeStone", "AxeFlint" };
        private const float WorkbenchSearchRadiusRg = 30f;

        /// <summary>
        /// Picks a tool for the resource that the companion can craft from its vanilla recipe (cheapest first) and sets
        /// _craftRecipe and _craftWorkbench, the station that recipe needs (null when it needs none).
        /// </summary>
        private bool TryPlanToolCraft(ResourceDataHelper.ResourceData resource)
        {
            _craftRecipe = null;
            _craftWorkbench = null;
            if (resource == null) return false;

            string[] candidates;
            if (resource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe)
                candidates = CraftablePickaxes;
            else if (resource.RequiredTool == ResourceDataHelper.ToolType.Axe)
                candidates = CraftableAxes;
            else
                return false;

            _craftRecipe = ResourceDataHelper.FindCraftableTool(candidates, resource.RequiredTool, resource.MinToolTier,
                _inventory?.GetStorageInventory(), Transform.position, WorkbenchSearchRadiusRg, out _craftWorkbench);
            return _craftRecipe != null;
        }

        private bool CraftPlannedTool()
        {
            var storage = _inventory?.GetStorageInventory();
            if (_craftRecipe == null || storage == null || !ResourceDataHelper.CraftTool(_craftRecipe, _craftWorkbench, storage))
                return false;

            _inventory.SaveToZDO();
            Debug.Log($"[ResourceGathering] {Companion?.companionName} crafted {_craftRecipe.m_item.name}");
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
            
            ItemDrop.ItemData bestTool = null;
            Container bestChest = null;
            
            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;
                
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                foreach (var item in chestInv.GetAllItems())
                {
                    if (ResourceDataHelper.IsToolAppropriate(item, requiredTool, minTier) &&
                        ResourceDataHelper.IsBetterTool(item, bestTool, requiredTool))
                    {
                        bestTool = item;
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
