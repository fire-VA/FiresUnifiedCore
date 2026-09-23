using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Companion idle behavior: find a water spot near home, walk to shore,
    /// cast a fishing rod, wait for a bite, reel in, and add the catch to inventory.
    ///
    /// Uses data-driven bait→fish mapping built from ZNetScene Fish prefabs.
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

        private const float ScanRadius = 30f;
        private const float MinWaterDepth = 0.5f;
        private const float ShoreBackDist = 3f;
        private const float CastWaitMin = 12f;
        private const float CastWaitMax = 28f;
        private const float CastAnimationDuration = 1.2f;
        private const float ReelDuration = 2f;
        private const string FishingRodPrefab = "FishingRod";

        // Player rig parameters (player_animator): the throw, and the Bool that holds the rod's pull pose.
        private const string CastTrigger = "fishingrod_throw";
        private const string ReelPoseBool = "fishingrod_charge";

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
                    UnequipFishingRod();
                    Complete();
                    return true;
            }
            return false;
        }

        /// <summary>Companion StartAttack never casts, so the rig is driven directly: throw on cast, pull pose while reeling.</summary>
        protected override void OnPhaseChanged(FishPhase fromPhase, FishPhase toPhase)
        {
            if (toPhase == FishPhase.Casting)
                ZAnim?.SetTrigger(CastTrigger);

            if (toPhase == FishPhase.Reeling)
                SetReelPose(true);
            else if (fromPhase == FishPhase.Reeling)
                SetReelPose(false);
        }

        public override void Cancel()
        {
            SetReelPose(false);
            UnequipFishingRod();
            base.Cancel();
        }

        private void SetReelPose(bool reeling) => ZAnim?.SetBool(ReelPoseBool, reeling);

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
        /// available — fishing without one is impossible.
        /// </summary>
        public void SetCommandedSpot(Vector3 waterPoint)
        {
            _commandedSpot = waterPoint;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: skip the toggle + Stay-mode gates. The rod
            // requirement still applies — no rod, no fishing.
            if (_commandedSpot.HasValue)
            {
                if (!HasFishingRodAvailable())
                {
                    LogVerbose("CanStart: FALSE — commanded but no fishing rod available");
                    return false;
                }
                LogVerbose("CanStart: TRUE — commanded fishing spot");
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
                    _waitDuration = Random.Range(CastWaitMin, CastWaitMax);
                    SetPhase(FishPhase.MovingToShore);
                    MoveToPosition(_fishingSpot);
                    return false;
                }
                LogVerbose("Commanded fishing spot has no reachable shore — falling back to autonomous scan");
            }

            var spot = FindWaterSpot();
            if (spot.HasValue)
            {
                _fishingSpot = spot.Value.shore;
                _castTarget = spot.Value.water;
                _waitDuration = Random.Range(CastWaitMin, CastWaitMax);
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

            bool stillThrowing = Character != null && Character.InAttack();
            if (!stillThrowing && TimeInCurrentPhase > CastAnimationDuration)
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
                // Fish on the line — reel in
                SetPhase(FishPhase.Reeling);
            }

            return false;
        }

        private bool UpdateReeling()
        {
            FaceTarget(_castTarget);

            if (TimeInCurrentPhase >= ReelDuration)
            {
                SetPhase(FishPhase.Collecting);
            }

            return false;
        }

        private bool UpdateCollecting()
        {
            // Rod first: the catch must not take the slot the rod came out of.
            UnequipFishingRod();

            string caught = DetermineCatch(_activeBaitName);
            if (!string.IsNullOrEmpty(caught) && AddFishToInventory(caught))
            {
                _fishCaught++;
                LogVerbose($"Caught {caught}");
            }

            NotifyOwner();

            Complete();
            return true;
        }

        #endregion

        #region Water Detection

        private (Vector3 shore, Vector3 water)? FindWaterSpot()
        {
            Vector3 origin = IdleBehavior?.HomePosition ?? Transform.position;

            for (float radius = 8f; radius <= ScanRadius; radius += 5f)
            {
                for (float angle = 0f; angle < 360f; angle += 30f)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    Vector3 waterCandidate = origin + new Vector3(
                        Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

                    if (!IsWaterAt(waterCandidate)) continue;

                    // Step back toward origin to find shoreline
                    Vector3 toOrigin = (origin - waterCandidate).normalized;
                    Vector3 shoreCandidate = waterCandidate + toOrigin * ShoreBackDist;

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

        /// <summary>True where the solid ground (terrain or pieces) lies at least MinWaterDepth below the sea level.
        /// No hit means no ground was found, which is not water.</summary>
        public static bool IsWaterAt(Vector3 pos)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || !zoneSystem.GetSolidHeight(pos, out float groundY)) return false;
            return zoneSystem.m_waterLevel - groundY >= MinWaterDepth;
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
            for (float radius = ShoreBackDist; radius <= 12f; radius += 1.5f)
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
                if (item?.m_dropPrefab?.name == FishingRodPrefab) { rod = item; break; }
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
                string prefabName = item.m_dropPrefab.name;
                if (!s_baitFishMap.ContainsKey(prefabName)) continue;
                _activeBaitName = prefabName;
                item.m_stack--;
                if (item.m_stack <= 0) storage.RemoveItem(item);
                Inventory.SaveToZDO();
                break;
            }

            if (_activeBaitName != null)
                LogVerbose($"Equipped rod with {_activeBaitName} bait");
            else
                LogVerbose("Equipped rod (no bait — low catch chance)");
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
            foreach (var candidate in pool) total += candidate.weight;

            float roll = Random.Range(0f, total);
            float cumulative = 0f;
            foreach (var candidate in pool)
            {
                cumulative += candidate.weight;
                if (roll <= cumulative) return candidate.prefab;
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

        // Created from the prefab so m_dropPrefab is set and the fish survives save/load (Inventory.cs:88-96).
        private bool AddFishToInventory(string fishPrefabName)
        {
            var storage = GetStorageInventory();
            if (storage == null || ZNetScene.instance == null) return false;

            var prefab = ZNetScene.instance.GetPrefab(fishPrefabName);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) return false;

            if (!storage.AddItem(prefab, 1)) return false;
            SaveInventory();
            return true;
        }

        private bool HasFishingRodAvailable()
        {
            if (Inventory == null) return false;

            var storage = GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name == FishingRodPrefab) return true;
                }
            }

            var rightHandItem = Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            return rightHandItem?.m_dropPrefab?.name == FishingRodPrefab;
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
