using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    // Helper methods: Chest operations, smelter operations, movement, items, etc.
    public partial class SmelterOperatorBehavior
    {
        #region Chest Operations
        
        /// <summary>
        /// Opens a chest visually with proper multiplayer sync.
        /// Uses the same mechanism as ChestInteractionService for consistent behavior.
        /// </summary>
        private void TryOpenChestVisually(Container chest)
        {
            if (chest == null) return;
            
            var nview = chest.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            // Check if already in use
            if (chest.IsInUse()) return;
            
            // Claim ownership so SetInUse works
            if (!nview.IsOwner())
            {
                nview.ClaimOwnership();
            }
            
            // Open the chest (triggers animation, sound, ZDO sync)
            chest.SetInUse(true);
            
            // Force immediate ZDO sync to all nearby players
            var zdo = nview.GetZDO();
            if (zdo != null && ZNet.instance != null)
            {
                foreach (var peer in ZNet.instance.GetConnectedPeers())
                {
                    ZDOMan.instance.ForceSendZDO(peer.m_uid, zdo.m_uid);
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Opened chest visually");
        }
        
        /// <summary>
        /// Closes a chest visually with proper multiplayer sync.
        /// </summary>
        private void TryCloseChestVisually(Container chest)
        {
            if (chest == null) return;
            
            var nview = chest.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            // Claim ownership so SetInUse works
            if (!nview.IsOwner())
            {
                nview.ClaimOwnership();
            }
            
            // Close the chest (triggers animation, sound, ZDO sync)
            chest.SetInUse(false);
            
            // Force immediate ZDO sync to all nearby players
            var zdo = nview.GetZDO();
            if (zdo != null && ZNet.instance != null)
            {
                foreach (var peer in ZNet.instance.GetConnectedPeers())
                {
                    ZDOMan.instance.ForceSendZDO(peer.m_uid, zdo.m_uid);
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Closed chest visually");
        }
        
        private void FindNearbyChests()
        {
            // Single scan from midpoint between companion and smelter
            // with enough radius to cover both positions
            Vector3 companionPos = Transform.position;
            Vector3 smelterPos = _targetSmelter != null ? _targetSmelter.transform.position : _targetPosition;
            Vector3 midpoint = (companionPos + smelterPos) * 0.5f;
            float halfDist = Vector3.Distance(companionPos, smelterPos) * 0.5f;
            float effectiveRadius = CHEST_SEARCH_RADIUS + halfDist;
            
            _nearbyChests = ChestHelper.FindNearbyChests(midpoint, effectiveRadius);
            
            // Also update the ResourceAccessService cache so it stays in sync
            if (_resources != null)
            {
                _resources.SetNearbyChests(_nearbyChests);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} found {_nearbyChests.Count} nearby chests (searched from companion pos {companionPos} and smelter pos {smelterPos}, radius={CHEST_SEARCH_RADIUS}m)");
                
                // Log what items are in the chests for debugging
                if (_nearbyChests.Count > 0 && _targetSmelter != null)
                {
                    // Check for ore
                    bool hasOre = false;
                    foreach (var conversion in _targetSmelter.m_conversion)
                    {
                        if (conversion.m_from != null)
                        {
                            string oreName = conversion.m_from.name;
                            int count = ChestHelper.CountItemInChests(_nearbyChests, oreName);
                            if (count > 0)
                            {
                                Debug.Log($"[SmelterOperator]   - Found {count}x {oreName} in chests");
                                hasOre = true;
                            }
                        }
                    }
                    if (!hasOre) Debug.Log($"[SmelterOperator]   - No ore found in any chest");
                    
                    // Check for fuel
                    if (_targetSmelter.m_fuelItem != null)
                    {
                        string fuelName = _targetSmelter.m_fuelItem.name;
                        int fuelCount = ChestHelper.CountItemInChests(_nearbyChests, fuelName);
                        Debug.Log($"[SmelterOperator]   - Found {fuelCount}x {fuelName} (fuel) in chests");
                    }
                }
            }
        }
        
        private void PullMaterialsFromChests()
        {
            _lastPullCount = 0; // Reset - will be set if items are actually pulled
            
            if (_inventory == null || _targetSmelter == null) return;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;
            
            // Calculate how much capacity the station has
            int oreCapacity = GetRemainingStationCapacity();
            int fuelCapacity = GetRemainingFuelCapacity();
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} PullMaterialsFromChests:");
                Debug.Log($"[SmelterOperator]   oreCapacity={oreCapacity}, fuelCapacity={fuelCapacity}, isKiln={_isKilnOperation}");
                Debug.Log($"[SmelterOperator]   nearbyChests count={_nearbyChests?.Count ?? 0}");
            }
            
            if (_isKilnOperation)
            {
                // For kilns, only pull wood - and only what we need
                int woodNeeded = Mathf.Max(oreCapacity, 0);
                if (woodNeeded > 0)
                {
                    string[] woodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
                    int totalPulled = 0;
                    
                    // CRITICAL FIX: Use PullItemsFromChests (contains matching) instead of PullItemsByPrefabName (exact matching)
                    // because wood prefab names may vary (e.g., "wood" vs "Wood")
                    foreach (string woodType in woodTypes)
                    {
                        if (totalPulled >= woodNeeded) break;
                        
                        // First try exact match
                        int pulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, woodType, woodNeeded - totalPulled);
                        
                        // If no exact match, try contains match (handles case differences)
                        if (pulled == 0)
                        {
                            pulled = ChestHelper.PullItemsFromChests(_nearbyChests, storageInv, woodType.ToLowerInvariant(), woodNeeded - totalPulled);
                        }
                        
                        totalPulled += pulled;
                        if (pulled > 0 && CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Pulled {pulled}x {woodType} from chests for kiln (need {woodNeeded}, total pulled={totalPulled})");
                    }
                    
                    // CRITICAL: Set _lastPullCount so circuit breaker knows we succeeded
                    _lastPullCount = totalPulled;
                    
                    // Log result
                    if (totalPulled == 0)
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                        {
                            Debug.LogWarning($"[SmelterOperator] {Companion?.companionName} FAILED to pull any wood from {_nearbyChests?.Count ?? 0} chests!");
                            if (_nearbyChests != null && _nearbyChests.Count > 0)
                            {
                                foreach (var chest in _nearbyChests)
                                {
                                    if (chest == null) continue;
                                    var inv = chest.GetInventory();
                                    if (inv == null) continue;
                                    var items = inv.GetAllItems();
                                    if (items.Count > 0)
                                    {
                                        Debug.Log($"[SmelterOperator]   Chest has {items.Count} stacks:");
                                        foreach (var item in items)
                                        {
                                            if (item != null)
                                                Debug.Log($"[SmelterOperator]     - {item.m_dropPrefab?.name ?? "?"} x{item.m_stack}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        _inventory.SaveToZDO();
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Successfully pulled {totalPulled} wood for kiln");
                    }
                }
                return;
            }
            
            // For smelters: Pull ores - only what we need based on remaining capacity
            int oreNeeded = Mathf.Max(oreCapacity, 0);
            int totalOrePulled = 0;
            if (oreNeeded > 0)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} trying to pull ORE from chests (need {oreNeeded})");
                
                foreach (var conversion in _targetSmelter.m_conversion)
                {
                    if (conversion.m_from != null && oreNeeded > totalOrePulled)
                    {
                        string orePrefab = conversion.m_from.name;
                        
                        int pulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, orePrefab, oreNeeded - totalOrePulled);
                        totalOrePulled += pulled;
                        
                        if (CompanionIdleBehavior.VerboseLogging && pulled > 0)
                            Debug.Log($"[SmelterOperator]   Pulled {pulled}x {orePrefab} (total ore pulled: {totalOrePulled})");
                    }
                }
            }
            else if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} oreNeeded=0, skipping ore pull");
            }
            
            // Pull fuel if needed - only what we need
            int totalFuelPulled = 0;
            if (_targetSmelter.m_fuelItem != null && fuelCapacity > 0)
            {
                string fuelPrefab = _targetSmelter.m_fuelItem.name;
                
                totalFuelPulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, fuelPrefab, fuelCapacity);
                
                if (CompanionIdleBehavior.VerboseLogging && totalFuelPulled > 0)
                    Debug.Log($"[SmelterOperator]   Pulled {totalFuelPulled}x {fuelPrefab} (fuel)");
            }
            
            // CRITICAL: Set _lastPullCount so circuit breaker knows we succeeded
            _lastPullCount = totalOrePulled + totalFuelPulled;
            
            // If we need coal and have a kiln nearby, pull wood for kiln operation
            if (_needsCoalFromKiln && _nearbyKiln != null)
            {
                int kilnCapacity = GetRemainingKilnCapacity();
                if (kilnCapacity > 0)
                {
                    string[] woodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
                    int totalPulled = 0;
                    foreach (string woodType in woodTypes)
                    {
                        if (totalPulled >= kilnCapacity) break;
                        int pulledWood = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, woodType, kilnCapacity - totalPulled);
                        totalPulled += pulledWood;
                        if (CompanionIdleBehavior.VerboseLogging && pulledWood > 0)
                            Debug.Log($"[SmelterOperator] Pulled {pulledWood}x {woodType} from chests for kiln (kiln capacity: {kilnCapacity})");
                    }
                    
                    // Also count wood pulled for kiln
                    _lastPullCount += totalPulled;
                }
            }
            
            // Save inventory if we pulled anything
            if (_lastPullCount > 0)
            {
                _inventory.SaveToZDO();
            }
            
            // Log final inventory state after pulling
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] After pulling - HasOre={HasOre()}, HasFuel={HasFuel()}, totalPulled={_lastPullCount}");
            }
        }
        
        /// <summary>
        /// Gets remaining ore/input capacity of the target station.
        /// </summary>
        private int GetRemainingStationCapacity()
        {
            if (_targetSmelter == null) return 0;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            return Mathf.Max(0, _targetSmelter.m_maxOre - queued);
        }
        
        /// <summary>
        /// Gets remaining fuel capacity of the target station.
        /// </summary>
        private int GetRemainingFuelCapacity()
        {
            if (_targetSmelter == null) return 0;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            return Mathf.Max(0, Mathf.FloorToInt(_targetSmelter.m_maxFuel - currentFuel));
        }
        
        /// <summary>
        /// Gets remaining capacity of the nearby kiln.
        /// </summary>
        private int GetRemainingKilnCapacity()
        {
            if (_nearbyKiln == null) return 0;
            
            var nview = _nearbyKiln.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            return Mathf.Max(0, _nearbyKiln.m_maxOre - queued);
        }
        
        private bool HasMaterialsInChests()
        {
            if (_targetSmelter == null) return false;
            
            // For kiln operations, check for wood
            if (_isKilnOperation)
            {
                return HasWoodInChests();
            }
            
            // Check for ores
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    if (ChestHelper.ChestsHaveItem(_nearbyChests, conversion.m_from.name))
                        return true;
                }
            }
            
            // Check for fuel
            if (_targetSmelter.m_fuelItem != null)
            {
                if (ChestHelper.ChestsHaveItem(_nearbyChests, _targetSmelter.m_fuelItem.name))
                    return true;
            }
            
            return false;
        }
        
        private void DepositToChests()
        {
            if (_inventory == null) return;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;
            
            // Get output item prefab names
            var outputPrefabs = new HashSet<string>();
            if (_targetSmelter != null)
            {
                foreach (var conversion in _targetSmelter.m_conversion)
                {
                    if (conversion.m_to != null)
                    {
                        outputPrefabs.Add(conversion.m_to.name.ToLowerInvariant());
                    }
                }
            }
            
            // Deposit each output type
            foreach (string outputName in outputPrefabs)
            {
                int deposited = ChestHelper.DepositItemType(_nearbyChests, storageInv, outputName);
                _itemsDeposited += deposited;
                
                if (CompanionIdleBehavior.VerboseLogging && deposited > 0)
                    Debug.Log($"[SmelterOperator] Deposited {deposited}x {outputName} to chests");
            }
            
            // Return unused input materials back to chests
            ReturnUnusedMaterialsToChests();
        }
        
        /// <summary>
        /// Returns unused input materials (ore, fuel, wood) back to nearby chests.
        /// Called during deposit phase to ensure we don't hoard materials.
        /// </summary>
        private void ReturnUnusedMaterialsToChests()
        {
            if (_inventory == null || _targetSmelter == null) return;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;
            
            // Build list of input material prefab names
            var inputPrefabs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            
            // Add ores
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    inputPrefabs.Add(conversion.m_from.name);
                }
            }
            
            // Add fuel
            if (_targetSmelter.m_fuelItem != null)
            {
                inputPrefabs.Add(_targetSmelter.m_fuelItem.name);
            }
            
            // Add wood types for kiln operations
            if (_isKilnOperation || _needsCoalFromKiln)
            {
                inputPrefabs.Add("Wood");
                inputPrefabs.Add("RoundLog");
                inputPrefabs.Add("FineWood");
                inputPrefabs.Add("ElderBark");
                inputPrefabs.Add("YggdrasilWood");
            }
            
            // Return each input type to chests
            var itemsToReturn = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
            int returnedCount = 0;
            
            foreach (var item in itemsToReturn)
            {
                if (item == null) continue;
                
                string dropName = item.m_dropPrefab?.name ?? "";
                
                if (inputPrefabs.Contains(dropName))
                {
                    // Try to return to a chest that already has this item
                    var targetChest = ChestHelper.FindChestWithItem(_nearbyChests, item);
                    if (targetChest != null)
                    {
                        var containerInv = targetChest.GetInventory();
                        if (containerInv != null && containerInv.CanAddItem(item))
                        {
                            containerInv.AddItem(item.Clone());
                            storageInv.RemoveItem(item);
                            returnedCount++;
                            continue;
                        }
                    }
                    
                    // Try any chest with room
                    foreach (var container in _nearbyChests)
                    {
                        if (container == null) continue;
                        var containerInv = container.GetInventory();
                        if (containerInv != null && containerInv.CanAddItem(item))
                        {
                            containerInv.AddItem(item.Clone());
                            storageInv.RemoveItem(item);
                            returnedCount++;
                            break;
                        }
                    }
                }
            }
            
            if (returnedCount > 0)
            {
                _inventory.SaveToZDO();
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Returned {returnedCount} unused input materials to chests");
            }
        }
        
        #endregion
        
        #region Smelter State Checks
        
        /// <summary>
        /// Checks if the smelter's ore level is below the refill threshold.
        /// Returns true if we should add more ore.
        /// </summary>
        private bool IsOreBelowThreshold()
        {
            if (_targetSmelter == null) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            int maxOre = _targetSmelter.m_maxOre;
            
            if (maxOre <= 0) return false;
            
            float fillRatio = (float)queued / maxOre;
            return fillRatio < ORE_REFILL_THRESHOLD;
        }
        
        /// <summary>
        /// Checks if the smelter's fuel level is below the refill threshold.
        /// Returns true if we should add more fuel.
        /// </summary>
        private bool IsFuelBelowThreshold()
        {
            if (_targetSmelter == null) return false;
            
            // Kilns don't use fuel
            if (_isKilnOperation) return false;
            
            // Some smelters don't use fuel
            if (_targetSmelter.m_maxFuel <= 0) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            float fillRatio = currentFuel / _targetSmelter.m_maxFuel;
            
            return fillRatio < FUEL_REFILL_THRESHOLD;
        }
        
        /// <summary>
        /// Checks if the station is adequately filled (above thresholds) and we should wait/idle.
        /// Returns true if station is full enough and we should wait for processing.
        /// </summary>
        private bool IsStationAdequatelyFilled()
        {
            // Station is adequately filled if BOTH ore and fuel are above their thresholds
            // OR if the station is completely full
            
            bool oreFull = !IsOreBelowThreshold();
            bool fuelFull = !IsFuelBelowThreshold() || _isKilnOperation || _targetSmelter?.m_maxFuel <= 0;
            
            return oreFull && fuelFull;
        }
        
        /// <summary>
        /// Checks if nearby chests have ore for the smelter.
        /// Uses ResourceAccessService when available for consistent caching.
        /// CRITICAL: For kilns, this checks for WOOD, not actual ore!
        /// </summary>
        private bool HasOreInChests()
        {
            if (_targetSmelter == null) return false;
            
            // CRITICAL: Make sure we have chests to check
            if (_nearbyChests == null || _nearbyChests.Count == 0)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasOreInChests - No nearby chests cached!");
                return false;
            }
            
            // CRITICAL FIX: For kilns, the "ore" is actually WOOD!
            // Kilns don't have m_conversion entries with real ores - they convert wood to coal
            if (_isKilnOperation)
            {
                bool hasWood = HasWoodInChests();
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasOreInChests (KILN) - Checking for wood: {hasWood}");
                return hasWood;
            }
            
            // Check each conversion type for regular smelters
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    string oreName = conversion.m_from.name;
                    bool found = ChestHelper.ChestsHaveItem(_nearbyChests, oreName);
                    if (found)
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] HasOreInChests - Found {oreName} in chests!");
                        return true;
                    }
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                // Log what ores we were looking for
                var oreNames = new List<string>();
                foreach (var conversion in _targetSmelter.m_conversion)
                {
                    if (conversion.m_from != null)
                        oreNames.Add(conversion.m_from.name);
                }
                Debug.Log($"[SmelterOperator] HasOreInChests - No ore found. Checked {_nearbyChests.Count} chests for: {string.Join(", ", oreNames)}");
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if nearby chests have fuel for the smelter.
        /// Uses ResourceAccessService when available for consistent caching.
        /// </summary>
        private bool HasFuelInChests()
        {
            if (_targetSmelter == null) return false;
            
            // CRITICAL: Make sure we have chests to check
            if (_nearbyChests == null || _nearbyChests.Count == 0)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasFuelInChests - No nearby chests cached!");
                return false;
            }
            
            // Check for the smelter's specific fuel type
            if (_targetSmelter.m_fuelItem != null)
            {
                string fuelName = _targetSmelter.m_fuelItem.name;
                bool found = ChestHelper.ChestsHaveItem(_nearbyChests, fuelName);
                if (found)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] HasFuelInChests - Found {fuelName} in chests!");
                    return true;
                }
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasFuelInChests - No {fuelName} found in {_nearbyChests.Count} chests");
                return false;
            }
            
            // Default fuel check (Coal)
            bool hasCoal = ChestHelper.ChestsHaveItem(_nearbyChests, "Coal");
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] HasFuelInChests - Coal check: {hasCoal} ({_nearbyChests.Count} chests)");
            return hasCoal;
        }
        
        /// <summary>
        /// Checks if smelter needs more fuel.
        /// Uses same logic as Valheim: fuel > (m_maxFuel - 1) means full
        /// </summary>
        private bool NeedsFuel()
        {
            if (_targetSmelter == null) return false;
            
            // Kilns don't use fuel (they use ore slot for wood)
            if (_isKilnOperation) return false;
            
            // Some smelters don't use fuel
            if (_targetSmelter.m_maxFuel == 0) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            
            // Valheim's check: GetFuel() > (m_maxFuel - 1) means full
            // So we need fuel if currentFuel <= (m_maxFuel - 1), i.e., currentFuel < m_maxFuel
            return currentFuel < _targetSmelter.m_maxFuel;
        }
        
        /// <summary>
        /// Gets the current fuel level in the smelter.
        /// </summary>
        private float GetCurrentFuel()
        {
            if (_targetSmelter == null) return 0f;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0f;
            
            return nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
        }
        
        /// <summary>
        /// Gets the current ore queue size in the smelter.
        /// </summary>
        private int GetCurrentOreCount()
        {
            if (_targetSmelter == null) return 0;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            return nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
        }
        
        /// <summary>
        /// Checks if companion has fuel in inventory.
        /// </summary>
        private bool HasFuel()
        {
            if (_inventory == null || _targetSmelter == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            string fuelPrefab = _targetSmelter.m_fuelItem?.gameObject.name ?? "Coal";
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                string dropName = item.m_dropPrefab?.name ?? "";
                if (dropName.Equals(fuelPrefab, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if smelter can accept more ore.
        /// </summary>
        private bool CanAddOre()
        {
            if (_targetSmelter == null) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            return queued < _targetSmelter.m_maxOre;
        }
        
        /// <summary>
        /// Checks if companion has ore in inventory.
        /// CRITICAL: For kilns, this checks for WOOD, not actual ore!
        /// </summary>
        private bool HasOre()
        {
            if (_inventory == null || _targetSmelter == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            // CRITICAL FIX: For kilns, the "ore" is actually WOOD!
            if (_isKilnOperation)
            {
                return HasWoodForKiln();
            }
            
            var neededOres = GetNeededOres();
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                string dropName = item.m_dropPrefab?.name ?? "";
                
                foreach (string oreName in neededOres)
                {
                    if (dropName.Equals(oreName, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            
            return false;
        }
        
        private bool CanAddMore()
        {
            if (_targetSmelter == null) return false;
            
            // Check if smelter can accept more ore
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            int queued = nview.GetZDO().GetInt("queued", 0);
            int maxItems = _targetSmelter.m_maxOre;
            
            if (queued >= maxItems) return false;
            
            // Check if we have materials
            return HasMaterials();
        }
        
        private bool HasMaterials()
        {
            if (_inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            var neededOres = GetNeededOres();
            string neededFuel = GetNeededFuel();
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                
                string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
                string dropName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                
                foreach (string oreName in neededOres)
                {
                    if (name.Contains(oreName.ToLowerInvariant()) || dropName.Contains(oreName.ToLowerInvariant()))
                        return true;
                }
                
                if (!string.IsNullOrEmpty(neededFuel))
                {
                    if (name.Contains(neededFuel.ToLowerInvariant()) || dropName.Contains(neededFuel.ToLowerInvariant()))
                        return true;
                }
            }
            
            return false;
        }
        
        private bool IsProcessing()
        {
            if (_targetSmelter == null) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            int queued = nview.GetZDO().GetInt("queued", 0);
            return queued > 0;
        }
        
        private bool HasOutput()
        {
            if (_targetSmelter == null) return false;
            
            // Check via ZDO if there's spawned output waiting
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // Smelters spawn output as a prefab - check if the spawn point exists
            // We can also check the ZDO for spawned count
            int spawned = nview.GetZDO().GetInt("spawnedAmount", 0);
            return spawned > 0;
        }
        
        #endregion
        
        #region Smelter Operations
        
        private List<string> GetNeededOres()
        {
            if (_targetSmelter == null) return new List<string>();
            
            var result = new List<string>();
            
            // CRITICAL FIX: For kilns, the "ore" is actually WOOD!
            // Kilns don't have m_conversion entries pointing to Wood - they just accept wood directly
            // IMPORTANT: These are EXACT prefab names - do NOT use pattern matching that might catch arrows!
            if (_isKilnOperation)
            {
                // Return EXACT wood prefab names for kilns (not patterns!)
                // This list is used for exact matching in TryAddOre()
                result.Add("Wood");
                result.Add("RoundLog");
                result.Add("FineWood");
                result.Add("ElderBark");
                result.Add("YggdrasilWood");
                return result;
            }
            
            // Check smelter's conversion list for regular smelters
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    result.Add(conversion.m_from.name);
                }
            }
            
            return result;
        }
        
        private string GetNeededFuel()
        {
            if (_targetSmelter == null) return null;
            
            if (_targetSmelter.m_fuelItem != null)
            {
                return _targetSmelter.m_fuelItem.name;
            }
            
            return "Coal"; // Default fuel for most smelters
        }
        
        private List<string> GetOutputNames()
        {
            if (_targetSmelter == null) return new List<string>();
            
            var result = new List<string>();
            
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_to != null)
                {
                    result.Add(conversion.m_to.name);
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Debug helper: Logs the companion's current inventory contents.
        /// </summary>
        private void LogInventoryContents(string context)
        {
            if (!CompanionIdleBehavior.VerboseLogging) return;
            
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null)
            {
                Debug.Log($"[SmelterOperator] {context} - No inventory");
                return;
            }
            
            var items = storageInv.GetAllItems();
            Debug.Log($"[SmelterOperator] {context} - Inventory has {items.Count} item stacks:");
            foreach (var item in items)
            {
                if (item != null)
                {
                    string dropName = item.m_dropPrefab?.name ?? "?";
                    Debug.Log($"  - {item.m_shared?.m_name} (prefab: {dropName}) x{item.m_stack}");
                }
            }
        }
        
        private bool TryAddOre()
        {
            if (_targetSmelter == null || _inventory == null || _humanoid == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // CRITICAL: Check if smelter can accept more ore BEFORE doing anything else
            // This prevents unnecessary inventory iteration and RPC spam
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            if (queued >= _targetSmelter.m_maxOre)
            {
                // Smelter is full - mark it and don't try again until output is collected
                _smelterIsFullWaitingForOutput = true;
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] TryAddOre: Station FULL ({queued}/{_targetSmelter.m_maxOre})");
                return false;
            }
            
            var neededOres = GetNeededOres();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] TryAddOre: Looking for ores: [{string.Join(", ", neededOres)}], isKiln={_isKilnOperation}");
            
            var allItems = storageInv.GetAllItems();
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] TryAddOre: Inventory has {allItems.Count} items:");
                foreach (var dbgItem in allItems)
                {
                    if (dbgItem != null)
                    {
                        string dbgPrefab = dbgItem.m_dropPrefab?.name ?? "?";
                        Debug.Log($"[SmelterOperator]   - {dbgPrefab} x{dbgItem.m_stack}");
                    }
                }
            }
            
            foreach (var item in allItems)
            {
                if (item == null) continue;
                if (item.m_stack <= 0) continue; // Skip empty stacks
                
                string dropName = item.m_dropPrefab?.name ?? "";
                if (string.IsNullOrEmpty(dropName)) continue;
                
                    // Check against needed ores (for kilns this includes wood types)
                    foreach (string oreName in neededOres)
                    {
                        // CRITICAL FIX: Use EXACT match only, not contains!
                        // This prevents ArrowWood from matching "Wood" and causing "Item not allowed" spam
                        bool matches = dropName.Equals(oreName, System.StringComparison.OrdinalIgnoreCase);
                        
                        // For kilns, also verify it's actually valid wood (not arrows, etc.)
                        if (matches && _isKilnOperation && !ChestHelper.IsValidWoodForKiln(dropName))
                        {
                            matches = false;
                        }
                        
                        if (matches)
                    {
                        // CRITICAL: Remove from inventory FIRST, then call RPC
                        // This ensures we don't add ore to smelter without consuming it
                        // Clone the item data before removal for logging
                        string itemName = item.m_shared?.m_name ?? dropName;
                        int stackBefore = item.m_stack;
                        
                        // Try to remove one item from the stack
                        if (!storageInv.RemoveOneItem(item))
                        {
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.LogWarning($"[SmelterOperator] Failed to remove ore from inventory: {itemName}");
                            continue; // Try next item
                        }
                        
                        // Successfully removed from inventory - now add to smelter via RPC
                        // CRITICAL: Use the ACTUAL prefab name from the item, not oreName
                        // This ensures the RPC gets the correct prefab reference
                        nview.InvokeRPC("RPC_AddOre", dropName);
                        _inventory.SaveToZDO();
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] SUCCESS: Added ore via RPC: {itemName} (prefab={dropName}, stack was {stackBefore}, now {stackBefore - 1})");
                        
                        return true;
                    }
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.LogWarning($"[SmelterOperator] TryAddOre FAILED: No matching ore found in inventory!");
            
            return false;
        }
        
        private bool TryAddFuel()
        {
            if (_targetSmelter == null || _inventory == null || _humanoid == null) return false;
            
            // Kilns don't use fuel
            if (_isKilnOperation) return false;
            
            // Some smelters don't use fuel
            if (_targetSmelter.m_maxFuel == 0) return false;
            
            var nview = _targetSmelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // CRITICAL: Use same check as Valheim: GetFuel() > (m_maxFuel - 1) means full
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            if (currentFuel > (_targetSmelter.m_maxFuel - 1))
            {
                // Fuel is full - no need to add more
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Fuel is full: {currentFuel}/{_targetSmelter.m_maxFuel}");
                return false;
            }
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            // Get the actual fuel item prefab name from the smelter
            if (_targetSmelter.m_fuelItem == null) return false;
            string fuelPrefabName = _targetSmelter.m_fuelItem.gameObject.name;
            
            // CRITICAL FIX: Find the fuel item FIRST, then verify we can remove it
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                if (item.m_stack <= 0) continue; // Skip empty stacks
                
                string dropName = item.m_dropPrefab?.name ?? "";
                
                if (dropName.Equals(fuelPrefabName, System.StringComparison.OrdinalIgnoreCase))
                {
                    // CRITICAL: Remove from inventory FIRST, then call RPC
                    // This ensures we don't add fuel to smelter without consuming it
                    string itemName = item.m_shared?.m_name ?? dropName;
                    int stackBefore = item.m_stack;
                    
                    // Try to remove one item from the stack
                    if (!storageInv.RemoveOneItem(item))
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.LogWarning($"[SmelterOperator] Failed to remove fuel from inventory: {itemName}");
                        continue; // Try next item
                    }
                    
                    // Successfully removed from inventory - now add to smelter via RPC
                    nview.InvokeRPC("RPC_AddFuel");
                    _inventory.SaveToZDO();
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Added fuel via RPC: {itemName} (stack was {stackBefore}, fuel now ~{currentFuel + 1}/{_targetSmelter.m_maxFuel})");
                    
                    return true;
                }
            }
            
            return false;
        }
        
        private bool TryCollectOutput()
        {
            if (_targetSmelter == null || _humanoid == null) return false;
            
            // Interact with smelter to collect output
            var interactable = _targetSmelter as Interactable;
            if (interactable != null && interactable.Interact(_humanoid, false, false))
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Collected output from smelter");
                return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Kiln Helpers
        
        /// <summary>
        /// Finds a nearby kiln for coal production.
        /// </summary>
        private Smelter FindNearbyKiln()
        {
            var colliders = Physics.OverlapSphere(_targetPosition, STATION_DETECTION_RANGE);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var smelter = col.GetComponent<Smelter>() ?? col.GetComponentInParent<Smelter>();
                if (smelter == null) continue;
                if (smelter == _targetSmelter) continue; // Skip our main target
                
                string stationType = GetStationTypeFromSmelter(smelter);
                if (stationType.ToLowerInvariant().Contains("kiln"))
                {
                    // Check if kiln is available
                    if (InteractableOccupancyManager.CanUseInteractable(smelter.gameObject, _character))
                    {
                        return smelter;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Checks if we have wood to fill a kiln.
        /// CRITICAL: Uses ChestHelper.IsValidWoodForKiln() to exclude arrows and other non-fuel items.
        /// </summary>
        private bool HasWoodForKiln()
        {
            if (_inventory == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasWoodForKiln: No inventory");
                return false;
            }
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] HasWoodForKiln: No storage inventory");
                return false;
            }
            
            var allItems = storageInv.GetAllItems();
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] HasWoodForKiln: Checking {allItems.Count} items in storage");
            
            foreach (var item in allItems)
            {
                if (item == null) continue;
                
                string dropName = item.m_dropPrefab?.name ?? "";
                
                // CRITICAL FIX: Use ChestHelper.IsValidWoodForKiln() to exclude arrows and other non-fuel items
                // This prevents "Item not allowed ArrowWood" spam
                if (ChestHelper.IsValidWoodForKiln(dropName))
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] HasWoodForKiln: FOUND valid wood - {dropName} x{item.m_stack}");
                    return true;
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] HasWoodForKiln: No valid wood found. Items in inventory:");
                foreach (var item in allItems)
                {
                    if (item != null)
                    {
                        string prefab = item.m_dropPrefab?.name ?? "?";
                        bool isValidWood = ChestHelper.IsValidWoodForKiln(prefab);
                        Debug.Log($"[SmelterOperator]   - {prefab} x{item.m_stack} (validWood={isValidWood})");
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if nearby chests have VALID wood for kilns.
        /// CRITICAL: Uses exact prefab matching to exclude arrows (ArrowWood, etc.)
        /// </summary>
        private bool HasWoodInChests()
        {
            // Valid wood types for kilns - EXACT prefab names only
            string[] validWoodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
            
            // Fallback to direct ChestHelper with EXACT matching
            if (_nearbyChests == null || _nearbyChests.Count == 0) return false;
            
            foreach (var container in _nearbyChests)
            {
                if (container == null) continue;
                
                var containerInv = container.GetInventory();
                if (containerInv == null) continue;
                
                foreach (var item in containerInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    string prefabName = item.m_dropPrefab?.name ?? "";
                    
                    // Use ChestHelper.IsValidWoodForKiln() to properly exclude arrows
                    if (ChestHelper.IsValidWoodForKiln(prefabName))
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] HasWoodInChests: Found valid wood {prefabName} x{item.m_stack}");
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Item Pickup
        
        /// <summary>
        /// Finds the nearest pickupable item within scan radius of the center point.
        /// Returns null if no items found.
        /// </summary>
        private ItemDrop FindNearestPickupItem(Vector3 center)
        {
            ItemDrop nearest = null;
            float nearestDist = float.MaxValue;
            
            var colliders = Physics.OverlapSphere(center, PICKUP_SCAN_RADIUS);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                ItemDrop itemDrop = col.GetComponent<ItemDrop>() ?? col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null) continue;
                
                // Check if we can pickup
                if (!itemDrop.CanPickup()) continue;
                if (itemDrop.IsPiece()) continue;
                
                // Check if item has valid ZNetView
                var itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                float dist = Vector3.Distance(Transform.position, itemDrop.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = itemDrop;
                }
            }
            
            return nearest;
        }
        
        /// <summary>
        /// Tries to pick up a specific ItemDrop.
        /// Returns true if successful.
        /// </summary>
        private bool TryPickupSpecificItem(ItemDrop itemDrop)
        {
            if (itemDrop == null || !itemDrop || !itemDrop.gameObject.activeInHierarchy) return false;
            if (!itemDrop.CanPickup()) return false;
            
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null) return false;
            
            var itemNview = itemDrop.GetComponent<ZNetView>();
            if (itemNview == null || !itemNview.IsValid()) return false;
            
            itemDrop.Load();
            var itemData = itemDrop.m_itemData;
            if (itemData == null) return false;
            
            // Check weight and capacity
            float maxWeight = _inventory.GetMaxCarryWeight();
            float currentWeight = storageInv.GetTotalWeight();
            float itemWeight = itemData.GetWeight();
            
            if (currentWeight + itemWeight > maxWeight) return false;
            if (!storageInv.CanAddItem(itemData, itemData.m_stack)) return false;
            
            // Add to inventory
            if (storageInv.AddItem(itemData))
            {
                _inventory.SaveToZDO();
                ZNetScene.instance?.Destroy(itemDrop.gameObject);
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Picked up specific item: {itemData.m_shared.m_name} x{itemData.m_stack}");
                
                return true;
            }
            
            return false;
        }
        
        private int CountNearbyGroundItems(Vector3 center)
        {
            int count = 0;
            var colliders = Physics.OverlapSphere(center, PICKUP_SCAN_RADIUS);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                ItemDrop itemDrop = col.GetComponent<ItemDrop>() ?? col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null) continue;
                
                // Check if we can pickup
                if (!itemDrop.CanPickup()) continue;
                if (itemDrop.IsPiece()) continue;
                
                count++;
            }
            
            return count;
        }
        
        private int PickupNearbyGroundItems(Vector3 center)
        {
            int pickedUp = 0;
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null) return 0;
            
            // Find ItemDrops near the output point
            var colliders = Physics.OverlapSphere(center, PICKUP_SCAN_RADIUS);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                ItemDrop itemDrop = col.GetComponent<ItemDrop>() ?? col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null) continue;
                
                // Check if we can pickup
                if (!itemDrop.CanPickup()) continue;
                if (itemDrop.IsPiece()) continue;
                
                var itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                
                itemDrop.Load();
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                // Check weight and capacity
                float maxWeight = _inventory.GetMaxCarryWeight();
                float currentWeight = storageInv.GetTotalWeight();
                float itemWeight = itemData.GetWeight();
                
                if (currentWeight + itemWeight > maxWeight) continue;
                if (!storageInv.CanAddItem(itemData, itemData.m_stack)) continue;
                
                // Add to inventory
                if (storageInv.AddItem(itemData))
                {
                    _inventory.SaveToZDO();
                    ZNetScene.instance?.Destroy(itemDrop.gameObject);
                    _itemsCollected++;
                    pickedUp++;
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Picked up {itemData.m_shared.m_name} x{itemData.m_stack} from ground");
                }
            }
            
            return pickedUp;
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Shows the current working status above the companion's head.
        /// Rate-limited by CompanionChatHelper to avoid spam.
        /// </summary>
        private void ShowCurrentStatus()
        {
            if (Companion == null) return;
            
            string status = GetStatusDescription();
            CompanionChatHelper.ShowWorkingStatus(Companion, status);
        }
        
        private string GetStationName()
        {
            if (_targetSmelter == null) return "station";
            
            // Use PieceDataHelper for proper station type identification
            return PieceDataHelper.GetSmelterType(_targetSmelter);
        }
        
        /// <summary>
        /// Gets the station type from a Smelter component.
        /// </summary>
        private string GetStationTypeFromSmelter(Smelter smelter)
        {
            if (smelter == null) return "station";
            return PieceDataHelper.GetSmelterType(smelter);
        }
        
        private void NotifyOwner()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            // Release occupancy when done
            if (_targetSmelter != null)
            {
                InteractableOccupancyManager.Release(_targetSmelter.gameObject, _character);
            }
            
            // Also release kiln if we were using it
            if (_nearbyKiln != null)
            {
                InteractableOccupancyManager.Release(_nearbyKiln.gameObject, _character);
            }
            
            // End gathering session and get items collected
            var gatheredItems = _autoPickup?.EndGatheringSession();
            int totalGathered = 0;
            if (gatheredItems != null)
            {
                foreach (var kvp in gatheredItems)
                {
                    totalGathered += kvp.Value;
                }
            }
            
            if (_itemsAdded == 0 && _itemsCollected == 0 && _itemsDeposited == 0 && totalGathered == 0) return;
            
            var owner = Companion.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                string message = $"{Companion.GetDisplayName()} operated {GetStationName()} ({_operationCycles + 1} cycles): " +
                    $"added {_itemsAdded}, collected {_itemsCollected + totalGathered}, deposited {_itemsDeposited}";
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }
        
        private void PlayInteractAnimation()
        {
            // Use simple trigger for interact animation - short and doesn't get stuck
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        /// <summary>
        /// Plays the crafting/working animation for station interaction.
        /// </summary>
        private void PlayWorkingAnimation(bool enable)
        {
            if (_zanim != null)
            {
                _zanim.SetBool("crafting", enable);
                _zanim.SetBool("Working", enable);
            }
        }
        
        private void SetPhase(OperatePhase phase)
        {
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }
        
        #endregion
        
        #region Movement
        
        /// <summary>
        /// Moves to a position using the base class TryMoveToPosition which integrates
        /// with UnifiedMovementAuthority and vanilla pathfinding.
        /// 
        /// Uses InteractionPointHelper to calculate a proper interaction point
        /// in front of the target, not behind or inside it.
        /// </summary>
        private void MoveToPosition(Vector3 position)
        {
            // Use InteractionPointHelper to find proper standing position
            Vector3 standPosition;
            
            if (_targetSmelter != null)
            {
                // For smelters: use the smelter interaction point helper
                standPosition = Core.InteractionPointHelper.GetSmelterInteractionPoint(
                    _targetSmelter, Transform.position);
            }
            else
            {
                // Fallback: calculate standoff from position
                standPosition = CalculateStandoffPosition(position);
            }
            
            // Store original target position for facing direction
            _targetPosition = position;
            
            // CRITICAL: Use base class TryMoveToPosition for proper authority integration
            // This uses CompanionAI pathfinding which handles obstacles correctly
            bool useRun = false; // Walk for idle behaviors
            TryMoveToPosition(standPosition, walk: true, run: useRun);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] {Companion?.companionName} moving to interaction point {standPosition} (target: {position})");
        }
        
        /// <summary>
        /// Moves to a chest using InteractionPointHelper for proper front-facing positioning.
        /// </summary>
        private void MoveToChest(Container chest)
        {
            if (chest == null) return;
            
            // Use InteractionPointHelper to find proper standing position in front of chest
            Vector3 standPosition = Core.InteractionPointHelper.GetContainerInteractionPoint(
                chest, Transform.position);
            
            // Store target for facing
            _targetChestForPull = chest;
            
            // Move to the interaction point
            TryMoveToPosition(standPosition, walk: true, run: false);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] {Companion?.companionName} moving to chest interaction point {standPosition}");
        }
        
        /// <summary>
        /// Stops movement - wraps base class StopMovement but does NOT release authority.
        /// Authority is held for the entire duration of the behavior to prevent re-acquisition spam.
        /// Authority is only released when the behavior completes or is cancelled.
        /// </summary>
        private new void StopMovement()
        {
            // DON'T call base.StopMovement() because it releases authority!
            // We want to keep authority throughout the behavior to prevent re-acquisition spam.
            
            // Clear movement direction without releasing authority
            if (CompanionAI != null)
            {
                // Just stop the pathfinding request without releasing authority
                // CompanionAI.ReleasePathfindingMovement() releases authority, so we avoid it
            }
            
            // Clear combat movement destination as backup
            _combatMovement?.ClearMoveDestination();
            
            // Zero out horizontal velocity to stop sliding
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
            
            // Stop the character's movement direction
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
        }
        
        /// <summary>
        /// Releases movement authority - called only when behavior completes or is cancelled.
        /// </summary>
        private void ReleaseMovementAuthorityFinal()
        {
            // Release via CompanionAI if available
            if (CompanionAI != null)
            {
                CompanionAI.ReleasePathfindingMovement(BehaviorName);
            }
            else
            {
                // Fallback to direct authority release
                var authority = Companion?.GetMovementAuthority();
                authority?.ReleaseAuthority(BehaviorName);
            }
        }
        
        private void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = (targetPos - Transform.position).normalized;
            dir.y = 0;
            
            if (dir.sqrMagnitude > 0.01f)
            {
                Quaternion targetRot = Quaternion.LookRotation(dir);
                Transform.rotation = Quaternion.Slerp(Transform.rotation, targetRot, Time.deltaTime * 5f);
            }
        }
        
        /// <summary>
        /// Stops all movement including zeroing rigidbody velocity to prevent jitter.
        /// </summary>
        private void StopAllMovement()
        {
            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // This causes Unity 6 warnings because Character internally tries to set velocity
            if (_rigidbody != null && _rigidbody.isKinematic)
            {
                return;
            }
            
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            // Only set velocity if NOT kinematic
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
        }
        
        /// <summary>
        /// Tries to play a random waiting emote for personality.
        /// Called periodically during waiting phases.
        /// </summary>
        private void TryPlayWaitingEmote()
        {
            if (Time.time - _lastEmoteTime < _nextEmoteDelay) return;
            
            // Pick a random emote
            string emote = _waitingEmotes[Random.Range(0, _waitingEmotes.Length)];
            
            // Play via ZSyncAnimation
            if (_zanim != null)
            {
                _zanim.SetTrigger(emote);
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} playing waiting emote: {emote}");
            }
            
            // Set next emote time
            _lastEmoteTime = Time.time;
            _nextEmoteDelay = Random.Range(MIN_EMOTE_INTERVAL, MAX_EMOTE_INTERVAL);
        }
        
        /// <summary>
        /// Calculates a standoff position near the smelter that respects the collision bubble.
        /// This keeps the companion at a comfortable distance to avoid physics pushing.
        /// </summary>
        private Vector3 CalculateStandoffPosition(Vector3 stationPos)
        {
            // Calculate direction from station to companion
            Vector3 dirFromStation = (Transform.position - stationPos).normalized;
            dirFromStation.y = 0;
            
            // If we're already far enough away or direction is zero, use station position
            if (dirFromStation.sqrMagnitude < 0.01f)
            {
                // Use a default direction (forward from station)
                if (_targetSmelter != null)
                {
                    dirFromStation = -_targetSmelter.transform.forward;
                    dirFromStation.y = 0;
                    dirFromStation.Normalize();
                }
                else
                {
                    dirFromStation = Vector3.back;
                }
            }
            
            // Calculate standoff position using InteractionPointHelper constants
            Vector3 standoffPos = stationPos + dirFromStation * InteractionPointHelper.DEFAULT_INTERACTION_DISTANCE;
            
            // Get ground height at standoff position
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(standoffPos, out groundHeight))
                {
                    standoffPos.y = groundHeight;
                }
            }
            
            return standoffPos;
        }
        
        /// <summary>
        /// Finds a safe position near the station to teleport to.
        /// Position must be WITHIN the arrival threshold so we don't get stuck in a loop!
        /// </summary>
        private Vector3 FindSafeTeleportPosition(Vector3 targetPos)
        {
            Vector3[] directions = new Vector3[]
            {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized
            };
            
            // CRITICAL: Teleport CLOSER than ARRIVAL_DISTANCE so we actually "arrive"
            // Use a small offset (0.3m) from the target so we're not exactly on it
            float safeDistance = 0.3f;
            
            foreach (var dir in directions)
            {
                Vector3 testPos = targetPos + dir * safeDistance;
                
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                    {
                        testPos.y = groundHeight + 0.1f;
                    }
                }
                
                // Check for obstructions
                Collider[] colliders = Physics.OverlapSphere(testPos + Vector3.up * 0.5f, 0.4f);
                bool blocked = false;
                foreach (var col in colliders)
                {
                    if (col == null || col.isTrigger) continue;
                    if (col.gameObject.layer == LayerMask.NameToLayer("terrain")) continue;
                    if (col.GetComponent<Smelter>() != null || col.GetComponentInParent<Smelter>() != null) continue;
                    blocked = true;
                    break;
                }
                
                if (!blocked && Physics.Raycast(testPos + Vector3.up * 2f, Vector3.down, 4f))
                {
                    return testPos;
                }
            }
            
            // Fallback: directly at target position (better than 3m away in loop!)
            Vector3 fallbackPos = targetPos;
            
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(fallbackPos, out groundHeight))
                {
                    fallbackPos.y = groundHeight + 0.1f;
                }
            }
            
            return fallbackPos;
        }
        
        #endregion
    }
}
