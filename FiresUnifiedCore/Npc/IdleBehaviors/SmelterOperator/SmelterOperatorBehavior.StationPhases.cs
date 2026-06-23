using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    // Station interaction phases: Moving to station, pulling from chests, filling, adding fuel/ore
    public partial class SmelterOperatorBehavior
    {
        #region Finding and Moving to Station
        
        private bool UpdateFindingStation()
        {
            // Must be commanded
            SetPhase(OperatePhase.Complete);
            return true;
        }
        
        private bool UpdateMovingToStation()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            float dist = Vector3.Distance(Transform.position, _targetPosition);
            
            // DEBUG: Log positions to understand the pathing issue
            if (CompanionIdleBehavior.VerboseLogging && (Time.time - _phaseStartTime < 0.5f || (int)(Time.time * 2) % 10 == 0))
            {
                Vector3 interactablePoint = Core.InteractionPointHelper.FindSmelterInteractablePoint(_targetSmelter);
                Debug.Log($"[SmelterOperator] {Companion?.companionName} MovingToStation:");
                Debug.Log($"  Companion pos: {Transform.position}");
                Debug.Log($"  _targetPosition (standing): {_targetPosition}");
                Debug.Log($"  Interactable point (Switch): {interactablePoint}");
                Debug.Log($"  Distance to target: {dist:F2}m (need <{ARRIVAL_DISTANCE:F1}m)");
            }
            
            // Use ARRIVAL_DISTANCE to check if we've arrived at the standing position
            if (dist < ARRIVAL_DISTANCE)
            {
                StopMovement();
                ResetProgressTracking();
                
                // CRITICAL: Register occupancy when we arrive at the smelter
                if (!InteractableOccupancyManager.TryOccupy(_targetSmelter.gameObject, _character, MAX_OPERATE_TIME))
                {
                    // Someone else grabbed it first
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion.companionName} could not occupy smelter - already taken");
                    SetPhase(OperatePhase.Complete);
                    return true;
                }
                
                // Find nearby chests
                FindNearbyChests();
                
                // Start the operation cycle
                SetPhase(OperatePhase.PullingFromChests);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            // Vanilla pathfinding requires MoveTo() to be called each frame to follow waypoints.
            TryMoveToPosition(_targetPosition, walk: true, run: false);
            
            // IMPROVED STUCK DETECTION: Track progress instead of just time
            bool isStuck = UpdateProgressTracking();
            
            // Calculate dynamic timeout based on distance
            float dynamicTimeout = Mathf.Min(
                PATHFINDING_TIMEOUT_BASE + (dist * PATHFINDING_TIMEOUT_PER_METER),
                MAX_PATHFINDING_TIMEOUT
            );
            
            // Check for timeout OR stuck condition
            bool shouldTeleport = false;
            string teleportReason = "";
            
            if (isStuck && _noProgressDuration >= NO_PROGRESS_TIMEOUT)
            {
                shouldTeleport = true;
                teleportReason = $"no progress for {_noProgressDuration:F1}s";
            }
            else if (Time.time - _phaseStartTime > dynamicTimeout)
            {
                shouldTeleport = true;
                teleportReason = $"timeout after {dynamicTimeout:F0}s";
            }
            
            if (shouldTeleport)
            {
                Debug.LogWarning($"[SmelterOperator] {Companion?.companionName} pathfinding failed ({teleportReason}) to smelter {dist:F1}m away - aborting so a closer target can be tried");
                // Blacklist this companion from smelter attempts briefly so other behaviors
                // (farming, gathering) get a turn before we retry a possibly unreachable station.
                string companionId = Companion?.companionId ?? Companion?.companionName ?? "unknown";
                _materialUnavailableCooldowns[companionId] = Time.time + MATERIAL_UNAVAILABLE_COOLDOWN;
                // Force a fresh station scan next time so we don't re-pick the same one.
                _cachedAutonomousSmelter = null;
                _lastAutonomousScanTime = -999f;
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Updates progress tracking and returns true if the companion appears stuck.
        /// </summary>
        private bool UpdateProgressTracking()
        {
            // Initialize on first call
            if (_lastProgressTime == 0f)
            {
                _lastProgressPosition = Transform.position;
                _lastProgressTime = Time.time;
                _noProgressDuration = 0f;
                return false;
            }
            
            // Only check at intervals to avoid jitter
            if (Time.time - _lastProgressTime < PROGRESS_CHECK_INTERVAL)
            {
                return _noProgressDuration >= NO_PROGRESS_TIMEOUT * 0.5f; // Return early stuck warning
            }
            
            // Calculate distance moved since last check
            float distanceMoved = Vector3.Distance(Transform.position, _lastProgressPosition);
            
            if (distanceMoved >= PROGRESS_THRESHOLD)
            {
                // Making progress - reset stuck counter
                _noProgressDuration = 0f;
                _lastProgressPosition = Transform.position;
            }
            else
            {
                // Not making progress - increment stuck counter
                _noProgressDuration += Time.time - _lastProgressTime;
                
                if (CompanionIdleBehavior.VerboseLogging && _noProgressDuration > 2f)
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} no progress for {_noProgressDuration:F1}s (moved {distanceMoved:F2}m)");
                }
            }
            
            _lastProgressTime = Time.time;
            
            return _noProgressDuration >= NO_PROGRESS_TIMEOUT * 0.5f;
        }
        
        /// <summary>
        /// Resets the progress tracking state.
        /// </summary>
        private void ResetProgressTracking()
        {
            _lastProgressPosition = Transform.position;
            _lastProgressTime = Time.time;
            _noProgressDuration = 0f;
        }
        
        #endregion
        
        #region Pulling From Chests
        
        // CIRCUIT BREAKER: Track consecutive failed pull attempts to prevent infinite loops
        private int _consecutiveFailedPulls = 0;
        private const int MAX_CONSECUTIVE_FAILED_PULLS = 3;
        
        // Track last successful pull for circuit breaker
        private int _lastPullCount = 0;
        
        private bool UpdatePullingFromChests()
        {
            StopMovement();
            
            // CRITICAL FIX: Only face target ONCE when entering this phase, not every frame!
            if (Time.time - _phaseStartTime < 0.2f)
            {
                FaceTarget(_targetPosition);
            }
            
            // CRITICAL: Re-find nearby chests in case they weren't found initially
            // This can happen if ContainerRegistry wasn't fully initialized when we first looked
            if (_nearbyChests == null || _nearbyChests.Count == 0)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} no chests cached - re-scanning...");
                FindNearbyChests();
            }
            
            // First check if companion already has needed materials in their own inventory
            // Only pull from chests if we don't have enough
            if (!HasMaterialsInOwnInventory())
            {
                // Check if we should use physical chest interaction
                if (UsePhysicalChestInteraction && _nearbyChests.Count > 0)
                {
                    // Find what we need and where to get it
                    if (TryStartPhysicalChestPull())
                    {
                        _consecutiveFailedPulls = 0; // Reset on successful start
                        return false; // Started physical interaction
                    }
                    // If no physical pull needed/possible, fall through to instant
                }
                
                // Instant pull (fallback or when physical interaction disabled)
                // CRITICAL FIX: Track the pull count before and after to detect actual success
                _lastPullCount = 0;
                PullMaterialsFromChests();
                
                // CIRCUIT BREAKER: Check if pull actually succeeded by looking at _lastPullCount
                // This is set by PullMaterialsFromChests() when items are actually pulled
                if (_lastPullCount > 0)
                {
                    // Successfully pulled items - reset counter
                    _consecutiveFailedPulls = 0;
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} pull SUCCESS: {_lastPullCount} items pulled");
                }
                else
                {
                    // Failed to pull any items
                    _consecutiveFailedPulls++;
                    Debug.LogWarning($"[SmelterOperator] {Companion?.companionName} failed pull attempt #{_consecutiveFailedPulls}/{MAX_CONSECUTIVE_FAILED_PULLS}");
                    
                    if (_consecutiveFailedPulls >= MAX_CONSECUTIVE_FAILED_PULLS)
                    {
                        Debug.LogWarning($"[SmelterOperator] {Companion?.companionName} CIRCUIT BREAKER: {_consecutiveFailedPulls} consecutive failed pulls - checking for resource gathering");
                        
                        // IMPROVEMENT: Before giving up, try to start resource gathering
                        // For kilns, try to gather wood from trees
                        // For smelters, try to mine ore
                        string materialNeeded = _isKilnOperation ? "wood" : "ore";
                        if (TryStartResourceGatheringForMaterial(materialNeeded))
                        {
                            Debug.Log($"[SmelterOperator] {Companion?.companionName} - Started resource gathering for {materialNeeded}");
                            // _needsResourceGathering flag is set, Update() will handle transition
                            return false;
                        }
                        
                        // Resource gathering not possible - set cooldown and complete
                        string companionId = Companion?.companionId ?? Companion?.companionName ?? "unknown";
                        _materialUnavailableCooldowns[companionId] = Time.time + MATERIAL_UNAVAILABLE_COOLDOWN;
                        
                        CompanionChatHelper.QuickMessages.NoMaterialsAvailable(Companion, materialNeeded);
                        SetPhase(OperatePhase.Complete);
                        return true;
                    }
                }
            }
            else
            {
                // Already have materials - reset counter
                _consecutiveFailedPulls = 0;
            }
            
            // CRITICAL: After pulling, we need to give the inventory a moment to sync
            // Use the _lastPullCount directly instead of re-checking inventory state
            // because inventory caching can cause HasOre()/HasWoodForKiln() to return stale data
            if (_lastPullCount > 0)
            {
                // We successfully pulled items - go directly to filling.
                // Log only in verbose mode ï¿½ this fires every pull cycle and spams the console.
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} pulled {_lastPullCount} items, proceeding to fill station");

                // CRITICAL: Set flag to prevent UpdateFillingStation from immediately going back to PullingFromChests
                _justPulledItems = true;
                _pullCompletedTime = Time.time;

                SetPhase(OperatePhase.FillingStation);
                return false;
            }
            
            // Only check resource availability if we didn't pull anything
            // This prevents false negatives from inventory caching
            if (!CheckResourceAvailability())
            {
                // No resources available - behavior will be cancelled
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            // Move to filling
            SetPhase(OperatePhase.FillingStation);
            return false;
        }
        
        /// <summary>
        /// Tries to start a physical chest pull operation.
        /// Returns true if started, false if nothing to pull or no chest found.
        /// </summary>
        private bool TryStartPhysicalChestPull()
        {
            if (_targetSmelter == null) return false;
            
            // Determine what item to pull based on smelter needs
            string itemToPull = null;
            int amountToPull = 0;
            Container bestChest = null;
            
            // For kilns, pull wood
            if (_isKilnOperation)
            {
                string[] woodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
                int capacity = GetRemainingStationCapacity();
                
                foreach (string woodType in woodTypes)
                {
                    bestChest = ContainerRegistry.GetClosestWithItem(Transform.position, woodType, CHEST_SEARCH_RADIUS);
                    if (bestChest != null)
                    {
                        itemToPull = woodType;
                        amountToPull = Mathf.Min(capacity, 10); // Pull up to 10 at a time
                        break;
                    }
                }
            }
            else
            {
                // For smelters: prioritize fuel if needed, then ore
                if (NeedsFuel() && !HasFuel())
                {
                    string fuelPrefab = _targetSmelter.m_fuelItem?.gameObject.name ?? "Coal";
                    bestChest = ContainerRegistry.GetClosestWithItem(Transform.position, fuelPrefab, CHEST_SEARCH_RADIUS);
                    if (bestChest != null)
                    {
                        itemToPull = fuelPrefab;
                        amountToPull = Mathf.Min(GetRemainingFuelCapacity(), 10);
                    }
                }
                
                // Then check for ore
                if (bestChest == null && !HasOre())
                {
                    foreach (var conversion in _targetSmelter.m_conversion)
                    {
                        if (conversion.m_from != null)
                        {
                            string orePrefab = conversion.m_from.name;
                            bestChest = ContainerRegistry.GetClosestWithItem(Transform.position, orePrefab, CHEST_SEARCH_RADIUS);
                            if (bestChest != null)
                            {
                                itemToPull = orePrefab;
                                amountToPull = Mathf.Min(GetRemainingStationCapacity(), 10);
                                break;
                            }
                        }
                    }
                }
            }
            
            if (bestChest == null || string.IsNullOrEmpty(itemToPull) || amountToPull <= 0)
            {
                return false;
            }
            
            // Store target info
            _targetChestForPull = bestChest;
            _pullItemPrefab = itemToPull;
            _pullItemAmount = amountToPull;
            
            // Check if we're already close enough to the chest
            // Use tight ARRIVAL_DISTANCE to ensure proper positioning
            float distToChest = Vector3.Distance(Transform.position, bestChest.transform.position);
            if (distToChest <= ARRIVAL_DISTANCE)
            {
                // Already at chest - go directly to interaction
                SetPhase(OperatePhase.InteractingWithChestForPull);
            }
            else
            {
                // Need to walk to chest - calculate proper interaction point in front of chest
                Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                    bestChest, Transform.position);
                SetPhase(OperatePhase.MovingToChestForPull);
                MoveToPosition(interactionPoint);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Starting physical pull: {amountToPull}x {itemToPull} from chest at {bestChest.transform.position}");
            
            return true;
        }
        
        /// <summary>
        /// Update phase: Moving to chest to pull materials.
        /// </summary>
        private bool UpdateMovingToChestForPull()
        {
            if (_targetChestForPull == null)
            {
                // Chest disappeared - fall back to instant pull
                SetPhase(OperatePhase.PullingFromChests);
                return false;
            }
            
            // Calculate interaction point (in front of chest) for proper arrival check
            Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChestForPull, Transform.position);
            float dist = Vector3.Distance(Transform.position, interactionPoint);
            
            // Use tight ARRIVAL_DISTANCE to ensure we reach the correct position in front of chest
            if (dist <= ARRIVAL_DISTANCE)
            {
                StopMovement();
                SetPhase(OperatePhase.InteractingWithChestForPull);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            // Move to the interaction point, not the chest center
            TryMoveToPosition(interactionPoint, walk: true, run: false);
            
            // Timeout check
            if (Time.time - _phaseStartTime > 30f)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Timeout walking to chest - using instant pull");
                StopMovement();
                PullMaterialsFromChests();
                SetPhase(OperatePhase.FillingStation);
            }
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Interacting with chest (open animation, pull items, close).
        /// Now properly shows chest opening and closing for all nearby clients.
        /// </summary>
        private bool UpdateInteractingWithChestForPull()
        {
            if (_targetChestForPull == null)
            {
                SetPhase(OperatePhase.FillingStation);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetChestForPull.transform.position);
            
            // Phase timing:
            // 0.0 - 0.1: Open chest + play animation
            // 0.1 - 0.6: Wait for open animation
            // 0.6 - 0.7: Pull items
            // 0.7 - 1.2: Wait for close animation
            // 1.2+: Done, move on
            
            float phaseTime = Time.time - _phaseStartTime;
            
            // At start: Play interact animation and OPEN chest visually
            if (phaseTime < 0.1f)
            {
                PlayInteractAnimation();
                TryOpenChestVisually(_targetChestForPull);
                return false;
            }
            
            // Wait for chest open animation
            if (phaseTime < 0.6f)
            {
                return false;
            }
            
            // Pull items (only once, around 0.6-0.7s mark)
            if (phaseTime >= 0.6f && phaseTime < 0.7f)
            {
                var storageInv = _inventory?.GetStorageInventory();
                if (storageInv != null && !string.IsNullOrEmpty(_pullItemPrefab))
                {
                    var result = InventoryTransferService.PullItem(
                        _targetChestForPull,
                        storageInv,
                        _pullItemPrefab,
                        _pullItemAmount
                    );
                    
                    if (result.Success)
                    {
                        _inventory.SaveToZDO();
                        _lastPullCount = result.AmountTransferred;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Pulled {result.AmountTransferred}x {_pullItemPrefab} from chest");
                        
                        // Fire event
                        Events.CompanionEvents.FireItemPulled(Companion, _pullItemPrefab, result.AmountTransferred);
                    }
                }
                return false;
            }
            
            // Close chest (around 0.7s mark)
            if (phaseTime >= 0.7f && phaseTime < 0.8f)
            {
                TryCloseChestVisually(_targetChestForPull);
                return false;
            }
            
            // Wait for close animation
            if (phaseTime < 1.2f)
            {
                return false;
            }
            
            // Done - clear pull target and return to smelter
            _targetChestForPull = null;
            _pullItemPrefab = null;
            _pullItemAmount = 0;
            
            // Return to smelter
            SetPhase(OperatePhase.MovingToStation);
            MoveToPosition(_targetPosition);
            
            return false;
        }
        
        /// <summary>
        /// Checks if any required resources are available (in inventory or chests).
        /// If not, tries to start resource gathering. If that's not possible, notifies the player.
        /// Returns false if operation should be cancelled.
        /// </summary>
        private bool CheckResourceAvailability()
        {
            // CRITICAL DEBUG: Log the check state
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} CheckResourceAvailability - chests found: {_nearbyChests?.Count ?? 0}");
                if (_targetSmelter != null)
                {
                    Debug.Log($"[SmelterOperator]   isKiln={_isKilnOperation}, smelter={_targetSmelter.m_name}");
                    Debug.Log($"[SmelterOperator]   HasOre()={HasOre()}, HasOreInChests()={HasOreInChests()}");
                    Debug.Log($"[SmelterOperator]   HasFuel()={HasFuel()}, HasFuelInChests()={HasFuelInChests()}, smelterFuel={GetCurrentFuel():F1}/{_targetSmelter?.m_maxFuel:F0}");
                    if (_isKilnOperation)
                    {
                        Debug.Log($"[SmelterOperator]   HasWoodForKiln()={HasWoodForKiln()}, HasWoodInChests()={HasWoodInChests()}");
                    }
                }
            }
            
            bool hasOre = HasOre() || HasOreInChests();
            // Also count fuel already loaded in the smelter ï¿½ if the station already has
            // coal we don't need any in the companion inventory or nearby chests.
            float loadedFuel = !_isKilnOperation ? GetCurrentFuel() : 0f;
            bool smelterAlreadyHasFuel = !_isKilnOperation && _targetSmelter != null
                                         && _targetSmelter.m_maxFuel > 0
                                         && loadedFuel > 0f;
            bool hasFuel = HasFuel() || HasFuelInChests() || smelterAlreadyHasFuel;
            bool hasWood = _isKilnOperation && (HasWoodForKiln() || HasWoodInChests());
            
            // For kilns, we only need wood
            if (_isKilnOperation)
            {
                if (!hasWood)
                {
                    // IMPROVEMENT: Try to start resource gathering for wood
                    if (TryStartResourceGatheringForMaterial("wood"))
                    {
                        Debug.Log($"[SmelterOperator] {Companion?.companionName} - No wood in chests, started gathering trees");
                        return false; // Will resume smelter operation after gathering
                    }
                    
                    CompanionChatHelper.QuickMessages.NoMaterialsAvailable(Companion, "wood");
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion?.companionName} - No wood available for kiln and no trees nearby, cancelling");
                    return false;
                }
                return true;
            }
            
            // For smelters, we need ore AND fuel (or ability to make fuel via kiln)
            bool canGetFuel = hasFuel || (FindNearbyKiln() != null && (HasWoodForKiln() || HasWoodInChests()));
            
            if (!hasOre && !hasFuel)
            {
                // No materials at all - try to gather
                if (TryStartResourceGatheringForMaterial("ore"))
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No ore in chests, started mining");
                    return false;
                }
                
                CompanionChatHelper.QuickMessages.NoMaterialsAvailable(Companion, "ore or fuel");
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No ore or fuel available, cancelling");
                return false;
            }
            
            if (!hasOre)
            {
                // Try to start mining for ore
                if (TryStartResourceGatheringForMaterial("ore"))
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No ore in chests, started mining");
                    return false;
                }
                
                CompanionChatHelper.QuickMessages.NoOreAvailable(Companion);
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No ore available, cancelling");
                return false;
            }
            
            if (!hasFuel && !canGetFuel)
            {
                // Try to gather wood to make coal
                if (TryStartResourceGatheringForMaterial("wood"))
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No fuel and no wood, started gathering trees");
                    return false;
                }
                
                CompanionChatHelper.QuickMessages.NoFuelAvailable(Companion);
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} - No fuel available and no kiln to make coal, cancelling");
                return false;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] {Companion?.companionName} - Resources available! hasOre={hasOre}, hasFuel={hasFuel}, canGetFuel={canGetFuel}");
            
            return true;
        }
        
        /// <summary>
        /// Tries to start a ResourceGatheringBehavior to gather the needed material type.
        /// Returns true if gathering was started, false if not possible.
        /// </summary>
        private bool TryStartResourceGatheringForMaterial(string materialType)
        {
            // Check if we have the required tool
            if (_inventory == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] TryStartResourceGathering - No inventory available");
                return false;
            }
            
            bool needsAxe = materialType == "wood";
            bool needsPickaxe = materialType == "ore";
            
            // Check for required tool by checking equipped and storage items
            bool hasTool = false;
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item == null) continue;
                    string prefab = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    if (needsAxe && prefab.Contains("axe") && !prefab.Contains("pickaxe"))
                    {
                        hasTool = true;
                        break;
                    }
                    if (needsPickaxe && prefab.Contains("pickaxe"))
                    {
                        hasTool = true;
                        break;
                    }
                }
            }
            
            // Also check equipped items
            if (!hasTool)
            {
                var equipped = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                if (equipped != null)
                {
                    string prefab = equipped.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    if (needsAxe && prefab.Contains("axe") && !prefab.Contains("pickaxe")) hasTool = true;
                    if (needsPickaxe && prefab.Contains("pickaxe")) hasTool = true;
                }
            }
            
            if (!hasTool)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] TryStartResourceGathering - No {(needsAxe ? "axe" : "pickaxe")} available");
                return false;
            }
            
            // Check if there's a suitable resource nearby
            Vector3 searchCenter = Transform.position;
            float searchRadius = 30f; // Default search radius for resources
            
            bool hasNearbyResource = false;
            
            if (needsAxe)
            {
                // Search for trees
                var colliders = Physics.OverlapSphere(searchCenter, searchRadius);
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    var tree = col.GetComponent<TreeBase>() ?? col.GetComponentInParent<TreeBase>();
                    if (tree != null)
                    {
                        hasNearbyResource = true;
                        break;
                    }
                    var treeLog = col.GetComponent<TreeLog>() ?? col.GetComponentInParent<TreeLog>();
                    if (treeLog != null)
                    {
                        hasNearbyResource = true;
                        break;
                    }
                }
            }
            else if (needsPickaxe)
            {
                // Search for mineable rocks
                var colliders = Physics.OverlapSphere(searchCenter, searchRadius);
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    var rock = col.GetComponent<MineRock>() ?? col.GetComponentInParent<MineRock>();
                    if (rock != null)
                    {
                        hasNearbyResource = true;
                        break;
                    }
                    var rock5 = col.GetComponent<MineRock5>() ?? col.GetComponentInParent<MineRock5>();
                    if (rock5 != null)
                    {
                        hasNearbyResource = true;
                        break;
                    }
                }
            }
            
            if (!hasNearbyResource)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] TryStartResourceGathering - No {materialType} resource found within {searchRadius}m");
                return false;
            }
            
            // Cancel this smelter operation and request resource gathering instead
            // The companion will gather resources, deposit them, then can start smelter operation again
            Debug.Log($"[SmelterOperator] {Companion?.companionName} starting resource gathering for {materialType}");
            
            // Tell the player what's happening
            CompanionChatHelper.ShowWorkingStatus(Companion, $"Going to gather {materialType}...");
            
            // Set a flag so we complete gracefully
            _needsResourceGathering = true;
            _resourceGatheringMaterial = materialType;
            
            return true;
        }
        
        // Flag to track if we need to start resource gathering
        private bool _needsResourceGathering = false;
        private string _resourceGatheringMaterial = "";
        
        /// <summary>
        /// Checks if companion has any required materials in their own storage inventory.
        /// </summary>
        private bool HasMaterialsInOwnInventory()
        {
            if (_inventory == null || _targetSmelter == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            // Check for ores
            foreach (var conversion in _targetSmelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    string orePrefab = conversion.m_from.name;
                    foreach (var item in storageInv.GetAllItems())
                    {
                        if (item == null) continue;
                        string dropName = item.m_dropPrefab?.name ?? "";
                        if (dropName.Equals(orePrefab, System.StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            
            // Check for fuel
            if (_targetSmelter.m_fuelItem != null)
            {
                string fuelPrefab = _targetSmelter.m_fuelItem.name;
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item == null) continue;
                    string dropName = item.m_dropPrefab?.name ?? "";
                    if (dropName.Equals(fuelPrefab, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Filling Station
        
        private bool UpdateFillingStation()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            StopMovement();
            
            // CRITICAL FIX: Only face target ONCE when entering this phase, not every frame!
            // Constantly calling FaceTarget() causes glitchy rotation due to competing rotation updates.
            if (Time.time - _phaseStartTime < 0.2f)
            {
                FaceTarget(_targetPosition);
            }
            
            // CRITICAL: Check if inventory is full - need to deposit before continuing
            var invStatus = GetInventoryStatus();
            if (invStatus.IsFull || invStatus.IsOverweight)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Inventory full ({invStatus.Reason}) - going to deposit");
                
                CompanionChatHelper.QuickMessages.InventoryFull(Companion);
                SetPhase(OperatePhase.DepositingToChests);
                return false;
            }
            
            // Detect switch positions from the smelter (only once)
            DetectSwitchPositions();
            
            // Calculate decisions once per frame (but only log occasionally to avoid spam)
            float currentFuel = GetCurrentFuel();
            int currentOre = GetCurrentOreCount();
            bool hasOreInv = HasOre();
            bool hasFuelInv = HasFuel();
            bool needsFuel = NeedsFuel();
            bool canAddOreCheck = CanAddOre();
            bool hasOreChests = HasOreInChests();
            bool hasFuelChests = HasFuelInChests();
            
            // NEW: Check if station is adequately filled (above thresholds)
            bool oreBelowThreshold = IsOreBelowThreshold();
            bool fuelBelowThreshold = IsFuelBelowThreshold();
            bool stationAdequatelyFilled = IsStationAdequatelyFilled();
            
            // RATE-LIMITED LOGGING: Only log at phase start (first 0.1s) to prevent spam
            // The filling station phase runs every frame, so we can't log every time
            bool shouldLog = CompanionIdleBehavior.VerboseLogging && (Time.time - _phaseStartTime < 0.1f);
            if (shouldLog)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} UpdateFillingStation decision:");
                Debug.Log($"[SmelterOperator]   Smelter: fuel={currentFuel:F1}/{_targetSmelter.m_maxFuel}, ore={currentOre}/{_targetSmelter.m_maxOre}");
                Debug.Log($"[SmelterOperator]   Thresholds: oreBelowThreshold={oreBelowThreshold}, fuelBelowThreshold={fuelBelowThreshold}, adequatelyFilled={stationAdequatelyFilled}");
                Debug.Log($"[SmelterOperator]   Inventory: HasOre={hasOreInv}, HasFuel={hasFuelInv}");
                Debug.Log($"[SmelterOperator]   Chests: HasOreInChests={hasOreChests}, HasFuelInChests={hasFuelChests}, ChestCount={_nearbyChests?.Count ?? 0}");
                Debug.Log($"[SmelterOperator]   Decisions: NeedsFuel={needsFuel}, CanAddOre={canAddOreCheck}");
            }
            
            // DEBUG: Log smelter state on first fill attempt each phase
            if (CompanionIdleBehavior.VerboseLogging && Time.time - _phaseStartTime < 0.1f)
            {
                Debug.Log($"[SmelterOperator] Switch positions: hasFuel={_hasFuelSwitch}, hasOre={_hasOreSwitch}");
                
                // Log what's in companion inventory
                LogInventoryContents("FillingStation start");
            }
            
            // ANTI-SPAM: If smelter was full and we're waiting for output, don't try to fill again
            if (_smelterIsFullWaitingForOutput)
            {
                if (HasOutput() || IsProcessing())
                {
                    SetPhase(OperatePhase.WaitingForOutput);
                    return false;
                }
                
                // Check for ground items near output point
                Vector3 outputPos = _targetSmelter.m_outputPoint != null 
                    ? _targetSmelter.m_outputPoint.position 
                    : _targetPosition;
                if (CountNearbyGroundItems(outputPos) > 0)
                {
                    _pickupAreaPosition = outputPos;
                    SetPhase(OperatePhase.MovingToPickupArea);
                    MoveToPosition(_pickupAreaPosition);
                    return false;
                }
                
                _smelterIsFullWaitingForOutput = false;
            }
            
            // NEW: If station is adequately filled (above thresholds), wait for output instead of constantly adding
            // This gives the station time to process and prevents hovering behavior
            if (stationAdequatelyFilled && IsProcessing())
            {
                if (shouldLog)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Station adequately filled, waiting for processing...");
                
                // Play a waiting emote occasionally
                TryPlayWaitingEmote();
                
                // Check for output to collect
                Vector3 outputPos = _targetSmelter.m_outputPoint != null 
                    ? _targetSmelter.m_outputPoint.position 
                    : _targetPosition;
                    
                if (CountNearbyGroundItems(outputPos) > 0)
                {
                    _pickupAreaPosition = outputPos;
                    SetPhase(OperatePhase.MovingToPickupArea);
                    MoveToPosition(_pickupAreaPosition);
                    return false;
                }
                
                // Just wait - don't spam logs, just play emotes occasionally
                return false;
            }
            
            // Decide what to do next - PRIORITIZE based on what's needed AND thresholds
            // Priority order:
            // 1. If ore is below threshold AND we have ore in inventory -> add it
            // 2. If fuel is below threshold AND we have fuel in inventory and need fuel -> add it  
            // 3. If we need ore and it's in chests -> go pull from chests
            // 4. If we need fuel and it's in chests -> go pull from chests
            // 5. If smelter is full -> wait for output
            
            // STEP 1: If we have ore and ore is below threshold, add it first (smelter won't run without ore)
            if (canAddOreCheck && hasOreInv && oreBelowThreshold)
            {
                if (shouldLog)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Adding ore (have ore in inventory, below {ORE_REFILL_THRESHOLD * 100:F0}% threshold)");
                if (_hasOreSwitch)
                {
                    SetPhase(OperatePhase.MovingToOreSwitch);
                    MoveToPosition(_oreSwitchPosition);
                }
                else
                {
                    SetPhase(OperatePhase.AddingOre);
                }
                return false;
            }
            
            // STEP 2: If we have fuel and fuel is below threshold, add it
            // CRITICAL FIX: Only go to fuel if we DON'T need ore OR smelter already has some ore
            // This prevents the companion from getting stuck adding fuel when ore slot is empty
            if (needsFuel && hasFuelInv && !_isKilnOperation && fuelBelowThreshold)
            {
                // Only add fuel if:
                // 1. Smelter already has ore queued, OR
                // 2. We have ore in inventory (will add ore next cycle), OR
                // 3. Smelter can't accept more ore (full)
                bool smelterHasOre = currentOre > 0;
                bool shouldAddFuel = smelterHasOre || hasOreInv || !canAddOreCheck;
                
                if (shouldAddFuel)
                {
                    if (shouldLog)
                        Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Adding fuel (have fuel in inventory, below {FUEL_REFILL_THRESHOLD * 100:F0}% threshold, smelterHasOre={smelterHasOre}, hasOreInv={hasOreInv})");
                    if (_hasFuelSwitch)
                    {
                        SetPhase(OperatePhase.MovingToFuelSwitch);
                        MoveToPosition(_fuelSwitchPosition);
                    }
                    else
                    {
                        SetPhase(OperatePhase.AddingFuel);
                    }
                    return false;
                }
                else if (shouldLog)
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Have fuel but smelter needs ore first (ore={currentOre}, canAddOre={canAddOreCheck})");
                }
            }
            
            // STEP 3: We don't have materials in inventory - need to pull from chests
            // CRITICAL: Skip this if we JUST pulled items - give inventory time to sync
            bool inPostPullGrace = _justPulledItems && (Time.time - _pullCompletedTime) < POST_PULL_GRACE_PERIOD;
            
            if (inPostPullGrace)
            {
                // We just pulled items but HasOre()/HasFuel() returned false - inventory hasn't synced yet
                // Wait a moment before making any decisions
                if (shouldLog)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> In post-pull grace period, waiting for inventory sync...");
                return false;
            }
            
            // Clear the flag after grace period
            if (_justPulledItems && (Time.time - _pullCompletedTime) >= POST_PULL_GRACE_PERIOD)
            {
                _justPulledItems = false;
            }
            
            // Check if we need ore (below threshold) and it's available in chests
            // Check if we need ore (below threshold) and it's available in chests
            // GUARD: if we just pulled items (_lastPullCount > 0), HasOre() may return false
            // due to a prefab-name mismatch between the puller and the checker ï¿½ in that case
            // attempt to add ore directly rather than looping back to pull again.
            if (canAddOreCheck && !hasOreInv && oreBelowThreshold && _lastPullCount > 0)
            {
                // Items were pulled last cycle but not recognised by HasOre(). Try adding anyway;
                // AddingOre/MovingToOreSwitch will simply no-op if nothing is there.
                if (shouldLog)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Pulled {_lastPullCount} items but HasOre=false; attempting direct add to break pull loop");
                _lastPullCount = 0;
                if (_hasOreSwitch)
                {
                    SetPhase(OperatePhase.MovingToOreSwitch);
                    MoveToPosition(_oreSwitchPosition);
                }
                else
                {
                    SetPhase(OperatePhase.AddingOre);
                }
                return false;
            }

            if (canAddOreCheck && !hasOreInv && hasOreChests && oreBelowThreshold)
            {
                if (shouldLog)
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Going to pull ORE from chests (below threshold)");
                SetPhase(OperatePhase.PullingFromChests);
                return false;
            }
            
            // STEP 4: Check if we need fuel (below threshold) and it's available in chests
            // CRITICAL FIX: Only pull fuel if smelter has ore OR we can't add ore
            if (needsFuel && !hasFuelInv && hasFuelChests && !_isKilnOperation && fuelBelowThreshold)
            {
                bool smelterHasOre = currentOre > 0;
                bool shouldPullFuel = smelterHasOre || !canAddOreCheck || !hasOreChests;
                
                if (shouldPullFuel)
                {
                    if (shouldLog)
                        Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Going to pull FUEL from chests (below threshold)");
                    SetPhase(OperatePhase.PullingFromChests);
                    return false;
                }
                else if (shouldLog)
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Need fuel but prioritizing ore first (ore={currentOre}, hasOreChests={hasOreChests})");
                }
            }
            
            // STEP 5: Need fuel but no fuel in chests - try kiln workflow
            if (needsFuel && !hasFuelInv && !hasFuelChests && !_isKilnOperation && fuelBelowThreshold)
            {
                bool haveOreSomewhere = hasOreInv || hasOreChests;
                bool canMakeCoal = (HasWoodForKiln() || HasWoodInChests()) && FindNearbyKiln() != null;
                
                if (haveOreSomewhere && canMakeCoal)
                {
                    _nearbyKiln = FindNearbyKiln();
                    if (_nearbyKiln != null)
                    {
                        _needsCoalFromKiln = true;
                        _primarySmelter = _targetSmelter;
                        _primarySmelterPosition = _targetPosition;
                        
                        Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Need coal, starting kiln workflow");
                        
                        // Pull wood from chests if needed
                        if (!HasWoodForKiln() && HasWoodInChests())
                        {
                            int kilnCapacity = GetRemainingKilnCapacity();
                            if (kilnCapacity > 0)
                            {
                                string[] woodTypes = { "Wood", "RoundLog", "FineWood" };
                                int totalPulled = 0;
                                foreach (string woodType in woodTypes)
                                {
                                    if (totalPulled >= kilnCapacity) break;
                                    var storageInv = _inventory.GetStorageInventory();
                                    if (storageInv != null)
                                    {
                                        int pulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, woodType, kilnCapacity - totalPulled);
                                        totalPulled += pulled;
                                        if (pulled > 0) break;
                                    }
                                }
                            }
                        }
                        
                        SetPhase(OperatePhase.MovingToKiln);
                        MoveToPosition(_nearbyKiln.transform.position);
                        return false;
                    }
                }
                else if (!haveOreSomewhere)
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> No ore anywhere, cancelling");
                    CompanionChatHelper.QuickMessages.NoOreAvailable(Companion);
                    
                    string companionId = Companion?.companionId ?? Companion?.companionName ?? "unknown";
                    _materialUnavailableCooldowns[companionId] = Time.time + MATERIAL_UNAVAILABLE_COOLDOWN;
                    
                    SetPhase(OperatePhase.Complete);
                    return true;
                }
                else if (!canMakeCoal)
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> No fuel and can't make coal, cancelling");
                    CompanionChatHelper.QuickMessages.NoFuelAvailable(Companion);
                    
                    string companionId = Companion?.companionId ?? Companion?.companionName ?? "unknown";
                    _materialUnavailableCooldowns[companionId] = Time.time + MATERIAL_UNAVAILABLE_COOLDOWN;
                    
                    SetPhase(OperatePhase.Complete);
                    return true;
                }
            }
            
            // STEP 6: Check if station is full (can't add more ore)
            if (!canAddOreCheck)
            {
                _smelterIsFullWaitingForOutput = true;
                
                if (IsProcessing() || HasOutput())
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Station full, waiting for output");
                    CompanionChatHelper.QuickMessages.StationFull(Companion, GetStationName());
                    SetPhase(OperatePhase.WaitingForOutput);
                }
                else
                {
                    Debug.Log($"[SmelterOperator] {Companion?.companionName} -> Station full, depositing to chests");
                    SetPhase(OperatePhase.DepositingToChests);
                }
                return false;
            }
            
            // STEP 7: Station is above thresholds but we could add more - check for output first
            if (stationAdequatelyFilled)
            {
                // Check for output to collect
                Vector3 outputPos = _targetSmelter.m_outputPoint != null 
                    ? _targetSmelter.m_outputPoint.position 
                    : _targetPosition;
                    
                if (CountNearbyGroundItems(outputPos) > 0)
                {
                    _pickupAreaPosition = outputPos;
                    SetPhase(OperatePhase.MovingToPickupArea);
                    MoveToPosition(_pickupAreaPosition);
                    return false;
                }
                
                // Just wait and emote - station is running fine
                TryPlayWaitingEmote();
                return false;
            }
            
            // STEP 8: No materials available anywhere - complete
            if (!hasOreInv && !hasOreChests && !hasFuelInv && !hasFuelChests)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} -> No materials available anywhere, completing");
                CompanionChatHelper.QuickMessages.NoMaterialsAvailable(Companion, "ore or fuel");
                SetPhase(OperatePhase.DepositingToChests);
                return false;
            }
            
            // Still here? Something unexpected - log once and wait
            // ANTI-SPAM: Only log this warning once per phase entry, not every frame
            if (Time.time - _phaseStartTime < 0.1f)
            {
                Debug.LogWarning($"[SmelterOperator] {Companion?.companionName} -> Unexpected state in UpdateFillingStation, waiting...");
            }
            
            // Try a waiting emote occasionally while stuck
            TryPlayWaitingEmote();
            return false;
        }
        
        /// <summary>
        /// Detects the positions of fuel and ore switches on the smelter.
        /// </summary>
        private void DetectSwitchPositions()
        {
            if (_targetSmelter == null) return;
            
            // Get fuel switch position
            if (_targetSmelter.m_addWoodSwitch != null)
            {
                _fuelSwitchPosition = _targetSmelter.m_addWoodSwitch.transform.position;
                _hasFuelSwitch = true;
            }
            else
            {
                _fuelSwitchPosition = _targetPosition;
                _hasFuelSwitch = false;
            }
            
            // Get ore switch position
            if (_targetSmelter.m_addOreSwitch != null)
            {
                _oreSwitchPosition = _targetSmelter.m_addOreSwitch.transform.position;
                _hasOreSwitch = true;
            }
            else
            {
                _oreSwitchPosition = _targetPosition;
                _hasOreSwitch = false;
            }
        }
        
        #endregion
        
        #region Adding Fuel
        
        /// <summary>
        /// Update phase: Moving to fuel switch position.
        /// </summary>
        private bool UpdateMovingToFuelSwitch()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            float dist = Vector3.Distance(Transform.position, _fuelSwitchPosition);
            
            // Use tight ARRIVAL_DISTANCE to ensure we reach the correct position
            if (dist < ARRIVAL_DISTANCE)
            {
                StopMovement();
                SetPhase(OperatePhase.AddingFuel);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            TryMoveToPosition(_fuelSwitchPosition, walk: true, run: false);
            
            // Timeout
            if (Time.time - _phaseStartTime > 10f)
            {
                StopMovement();
                SetPhase(OperatePhase.AddingFuel); // Try to add anyway
            }
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Adding fuel items one at a time with animation pacing.
        /// Stays at the fuel switch until all fuel is added or smelter is full.
        /// </summary>
        private bool UpdateAddingFuel()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            StopMovement();
            
            // CRITICAL FIX: Only face target ONCE when entering this phase, not every frame!
            if (Time.time - _phaseStartTime < 0.2f)
            {
                FaceTarget(_hasFuelSwitch ? _fuelSwitchPosition : _targetPosition);
            }
            
            // Wait for animation cooldown before adding next item
            if (Time.time - _lastAnyAddTime < MIN_SINGLE_INPUT_INTERVAL)
            {
                return false;
            }
            
            // Check if we still need fuel and have fuel to add
            if (!NeedsFuel() || !HasFuel())
            {
                // Done adding fuel - move on to check if we need to add ore
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Done adding fuel - NeedsFuel={NeedsFuel()}, HasFuel={HasFuel()}");
                SetPhase(OperatePhase.FillingStation);
                return false;
            }
            
            // Try to add ONE fuel item
            if (TryAddFuel())
            {
                _itemsAdded++;
                _lastFuelAddTime = Time.time;
                _lastAnyAddTime = Time.time;
                PlayInteractAnimation();
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion.companionName} added FUEL to smelter (total: {_itemsAdded})");
            }
            else
            {
                // Failed to add fuel - move on
                SetPhase(OperatePhase.FillingStation);
            }
            
            // Stay in this phase to add more fuel
            return false;
        }
        
        #endregion
        
        #region Adding Ore
        
        /// <summary>
        /// Update phase: Moving to ore switch position.
        /// </summary>
        private bool UpdateMovingToOreSwitch()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            float dist = Vector3.Distance(Transform.position, _oreSwitchPosition);
            
            // Use tight ARRIVAL_DISTANCE to ensure we reach the correct position
            if (dist < ARRIVAL_DISTANCE)
            {
                StopMovement();
                SetPhase(OperatePhase.AddingOre);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            TryMoveToPosition(_oreSwitchPosition, walk: true, run: false);
            
            // Timeout
            if (Time.time - _phaseStartTime > 10f)
            {
                StopMovement();
                SetPhase(OperatePhase.AddingOre); // Try to add anyway
            }
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Adding ore items one at a time with animation pacing.
        /// Stays at the ore switch until all ore is added or smelter is full.
        /// </summary>
        private bool UpdateAddingOre()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            StopMovement();
            
            // CRITICAL FIX: Only face target ONCE when entering this phase, not every frame!
            if (Time.time - _phaseStartTime < 0.2f)
            {
                FaceTarget(_hasOreSwitch ? _oreSwitchPosition : _targetPosition);
            }
            
            // Wait for animation cooldown before adding next item
            if (Time.time - _lastAnyAddTime < MIN_SINGLE_INPUT_INTERVAL)
            {
                return false;
            }
            
            // Check if we can still add ore and have ore to add
            if (!CanAddOre() || !HasOre())
            {
                // Done adding ore - check what to do next
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Done adding ore - CanAddOre={CanAddOre()}, HasOre={HasOre()}");
                
                if (!CanAddOre())
                {
                    // Smelter is full
                    _smelterIsFullWaitingForOutput = true;
                    if (IsProcessing() || HasOutput())
                    {
                        SetPhase(OperatePhase.WaitingForOutput);
                    }
                    else
                    {
                        SetPhase(OperatePhase.DepositingToChests);
                    }
                }
                else
                {
                    // Out of ore - go back to decision phase (may need to pull from chests)
                    SetPhase(OperatePhase.FillingStation);
                }
                return false;
            }
            
            // Try to add ONE ore item
            if (TryAddOre())
            {
                _itemsAdded++;
                _lastOreAddTime = Time.time;
                _lastAnyAddTime = Time.time;
                PlayInteractAnimation();
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion.companionName} added ORE to smelter (total: {_itemsAdded})");
            }
            else
            {
                // Failed to add ore - move on
                SetPhase(OperatePhase.FillingStation);
            }
            
            // Stay in this phase to add more ore
            return false;
        }
        
        #endregion
    }
}
