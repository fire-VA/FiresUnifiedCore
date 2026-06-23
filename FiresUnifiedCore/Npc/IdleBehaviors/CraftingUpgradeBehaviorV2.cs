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
        
        private const float WORKSTATION_DETECTION_RANGE = 8f;
        private const float INTERACTION_DISTANCE = 2f;
        private const float UPGRADE_ANIMATION_DURATION = 2f;
        private const float MAX_UPGRADE_TIME = 60f;
        private const float UPGRADE_ATTEMPT_CHANCE = 0.3f;
        private const int MAX_UPGRADES_PER_SESSION = 3;
        
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
            MaxDuration = MAX_UPGRADE_TIME + 30f;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || Inventory == null) return false;
            if (!CompanionBehaviorToggles.IsCraftingEnabled(Companion)) return false;
            if (Random.value > UPGRADE_ATTEMPT_CHANCE) return false;
            
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
            if (TimeInCurrentPhase < UPGRADE_ANIMATION_DURATION)
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
            
            if (_upgradesCompleted < MAX_UPGRADES_PER_SESSION)
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
            
            var colliders = Physics.OverlapSphere(Transform.position, WORKSTATION_DETECTION_RANGE);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var station = col.GetComponent<CraftingStation>() ?? col.GetComponentInParent<CraftingStation>();
                if (station == null) continue;
                
                int level = station.GetLevel();
                float dist = DistanceTo(station.transform.position);
                
                if (level > bestLevel || (level == bestLevel && dist < bestDist))
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
                
                var requirements = GetUpgradeRequirements(item, currentQuality + 1);
                if (requirements == null || requirements.Count == 0) continue;
                
                if (!HasRequiredMaterials(requirements, storageInv)) continue;
                if (!CanStationUpgrade(station, item)) continue;
                
                return (item, slot, currentQuality, requirements);
            }
            
            return null;
        }
        
        private List<Piece.Requirement> GetUpgradeRequirements(ItemDrop.ItemData item, int targetQuality)
        {
            if (item == null || ObjectDB.instance == null) return null;
            
            Recipe recipe = ObjectDB.instance.GetRecipe(item);
            if (recipe == null) return null;
            
            var requirements = new List<Piece.Requirement>();
            
            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;
                
                int amountNeeded = req.GetAmount(targetQuality);
                int amountPrev = req.GetAmount(targetQuality - 1);
                int upgradeAmount = amountNeeded - amountPrev;
                
                if (upgradeAmount > 0)
                {
                    requirements.Add(new Piece.Requirement
                    {
                        m_resItem = req.m_resItem,
                        m_amount = upgradeAmount
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
        
        private bool CanStationUpgrade(CraftingStation station, ItemDrop.ItemData item)
        {
            if (station == null || item == null) return false;
            
            Recipe recipe = ObjectDB.instance?.GetRecipe(item);
            if (recipe == null) return false;
            
            if (recipe.m_craftingStation == null) return true;
            
            return recipe.m_craftingStation.m_name == station.m_name;
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
