using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Companion idle behavior: find a water spot near home, walk to shore,
    /// cast a fishing rod, wait for a bite, reel in, and add the catch to inventory.
    ///
    /// Uses data-driven baitâ†’fish mapping built from ZNetScene Fish prefabs.
    /// </summary>
    public class FishingBehavior : WorkBehaviorBase<FishingBehavior.FishPhase>
    {
        public enum FishPhase
        {
            Scanning,
            MovingToShore,
            Casting,
            Waiting,
            Reeling,
            Collecting,
            Complete
        }

        public override string BehaviorName => "Fishing";
        public override bool AvailableForIdleRotation => true;
        public override float WanderRadiusMultiplier => 2f;

        #region Constants

        private const float SCAN_RADIUS = 30f;
        private const float MIN_WATER_DEPTH = 0.5f;
        private const float SHORE_BACK_DIST = 3f;
        private const float CAST_WAIT_MIN = 12f;
        private const float CAST_WAIT_MAX = 28f;
        private const float REEL_DURATION = 2f;
        private const string FISHING_ROD_PREFAB = "FishingRod";

        #endregion

        #region State

        private Vector3 _fishingSpot;
        private Vector3 _castTarget;
        private string _activeBaitName;
        private ItemDrop.ItemData _savedRightHand;
        private ItemDrop.ItemData _equippedRod;
        private int _fishCaught;
        private float _waitDuration;

        // Set by the command system (Shift+MMB on a water surface) to force
        // this companion to fish at that spot. Bypasses the Stay-mode + home
        // checks. Cleared once consumed in Start().
        private Vector3? _commandedSpot;

        private static Dictionary<string, List<(string fishPrefab, float weight)>> s_baitFishMap;

        #endregion

        #region WorkBehaviorBase Abstract Implementation

        protected override FishPhase InitialPhase => FishPhase.Scanning;

        protected override float GetPhaseTimeout(FishPhase phase)
        {
            return phase switch
            {
                FishPhase.Scanning => 10f,
                FishPhase.MovingToShore => 35f,
                FishPhase.Casting => 8f,
                FishPhase.Waiting => _waitDuration + 10f,
                FishPhase.Reeling => 8f,
                FishPhase.Collecting => 5f,
                _ => 0f
            };
        }

        protected override string GetPhaseDescription(FishPhase phase)
        {
            return phase switch
            {
                FishPhase.Scanning => "Looking for water",
                FishPhase.MovingToShore => "Going to fishing spot",
                FishPhase.Casting => "Casting line",
                FishPhase.Waiting => "Waiting for a bite",
                FishPhase.Reeling => "Reeling in",
                FishPhase.Collecting => "Collecting catch",
                _ => "Fishing"
            };
        }

        protected override bool UpdatePhase(FishPhase phase)
        {
            switch (phase)
            {
                case FishPhase.Scanning:     return UpdateScanning();
                case FishPhase.MovingToShore:return UpdateMovingToShore();
                case FishPhase.Casting:      return UpdateCasting();
                case FishPhase.Waiting:      return UpdateWaiting();
                case FishPhase.Reeling:      return UpdateReeling();
                case FishPhase.Collecting:   return UpdateCollecting();
                case FishPhase.Complete:
                    Complete();
                    return true;
            }
            return false;
        }

        protected override FishPhase OnPhaseTimeout(FishPhase timedOut)
        {
            LogVerbose($"Phase {timedOut} timed out");
            switch (timedOut)
            {
                case FishPhase.Scanning:
                case FishPhase.MovingToShore:
                    return FishPhase.Complete;
                case FishPhase.Casting:
                    return FishPhase.Waiting;
                case FishPhase.Waiting:
                    return FishPhase.Reeling;
                default:
                    return FishPhase.Complete;
            }
        }

        #endregion

        #region CanStart

        /// <summary>
        /// Set by the command system when the player Shift+MMBs a water
        /// surface. Forces this companion to fish at that location, bypassing
        /// the Stay-mode requirement. Still requires a fishing rod to be
        /// available â€” fishing without one is impossible.
        /// </summary>
        public void SetCommandedSpot(Vector3 waterPoint)
        {
            _commandedSpot = waterPoint;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: skip the toggle + Stay-mode gates. The rod
            // requirement still applies â€” no rod, no fishing.
            if (_commandedSpot.HasValue)
            {
                if (!HasFishingRodAvailable())
                {
                    LogVerbose("CanStart: FALSE â€” commanded but no fishing rod available");
                    return false;
                }
                LogVerbose("CanStart: TRUE â€” commanded fishing spot");
                return true;
            }

            if (!CompanionBehaviorToggles.IsFishingEnabled(Companion)) return false;
            if (!CanStartBase()) return false;

            if (IdleBehavior == null || !IdleBehavior.HasHomePosition) return false;
            if (Companion.ShouldBeFollowing) return false;

            if (!HasFishingRodAvailable()) return false;

            return true;
        }

        #endregion

        #region Phase Handlers

        private bool UpdateScanning()
        {
            // Commanded path: the player picked a specific water surface point.
            // Find a shore stand-position near it instead of running the
            // home-radius scan. If we can't, fall through to the autonomous
            // scan below.
            if (_commandedSpot.HasValue)
            {
                var commandedSpot = FindShoreNearWaterPoint(_commandedSpot.Value);
                _commandedSpot = null; // consume regardless of outcome
                if (commandedSpot.HasValue)
                {
                    _fishingSpot = commandedSpot.Value.shore;
                    _castTarget = commandedSpot.Value.water;
                    _waitDuration = Random.Range(CAST_WAIT_MIN, CAST_WAIT_MAX);
                    SetPhase(FishPhase.MovingToShore);
                    MoveToPosition(_fishingSpot);
                    return false;
                }
                LogVerbose("Commanded fishing spot has no reachable shore â€” falling back to autonomous scan");
            }

            var spot = FindWaterSpot();
            if (spot.HasValue)
            {
                _fishingSpot = spot.Value.shore;
                _castTarget = spot.Value.water;
                _waitDuration = Random.Range(CAST_WAIT_MIN, CAST_WAIT_MAX);
                SetPhase(FishPhase.MovingToShore);
                MoveToPosition(_fishingSpot);
            }
            else
            {
                LogVerbose("No suitable water found near home");
                Complete();
                return true;
            }
            return false;
        }

        private bool UpdateMovingToShore()
        {
            if (ContinueMovement())
            {
                StopMovement();
                FaceTarget(_castTarget);
                EquipFishingRod();
                SetPhase(FishPhase.Casting);
            }
            return false;
        }

        private bool UpdateCasting()
        {
            FaceTarget(_castTarget);

            if (TimeInCurrentPhase < 0.3f)
            {
                Humanoid?.StartAttack(null, false);
                return false;
            }

            bool stillAttacking = Character != null && Character.InAttack();
            if (!stillAttacking && TimeInCurrentPhase > 1.2f)
            {
                SetPhase(FishPhase.Waiting);
            }

            return false;
        }

        private bool UpdateWaiting()
        {
            FaceTarget(_castTarget);

            if (TimeInCurrentPhase >= _waitDuration)
            {
                // Fish on the line â€” reel in
                Humanoid?.StartAttack(null, false);
                SetPhase(FishPhase.Reeling);
            }

            return false;
        }

        private bool UpdateReeling()
        {
            FaceTarget(_castTarget);

            if (TimeInCurrentPhase >= REEL_DURATION)
            {
                SetPhase(FishPhase.Collecting);
            }

            return false;
        }

        private bool UpdateCollecting()
        {
            string caught = DetermineCatch(_activeBaitName);
            if (!string.IsNullOrEmpty(caught))
            {
                AddFishToInventory(caught);
                _fishCaught++;
                LogVerbose($"Caught {caught}");
            }

            UnequipFishingRod();
            NotifyOwner();

            Complete();
            return true;
        }

        #endregion

        #region Water Detection

        private (Vector3 shore, Vector3 water)? FindWaterSpot()
        {
            Vector3 origin = IdleBehavior?.HomePosition ?? Transform.position;

            for (float radius = 8f; radius <= SCAN_RADIUS; radius += 5f)
            {
                for (float angle = 0f; angle < 360f; angle += 30f)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    Vector3 waterCandidate = origin + new Vector3(
                        Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

                    if (!IsWaterAt(waterCandidate)) continue;

                    // Step back toward origin to find shoreline
                    Vector3 toOrigin = (origin - waterCandidate).normalized;
                    Vector3 shoreCandidate = waterCandidate + toOrigin * SHORE_BACK_DIST;

                    if (ZoneSystem.instance != null &&
                        ZoneSystem.instance.GetGroundHeight(shoreCandidate, out float groundY))
                    {
                        shoreCandidate.y = groundY;
                    }

                    if (!IsWaterAt(shoreCandidate))
                    {
                        return (shoreCandidate, waterCandidate);
                    }
                }
            }

            return null;
        }

        private bool IsWaterAt(Vector3 pos)
        {
            if (ZoneSystem.instance == null) return false;
            ZoneSystem.instance.GetSolidHeight(pos, out float groundY);
            // Sea level in Valheim is y=30; water exists where terrain is submerged
            return (30f - groundY) >= MIN_WATER_DEPTH;
        }

        /// <summary>
        /// Find a shore stand position adjacent to a specific water point. Used
        /// by the command system: the player Shift+MMBs a water surface, we
        /// receive the world-space hit point, and need to convert it into a
        /// (shore, water) pair the rest of the fishing pipeline understands.
        /// Returns null if no walkable shore is reachable nearby.
        /// </summary>
        private (Vector3 shore, Vector3 water)? FindShoreNearWaterPoint(Vector3 waterPoint)
        {
            if (!IsWaterAt(waterPoint)) return null;

            // Sweep candidate shore points outward from the water point in
            // every direction; pick the first that is solid ground.
            for (float radius = SHORE_BACK_DIST; radius <= 12f; radius += 1.5f)
            {
                for (float angle = 0f; angle < 360f; angle += 30f)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    Vector3 shoreCandidate = waterPoint + new Vector3(
                        Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

                    if (ZoneSystem.instance != null &&
                        ZoneSystem.instance.GetGroundHeight(shoreCandidate, out float groundY))
                    {
                        shoreCandidate.y = groundY;
                    }

                    if (!IsWaterAt(shoreCandidate))
                    {
                        return (shoreCandidate, waterPoint);
                    }
                }
            }

            return null;
        }

        #endregion

        #region Rod Equip / Unequip

        private void EquipFishingRod()
        {
            if (Inventory == null) return;
            var storage = GetStorageInventory();
            if (storage == null) return;

            ItemDrop.ItemData rod = null;
            foreach (var item in storage.GetAllItems())
            {
                if (item?.m_dropPrefab?.name == FISHING_ROD_PREFAB) { rod = item; break; }
            }
            if (rod == null) return;

            _savedRightHand = Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (_savedRightHand != null)
                Inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);

            storage.RemoveItem(rod);
            Inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, rod);
            Inventory.RecalculateEquipmentBonusesPublic();
            Inventory.ApplyVisualEquipment();
            Inventory.SaveToZDO();
            _equippedRod = rod;

            // Find and consume one bait from storage
            EnsureBaitFishMapBuilt();
            _activeBaitName = null;
            foreach (var item in storage.GetAllItems())
            {
                if (item?.m_dropPrefab == null) continue;
                string pname = item.m_dropPrefab.name;
                if (!s_baitFishMap.ContainsKey(pname)) continue;
                _activeBaitName = pname;
                item.m_stack--;
                if (item.m_stack <= 0) storage.RemoveItem(item);
                Inventory.SaveToZDO();
                break;
            }

            if (_activeBaitName != null)
                LogVerbose($"Equipped rod with {_activeBaitName} bait");
            else
                LogVerbose("Equipped rod (no bait â€” low catch chance)");
        }

        private void UnequipFishingRod()
        {
            if (Inventory == null || _equippedRod == null) return;

            Inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
            GetStorageInventory()?.AddItem(_equippedRod);
            _equippedRod = null;

            if (_savedRightHand != null)
            {
                Inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, _savedRightHand);
                _savedRightHand = null;
            }

            Inventory.RecalculateEquipmentBonusesPublic();
            Inventory.ApplyVisualEquipment();
            Inventory.SaveToZDO();
        }

        #endregion

        #region Catch Determination

        private string DetermineCatch(string baitPrefabName)
        {
            EnsureBaitFishMapBuilt();

            List<(string prefab, float weight)> pool;
            if (!string.IsNullOrEmpty(baitPrefabName) &&
                s_baitFishMap.TryGetValue(baitPrefabName, out var specific))
            {
                pool = specific;
            }
            else
            {
                // No bait: pool all fish equally
                pool = new List<(string, float)>();
                foreach (var kvp in s_baitFishMap)
                    foreach (var entry in kvp.Value)
                        pool.Add(entry);
            }

            if (pool.Count == 0) return "Fish1";

            float total = 0f;
            foreach (var c in pool) total += c.weight;

            float roll = Random.Range(0f, total);
            float cumulative = 0f;
            foreach (var c in pool)
            {
                cumulative += c.weight;
                if (roll <= cumulative) return c.prefab;
            }

            return pool[pool.Count - 1].prefab;
        }

        private static void EnsureBaitFishMapBuilt()
        {
            if (s_baitFishMap != null) return;
            s_baitFishMap = new Dictionary<string, List<(string, float)>>();

            if (ZNetScene.instance == null) return;

            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                var fish = prefab.GetComponent<Fish>();
                if (fish?.m_baits == null) continue;

                foreach (var bait in fish.m_baits)
                {
                    if (bait?.m_bait == null) continue;
                    string baitName = bait.m_bait.name;
                    if (!s_baitFishMap.ContainsKey(baitName))
                        s_baitFishMap[baitName] = new List<(string, float)>();
                    s_baitFishMap[baitName].Add((prefab.name, bait.m_chance));
                }
            }
        }

        #endregion

        #region Inventory Helpers

        private void AddFishToInventory(string fishPrefabName)
        {
            var storage = GetStorageInventory();
            if (storage == null || ZNetScene.instance == null) return;

            var prefab = ZNetScene.instance.GetPrefab(fishPrefabName);
            if (prefab == null) return;

            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null) return;

            var fishItem = itemDrop.m_itemData.Clone();
            fishItem.m_stack = 1;
            if (storage.AddItem(fishItem))
                SaveInventory();
        }

        private bool HasFishingRodAvailable()
        {
            if (Inventory == null) return false;

            var storage = GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name == FISHING_ROD_PREFAB) return true;
                }
            }

            var rh = Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            return rh?.m_dropPrefab?.name == FISHING_ROD_PREFAB;
        }

        private void NotifyOwner()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);

            var owner = Companion?.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;

            if (_fishCaught > 0)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{Companion.GetDisplayName()} caught {_fishCaught} fish!");
            }
        }

        #endregion
    }
}
