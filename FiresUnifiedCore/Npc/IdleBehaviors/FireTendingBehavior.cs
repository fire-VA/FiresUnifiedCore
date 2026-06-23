// TODO: REMOVE AFTER TESTING V2 - This file is no longer registered in CompanionIdleBehavior.SubBehaviors.cs
// FireTendingBehaviorV2 is now used instead. Remove this file once V2 is confirmed working.
// See: CompanionIdleBehavior.SubBehaviors.cs InitializeSubBehaviors()

using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Handles companion interactions with fire sources (campfires, hearths, kilns, cooking stations).
    /// Companion will add fuel to fires and cook food.
    /// 
    /// FEATURES:
    /// - Adds wood to campfires/hearths when fuel is low
    /// - Cooks food at cooking stations
    /// - Removes cooked food from stations
    /// - Pulls fuel from companion inventory or nearby chests
    /// 
    /// SUPPORTED FIRE TYPES:
    /// - Campfire (fire_pit)
    /// - Hearth (hearth)
    /// - Bonfire (bonfire)
    /// - Cooking Station (piece_cookingstation)
    /// - Iron Cooking Station (piece_cookingstation_iron)
    /// - Cauldron (piece_cauldron)
    /// </summary>
    public class FireTendingBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "FireTending";
        
        /// <summary>
        /// Fire tending is available for idle rotation when staying.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        #region Settings
        
        private const float FIRE_DETECTION_RANGE = 10f;
        private const float INTERACTION_DISTANCE = 2f;
        private const float MIN_FUEL_TO_ADD = 5f;      // Only add fuel if below this
        private const float FUEL_TO_ADD = 10f;          // Add this much fuel at once
        private const float COOK_CHECK_INTERVAL = 2f;
        private const float MAX_TEND_TIME = 60f;
        
        #endregion
        
        #region State
        
        private enum TendPhase
        {
            FindingFire,
            MovingToFire,
            AddingFuel,
            CookingFood,
            CollectingFood,
            Complete
        }
        
        private TendPhase _currentPhase = TendPhase.FindingFire;
        private Fireplace _targetFireplace;
        private CookingStation _targetCookingStation;
        private Vector3 _targetPosition;
        private float _phaseStartTime;
        private float _lastCookCheck;
        private int _fuelAdded;
        private int _foodCooked;
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private CompanionInventory _inventory;
        private CompanionCombatMovement _combatMovement;
        private Rigidbody _rigidbody;
        private ZSyncAnimation _zanim;
        
        // Commanded target (set by ping system)
        private GameObject _commandedTarget;
        
        // Nearby chests for pulling fuel
        private List<Container> _nearbyChests = new List<Container>();
        
        #endregion
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            _zanim = companion.GetComponent<ZSyncAnimation>();
            
            MaxDuration = MAX_TEND_TIME + 30f;
        }
        
        /// <summary>
        /// Sets a specific fire/cooking station as target (used by command system).
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;
            
            // If commanded to a fire, always allow
            if (_commandedTarget != null) return true;
            
            // Otherwise, check for nearby fires that need tending
            var fireplace = FindNearbyFireplace();
            if (fireplace != null && NeedsFuel(fireplace))
            {
                // Check if we have fuel OR there's fuel in nearby chests
                if (HasFuelItem() || HasFuelInNearbyChests(fireplace.transform.position))
                {
                    return true;
                }
            }
            
            var cookingStation = FindNearbyCookingStation();
            if (cookingStation != null && HasFoodToCook()) return true;
            
            return false;
        }
        
        /// <summary>
        /// Checks if there's fuel available in nearby chests.
        /// Used to determine if fire tending is viable when companion has no fuel.
        /// </summary>
        private bool HasFuelInNearbyChests(Vector3 position)
        {
            var chests = ChestHelper.FindNearbyChests(position, ChestHelper.DEFAULT_SEARCH_RADIUS);
            if (chests.Count == 0) return false;
            
            // Check for common fuel items in chests
            foreach (string fuelName in ChestHelper.FuelItems)
            {
                if (ChestHelper.ChestsHaveItem(chests, fuelName))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = TendPhase.FindingFire;
            _phaseStartTime = Time.time;
            _fuelAdded = 0;
            _foodCooked = 0;
            _nearbyChests.Clear();
            
            if (_commandedTarget != null)
            {
                // Check if it's a fireplace or cooking station
                _targetFireplace = _commandedTarget.GetComponent<Fireplace>();
                _targetCookingStation = _commandedTarget.GetComponent<CookingStation>();
                _targetPosition = _commandedTarget.transform.position;
                
                // CRITICAL: Find nearby chests near the TARGET (fire), not near companion
                // This ensures we find chests that are close to the fireplace
                _nearbyChests = ChestHelper.FindNearbyChests(_targetPosition, ChestHelper.DEFAULT_SEARCH_RADIUS);
                
                _commandedTarget = null;
                
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                
                // Chat feedback
                CompanionChatHelper.QuickMessages.TendingFire(Companion);
            }
            else
            {
                // Not commanded - find nearest fire/cooking station
                _targetFireplace = FindNearbyFireplace();
                _targetCookingStation = FindNearbyCookingStation();
                
                if (_targetFireplace != null)
                {
                    _targetPosition = _targetFireplace.transform.position;
                    _nearbyChests = ChestHelper.FindNearbyChests(_targetPosition, ChestHelper.DEFAULT_SEARCH_RADIUS);
                }
                else if (_targetCookingStation != null)
                {
                    _targetPosition = _targetCookingStation.transform.position;
                    _nearbyChests = ChestHelper.FindNearbyChests(_targetPosition, ChestHelper.DEFAULT_SEARCH_RADIUS);
                }
            }
            
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[FireTending] {Companion.companionName} starting fire tending, found {_nearbyChests.Count} nearby chests");
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut())
            {
                Debug.Log($"[FireTending] {Companion?.companionName} TIMEOUT: Behavior exceeded max duration ({MaxDuration:F0}s), phase={_currentPhase}");
                Complete();
                return true;
            }
            
            // Show current status above companion's head
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            switch (_currentPhase)
            {
                case TendPhase.FindingFire:
                    return UpdateFindingFire();
                    
                case TendPhase.MovingToFire:
                    return UpdateMovingToFire();
                    
                case TendPhase.AddingFuel:
                    return UpdateAddingFuel();
                    
                case TendPhase.CookingFood:
                    return UpdateCookingFood();
                    
                case TendPhase.CollectingFood:
                    return UpdateCollectingFood();
                    
                case TendPhase.Complete:
                    NotifyOwner();
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            Debug.Log($"[FireTending] {Companion?.companionName} CANCELLED in phase {_currentPhase} (fuelAdded={_fuelAdded}, foodCooked={_foodCooked})");
            
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            // Release occupancy when cancelled
            if (_targetFireplace != null)
            {
                InteractableOccupancyManager.Release(_targetFireplace.gameObject, _character);
            }
            if (_targetCookingStation != null)
            {
                InteractableOccupancyManager.Release(_targetCookingStation.gameObject, _character);
            }
            
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();
            NotifyOwner();
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            return _currentPhase switch
            {
                TendPhase.FindingFire => "Looking for fire",
                TendPhase.MovingToFire => "Walking to fire",
                TendPhase.AddingFuel => "Adding fuel",
                TendPhase.CookingFood => "Cooking food",
                TendPhase.CollectingFood => "Collecting food",
                _ => "Tending fire"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateFindingFire()
        {
            // If we already have a target from Start (commanded), skip finding
            if (_targetFireplace != null || _targetCookingStation != null)
            {
                _targetPosition = _targetFireplace?.transform.position ?? _targetCookingStation.transform.position;
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // Look for fireplace that needs fuel
            _targetFireplace = FindNearbyFireplace();
            if (_targetFireplace != null && NeedsFuel(_targetFireplace))
            {
                _targetPosition = _targetFireplace.transform.position;
                _nearbyChests = ChestHelper.FindNearbyChests(_targetPosition, ChestHelper.DEFAULT_SEARCH_RADIUS);
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // Look for cooking station
            _targetCookingStation = FindNearbyCookingStation();
            if (_targetCookingStation != null)
            {
                _targetPosition = _targetCookingStation.transform.position;
                _nearbyChests = ChestHelper.FindNearbyChests(_targetPosition, ChestHelper.DEFAULT_SEARCH_RADIUS);
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // ALWAYS log why we're completing early - this is important for debugging command failures
            Debug.Log($"[FireTending] {Companion?.companionName} FAILED: No fire or cooking station found nearby");
            
            SetPhase(TendPhase.Complete);
            return true;
        }
        
        private bool UpdateMovingToFire()
        {
            Vector3 targetPos = _targetFireplace?.transform.position ?? 
                               _targetCookingStation?.transform.position ?? 
                               _targetPosition;
            
            float dist = Vector3.Distance(Transform.position, targetPos);
            
            if (dist < INTERACTION_DISTANCE)
            {
                StopMovement();
                
                // CRITICAL: Register occupancy when we arrive at the target
                GameObject targetObj = _targetFireplace?.gameObject ?? _targetCookingStation?.gameObject;
                if (targetObj != null)
                {
                    if (!InteractableOccupancyManager.TryOccupy(targetObj, _character, MAX_TEND_TIME))
                    {
                        // Someone else grabbed it first
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[FireTending] {Companion.companionName} could not occupy target - already taken");
                        SetPhase(TendPhase.Complete);
                        return true;
                    }
                    
                    // Find nearby chests if we haven't already
                    if (_nearbyChests.Count == 0)
                    {
                        _nearbyChests = ChestHelper.FindNearbyChests(targetPos, ChestHelper.DEFAULT_SEARCH_RADIUS);
                    }
                    
                    // Only pull fuel from chests if companion doesn't have any in their own inventory
                    if (!HasFuelItem() && _nearbyChests.Count > 0 && _inventory != null)
                    {
                        PullFuelFromChests();
                    }
                    else if (HasFuelItem() && CompanionIdleBehavior.VerboseLogging)
                    {
                        Debug.Log($"[FireTending] {Companion.companionName} has fuel in own inventory, using that first");
                    }
                }
                
                // Decide what to do
                if (_targetFireplace != null && NeedsFuel(_targetFireplace))
                {
                    SetPhase(TendPhase.AddingFuel);
                }
                else if (_targetCookingStation != null)
                {
                    if (HasCookedFood(_targetCookingStation))
                    {
                        SetPhase(TendPhase.CollectingFood);
                    }
                    else if (HasFoodToCook())
                    {
                        SetPhase(TendPhase.CookingFood);
                    }
                    else
                    {
                        SetPhase(TendPhase.Complete);
                    }
                }
                else
                {
                    SetPhase(TendPhase.Complete);
                }
                
                return false;
            }
            
            // Timeout - couldn't reach the fire via pathfinding
            // Use a longer timeout to give pathfinding a fair chance
            // 20 seconds should be enough for most paths, 30 seconds for complex terrain
            float pathfindingTimeout = dist > 15f ? 30f : 20f;
            
            if (Time.time - _phaseStartTime > pathfindingTimeout)
            {
                Debug.Log($"[FireTending] {Companion?.companionName} pathfinding timeout after {pathfindingTimeout}s - TELEPORTING near fire (distance: {dist:F1}m)");
                
                // CRITICAL: Teleport NEAR the fire, not ON TOP of it!
                // Find a safe position around the fire at interaction distance
                Vector3 teleportPos = FindSafeTeleportPosition(targetPos);
                
                // Teleport the companion
                Transform.position = teleportPos;
                
                // Also update the ZDO position for network sync
                var nview = Companion?.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    nview.GetZDO().SetPosition(teleportPos);
                }
                
                // Reset pathfinding state in AI to avoid stuck detection issues
                var companionAI = Companion.GetCompanionAI();
                companionAI?.ResetPathfindingState();
                
                // Reset phase timer since we just teleported
                _phaseStartTime = Time.time;
                
                // Notify the player that we had to teleport
                var owner = Companion?.GetOwner();
                if (owner != null && owner == Player.m_localPlayer)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"{Companion.GetDisplayName()}: Couldn't find path - teleported near fire");
                }
            }
            
            return false;
        }
        
        private bool UpdateAddingFuel()
        {
            if (_targetFireplace == null)
            {
                Debug.Log($"[FireTending] {Companion?.companionName} COMPLETE: Target fireplace was destroyed or lost");
                SetPhase(TendPhase.Complete);
                return true;
            }
            
            StopMovement();
            FaceTarget(_targetFireplace.transform.position);
            
            // Try to pull more fuel from chests if we're running low
            if (!HasFuelItem() && _nearbyChests.Count > 0)
            {
                PullFuelFromChests();
            }
            
            // Try to add fuel
            if (TryAddFuel())
            {
                _fuelAdded++;
                PlayInteractAnimation();
                
                // Check if fire is full or we're out of fuel
                if (!NeedsFuel(_targetFireplace) || !HasFuelItem())
                {
                    // Try to get more fuel from chests before giving up
                    if (!HasFuelItem() && _nearbyChests.Count > 0)
                    {
                        PullFuelFromChests();
                    }
                    
                    // If still no fuel, move on
                    if (!HasFuelItem())
                    {
                        // Move on to cooking if we have food
                        if (_targetCookingStation != null && (HasFoodToCook() || HasCookedFood(_targetCookingStation)))
                        {
                            SetPhase(TendPhase.MovingToFire);
                            _targetFireplace = null;
                        }
                        else
                        {
                            Debug.Log($"[FireTending] {Companion?.companionName} COMPLETE: Fire full or no more fuel (added {_fuelAdded} fuel)");
                            SetPhase(TendPhase.Complete);
                        }
                    }
                }
            }
            else
            {
                // Can't add fuel, move on - LOG THE REASON
                bool hasFuel = HasFuelItem();
                bool needsFuel = _targetFireplace != null && NeedsFuel(_targetFireplace);
                Debug.Log($"[FireTending] {Companion?.companionName} COMPLETE: Cannot add fuel (hasFuel={hasFuel}, needsFuel={needsFuel}, fuelAdded={_fuelAdded})");
                SetPhase(TendPhase.Complete);
            }
            
            return false;
        }
        
        private bool UpdateCookingFood()
        {
            if (_targetCookingStation == null)
            {
                SetPhase(TendPhase.Complete);
                return true;
            }
            
            StopMovement();
            FaceTarget(_targetCookingStation.transform.position);
            
            // Check for cooked food periodically
            if (Time.time - _lastCookCheck >= COOK_CHECK_INTERVAL)
            {
                _lastCookCheck = Time.time;
                
                // Try to add food to station
                if (HasFoodToCook() && CanAddToStation(_targetCookingStation))
                {
                    TryAddFoodToStation();
                    PlayInteractAnimation();
                }
                
                // Check for cooked food to collect
                if (HasCookedFood(_targetCookingStation))
                {
                    SetPhase(TendPhase.CollectingFood);
                    return false;
                }
            }
            
            // Timeout on cooking
            if (Time.time - _phaseStartTime > 30f)
            {
                SetPhase(TendPhase.Complete);
            }
            
            return false;
        }
        
        private bool UpdateCollectingFood()
        {
            if (_targetCookingStation == null)
            {
                SetPhase(TendPhase.Complete);
                return true;
            }
            
            StopMovement();
            FaceTarget(_targetCookingStation.transform.position);
            
            // Collect cooked food
            if (TryCollectCookedFood())
            {
                _foodCooked++;
                PlayInteractAnimation();
            }
            
            // Check if there's more food or we should finish
            if (!HasCookedFood(_targetCookingStation))
            {
                if (HasFoodToCook() && CanAddToStation(_targetCookingStation))
                {
                    SetPhase(TendPhase.CookingFood);
                }
                else
                {
                    SetPhase(TendPhase.Complete);
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Fire Interaction
        
        private Fireplace FindNearbyFireplace()
        {
            Fireplace nearest = null;
            float nearestDist = float.MaxValue;
            
            // Use SearchCenter for staying companions (searches around home position)
            float searchRadius = GetEffectiveSearchRadius(FIRE_DETECTION_RANGE);
            var colliders = Physics.OverlapSphere(SearchCenter, searchRadius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var fireplace = col.GetComponent<Fireplace>() ?? col.GetComponentInParent<Fireplace>();
                if (fireplace == null) continue;
                
                // Check if fireplace is already being used by another companion or player
                if (!InteractableOccupancyManager.CanUseInteractable(fireplace.gameObject, _character))
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[FireTending] {Companion?.companionName} skipping fireplace - occupied or crowded");
                    continue;
                }
                
                float dist = Vector3.Distance(Transform.position, fireplace.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = fireplace;
                }
            }
            
            return nearest;
        }
        
        private CookingStation FindNearbyCookingStation()
        {
            CookingStation nearest = null;
            float nearestDist = float.MaxValue;
            
            // Use SearchCenter for staying companions (searches around home position)
            float searchRadius = GetEffectiveSearchRadius(FIRE_DETECTION_RANGE);
            var colliders = Physics.OverlapSphere(SearchCenter, searchRadius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var station = col.GetComponent<CookingStation>() ?? col.GetComponentInParent<CookingStation>();
                if (station == null) continue;
                
                // Check if cooking station is already being used by another companion or player
                if (!InteractableOccupancyManager.CanUseInteractable(station.gameObject, _character))
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[FireTending] {Companion?.companionName} skipping cooking station - occupied or crowded");
                    continue;
                }
                
                float dist = Vector3.Distance(Transform.position, station.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = station;
                }
            }
            
            return nearest;
        }
        
        private bool NeedsFuel(Fireplace fireplace)
        {
            if (fireplace == null) return false;
            
            // Check current fuel level via ZDO
            var nview = fireplace.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // Fireplace stores fuel in ZDOVars.s_fuel
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            float maxFuel = fireplace.m_maxFuel;
            
            // Only add fuel if below half
            return currentFuel < maxFuel * 0.5f;
        }
        
        private bool HasFuelItem()
        {
            if (_inventory == null) return false;
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;
            
            // Get the fuel item name from the target fireplace if available
            string fuelItemName = "Wood";
            if (_targetFireplace != null && _targetFireplace.m_fuelItem != null)
            {
                fuelItemName = _targetFireplace.m_fuelItem.m_itemData.m_shared.m_name;
            }
            
            // Look for the fuel item in storage
            return storage.HaveItem(fuelItemName);
        }
        
        private bool TryAddFuel()
        {
            if (_targetFireplace == null || _inventory == null || _humanoid == null) return false;
            
            // Check if fireplace can be refilled
            if (!_targetFireplace.m_canRefill || _targetFireplace.m_infiniteFuel) return false;
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;
            
            // Get the fuel item type from the fireplace
            if (_targetFireplace.m_fuelItem == null) return false;
            string fuelPrefabName = _targetFireplace.m_fuelItem.gameObject.name;
            string fuelItemName = _targetFireplace.m_fuelItem.m_itemData.m_shared.m_name;
            
            // Find fuel in inventory
            ItemDrop.ItemData fuelItem = storage.GetItem(fuelItemName);
            if (fuelItem == null) return false;
            
            // Check if fireplace is full
            var nview = _targetFireplace.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            if (currentFuel >= _targetFireplace.m_maxFuel) return false;
            
            // Use RPC to add fuel - this is how Valheim does it
            nview.InvokeRPC("RPC_AddFuel");
            storage.RemoveOneItem(fuelItem);
            _inventory.SaveToZDO();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[FireTending] {Companion.companionName} added fuel to fire via RPC");
            
            return true;
        }
        
        /// <summary>
        /// Pulls fuel items from nearby chests into companion's inventory.
        /// </summary>
        private void PullFuelFromChests()
        {
            if (_nearbyChests.Count == 0 || _inventory == null) return;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return;
            
            // Determine what fuel type is needed
            string fuelItemName = "Wood"; // Default
            if (_targetFireplace != null && _targetFireplace.m_fuelItem != null)
            {
                fuelItemName = _targetFireplace.m_fuelItem.gameObject.name;
            }
            
            // Try to pull the specific fuel type first
            int pulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, fuelItemName, 10);
            
            // If that didn't work, try common fuel items
            if (pulled == 0)
            {
                foreach (string fuelName in ChestHelper.FuelItems)
                {
                    pulled = ChestHelper.PullItemsByPrefabName(_nearbyChests, storageInv, fuelName, 10);
                    if (pulled > 0) break;
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging && pulled > 0)
                Debug.Log($"[FireTending] {Companion.companionName} pulled {pulled} fuel from chests");
        }
        
        #endregion
        
        #region Cooking
        
        private bool HasFoodToCook()
        {
            if (_inventory == null) return false;
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;
            
            foreach (var item in storage.GetAllItems())
            {
                // Check if item is cookable
                if (IsCookable(item))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private bool IsCookable(ItemDrop.ItemData item)
        {
            if (item == null || _targetCookingStation == null) return false;
            
            // Check against the cooking station's conversion list
            string itemPrefabName = item.m_dropPrefab?.name ?? "";
            
            foreach (var conversion in _targetCookingStation.m_conversion)
            {
                if (conversion.m_from != null && 
                    conversion.m_from.gameObject.name == itemPrefabName)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private bool CanAddToStation(CookingStation station)
        {
            if (station == null) return false;
            
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // Check each slot for an empty one
            for (int i = 0; i < station.m_slots.Length; i++)
            {
                string slotItem = nview.GetZDO().GetString("slot" + i);
                if (string.IsNullOrEmpty(slotItem))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private bool HasCookedFood(CookingStation station)
        {
            if (station == null) return false;
            
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // CookingStation stores slots as "slot0", "slot1", etc.
            // Status is stored as "slotstatus0", etc. (0=NotDone, 1=Done, 2=Burnt)
            try
            {
                for (int i = 0; i < station.m_slots.Length; i++)
                {
                    string slotItem = nview.GetZDO().GetString("slot" + i);
                    if (!string.IsNullOrEmpty(slotItem))
                    {
                        // Check status - 1 = Done, 2 = Burnt (both are collectible)
                        int status = nview.GetZDO().GetInt("slotstatus" + i, 0);
                        if (status >= 1)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            
            return false;
        }
        
        private void TryAddFoodToStation()
        {
            if (_targetCookingStation == null || _inventory == null || _humanoid == null) return;
            
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return;
            
            // Find cookable item
            ItemDrop.ItemData foodItem = null;
            foreach (var item in storage.GetAllItems())
            {
                if (IsCookable(item))
                {
                    foodItem = item;
                    break;
                }
            }
            
            if (foodItem == null) return;
            
            // Try to use the cooking station with this item
            if (_targetCookingStation.UseItem(_humanoid, foodItem))
            {
                storage.RemoveOneItem(foodItem);
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[FireTending] {Companion.companionName} added {foodItem.m_shared.m_name} to cooking station");
            }
        }
        
        private bool TryCollectCookedFood()
        {
            if (_targetCookingStation == null || _inventory == null || _humanoid == null) return false;
            
            // Use the cooking station to collect food
            if (_targetCookingStation.Interact(_humanoid, false, false))
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[FireTending] {Companion.companionName} collected cooked food");
                return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Helpers
        
        private void NotifyOwner()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            // Release occupancy when done
            if (_targetFireplace != null)
            {
                InteractableOccupancyManager.Release(_targetFireplace.gameObject, _character);
            }
            if (_targetCookingStation != null)
            {
                InteractableOccupancyManager.Release(_targetCookingStation.gameObject, _character);
            }
            
            var owner = Companion?.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            // If we did work, report success
            if (_fuelAdded > 0 || _foodCooked > 0)
            {
                string message = "";
                if (_fuelAdded > 0 && _foodCooked > 0)
                {
                    message = $"{Companion.GetDisplayName()} added fuel and cooked {_foodCooked} items";
                }
                else if (_fuelAdded > 0)
                {
                    message = $"{Companion.GetDisplayName()} tended the fire";
                }
                else
                {
                    message = $"{Companion.GetDisplayName()} cooked {_foodCooked} items";
                }
                
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
            else
            {
                // We completed without doing any work - explain why to the player
                string failReason = GetFailureReason();
                if (!string.IsNullOrEmpty(failReason))
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"{Companion.GetDisplayName()}: {failReason}");
                }
            }
        }
        
        /// <summary>
        /// Determines why the fire tending task couldn't be completed.
        /// </summary>
        private string GetFailureReason()
        {
            if (_targetFireplace == null && _targetCookingStation == null)
            {
                return "No fire found nearby";
            }
            
            if (_targetFireplace != null)
            {
                if (!NeedsFuel(_targetFireplace))
                {
                    return "Fire is already full of fuel";
                }
                
                if (!HasFuelItem())
                {
                    return "I don't have any fuel (wood)";
                }
            }
            
            if (_targetCookingStation != null)
            {
                if (!HasFoodToCook() && !HasCookedFood(_targetCookingStation))
                {
                    return "No food to cook";
                }
            }
            
            return "Couldn't complete the task";
        }
        
        private void PlayInteractAnimation()
        {
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        private void SetPhase(TendPhase phase)
        {
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }
        
        private void MoveToPosition(Vector3 position)
        {
            // CRITICAL: Use CompanionCombatMovement.SetMoveDestination() for proper AI pathfinding
            // This integrates with CompanionAI's pathfinding loop which handles continuous movement
            // and obstacle avoidance. Direct SetMoveDir() only works for single frames.
            
            // Store target position for distance checks in Update
            _targetPosition = position;
            
            // Use CompanionCombatMovement for proper AI-driven pathfinding
            // WALKING for idle behaviors - no need to sprint
            // CRITICAL: skipCommandOverride=true to avoid cancelling the parent SubBehavior command
            if (_combatMovement != null)
            {
                _combatMovement.SetMoveDestination(position, useWalk: true, skipCommandOverride: true);
            }
            else
            {
                // Fallback: direct movement (won't persist across frames, but better than nothing)
                if (_character != null)
                {
                    _character.SetWalk(true);
                    _character.SetRun(false);
                    
                    Vector3 direction = (position - Transform.position).normalized;
                    direction.y = 0;
                    
                    if (direction.sqrMagnitude > 0.01f)
                    {
                        _character.SetMoveDir(direction);
                    }
                }
            }
        }
        
        private new void StopMovement()
        {
            // Clear combat movement destination
            if (_combatMovement != null)
            {
                _combatMovement.ClearMoveDestination();
            }
            
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
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
        /// Finds a safe position near the fire to teleport to.
        /// Avoids teleporting directly onto the fire (which would cause damage).
        /// </summary>
        private Vector3 FindSafeTeleportPosition(Vector3 firePos)
        {
            // Try multiple directions around the fire at interaction distance
            // Start with cardinal directions, then diagonals
            Vector3[] directions = new Vector3[]
            {
                Vector3.forward,
                Vector3.back,
                Vector3.left,
                Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized
            };
            
            float safeDistance = INTERACTION_DISTANCE + 0.5f; // Slightly beyond interaction distance
            
            foreach (var dir in directions)
            {
                Vector3 testPos = firePos + dir * safeDistance;
                
                // Get ground height at this position
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                    {
                        testPos.y = groundHeight + 0.1f; // Slightly above ground
                    }
                }
                
                // Check if position is safe (no collisions, not in fire)
                if (IsPositionSafe(testPos, firePos))
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[FireTending] Found safe teleport position at {testPos} (dir: {dir})");
                    return testPos;
                }
            }
            
            // Fallback: use a position further away if close positions are blocked
            for (float distance = 3f; distance <= 5f; distance += 1f)
            {
                foreach (var dir in directions)
                {
                    Vector3 testPos = firePos + dir * distance;
                    
                    if (ZoneSystem.instance != null)
                    {
                        float groundHeight;
                        if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                        {
                            testPos.y = groundHeight + 0.1f;
                        }
                    }
                    
                    if (IsPositionSafe(testPos, firePos))
                    {
                        Debug.Log($"[FireTending] Using fallback teleport position at distance {distance}m");
                        return testPos;
                    }
                }
            }
            
            // Last resort: just offset from fire in a random direction
            Vector3 fallbackDir = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0, UnityEngine.Random.Range(-1f, 1f)).normalized;
            Vector3 fallbackPos = firePos + fallbackDir * 3f;
            
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(fallbackPos, out groundHeight))
                {
                    fallbackPos.y = groundHeight + 0.1f;
                }
            }
            
            Debug.LogWarning($"[FireTending] Using last-resort teleport position (no safe spots found)");
            return fallbackPos;
        }
        
        /// <summary>
        /// Checks if a position is safe to teleport to.
        /// </summary>
        private bool IsPositionSafe(Vector3 pos, Vector3 firePos)
        {
            // Check we're not too close to the fire (would take damage)
            float distToFire = Vector3.Distance(pos, firePos);
            if (distToFire < 1.5f)
                return false;
            
            // Check for physical obstructions (walls, objects)
            // Use a small sphere check to see if position is clear
            Collider[] colliders = Physics.OverlapSphere(pos + Vector3.up * 0.5f, 0.4f);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.isTrigger) continue;
                
                // Ignore terrain
                if (col.gameObject.layer == LayerMask.NameToLayer("terrain"))
                    continue;
                
                // Ignore the fire itself
                if (col.GetComponent<Fireplace>() != null || col.GetComponentInParent<Fireplace>() != null)
                    continue;
                    
                // Ignore cooking stations
                if (col.GetComponent<CookingStation>() != null || col.GetComponentInParent<CookingStation>() != null)
                    continue;
                
                // Found an obstruction
                return false;
            }
            
            // Check that we can actually stand on the ground here
            // Raycast down to verify ground exists
            if (!Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, 4f))
                return false;
            
            return true;
        }
        
        #endregion
    }
}
