using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Loot pickup behavior using the new WorkBehaviorBase infrastructure.
    /// Handles companion looting of enemy drops and treasure.
    /// 
    /// MIGRATED FROM: LootPickupBehavior.cs
    /// USES: WorkBehaviorBase<TPhase>, ResourceAccessService, BehaviorPhaseManager
    /// </summary>
    public class LootPickupBehaviorV2 : WorkBehaviorBase<LootPickupBehaviorV2.LootPhase>
    {
        public override string BehaviorName => "LootPickup";
        public override bool AvailableForIdleRotation => true;
        
        /// <summary>
        /// Loot pickup has slightly elevated priority (5) to collect dropped items
        /// before they despawn.
        /// </summary>
        public override int InventoryPriority 
        {
            get
            {
                // If inventory is full, lower priority
                if (IsInventoryCompletelyFull()) return -50;
                return 5; // Slightly elevated to collect loot before despawn
            }
        }
        
        #region Phase Enum
        
        public enum LootPhase
        {
            Scanning,
            MovingToItem,
            PickingUp,
            DepositingToChests,
            Complete
        }
        
        #endregion
        
        #region Settings
        
        private const float LOOT_DETECTION_RANGE = 8f;
        private const float STAY_MODE_LOOT_RANGE = 25f;
        private const float PICKUP_RANGE = 1.5f;
        private const float MAX_LOOT_TIME = 30f;
        
        private const float TROPHY_PICKUP_CHANCE = 1.0f;
        private const float MATERIAL_PICKUP_CHANCE = 0.7f;
        private const float CONSUMABLE_PICKUP_CHANCE = 0.5f;
        private const float EQUIPMENT_PICKUP_CHANCE = 0.3f;
        private const float OTHER_PICKUP_CHANCE = 0.2f;
        
        private const int MAX_ITEMS_PER_SESSION = 10;
        
        #endregion
        
        #region State
        
        private ItemDrop _targetItem;
        private int _itemsPickedUp;
        private int _itemsDeposited;
        private List<ItemDrop> _itemsToLoot = new List<ItemDrop>();
        private List<Vector3> _lootedPositions = new List<Vector3>();
        
        #endregion
        
        #region WorkBehaviorBase Implementation
        
        protected override LootPhase InitialPhase => LootPhase.Scanning;
        
        protected override float GetPhaseTimeout(LootPhase phase)
        {
            return phase switch
            {
                LootPhase.Scanning => 5f,
                LootPhase.MovingToItem => 10f,
                LootPhase.PickingUp => 5f,
                LootPhase.DepositingToChests => 10f,
                _ => 5f
            };
        }
        
        protected override string GetPhaseDescription(LootPhase phase)
        {
            return phase switch
            {
                LootPhase.Scanning => "Looking for loot",
                LootPhase.MovingToItem => "Walking to loot",
                LootPhase.PickingUp => "Picking up items",
                LootPhase.DepositingToChests => "Storing items",
                _ => _itemsPickedUp > 0 ? $"Looted {_itemsPickedUp} items" : "Looting"
            };
        }
        
        protected override LootPhase OnPhaseTimeout(LootPhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");
            
            return timedOutPhase switch
            {
                LootPhase.MovingToItem => LootPhase.Scanning,
                LootPhase.PickingUp => LootPhase.Scanning,
                LootPhase.DepositingToChests => LootPhase.Scanning,
                _ => LootPhase.Complete
            };
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MAX_LOOT_TIME + 10f;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || Inventory == null) return false;
            if (!CompanionBehaviorToggles.IsLootEnabled(Companion)) return false;

            float searchRange = GetSearchRange();
            var items = ScanForLoot(searchRange);
            return items.Count > 0;
        }
        
        public override void Start()
        {
            base.Start();
            
            _itemsPickedUp = 0;
            _itemsDeposited = 0;
            _itemsToLoot.Clear();
            _lootedPositions.Clear();
            
            if (IsCommandInitiated)
            {
                CompanionChatHelper.QuickMessages.PickingUpLoot(Companion);
            }
            
            LogVerbose($"Starting loot pickup, found {Resources.ChestCount} nearby chests");
        }
        
        protected override bool UpdatePhase(LootPhase phase)
        {
            if (_itemsPickedUp >= MAX_ITEMS_PER_SESSION)
            {
                SetPhase(LootPhase.Complete);
            }
            
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            return phase switch
            {
                LootPhase.Scanning => UpdateScanning(),
                LootPhase.MovingToItem => UpdateMovingToItem(),
                LootPhase.PickingUp => UpdatePickingUp(),
                LootPhase.DepositingToChests => UpdateDepositingToChests(),
                LootPhase.Complete => CompleteAndNotify(),
                _ => true
            };
        }
        
        public override void Cancel()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            NotifyOwner();
            base.Cancel();
        }
        
        #endregion
        
        #region Phase Updates
        
        private bool UpdateScanning()
        {
            float searchRange = GetSearchRange();
            _itemsToLoot = ScanForLoot(searchRange);
            
            if (_itemsToLoot.Count == 0)
            {
                SetPhase(LootPhase.Complete);
                return false;
            }
            
            _targetItem = GetNextItemToLoot();
            
            if (_targetItem == null)
            {
                SetPhase(LootPhase.Complete);
                return false;
            }
            
            SetPhase(LootPhase.MovingToItem);
            MoveToPosition(_targetItem.transform.position);
            return false;
        }
        
        private bool UpdateMovingToItem()
        {
            if (_targetItem == null)
            {
                SetPhase(LootPhase.Scanning);
                return false;
            }
            
            // CRITICAL: Call ContinueMovement() every frame for vanilla pathfinding to work!
            if (ContinueMovement())
            {
                // Arrived at destination
                StopMovement();
                SetPhase(LootPhase.PickingUp);
            }
            
            return false;
        }
        
        private bool UpdatePickingUp()
        {
            if (_targetItem == null)
            {
                SetPhase(LootPhase.Scanning);
                return false;
            }
            
            StopMovement();
            
            if (TryPickupItem(_targetItem))
            {
                _itemsPickedUp++;
                _lootedPositions.Add(_targetItem.transform.position);
                
                // Fire event
                string itemName = _targetItem.m_itemData?.m_dropPrefab?.name ?? "unknown";
                CompanionEvents.FireResourceGathered(Companion, itemName, 1);
            }
            
            _targetItem = null;
            
            // Check if we should deposit
            if (ShouldDeposit())
            {
                SetPhase(LootPhase.DepositingToChests);
            }
            else
            {
                SetPhase(LootPhase.Scanning);
            }
            
            return false;
        }
        
        private bool UpdateDepositingToChests()
        {
            if (!Resources.HasNearbyChests)
            {
                SetPhase(LootPhase.Scanning);
                return false;
            }
            
            StopMovement();
            
            int deposited = Resources.SmartDepositAll();
            _itemsDeposited += deposited;
            
            if (deposited > 0)
            {
                LogVerbose($"Deposited {deposited} items to chests");
                CompanionEvents.FireItemDeposited(Companion, "mixed", deposited);
            }
            
            SetPhase(LootPhase.Scanning);
            return false;
        }
        
        private bool CompleteAndNotify()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            NotifyOwner();
            Complete();
            return true;
        }
        
        #endregion
        
        #region Loot Logic
        
        private float GetSearchRange()
        {
            bool isStaying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            return isStaying ? STAY_MODE_LOOT_RANGE : LOOT_DETECTION_RANGE;
        }
        
        private bool ShouldDeposit()
        {
            if (!Resources.HasNearbyChests) return false;
            
            var storage = GetStorageInventory();
            if (storage == null) return false;
            
            int itemCount = storage.GetAllItems().Count;
            int maxSlots = storage.GetWidth() * storage.GetHeight();
            
            return _itemsPickedUp >= 5 || itemCount > maxSlots / 2;
        }
        
        private List<ItemDrop> ScanForLoot(float range)
        {
            var result = new List<ItemDrop>();
            
            Vector3 center = SearchCenter;
            float searchRadius = GetEffectiveSearchRadius(range);
            var colliders = Physics.OverlapSphere(center, searchRadius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var itemDrop = col.GetComponent<ItemDrop>();
                if (itemDrop == null || !itemDrop.CanPickup()) continue;
                
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                if (IsPositionLooted(itemDrop.transform.position)) continue;
                
                float pickupChance = GetPickupChance(itemData);
                if (Random.value > pickupChance) continue;
                
                result.Add(itemDrop);
            }
            
            return result;
        }
        
        private ItemDrop GetNextItemToLoot()
        {
            if (_itemsToLoot.Count == 0) return null;
            
            _itemsToLoot.Sort((a, b) =>
            {
                float scoreA = GetItemPriority(a.m_itemData);
                float scoreB = GetItemPriority(b.m_itemData);
                return scoreB.CompareTo(scoreA);
            });
            
            return _itemsToLoot[0];
        }
        
        private float GetPickupChance(ItemDrop.ItemData item)
        {
            if (item == null) return 0f;
            
            var itemType = item.m_shared.m_itemType;
            string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            if (itemType == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy"))
                return TROPHY_PICKUP_CHANCE;
            
            if (itemType == ItemDrop.ItemData.ItemType.Material)
                return MATERIAL_PICKUP_CHANCE;
            
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
                return CONSUMABLE_PICKUP_CHANCE;
            
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Helmet ||
                itemType == ItemDrop.ItemData.ItemType.Chest ||
                itemType == ItemDrop.ItemData.ItemType.Legs ||
                itemType == ItemDrop.ItemData.ItemType.Shoulder)
            {
                return EQUIPMENT_PICKUP_CHANCE;
            }
            
            return OTHER_PICKUP_CHANCE;
        }
        
        private float GetItemPriority(ItemDrop.ItemData item)
        {
            if (item == null) return 0f;
            
            var itemType = item.m_shared.m_itemType;
            string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            if (itemType == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy"))
                return 100f;
            
            if (name.Contains("black") || name.Contains("silver") || name.Contains("gold") ||
                name.Contains("flametal") || name.Contains("iron") || name.Contains("copper"))
                return 80f;
            
            if (itemType == ItemDrop.ItemData.ItemType.Material)
                return 50f;
            
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon)
                return 40f;
            
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
                return 30f;
            
            return 10f;
        }
        
        private bool TryPickupItem(ItemDrop itemDrop)
        {
            if (itemDrop == null || Inventory == null) return false;
            
            try
            {
                if (!itemDrop.CanPickup()) return false;
                
                var itemData = itemDrop.m_itemData;
                if (itemData == null) return false;
                
                var storage = GetStorageInventory();
                if (storage == null) return false;
                
                if (storage.CanAddItem(itemData))
                {
                    var clonedItem = itemData.Clone();
                    
                    var nview = itemDrop.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid() && !nview.IsOwner())
                    {
                        nview.ClaimOwnership();
                    }

                    // Add to inventory and remove the world drop. ItemDrop
                    // carries a ZNetView, so we MUST route the destroy through
                    // ZNetScene (via the helper). Raw Object.Destroy on a
                    // ZNetView'd object leaves a stale ZNetScene.m_instances
                    // entry that NREs in RemoveObjects every tick afterwards.
                    storage.AddItem(clonedItem);
                    CompanionNetworkHelper.Destroy(itemDrop.gameObject, disableFirst: false);

                    LogVerbose($"Picked up {itemData.m_shared.m_name}");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                LogWarning($"Failed to pickup item: {ex.Message}");
            }
            
            return false;
        }
        
        private bool IsPositionLooted(Vector3 pos)
        {
            foreach (var lootedPos in _lootedPositions)
            {
                if (Vector3.Distance(pos, lootedPos) < 1f)
                    return true;
            }
            return false;
        }
        
        #endregion
        
        #region Notifications
        
        private void NotifyOwner()
        {
            // Final deposit
            if (Resources.HasNearbyChests)
            {
                int deposited = Resources.SmartDepositAll();
                _itemsDeposited += deposited;
            }
            
            if (_itemsPickedUp == 0) return;
            
            string taskDescription;
            if (_itemsDeposited > 0)
            {
                taskDescription = $"collected {_itemsPickedUp} items and stored {_itemsDeposited} in chests";
            }
            else
            {
                taskDescription = $"collected {_itemsPickedUp} items";
            }
            
            CompanionChatHelper.NotifyTaskComplete(Companion, taskDescription);
        }
        
        #endregion
    }
}
