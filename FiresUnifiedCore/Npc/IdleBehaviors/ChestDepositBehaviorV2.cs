using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Chest deposit behavior using the new WorkBehaviorBase infrastructure.
    /// Deposits items from companion inventory into nearby chests with smart stacking.
    /// 
    /// MIGRATED FROM: ChestDepositBehavior.cs
    /// USES: WorkBehaviorBase<TPhase>, ResourceAccessService, BehaviorPhaseManager
    /// </summary>
    public class ChestDepositBehaviorV2 : WorkBehaviorBase<ChestDepositBehaviorV2.DepositPhase>
    {
        public override string BehaviorName => "ChestDeposit";
        public override bool AvailableForIdleRotation => true;
        
        /// <summary>
        /// Returns HIGH priority when inventory needs deposit.
        /// This ensures deposit behavior runs BEFORE gathering behaviors when inventory is full.
        /// Priority scale: 100 = high (deposit first), 0 = normal, -100 = low (skip when full)
        /// </summary>
        public override int InventoryPriority
        {
            get
            {
                // If inventory needs deposit, return high priority
                if (InventoryNeedsDeposit())
                {
                    return 100; // High priority - should run first
                }
                return 0; // Normal priority when inventory has room
            }
        }
        
        #region Phase Enum
        
        public enum DepositPhase
        {
            Scanning,
            MovingToChest,
            Depositing,
            Organizing,
            FindingNextChest,
            Complete
        }
        
        #endregion
        
        #region Settings
        
        private float ChestDetectionRange => CompanionSettings.ChestSearchRadius;
        private float ChestClusterRange => CompanionSettings.ChestAutoSortRadius;
        private const float InteractionRange = 2.5f;
        private const float MaxDepositTime = 90f;
        
        // CRITICAL: These thresholds determine when deposit is HIGH priority (100)
        // At 60%, we were missing cases where companions said "bags full" but didn't deposit
        // Lowered to 50% to ensure deposit happens BEFORE inventory is completely full
        private const float WeightThresholdPercent = 0.50f;
        private const float SlotThresholdPercent = 0.50f;
        
        private static readonly HashSet<ItemDrop.ItemData.ItemType> DepositableTypes = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.Material,
            ItemDrop.ItemData.ItemType.Trophy,
            ItemDrop.ItemData.ItemType.Misc,
            ItemDrop.ItemData.ItemType.Ammo,
            ItemDrop.ItemData.ItemType.AmmoNonEquipable
        };
        
        private static readonly HashSet<ItemDrop.ItemData.ItemType> KeepTypes = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.OneHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Shield,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Utility,
            ItemDrop.ItemData.ItemType.Tool,
            ItemDrop.ItemData.ItemType.Torch
        };
        
        #endregion
        
        #region State
        
        private Container _targetChest;
        private int _totalItemsDeposited;
        private int _chestsVisited;
        private int _itemsOrganized;
        private List<Container> _clusterChests = new List<Container>();
        private HashSet<Container> _visitedChests = new HashSet<Container>();
        private Dictionary<string, int> _depositedItemCounts = new Dictionary<string, int>();
        private GameObject _commandedTarget;
        
        #endregion
        
        #region WorkBehaviorBase Implementation
        
        protected override DepositPhase InitialPhase => DepositPhase.Scanning;
        
        protected override float GetPhaseTimeout(DepositPhase phase)
        {
            return phase switch
            {
                DepositPhase.Scanning => 5f,
                DepositPhase.MovingToChest => 15f,
                DepositPhase.Depositing => 20f,
                DepositPhase.Organizing => 15f,
                DepositPhase.FindingNextChest => 5f,
                _ => 10f
            };
        }
        
        protected override string GetPhaseDescription(DepositPhase phase)
        {
            return phase switch
            {
                DepositPhase.Scanning => "Looking for chests",
                DepositPhase.MovingToChest => "Walking to chest",
                DepositPhase.Depositing => $"Depositing items ({_totalItemsDeposited} stored)",
                DepositPhase.Organizing => $"Organizing chests ({_itemsOrganized} moved)",
                DepositPhase.FindingNextChest => "Looking for more chests",
                _ => _totalItemsDeposited > 0 ? $"Stored {_totalItemsDeposited} items" : "Organizing inventory"
            };
        }
        
        protected override DepositPhase OnPhaseTimeout(DepositPhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");

            // When we fail to reach a chest, mark it visited so FindingNextChest
            // skips it and tries a different (hopefully reachable) chest.
            if (timedOutPhase == DepositPhase.MovingToChest && _targetChest != null)
                _visitedChests.Add(_targetChest);

            return timedOutPhase switch
            {
                DepositPhase.MovingToChest => DepositPhase.FindingNextChest,
                DepositPhase.Depositing => DepositPhase.FindingNextChest,
                DepositPhase.Organizing => HasItemsToDeposit() ? DepositPhase.FindingNextChest : DepositPhase.Complete,
                _ => DepositPhase.Complete
            };
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MaxDepositTime;
        }
        
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || Inventory == null) return false;

            // Commanded deposits always run.  Autonomous deposit/organization is
            // gated by the radial-menu toggle.
            if (_commandedTarget == null && !CompanionBehaviorToggles.IsOrganizeEnabled(Companion)) return false;

            if (_commandedTarget != null)
            {
                var targetContainer = _commandedTarget.GetComponent<Container>();
                if (targetContainer != null && HasItemsToDeposit())
                {
                    LogVerbose($"CanStart: TRUE - commanded target chest, hasItemsToDeposit=true");
                    return true;
                }
            }
            
            // Check if there are nearby chests
            if (!Resources.HasNearbyChests && Resources.RefreshNearbyChests() == 0)
            {
                LogVerbose($"CanStart: FALSE - no chests nearby");
                return false;
            }
            
            // Check if we have items to deposit
            if (!HasItemsToDeposit())
            {
                LogVerbose($"CanStart: FALSE - no items to deposit");
                return false;
            }
            
            // PRIORITY 1: Always start if inventory is near capacity (50%+ weight or slots)
            if (IsInventoryNearCapacity())
            {
                LogVerbose($"CanStart: TRUE - inventory near capacity, {Resources.ChestCount} chests nearby");
                return true;
            }
            
            // PRIORITY 2: For stayed companions, also check if chests need organization
            // This allows companions to proactively organize storage even with low inventory
            bool isStaying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            if (isStaying && Resources.ChestCount >= 2)
            {
                // Check if there are items that could be better organized (stacks to consolidate)
                if (ShouldOrganizeChests())
                {
                    LogVerbose($"CanStart: TRUE - staying companion can organize {Resources.ChestCount} chests");
                    return true;
                }
            }
            
            // PRIORITY 3: Lower threshold for stayed companions (30% instead of 50%)
            // Stayed companions should deposit more frequently to keep inventory clear for gathering
            if (isStaying)
            {
                var status = Resources.GetInventoryStatus();
                float slotUsage = status.TotalSlots > 0 
                    ? (float)(status.TotalSlots - status.FreeSlots) / status.TotalSlots 
                    : 1f;
                    
                // 30% threshold for stayed companions
                if (status.WeightPercent >= 0.30f || slotUsage >= 0.30f)
                {
                    LogVerbose($"CanStart: TRUE - stayed companion (30% threshold met: weight={status.WeightPercent:P0}, slots={slotUsage:P0}), {Resources.ChestCount} chests nearby");
                    return true;
                }
            }
            
            LogVerbose($"CanStart: FALSE - inventory not full enough");
            return false;
        }
        
        /// <summary>
        /// Checks if nearby chests would benefit from organization.
        /// Returns true if there are partial stacks of the same item in different chests.
        /// </summary>
        private bool ShouldOrganizeChests()
        {
            if (Resources.ChestCount < 2) return false;
            
            // Build a map of item types to their stacks across chests
            var itemLocations = new Dictionary<string, List<(Container chest, int stack, int maxStack)>>();
            
            foreach (var chest in Resources.NearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;
                
                foreach (var item in inv.GetAllItems())
                {
                    if (item == null) continue;
                    string key = item.m_shared?.m_name ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    
                    if (!itemLocations.ContainsKey(key))
                        itemLocations[key] = new List<(Container, int, int)>();
                    
                    itemLocations[key].Add((chest, item.m_stack, item.m_shared.m_maxStackSize));
                }
            }
            
            // Check if any item type has partial stacks in multiple chests
            foreach (var kvp in itemLocations)
            {
                if (kvp.Value.Count < 2) continue; // Only in one chest
                
                // Check if there are partial stacks that could be consolidated
                int partialStackCount = 0;
                foreach (var entry in kvp.Value)
                {
                    if (entry.stack < entry.maxStack)
                        partialStackCount++;
                }
                
                // If we have 2+ partial stacks of the same item, organization would help
                if (partialStackCount >= 2)
                    return true;
            }
            
            return false;
        }
        
        public override void Start()
        {
            base.Start();
            
            _totalItemsDeposited = 0;
            _chestsVisited = 0;
            _itemsOrganized = 0;
            _clusterChests.Clear();
            _visitedChests.Clear();
            _depositedItemCounts.Clear();
            _targetChest = null;
            
            if (_commandedTarget != null)
            {
                var targetContainer = _commandedTarget.GetComponent<Container>();
                if (targetContainer != null)
                {
                    _targetChest = targetContainer;
                    _commandedTarget = null;
                    
                    SetPhase(DepositPhase.MovingToChest);
                    MoveToPosition(_targetChest.transform.position);
                    CompanionChatHelper.QuickMessages.DepositingItems(Companion);
                    return;
                }
                _commandedTarget = null;
            }
            
            LogVerbose("Starting chest deposit behavior");
        }
        
        protected override bool UpdatePhase(DepositPhase phase)
        {
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            return phase switch
            {
                DepositPhase.Scanning => UpdateScanning(),
                DepositPhase.MovingToChest => UpdateMovingToChest(),
                DepositPhase.Depositing => UpdateDepositing(),
                DepositPhase.Organizing => UpdateOrganizing(),
                DepositPhase.FindingNextChest => UpdateFindingNextChest(),
                DepositPhase.Complete => CompleteAndNotify(),
                _ => true
            };
        }
        
        public override void Cancel()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            if (_totalItemsDeposited > 0)
            {
                NotifyOwnerViaChat();
            }
            
            base.Cancel();
        }
        
        #endregion
        
        #region Phase Updates
        
        private bool UpdateScanning()
        {
            Resources.RefreshNearbyChests(true);
            
            if (!Resources.HasNearbyChests)
            {
                LogVerbose("Found no chests nearby");
                SetPhase(DepositPhase.Complete);
                return false;
            }
            
            _targetChest = FindBestChestForDeposit();
            
            if (_targetChest == null)
            {
                LogVerbose("No suitable chest found");
                SetPhase(DepositPhase.Complete);
                return false;
            }
            
            SetPhase(DepositPhase.MovingToChest);
            
            // Use InteractionPointHelper to get proper standing position in front of chest
            Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, Transform.position, InteractionPointHelper.DEFAULT_INTERACTION_DISTANCE);
            
            MoveToPosition(interactionPoint);
            
            LogVerbose($"Moving to chest interaction point at {interactionPoint} (chest at {_targetChest.transform.position})");
            return false;
        }
        
        private bool UpdateMovingToChest()
        {
            if (_targetChest == null)
            {
                SetPhase(DepositPhase.FindingNextChest);
                return false;
            }
            
            // Check if we're close enough to the CHEST (not the move target) to interact
            // This handles cases where pathfinding takes us to a slightly different spot
            float distToChest = DistanceTo(_targetChest.transform.position);
            if (distToChest < InteractionRange)
            {
                // Close enough to interact!
                StopMovement();
                FaceTarget(_targetChest.transform.position);
                PlayInteractAnimation();
                SetPhase(DepositPhase.Depositing);
                return false;
            }
            
            // CRITICAL: Call ContinueMovement() every frame for vanilla pathfinding to work!
            if (ContinueMovement())
            {
                // Arrived at movement target - also check chest distance as backup
                StopMovement();
                FaceTarget(_targetChest.transform.position);
                PlayInteractAnimation();
                SetPhase(DepositPhase.Depositing);
            }
            
            return false;
        }
        
        private bool UpdateDepositing()
        {
            if (_targetChest == null || Inventory == null)
            {
                SetPhase(DepositPhase.FindingNextChest);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetChest.transform.position);
            PlayInteractAnimation();
            
            // Find all chests in cluster
            _clusterChests = ChestHelper.FindNearbyChests(_targetChest.transform.position, ChestClusterRange);
            LogVerbose($"Found {_clusterChests.Count} chests within {ChestClusterRange}m cluster");
            
            // Deposit items
            int deposited = DepositItemsToCluster();
            _totalItemsDeposited += deposited;
            _chestsVisited++;
            _visitedChests.Add(_targetChest);
            
            foreach (var chest in _clusterChests)
            {
                _visitedChests.Add(chest);
            }
            
            // Fire event
            if (deposited > 0)
            {
                CompanionEvents.FireItemDeposited(Companion, "mixed", deposited);
            }

            LogVerbose($"Deposited {deposited} items across {_clusterChests.Count} chests");

            // Always run the organize pass - even a single chest benefits from stack
            // consolidation, and it ensures chests are saved after deposit.
            SetPhase(DepositPhase.Organizing);
            
            return false;
        }
        
        private bool UpdateOrganizing()
        {
            if (_clusterChests.Count < 2)
            {
                SetPhase(HasItemsToDeposit() ? DepositPhase.FindingNextChest : DepositPhase.Complete);
                return false;
            }
            
            PlayInteractAnimation();
            
            int organized = OrganizeClusterChests();
            _itemsOrganized += organized;
            
            if (organized > 0)
            {
                LogVerbose($"Organized {organized} items across chests");
            }
            
            SetPhase(HasItemsToDeposit() ? DepositPhase.FindingNextChest : DepositPhase.Complete);
            return false;
        }
        
        private bool UpdateFindingNextChest()
        {
            Resources.RefreshNearbyChests(true);
            
            _targetChest = FindBestChestForDeposit();
            
            if (_targetChest == null)
            {
                SetPhase(DepositPhase.Complete);
                return false;
            }
            
            SetPhase(DepositPhase.MovingToChest);
            
            // Use InteractionPointHelper to get proper standing position in front of chest
            Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, Transform.position, InteractionPointHelper.DEFAULT_INTERACTION_DISTANCE);
            
            MoveToPosition(interactionPoint);
            return false;
        }
        
        private bool CompleteAndNotify()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            NotifyOwnerViaChat();
            Complete();
            return true;
        }
        
        #endregion
        
        #region Deposit Logic
        
        /// <summary>
        /// Checks if the inventory needs to deposit items (used by InventoryPriority).
        /// Returns true if inventory is near capacity AND has depositable items.
        /// </summary>
        private new bool InventoryNeedsDeposit()
        {
            return IsInventoryNearCapacity() && HasItemsToDeposit();
        }
        
        private bool IsInventoryNearCapacity()
        {
            var status = Resources.GetInventoryStatus();
            // WeightPercent is 0-1 (as fraction), THRESHOLD is 0-1, so multiply by 100 to compare
            bool weightNearFull = status.WeightPercent >= WeightThresholdPercent;
            
            // Calculate slot usage from FreeSlots and TotalSlots
            float slotUsage = status.TotalSlots > 0 
                ? (float)(status.TotalSlots - status.FreeSlots) / status.TotalSlots 
                : 1f;
            bool slotsNearFull = slotUsage >= SlotThresholdPercent;
            
            return weightNearFull || slotsNearFull;
        }
        
        private bool HasItemsToDeposit()
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;
            
            foreach (var item in storage.GetAllItems())
            {
                if (item != null && ShouldDepositItem(item))
                    return true;
            }
            return false;
        }
        
        private bool ShouldDepositItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (item.m_equipped)
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (equipped)");
                return false;
            }
            if (item.m_shared.m_questItem)
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (quest item)");
                return false;
            }
            
            var itemType = item.m_shared.m_itemType;
            
            if (KeepTypes.Contains(itemType))
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (keep type: {itemType})");
                return false;
            }
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (consumable/food)");
                return false;
            }
            
            if (item.IsWeapon())
            {
                int meleeCount = CountWeaponsInInventory(false);
                int rangedCount = CountWeaponsInInventory(true);
                bool isRanged = IsRangedWeapon(item);
                
                if (isRanged && rangedCount <= 1)
                {
                    LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (last ranged weapon)");
                    return false;
                }
                if (!isRanged && meleeCount <= 1)
                {
                    LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (last melee weapon)");
                    return false;
                }
            }
            
            string itemPrefab = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            bool isPickaxe = itemPrefab.Contains("pickaxe");
            bool isAxe = itemPrefab.Contains("axe") && !isPickaxe;
            
            if ((isPickaxe || isAxe) && CountToolsOfType(isPickaxe ? "pickaxe" : "axe") <= 1)
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - NO (last tool of type)");
                return false;
            }
            
            if (DepositableTypes.Contains(itemType))
            {
                LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - YES (depositable type: {itemType})");
                return true;
            }
            
            LogVerbose($"ShouldDepositItem: {item.m_shared?.m_name} - YES (default depositable)");
            return true;
        }
        
        private int CountWeaponsInInventory(bool ranged)
        {
            int count = 0;
            var storage = GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item != null && item.IsWeapon() && IsRangedWeapon(item) == ranged)
                        count++;
                }
            }
            return count;
        }
        
        private int CountToolsOfType(string toolType)
        {
            int count = 0;
            var storage = GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item == null) continue;
                    string prefab = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                    if (toolType == "pickaxe" && prefab.Contains("pickaxe")) count++;
                    else if (toolType == "axe" && prefab.Contains("axe") && !prefab.Contains("pickaxe")) count++;
                }
            }
            return count;
        }
        
        private bool IsRangedWeapon(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Bows) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Crossbows) return true;
            if (item.m_shared.m_attack?.m_attackProjectile != null) return true;
            return false;
        }
        
        private Container FindBestChestForDeposit()
        {
            var storage = GetStorageInventory();
            if (storage == null) return null;
            
            Container bestChest = null;
            int bestScore = -1;
            float bestDistance = float.MaxValue;
            
            foreach (var chest in Resources.NearbyChests)
            {
                if (chest == null || _visitedChests.Contains(chest)) continue;

                var nview = chest.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                var chestInv = chest.GetInventory();
                if (chestInv == null || chestInv.GetEmptySlots() == 0) continue;

                // Skip chests we can't pathfind to — mark visited so we don't retry this session.
                if (!IsReachable(chest.transform.position))
                {
                    _visitedChests.Add(chest);
                    continue;
                }

                int matchScore = 0;
                foreach (var item in storage.GetAllItems())
                {
                    if (item == null || !ShouldDepositItem(item)) continue;
                    if (ChestContainsMatchingItem(chestInv, item))
                        matchScore += 10;
                    else if (chestInv.GetEmptySlots() > 0)
                        matchScore += 1;
                }

                float distance = DistanceTo(chest.transform.position);

                if (matchScore > bestScore || (matchScore == bestScore && distance < bestDistance))
                {
                    bestScore = matchScore;
                    bestDistance = distance;
                    bestChest = chest;
                }
            }
            
            return bestChest;
        }
        
        private bool ChestContainsMatchingItem(Inventory chestInv, ItemDrop.ItemData item)
        {
            if (chestInv == null || item == null) return false;
            
            string itemName = item.m_shared?.m_name ?? "";
            
            foreach (var chestItem in chestInv.GetAllItems())
            {
                if (chestItem == null) continue;
                if (itemName.Equals(chestItem.m_shared?.m_name ?? "", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (chestItem.m_stack < chestItem.m_shared.m_maxStackSize)
                        return true;
                }
            }
            
            return false;
        }
        
        private int DepositItemsToCluster()
        {
            var storage = GetStorageInventory();
            if (storage == null || _clusterChests.Count == 0)
            {
                LogVerbose($"DepositItemsToCluster: No storage ({storage == null}) or no chests ({_clusterChests.Count})");
                return 0;
            }
            
            int totalDeposited = 0;
            int skippedItems = 0;
            int failedDeposits = 0;
            var dirtiedChests = new HashSet<Container>();
            var itemsToCheck = new List<ItemDrop.ItemData>(storage.GetAllItems());
            
            LogVerbose($"DepositItemsToCluster: Checking {itemsToCheck.Count} items in inventory against {_clusterChests.Count} chests");
            
            foreach (var item in itemsToCheck)
            {
                if (item == null) continue;
                
                if (!ShouldDepositItem(item))
                {
                    skippedItems++;
                    continue;
                }
                
                Container bestChest = FindBestChestForItem(item);
                
                if (bestChest == null)
                {
                    LogVerbose($"DepositItemsToCluster: No chest found for {item.m_shared?.m_name}");
                    failedDeposits++;
                    continue;
                }
                
                var chestInv = bestChest.GetInventory();
                if (chestInv == null)
                {
                    LogVerbose($"DepositItemsToCluster: Chest has null inventory");
                    failedDeposits++;
                    continue;
                }

                if (!chestInv.CanAddItem(item))
                {
                    LogVerbose($"DepositItemsToCluster: Chest cannot accept {item.m_shared?.m_name} (full or wrong type?)");
                    failedDeposits++;
                    continue;
                }
                
                var clone = item.Clone();
                if (chestInv.AddItem(clone))
                {
                    storage.RemoveItem(item);
                    totalDeposited++;
                    dirtiedChests.Add(bestChest);

                    string itemName = Localization.instance.Localize(item.m_shared?.m_name ?? "Item");
                    if (_depositedItemCounts.ContainsKey(itemName))
                        _depositedItemCounts[itemName] += item.m_stack;
                    else
                        _depositedItemCounts[itemName] = item.m_stack;

                    LogVerbose($"DepositItemsToCluster: Deposited {item.m_stack}x {item.m_shared?.m_name}");
                }
                else
                {
                    LogVerbose($"DepositItemsToCluster: AddItem failed for {item.m_shared?.m_name}");
                    failedDeposits++;
                }
            }

            if (totalDeposited > 0)
            {
                // Save companion inventory and every chest that received items
                SaveInventory();
                foreach (var chest in dirtiedChests)
                    SmartStorageOrganizer.SaveContainer(chest);
            }
            
            LogVerbose($"DepositItemsToCluster: Deposited {totalDeposited}, skipped {skippedItems} (keep types), failed {failedDeposits}");
            
            return totalDeposited;
        }
        
        private Container FindBestChestForItem(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            
            Vector3 searchCenter = _targetChest?.transform.position ?? Transform.position;
            
            var recommendation = SmartStorageOrganizer.FindBestChestForItem(
                item, _clusterChests, searchCenter, stationSearchRadius: 25f);
            
            if (recommendation != null)
                return recommendation.Chest;
            
            // Fallback
            Container matchingChest = null;
            Container emptySlotChest = null;
            
            foreach (var chest in _clusterChests)
            {
                if (chest == null) continue;
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                if (ChestContainsMatchingItem(chestInv, item))
                    matchingChest = chest;
                
                if (emptySlotChest == null && chestInv.GetEmptySlots() > 0)
                    emptySlotChest = chest;
            }
            
            return matchingChest ?? emptySlotChest;
        }
        
        private int OrganizeClusterChests()
        {
            if (_clusterChests.Count < 2) return 0;
            
            Vector3 clusterCenter = _targetChest?.transform.position ?? Transform.position;
            
            var result = SmartStorageOrganizer.OrganizeChestCluster(
                _clusterChests, clusterCenter, stationSearchRadius: 25f);
            
            return result.ItemsMoved + result.StacksConsolidated;
        }
        
        #endregion
        
        #region Notifications
        
        private void NotifyOwnerViaChat()
        {
            if (_totalItemsDeposited == 0) return;
            
            string taskDescription;
            if (_depositedItemCounts.Count <= 3)
            {
                var itemList = _depositedItemCounts.Select(kvp => $"{kvp.Value}x {kvp.Key}").ToList();
                taskDescription = $"stored {string.Join(", ", itemList)} in chests";
            }
            else
            {
                taskDescription = $"stored {_totalItemsDeposited} items in {_chestsVisited} chest(s)";
            }
            
            CompanionChatHelper.NotifyTaskComplete(Companion, taskDescription);
        }
        
        #endregion
    }
}
