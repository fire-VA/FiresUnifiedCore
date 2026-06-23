// TODO: REMOVE AFTER TESTING V2 - This file is no longer registered in CompanionIdleBehavior.SubBehaviors.cs
// CraftingUpgradeBehaviorV2 is now used instead. Remove this file once V2 is confirmed working.
// See: CompanionIdleBehavior.SubBehaviors.cs InitializeSubBehaviors()

using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Handles companion auto-upgrading of their own equipment at workbenches.
    /// When idle near a workbench with sufficient materials, companion will upgrade their gear.
    /// 
    /// FEATURES:
    /// - Scans companion's equipped items for possible upgrades
    /// - Checks companion's storage inventory for required materials
    /// - Upgrades equipment at appropriate crafting station
    /// - Notifies owner when upgrades are completed
    /// 
    /// REQUIREMENTS:
    /// - Must be near appropriate crafting station (workbench, forge, etc.)
    /// - Must have upgrade materials in storage inventory
    /// - Equipment must not be at max level
    /// 
    /// PRIORITY:
    /// 1. Weapons (most important for combat effectiveness)
    /// 2. Armor (protection)
    /// 3. Tools (utility)
    /// </summary>
    public class CraftingUpgradeBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "CraftingUpgrade";
        
        #region Settings
        
        private const float WORKSTATION_DETECTION_RANGE = 8f;
        private const float INTERACTION_DISTANCE = 2f;
        private const float UPGRADE_ANIMATION_DURATION = 2f;
        private const float MAX_UPGRADE_TIME = 60f;
        
        // Chance to attempt upgrade when conditions are met
        private const float UPGRADE_ATTEMPT_CHANCE = 0.3f;
        
        #endregion
        
        #region State
        
        private enum UpgradePhase
        {
            FindingWorkstation,
            CheckingEquipment,
            MovingToWorkstation,
            Upgrading,
            Complete
        }
        
        private UpgradePhase _currentPhase = UpgradePhase.FindingWorkstation;
        private CraftingStation _targetStation;
        private Vector3 _workPosition;
        private float _phaseStartTime;
        
        // Upgrade target
        private ItemDrop.ItemData _itemToUpgrade;
        private CompanionInventory.EquipmentSlot _upgradeSlot;
        private int _currentQuality;
        private List<Piece.Requirement> _upgradeRequirements;
        
        // Results
        private int _upgradesCompleted;
        private List<string> _upgradedItems = new List<string>();
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private CompanionInventory _inventory;
        private CompanionCombatMovement _combatMovement;
        private Rigidbody _rigidbody;
        private ZSyncAnimation _zanim;
        
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
            
            MaxDuration = MAX_UPGRADE_TIME + 30f;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || _inventory == null) return false;
            
            // Random chance to attempt
            if (Random.value > UPGRADE_ATTEMPT_CHANCE) return false;
            
            // Check for nearby workstation
            var station = FindNearbyWorkstation();
            if (station == null) return false;
            
            // Check if we have any upgradeable equipment
            if (!HasUpgradeableEquipment(station)) return false;
            
            return true;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = UpgradePhase.FindingWorkstation;
            _phaseStartTime = Time.time;
            _upgradesCompleted = 0;
            _upgradedItems.Clear();
            _itemToUpgrade = null;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[CraftingUpgrade] {Companion.companionName} starting equipment upgrade check");
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut())
            {
                Complete();
                return true;
            }
            
            switch (_currentPhase)
            {
                case UpgradePhase.FindingWorkstation:
                    return UpdateFindingWorkstation();
                    
                case UpgradePhase.CheckingEquipment:
                    return UpdateCheckingEquipment();
                    
                case UpgradePhase.MovingToWorkstation:
                    return UpdateMovingToWorkstation();
                    
                case UpgradePhase.Upgrading:
                    return UpdateUpgrading();
                    
                case UpgradePhase.Complete:
                    NotifyOwner();
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            _combatMovement?.UnlockMovement();
            NotifyOwner();
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            if (_itemToUpgrade != null)
            {
                return $"Upgrading {_itemToUpgrade.m_shared.m_name}";
            }
            
            return _currentPhase switch
            {
                UpgradePhase.FindingWorkstation => "Looking for workbench",
                UpgradePhase.CheckingEquipment => "Checking equipment",
                UpgradePhase.MovingToWorkstation => "Walking to workbench",
                UpgradePhase.Upgrading => "Crafting upgrade",
                _ => "Upgrading equipment"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateFindingWorkstation()
        {
            _targetStation = FindNearbyWorkstation();
            
            if (_targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return true;
            }
            
            SetPhase(UpgradePhase.CheckingEquipment);
            return false;
        }
        
        private bool UpdateCheckingEquipment()
        {
            // Find an item that can be upgraded
            var upgradeInfo = FindBestUpgrade(_targetStation);
            
            if (upgradeInfo == null)
            {
                SetPhase(UpgradePhase.Complete);
                return true;
            }
            
            _itemToUpgrade = upgradeInfo.Value.item;
            _upgradeSlot = upgradeInfo.Value.slot;
            _currentQuality = upgradeInfo.Value.currentQuality;
            _upgradeRequirements = upgradeInfo.Value.requirements;
            
            _workPosition = CalculateWorkPosition(_targetStation);
            SetPhase(UpgradePhase.MovingToWorkstation);
            MoveToPosition(_workPosition);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[CraftingUpgrade] {Companion.companionName} will upgrade {_itemToUpgrade.m_shared.m_name} from level {_currentQuality} to {_currentQuality + 1}");
            
            return false;
        }
        
        private bool UpdateMovingToWorkstation()
        {
            if (_targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return true;
            }
            
            float dist = Vector3.Distance(Transform.position, _workPosition);
            
            if (dist < INTERACTION_DISTANCE)
            {
                StopMovement();
                SetPhase(UpgradePhase.Upgrading);
                return false;
            }
            
            // Timeout
            if (Time.time - _phaseStartTime > 15f)
            {
                SetPhase(UpgradePhase.Complete);
            }
            
            return false;
        }
        
        private bool UpdateUpgrading()
        {
            if (_itemToUpgrade == null || _targetStation == null)
            {
                SetPhase(UpgradePhase.Complete);
                return true;
            }
            
            StopMovement();
            FaceTarget(_targetStation.transform.position);
            
            // Lock movement during upgrade
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("CraftingUpgrade", 5f);
            }
            
            // Play crafting animation
            PlayCraftAnimation();
            
            // Wait for animation
            if (Time.time - _phaseStartTime < UPGRADE_ANIMATION_DURATION)
            {
                return false;
            }
            
            // Perform the upgrade
            if (PerformUpgrade())
            {
                _upgradesCompleted++;
                _upgradedItems.Add(_itemToUpgrade.m_shared.m_name);
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[CraftingUpgrade] {Companion.companionName} upgraded {_itemToUpgrade.m_shared.m_name} to level {_currentQuality + 1}!");
            }
            
            // Check for more upgrades
            _itemToUpgrade = null;
            var nextUpgrade = FindBestUpgrade(_targetStation);
            
            if (nextUpgrade != null && _upgradesCompleted < 3) // Max 3 upgrades per session
            {
                _itemToUpgrade = nextUpgrade.Value.item;
                _upgradeSlot = nextUpgrade.Value.slot;
                _currentQuality = nextUpgrade.Value.currentQuality;
                _upgradeRequirements = nextUpgrade.Value.requirements;
                
                SetPhase(UpgradePhase.Upgrading);
                return false;
            }
            
            SetPhase(UpgradePhase.Complete);
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
                
                // Check station level - prefer higher level stations
                int level = station.GetLevel();
                float dist = Vector3.Distance(Transform.position, station.transform.position);
                
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
            if (_inventory == null || station == null) return null;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return null;
            
            // Priority order: weapons, then armor
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
                var item = _inventory.GetEquippedItem(slot);
                if (item == null) continue;
                
                // Check if item can be upgraded
                int currentQuality = item.m_quality;
                int maxQuality = item.m_shared.m_maxQuality;
                
                if (currentQuality >= maxQuality) continue;
                
                // Get upgrade recipe/requirements
                var requirements = GetUpgradeRequirements(item, currentQuality + 1);
                if (requirements == null || requirements.Count == 0) continue;
                
                // Check if we have the required materials
                if (!HasRequiredMaterials(requirements, storageInv)) continue;
                
                // Check if this station can craft/upgrade this item
                if (!CanStationUpgrade(station, item)) continue;
                
                return (item, slot, currentQuality, requirements);
            }
            
            return null;
        }
        
        private List<Piece.Requirement> GetUpgradeRequirements(ItemDrop.ItemData item, int targetQuality)
        {
            if (item == null || ObjectDB.instance == null) return null;
            
            // Find the recipe for this item
            Recipe recipe = ObjectDB.instance.GetRecipe(item);
            if (recipe == null) return null;
            
            var requirements = new List<Piece.Requirement>();
            
            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;
                
                // Calculate amount needed for this upgrade level
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
                
                if (have < needed)
                {
                    return false;
                }
            }
            
            return true;
        }
        
        private bool CanStationUpgrade(CraftingStation station, ItemDrop.ItemData item)
        {
            if (station == null || item == null) return false;
            
            // Get the recipe
            Recipe recipe = ObjectDB.instance?.GetRecipe(item);
            if (recipe == null) return false;
            
            // Check if this station type matches
            if (recipe.m_craftingStation == null) return true; // No station required
            
            return recipe.m_craftingStation.m_name == station.m_name;
        }
        
        private bool PerformUpgrade()
        {
            if (_itemToUpgrade == null || _upgradeRequirements == null || _inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            // Consume the materials
            foreach (var req in _upgradeRequirements)
            {
                if (req.m_resItem == null) continue;
                
                string itemName = req.m_resItem.m_itemData.m_shared.m_name;
                int toRemove = req.m_amount;
                
                // Find and remove items
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
            
            // CRITICAL: Use UpdateItemQuality to properly persist the upgrade
            // This method updates the ItemData, the internal quality tracking,
            // saves to ZDO, AND saves to Vault for proper persistence across logout/login
            int newQuality = _itemToUpgrade.m_quality + 1;
            bool upgraded = _inventory.UpdateItemQuality(_itemToUpgrade, newQuality);
            
            if (!upgraded)
            {
                // Fallback if item wasn't found in equipped items (shouldn't happen for equipment)
                // Still update the quality but log a warning
                Debug.LogWarning($"[CraftingUpgrade] UpdateItemQuality failed for {_itemToUpgrade.m_shared.m_name} - using fallback");
                _itemToUpgrade.m_quality = newQuality;
                _inventory.RecalculateEquipmentBonusesPublic();
                _inventory.TriggerSaveToZDO();
                
                // Also save to vault via companion controller
                if (Companion != null)
                {
                    Companion.SaveCompanionToVault();
                }
            }
            
            return true;
        }
        
        #endregion
        
        #region Helpers
        
        private void NotifyOwner()
        {
            if (_upgradesCompleted == 0) return;
            
            var owner = Companion.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                string itemList = string.Join(", ", _upgradedItems);
                string message = $"{Companion.GetDisplayName()} upgraded: {itemList}";
                
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, message);
                Debug.Log($"[CraftingUpgrade] {message}");
            }
        }
        
        private void PlayCraftAnimation()
        {
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        private Vector3 CalculateWorkPosition(CraftingStation station)
        {
            Vector3 stationPos = station.transform.position;
            Vector3 stationForward = station.transform.forward;
            
            return stationPos + stationForward * 1.5f;
        }
        
        private void SetPhase(UpgradePhase phase)
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
            _workPosition = position;
            
            // Use CompanionCombatMovement for proper AI-driven pathfinding
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
        
        #endregion
    }
}
