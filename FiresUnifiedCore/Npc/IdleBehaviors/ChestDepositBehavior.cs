// TODO: REMOVE AFTER TESTING V2 - This file is no longer registered in CompanionIdleBehavior.SubBehaviors.cs
// ChestDepositBehaviorV2 is now used instead. Remove this file once V2 is confirmed working.
// See: CompanionIdleBehavior.SubBehaviors.cs InitializeSubBehaviors()

using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Idle sub-behavior that deposits items from companion inventory into nearby chests.
    /// Triggers automatically when inventory is near capacity or weight limit.
    /// 
    /// FLOW:
    /// 1. Detect that inventory is getting full (slots or weight)
    /// 2. Scan for nearby chests (within 15m)
    /// 3. Walk to nearest suitable chest
    /// 4. SCAN ALL chests within 10m radius of that chest
    /// 5. Deposit items - STACKING with existing items across ALL nearby chests
    /// 6. ORGANIZE: Move items between chests to consolidate stacks
    /// 7. Complete and notify owner via chat
    /// 
    /// DEPOSIT PRIORITY:
    /// 1. Stack with existing items in ANY chest within range (matching items first)
    /// 2. Materials and resources (always deposit)
    /// 3. Trophies (deposit to trophy-containing chests)
    /// 4. Keep equipped items and consumables companion might need
    /// 
    /// ORGANIZATION:
    /// When depositing, also scans nearby chests and consolidates partial stacks.
    /// </summary>
    public class ChestDepositBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "ChestDeposit";
        
        /// <summary>
        /// Chest deposit is always available for idle rotation.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        #region Settings
        
        // Use centralized settings from CompanionSettings
        private float CHEST_DETECTION_RANGE => CompanionSettings.ChestSearchRadius;
        private float CHEST_CLUSTER_RANGE => CompanionSettings.ChestAutoSortRadius;
        private const float INTERACTION_RANGE = 2.5f;
        private const float MAX_DEPOSIT_TIME = 90f;  // Increased for organization
        
        // Thresholds for triggering deposit behavior
        private const float WEIGHT_THRESHOLD_PERCENT = 0.60f;  // 60% of max carry weight
        private const float SLOT_THRESHOLD_PERCENT = 0.60f;    // 60% of slots used
        
        // Item types to deposit (materials, trophies, misc resources)
        private static readonly HashSet<ItemDrop.ItemData.ItemType> DepositableTypes = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.Material,
            ItemDrop.ItemData.ItemType.Trophy,
            ItemDrop.ItemData.ItemType.Misc,
            ItemDrop.ItemData.ItemType.Ammo,
            ItemDrop.ItemData.ItemType.AmmoNonEquipable
        };
        
        // Item types to keep (equipment, consumables the companion might use)
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
            ItemDrop.ItemData.ItemType.Torch  // Torches are useful for companions
        };
        
        // Minimum counts of each essential item type to keep
        // Companions should always keep at least one primary weapon and optionally one ranged
        private const int MIN_MELEE_WEAPONS_TO_KEEP = 1;
        private const int MIN_RANGED_WEAPONS_TO_KEEP = 1;
        private const int MIN_FOOD_STACKS_TO_KEEP = 2;
        
        #endregion
        
        #region State
        
        private enum DepositPhase
        {
            Scanning,
            MovingToChest,
            Depositing,
            Organizing,        // NEW: Organize items across nearby chests
            FindingNextChest,
            Complete
        }
        
        private DepositPhase _currentPhase = DepositPhase.Scanning;
        private Container _targetChest;
        private float _phaseStartTime;
        private int _totalItemsDeposited;
        private int _chestsVisited;
        private int _itemsOrganized;   // Track items moved during organization
        private List<Container> _nearbyChests = new List<Container>();
        private List<Container> _clusterChests = new List<Container>();  // All chests within 10m of target
        private HashSet<Container> _visitedChests = new HashSet<Container>();
        
        // Track what items were deposited for chat notification
        private Dictionary<string, int> _depositedItemCounts = new Dictionary<string, int>();
        
        // Commanded target (set by command system for direct chest deposit)
        private GameObject _commandedTarget;
        
        // Components
        private Character _character;
        private CompanionInventory _inventory;
        private CompanionCombatMovement _combatMovement;
        private ZSyncAnimation _zanim;
        private Rigidbody _rigidbody;
        
        #endregion
        
        /// <summary>
        /// Sets a specific chest as the target (used by command system).
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            
            MaxDuration = MAX_DEPOSIT_TIME;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || _inventory == null) return false;
            
            // If commanded to a specific chest, always allow (as long as we have items)
            if (_commandedTarget != null)
            {
                var targetContainer = _commandedTarget.GetComponent<Container>();
                if (targetContainer != null && HasItemsToDeposit())
                {
                    return true;
                }
            }
            
            // Check if inventory is getting full (weight or slots)
            if (!IsInventoryNearCapacity()) return false;
            
            // Check if there are nearby chests
            var chests = ChestHelper.FindNearbyChests(Transform.position, CHEST_DETECTION_RANGE);
            if (chests.Count == 0) return false;
            
            // Check if we have items to deposit
            if (!HasItemsToDeposit()) return false;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] {Companion.companionName} CAN start - inventory near capacity, {chests.Count} chests nearby");
            
            return true;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = DepositPhase.Scanning;
            _phaseStartTime = Time.time;
            _totalItemsDeposited = 0;
            _chestsVisited = 0;
            _itemsOrganized = 0;
            _nearbyChests.Clear();
            _clusterChests.Clear();
            _visitedChests.Clear();
            _depositedItemCounts.Clear();
            _targetChest = null;
            
            // If commanded to a specific chest, use that as the target
            if (_commandedTarget != null)
            {
                var targetContainer = _commandedTarget.GetComponent<Container>();
                if (targetContainer != null)
                {
                    _targetChest = targetContainer;
                    _nearbyChests.Add(targetContainer);
                    _commandedTarget = null;
                    
                    SetPhase(DepositPhase.MovingToChest);
                    MoveToPosition(_targetChest.transform.position);
                    
                    // Chat feedback
                    CompanionChatHelper.QuickMessages.DepositingItems(Companion);
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ChestDeposit] {Companion.companionName} commanded to deposit to specific chest");
                    
                    return;
                }
                _commandedTarget = null;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] {Companion.companionName} starting chest deposit behavior");
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestDeposit] {Companion.companionName} timed out");
                NotifyOwnerAndComplete();
                return true;
            }
            
            // Show current status above companion's head
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            switch (_currentPhase)
            {
                case DepositPhase.Scanning:
                    return UpdateScanning();
                    
                case DepositPhase.MovingToChest:
                    return UpdateMovingToChest();
                    
                case DepositPhase.Depositing:
                    return UpdateDepositing();
                    
                case DepositPhase.Organizing:
                    return UpdateOrganizing();
                    
                case DepositPhase.FindingNextChest:
                    return UpdateFindingNextChest();
                    
                case DepositPhase.Complete:
                    NotifyOwnerAndComplete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            StopMovement();
            
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            // Still notify if we deposited anything
            if (_totalItemsDeposited > 0)
            {
                NotifyOwnerViaChat();
            }
            
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            return _currentPhase switch
            {
                DepositPhase.Scanning => "Looking for chests",
                DepositPhase.MovingToChest => "Walking to chest",
                DepositPhase.Depositing => $"Depositing items ({_totalItemsDeposited} stored)",
                DepositPhase.Organizing => $"Organizing chests ({_itemsOrganized} moved)",
                DepositPhase.FindingNextChest => "Looking for more chests",
                _ => _totalItemsDeposited > 0 ? $"Stored {_totalItemsDeposited} items" : "Organizing inventory"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateScanning()
        {
            // Find all nearby chests
            _nearbyChests = ChestHelper.FindNearbyChests(Transform.position, CHEST_DETECTION_RANGE);
            
            if (_nearbyChests.Count == 0)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestDeposit] {Companion.companionName} found no chests nearby");
                SetPhase(DepositPhase.Complete);
                return true;
            }
            
            // Find best chest to deposit to
            _targetChest = FindBestChestForDeposit();
            
            if (_targetChest == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestDeposit] {Companion.companionName} no suitable chest found");
                SetPhase(DepositPhase.Complete);
                return true;
            }
            
            SetPhase(DepositPhase.MovingToChest);
            MoveToPosition(_targetChest.transform.position);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] {Companion.companionName} moving to chest at {_targetChest.transform.position}");
            
            return false;
        }
        
        private bool UpdateMovingToChest()
        {
            // Check if chest still exists
            if (_targetChest == null)
            {
                SetPhase(DepositPhase.FindingNextChest);
                return false;
            }
            
            float dist = Vector3.Distance(Transform.position, _targetChest.transform.position);
            
            if (dist < INTERACTION_RANGE)
            {
                StopMovement();
                FaceTarget(_targetChest.transform.position);
                PlayInteractAnimation();
                SetPhase(DepositPhase.Depositing);
                return false;
            }
            
            // Keep moving
            MoveToPosition(_targetChest.transform.position);
            
            // Timeout on movement (15 seconds)
            if (Time.time - _phaseStartTime > 15f)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ChestDeposit] {Companion.companionName} couldn't reach chest, skipping");
                
                _visitedChests.Add(_targetChest);
                SetPhase(DepositPhase.FindingNextChest);
            }
            
            return false;
        }
        
        private bool UpdateDepositing()
        {
            if (_targetChest == null || _inventory == null)
            {
                SetPhase(DepositPhase.FindingNextChest);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetChest.transform.position);
            
            // Play interact animation
            PlayInteractAnimation();
            
            // CRITICAL: Find ALL chests within 10m of this chest for smart stacking
            _clusterChests = ChestHelper.FindNearbyChests(_targetChest.transform.position, CHEST_CLUSTER_RANGE);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] Found {_clusterChests.Count} chests within {CHEST_CLUSTER_RANGE}m cluster");
            
            // Deposit items - prioritizing stacking across ALL cluster chests
            int deposited = DepositItemsToCluster();
            _totalItemsDeposited += deposited;
            _chestsVisited++;
            _visitedChests.Add(_targetChest);
            
            // Mark all cluster chests as visited
            foreach (var chest in _clusterChests)
            {
                _visitedChests.Add(chest);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] {Companion.companionName} deposited {deposited} items across {_clusterChests.Count} chests");
            
            // After depositing, organize the cluster chests
            if (_clusterChests.Count > 1)
            {
                SetPhase(DepositPhase.Organizing);
            }
            else if (HasItemsToDeposit())
            {
                SetPhase(DepositPhase.FindingNextChest);
            }
            else
            {
                SetPhase(DepositPhase.Complete);
            }
            
            return false;
        }
        
        private bool UpdateFindingNextChest()
        {
            // Refresh chest list
            _nearbyChests = ChestHelper.FindNearbyChests(Transform.position, CHEST_DETECTION_RANGE);
            
            // Find next unvisited chest with room
            _targetChest = FindBestChestForDeposit();
            
            if (_targetChest == null)
            {
                // No more chests with room available
                // Try to spawn a new chest above the last visited one
                if (_visitedChests.Count > 0 && HasItemsToDeposit())
                {
                    // Get the last visited chest as reference
                    Container referenceChest = null;
                    foreach (var chest in _visitedChests)
                    {
                        if (chest != null)
                        {
                            referenceChest = chest;
                            break;
                        }
                    }
                    
                    if (referenceChest != null)
                    {
                        // Try to find or spawn a chest with room
                        var newChest = ChestHelper.FindOrSpawnChestWithRoom(referenceChest, CHEST_DETECTION_RANGE);
                        if (newChest != null && !_visitedChests.Contains(newChest))
                        {
                            _targetChest = newChest;
                            _nearbyChests.Add(newChest);
                            
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ChestDeposit] {Companion?.companionName} spawned/found new chest for overflow items");
                            
                            SetPhase(DepositPhase.MovingToChest);
                            MoveToPosition(_targetChest.transform.position);
                            return false;
                        }
                    }
                }
                
                // No chests available and couldn't spawn one
                SetPhase(DepositPhase.Complete);
                return false;
            }
            
            SetPhase(DepositPhase.MovingToChest);
            MoveToPosition(_targetChest.transform.position);
            
            return false;
        }
        
        /// <summary>
        /// Organizes items across cluster chests - consolidating partial stacks.
        /// </summary>
        private bool UpdateOrganizing()
        {
            if (_clusterChests.Count < 2)
            {
                // Nothing to organize
                if (HasItemsToDeposit())
                    SetPhase(DepositPhase.FindingNextChest);
                else
                    SetPhase(DepositPhase.Complete);
                return false;
            }
            
            // Play interact animation for organization
            PlayInteractAnimation();
            
            // Organize: Move partial stacks to consolidate
            int organized = OrganizeClusterChests();
            _itemsOrganized += organized;
            
            if (CompanionIdleBehavior.VerboseLogging && organized > 0)
                Debug.Log($"[ChestDeposit] Organized {organized} items across chests");
            
            // Check if we still have items to deposit
            if (HasItemsToDeposit())
            {
                SetPhase(DepositPhase.FindingNextChest);
            }
            else
            {
                SetPhase(DepositPhase.Complete);
            }
            
            return false;
        }
        
        #endregion
        
        #region Deposit Logic
        
        /// <summary>
        /// Checks if the companion's inventory is near capacity.
        /// </summary>
        private bool IsInventoryNearCapacity()
        {
            if (_inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            // Check weight
            float currentWeight = _inventory.GetTotalWeight();
            float maxWeight = _inventory.GetMaxCarryWeight();
            if (currentWeight / maxWeight >= WEIGHT_THRESHOLD_PERCENT)
            {
                return true;
            }
            
            // Check slots using proper empty slot count
            int totalSlots = storageInv.GetWidth() * storageInv.GetHeight();
            int emptySlots = storageInv.GetEmptySlots();
            int usedSlots = totalSlots - emptySlots;
            if ((float)usedSlots / totalSlots >= SLOT_THRESHOLD_PERCENT)
            {
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if we have items that can be deposited.
        /// </summary>
        private bool HasItemsToDeposit()
        {
            if (_inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                if (ShouldDepositItem(item))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Determines if an item should be deposited to chests.
        /// CRITICAL: This method protects essential combat gear from being deposited.
        /// Companions MUST keep at least one primary weapon and optionally one ranged weapon.
        /// </summary>
        private bool ShouldDepositItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            
            var itemType = item.m_shared.m_itemType;
            
            // Never deposit equipped items
            if (item.m_equipped) return false;
            
            // Never deposit quest items
            if (item.m_shared.m_questItem) return false;
            
            // Keep equipment types (weapons, armor, tools)
            if (KeepTypes.Contains(itemType)) return false;
            
            // NEVER deposit consumables (food) - companions need these for healing/stamina
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                return false;
            }
            
            // CRITICAL: Double-check weapons are protected even if not in KeepTypes
            // This catches edge cases where items are weapons but have unusual ItemTypes
            if (item.IsWeapon())
            {
                // Count how many weapons companion would have left after depositing this one
                int meleeCount = CountWeaponsInInventory(false);
                int rangedCount = CountWeaponsInInventory(true);
                
                bool isRanged = IsRangedWeapon(item);
                
                if (isRanged && rangedCount <= MIN_RANGED_WEAPONS_TO_KEEP)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ChestDeposit] Keeping ranged weapon {item.m_shared?.m_name} - only have {rangedCount}");
                    return false;
                }
                
                if (!isRanged && meleeCount <= MIN_MELEE_WEAPONS_TO_KEEP)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ChestDeposit] Keeping melee weapon {item.m_shared?.m_name} - only have {meleeCount}");
                    return false;
                }
            }
            
            // CRITICAL: Never deposit the ONLY pickaxe or axe - these are essential for gathering
            string itemPrefab = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            string itemSharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
            
            bool isPickaxe = itemPrefab.Contains("pickaxe") || itemSharedName.Contains("pickaxe");
            bool isAxe = (itemPrefab.Contains("axe") && !itemPrefab.Contains("pickaxe")) ||
                         (itemSharedName.Contains("axe") && !itemSharedName.Contains("pickaxe"));
            
            if (isPickaxe || isAxe)
            {
                // Count how many of this tool type we have
                int toolCount = CountToolsOfType(isPickaxe ? "pickaxe" : "axe");
                
                if (toolCount <= 1)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ChestDeposit] Keeping {(isPickaxe ? "pickaxe" : "axe")} {item.m_shared?.m_name} - only have {toolCount}");
                    return false;
                }
            }
            
            // Always deposit materials, trophies, misc, ammo
            if (DepositableTypes.Contains(itemType)) return true;
            
            // Default: deposit if it's a material-like item
            return true;
        }
        
        /// <summary>
        /// Counts weapons of a specific type in the companion's inventory.
        /// Checks both equipped slots and storage inventory.
        /// </summary>
        private int CountWeaponsInInventory(bool ranged)
        {
            if (_inventory == null) return 0;
            
            int count = 0;
            
            // Check equipped slots
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            if (rightHand != null && rightHand.IsWeapon() && IsRangedWeapon(rightHand) == ranged) count++;
            if (leftHand != null && leftHand.IsWeapon() && IsRangedWeapon(leftHand) == ranged) count++;
            if (rightBack != null && rightBack.IsWeapon() && IsRangedWeapon(rightBack) == ranged) count++;
            if (leftBack != null && leftBack.IsWeapon() && IsRangedWeapon(leftBack) == ranged) count++;
            
            // Check storage inventory
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item != null && item.IsWeapon() && IsRangedWeapon(item) == ranged)
                    {
                        count++;
                    }
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Counts tools of a specific type (pickaxe or axe) in the companion's inventory.
        /// Checks both equipped slots and storage inventory.
        /// </summary>
        private int CountToolsOfType(string toolType)
        {
            if (_inventory == null) return 0;
            
            int count = 0;
            string searchType = toolType.ToLowerInvariant();
            
            // Helper to check if item matches tool type
            bool IsMatchingTool(ItemDrop.ItemData item)
            {
                if (item == null) return false;
                string prefab = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                string sharedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
                
                if (searchType == "pickaxe")
                    return prefab.Contains("pickaxe") || sharedName.Contains("pickaxe");
                else if (searchType == "axe")
                    return (prefab.Contains("axe") && !prefab.Contains("pickaxe")) ||
                           (sharedName.Contains("axe") && !sharedName.Contains("pickaxe"));
                return false;
            }
            
            // Check equipped slots
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            
            if (IsMatchingTool(rightHand)) count++;
            if (IsMatchingTool(leftHand)) count++;
            if (IsMatchingTool(rightBack)) count++;
            if (IsMatchingTool(leftBack)) count++;
            
            // Check storage inventory
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                foreach (var item in storageInv.GetAllItems())
                {
                    if (IsMatchingTool(item))
                    {
                        count++;
                    }
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Checks if an item is a ranged weapon (bow, crossbow, staff).
        /// </summary>
        private bool IsRangedWeapon(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Bows) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Crossbows) return true;
            if (item.m_shared.m_attack?.m_bowDraw == true) return true;
            if (item.m_shared.m_attack?.m_requiresReload == true) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.ElementalMagic) return true;
            if (item.m_shared.m_skillType == Skills.SkillType.BloodMagic) return true;
            if (item.m_shared.m_attack?.m_attackProjectile != null) return true;
            
            return false;
        }
        
        /// <summary>
        /// Finds the best chest to deposit to - prioritizing chests with matching items.
        /// </summary>
        private Container FindBestChestForDeposit()
        {
            if (_inventory == null) return null;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return null;
            
            Container bestChest = null;
            int bestScore = -1;
            float bestDistance = float.MaxValue;
            
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                if (_visitedChests.Contains(chest)) continue;
                
                // Check if chest is accessible
                var nview = chest.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                
                // Check if chest has room
                var chestInv = chest.GetInventory();
                if (chestInv == null || chestInv.GetEmptySlots() == 0) continue;
                
                // Calculate score based on matching items
                int matchScore = 0;
                foreach (var item in storageInv.GetAllItems())
                {
                    if (item == null || !ShouldDepositItem(item)) continue;
                    
                    // Check if chest has matching items (for stacking)
                    if (ChestContainsMatchingItem(chestInv, item))
                    {
                        matchScore += 10;
                    }
                    else if (chestInv.GetEmptySlots() > 0)
                    {
                        matchScore += 1;
                    }
                }
                
                float distance = Vector3.Distance(Transform.position, chest.transform.position);
                
                // Prioritize by score, then by distance
                if (matchScore > bestScore || (matchScore == bestScore && distance < bestDistance))
                {
                    bestScore = matchScore;
                    bestDistance = distance;
                    bestChest = chest;
                }
            }
            
            return bestChest;
        }
        
        /// <summary>
        /// Checks if a chest contains an item that matches (for stacking).
        /// </summary>
        private bool ChestContainsMatchingItem(Inventory chestInv, ItemDrop.ItemData item)
        {
            if (chestInv == null || item == null) return false;
            
            string itemName = item.m_shared?.m_name ?? "";
            
            foreach (var chestItem in chestInv.GetAllItems())
            {
                if (chestItem == null) continue;
                
                string chestItemName = chestItem.m_shared?.m_name ?? "";
                
                if (itemName.Equals(chestItemName, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Check if there's room to stack
                    if (chestItem.m_stack < chestItem.m_shared.m_maxStackSize)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Deposits items from companion inventory to the target chest.
        /// </summary>
        private int DepositItemsToChest(Container chest)
        {
            if (chest == null || _inventory == null) return 0;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return 0;
            
            var chestInv = chest.GetInventory();
            if (chestInv == null) return 0;
            
            int deposited = 0;
            
            // Get a copy of items to avoid modification during iteration
            var itemsToCheck = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
            
            foreach (var item in itemsToCheck)
            {
                if (item == null) continue;
                if (!ShouldDepositItem(item)) continue;
                
                // Try to add to chest
                if (chestInv.CanAddItem(item))
                {
                    var clone = item.Clone();
                    
                    if (chestInv.AddItem(clone))
                    {
                        storageInv.RemoveItem(item);
                        deposited++;
                        
                        // Track for notification
                        string itemName = Localization.instance.Localize(item.m_shared?.m_name ?? "Item");
                        if (_depositedItemCounts.ContainsKey(itemName))
                            _depositedItemCounts[itemName] += item.m_stack;
                        else
                            _depositedItemCounts[itemName] = item.m_stack;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ChestDeposit] Deposited {item.m_stack}x {item.m_shared?.m_name}");
                    }
                }
                
                // Stop if chest is full
                if (chestInv.GetEmptySlots() == 0) break;
            }
            
            // Save inventory changes
            if (deposited > 0)
            {
                _inventory.SaveToZDO();
            }
            
            return deposited;
        }
        
        /// <summary>
        /// Deposits items from companion inventory to the best chest in the cluster.
        /// Prioritizes stacking with existing items across ALL cluster chests.
        /// </summary>
        private int DepositItemsToCluster()
        {
            if (_inventory == null || _clusterChests.Count == 0) return 0;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return 0;
            
            int totalDeposited = 0;
            
            // Get a copy of items to avoid modification during iteration
            var itemsToCheck = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
            
            foreach (var item in itemsToCheck)
            {
                if (item == null) continue;
                if (!ShouldDepositItem(item)) continue;
                
                // FIRST: Try to find a chest with matching items for stacking
                Container bestChest = FindBestChestForItem(item);
                
                if (bestChest != null)
                {
                    var chestInv = bestChest.GetInventory();
                    if (chestInv != null && chestInv.CanAddItem(item))
                    {
                        var clone = item.Clone();
                        
                        if (chestInv.AddItem(clone))
                        {
                            storageInv.RemoveItem(item);
                            totalDeposited++;
                            
                            // Track for notification
                            string itemName = Localization.instance.Localize(item.m_shared?.m_name ?? "Item");
                            if (_depositedItemCounts.ContainsKey(itemName))
                                _depositedItemCounts[itemName] += item.m_stack;
                            else
                                _depositedItemCounts[itemName] = item.m_stack;
                            
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ChestDeposit] Deposited {item.m_stack}x {item.m_shared?.m_name} to stacking chest");
                        }
                    }
                }
            }
            
            // Save inventory changes
            if (totalDeposited > 0)
            {
                _inventory.SaveToZDO();
            }
            
            return totalDeposited;
        }
        
        /// <summary>
        /// Finds the best chest in the cluster for a specific item.
        /// Uses smart station-aware logic to place items near relevant crafting stations.
        /// </summary>
        private Container FindBestChestForItem(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            
            // Use smart storage organizer for station-aware placement
            Vector3 searchCenter = _targetChest?.transform.position ?? Transform.position;
            
            var recommendation = SmartStorageOrganizer.FindBestChestForItem(
                item, 
                _clusterChests, 
                searchCenter,
                stationSearchRadius: 25f);
            
            if (recommendation != null)
            {
                if (CompanionIdleBehavior.VerboseLogging && recommendation.NearestRelevantStation != null)
                {
                    Debug.Log($"[ChestDeposit] Best chest for {item.m_shared?.m_name}: {recommendation.Reason}");
                }
                return recommendation.Chest;
            }
            
            // Fallback: find any chest with matching items or empty slots
            Container matchingChest = null;
            Container emptySlotChest = null;
            int bestMatchStack = 0;
            
            foreach (var chest in _clusterChests)
            {
                if (chest == null) continue;
                
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                // Check for matching items that can stack
                foreach (var chestItem in chestInv.GetAllItems())
                {
                    if (chestItem == null) continue;
                    
                    string chestItemName = chestItem.m_shared?.m_name ?? "";
                    string itemName = item.m_shared?.m_name ?? "";
                    
                    if (itemName.Equals(chestItemName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Found matching item - check if there's room to stack
                        int canStack = chestItem.m_shared.m_maxStackSize - chestItem.m_stack;
                        if (canStack > 0 && canStack > bestMatchStack)
                        {
                            bestMatchStack = canStack;
                            matchingChest = chest;
                        }
                    }
                }
                
                // Also track chests with empty slots as fallback
                if (emptySlotChest == null && chestInv.GetEmptySlots() > 0)
                {
                    emptySlotChest = chest;
                }
            }
            
            // Prefer matching chest for stacking, fall back to chest with empty slots
            return matchingChest ?? emptySlotChest;
        }
        
        /// <summary>
        /// Organizes items across cluster chests using smart station-aware logic.
        /// Moves items to be near the crafting stations where they'll be used:
        /// - Ores near smelters
        /// - Metal bars near forges
        /// - Food near cooking stations
        /// - Wood near kilns and fireplaces
        /// - Etc.
        /// </summary>
        private int OrganizeClusterChests()
        {
            if (_clusterChests.Count < 2) return 0;
            
            // Use the new smart storage organizer
            Vector3 clusterCenter = _targetChest?.transform.position ?? Transform.position;
            
            var result = SmartStorageOrganizer.OrganizeChestCluster(
                _clusterChests, 
                clusterCenter,
                stationSearchRadius: 25f);
            
            // Log the organization actions
            if (CompanionIdleBehavior.VerboseLogging && result.Actions.Count > 0)
            {
                Debug.Log($"[ChestDeposit] Smart organization completed:");
                foreach (var action in result.Actions.Take(5)) // Limit log spam
                {
                    Debug.Log($"  - {action}");
                }
                if (result.Actions.Count > 5)
                {
                    Debug.Log($"  - ... and {result.Actions.Count - 5} more actions");
                }
            }
            
            return result.ItemsMoved + result.StacksConsolidated;
        }
        
        #endregion
        
        #region Helpers
        
        private void NotifyOwnerAndComplete()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            NotifyOwnerViaChat();
            Complete();
        }
        
        /// <summary>
        /// Notifies the owner via in-game chat about completed deposits.
        /// </summary>
        private void NotifyOwnerViaChat()
        {
            if (_totalItemsDeposited == 0) return;
            
            // Build task description
            string taskDescription;
            if (_depositedItemCounts.Count <= 3)
            {
                // List specific items if 3 or fewer types
                var itemList = new List<string>();
                foreach (var kvp in _depositedItemCounts)
                {
                    itemList.Add($"{kvp.Value}x {kvp.Key}");
                }
                taskDescription = $"stored {string.Join(", ", itemList)} in chests";
            }
            else
            {
                // General message for many items
                taskDescription = $"stored {_totalItemsDeposited} items in {_chestsVisited} chest(s)";
            }
            
            // Use the centralized chat helper
            CompanionChatHelper.NotifyTaskComplete(Companion, taskDescription);
        }
        
        private void SetPhase(DepositPhase phase)
        {
            if (phase == _currentPhase) return;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ChestDeposit] {Companion?.companionName} phase: {_currentPhase} -> {phase}");
            
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }
        
        private void MoveToPosition(Vector3 position)
        {
            // WALKING for idle behaviors - no need to sprint
            // CRITICAL: skipCommandOverride=true to avoid cancelling the parent SubBehavior command
            if (_combatMovement != null)
            {
                _combatMovement.SetMoveDestination(position, useWalk: true, skipCommandOverride: true);
            }
            else if (_character != null)
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
        
        private new void StopMovement()
        {
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
        /// Plays the interact animation for chest interaction.
        /// </summary>
        private void PlayInteractAnimation()
        {
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        #endregion
    }
}
