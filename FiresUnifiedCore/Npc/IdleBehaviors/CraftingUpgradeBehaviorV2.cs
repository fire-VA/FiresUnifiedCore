using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Crafting upgrade behavior using the new WorkBehaviorBase infrastructure.
    /// Handles companion auto-upgrading of their own equipment at workbenches.
    /// 
    /// MIGRATED FROM: CraftingUpgradeBehavior.cs
    /// USES: WorkBehaviorBase<TPhase>, ResourceAccessService, BehaviorPhaseManager
    /// </summary>
    public class CraftingUpgradeBehaviorV2 : WorkBehaviorBase<CraftingUpgradeBehaviorV2.UpgradePhase>
    {
        public override string BehaviorName => "CraftingUpgrade";
        public override bool AvailableForIdleRotation => false; // Triggered by chance
        
        #region Phase Enum
        
        public enum UpgradePhase
        {
            FindingWorkstation,
            CheckingEquipment,
            MovingToWorkstation,
            Upgrading,
            Complete
        }
        
        #endregion
        
        #region Settings
        
        private const float WorkstationDetectionRange = 8f;
        private const float InteractionDistance = 2f;
        private const float UpgradeAnimationDuration = 2f;
        private const float MaxUpgradeTime = 60f;
        private const float UpgradeAttemptChance = 0.3f;
        private const int MaxUpgradesPerSession = 3;
        private const float MinStationCover = 0.7f;
        
        #endregion
        
        #region State
        
        private CraftingStation _targetStation;
        private Vector3 _workPosition;
        
        private ItemDrop.ItemData _itemToUpgrade;
        private CompanionInventory.EquipmentSlot _upgradeSlot;
        private int _currentQuality;
        private List<Piece.Requirement> _upgradeRequirements;
        
        private int _upgradesCompleted;
        private List<string> _upgradedItems = new List<string>();
        
        #endregion
        
        #region WorkBehaviorBase Implementation
        
        protected override UpgradePhase InitialPhase => UpgradePhase.FindingWorkstation;
        
        protected override float GetPhaseTimeout(UpgradePhase phase)
        {
            return phase switch
            {
                UpgradePhase.FindingWorkstation => 5f,
                UpgradePhase.CheckingEquipment => 5f,
                UpgradePhase.MovingToWorkstation => 15f,
                UpgradePhase.Upgrading => 10f,
                _ => 5f
            };
        }
        
        protected override string GetPhaseDescription(UpgradePhase phase)
        {
            if (_itemToUpgrade != null)
            {
                return $"Upgrading {_itemToUpgrade.m_shared.m_name}";
            }
            
            return phase switch
            {
                UpgradePhase.FindingWorkstation => "Looking for workbench",
                UpgradePhase.CheckingEquipment => "Checking equipment",
                UpgradePhase.MovingToWorkstation => "Walking to workbench",
                UpgradePhase.Upgrading => "Crafting upgrade",
                _ => "Upgrading equipment"
            };
        }
        
        protected override UpgradePhase OnPhaseTimeout(UpgradePhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");
            return UpgradePhase.Complete;
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MaxUpgradeTime + 30f;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || Inventory == null) return false;
            if (!CompanionBehaviorToggles.IsCraftingEnabled(Companion)) return false;
            if (Random.value > UpgradeAttemptChance) return false;
            
            var station = FindNearbyWorkstation();
            if (station == null) return false;
            
            return HasUpgradeableEquipment(station);
        }
        
        public override void Start()
        {
            base.Start();
            
            _upgradesCompleted = 0;
            _upgradedItems.Clear();
            _itemToUpgrade = null;
            
            LogVerbose("Starting equipment upgrade check");
        }
        
        protected override bool UpdatePhase(UpgradePhase phase)
        {
            return phase switch
            {
                UpgradePhase.FindingWorkstation => UpdateFindingWorkstation(),
                UpgradePhase.CheckingEquipment => UpdateCheckingEquipment(),
                UpgradePhase.MovingToWorkstation => UpdateMovingToWorkstation(),
                UpgradePhase.Upgrading => UpdateUpgrading(),
                UpgradePhase.Complete => CompleteAndNotify(),
                _ => true
            };
        }
        
        public override void Cancel()
        {
            CombatMovement?.UnlockMovement();
            NotifyOwner();
            base.Cancel();
        }
        
        #endregion
        
        #region Phase Updates
        
        private bool UpdateFindingWorkstation()
        {
            _targetStation = FindNearbyWorkstation();
            
            if (_targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return false;
            }
            
            SetPhase(UpgradePhase.CheckingEquipment);
            return false;
        }
        
        private bool UpdateCheckingEquipment()
        {
            var upgradeInfo = FindBestUpgrade(_targetStation);
            
            if (upgradeInfo == null)
            {
                SetPhase(UpgradePhase.Complete);
                return false;
            }
            
            _itemToUpgrade = upgradeInfo.Value.item;
            _upgradeSlot = upgradeInfo.Value.slot;
            _currentQuality = upgradeInfo.Value.currentQuality;
            _upgradeRequirements = upgradeInfo.Value.requirements;
            
            _workPosition = CalculateWorkPosition(_targetStation);
            SetPhase(UpgradePhase.MovingToWorkstation);
            MoveToPosition(_workPosition);
            
            LogVerbose($"Will upgrade {_itemToUpgrade.m_shared.m_name} from level {_currentQuality} to {_currentQuality + 1}");
            return false;
        }
        
        private bool UpdateMovingToWorkstation()
        {
            if (_targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return false;
            }
            
            // CRITICAL: Call ContinueMovement() every frame for vanilla pathfinding to work!
            if (ContinueMovement())
            {
                // Arrived at destination
                StopMovement();
                SetPhase(UpgradePhase.Upgrading);
            }
            
            return false;
        }
        
        private bool UpdateUpgrading()
        {
            if (_itemToUpgrade == null || _targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetStation.transform.position);
            
            // Lock movement during upgrade
            if (CombatMovement != null && !CombatMovement.IsMovementLocked)
            {
                CombatMovement.LockMovement("CraftingUpgrade", 5f);
            }
            
            PlayInteractAnimation();
            
            // Wait for animation
            if (TimeInCurrentPhase < UpgradeAnimationDuration)
            {
                return false;
            }
            
            // Perform the upgrade
            if (PerformUpgrade())
            {
                _upgradesCompleted++;
                _upgradedItems.Add(_itemToUpgrade.m_shared.m_name);
                
                LogVerbose($"Upgraded {_itemToUpgrade.m_shared.m_name} to level {_currentQuality + 1}!");
                
                // Fire event
                CompanionEvents.FireWorkStationCompleted(Companion, _targetStation.gameObject, 0, 1);
            }
            
            // Check for more upgrades
            _itemToUpgrade = null;
            
            if (_upgradesCompleted < MaxUpgradesPerSession)
            {
                var nextUpgrade = FindBestUpgrade(_targetStation);
                if (nextUpgrade != null)
                {
                    _itemToUpgrade = nextUpgrade.Value.item;
                    _upgradeSlot = nextUpgrade.Value.slot;
                    _currentQuality = nextUpgrade.Value.currentQuality;
                    _upgradeRequirements = nextUpgrade.Value.requirements;
                    
                    ResetPhaseTimer();
                    return false;
                }
            }
            
            SetPhase(UpgradePhase.Complete);
            return false;
        }
        
        private bool CompleteAndNotify()
        {
            NotifyOwner();
            Complete();
            return true;
        }
        
        #endregion
        
        #region Upgrade Logic
        
        private CraftingStation FindNearbyWorkstation()
        {
            CraftingStation best = null;
            int bestLevel = -1;
            float bestDist = float.MaxValue;
            
            var colliders = Physics.OverlapSphere(Transform.position, WorkstationDetectionRange);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null || station.m_upgrader) continue;

                int level = station.GetLevel();
                float dist = DistanceTo(station.transform.position);

                if ((level > bestLevel || (level == bestLevel && dist < bestDist)) && IsStationUsable(station))
                {
                    bestLevel = level;
                    bestDist = dist;
                    best = station;
                }
            }
            
            return best;
        }
        
        private bool HasUpgradeableEquipment(CraftingStation station)
        {
            return FindBestUpgrade(station) != null;
        }
        
        private (ItemDrop.ItemData item, CompanionInventory.EquipmentSlot slot, int currentQuality, List<Piece.Requirement> requirements)? FindBestUpgrade(CraftingStation station)
        {
            if (Inventory == null || station == null) return null;
            
            var storageInv = GetStorageInventory();
            if (storageInv == null) return null;
            
            CompanionInventory.EquipmentSlot[] prioritySlots = new[]
            {
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.LeftHand,
                CompanionInventory.EquipmentSlot.RightBack,
                CompanionInventory.EquipmentSlot.Chest,
                CompanionInventory.EquipmentSlot.Legs,
                CompanionInventory.EquipmentSlot.Helmet,
                CompanionInventory.EquipmentSlot.Shoulder,
            };
            
            foreach (var slot in prioritySlots)
            {
                var item = Inventory.GetEquippedItem(slot);
                if (item == null) continue;
                
                int currentQuality = item.m_quality;
                int maxQuality = item.m_shared.m_maxQuality;
                
                if (currentQuality >= maxQuality) continue;

                int targetQuality = currentQuality + 1;
                Recipe recipe = ObjectDB.instance?.GetRecipe(item);
                if (recipe == null || !CanStationUpgrade(station, recipe, targetQuality)) continue;

                var requirements = GetUpgradeRequirements(recipe, targetQuality);
                if (requirements.Count == 0) continue;

                if (!HasRequiredMaterials(requirements, storageInv)) continue;

                return (item, slot, currentQuality, requirements);
            }
            
            return null;
        }
        
        /// <summary>What a normal (non-upgrader) station charges: GetAmount(q) of every non-upgrader resource (Player.ConsumeResources:2086-2090).</summary>
        private List<Piece.Requirement> GetUpgradeRequirements(Recipe recipe, int targetQuality)
        {
            var requirements = new List<Piece.Requirement>();

            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null || req.m_upgraderResource) continue;

                int amount = req.GetAmount(targetQuality);
                if (amount > 0)
                {
                    requirements.Add(new Piece.Requirement
                    {
                        m_resItem = req.m_resItem,
                        m_amount = amount
                    });
                }
            }

            return requirements;
        }
        
        private bool HasRequiredMaterials(List<Piece.Requirement> requirements, Inventory storage)
        {
            if (requirements == null || storage == null) return false;
            
            foreach (var req in requirements)
            {
                if (req.m_resItem == null) continue;
                
                string itemName = req.m_resItem.m_itemData.m_shared.m_name;
                int needed = req.m_amount;
                int have = storage.CountItems(itemName);
                
                if (have < needed) return false;
            }
            
            return true;
        }
        
        /// <summary>Vanilla Player.RequiredCraftingStation(recipe, quality, checkLevel: true) with this (non-upgrader) station as the current one.</summary>
        private static bool CanStationUpgrade(CraftingStation station, Recipe recipe, int targetQuality)
        {
            CraftingStation requiredStation = recipe.GetRequiredStation(targetQuality);
            if (requiredStation == null) return station.m_showBasicRecipies;
            return requiredStation.m_name == station.m_name
                && station.GetLevel() >= recipe.GetRequiredStationLevel(targetQuality);
        }

        /// <summary>Vanilla CraftingStation.CheckUsable: roof cover and fire where the station requires them.</summary>
        private static bool IsStationUsable(CraftingStation station)
        {
            if (station.m_craftRequireRoof)
            {
                Cover.GetCoverForPoint(station.m_roofCheckPoint.position, out float coverPercentage, out bool underRoof);
                if (!underRoof || coverPercentage < MinStationCover) return false;
            }
            return !station.m_craftRequireFire || EffectArea.IsPointPlus025InsideBurningArea(station.transform.position);
        }
        
        private bool PerformUpgrade()
        {
            if (_itemToUpgrade == null || _upgradeRequirements == null || Inventory == null) return false;
            
            var storageInv = GetStorageInventory();
            if (storageInv == null) return false;
            
            // Consume materials
            foreach (var req in _upgradeRequirements)
            {
                if (req.m_resItem == null) continue;
                
                string itemName = req.m_resItem.m_itemData.m_shared.m_name;
                int toRemove = req.m_amount;
                
                var items = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
                foreach (var item in items)
                {
                    if (item.m_shared.m_name == itemName)
                    {
                        int removeCount = Mathf.Min(item.m_stack, toRemove);
                        storageInv.RemoveItem(item, removeCount);
                        toRemove -= removeCount;
                        
                        if (toRemove <= 0) break;
                    }
                }
            }
            
            // Perform upgrade
            int newQuality = _itemToUpgrade.m_quality + 1;
            bool upgraded = Inventory.UpdateItemQuality(_itemToUpgrade, newQuality);
            
            if (!upgraded)
            {
                LogWarning($"UpdateItemQuality failed for {_itemToUpgrade.m_shared.m_name} - using fallback");
                _itemToUpgrade.m_quality = newQuality;
                Inventory.RecalculateEquipmentBonusesPublic();
                SaveInventory();
                
                if (Companion != null)
                {
                    Companion.SaveCompanionToVault();
                }
            }
            
            return true;
        }
        
        private Vector3 CalculateWorkPosition(CraftingStation station)
        {
            Vector3 stationPos = station.transform.position;
            Vector3 stationForward = station.transform.forward;
            return stationPos + stationForward * 1.5f;
        }
        
        #endregion
        
        #region Notifications
        
        private void NotifyOwner()
        {
            if (_upgradesCompleted == 0) return;
            
            var owner = Companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                string itemList = string.Join(", ", _upgradedItems);
                string message = $"{Companion.GetDisplayName()} upgraded: {itemList}";
                
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, message);
            }
        }
        
        #endregion
    }
}
