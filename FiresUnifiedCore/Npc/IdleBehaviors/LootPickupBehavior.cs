// TODO: REMOVE AFTER TESTING V2 - This file is no longer registered in CompanionIdleBehavior.SubBehaviors.cs
// LootPickupBehaviorV2 is now used instead. Remove this file once V2 is confirmed working.
// See: CompanionIdleBehavior.SubBehaviors.cs InitializeSubBehaviors()

using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Picks up enemy drops and treasure after a fight or while wandering, by chance and by value: trophies always,
    /// crafting materials usually, consumables sometimes, anything else rarely. Items go to the companion's storage.
    /// </summary>
    public class LootPickupBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "LootPickup";
        
        /// <summary>
        /// Loot pickup is always available for idle rotation.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        #region Settings
        
        private const float LootDetectionRange = 8f;
        private const float StayModeLootRange = 25f;  // Larger range when staying
        private const float PickupRange = 1.5f;
        private const float MaxLootTime = 30f;
        
        // Pickup chances by item type
        private const float TrophyPickupChance = 1.0f;        // Always pick up trophies
        private const float MaterialPickupChance = 0.7f;       // 70% for materials
        private const float ConsumablePickupChance = 0.5f;     // 50% for food/potions
        private const float EquipmentPickupChance = 0.3f;      // 30% for weapons/armor
        private const float OtherPickupChance = 0.2f;          // 20% for misc
        
        // Maximum items to pick up per session
        private const int MaxItemsPerSession = 10;
        
        #endregion
        
        #region State
        
        private enum LootPhase
        {
            Scanning,
            MovingToItem,
            PickingUp,
            DepositingToChests,
            Complete
        }
        
        private LootPhase _currentPhase = LootPhase.Scanning;
        private ItemDrop _targetItem;
        private float _phaseStartTime;
        private int _itemsPickedUp;
        private int _itemsDeposited;
        private List<ItemDrop> _itemsToLoot = new List<ItemDrop>();
        
        // Nearby chests for depositing
        private List<Container> _nearbyChests = new List<Container>();
        
        // Components
        private Character _character;
        private CompanionInventory _inventory;
        private CompanionCombatMovement _combatMovement;
        private Rigidbody _rigidbody;
        
        // Track recently looted positions to avoid returning to same spot
        private List<Vector3> _lootedPositions = new List<Vector3>();
        
        #endregion
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            
            MaxDuration = MaxLootTime + 10f;
        }
        
        public override bool CanStart()
        {
            if (Companion == null || _inventory == null) return false;
            
            // Use larger range when in Stay mode
            bool isStaying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            float searchRange = isStaying ? StayModeLootRange : LootDetectionRange;
            
            // Check if there are items nearby to loot
            var items = ScanForLoot(searchRange);
            return items.Count > 0;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = LootPhase.Scanning;
            _phaseStartTime = Time.time;
            _itemsPickedUp = 0;
            _itemsDeposited = 0;
            _itemsToLoot.Clear();
            _lootedPositions.Clear();
            _nearbyChests.Clear();
            
            // Find nearby chests for depositing later
            _nearbyChests = ChestHelper.FindNearbyChests(Transform.position, 10f);
            
            // Chat feedback only for commanded loot (not automatic idle pickup)
            if (IsCommandInitiated)
            {
                CompanionChatHelper.QuickMessages.PickingUpLoot(Companion);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[LootPickup] {Companion.companionName} starting loot pickup, found {_nearbyChests.Count} nearby chests");
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut() || _itemsPickedUp >= MaxItemsPerSession)
            {
                Complete();
                return true;
            }
            
            // Show current status above companion's head
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            switch (_currentPhase)
            {
                case LootPhase.Scanning:
                    return UpdateScanning();
                    
                case LootPhase.MovingToItem:
                    return UpdateMovingToItem();
                    
                case LootPhase.PickingUp:
                    return UpdatePickingUp();
                    
                case LootPhase.DepositingToChests:
                    return UpdateDepositingToChests();
                    
                case LootPhase.Complete:
                    NotifyOwner();
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            NotifyOwner();
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            return _currentPhase switch
            {
                LootPhase.Scanning => "Looking for loot",
                LootPhase.MovingToItem => "Walking to loot",
                LootPhase.PickingUp => "Picking up items",
                _ => _itemsPickedUp > 0 ? $"Looted {_itemsPickedUp} items" : "Looting"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateScanning()
        {
            // Use larger range when in Stay mode
            bool isStaying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            float searchRange = isStaying ? StayModeLootRange : LootDetectionRange;
            
            _itemsToLoot = ScanForLoot(searchRange);
            
            if (_itemsToLoot.Count == 0)
            {
                SetPhase(LootPhase.Complete);
                return true;
            }
            
            // Get next item to loot
            _targetItem = GetNextItemToLoot();
            
            if (_targetItem == null)
            {
                SetPhase(LootPhase.Complete);
                return true;
            }
            
            SetPhase(LootPhase.MovingToItem);
            MoveToPosition(_targetItem.transform.position);
            
            return false;
        }
        
        private bool UpdateMovingToItem()
        {
            // Check if item still exists
            if (_targetItem == null)
            {
                SetPhase(LootPhase.Scanning);
                return false;
            }
            
            float dist = Vector3.Distance(Transform.position, _targetItem.transform.position);
            
            if (dist < PickupRange)
            {
                StopMovement();
                SetPhase(LootPhase.PickingUp);
                return false;
            }
            
            // Keep moving
            MoveToPosition(_targetItem.transform.position);
            
            // Timeout
            if (Time.time - _phaseStartTime > 10f)
            {
                // Skip this item
                _lootedPositions.Add(_targetItem.transform.position);
                SetPhase(LootPhase.Scanning);
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
            
            // Try to pick up
            if (TryPickupItem(_targetItem))
            {
                _itemsPickedUp++;
                _lootedPositions.Add(_targetItem.transform.position);
            }
            
            _targetItem = null;
            
            // Check if inventory is getting full - deposit to chests if we have any
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv != null && _nearbyChests.Count > 0)
            {
                // If we've picked up enough items or inventory is more than half full, deposit
                int itemCount = storageInv.GetAllItems().Count;
                int maxSlots = storageInv.GetWidth() * storageInv.GetHeight();
                
                if (_itemsPickedUp >= 5 || itemCount > maxSlots / 2)
                {
                    SetPhase(LootPhase.DepositingToChests);
                    return false;
                }
            }
            
            // Continue scanning for more
            SetPhase(LootPhase.Scanning);
            
            return false;
        }
        
        private bool UpdateDepositingToChests()
        {
            if (_nearbyChests.Count == 0 || _inventory == null)
            {
                SetPhase(LootPhase.Scanning);
                return false;
            }
            
            StopMovement();
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv != null)
            {
                // Deposit materials to nearby chests (prioritizing chests that already have the items)
                int deposited = ChestHelper.DepositToChests(_nearbyChests, storageInv, null);
                _itemsDeposited += deposited;
                
                if (CompanionIdleBehavior.VerboseLogging && deposited > 0)
                    Debug.Log($"[LootPickup] {Companion.companionName} deposited {deposited} items to chests");
            }
            
            // Continue scanning for more loot
            SetPhase(LootPhase.Scanning);
            
            return false;
        }
        
        #endregion
        
        #region Loot Logic
        
        private List<ItemDrop> ScanForLoot(float range = LootDetectionRange)
        {
            var result = new List<ItemDrop>();
            
            // Use SearchCenter for staying companions (searches around home position)
            Vector3 center = SearchCenter;
            float searchRadius = GetEffectiveSearchRadius(range);
            var colliders = Physics.OverlapSphere(center, searchRadius);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var itemDrop = collider.GetComponent<ItemDrop>();
                if (itemDrop == null || !itemDrop.CanPickup()) continue;
                
                var itemData = itemDrop.m_itemData;
                if (itemData == null) continue;
                
                // Skip already looted positions
                if (IsPositionLooted(itemDrop.transform.position)) continue;
                
                // Check pickup chance based on item type
                float pickupChance = GetPickupChance(itemData);
                if (Random.value > pickupChance) continue;
                
                result.Add(itemDrop);
            }
            
            return result;
        }
        
        private ItemDrop GetNextItemToLoot()
        {
            if (_itemsToLoot.Count == 0) return null;
            
            // Prioritize by value/usefulness
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
            
            // Trophies - always pick up
            if (itemType == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy"))
            {
                return TrophyPickupChance;
            }
            
            // Materials - high priority
            if (itemType == ItemDrop.ItemData.ItemType.Material)
            {
                return MaterialPickupChance;
            }
            
            // Consumables
            if (itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                return ConsumablePickupChance;
            }
            
            // Equipment
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Helmet ||
                itemType == ItemDrop.ItemData.ItemType.Chest ||
                itemType == ItemDrop.ItemData.ItemType.Legs ||
                itemType == ItemDrop.ItemData.ItemType.Shoulder)
            {
                return EquipmentPickupChance;
            }
            
            return OtherPickupChance;
        }
        
        private float GetItemPriority(ItemDrop.ItemData item)
        {
            if (item == null) return 0f;
            
            float priority = 0f;
            
            var itemType = item.m_shared.m_itemType;
            string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            // Trophies have highest priority
            if (itemType == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy"))
            {
                priority = 100f;
            }
            // Valuable materials
            else if (name.Contains("black") || name.Contains("silver") || name.Contains("gold") ||
                     name.Contains("flametal") || name.Contains("iron") || name.Contains("copper"))
            {
                priority = 80f;
            }
            // Regular materials
            else if (itemType == ItemDrop.ItemData.ItemType.Material)
            {
                priority = 50f;
            }
            // Equipment
            else if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                     itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon)
            {
                priority = 40f;
            }
            // Consumables
            else if (itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                priority = 30f;
            }
            else
            {
                priority = 10f;
            }
            
            // Closer items get slight priority bonus
            if (item.m_dropPrefab != null)
            {
                // Use world position of the ItemDrop object instead of grid position
                // m_gridPos is a Vector2i for inventory slots, not world position
            }
            
            return priority;
        }
        
        private bool TryPickupItem(ItemDrop itemDrop)
        {
            if (itemDrop == null || _inventory == null) return false;
            
            try
            {
                // CRITICAL: Re-check CanPickup - the item may have been picked up by another entity
                if (!itemDrop.CanPickup()) return false;
                
                var itemData = itemDrop.m_itemData;
                if (itemData == null) return false;
                
                var storageInv = _inventory.GetStorageInventory();
                if (storageInv == null) return false;
                
                if (storageInv.CanAddItem(itemData))
                {
                    // Clone the item data before removing from world
                    var clonedItem = itemData.Clone();
                    
                    // Use Valheim's proper pickup method to handle networking correctly
                    var nview = itemDrop.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid())
                    {
                        // Request ownership if we don't have it
                        if (!nview.IsOwner())
                        {
                            nview.ClaimOwnership();
                        }
                        
                        // Add to our inventory and remove the world drop.
                        // ItemDrop carries a ZNetView, so we MUST route the
                        // destroy through ZNetScene (via the helper). Raw
                        // Object.Destroy on a ZNetView'd object leaves a stale
                        // ZNetScene.m_instances entry that NREs in
                        // RemoveObjects every tick afterwards.
                        storageInv.AddItem(clonedItem);
                        CompanionNetworkHelper.Destroy(itemDrop.gameObject, disableFirst: false);
                    }

                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[LootPickup] {Companion.companionName} picked up {itemData.m_shared.m_name}");
                    
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[LootPickup] Failed to pickup item: {ex.Message}");
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
        
        #region Helpers
        
        private void NotifyOwner()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            // Do a final deposit before finishing
            if (_nearbyChests.Count > 0 && _inventory != null)
            {
                var storageInv = _inventory.GetStorageInventory();
                if (storageInv != null)
                {
                    int deposited = ChestHelper.DepositToChests(_nearbyChests, storageInv, null);
                    _itemsDeposited += deposited;
                }
            }
            
            if (_itemsPickedUp == 0) return;
            
            // Use CompanionChatHelper for consistent notifications
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
        
        private void SetPhase(LootPhase phase)
        {
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }
        
        private void MoveToPosition(Vector3 position)
        {
            // CRITICAL: Use CompanionCombatMovement.SetMoveDestination() for proper AI pathfinding
            // This integrates with CompanionAI's pathfinding loop which handles continuous movement
            // and obstacle avoidance. Direct SetMoveDir() only works for single frames.
            
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
        
        #endregion
    }
}
