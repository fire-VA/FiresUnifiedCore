using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.AI;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Companion idle behavior: real fishing on the one fishing brain (Core 0.2.243, <see cref="FishingBrain"/>, the bot's too). Plan
    /// a dry stand at the water's edge near home with deep water in reach (toward fish that take a carried bait), walk there, take out
    /// the rod, throw the rod's own projectile with one bait at the planned point (the game drops a FishingFloat where it lands), then
    /// each tick do what the brain advises: wait, hook and reel (hold the line in) on a bite, hold while a hooked fish runs, recast when
    /// nothing bites. The game's float lands the catch into the companion's bag; it is moved into storage.
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
        /// <summary>0.2.268: how long a companion leaves fishing alone after a plan found no fish / water / shore near home.</summary>
        private const float NoFishCooldown = 300f;
        private float _noFishUntil;
        private const float CommandedRadius = 14f;
        private const float MinWaterDepth = 0.5f;
        private const float CastReleaseTime = 0.55f;
        private const float CastAnimationDuration = 1.2f;
        private const float RestandDistance = 2f;
        private const int MaxCasts = 8;
        private const int MaxCatches = 3;

        // Player rig parameters (player_animator): the throw, and the Bool that holds the rod's pull pose.
        private const string CastTrigger = "fishingrod_throw";
        private const string ReelPoseBool = "fishingrod_charge";

        #endregion

        #region State

        private FishingBrain.FishPlan _plan;
        private Vector3 _planNear;
        private float _planRadius;
        private bool _thrown;
        private int _casts;
        private ItemDrop.ItemData _savedRightHand;
        private ItemDrop.ItemData _equippedRod;
        private int _fishCaught;

        // Set by the command system (Shift+MMB on a water surface) to force
        // this companion to fish at that spot. Bypasses the Stay-mode + home
        // checks. Cleared once consumed in Start().
        private Vector3? _commandedSpot;

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
                FishPhase.Waiting => 45f,     // the brain recasts after 30 s without a bite
                FishPhase.Reeling => 60f,
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
                case FishPhase.Waiting:
                case FishPhase.Reeling:      return UpdateLine();
                case FishPhase.Collecting:   return UpdateCollecting();
                case FishPhase.Complete:
                    StopFishing();
                    NotifyOwner();
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
            StopFishing();
            base.Cancel();
        }

        private void SetReelPose(bool reeling) => ZAnim?.SetBool(ReelPoseBool, reeling);

        protected override FishPhase OnPhaseTimeout(FishPhase timedOut)
        {
            LogVerbose($"Phase {timedOut} timed out");
            switch (timedOut)
            {
                case FishPhase.Casting:
                case FishPhase.Waiting:
                case FishPhase.Reeling:
                    FishingBrain.Withdraw(Character);
                    return FishPhase.Complete;
                case FishPhase.MovingToShore:
                {
                    // 0.2.258 (R37: three stalled walks to the water's edge at a shelved pond): the stand wasn't reached; cast from where
                    // it stands when deep water lies within a cast, else give up as before.
                    var storage = GetStorageInventory();
                    FishingBrain.FishPlan fromHere = null;
                    string here = storage != null
                        ? FishingBrain.PlanHere(Character, storage, _equippedRod ?? FishingBrain.RodIn(storage), out fromHere)
                        : "missing: no storage";
                    Vector3 at = Transform != null ? Transform.position : Vector3.zero;
                    if (here == "")
                    {
                        Debug.Log($"[Fishing] {Companion?.companionName} did not reach the stand at ({_plan.Stand.x:0}, {_plan.Stand.z:0}) from ({at.x:0}, {at.z:0}); casting from here: {fromHere}");
                        _plan = fromHere;
                        StopMovement();
                        FaceTarget(_plan.Aim);
                        EquipFishingRod();
                        _thrown = false;
                        return FishPhase.Casting;
                    }
                    Debug.Log($"[Fishing] {Companion?.companionName} did not reach the stand at ({_plan.Stand.x:0}, {_plan.Stand.z:0}) from ({at.x:0}, {at.z:0}) and cannot cast from here: {here}");
                    return FishPhase.Complete;
                }
                default:
                    return FishPhase.Complete;
            }
        }

        #endregion

        #region CanStart

        /// <summary>
        /// Set by the command system when the player Shift+MMBs a water
        /// surface. Forces this companion to fish at that location, bypassing
        /// the Stay-mode requirement. Still requires a fishing rod and bait.
        /// </summary>
        public void SetCommandedSpot(Vector3 waterPoint)
        {
            _commandedSpot = waterPoint;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: skip the toggle + Stay-mode gates. Rod and bait still apply: no rod or bait, no fishing.
            if (_commandedSpot.HasValue)
            {
                string missing = MissingGear();
                if (missing != null)
                {
                    Debug.Log($"[Fishing] {Companion.companionName} cannot fish at the commanded spot: {missing}");
                    _commandedSpot = null;
                    return false;
                }
                LogVerbose("CanStart: TRUE — commanded fishing spot");
                return true;
            }

            if (!CompanionBehaviorToggles.IsFishingEnabled(Companion)) return false;
            // 0.2.268 (HR3: "Fishing after 0 s (done)" over and over at a pond with no fish): after a plan found no fish / water /
            // shore near home, this companion leaves fishing alone for NoFishCooldown (a commanded spot above still goes).
            if (Time.time < _noFishUntil) return false;
            if (!CanStartBase()) return false;

            if (IdleBehavior == null || !IdleBehavior.HasHomePosition) return false;
            if (Companion.ShouldBeFollowing) return false;

            return MissingGear() == null;
        }

        #endregion

        #region Phase Handlers

        private bool UpdateScanning()
        {
            _casts = 0;
            _fishCaught = 0;
            if (_commandedSpot.HasValue)
            {
                _planNear = _commandedSpot.Value;
                _planRadius = CommandedRadius;
                _commandedSpot = null; // consume regardless of outcome
            }
            else
            {
                _planNear = IdleBehavior?.HomePosition ?? Transform.position;
                _planRadius = ScanRadius;
            }

            string why = PlanCast();
            if (why != "")
            {
                bool rest = why.StartsWith("nofish") || why.StartsWith("nowater") || why.StartsWith("noshore");
                if (rest) _noFishUntil = Time.time + NoFishCooldown;
                Debug.Log($"[Fishing] {Companion?.companionName} cannot fish near ({_planNear.x:0}, {_planNear.z:0}): {why}" +
                          (rest ? $"; not trying again for {NoFishCooldown / 60f:0} min" : ""));
                SetPhase(FishPhase.Complete);
                return false;
            }
            // 0.2.258: the stand, how it was chosen and its reach, the cast distance (always on).
            Debug.Log($"[Fishing] {Companion?.companionName} plans: {_plan}");
            SetPhase(FishPhase.MovingToShore);
            MoveToPosition(_plan.Stand);
            return false;
        }

        private bool UpdateMovingToShore()
        {
            if (ContinueMovement())
            {
                StopMovement();
                FaceTarget(_plan.Aim);
                EquipFishingRod();
                _thrown = false;
                SetPhase(FishPhase.Casting);
            }
            return false;
        }

        private bool UpdateCasting()
        {
            FaceTarget(_plan.Aim);

            // The throw animation releases about half a second in; the rod's projectile leaves then.
            if (!_thrown && TimeInCurrentPhase >= CastReleaseTime)
            {
                _thrown = true;
                string why = FishingBrain.CastProjectile(Character, _plan, GetStorageInventory());
                if (why != "")
                {
                    Debug.Log($"[Fishing] {Companion?.companionName} cannot cast: {why}");
                    SetPhase(FishPhase.Complete);
                    return false;
                }
                _casts++;
                SaveInventory();
            }
            if (_thrown && TimeInCurrentPhase > CastAnimationDuration)
                SetPhase(FishPhase.Waiting);
            return false;
        }

        // The line is out: what the brain says, every tick.
        private bool UpdateLine()
        {
            FaceTarget(_plan.Aim);
            SweepBagIntoStorage();

            FishingBrain.FishMove move = FishingBrain.Advise(Character, out string why);
            switch (move)
            {
                case FishingBrain.FishMove.Wait:
                case FishingBrain.FishMove.Rest:
                    FishingBrain.SetReeling(Character, false);
                    if (CurrentPhase != FishPhase.Waiting) SetPhase(FishPhase.Waiting);
                    break;
                case FishingBrain.FishMove.Hook:
                case FishingBrain.FishMove.Reel:
                    FishingBrain.SetReeling(Character, true);
                    if (CurrentPhase != FishPhase.Reeling) SetPhase(FishPhase.Reeling);
                    break;
                case FishingBrain.FishMove.Landed:
                    FishingBrain.SetReeling(Character, false);
                    _fishCaught++;
                    SetPhase(FishPhase.Collecting);
                    break;
                case FishingBrain.FishMove.Lost:
                case FishingBrain.FishMove.Recast:
                    FishingBrain.SetReeling(Character, false);
                    NextCast(why);
                    break;
                default:
                    Debug.Log($"[Fishing] {Companion?.companionName} stops fishing: {why}");
                    SetPhase(FishPhase.Complete);
                    break;
            }
            return false;
        }

        private bool UpdateCollecting()
        {
            SweepBagIntoStorage();
            if (TimeInCurrentPhase < 0.5f) return false;
            if (_fishCaught >= MaxCatches) { SetPhase(FishPhase.Complete); return false; }
            NextCast("landed one");
            return false;
        }

        // Another cast: the old float back in (its bait returned), a fresh plan (fish move), walking only when the stand moved.
        private void NextCast(string why)
        {
            FishingBrain.Withdraw(Character);
            SweepBagIntoStorage();
            if (_casts >= MaxCasts) { LogVerbose($"Done after {_casts} casts"); SetPhase(FishPhase.Complete); return; }
            Vector3 was = _plan.Stand;
            string refusal = PlanCast();
            if (refusal != "")
            {
                Debug.Log($"[Fishing] {Companion?.companionName} stops fishing ({why}): {refusal}");
                SetPhase(FishPhase.Complete);
                return;
            }
            if (Vector3.Distance(was, _plan.Stand) > RestandDistance)
            {
                SetPhase(FishPhase.MovingToShore);
                MoveToPosition(_plan.Stand);
                return;
            }
            _thrown = false;
            SetPhase(FishPhase.Casting);
        }

        private string PlanCast()
        {
            var storage = GetStorageInventory();
            if (storage == null) return "missing: no storage";
            string why = FishingBrain.Plan(Character, storage, _equippedRod ?? FishingBrain.RodIn(storage), _planNear, _planRadius, IsReachable, out var plan);
            if (why == "") _plan = plan;
            return why;
        }

        #endregion

        #region Water Detection

        /// <summary>True where water at least MinWaterDepth deep lies over the solid ground (terrain or pieces): the sea or inland
        /// water (FishingBrain.WaterAt, the WaterVolume the fish use).</summary>
        public static bool IsWaterAt(Vector3 pos) => FishingBrain.WaterAt(pos, out _, out float depth) && depth >= MinWaterDepth;

        #endregion

        #region Rod Equip / Unequip

        // What is missing to fish: a rod (in storage or in hand) and bait for it; null when both are there.
        private string MissingGear()
        {
            if (Inventory == null) return "no inventory";
            var storage = GetStorageInventory();
            ItemDrop.ItemData rod = FishingBrain.RodIn(storage);
            var inHand = Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rod == null && FishingBrain.FloatPrefabOf(inHand) != null) rod = inHand;
            if (rod == null) return "no fishing rod";
            return FishingBrain.BestBait(storage, null, null, rod) == null ? "no bait" : null;
        }

        private void EquipFishingRod()
        {
            if (Inventory == null || _equippedRod != null) return;
            var storage = GetStorageInventory();
            ItemDrop.ItemData rod = FishingBrain.RodIn(storage);
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
            if (_plan != null) _plan.Rod = rod;
            LogVerbose($"Equipped {rod.m_dropPrefab?.name}");
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

        // The line in, the reel let go, the catch and any returned bait into storage, the rod away.
        private void StopFishing()
        {
            if (Character != null)
            {
                FishingBrain.Withdraw(Character);
                FishingBrain.SetReeling(Character, false);
            }
            SetReelPose(false);
            SweepBagIntoStorage();
            UnequipFishingRod();
        }

        #endregion

        #region Inventory Helpers

        // The game's float lands a catch (and returns an unused bait) into the companion's Humanoid bag, which nothing else uses; it all
        // goes into storage.
        private void SweepBagIntoStorage()
        {
            Inventory bag = Humanoid != null ? Humanoid.GetInventory() : null;
            Inventory storage = GetStorageInventory();
            if (bag == null || storage == null || bag.NrOfItems() == 0) return;
            bool moved = false;
            foreach (ItemDrop.ItemData item in bag.GetAllItems().ToList())
            {
                if (item == null || !storage.CanAddItem(item)) continue;
                bag.RemoveItem(item);
                storage.AddItem(item);
                moved = true;
                if (item.m_dropPrefab != null && item.m_dropPrefab.GetComponent<Fish>() != null)
                {
                    Debug.Log($"[Fishing] {Companion?.companionName} stored {item.m_dropPrefab.name}{(item.m_quality > 1 ? $" (q{item.m_quality})" : "")}");
                    AI.ChoreBrain.ChoreDone(Companion?.companionName, "fishing", $"caught {item.m_dropPrefab.name}{(item.m_quality > 1 ? $" (q{item.m_quality})" : "")}");
                }
            }
            if (moved) SaveInventory();
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
