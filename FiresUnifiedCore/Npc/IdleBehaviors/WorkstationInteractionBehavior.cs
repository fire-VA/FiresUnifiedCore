// TODO: REMOVE AFTER TESTING V2 - This file is no longer registered in CompanionIdleBehavior.SubBehaviors.cs
// WorkstationInteractionBehaviorV2 is now used instead. Remove this file once V2 is confirmed working.
// See: CompanionIdleBehavior.SubBehaviors.cs InitializeSubBehaviors()

using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Handles companion interactions with crafting workstations (workbench, forge, stonecutter, etc.).
    /// When idle near a workstation, companion will perform crafting animations.
    /// Can be commanded to a specific workstation via ping system.
    /// 
    /// Uses PieceDataHelper for extracting proper interaction data from pieces.
    /// 
    /// SUPPORTED WORKSTATIONS:
    /// - Workbench (piece_workbench)
    /// - Forge (forge)
    /// - Stonecutter (piece_stonecutter)
    /// - Artisan Table (piece_artisanstation)
    /// - Cauldron (piece_cauldron)
    /// - Black Forge (blackforge)
    /// - Galdr Table (piece_magetable)
    /// 
    /// ANIMATIONS:
    /// - Uses "Working" or crafting-specific animations based on station type
    /// - Companion faces the workstation
    /// </summary>
    public class WorkstationInteractionBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "WorkstationInteraction";
        
        /// <summary>
        /// Workstation interaction supports being interrupted by combat and resumed afterwards.
        /// </summary>
        public override bool SupportsResumption => true;
        
        /// <summary>
        /// Workstation interaction is available for idle rotation when companion is in Stay mode.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        #region Settings
        
        private const float WORKSTATION_DETECTION_RANGE = 10f;
        private const float INTERACTION_DISTANCE = 2f;
        private const float MIN_WORK_DURATION = 10f;   // Reduced from 30s to prevent getting stuck
        private const float MAX_WORK_DURATION = 20f;   // Reduced from 120s to allow behavior rotation
        private const float WORK_ANIMATION_INTERVAL = 3f;
        
        // Auto-repair settings
        private const float REPAIR_CHANCE_PER_ANIMATION = 0.25f;  // 25% chance per work animation to repair an item
        private const float REPAIR_AMOUNT_PERCENT = 0.15f;        // Repair 15% of max durability per repair tick
        
        // Upgrade settings - very rare chance to upgrade an item
        private const float UPGRADE_CHANCE_PER_ANIMATION = 0.02f;  // 2% chance per work animation to fully repair AND upgrade
        private const float FULL_REPAIR_CHANCE = 0.08f;            // 8% chance to fully repair an item instead of partial
        
        #endregion
        
        #region State
        
        private enum WorkPhase
        {
            FindingWorkstation,
            MovingToWorkstation,
            Working,
            Complete
        }
        
        private WorkPhase _currentPhase = WorkPhase.FindingWorkstation;
        private CraftingStation _targetStation;
        private PieceDataHelper.PieceData _pieceData;
        private Vector3 _workPosition;
        private float _phaseStartTime;
        private float _workEndTime;
        private float _lastAnimationTime;
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionCombatMovement _combatMovement;
        private CompanionInventory _inventory;
        private Rigidbody _rigidbody;
        
        // Commanded target (set by ping system)
        private CraftingStation _commandedStation;
        
        // Saved state for combat resumption
        private WorkPhase _savedPhase;
        private CraftingStation _savedStation;
        private Vector3 _savedWorkPosition;
        private float _savedWorkEndTime;
        
        #endregion
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _animator = companion.GetComponentInChildren<Animator>(true);
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            
            MaxDuration = MAX_WORK_DURATION + 30f;
        }
        
        /// <summary>
        /// Sets a specific workstation as the target (used by command system).
        /// </summary>
        public void SetCommandedStation(CraftingStation station)
        {
            _commandedStation = station;
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;
            
            // If commanded to a station, always allow
            if (_commandedStation != null) return true;
            
            // Otherwise, check for nearby workstations
            var station = FindNearbyWorkstation();
            return station != null;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = WorkPhase.FindingWorkstation;
            _phaseStartTime = Time.time;
            
            // Use commanded station if set, otherwise find one
            if (_commandedStation != null)
            {
                _targetStation = _commandedStation;
                _commandedStation = null;
                
                // Extract piece data for proper interaction handling
                _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
                if (_pieceData != null && CompanionIdleBehavior.VerboseLogging)
                {
                    PieceDataHelper.LogPieceData(_pieceData, $"{Companion.companionName} targeting");
                }
                
                _workPosition = CalculateWorkPosition(_targetStation);
                SetPhase(WorkPhase.MovingToWorkstation);
                MoveToPosition(_workPosition);
                
                // Chat feedback for commanded workstation
                string stationName = GetWorkstationDisplayName(_targetStation);
                CompanionChatHelper.QuickMessages.WorkingAtStation(Companion, stationName);
            }
            
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} starting workstation interaction");
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
                case WorkPhase.FindingWorkstation:
                    return UpdateFindingWorkstation();
                    
                case WorkPhase.MovingToWorkstation:
                    return UpdateMovingToWorkstation();
                    
                case WorkPhase.Working:
                    return UpdateWorking();
                    
                case WorkPhase.Complete:
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            // Release occupancy when cancelled
            if (_targetStation != null)
            {
                InteractableOccupancyManager.Release(_targetStation.gameObject, _character);
            }
            
            StopWorkAnimation();
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();
            base.Cancel();
        }
        
        #region Combat Resumption State
        
        /// <summary>
        /// Saves the current state before combat interruption.
        /// </summary>
        protected override void SaveState()
        {
            _savedPhase = _currentPhase;
            _savedStation = _targetStation;
            _savedWorkPosition = _workPosition;
            _savedWorkEndTime = _workEndTime;
            
            // Stop animation but don't release occupancy - we'll resume
            StopWorkAnimation();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] Saved state: phase={_savedPhase}, station={_savedStation?.m_name}");
        }
        
        /// <summary>
        /// Restores the saved state after combat ends.
        /// </summary>
        protected override void RestoreState()
        {
            // Restore the saved state
            _targetStation = _savedStation;
            _workPosition = _savedWorkPosition;
            
            // Validate that the workstation still exists
            if (_targetStation == null || !_targetStation.gameObject.activeInHierarchy)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[WorkstationInteraction] Cannot restore - workstation no longer exists");
                SetPhase(WorkPhase.Complete);
                return;
            }
            
            // Re-extract piece data
            _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
            
            // Try to re-occupy the workstation
            if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, _character, MAX_WORK_DURATION))
            {
                // Someone else grabbed it while we were fighting
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[WorkstationInteraction] Cannot restore - workstation is now occupied");
                SetPhase(WorkPhase.Complete);
                return;
            }
            
            // Set command priority again
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
            
            // Resume from where we were, or start moving back to station
            float dist = Vector3.Distance(Transform.position, _workPosition);
            if (dist > INTERACTION_DISTANCE)
            {
                // Need to walk back to the workstation first
                SetPhase(WorkPhase.MovingToWorkstation);
                MoveToPosition(_workPosition);
            }
            else
            {
                // We're close enough - resume working
                // Calculate remaining work time (give some extra since we were interrupted)
                float remainingWorkTime = Mathf.Max(15f, _savedWorkEndTime - Time.time + 30f);
                _workEndTime = Time.time + remainingWorkTime;
                
                // Lock movement
                if (_combatMovement != null)
                {
                    _combatMovement.LockMovement("WorkstationWork", remainingWorkTime + 5f);
                }
                
                SetPhase(WorkPhase.Working);
                PlayWorkAnimation();
                _lastAnimationTime = Time.time;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] Restored state: resuming from phase={_currentPhase}");
        }
        
        #endregion
        
        public override string GetStatusDescription()
        {
            if (_targetStation == null) return "Looking for workstation";
            
            string stationName = GetWorkstationDisplayName(_targetStation);
            return _currentPhase switch
            {
                WorkPhase.MovingToWorkstation => $"Walking to {stationName}",
                WorkPhase.Working => $"Working at {stationName}",
                _ => "Working"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateFindingWorkstation()
        {
            _targetStation = FindNearbyWorkstation();
            
            if (_targetStation == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[WorkstationInteraction] {Companion.companionName} couldn't find workstation");
                Complete();
                return true;
            }
            
            // Extract piece data for proper interaction handling
            _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
            if (_pieceData != null && CompanionIdleBehavior.VerboseLogging)
            {
                PieceDataHelper.LogPieceData(_pieceData, $"{Companion.companionName} found");
            }
            
            _workPosition = CalculateWorkPosition(_targetStation);
            SetPhase(WorkPhase.MovingToWorkstation);
            MoveToPosition(_workPosition);
            
            return false;
        }
        
        private bool UpdateMovingToWorkstation()
        {
            if (_targetStation == null)
            {
                Complete();
                return true;
            }
            
            float dist = Vector3.Distance(Transform.position, _workPosition);
            
            if (dist < INTERACTION_DISTANCE)
            {
                StopMovement();
                StartWorking();
                return false;
            }
            
            // Timeout on movement - TELEPORT instead of giving up!
            // Use a longer timeout to give pathfinding a fair chance
            float pathfindingTimeout = dist > 15f ? 30f : 20f;
            
            if (Time.time - _phaseStartTime > pathfindingTimeout)
            {
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} pathfinding timeout after {pathfindingTimeout}s - TELEPORTING near workstation (distance: {dist:F1}m)");
                
                // CRITICAL: Teleport NEAR the workstation, not ON TOP of it!
                // The work position is already calculated to be in front of the station
                Vector3 teleportPos = _workPosition;
                
                // Get ground height
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(_workPosition, out groundHeight))
                    {
                        teleportPos.y = groundHeight + 0.1f;
                    }
                }
                
                // Teleport the companion
                Transform.position = teleportPos;
                
                // Also update the ZDO position if available
                var nview = Companion?.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    nview.GetZDO().SetPosition(teleportPos);
                }
                
                // Reset pathfinding state in AI
                var companionAI = Companion.GetCompanionAI();
                companionAI?.ResetPathfindingState();
                
                // Reset phase timer since we just teleported
                _phaseStartTime = Time.time;
                
                // Notify the player that we had to teleport
                var owner = Companion?.GetOwner();
                if (owner != null && owner == Player.m_localPlayer)
                {
                    string stationName = GetWorkstationDisplayName(_targetStation);
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"{Companion.GetDisplayName()}: Couldn't find path - teleported near {stationName}");
                }
            }
            
            return false;
        }
        
        private bool UpdateWorking()
        {
            if (_targetStation == null)
            {
                Complete();
                return true;
            }
            
            // Keep facing the workstation
            FaceTarget(_targetStation.transform.position);
            
            // CRITICAL: Ensure movement stays stopped every frame to prevent drift/sliding
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            // Zero velocity to prevent sliding animations
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                if (vel.x != 0 || vel.z != 0)
                {
                    _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
                }
            }
            
            // Play work animation periodically
            if (Time.time - _lastAnimationTime >= WORK_ANIMATION_INTERVAL)
            {
                PlayWorkAnimation();
                _lastAnimationTime = Time.time;
            }
            
            // Check if work duration is complete
            if (Time.time >= _workEndTime)
            {
                // Release occupancy when done
                InteractableOccupancyManager.Release(_targetStation.gameObject, _character);
                
                StopWorkAnimation();
                
                // Unlock movement
                if (_combatMovement != null)
                {
                    _combatMovement.UnlockMovement();
                }
                
                SetPhase(WorkPhase.Complete);
                return true;
            }
            
            return false;
        }
        
        private void StartWorking()
        {
            // CRITICAL: Register occupancy before starting work
            if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, _character, MAX_WORK_DURATION + 30f))
            {
                // Someone else grabbed it first
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[WorkstationInteraction] {Companion.companionName} could not occupy {_targetStation.m_name} - already taken");
                Complete();
                return;
            }
            
            float workDuration = Random.Range(MIN_WORK_DURATION, MAX_WORK_DURATION);
            _workEndTime = Time.time + workDuration;
            
            // CRITICAL: Stop movement completely before locking
            StopMovement();
            
            // Lock movement while working
            if (_combatMovement != null)
            {
                _combatMovement.LockMovement("WorkstationWork", workDuration + 5f);
            }
            
            // Make rigidbody kinematic to prevent drift
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            // Face the workstation
            FaceTarget(_targetStation.transform.position);
            
            SetPhase(WorkPhase.Working);
            PlayWorkAnimation();
            _lastAnimationTime = Time.time;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} started working at {_targetStation.m_name} for {workDuration:F0}s");
        }
        
        #endregion
        
        #region Animations
        
        private void PlayWorkAnimation()
        {
            // Use simple interact trigger - short animation that doesn't get stuck
            // CrossFade to specific clips like "Workbench", "Forge", "Cooking" caused
            // animations to get stuck without proper exit transitions
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
            else if (_animator != null)
            {
                if (HasAnimatorParameter("interact"))
                    _animator.SetTrigger("interact");
            }
            
            // Try to repair an item with each work animation
            TryRepairItem();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} playing work animation");
        }
        
        /// <summary>
        /// Attempts to repair a damaged item in the companion's inventory.
        /// Has a chance-based roll per work animation cycle.
        /// Small chance to fully repair, very small chance to upgrade.
        /// </summary>
        private void TryRepairItem()
        {
            if (_inventory == null) return;
            
            // First check for upgrade chance (very rare - 2%)
            if (Random.value <= UPGRADE_CHANCE_PER_ANIMATION)
            {
                TryUpgradeItem();
                return; // Upgrade attempt includes full repair, so don't also do normal repair
            }
            
            // Check for full repair chance (8%)
            if (Random.value <= FULL_REPAIR_CHANCE)
            {
                TryFullRepairItem();
                return;
            }
            
            // Roll for normal partial repair chance (25%)
            if (Random.value > REPAIR_CHANCE_PER_ANIMATION) return;
            
            // Find items that need repair
            var damagedItems = GetDamagedItems();
            if (damagedItems.Count == 0) return;
            
            // Pick a random damaged item to repair
            var itemToRepair = damagedItems[Random.Range(0, damagedItems.Count)];
            
            // Repair it (partial)
            float maxDurability = itemToRepair.GetMaxDurability();
            float repairAmount = maxDurability * REPAIR_AMOUNT_PERCENT;
            float oldDurability = itemToRepair.m_durability;
            float newDurability = Mathf.Min(itemToRepair.m_durability + repairAmount, maxDurability);
            float actualRepairPercent = (newDurability - oldDurability) / maxDurability;
            
            itemToRepair.m_durability = newDurability;
            
            // Get item name for notification
            string itemName = Localization.instance?.Localize(itemToRepair.m_shared?.m_name) ?? itemToRepair.m_shared?.m_name ?? "item";
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} repaired {itemName}: " +
                    $"{oldDurability:F0} -> {newDurability:F0} / {maxDurability:F0}");
            }
            
            // Notify owner via chat bubble (rate-limited internally)
            CompanionChatHelper.QuickMessages.ItemRepaired(Companion, itemName, actualRepairPercent);
        }
        
        /// <summary>
        /// Attempts to fully repair a random damaged item.
        /// </summary>
        private void TryFullRepairItem()
        {
            var damagedItems = GetDamagedItems();
            if (damagedItems.Count == 0) return;
            
            // Pick a random damaged item
            var itemToRepair = damagedItems[Random.Range(0, damagedItems.Count)];
            
            float maxDurability = itemToRepair.GetMaxDurability();
            float oldDurability = itemToRepair.m_durability;
            
            // Full repair
            itemToRepair.m_durability = maxDurability;
            
            string itemName = Localization.instance?.Localize(itemToRepair.m_shared?.m_name) ?? itemToRepair.m_shared?.m_name ?? "item";
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} FULLY repaired {itemName}!");
            }
            
            // Special notification for full repair
            var owner = Companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                string[] fullRepairMessages = new[]
                {
                    $"Good as new! I fully repaired the {itemName}.",
                    $"The {itemName} is completely fixed now!",
                    $"I restored the {itemName} to perfect condition.",
                    $"All done - {itemName} is like new!"
                };
                CompanionChatHelper.SaySpeechBubble(Companion, fullRepairMessages[Random.Range(0, fullRepairMessages.Length)], 5f);
            }
        }
        
        /// <summary>
        /// Attempts to upgrade a random item by one quality level.
        /// This is very rare and represents exceptional craftsmanship.
        /// Also fully repairs the item.
        /// </summary>
        private void TryUpgradeItem()
        {
            // Find items that can be upgraded
            var upgradableItems = GetUpgradableItems();
            if (upgradableItems.Count == 0)
            {
                // No upgradable items, do a full repair instead
                TryFullRepairItem();
                return;
            }
            
            // Pick a random item to upgrade
            var itemToUpgrade = upgradableItems[Random.Range(0, upgradableItems.Count)];
            
            int oldQuality = itemToUpgrade.m_quality;
            int newQuality = oldQuality + 1;
            
            // CRITICAL: Use CompanionInventory.UpdateItemQuality() to properly persist the change
            // This updates the ItemData, the internal quality tracking, saves to ZDO, AND saves to Vault
            // Without the vault save, the upgrade is lost when the player logs out and back in
            bool upgraded = _inventory.UpdateItemQuality(itemToUpgrade, newQuality);
            
            if (!upgraded)
            {
                // If the item wasn't found in equipped items (maybe it's in storage),
                // update it directly and save manually
                Debug.LogWarning($"[WorkstationInteraction] UpdateItemQuality failed for {itemToUpgrade.m_shared.m_name} - using fallback");
                itemToUpgrade.m_quality = newQuality;
                _inventory.TriggerSaveToZDO();
                
                // CRITICAL: Also save to vault via companion controller
                if (Companion != null)
                {
                    Companion.SaveCompanionToVault();
                }
            }
            
            // Also fully repair it
            itemToUpgrade.m_durability = itemToUpgrade.GetMaxDurability();
            
            string itemName = Localization.instance?.Localize(itemToUpgrade.m_shared?.m_name) ?? itemToUpgrade.m_shared?.m_name ?? "item";
            
            Debug.Log($"[WorkstationInteraction] {Companion.companionName} UPGRADED {itemName} from quality {oldQuality} to {newQuality}!");
            
            // Big notification for upgrade - this is a special event!
            var owner = Companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                CompanionChatHelper.QuickMessages.ItemUpgraded(Companion, itemName, newQuality);
            }
        }
        
        /// <summary>
        /// Gets a list of all damaged items the companion has.
        /// </summary>
        private List<ItemDrop.ItemData> GetDamagedItems()
        {
            var damagedItems = new List<ItemDrop.ItemData>();
            
            // Check equipped items first (prioritize)
            var equippedSlots = new[] 
            { 
                CompanionInventory.EquipmentSlot.Helmet,
                CompanionInventory.EquipmentSlot.Chest,
                CompanionInventory.EquipmentSlot.Legs,
                CompanionInventory.EquipmentSlot.Shoulder,
                CompanionInventory.EquipmentSlot.Utility,
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.LeftHand,
                CompanionInventory.EquipmentSlot.RightBack,
                CompanionInventory.EquipmentSlot.LeftBack
            };
            
            foreach (var slot in equippedSlots)
            {
                var item = _inventory.GetEquippedItem(slot);
                if (item != null && ItemNeedsRepair(item))
                {
                    damagedItems.Add(item);
                }
            }
            
            // Also check storage inventory
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item != null && ItemNeedsRepair(item))
                    {
                        damagedItems.Add(item);
                    }
                }
            }
            
            return damagedItems;
        }
        
        /// <summary>
        /// Gets a list of items that can be upgraded (have quality levels and aren't at max).
        /// </summary>
        private List<ItemDrop.ItemData> GetUpgradableItems()
        {
            var upgradableItems = new List<ItemDrop.ItemData>();
            
            // Check equipped items
            var equippedSlots = new[] 
            { 
                CompanionInventory.EquipmentSlot.Helmet,
                CompanionInventory.EquipmentSlot.Chest,
                CompanionInventory.EquipmentSlot.Legs,
                CompanionInventory.EquipmentSlot.Shoulder,
                CompanionInventory.EquipmentSlot.Utility,
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.LeftHand,
                CompanionInventory.EquipmentSlot.RightBack,
                CompanionInventory.EquipmentSlot.LeftBack
            };
            
            foreach (var slot in equippedSlots)
            {
                var item = _inventory.GetEquippedItem(slot);
                if (item != null && CanUpgradeItem(item))
                {
                    upgradableItems.Add(item);
                }
            }
            
            // Also check storage inventory
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item != null && CanUpgradeItem(item))
                    {
                        upgradableItems.Add(item);
                    }
                }
            }
            
            return upgradableItems;
        }
        
        /// <summary>
        /// Checks if an item can be upgraded (has quality levels and isn't at max).
        /// </summary>
        private bool CanUpgradeItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            
            // Check if item has max quality defined (weapons/armor typically have maxQuality > 1)
            int maxQuality = item.m_shared.m_maxQuality;
            if (maxQuality <= 1) return false; // Item doesn't support quality levels
            
            // Check if already at max quality
            if (item.m_quality >= maxQuality) return false;
            
            // Only upgrade items that are equipment (weapons, armor, tools)
            var itemType = item.m_shared.m_itemType;
            bool isUpgradableType = itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                                   itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                                   itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                                   itemType == ItemDrop.ItemData.ItemType.Bow ||
                                   itemType == ItemDrop.ItemData.ItemType.Shield ||
                                   itemType == ItemDrop.ItemData.ItemType.Helmet ||
                                   itemType == ItemDrop.ItemData.ItemType.Chest ||
                                   itemType == ItemDrop.ItemData.ItemType.Legs ||
                                   itemType == ItemDrop.ItemData.ItemType.Shoulder ||
                                   itemType == ItemDrop.ItemData.ItemType.Utility ||
                                   itemType == ItemDrop.ItemData.ItemType.Tool ||
                                   itemType == ItemDrop.ItemData.ItemType.Torch;
            
            return isUpgradableType;
        }
        
        /// <summary>
        /// Checks if an item uses durability and needs repair.
        /// </summary>
        private bool ItemNeedsRepair(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            
            // Check if item uses durability
            if (!item.m_shared.m_useDurability) return false;
            
            // Check if item is damaged (below 95% durability)
            float maxDurability = item.GetMaxDurability();
            if (maxDurability <= 0) return false;
            
            float durabilityPercent = item.m_durability / maxDurability;
            return durabilityPercent < 0.95f;
        }
        
        private void StopWorkAnimation()
        {
            // Reset any animation state flags
            if (_zanim != null)
            {
                _zanim.SetBool("crafting", false);
            }
            
            if (_animator != null)
            {
                if (HasAnimatorParameter("crafting"))
                    _animator.SetBool("crafting", false);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[WorkstationInteraction] {Companion.companionName} stopped work animation");
        }
        
        #endregion
        
        #region Helpers
        
        private CraftingStation FindNearbyWorkstation()
        {
            CraftingStation bestStation = null;
            float bestDistance = float.MaxValue;
            
            // Use SearchCenter for staying companions (searches around home position)
            float searchRadius = GetEffectiveSearchRadius(WORKSTATION_DETECTION_RANGE);
            var colliders = Physics.OverlapSphere(SearchCenter, searchRadius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var station = col.GetComponent<CraftingStation>() ?? col.GetComponentInParent<CraftingStation>();
                if (station == null) continue;
                
                // Check if station is valid (has required extension level for example)
                if (!IsValidWorkstation(station)) continue;
                
                float dist = Vector3.Distance(Transform.position, station.transform.position);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestStation = station;
                }
            }
            
            return bestStation;
        }
        
        private bool IsValidWorkstation(CraftingStation station)
        {
            if (station == null) return false;
            
            // Any CraftingStation is valid for companion to "work" at
            // This includes: Workbench, Forge, Stonecutter, Artisan Table, Cauldron, etc.
            // The companion will just play work animations - not actually craft
            
            // Check if the workstation is already being used by another companion or player
            if (!InteractableOccupancyManager.CanUseInteractable(station.gameObject, _character))
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[WorkstationInteraction] {Companion?.companionName} skipping {station.m_name} - occupied or crowded");
                return false;
            }
            
            return true;
        }
        
        private Vector3 CalculateWorkPosition(CraftingStation station)
        {
            // Use PieceDataHelper for proper interaction position
            if (_pieceData != null)
            {
                return PieceDataHelper.GetSafeInteractionPosition(_pieceData, 1.5f);
            }
            
            // Fallback: Position in front of the station
            Vector3 stationPos = station.transform.position;
            Vector3 stationForward = station.transform.forward;
            
            // Stand slightly in front of the workstation
            Vector3 workPos = stationPos + stationForward * 1.5f;
            
            // Ground the position
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(workPos, out groundHeight))
                {
                    workPos.y = groundHeight;
                }
            }
            
            return workPos;
        }
        
        private string GetWorkstationDisplayName(CraftingStation station)
        {
            // Use PieceDataHelper for display name
            if (_pieceData != null)
            {
                return PieceDataHelper.GetCraftingStationType(station);
            }
            
            if (station == null) return "workstation";
            
            // Use the station's localized name if available
            if (!string.IsNullOrEmpty(station.m_name))
            {
                return Localization.instance?.Localize(station.m_name) ?? station.m_name;
            }
            
            return "workstation";
        }
        
        private void SetPhase(WorkPhase phase)
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
            // WALKING for idle/work behaviors - companions should move slowly and relaxed
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
            Vector3 dir = targetPos - Transform.position;
            dir.y = 0;
            
            // CRITICAL: Check magnitude BEFORE normalizing to avoid zero vector issues
            float magnitude = dir.magnitude;
            if (magnitude < 0.01f)
            {
                return; // Too close to target, don't rotate
            }
            
            dir = dir / magnitude; // Manual normalize
            
            Quaternion targetRot = Quaternion.LookRotation(dir);
            
            // Validate quaternion before applying
            if (float.IsNaN(targetRot.x) || float.IsNaN(targetRot.y) || float.IsNaN(targetRot.z) || float.IsNaN(targetRot.w))
            {
                return;
            }
            
            Transform.rotation = Quaternion.Slerp(Transform.rotation, targetRot, Time.deltaTime * 5f);
        }
        
        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }
        
        #endregion
    }
}
