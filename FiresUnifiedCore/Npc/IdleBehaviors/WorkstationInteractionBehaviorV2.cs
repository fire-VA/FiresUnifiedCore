using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Workstation interaction behavior using the new WorkBehaviorBase infrastructure.
    /// Handles companion interactions with crafting workstations.
    /// 
    /// MIGRATED FROM: WorkstationInteractionBehavior.cs
    /// USES: WorkBehaviorBase<TPhase>, BehaviorPhaseManager, PieceDataHelper
    /// 
    /// Note: This behavior includes repair/upgrade functionality that benefits from
    /// the time spent "working" at the station.
    /// </summary>
    public class WorkstationInteractionBehaviorV2 : WorkBehaviorBase<WorkstationInteractionBehaviorV2.WorkPhase>
    {
        public override string BehaviorName => "WorkstationInteraction";
        public override bool SupportsResumption => true;
        public override bool AvailableForIdleRotation => true;
        
        /// <summary>
        /// Workstation interaction has LOW priority (-20) to give other behaviors
        /// (fire tending, smelter operation, resource gathering) a better chance to run.
        /// Workbenches are everywhere, so without this penalty, companions would
        /// spend most of their time at workbenches instead of doing other useful tasks.
        /// </summary>
        public override int InventoryPriority => -20;
        
        #region Phase Enum
        
        public enum WorkPhase
        {
            FindingWorkstation,
            MovingToWorkstation,
            Working,
            Complete
        }
        
        #endregion
        
        #region Settings
        
        private const float WorkstationDetectionRange = 10f;
        private const float InteractionDistance = 2f;
        private const float MinWorkDuration = 10f;
        private const float MaxWorkDuration = 20f;
        private const float WorkAnimationInterval = 3f;
        
        // Repair/upgrade chances - lowered to make interactions feel less robotic
        private const float RepairChancePerAnimation = 0.15f;  // Reduced from 0.25
        private const float RepairAmountPercent = 0.15f;
        private const float UpgradeChancePerAnimation = 0.02f;
        private const float FullRepairChance = 0.05f;  // Reduced from 0.08
        
        // Repair threshold - only repair items below 75% durability (was 95% which caused unnecessary repairs)
        private const float RepairThreshold = 0.75f;
        
        // Workstation cooldown - companions shouldn't interact with workbenches too often
        private const float WorkstationCooldown = 300f;  // 5 minutes between workstation interactions
        
        // Upgrade limit - companions can only upgrade items once per in-game day (1800 seconds)
        private const float UpgradeDayLength = 1800f;  // 30 minutes real time = 1 in-game day
        
        #endregion
        
        #region State
        
        private CraftingStation _targetStation;
        private PieceDataHelper.PieceData _pieceData;
        private Vector3 _workPosition;
        private float _workEndTime;
        private float _lastAnimationTime;
        private bool _isCraftingAnimationActive = false;  // Track if crafting animation is playing
        
        // Cooldown tracking (stored per companion via static dictionary)
        private static Dictionary<string, float> _lastWorkstationTime = new Dictionary<string, float>();
        
        // Upgrade tracking - stored in ZDO via companion
        private const string LastUpgradeDayKey = "lastUpgradeDay";
        
        // Commanded target
        private CraftingStation _commandedStation;
        
        // Saved state for combat resumption
        private WorkPhase _savedPhase;
        private CraftingStation _savedStation;
        private Vector3 _savedWorkPosition;
        private float _savedWorkEndTime;
        
        #endregion
        
        #region WorkBehaviorBase Implementation
        
        protected override WorkPhase InitialPhase => WorkPhase.FindingWorkstation;
        
        protected override float GetPhaseTimeout(WorkPhase phase)
        {
            return phase switch
            {
                WorkPhase.FindingWorkstation => 5f,
                WorkPhase.MovingToWorkstation => 30f,
                WorkPhase.Working => MaxWorkDuration + 10f,
                _ => 10f
            };
        }
        
        protected override string GetPhaseDescription(WorkPhase phase)
        {
            if (_targetStation == null) return "Looking for workstation";
            
            string stationName = GetWorkstationDisplayName(_targetStation);
            return phase switch
            {
                WorkPhase.MovingToWorkstation => $"Walking to {stationName}",
                WorkPhase.Working => $"Working at {stationName}",
                _ => "Working"
            };
        }
        
        protected override WorkPhase OnPhaseTimeout(WorkPhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");
            return WorkPhase.Complete;
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MaxWorkDuration + 30f;
        }
        
        public void SetCommandedStation(CraftingStation station)
        {
            _commandedStation = station;
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;
            
            // If commanded to go to a specific station, skip cooldown check
            if (_commandedStation != null) return true;
            
            // Check cooldown - don't interact with workbenches too often
            string companionId = Companion.companionId ?? Companion.GetInstanceID().ToString();
            if (_lastWorkstationTime.TryGetValue(companionId, out float lastTime))
            {
                if (Time.time - lastTime < WorkstationCooldown)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[WorkstationInteraction] {Companion?.companionName} skipping - on cooldown ({(WorkstationCooldown - (Time.time - lastTime)):F0}s remaining)");
                    return false;
                }
            }
            
            return FindNearbyWorkstation() != null;
        }
        
        public override void Start()
        {
            base.Start();
            
            // Record that we're starting a workstation interaction (for cooldown tracking)
            string companionId = Companion?.companionId ?? Companion?.GetInstanceID().ToString() ?? "unknown";
            _lastWorkstationTime[companionId] = Time.time;
            
            if (_commandedStation != null)
            {
                _targetStation = _commandedStation;
                _commandedStation = null;

                _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
                _workPosition = CalculateWorkPosition(_targetStation);

                // EARLY RESERVATION: Claim immediately so concurrent companions don't pile in.
                if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, Character, MaxWorkDuration + 30f))
                {
                    LogVerbose($"Could not reserve {_targetStation.m_name} at Start - already taken");
                    _targetStation = null;
                    SetPhase(WorkPhase.Complete);
                    return;
                }

                SetPhase(WorkPhase.MovingToWorkstation);
                MoveToPosition(_workPosition);

                string stationName = GetWorkstationDisplayName(_targetStation);
                CompanionChatHelper.QuickMessages.WorkingAtStation(Companion, stationName);
            }
            
            _isCraftingAnimationActive = false;
            LogVerbose("Starting workstation interaction");
        }
        
        protected override bool UpdatePhase(WorkPhase phase)
        {
            return phase switch
            {
                WorkPhase.FindingWorkstation => UpdateFindingWorkstation(),
                WorkPhase.MovingToWorkstation => UpdateMovingToWorkstation(),
                WorkPhase.Working => UpdateWorking(),
                WorkPhase.Complete => CompleteAndFinish(),
                _ => true
            };
        }
        
        public override void Cancel()
        {
            ReleaseOccupancy();
            StopWorkAnimation();
            StopCraftingAnimation();  // Make sure crafting animation is stopped
            base.Cancel();
        }
        
        #endregion
        
        #region Combat Resumption
        
        protected override void SaveState()
        {
            _savedPhase = CurrentPhase;
            _savedStation = _targetStation;
            _savedWorkPosition = _workPosition;
            _savedWorkEndTime = _workEndTime;
            
            StopWorkAnimation();
            
            LogVerbose($"Saved state: phase={_savedPhase}, station={_savedStation?.m_name}");
        }
        
        protected override void RestoreState()
        {
            _targetStation = _savedStation;
            _workPosition = _savedWorkPosition;
            
            if (_targetStation == null || !_targetStation.gameObject.activeInHierarchy)
            {
                LogVerbose("Cannot restore - workstation no longer exists");
                SetPhase(WorkPhase.Complete);
                return;
            }
            
            _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
            
            if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, Character, MaxWorkDuration))
            {
                LogVerbose("Cannot restore - workstation is now occupied");
                SetPhase(WorkPhase.Complete);
                return;
            }
            
            float dist = DistanceTo(_workPosition);
            if (dist > InteractionDistance)
            {
                SetPhase(WorkPhase.MovingToWorkstation);
                MoveToPosition(_workPosition);
            }
            else
            {
                float remainingWorkTime = Mathf.Max(15f, _savedWorkEndTime - Time.time + 30f);
                _workEndTime = Time.time + remainingWorkTime;
                
                CombatMovement?.LockMovement("WorkstationWork", remainingWorkTime + 5f);
                
                SetPhase(WorkPhase.Working);
                StartCraftingAnimation();  // Use crafting animation instead of generic PlayWorkAnimation
                _lastAnimationTime = Time.time;
            }
            
            LogVerbose($"Restored state: resuming from phase={CurrentPhase}");
        }
        
        #endregion
        
        #region Phase Updates
        
        private bool UpdateFindingWorkstation()
        {
            _targetStation = FindNearbyWorkstation();

            if (_targetStation == null)
            {
                LogVerbose("Couldn't find workstation");
                Complete();
                return true;
            }

            // EARLY RESERVATION: Claim before we walk so other companions don't pick the same one.
            if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, Character, MaxWorkDuration + 30f))
            {
                LogVerbose($"Lost race for {_targetStation.m_name} - another companion got there first");
                _targetStation = null;
                Complete();
                return true;
            }

            _pieceData = PieceDataHelper.GetPieceData(_targetStation.gameObject);
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
            
            // CRITICAL: Call ContinueMovement() every frame for vanilla pathfinding to work!
            // This keeps calling MoveTo() which follows waypoints around obstacles.
            if (ContinueMovement())
            {
                // Arrived at destination
                StopMovement();
                StartWorking();
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
            
            FaceTarget(_targetStation.transform.position);

            // Single-writer: own the standstill at the station instead of a per-frame raw SetMoveDir(0).
            // Acquire SubBehavior (or PlayerCommand) authority if we don't hold it, then Hold — UMA's
            // single writer keeps the body at zero. If a higher source (combat/command) preempts us,
            // Hold no-ops and the sub-behavior's interruption path takes over; we never fight the channel.
            if (MovementAuthority != null)
            {
                var src = IsCommandInitiated
                    ? UnifiedMovementAuthority.MovementSource.PlayerCommand
                    : UnifiedMovementAuthority.MovementSource.SubBehavior;
                if (!MovementAuthority.HasAuthority(BehaviorName))
                    MovementAuthority.TryAcquireAuthority(src, BehaviorName, 5f);
                MovementAuthority.Hold(BehaviorName);
            }
            else if (Character != null)
            {
                // No-UMA fallback (should not happen for a real companion): keep the raw hold.
                Character.SetMoveDir(Vector3.zero);
                Character.SetWalk(false);
                Character.SetRun(false);
            }

            if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Vector3 vel = Rigidbody.linearVelocity;
                if (vel.x != 0 || vel.z != 0)
                {
                    Rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
                }
            }
            
            // Play work animation periodically
            if (Time.time - _lastAnimationTime >= WorkAnimationInterval)
            {
                PlayPeriodicWorkAnimation();  // Use renamed method
                _lastAnimationTime = Time.time;
            }
            
            if (Time.time >= _workEndTime)
            {
                ReleaseOccupancy();
                StopWorkAnimation();
                StopCraftingAnimation();  // Stop crafting animation when done
                CombatMovement?.UnlockMovement();
                
                // Fire event
                CompanionEvents.FireWorkStationCompleted(Companion, _targetStation.gameObject, 0, 0);
                
                SetPhase(WorkPhase.Complete);
                return true;
            }
            
            return false;
        }
        
        private void StartWorking()
        {
            if (!InteractableOccupancyManager.TryOccupy(_targetStation.gameObject, Character, MaxWorkDuration + 30f))
            {
                LogVerbose($"Could not occupy {_targetStation.m_name} - already taken");
                Complete();
                return;
            }
            
            // Fire start event
            string stationType = PieceDataHelper.GetCraftingStationType(_targetStation);
            CompanionEvents.FireWorkStationStarted(Companion, _targetStation.gameObject, stationType);
            
            float workDuration = Random.Range(MinWorkDuration, MaxWorkDuration);
            _workEndTime = Time.time + workDuration;
            
            StopMovement();
            CombatMovement?.LockMovement("WorkstationWork", workDuration + 5f);
            
            if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Rigidbody.linearVelocity = Vector3.zero;
                Rigidbody.angularVelocity = Vector3.zero;
            }
            
            FaceTarget(_targetStation.transform.position);
            
            SetPhase(WorkPhase.Working);
            
            // Start the crafting animation (continuous work stance)
            StartCraftingAnimation();
            
            // Also do an initial hammer swing
            PlayPeriodicWorkAnimation();
            _lastAnimationTime = Time.time;
            
            LogVerbose($"Started working at {_targetStation.m_name} for {workDuration:F0}s");
        }
        
        private bool CompleteAndFinish()
        {
            Complete();
            return true;
        }
        
        #endregion
        
        #region Animations & Repair
        
        /// <summary>
        /// Plays a periodic work animation (hammer swing) and attempts repair/upgrade.
        /// Uses the 'crafting' animation bool for proper workbench animations.
        /// </summary>
        private void PlayPeriodicWorkAnimation()
        {
            // Play hammer/crafting animation instead of generic interact
            // The 'crafting' bool triggers the proper workbench animation
            if (!_isCraftingAnimationActive)
            {
                StartCraftingAnimation();
            }
            
            // Also trigger an interact for the hammer swing effect
            // This makes the companion visually "hit" the workbench
            if (ZAnim != null)
            {
                ZAnim.SetTrigger("interact");
            }
            
            // Try to repair or upgrade items while working
            TryRepairItem();
            LogVerbose("Playing work animation");
        }
        
        /// <summary>
        /// Starts the crafting animation (continuous work stance).
        /// </summary>
        private void StartCraftingAnimation()
        {
            if (ZAnim != null)
            {
                ZAnim.SetBool("crafting", true);
                ZAnim.SetBool("Working", true);
            }
            _isCraftingAnimationActive = true;
            LogVerbose("Started crafting animation");
        }
        
        /// <summary>
        /// Stops the crafting animation.
        /// </summary>
        private void StopCraftingAnimation()
        {
            if (ZAnim != null)
            {
                ZAnim.SetBool("crafting", false);
                ZAnim.SetBool("Working", false);
            }
            _isCraftingAnimationActive = false;
            LogVerbose("Stopped crafting animation");
        }
        
        private void TryRepairItem()
        {
            if (Inventory == null) return;
            
            // Check upgrade chance first (upgrade is rarer than repair)
            if (Random.value <= UpgradeChancePerAnimation)
            {
                // Check if we're allowed to upgrade today
                if (CanUpgradeToday())
                {
                    TryUpgradeItem();
                }
                return;
            }
            
            if (Random.value <= FullRepairChance)
            {
                TryFullRepairItem();
                return;
            }
            
            if (Random.value > RepairChancePerAnimation) return;
            
            var damagedItems = GetDamagedItems();
            if (damagedItems.Count == 0) return;
            
            var itemToRepair = damagedItems[Random.Range(0, damagedItems.Count)];
            
            float maxDurability = itemToRepair.GetMaxDurability();
            float repairAmount = maxDurability * RepairAmountPercent;
            float oldDurability = itemToRepair.m_durability;
            float newDurability = Mathf.Min(itemToRepair.m_durability + repairAmount, maxDurability);
            float actualRepairPercent = (newDurability - oldDurability) / maxDurability;
            
            itemToRepair.m_durability = newDurability;
            
            string itemName = Localization.instance?.Localize(itemToRepair.m_shared?.m_name) ?? "item";
            
            LogVerbose($"Repaired {itemName}: {oldDurability:F0} -> {newDurability:F0} / {maxDurability:F0}");
            CompanionChatHelper.QuickMessages.ItemRepaired(Companion, itemName, actualRepairPercent);
        }
        
        /// <summary>
        /// Checks if the companion is allowed to upgrade items today.
        /// Companions can only upgrade once per in-game day to prevent excessive gear progression.
        /// </summary>
        private bool CanUpgradeToday()
        {
            if (Companion == null) return false;
            
            // Get current game day - EnvMan.GetDay() returns the current day number
            int currentDay = EnvMan.instance != null ? EnvMan.instance.GetDay(ZNet.instance?.GetTimeSeconds() ?? 0) : 0;
            
            // Try to get last upgrade day from companion's ZDO
            var nview = Companion.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return true;  // Allow if can't track
            
            int lastUpgradeDay = nview.GetZDO().GetInt(LastUpgradeDayKey, -1);
            
            // If never upgraded or last upgrade was at least 1 day ago, allow
            if (lastUpgradeDay < 0 || currentDay > lastUpgradeDay)
            {
                return true;
            }
            
            LogVerbose($"Cannot upgrade today - already upgraded on day {lastUpgradeDay}, current day {currentDay}");
            return false;
        }
        
        /// <summary>
        /// Records that an upgrade happened today.
        /// </summary>
        private void RecordUpgradeDay()
        {
            if (Companion == null) return;
            
            int currentDay = EnvMan.instance != null ? EnvMan.instance.GetDay(ZNet.instance?.GetTimeSeconds() ?? 0) : 0;
            
            var nview = Companion.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                nview.GetZDO().Set(LastUpgradeDayKey, currentDay);
            }
        }
        
        private void TryFullRepairItem()
        {
            var damagedItems = GetDamagedItems();
            if (damagedItems.Count == 0) return;
            
            var itemToRepair = damagedItems[Random.Range(0, damagedItems.Count)];
            itemToRepair.m_durability = itemToRepair.GetMaxDurability();
            
            string itemName = Localization.instance?.Localize(itemToRepair.m_shared?.m_name) ?? "item";
            LogVerbose($"FULLY repaired {itemName}!");
            
            var owner = Companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                CompanionChatHelper.SaySpeechBubble(Companion, $"Good as new! I fully repaired the {itemName}.", 5f);
            }
        }
        
        private void TryUpgradeItem()
        {
            var upgradableItems = GetUpgradableItems();
            if (upgradableItems.Count == 0)
            {
                TryFullRepairItem();
                return;
            }
            
            var itemToUpgrade = upgradableItems[Random.Range(0, upgradableItems.Count)];
            
            int oldQuality = itemToUpgrade.m_quality;
            int newQuality = oldQuality + 1;
            
            bool upgraded = Inventory.UpdateItemQuality(itemToUpgrade, newQuality);
            
            if (!upgraded)
            {
                LogWarning($"UpdateItemQuality failed for {itemToUpgrade.m_shared.m_name} - using fallback");
                itemToUpgrade.m_quality = newQuality;
                SaveInventory();
                Companion?.SaveCompanionToVault();
            }
            
            itemToUpgrade.m_durability = itemToUpgrade.GetMaxDurability();
            
            // Record that we upgraded today - can't upgrade again until next day
            RecordUpgradeDay();
            
            string itemName = Localization.instance?.Localize(itemToUpgrade.m_shared?.m_name) ?? "item";
            Debug.Log($"[WorkstationInteraction] {Companion?.companionName} UPGRADED {itemName} to quality {newQuality}!");
            
            CompanionChatHelper.QuickMessages.ItemUpgraded(Companion, itemName, newQuality);
        }
        
        private List<ItemDrop.ItemData> GetDamagedItems()
        {
            var damagedItems = new List<ItemDrop.ItemData>();
            
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
                var item = Inventory.GetEquippedItem(slot);
                if (item != null && ItemNeedsRepair(item))
                {
                    damagedItems.Add(item);
                }
            }
            
            var storageInv = GetStorageInventory();
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
        
        private List<ItemDrop.ItemData> GetUpgradableItems()
        {
            var upgradableItems = new List<ItemDrop.ItemData>();
            
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
                var item = Inventory.GetEquippedItem(slot);
                if (item != null && CanUpgradeItem(item))
                {
                    upgradableItems.Add(item);
                }
            }
            
            var storageInv = GetStorageInventory();
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
        
        private bool CanUpgradeItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            int maxQuality = item.m_shared.m_maxQuality;
            if (maxQuality <= 1 || item.m_quality >= maxQuality) return false;
            
            var itemType = item.m_shared.m_itemType;
            return itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
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
        }
        
        private bool ItemNeedsRepair(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || !item.m_shared.m_useDurability) return false;
            
            float maxDurability = item.GetMaxDurability();
            if (maxDurability <= 0) return false;
            
            // Only repair items that are actually damaged - below 75% durability
            // Previously was 95% which caused companions to "repair" items that were nearly full
            return item.m_durability / maxDurability < RepairThreshold;
        }
        
        private void StopWorkAnimation()
        {
            if (ZAnim != null)
            {
                ZAnim.SetBool("crafting", false);
            }
            LogVerbose("Stopped work animation");
        }
        
        #endregion
        
        #region Helpers
        
        private void ReleaseOccupancy()
        {
            if (_targetStation != null)
            {
                InteractableOccupancyManager.Release(_targetStation.gameObject, Character);
            }
        }
        
        private CraftingStation FindNearbyWorkstation()
        {
            CraftingStation bestStation = null;
            float bestDistance = float.MaxValue;
            
            float searchRadius = GetEffectiveSearchRadius(WorkstationDetectionRange);
            var colliders = Physics.OverlapSphere(SearchCenter, searchRadius);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null) continue;
                
                if (!InteractableOccupancyManager.CanUseInteractable(station.gameObject, Character))
                    continue;
                
                float dist = DistanceTo(station.transform.position);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestStation = station;
                }
            }
            
            return bestStation;
        }
        
        private Vector3 CalculateWorkPosition(CraftingStation station)
        {
            if (_pieceData != null)
            {
                return PieceDataHelper.GetSafeInteractionPosition(_pieceData, 1.5f);
            }
            
            Vector3 stationPos = station.transform.position;
            Vector3 stationForward = station.transform.forward;
            Vector3 workPos = stationPos + stationForward * 1.5f;
            
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
            if (_pieceData != null)
            {
                return PieceDataHelper.GetCraftingStationType(station);
            }
            
            if (station == null) return "workstation";
            
            if (!string.IsNullOrEmpty(station.m_name))
            {
                return Localization.instance?.Localize(station.m_name) ?? station.m_name;
            }
            
            return "workstation";
        }
        
        #endregion
    }
}
