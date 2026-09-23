using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Fire tending behavior - adds fuel to campfires/hearths.
    /// 
    /// CORRECT FLOW:
    /// 1. Check inventory for fuel
    /// 2. If no fuel, check nearby chests
    /// 3. If fuel in chest, go to chest and get it
    /// 4. Go to fire
    /// 5. Add fuel
    /// 6. Complete
    /// </summary>
    public class FireTendingBehaviorV2 : WorkBehaviorBase<FireTendingBehaviorV2.TendPhase>
    {
        public override string BehaviorName => "FireTending";
        public override bool AvailableForIdleRotation => true;
        public override int InventoryPriority => 10;
        
        #region Phase Enum
        
        public enum TendPhase
        {
            CheckingResources,      // Check inventory and chests for fuel
            MovingToChest,          // Walking to chest to get fuel
            RetrievingFuel,         // Taking fuel from chest
            MovingToFire,           // Walking to fire
            AddingFuel,             // Adding fuel to fire
            CookingFood,
            CollectingFood,
            Complete
        }
        
        #endregion
        
        #region Settings
        
        private const float FireDetectionRange = 10f;
        private const float InteractionDistance = 2f;
        private const float CookCheckInterval = 2f;
        private const float MaxTendTime = 120f;
        private const float FuelAddInterval = 1f;
        private const float RefuelBelowFuelFraction = 0.7f;
        private const float CookTimeoutMargin = 5f;
        
        #endregion
        
        #region State
        
        private Fireplace _targetFireplace;
        private CookingStation _targetCookingStation;
        private Vector3 _targetPosition;
        private float _lastCookCheck;
        private float _nextFuelAddTime;
        private int _fuelAdded;
        private int _foodCooked;
        private readonly CompanionCookingBehavior.FinishedFoodCollector _foodCollector = new CompanionCookingBehavior.FinishedFoodCollector();
        
        // Chest retrieval state
        private Container _fuelChest;
        private string _fuelItemName;
        
        // Commanded target (set by ping system)
        private GameObject _commandedTarget;
        
        #endregion
        
        #region WorkBehaviorBase Implementation
        
        protected override TendPhase InitialPhase => TendPhase.CheckingResources;
        
        protected override float GetPhaseTimeout(TendPhase phase)
        {
            return phase switch
            {
                TendPhase.CheckingResources => 5f,
                TendPhase.MovingToChest => 30f,
                TendPhase.RetrievingFuel => 10f,
                TendPhase.MovingToFire => 30f,
                TendPhase.AddingFuel => 20f,
                TendPhase.CookingFood => CompanionCookingBehavior.SlowestCookTime(_targetCookingStation) + CookTimeoutMargin,
                TendPhase.CollectingFood => 15f,
                _ => 10f
            };
        }
        
        protected override string GetPhaseDescription(TendPhase phase)
        {
            return phase switch
            {
                TendPhase.CheckingResources => "Checking for fuel",
                TendPhase.MovingToChest => "Getting fuel from chest",
                TendPhase.RetrievingFuel => "Taking fuel",
                TendPhase.MovingToFire => "Walking to fire",
                TendPhase.AddingFuel => "Adding fuel",
                TendPhase.CookingFood => "Cooking food",
                TendPhase.CollectingFood => "Collecting food",
                _ => "Tending fire"
            };
        }
        
        protected override TendPhase OnPhaseTimeout(TendPhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");
            return TendPhase.Complete;
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MaxTendTime;
        }
        
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;

            // If commanded to a fire, always allow
            if (_commandedTarget != null) 
            {
                LogVerbose($"CanStart: TRUE - has commanded target");
                return true;
            }

            // Autonomous fire tending is gated by the radial-menu toggle.
            if (!CompanionBehaviorToggles.IsFiresEnabled(Companion)) return false;
            
            // Find a nearby fire that needs tending
            var fireplace = FindFireNeedingFuel();
            if (fireplace != null)
            {
                LogVerbose($"CanStart: TRUE - found fire needing {fireplace.m_fuelItem.gameObject.name}, have fuel available");
                return true;
            }
            
            var cookingStation = FindNearbyCookingStation();
            if (cookingStation != null && HasFoodToCook(cookingStation))
            {
                LogVerbose($"CanStart: TRUE - found cooking station and have food");
                return true;
            }
            
            return false;
        }
        
        public override void Start()
        {
            base.Start();
            
            _fuelAdded = 0;
            _foodCooked = 0;
            _nextFuelAddTime = 0f;
            _foodCollector.Begin();
            _fuelChest = null;
            _fuelItemName = "Wood";
            
            // Get target fire
            if (_commandedTarget != null)
            {
                _targetFireplace = _commandedTarget.GetComponent<Fireplace>();
                var commandedStation = _commandedTarget.GetComponent<CookingStation>();
                _targetCookingStation = commandedStation != null && CompanionCookingBehavior.TakesFoodDirectly(commandedStation)
                    ? commandedStation
                    : null;
                _targetPosition = GroundedPosition(_commandedTarget.transform.position);
                _commandedTarget = null;
            }
            else
            {
                _targetFireplace = FindFireNeedingFuel();
                _targetCookingStation = FindNearbyCookingStation();

                if (_targetFireplace != null)
                    _targetPosition = GroundedPosition(_targetFireplace.transform.position);
                else if (_targetCookingStation != null)
                    _targetPosition = _targetCookingStation.transform.position;
            }
            
            // Determine fuel type
            if (_targetFireplace?.m_fuelItem != null)
                _fuelItemName = _targetFireplace.m_fuelItem.gameObject.name;

            // EARLY RESERVATION: Claim the fire/station immediately so other companions
            // evaluating CanStart this frame won't all converge on it.
            GameObject targetObj = _targetFireplace?.gameObject ?? _targetCookingStation?.gameObject;
            if (targetObj != null && !InteractableOccupancyManager.TryOccupy(targetObj, Character, MaxTendTime))
            {
                LogVerbose("Could not reserve fire/station at Start - already taken");
                _targetFireplace = null;
                _targetCookingStation = null;
                SetPhase(TendPhase.Complete);
                return;
            }

            // Refresh chests
            Resources.RefreshNearbyChests(true);

            LogVerbose($"Starting fire tending. Target={_targetFireplace?.name ?? _targetCookingStation?.name ?? "none"}, ChestCount={Resources.ChestCount}");

            // Start with checking resources
            SetPhase(TendPhase.CheckingResources);

            CompanionChatHelper.QuickMessages.TendingFire(Companion);
        }
        
        protected override bool UpdatePhase(TendPhase phase)
        {
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            return phase switch
            {
                TendPhase.CheckingResources => UpdateCheckingResources(),
                TendPhase.MovingToChest => UpdateMovingToChest(),
                TendPhase.RetrievingFuel => UpdateRetrievingFuel(),
                TendPhase.MovingToFire => UpdateMovingToFire(),
                TendPhase.AddingFuel => UpdateAddingFuel(),
                TendPhase.CookingFood => UpdateCookingFood(),
                TendPhase.CollectingFood => UpdateCollectingFood(),
                TendPhase.Complete => CompleteAndNotify(),
                _ => true
            };
        }
        
        public override void Cancel()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            ReleaseOccupancy();
            base.Cancel();
        }
        
        #endregion
        
        #region Phase Updates
        
        /// <summary>
        /// STEP 1: Check inventory for fuel. If none, check chests.
        /// </summary>
        private bool UpdateCheckingResources()
        {
            if (_targetFireplace == null && _targetCookingStation == null)
            {
                LogVerbose("No target fire/station found");
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            // For cooking station, go directly to it
            if (_targetCookingStation != null && _targetFireplace == null)
            {
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // Check if fire needs fuel
            if (_targetFireplace != null && !NeedsFuel(_targetFireplace))
            {
                LogVerbose("Fire doesn't need fuel");
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            // STEP 1: Check inventory for fuel
            if (HasFuelItem())
            {
                LogVerbose($"Have {_fuelItemName} in inventory - going to fire");
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // STEP 2: Check chests for fuel
            _fuelChest = FindChestWithFuel(_fuelItemName);
            if (_fuelChest != null)
            {
                LogVerbose($"Found {_fuelItemName} in chest at {_fuelChest.transform.position} - going to get it");
                SetPhase(TendPhase.MovingToChest);
                
                Vector3 chestInteractionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                    _fuelChest, Transform.position, InteractionDistance);
                MoveToPosition(chestInteractionPoint);
                return false;
            }
            
            // No fuel available anywhere
            LogVerbose("No fuel available in inventory or chests");
            NotifyNoFuelAvailable();
            SetPhase(TendPhase.Complete);
            return false;
        }
        
        /// <summary>
        /// STEP 2: Walk to chest containing fuel.
        /// </summary>
        private bool UpdateMovingToChest()
        {
            if (_fuelChest == null)
            {
                LogVerbose("Fuel chest was destroyed");
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            if (ContinueMovement())
            {
                StopMovement();
                SetPhase(TendPhase.RetrievingFuel);
            }
            
            return false;
        }
        
        /// <summary>
        /// STEP 3: Take fuel from chest.
        /// </summary>
        private bool UpdateRetrievingFuel()
        {
            if (_fuelChest == null)
            {
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            StopMovement();
            FaceTarget(_fuelChest.transform.position);
            
            // Play animation
            if (TimeInCurrentPhase < 0.1f)
            {
                PlayInteractAnimation();
                return false;
            }
            
            // Wait for animation
            if (TimeInCurrentPhase < 0.5f)
                return false;
            
            // Pull fuel from chest
            int pulled = PullFuelFromChest(_fuelChest, 10);
            
            if (pulled > 0)
            {
                LogVerbose($"Pulled {pulled}x {_fuelItemName} from chest");
                CompanionEvents.FireItemPulled(Companion, _fuelItemName, pulled);
                
                // Now go to fire
                SetPhase(TendPhase.MovingToFire);
                MoveToPosition(_targetPosition);
            }
            else
            {
                LogVerbose("Failed to pull fuel from chest");
                SetPhase(TendPhase.Complete);
            }
            
            _fuelChest = null;
            return false;
        }
        
        /// <summary>
        /// STEP 4: Walk to fire.
        /// </summary>
        private bool UpdateMovingToFire()
        {
            if (_targetFireplace == null && _targetCookingStation == null)
            {
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            if (ContinueMovement())
            {
                StopMovement();
                
                // Register occupancy
                GameObject targetObj = _targetFireplace?.gameObject ?? _targetCookingStation?.gameObject;
                if (targetObj != null && !InteractableOccupancyManager.TryOccupy(targetObj, Character, MaxTendTime))
                {
                    LogVerbose("Fire occupied by another companion");
                    SetPhase(TendPhase.Complete);
                    return false;
                }
                
                // Decide what to do
                if (_targetFireplace != null && NeedsFuel(_targetFireplace))
                {
                    SetPhase(TendPhase.AddingFuel);
                }
                else if (_targetCookingStation != null)
                {
                    if (CompanionCookingBehavior.HasFinishedFood(_targetCookingStation))
                        SetPhase(TendPhase.CollectingFood);
                    else if (HasFoodToCook(_targetCookingStation))
                        SetPhase(TendPhase.CookingFood);
                    else
                        SetPhase(TendPhase.Complete);
                }
                else
                {
                    SetPhase(TendPhase.Complete);
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// STEP 5: Add fuel to fire.
        /// </summary>
        private bool UpdateAddingFuel()
        {
            if (_targetFireplace == null)
            {
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetFireplace.transform.position);

            if (Time.time < _nextFuelAddTime)
                return false;
            _nextFuelAddTime = Time.time + FuelAddInterval;

            if (TryAddFuel())
            {
                _fuelAdded++;
                PlayInteractAnimation();
                CompanionEvents.FireResourceGathered(Companion, _fuelItemName, 1);
                
                // Check if done
                if (!NeedsFuel(_targetFireplace))
                {
                    LogVerbose($"Fire is full (added {_fuelAdded} fuel)");
                    SetPhase(TendPhase.Complete);
                }
                else if (!HasFuelItem())
                {
                    // Need more fuel - check chests again
                    _fuelChest = FindChestWithFuel(_fuelItemName);
                    if (_fuelChest != null)
                    {
                        SetPhase(TendPhase.MovingToChest);
                        Vector3 chestInteractionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                            _fuelChest, Transform.position, InteractionDistance);
                        MoveToPosition(chestInteractionPoint);
                    }
                    else
                    {
                        LogVerbose($"Out of fuel (added {_fuelAdded})");
                        SetPhase(TendPhase.Complete);
                    }
                }
            }
            else
            {
                LogVerbose($"Cannot add fuel (hasFuel={HasFuelItem()})");
                SetPhase(TendPhase.Complete);
            }
            
            return false;
        }
        
        private bool UpdateCookingFood()
        {
            if (_targetCookingStation == null)
            {
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetCookingStation.transform.position);
            
            if (Time.time - _lastCookCheck >= CookCheckInterval)
            {
                _lastCookCheck = Time.time;
                
                if (HasFoodToCook(_targetCookingStation) && CompanionCookingBehavior.HasFreeSlot(_targetCookingStation) && TimeToCookAnother())
                {
                    TryAddFoodToStation();
                    PlayInteractAnimation();
                }

                if (CompanionCookingBehavior.HasFinishedFood(_targetCookingStation))
                    SetPhase(TendPhase.CollectingFood);
            }
            
            return false;
        }
        
        /// <summary>A batch put on now can cook and be collected before the behaviour's time runs out; one put on later would burn.</summary>
        private bool TimeToCookAnother() =>
            Time.time - StartTime + CompanionCookingBehavior.SlowestCookTime(_targetCookingStation) + CookTimeoutMargin <= MaxDuration;

        private bool UpdateCollectingFood()
        {
            if (_targetCookingStation == null)
            {
                SetPhase(TendPhase.Complete);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetCookingStation.transform.position);
            
            if (!CompanionCookingBehavior.HasFinishedFood(_targetCookingStation) && !_foodCollector.HasDropsOnGround)
            {
                if (HasFoodToCook(_targetCookingStation) && CompanionCookingBehavior.HasFreeSlot(_targetCookingStation) && TimeToCookAnother())
                    SetPhase(TendPhase.CookingFood);
                else
                    SetPhase(TendPhase.Complete);
                return false;
            }

            if (!CompanionCookingBehavior.OwnStation(_targetCookingStation))
                return false;

            if (_foodCollector.PickUpDrops(_targetCookingStation, GetStorageInventory()) > 0)
                SaveInventory();

            if (_foodCollector.CollectOne(_targetCookingStation, Humanoid))
            {
                _foodCooked++;
                PlayInteractAnimation();
            }

            return false;
        }
        
        private bool CompleteAndNotify()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            ReleaseOccupancy();
            NotifyOwner();
            Complete();
            return true;
        }
        
        #endregion
        
        #region Fuel Management
        
        private bool HasFuelItem()
        {
            return HasFuelItemOfType(_fuelItemName);
        }

        private bool HasFuelItemOfType(string fuelName)
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
            {
                if (item == null) continue;
                if ((item.m_dropPrefab?.name ?? "").Equals(fuelName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool CanGetFuel(string fuelName)
        {
            return HasFuelItemOfType(fuelName) || FindChestWithFuel(fuelName) != null;
        }

        private Container FindChestWithFuel(string fuelName)
        {
            foreach (var chest in Resources.NearbyChests)
            {
                if (chest != null && ChestHelper.CountPrefabInInventory(chest.GetInventory(), fuelName) > 0)
                    return chest;
            }
            return null;
        }
        
        private int PullFuelFromChest(Container chest, int amount)
        {
            if (chest == null) return 0;
            
            var chestInv = chest.GetInventory();
            var storage = GetStorageInventory();
            if (chestInv == null || storage == null) return 0;
            
            int pulled = 0;
            var items = new List<ItemDrop.ItemData>(chestInv.GetAllItems());
            
            foreach (var item in items)
            {
                if (item == null || pulled >= amount) continue;
                
                string prefab = item.m_dropPrefab?.name ?? "";
                if (!prefab.Equals(_fuelItemName, System.StringComparison.OrdinalIgnoreCase)) continue;
                
                int toPull = Mathf.Min(item.m_stack, amount - pulled);
                for (int i = 0; i < toPull; i++)
                {
                    var clone = item.Clone();
                    clone.m_stack = 1;
                    if (storage.AddItem(clone))
                    {
                        chestInv.RemoveOneItem(item);
                        pulled++;
                    }
                    else break;
                }
            }
            
            if (pulled > 0)
                Inventory?.SaveToZDO();
            
            return pulled;
        }
        
        private bool TryAddFuel()
        {
            if (_targetFireplace == null) return false;
            if (!_targetFireplace.m_canRefill || _targetFireplace.m_infiniteFuel) return false;
            
            var storage = GetStorageInventory();
            if (storage == null) return false;
            
            // Find fuel in inventory
            ItemDrop.ItemData fuelItem = null;
            foreach (var item in storage.GetAllItems())
            {
                if (item == null) continue;
                string prefab = item.m_dropPrefab?.name ?? "";
                if (prefab.Equals(_fuelItemName, System.StringComparison.OrdinalIgnoreCase))
                {
                    fuelItem = item;
                    break;
                }
            }
            
            if (fuelItem == null) return false;
            
            var nview = _targetFireplace.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            // RPC_AddFuel only runs on an owner (Fireplace.cs:350); vanilla Interact claims an unowned fire the same way.
            if (!nview.HasOwner()) nview.ClaimOwnership();

            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            if (Mathf.CeilToInt(currentFuel) >= _targetFireplace.m_maxFuel) return false;
            
            // Remove from inventory FIRST
            if (!storage.RemoveOneItem(fuelItem)) return false;
            
            nview.InvokeRPC("RPC_AddFuel");
            SaveInventory();
            
            LogVerbose($"Added {_fuelItemName} to fire");
            return true;
        }
        
        private bool NeedsFuel(Fireplace fireplace)
        {
            if (fireplace == null || !fireplace.m_canRefill || fireplace.m_infiniteFuel || fireplace.m_fuelItem == null) return false;
            var nview = fireplace.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            return currentFuel < fireplace.m_maxFuel * RefuelBelowFuelFraction;
        }
        
        private void NotifyNoFuelAvailable()
        {
            var owner = Companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                    $"{Companion.GetDisplayName()}: No {Localization.instance.Localize(_targetFireplace.m_fuelItem.m_itemData.m_shared.m_name)} available to tend fire");
            }
        }
        
        #endregion
        
        #region Fire Finding
        
        private Fireplace FindFireNeedingFuel()
        {
            Fireplace nearest = null;
            float nearestDist = float.MaxValue;

            var colliders = Physics.OverlapSphere(SearchCenter, GetEffectiveSearchRadius(FireDetectionRange));
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                var fireplace = collider.GetComponent<Fireplace>() ?? collider.GetComponentInParent<Fireplace>();
                if (fireplace == null) continue;

                // XZ-only distance: wall-mounted torches are at various heights
                float dist = DistanceXZ(fireplace.transform.position);
                if (dist >= nearestDist || !NeedsFuel(fireplace)) continue;
                if (!InteractableOccupancyManager.CanUseInteractable(fireplace.gameObject, Character)) continue;
                if (!CanGetFuel(fireplace.m_fuelItem.gameObject.name)) continue;

                nearestDist = dist;
                nearest = fireplace;
            }
            return nearest;
        }

        private CookingStation FindNearbyCookingStation()
        {
            if (!CompanionBehaviorToggles.IsCookingEnabled(Companion)) return null;

            CookingStation nearest = null;
            float nearestDist = float.MaxValue;

            var colliders = Physics.OverlapSphere(SearchCenter, GetEffectiveSearchRadius(FireDetectionRange));
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                var station = collider.GetComponent<CookingStation>() ?? collider.GetComponentInParent<CookingStation>();
                if (station == null || !CompanionCookingBehavior.TakesFoodDirectly(station)) continue;
                if (!InteractableOccupancyManager.CanUseInteractable(station.gameObject, Character)) continue;

                float dist = DistanceXZ(station.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = station;
                }
            }
            return nearest;
        }

        private float DistanceXZ(Vector3 target)
        {
            float dx = Transform.position.x - target.x;
            float dz = Transform.position.z - target.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private Vector3 GroundedPosition(Vector3 worldPos)
        {
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetGroundHeight(worldPos, out float groundY))
            {
                return new Vector3(worldPos.x, groundY, worldPos.z);
            }
            return new Vector3(worldPos.x, Transform.position.y, worldPos.z);
        }
        
        #endregion
        
        #region Cooking
        
        private bool HasFoodToCook(CookingStation station)
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
                if (CompanionCookingBehavior.IsRawFor(station, item)) return true;
            return false;
        }

        private void TryAddFoodToStation()
        {
            if (_targetCookingStation == null || Humanoid == null) return;
            var storage = GetStorageInventory();
            if (storage == null) return;
            
            foreach (var item in storage.GetAllItems())
            {
                if (CompanionCookingBehavior.IsRawFor(_targetCookingStation, item))
                {
                    if (_targetCookingStation.UseItem(Humanoid, item))
                    {
                        storage.RemoveOneItem(item);
                        LogVerbose($"Added {item.m_shared.m_name} to cooking station");
                    }
                    return;
                }
            }
        }
        
        #endregion
        
        #region Helpers
        
        private void ReleaseOccupancy()
        {
            if (_targetFireplace != null)
                InteractableOccupancyManager.Release(_targetFireplace.gameObject, Character);
            if (_targetCookingStation != null)
                InteractableOccupancyManager.Release(_targetCookingStation.gameObject, Character);
        }
        
        private void NotifyOwner()
        {
            var owner = Companion?.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            if (_fuelAdded > 0 || _foodCooked > 0)
            {
                string message = _fuelAdded > 0 && _foodCooked > 0
                    ? $"{Companion.GetDisplayName()} added fuel and cooked {_foodCooked} items"
                    : _fuelAdded > 0
                        ? $"{Companion.GetDisplayName()} tended the fire"
                        : $"{Companion.GetDisplayName()} cooked {_foodCooked} items";
                
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }
        
        #endregion
    }
}
