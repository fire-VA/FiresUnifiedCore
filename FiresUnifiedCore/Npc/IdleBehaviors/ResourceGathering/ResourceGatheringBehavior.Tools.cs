using UnityEngine;
using FiresCore.Npc.Events;
using System.Collections.Generic;
using System.Linq;

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

        // 0.2.274 (Fire: "put enough resources in the yard for them to create themselves. A pickaxe for the mud pile"): the ladders
        // by tier, cheapest first; the tiers are the runtime prefabs'. Both the bag-only craft and the base-stock craft below use them.
        private static readonly string[] CraftablePickaxes = { "PickaxeAntler", "PickaxeBronze", "PickaxeIron", "PickaxeBlackMetal" };
        private static readonly string[] CraftableAxes = { "AxeStone", "AxeFlint", "AxeBronze", "AxeIron", "AxeBlackMetal" };
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

        // ── 0.2.274: a tool crafted from base stock ──────────────────────────────────────────────────────────────────────────────
        // Paid from the bag plus the base chests the owner may use, through ChoreBrain.CraftFromStock (the bot's decision: the whole cost
        // or nothing fetched; station kind, level, roof and fire checked). At most one intermediate: when the tool is short of exactly
        // one item that has its own recipe (Bronze <- Copper + Tin at the forge) and stock covers k crafts of it plus the rest of the
        // tool, the k come first. Each step takes what its plan lists from each chest (FetchingCraftStock), walks to its station and
        // crafts (MovingToWorkbench / CraftingTool), then the next step is planned on the bag as it now is. No item is made from nothing.
        private const float StockPlanCacheSeconds = 30f, StockFetchTimeout = 30f;

        private sealed class StockToolPlan
        {
            internal string Tool;
            internal string Intermediate;        // crafted first, or null
            internal int IntermediateCrafts;
            internal string Summary;             // for the line
        }

        private readonly Dictionary<long, (float At, StockToolPlan Plan, string Why)> _stockPlanCache = new Dictionary<long, (float, StockToolPlan, string)>();
        private StockToolPlan _stockPlan;
        private int _stockIntermediateDone;
        private AI.ChoreBrain.StockCraft _stockStep;
        private int _stockFetchIndex;
        private string _stockFor;

        private Vector3 StockBase => IdleBehavior?.HasHomePosition == true ? IdleBehavior.HomePosition : Transform.position;
        private float StockRadius => Mathf.Max(CompanionSettings.ChestSearchRadius, WorkbenchSearchRadiusRg);

        private System.Func<Container, bool> StockMayUse
        {
            get
            {
                long owner = ChestHelper.ChestOwnerIdFor(Companion);
                return chest => ChestHelper.OwnerMayWrite(chest, owner);
            }
        }

        private bool CanCraftToolFromStock(ResourceDataHelper.ToolType tool, int minTier) => PlanStockTool(tool, minTier, out _) != null;

        private string StockCraftWhy(ResourceDataHelper.ToolType tool, int minTier)
        {
            PlanStockTool(tool, minTier, out string why);
            return why;
        }

        // The cheapest tool on the ladder that suits (tool, minTier) and stock can pay for, directly or with one intermediate; cached.
        private StockToolPlan PlanStockTool(ResourceDataHelper.ToolType tool, int minTier, out string why)
        {
            long key = ((long)tool << 32) | (uint)minTier;
            if (_stockPlanCache.TryGetValue(key, out var cached) && Time.time - cached.At < StockPlanCacheSeconds)
            {
                why = cached.Why;
                return cached.Plan;
            }
            StockToolPlan plan = ComputeStockTool(tool, minTier, out why);
            _stockPlanCache[key] = (Time.time, plan, why);
            return plan;
        }

        private StockToolPlan ComputeStockTool(ResourceDataHelper.ToolType tool, int minTier, out string why)
        {
            why = $"no {tool.ToString().ToLower()} of tier {minTier} or above on the craft ladder";
            string[] ladder = tool == ResourceDataHelper.ToolType.Pickaxe ? CraftablePickaxes
                : tool == ResourceDataHelper.ToolType.Axe ? CraftableAxes : null;
            var bag = _inventory?.GetStorageInventory();
            if (ladder == null || bag == null || ObjectDB.instance == null || Companion == null) return null;
            string firstWhy = null;
            foreach (string prefab in ladder)
            {
                var drop = ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>();
                if (drop == null || !ResourceDataHelper.IsToolAppropriate(drop.m_itemData, tool, minTier)) continue;
                string direct = AI.ChoreBrain.CraftFromStock(prefab, bag, Transform.position, StockBase, StockRadius, StockMayUse, out var stock);
                if (direct == "")
                    return new StockToolPlan { Tool = prefab, Summary = $"{prefab} at {stock.StationName} ({stock.Cost})" };
                string interWhy = null;
                if (direct.StartsWith("stock: ", System.StringComparison.Ordinal)
                    && TryPlanIntermediate(prefab, bag, out string inter, out int crafts, out string interSummary, out interWhy))
                    return new StockToolPlan { Tool = prefab, Intermediate = inter, IntermediateCrafts = crafts,
                        Summary = $"{inter} x{crafts} first ({interSummary}), then {prefab}" };
                firstWhy ??= $"{prefab}: {direct}" + (interWhy != null ? $"; {interWhy}" : "");
            }
            why = firstWhy ?? why;
            return null;
        }

        // The one intermediate a tool may need: the tool is short of exactly one item with its own recipe, its station is in reach, and
        // stock covers the k crafts of it plus everything else the tool takes.
        private bool TryPlanIntermediate(string toolPrefab, Inventory bag, out string inter, out int crafts, out string summary, out string why)
        {
            inter = null; crafts = 0; summary = null; why = null;
            Recipe toolRecipe = AI.ChoreBrain.RecipeFor(toolPrefab);
            if (toolRecipe == null) return false;
            var chests = ChestHelper.FindNearbyChests(StockBase, StockRadius).Where(c => c != null && StockMayUse(c)).ToList();
            int Have(ItemDrop item) => bag.CountItems(item.m_itemData.m_shared.m_name)
                + chests.Sum(c => Core.InventoryTransferService.CountItem(c, item.gameObject.name));
            var need = new Dictionary<string, (ItemDrop Item, int Amount)>();
            void Need(ItemDrop item, int amount)
            {
                string name = item.gameObject.name;
                need[name] = need.TryGetValue(name, out var had) ? (item, had.Amount + amount) : (item, amount);
            }

            ItemDrop shortItem = null;
            int shortBy = 0;
            foreach (var req in AI.ChoreBrain.UpgradeRequirements(toolRecipe, 1))
            {
                int have = Have(req.m_resItem);
                if (have >= req.m_amount) { Need(req.m_resItem, req.m_amount); continue; }
                if (shortItem != null) { why = $"short of both {shortItem.gameObject.name} and {req.m_resItem.gameObject.name}"; return false; }
                shortItem = req.m_resItem;
                shortBy = req.m_amount - have;
                if (have > 0) Need(req.m_resItem, have);
            }
            if (shortItem == null) return false;
            Recipe sub = AI.ChoreBrain.RecipeFor(shortItem.gameObject.name);
            if (sub == null) { why = $"{shortItem.gameObject.name} has no recipe"; return false; }
            string stationWhy = AI.ChoreBrain.CraftStationRefusal(sub, StockBase, StockRadius, out CraftingStation subStation);
            if (stationWhy != "") { why = $"{shortItem.gameObject.name}: {stationWhy}"; return false; }
            crafts = (shortBy + Mathf.Max(1, sub.m_amount) - 1) / Mathf.Max(1, sub.m_amount);
            var subCost = AI.ChoreBrain.UpgradeRequirements(sub, 1);
            foreach (var req in subCost) Need(req.m_resItem, req.m_amount * crafts);
            var shortOf = new List<string>();
            foreach (var n in need.Values)
            {
                int have = Have(n.Item);
                if (have < n.Amount) shortOf.Add($"{n.Item.gameObject.name} {have}/{n.Amount}");
            }
            int craftsWanted = crafts;
            if (shortOf.Count > 0) { why = $"for {craftsWanted} {shortItem.gameObject.name} too: stock {string.Join(", ", shortOf)}"; crafts = 0; return false; }
            inter = shortItem.gameObject.name;
            summary = string.Join(", ", subCost.Select(r => $"{r.m_resItem.gameObject.name} {r.m_amount * craftsWanted}"))
                + $" at {(subStation != null ? Utils.GetPrefabName(subStation.gameObject) : "hand")}";
            return true;
        }

        // Start crafting a tool for the resource from base stock; false (with the reason as a line) when stock can't make one.
        private bool TryBeginStockToolCraft(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null || resource.RequiredTool == ResourceDataHelper.ToolType.None || Companion == null) return false;
            _stockPlanCache.Clear();   // decide on the stock as it is now
            string forWhat = resource.GameObject != null ? Utils.GetPrefabName(resource.GameObject) : resource.Name;
            string toolName = resource.RequiredTool.ToString().ToLower();
            StockToolPlan plan = PlanStockTool(resource.RequiredTool, resource.MinToolTier, out string why);
            if (plan == null)
            {
                AI.ChoreBrain.ChoreDone(Companion.companionName, "crafting", $"can't craft a tier-{resource.MinToolTier} {toolName} for {forWhat}: {why}");
                return false;
            }
            int has = GetBestObtainableToolTier(resource.RequiredTool);
            _stockPlan = plan;
            _stockIntermediateDone = 0;
            _stockFor = forWhat;
            // The crafting is the work: the run gets its full gather time after it, not what is left of it.
            MaxDuration = Mathf.Max(MaxDuration, Time.time - StartTime + MaxGatherTime + 30f);
            AI.ChoreBrain.ChoreDone(Companion.companionName, "crafting",
                $"{plan.Tool} for {forWhat} (needs {toolName} tier {resource.MinToolTier}, has {(has == NoObtainableTool ? "none" : has.ToString())}): {plan.Summary}");
            return StartStockCraftStep();
        }

        // The next step: an intermediate still owed, else the tool itself. False (with a line) when stock no longer covers it.
        private bool StartStockCraftStep()
        {
            var bag = _inventory?.GetStorageInventory();
            if (_stockPlan == null || bag == null) return false;
            string step = _stockPlan.Intermediate != null && _stockIntermediateDone < _stockPlan.IntermediateCrafts ? _stockPlan.Intermediate : _stockPlan.Tool;
            string why = AI.ChoreBrain.CraftFromStock(step, bag, Transform.position, StockBase, StockRadius, StockMayUse, out var stock);
            if (why != "")
            {
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting", $"{step} for {_stockFor}: stopped, {why}");
                EndStockCraft();
                return false;
            }
            _stockStep = stock;
            _stockFetchIndex = 0;
            _craftRecipe = stock.Recipe;
            _craftWorkbench = stock.Station;
            if (stock.Fetch.Count > 0)
            {
                SetPhase(GatherPhase.FetchingCraftStock);
                return true;
            }
            BeginToolCraft();
            return true;
        }

        private void EndStockCraft()
        {
            _stockPlan = null;
            _stockStep = null;
            _stockFetchIndex = 0;
            _stockIntermediateDone = 0;
        }

        // Walk to each chest the step's plan lists and take exactly what it lists; then to the station.
        private bool UpdateFetchingCraftStock()
        {
            if (_stockStep == null || _stockFetchIndex >= _stockStep.Fetch.Count)
            {
                BeginToolCraft();
                return false;
            }
            var (chest, prefab, amount) = _stockStep.Fetch[_stockFetchIndex];
            string step = _stockStep.Recipe != null && _stockStep.Recipe.m_item != null ? _stockStep.Recipe.m_item.name : "?";
            if (chest == null)
            {
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting", $"{step} for {_stockFor}: stopped, the chest holding {prefab} is gone");
                EndStockCraft();
                SetPhase(GatherPhase.Complete);
                return true;
            }
            Vector3 interactionPoint = Core.InteractionPointHelper.GetContainerInteractionPoint(chest, Transform.position, ChestInteractionDistance);
            if (Vector3.Distance(Transform.position, interactionPoint) > Core.InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                MoveToPosition(interactionPoint);
                if (Time.time - _phaseStartTime > StockFetchTimeout)
                {
                    AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting",
                        $"{step} for {_stockFor}: stopped, couldn't reach {Utils.GetPrefabName(chest.gameObject)} for {prefab} in {StockFetchTimeout:0} s");
                    EndStockCraft();
                    SetPhase(GatherPhase.Complete);
                }
                return false;
            }
            StopMovement();
            int got = AI.ChoreBrain.Withdraw(chest, _inventory.GetStorageInventory(), prefab, amount, ChestHelper.ChestOwnerIdFor(Companion));
            if (got < amount)
            {
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting",
                    $"{step} for {_stockFor}: stopped, took {got}/{amount} {prefab} from {Utils.GetPrefabName(chest.gameObject)}");
                EndStockCraft();
                SetPhase(GatherPhase.Complete);
                return true;
            }
            _inventory.SaveToZDO();
            AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting", $"took {got} {prefab} from {Utils.GetPrefabName(chest.gameObject)} for {step}");
            _stockFetchIndex++;
            SetPhase(GatherPhase.FetchingCraftStock);   // each chest gets its own walk time
            if (_stockFetchIndex >= _stockStep.Fetch.Count) BeginToolCraft();
            return false;
        }

        // After a craft in a stock chain: an intermediate starts the next step; the tool ends the chain (the caller equips it).
        // True when the chain goes on (the caller does nothing else).
        private bool ContinueStockCraft(string made)
        {
            if (_stockPlan == null) return false;
            if (_stockPlan.Intermediate != null && _stockIntermediateDone < _stockPlan.IntermediateCrafts && made == _stockPlan.Intermediate)
            {
                _stockIntermediateDone++;
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "crafting", $"crafted {made} ({_stockIntermediateDone}/{_stockPlan.IntermediateCrafts}) for {_stockPlan.Tool}");
                if (!StartStockCraftStep()) SetPhase(GatherPhase.Complete);
                return true;
            }
            EndStockCraft();
            return false;
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
