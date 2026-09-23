using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    public partial class FarmingBehavior
    {
        private const float PhaseEntryWindow = 0.1f;
        private const float InteractPauseDuration = 0.5f;
        private const float CraftingPauseDuration = 1.5f;
        private const float BeehiveResampleInterval = 5f;
        private const float DropCollectionRadius = 5f;
        private const float PickupDistance = 1.5f;
        private const float DropCollectionTimeout = 10f;

        // -
        // Cultivator acquisition phases
        // -

        #region Cultivator Acquisition

        private bool UpdateGettingCultivator()
        {
            // AutoPickup may have collected one since we last checked.
            if (HasCultivator())
            {
                _cultivatorReadyThisSession = true;
                SetPhase(FarmPhase.Idle);
                return false;
            }

            // Refresh chest list then look for one.
            RefreshNearbyChests();
            _cultivatorChest = FindChestWith(IsCultivator);
            if (_cultivatorChest != null && IsReachable(_cultivatorChest.transform.position))
            {
                _targetPosition = InteractionPointHelper.GetContainerInteractionPoint(
                    _cultivatorChest, Transform.position, InteractionDistance);
                SetPhase(FarmPhase.MovingToCultivatorChest);
                TryMoveToPosition(_targetPosition);
                return false;
            }

            // No cultivator in chests — try crafting at a Forge.
            if (CanCraftCultivator())
            {
                _targetForge = FindNearestForge();
                if (_targetForge != null && IsReachable(_targetForge.transform.position))
                {
                    _targetPosition = InteractionPointHelper.GetInteractionPoint(
                        _targetForge.gameObject, Transform.position, InteractionDistance);
                    SetPhase(FarmPhase.MovingToForge);
                    TryMoveToPosition(_targetPosition);
                    return false;
                }
            }

            // Cannot acquire a cultivator this session — skip planting entirely.
            Debug.Log($"[Farming] {Companion?.companionName} cannot acquire cultivator — planting skipped");
            return true; // signal Complete
        }

        private bool UpdateMovingToCultivatorChest()
        {
            if (_cultivatorChest == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            float dist = Vector3.Distance(Transform.position, _targetPosition);
            if (dist <= ArrivalDistance)
            {
                StopMovement();
                if (TakeOneFromChest(_cultivatorChest, IsCultivator))
                {
                    _cultivatorReadyThisSession = true;
                    Debug.Log($"[Farming] {Companion?.companionName} retrieved Cultivator from chest");
                    PlayInteractAnimation();
                }
                _cultivatorChest = null;
                SetPhase(FarmPhase.Idle);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to cultivator chest");
                _cultivatorChest = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private bool UpdateMovingToForge()
        {
            if (_targetForge == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            float dist = Vector3.Distance(Transform.position, _targetPosition);
            if (dist <= ArrivalDistance)
            {
                StopMovement();
                SetPhase(FarmPhase.CraftingCultivator);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to forge");
                _targetForge = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private bool UpdateCraftingCultivator()
        {
            StopMovement();

            // Brief pause so the companion looks busy.
            if (Time.time - _phaseStartTime < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (Time.time - _phaseStartTime < CraftingPauseDuration) return false;

            var storage = _inventory?.GetStorageInventory();
            if (storage != null && CanCraftCultivator())
            {
                // Created from its prefab so m_dropPrefab is set and the tool survives save/load (Inventory.cs:88-96).
                var cultivator = ObjectDB.instance?.GetItemPrefab(CultivatorPrefab);
                if (cultivator != null && storage.CanAddItem(cultivator, 1))
                {
                    RemoveItems(storage, RecipeCorewood, RecipeCorewoodCount);
                    RemoveItems(storage, RecipeBronze,   RecipeBronzeCount);
                    storage.AddItem(cultivator, 1);
                    _cultivatorReadyThisSession = true;
                    _inventory?.SaveToZDO();
                    Debug.Log($"[Farming] {Companion?.companionName} crafted a Cultivator");
                }
                else
                {
                    Debug.LogWarning($"[Farming] {Companion?.companionName} crafting failed - prefab '{CultivatorPrefab}' not found or no inventory space");
                }
            }

            _targetForge = null;
            SetPhase(FarmPhase.Idle);
            return false;
        }

        // Transfer one of the first matching items from a container into the companion's storage inventory.
        private bool TakeOneFromChest(Container chest, System.Predicate<ItemDrop.ItemData> match)
        {
            var chestInv = chest != null && ChestHelper.TryClaimForWrite(chest, Companion) ? chest.GetInventory() : null;
            var storage  = _inventory?.GetStorageInventory();
            if (chestInv == null || storage == null) return false;

            foreach (var item in new List<ItemDrop.ItemData>(chestInv.GetAllItems()))
            {
                if (!match(item)) continue;

                bool took = ChestHelper.MoveItem(chestInv, storage, item, 1) > 0;
                if (took) _inventory?.SaveToZDO();
                return took;
            }
            return false;
        }

        #endregion

        // -
        // Seed retrieval phases
        // -

        #region Seed Retrieval

        private bool UpdateGettingSeeds()
        {
            // Already picked up seeds since we entered this phase.
            if (StorageHas(IsSeedForPlantSpots)) { SetPhase(FarmPhase.Idle); return false; }

            RefreshNearbyChests();
            _seedChest = FindChestWith(IsSeedForPlantSpots);
            if (_seedChest != null && IsReachable(_seedChest.transform.position))
            {
                _targetPosition = InteractionPointHelper.GetContainerInteractionPoint(
                    _seedChest, Transform.position, InteractionDistance);
                SetPhase(FarmPhase.MovingToSeedChest);
                TryMoveToPosition(_targetPosition);
                return false;
            }

            // No reachable chest with seeds — nothing to plant this session.
            Debug.Log($"[Farming] {Companion?.companionName} cannot find seeds in any chest — planting skipped");
            return true; // signal Complete
        }

        private bool UpdateMovingToSeedChest()
        {
            if (_seedChest == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            float dist = Vector3.Distance(Transform.position, _targetPosition);
            if (dist <= ArrivalDistance)
            {
                StopMovement();
                TakeAllSeedsFromChest(_seedChest);
                _seedChest = null;
                SetPhase(FarmPhase.Idle);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to seed chest");
                _seedChest = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        // Pull every seed that grows on the current plant spots from a chest into the companion's storage.
        private void TakeAllSeedsFromChest(Container chest)
        {
            var chestInv = chest != null && ChestHelper.TryClaimForWrite(chest, Companion) ? chest.GetInventory() : null;
            var storage  = _inventory?.GetStorageInventory();
            if (chestInv == null || storage == null) return;

            int taken = 0;
            foreach (var item in new List<ItemDrop.ItemData>(chestInv.GetAllItems()))
            {
                if (!IsSeedForPlantSpots(item)) continue;
                if (ChestHelper.MoveItem(chestInv, storage, item, item.m_stack) > 0) taken++;
            }

            if (taken > 0)
            {
                _inventory?.SaveToZDO();
                PlayInteractAnimation();
                Debug.Log($"[Farming] {Companion?.companionName} retrieved {taken} seed stacks from chest");
            }
        }

        #endregion

        // -
        // Beehive phases
        // -

        #region Beehive

        private bool UpdateMovingToBeehive()
        {
            if (_targetBeehive == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            // Periodically re-sample the interaction point in case the initial
            // sample landed inside geometry (e.g. beehive on a wall).
            float age = Time.time - _phaseStartTime;
            if (age > BeehiveResampleInterval && age % BeehiveResampleInterval < 0.1f)
                _targetPosition = InteractionPointHelper.GetInteractionPoint(
                    _targetBeehive.gameObject, Transform.position, InteractionDistance);

            if (Vector3.Distance(Transform.position, _targetPosition) <= ArrivalDistance)
            {
                StopMovement();
                SetPhase(FarmPhase.HarvestingBeehive);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to beehive");
                _targetBeehive = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private bool UpdateHarvestingBeehive()
        {
            if (_targetBeehive == null) { SetPhase(FarmPhase.Idle); return false; }

            StopMovement();
            FaceTarget(_targetBeehive.transform.position);

            float age = Time.time - _phaseStartTime;
            if (age < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (age < InteractPauseDuration) return false;

            if (FarmingDataHelper.HasProduceReady(_targetBeehive) && _humanoid != null)
            {
                int stored = FarmingDataHelper.GetStoredProduce(_targetBeehive);
                // RPC_Extract spawns the produce on the hive's owner (Beehive.cs:104-116); own it so the drops are ours to collect.
                _targetBeehive.GetComponent<ZNetView>().ClaimOwnership();
                _targetBeehive.Interact(_humanoid, false, false);
                _hiveProduceHarvested += stored;
                Debug.Log($"[Farming] {Companion?.companionName} collected {stored} {FarmingDataHelper.GetProduceName(_targetBeehive)}");
            }

            _targetBeehive = null;
            SetPhase(FarmPhase.CollectingDrops);
            return false;
        }

        #endregion

        // -
        // Crop phases
        // -

        #region Crops

        private bool UpdateMovingToCrop()
        {
            if (_targetCrop == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            if (Vector3.Distance(Transform.position, _targetPosition) <= ArrivalDistance)
            {
                StopMovement();
                SetPhase(FarmPhase.HarvestingCrop);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to crop");
                _targetCrop = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private bool UpdateHarvestingCrop()
        {
            if (_targetCrop == null) { SetPhase(FarmPhase.Idle); return false; }

            StopMovement();
            FaceTarget(_targetCrop.transform.position);

            float age = Time.time - _phaseStartTime;
            if (age < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (age < InteractPauseDuration) return false;

            if (FarmingDataHelper.IsPickableReady(_targetCrop) && _humanoid != null && Player.m_localPlayer != null)
            {
                // RPC_Pick runs on the pickable's owner and reads Player.m_localPlayer (Pickable.cs:171): own it so it runs here.
                _targetCrop.GetComponent<ZNetView>().ClaimOwnership();
                _targetCrop.Interact(_humanoid, false, false);
                if (_targetCrop.GetPicked())
                {
                    _cropsHarvested++;
                    Debug.Log($"[Farming] {Companion?.companionName} harvested {_targetCrop.name.Replace("(Clone)", "").Trim()}");
                }
            }

            _targetCrop = null;
            SetPhase(FarmPhase.CollectingDrops);
            return false;
        }

        #endregion

        // -
        // Planting phases
        // -

        #region Planting

        private bool UpdateMovingToPlantSpot()
        {
            if (_cropToPlant == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            if (Vector3.Distance(Transform.position, _plantPosition) <= ArrivalDistance)
            {
                StopMovement();
                SetPhase(FarmPhase.Planting);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to plant spot");
                _cropToPlant = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private bool UpdatePlanting()
        {
            if (_cropToPlant == null) { SetPhase(FarmPhase.Idle); return false; }

            StopMovement();
            FaceTarget(_plantPosition);

            float age = Time.time - _phaseStartTime;
            if (age < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (age < InteractPauseDuration) return false;

            // Position may no longer be valid if something else planted there.
            if (!FarmingDataHelper.IsValidPlantingPosition(_plantPosition, _cropToPlant, PlantSpacing))
            {
                Debug.Log($"[Farming] {Companion?.companionName} plant position no longer valid — re-scanning");
                _cropToPlant = null;
                SetPhase(FarmPhase.Idle);
                return false;
            }

            var storage = _inventory?.GetStorageInventory();
            if (storage != null && HasSeedsFor(storage, _cropToPlant))
            {
                foreach (var seed in _cropToPlant.Seeds)
                    RemoveItems(storage, seed.Item, seed.Amount);
                _inventory?.SaveToZDO();

                FarmingDataHelper.PlantSapling(_cropToPlant, _plantPosition);
                string sapling = _cropToPlant.Prefab.name;
                _plantedCountBySapling.TryGetValue(sapling, out int planted);
                _plantedCountBySapling[sapling] = planted + 1;
                _seedsPlanted++;
                Debug.Log($"[Farming] {Companion?.companionName} planted {sapling}");
            }

            _cropToPlant = null;
            SetPhase(FarmPhase.Idle);
            return false;
        }

        #endregion

        // -
        // Drop collection phases
        // -

        #region Drop Collection

        private bool UpdateCollectingDrops()
        {
            // Brief pause so world drops have time to spawn.
            if (Time.time - _phaseStartTime < 1f) return false;

            // Populate pickup list if not already built.
            if (_pendingPickups.Count == 0 && _currentPickupTarget == null)
            {
                var cols = Physics.OverlapSphere(Transform.position, DropCollectionRadius);
                _pendingPickups.Clear();
                foreach (var collider in cols)
                {
                    if (collider == null) continue;
                    var drop = collider.GetComponent<ItemDrop>();
                    if (drop != null && drop.CanPickup(true)) _pendingPickups.Add(drop);
                }

                if (_pendingPickups.Count == 0) { SetPhase(FarmPhase.Idle); return false; }
            }

            // Pick up the next item in the list.
            if (_currentPickupTarget == null)
            {
                _currentPickupTarget = _pendingPickups[0];
                _pendingPickups.RemoveAt(0);
            }

            if (_currentPickupTarget == null || !_currentPickupTarget.gameObject.activeInHierarchy)
            {
                _currentPickupTarget = null;
                return false;
            }

            float dist = Vector3.Distance(Transform.position, _currentPickupTarget.transform.position);
            if (dist > PickupDistance)
            {
                TryMoveToPosition(_currentPickupTarget.transform.position);
            }
            else
            {
                StopMovement();
                TryPickupItem(_currentPickupTarget);
                _currentPickupTarget = null;
                if (_pendingPickups.Count == 0) SetPhase(FarmPhase.Idle);
            }

            if (Time.time - _phaseStartTime > DropCollectionTimeout)
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout collecting drops");
                _pendingPickups.Clear();
                _currentPickupTarget = null;
                SetPhase(FarmPhase.Idle);
            }
            return false;
        }

        private void TryPickupItem(ItemDrop drop)
        {
            if (drop == null || _inventory == null) return;
            var storage = _inventory.GetStorageInventory();
            if (storage == null) return;

            var data = drop.m_itemData;
            if (data == null || !storage.CanAddItem(data)) return;

            // A partial merge returns false with the rest left in the clone's stack; that rest stays on the ground.
            var picked = data.Clone();
            if (storage.AddItem(picked))
                drop.GetComponent<ZNetView>()?.Destroy();
            else
                drop.SetStack(picked.m_stack);
            _inventory.SaveToZDO();
        }

        #endregion

        // -
        // Deposit phases
        // -

        #region Deposit

        private bool UpdateMovingToChest()
        {
            if (_targetChest == null) { SetPhase(FarmPhase.Idle); return false; }

            TryMoveToPosition(_targetPosition);

            if (Vector3.Distance(Transform.position, _targetPosition) <= ArrivalDistance)
            {
                StopMovement();
                SetPhase(FarmPhase.Depositing);
                return false;
            }

            if (MovementTimedOut())
            {
                Debug.LogWarning($"[Farming] {Companion?.companionName} timeout moving to chest");
                _targetChest = null;
                SetPhase(FarmPhase.Complete);
            }
            return false;
        }

        private bool UpdateDepositing()
        {
            if (_targetChest == null || _inventory == null) { SetPhase(FarmPhase.Complete); return false; }

            StopMovement();
            FaceTarget(_targetChest.transform.position);

            float age = Time.time - _phaseStartTime;
            if (age < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (age < InteractPauseDuration) return false;

            var storage  = _inventory.GetStorageInventory();
            var chestInv = ChestHelper.TryClaimForWrite(_targetChest, Companion) ? _targetChest.GetInventory() : null;

            if (storage != null && chestInv != null)
            {
                foreach (var item in new List<ItemDrop.ItemData>(storage.GetAllItems()))
                {
                    if (item == null) continue;
                    if (IsEquippedItem(item)) continue;
                    if (!FarmingDataHelper.IsFarmProduce(item.m_dropPrefab?.name)) continue;
                    if (ChestHelper.MoveItem(storage, chestInv, item, item.m_stack) > 0) _itemsDeposited++;
                }
                _inventory.SaveToZDO();
            }

            Debug.Log($"[Farming] {Companion?.companionName} deposited {_itemsDeposited} farming items");
            _targetChest = null;
            SetPhase(FarmPhase.Complete);
            return false;
        }

        private bool IsEquippedItem(ItemDrop.ItemData item)
        {
            if (_inventory == null) return false;
            foreach (CompanionInventory.EquipmentSlot slot in System.Enum.GetValues(typeof(CompanionInventory.EquipmentSlot)))
            {
                if (_inventory.GetEquippedItem(slot) == item) return true;
            }
            return false;
        }

        #endregion
    }
}
