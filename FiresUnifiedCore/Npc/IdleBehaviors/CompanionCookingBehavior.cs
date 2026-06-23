using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Dedicated cooking behavior for companions.
    ///
    /// FULL LOOP
    /// ---------
    /// 1. Scan for nearby CookingStation(s) within range.
    /// 2. Check whether the companion carries raw food that any station accepts.
    ///    If not, scan nearby chests for raw food and pull what is needed.
    /// 3. Walk to the station, fill every empty slot with the appropriate raw item.
    /// 4. Stand at the station and wait.  Every tick we:
    ///    a. Collect any slot that is DONE (slotstatus == 2) so it doesn't burn.
    ///    b. Refill vacated slots with more raw food if available.
    ///    c. Detect burned items (slotstatus == 3) and collect them so the slot
    ///       is freed up.
    /// 5. When all food is loaded and nothing is left to add, keep waiting until
    ///    every slot that still has food is collected.
    /// 6. Deposit cooked food to nearby chests.
    /// 7. Complete and notify the owner.
    ///
    /// DESIGN NOTES
    /// ------------
    /// * Handles multiple cooking stations in sequence (visits nearest first).
    /// * Pulls raw food from chests when the companion's own inventory is empty.
    /// * Never keeps burned/spoiled food ï¿½ collects it immediately to free the slot.
    /// * Deposits cooked output to chests using SmartStorageOrganizer so it lands
    ///   in the right chest (food storage, near cauldron, etc.).
    /// * Priority is elevated when the companion has raw food that is about to
    ///   expire (m_shared.m_foodBurnTime > 0 and item is close to its limit) ï¿½
    ///   this is a nice-to-have future extension; for now priority is 0.
    /// </summary>
    public class CompanionCookingBehavior : WorkBehaviorBase<CompanionCookingBehavior.CookPhase>
    {
        public override string BehaviorName => "CompanionCooking";
        public override bool AvailableForIdleRotation => true;

        // Priority tiers:
        //   50 = done/burned food on a nearby station needs immediate collection
        //   20 = raw food in inventory ready to load
        //    0 = nothing urgent; behaviour competes with other idle tasks
        public override int InventoryPriority
        {
            get
            {
                if (HasRawFoodInInventory()) return 20;
                // Check if any nearby station has finished cooking ï¿½ collecting
                // before it burns is time-critical so beat all priority-0 tasks.
                var stations = FindCookingStations();
                foreach (var s in stations)
                    if (HasDoneItems(s) || HasBurnedItems(s)) return 50;
                return 0;
            }
        }

        // ?? phase enum ????????????????????????????????????????????????????????

        public enum CookPhase
        {
            Scanning,           // Find a station and raw food source
            MovingToChest,      // Pull raw food from a nearby chest
            PullingFood,        // Transfer items from chest to companion
            MovingToStation,    // Walk to cooking station
            LoadingFood,        // Add raw food to every empty slot
            Tending,            // Wait; collect done items; refill slots
            MovingToDeposit,    // Walk to nearest chest to deposit cooked output
            Depositing,         // Transfer cooked food into chest
            Complete
        }

        // ?? tuneable constants ????????????????????????????????????????????????

        private const float STATION_SCAN_RADIUS  = 12f;
        private float ChestSearchRadius => CompanionSettings.ChestSearchRadius;
        private const float INTERACTION_DIST     = 2.0f;
        private const float MAX_COOK_TIME        = 300f;  // 5-minute global cap
        private const float TEND_CHECK_INTERVAL  = 1.5f;  // how often we query slot state
        private const float TEND_CHECK_INTERVAL_URGENT = 0.3f; // when food is done/burning, react fast
        private const float IDLE_GIVE_UP_TIME    = 40f;   // bail if nothing happens this long

        // slotstatus ZDO values used by Valheim's CookingStation
        private const int STATUS_EMPTY  = 0;
        private const int STATUS_COOKING = 1;
        private const int STATUS_DONE   = 2;
        private const int STATUS_BURNED = 3;

        // ?? state ?????????????????????????????????????????????????????????????

        private List<CookingStation> _stations = new List<CookingStation>();
        private CookingStation _currentStation;
        private Container       _rawFoodChest;

        private int _itemsCooked;
        private int _itemsDeposited;

        private float _tendTimer;
        private float _idleTimer;   // time with nothing cooking ï¿½ bail guard

        private List<Container>           _depositChests   = new List<Container>();
        private List<ItemDrop.ItemData>   _toDeposit       = new List<ItemDrop.ItemData>();
        private int                        _depositIndex;

        // Stations that failed pathfinding this session â€” skipped in Scanning until behavior restarts.
        private HashSet<CookingStation> _failedStations = new HashSet<CookingStation>();

        // Set by the command system (Shift+MMB on a CookingStation) to force this
        // companion onto a specific station. Bypasses the toggle and the
        // autonomous CanStart checks. Cleared once consumed in Start().
        private GameObject _commandedTarget;

        // ?? WorkBehaviorBase wiring ???????????????????????????????????????????

        protected override CookPhase InitialPhase => CookPhase.Scanning;

        protected override float GetPhaseTimeout(CookPhase phase)
        {
            return phase switch
            {
                CookPhase.Scanning        => 8f,
                CookPhase.MovingToChest   => 20f,
                CookPhase.PullingFood     => 10f,
                CookPhase.MovingToStation => 30f,
                CookPhase.LoadingFood     => 15f,
                CookPhase.Tending         => MAX_COOK_TIME,
                CookPhase.MovingToDeposit => 20f,
                CookPhase.Depositing      => 15f,
                _                         => 10f
            };
        }

        protected override string GetPhaseDescription(CookPhase phase)
        {
            return phase switch
            {
                CookPhase.Scanning        => "Looking for cooking station",
                CookPhase.MovingToChest   => "Getting raw food from chest",
                CookPhase.PullingFood     => "Taking raw food",
                CookPhase.MovingToStation => "Walking to cooking station",
                CookPhase.LoadingFood     => "Loading food onto station",
                CookPhase.Tending         => $"Cooking ({_itemsCooked} done)",
                CookPhase.MovingToDeposit => "Carrying cooked food to storage",
                CookPhase.Depositing      => "Storing cooked food",
                _                         => "Cooking"
            };
        }

        protected override CookPhase OnPhaseTimeout(CookPhase timedOut)
        {
            LogWarning($"Phase {timedOut} timed out");

            // When we fail to reach a station, mark it as unreachable so
            // Scanning skips it and tries a different one (or completes if none left).
            if (timedOut == CookPhase.MovingToStation && _currentStation != null)
            {
                _failedStations.Add(_currentStation);
                _currentStation = null;
            }

            return timedOut switch
            {
                CookPhase.MovingToChest   => CookPhase.Scanning,
                CookPhase.MovingToStation => CookPhase.Scanning,
                CookPhase.LoadingFood     => CookPhase.Tending,
                CookPhase.MovingToDeposit => CookPhase.Complete,
                CookPhase.Depositing      => CookPhase.Complete,
                _                         => CookPhase.Complete
            };
        }

        // ?? lifecycle ?????????????????????????????????????????????????????????

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MAX_COOK_TIME;
        }

        /// <summary>
        /// Set by the command system when the player Shift+MMBs a CookingStation.
        /// Forces this companion to operate that specific station, bypassing the
        /// behavior toggle and the autonomous proximity scan.
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: the player explicitly told this companion to cook
            // at a specific station â€” skip the toggle and autonomous checks. We
            // still verify the target actually has a CookingStation component.
            if (_commandedTarget != null)
            {
                var commandedStation = _commandedTarget.GetComponent<CookingStation>()
                                    ?? _commandedTarget.GetComponentInParent<CookingStation>();
                if (commandedStation != null)
                {
                    LogVerbose("CanStart: TRUE â€” commanded cooking station");
                    return true;
                }
                // Target lost its station component (destroyed, replaced); drop the command.
                _commandedTarget = null;
            }

            // Player can disable cooking entirely from the radial menu.
            if (!CompanionBehaviorToggles.IsCookingEnabled(Companion)) return false;

            // Need at least one accessible cooking station nearby.
            var stations = FindCookingStations();
            if (stations.Count == 0)
            {
                LogVerbose("CanStart: FALSE ï¿½ no cooking stations nearby");
                return false;
            }

            // Check if any station already has food we should collect first.
            foreach (var s in stations)
            {
                if (HasDoneItems(s) || HasBurnedItems(s))
                {
                    LogVerbose("CanStart: TRUE ï¿½ station has finished/burned food to collect");
                    return true;
                }
            }

            // Check if we have raw food to cook (in inventory or in nearby chests).
            if (HasRawFoodForAnyStation(stations))
            {
                LogVerbose("CanStart: TRUE ï¿½ have raw food for a station");
                return true;
            }

            LogVerbose("CanStart: FALSE ï¿½ no raw food available");
            return false;
        }

        public override void Start()
        {
            base.Start();

            _stations.Clear();
            _stations = FindCookingStations();
            _currentStation = null;
            _rawFoodChest   = null;
            _failedStations.Clear();

            // If the player commanded a specific station, pin it as the active
            // target and put it at the head of the candidate list so Scanning
            // picks it on the first tick. Then clear the command flag â€” it has
            // served its purpose and shouldn't survive a behavior restart.
            if (_commandedTarget != null)
            {
                var commandedStation = _commandedTarget.GetComponent<CookingStation>()
                                    ?? _commandedTarget.GetComponentInParent<CookingStation>();
                if (commandedStation != null)
                {
                    _currentStation = commandedStation;
                    _stations.Remove(commandedStation);
                    _stations.Insert(0, commandedStation);
                }
                _commandedTarget = null;
            }
            _itemsCooked    = 0;
            _itemsDeposited = 0;
            _tendTimer      = 0f;
            _idleTimer      = 0f;
            _pendingCollectTime = -1f;
            _toDeposit.Clear();
            _depositChests.Clear();
            _depositIndex = 0;

            Resources.RefreshNearbyChests(true);

            CompanionChatHelper.QuickMessages.TendingFire(Companion);

            // Register so the CookingStation patch can wake us up when food finishes.
            lock (_activeBehaviorsLock)
            {
                _activeBehaviors.Add(this);
            }
        }

        protected override void Complete()
        {
            UnregisterActiveBehavior();
            base.Complete();
        }

        private void UnregisterActiveBehavior()
        {
            lock (_activeBehaviorsLock)
            {
                _activeBehaviors.Remove(this);
            }
        }

        // ?? event-driven wake-up ?????????????????????????????????????????????
        // The CookingStation patch (below) calls into here the moment a slot
        // transitions to Done.  Reset the tend timer so the very next Update
        // tick runs the collection loop instead of waiting for the urgent poll
        // interval.  The companion effectively "hears" the dish-is-ready cue
        // and reacts immediately.

        private static readonly HashSet<CompanionCookingBehavior> _activeBehaviors
            = new HashSet<CompanionCookingBehavior>();
        private static readonly object _activeBehaviorsLock = new object();

        // When the first slot in the station hits Done, we schedule a single
        // batched pickup at this time (Time.time + COLLECT_BATCH_DELAY) so any
        // additional slots that finish in the same window are picked up in one
        // pass.  -1 means "no pickup currently pending".
        private float _pendingCollectTime = -1f;
        private const float COLLECT_BATCH_DELAY = 1.5f;

        private void OnStationFinished(CookingStation station)
        {
            if (station == null) return;
            if (_currentStation != station) return;
            if (CurrentPhase != CookPhase.Tending && CurrentPhase != CookPhase.LoadingFood)
                return;

            // First "ding" of the batch ï¿½ schedule a delayed collect.  Don't
            // overwrite an already-scheduled time, otherwise a cluster of slots
            // finishing back-to-back would keep pushing the deadline forward
            // forever and food would burn.
            if (_pendingCollectTime < 0f)
            {
                _pendingCollectTime = Time.time + COLLECT_BATCH_DELAY;
                _idleTimer = 0f;
            }
        }

        /// <summary>
        /// Harmony patch: postfix on <c>CookingStation.UpdateCooking</c>.  Detects
        /// any slot that has just transitioned to Done (status 2) and pings every
        /// active <see cref="CompanionCookingBehavior"/> tending that station so
        /// it pulls the food off the fire before it can burn.
        /// </summary>
        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        private static class CookingStation_UpdateCooking_Notify
        {
            // Per-station previous status snapshot, keyed by ZNetView instance ID.
            private static readonly Dictionary<int, int[]> _prevStatuses = new Dictionary<int, int[]>();

            [HarmonyPostfix]
            private static void Postfix(CookingStation __instance)
            {
                if (__instance == null || __instance.m_slots == null) return;
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                if (!nview.IsOwner()) return; // only the owner ticks status; only fire from there

                int slotCount = __instance.m_slots.Length;
                int key = nview.GetInstanceID();
                int[] prev;
                if (!_prevStatuses.TryGetValue(key, out prev) || prev.Length != slotCount)
                {
                    prev = new int[slotCount];
                    _prevStatuses[key] = prev;
                }

                bool anyNewlyDone = false;
                var zdo = nview.GetZDO();
                for (int i = 0; i < slotCount; i++)
                {
                    int status = zdo.GetInt("slotstatus" + i, STATUS_EMPTY);
                    if (status == STATUS_DONE && prev[i] != STATUS_DONE)
                    {
                        anyNewlyDone = true;
                    }
                    prev[i] = status;
                }

                if (!anyNewlyDone) return;

                // Wake any companion tending this station.  Copy the set under lock
                // because OnStationFinished could indirectly mutate it.
                CompanionCookingBehavior[] snapshot;
                lock (_activeBehaviorsLock)
                {
                    snapshot = _activeBehaviors.ToArray();
                }
                foreach (var beh in snapshot)
                {
                    try { beh?.OnStationFinished(__instance); } catch { }
                }
            }
        }

        protected override bool UpdatePhase(CookPhase phase)
        {
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());

            return phase switch
            {
                CookPhase.Scanning        => UpdateScanning(),
                CookPhase.MovingToChest   => UpdateMovingToChest(),
                CookPhase.PullingFood     => UpdatePullingFood(),
                CookPhase.MovingToStation => UpdateMovingToStation(),
                CookPhase.LoadingFood     => UpdateLoadingFood(),
                CookPhase.Tending         => UpdateTending(),
                CookPhase.MovingToDeposit => UpdateMovingToDeposit(),
                CookPhase.Depositing      => UpdateDepositing(),
                CookPhase.Complete        => CompleteAndNotify(),
                _                         => true
            };
        }

        public override void Cancel()
        {
            UnregisterActiveBehavior();
            CompanionChatHelper.ClearWorkingStatus(Companion);
            ReleaseStation();
            base.Cancel();
        }

        // ?? phase handlers ????????????????????????????????????????????????????

        private bool UpdateScanning()
        {
            // Refresh station list in case of world changes.
            _stations = FindCookingStations();

            if (_stations.Count == 0)
            {
                LogVerbose("Scan: no stations found");
                SetPhase(CookPhase.Complete);
                return false;
            }

            // Priority 1: collect finished or burned items from any station.
            foreach (var s in _stations)
            {
                if (_failedStations.Contains(s)) continue;
                if (!IsReachable(s.transform.position)) { _failedStations.Add(s); continue; }
                if (HasDoneItems(s) || HasBurnedItems(s))
                {
                    // EARLY RESERVATION so other companions don't pick the same station.
                    if (!InteractableOccupancyManager.TryOccupy(s.gameObject, Character, MAX_COOK_TIME))
                    {
                        _failedStations.Add(s);
                        continue;
                    }
                    _currentStation = s;
                    LogVerbose($"Scan: found station with finished food ï¿½ going to collect");
                    SetPhase(CookPhase.MovingToStation);
                    MoveToPosition(InteractionPointHelper.GetInteractionPoint(
                        s.gameObject, Transform.position, INTERACTION_DIST));
                    return false;
                }
            }

            // Priority 2: find a station with free slots and raw food to load.
            foreach (var s in _stations)
            {
                if (_failedStations.Contains(s)) continue;
                if (!IsReachable(s.transform.position)) { _failedStations.Add(s); continue; }
                if (!HasFreeSlot(s)) continue;

                if (HasRawFoodForStation(s, GetStorageInventory()))
                {
                    // EARLY RESERVATION
                    if (!InteractableOccupancyManager.TryOccupy(s.gameObject, Character, MAX_COOK_TIME))
                    {
                        _failedStations.Add(s);
                        continue;
                    }
                    // Raw food already in inventory ï¿½ go straight to station.
                    _currentStation = s;
                    LogVerbose($"Scan: have raw food in inventory for {s.name}");
                    SetPhase(CookPhase.MovingToStation);
                    MoveToPosition(InteractionPointHelper.GetInteractionPoint(
                        s.gameObject, Transform.position, INTERACTION_DIST));
                    return false;
                }

                // Check chests.
                Resources.RefreshNearbyChests(true);
                _rawFoodChest = FindChestWithRawFood(s);
                if (_rawFoodChest != null)
                {
                    // EARLY RESERVATION
                    if (!InteractableOccupancyManager.TryOccupy(s.gameObject, Character, MAX_COOK_TIME))
                    {
                        _failedStations.Add(s);
                        _rawFoodChest = null;
                        continue;
                    }
                    _currentStation = s;
                    LogVerbose($"Scan: found raw food in chest for {s.name}");
                    SetPhase(CookPhase.MovingToChest);
                    MoveToPosition(InteractionPointHelper.GetContainerInteractionPoint(
                        _rawFoodChest, Transform.position, INTERACTION_DIST));
                    return false;
                }
            }

            LogVerbose("Scan: nothing to do");
            SetPhase(CookPhase.Complete);
            return false;
        }

        private bool UpdateMovingToChest()
        {
            if (_rawFoodChest == null || _currentStation == null)
            {
                SetPhase(CookPhase.Scanning);
                return false;
            }

            if (DistanceTo(_rawFoodChest.transform.position) <= INTERACTION_DIST || ContinueMovement())
            {
                StopMovement();
                FaceTarget(_rawFoodChest.transform.position);
                SetPhase(CookPhase.PullingFood);
            }
            return false;
        }

        private bool UpdatePullingFood()
        {
            if (_rawFoodChest == null || _currentStation == null)
            {
                SetPhase(CookPhase.Scanning);
                return false;
            }

            StopMovement();
            FaceTarget(_rawFoodChest.transform.position);

            // Short pause for animation feel.
            if (TimeInCurrentPhase < 0.6f)
            {
                if (TimeInCurrentPhase < 0.05f) PlayInteractAnimation();
                return false;
            }

            int pulled = PullRawFoodFromChest(_rawFoodChest, _currentStation);
            _rawFoodChest = null;

            if (pulled > 0)
            {
                LogVerbose($"Pulled {pulled} raw food items from chest");
                SetPhase(CookPhase.MovingToStation);
                MoveToPosition(InteractionPointHelper.GetInteractionPoint(
                    _currentStation.gameObject, Transform.position, INTERACTION_DIST));
            }
            else
            {
                LogVerbose("Chest had no usable raw food ï¿½ re-scanning");
                SetPhase(CookPhase.Scanning);
            }
            return false;
        }

        private bool UpdateMovingToStation()
        {
            if (_currentStation == null)
            {
                SetPhase(CookPhase.Scanning);
                return false;
            }

            if (DistanceTo(_currentStation.transform.position) <= INTERACTION_DIST || ContinueMovement())
            {
                StopMovement();
                FaceTarget(_currentStation.transform.position);

                if (!InteractableOccupancyManager.TryOccupy(_currentStation.gameObject, Character, MAX_COOK_TIME))
                {
                    LogVerbose("Station occupied by another companion");
                    _currentStation = null;
                    SetPhase(CookPhase.Scanning);
                    return false;
                }

                // If there is already finished/burned food, go collect first.
                if (HasDoneItems(_currentStation) || HasBurnedItems(_currentStation))
                {
                    SetPhase(CookPhase.Tending);
                    _tendTimer = 0f;
                    _idleTimer = 0f;
                    return false;
                }

                SetPhase(CookPhase.LoadingFood);
            }
            return false;
        }

        private bool UpdateLoadingFood()
        {
            if (_currentStation == null)
            {
                SetPhase(CookPhase.Complete);
                return false;
            }

            StopMovement();
            FaceTarget(_currentStation.transform.position);

            var storage = GetStorageInventory();
            if (storage == null)
            {
                SetPhase(CookPhase.Tending);
                return false;
            }

            bool addedAny = false;
            // Fill every free slot.
            while (HasFreeSlot(_currentStation))
            {
                ItemDrop.ItemData raw = FindRawFoodForStation(_currentStation, storage);
                if (raw == null) break;

                if (_currentStation.UseItem(Humanoid, raw))
                {
                    storage.RemoveOneItem(raw);
                    SaveInventory();
                    addedAny = true;
                    LogVerbose($"Loaded {raw.m_shared.m_name} onto station");
                }
                else break;
            }

            if (addedAny) PlayInteractAnimation();

            // Transition to tending regardless ï¿½ even if we loaded nothing,
            // there may already be items cooking from before.
            SetPhase(CookPhase.Tending);
            _tendTimer = 0f;
            _idleTimer = 0f;
            return false;
        }

        private bool UpdateTending()
        {
            if (_currentStation == null)
            {
                TryTransitionToDeposit();
                return false;
            }

            StopMovement();
            FaceTarget(_currentStation.transform.position);

            // EVENT-DRIVEN BATCH PICKUP
            // The CookingStation patch (see _pendingCollectTime / OnStationFinished)
            // schedules a single collection 1.5s after the FIRST slot in this
            // station hits Done.  That window lets any other slots that finish in
            // the same burst be collected in one batched pass instead of running
            // the collect?refill cycle slot-by-slot (which previously caused the
            // companion to grab slot 0 and miss slots 1+).
            //
            // When the deadline arrives we jump straight into the pickup loop,
            // bypassing the normal poll interval.
            if (_pendingCollectTime > 0f && Time.time >= _pendingCollectTime)
            {
                _pendingCollectTime = -1f;
                _tendTimer = 0f;
                LogVerbose("Batched collect deadline reached ï¿½ collecting now");
            }
            else
            {
                // Use a tight interval whenever something is finished ï¿½ done food has a
                // limited grace period before it burns, so we want to grab it ASAP.
                // When nothing is done yet, fall back to the normal interval to save work.
                float interval = (HasDoneItems(_currentStation) || HasBurnedItems(_currentStation))
                    ? TEND_CHECK_INTERVAL_URGENT
                    : TEND_CHECK_INTERVAL;

                _tendTimer += Time.deltaTime;
                if (_tendTimer < interval)
                    return false;
                _tendTimer = 0f;
            }

            var nview = _currentStation.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                TryTransitionToDeposit();
                return false;
            }

            // CRITICAL: CookingStation.Interact() returns false and internally calls
            // ClaimOwnership() the first time this machine doesn't own the station ZDO.
            // Without pre-claiming here, the interact loop always breaks on the first
            // call (pickups==0), then the idle-guard below immediately fires because
            // anyCooking=false (food is DONE=2, not COOKING=1) and haveMore=false
            // (no raw food left), causing the companion to walk away and leave the
            // finished food to burn.  Yield one tick after claiming so ownership
            // propagates before we try to interact.
            if (!nview.IsOwner())
            {
                nview.ClaimOwnership();
                LogVerbose("Claimed station ZDO ownership ï¿½ yielding one tick before collecting");
                return false;
            }

            bool actedThisTick = false;

            // ?? skill-driven batch collection ????????????????????????????????
            // Higher cooking skill = more reliable collection.  At low skill the
            // companion has a chance to "fumble" any given Done item ï¿½ we simply
            // skip it on this pass and the next Update will see it has tipped
            // into Burnt status (still collected, but yields wood instead of
            // food).  Burned slots are always cleared so we free the slot.
            int doneCount   = CountSlotsWithStatus(_currentStation, STATUS_DONE);
            int burnedCount = CountSlotsWithStatus(_currentStation, STATUS_BURNED);

            float skill = GetCookingSkill();
            float fumblePerItem = ComputeCookingFumbleChance(skill);

            int doneToCollect = 0;
            for (int i = 0; i < doneCount; i++)
            {
                if (UnityEngine.Random.value >= fumblePerItem)
                    doneToCollect++;
            }

            int targetPickups = doneToCollect + burnedCount;
            const int MAX_PICKUPS_PER_TICK = 16;
            if (targetPickups > MAX_PICKUPS_PER_TICK) targetPickups = MAX_PICKUPS_PER_TICK;

            int pickups = 0;
            while (pickups < targetPickups && (HasDoneItems(_currentStation) || HasBurnedItems(_currentStation)))
            {
                if (!_currentStation.Interact(Humanoid, false, false))
                    break; // inventory full or station refused ï¿½ stop trying

                _itemsCooked++;
                pickups++;
                actedThisTick = true;
            }
            if (pickups > 0)
            {
                PlayInteractAnimation();
                LogVerbose($"Collected {pickups} of {doneCount + burnedCount} (skill={skill:F0}, fumble={fumblePerItem:P0})");

                // Award skill XP only for items that came off as Done (not burned
                // and not fumbled).  doneToCollect is the perfect-skill count.
                if (doneToCollect > 0)
                    RaiseCookingSkill(doneToCollect * COOKING_SKILL_XP_PER_ITEM);
            }

            // ?? refill vacated slots ?????????????????????????????????????????
            var storage = GetStorageInventory();
            if (storage != null)
            {
                while (HasFreeSlot(_currentStation))
                {
                    ItemDrop.ItemData raw = FindRawFoodForStation(_currentStation, storage);
                    if (raw == null) break;

                    if (_currentStation.UseItem(Humanoid, raw))
                    {
                        storage.RemoveOneItem(raw);
                        SaveInventory();
                        actedThisTick = true;
                        LogVerbose($"Refilled slot with {raw.m_shared.m_name}");
                    }
                    else break;
                }
            }

            // ?? idle-give-up guard ????????????????????????????????????????????
            // CRITICAL: only count time toward the give-up limit when there is
            // genuinely nothing to do ï¿½ no slots cooking, no done/burned food to
            // collect, and no raw food left to refill with.  Previously the timer
            // accumulated on EVERY non-acting tick, including the normal wait while
            // food is cooking, so companions routinely hit IDLE_GIVE_UP_TIME (~40s)
            // mid-cook and walked away, leaving everything to burn.
            bool anyCooking       = AnySlotCooking(_currentStation);
            bool haveMore         = storage != null && HasRawFoodForStation(_currentStation, storage);
            bool stillNeedCollect = HasDoneItems(_currentStation) || HasBurnedItems(_currentStation);

            if (actedThisTick || anyCooking || stillNeedCollect)
            {
                // Fire is live or we just did something ï¿½ reset idle watchdog.
                _idleTimer = 0f;
            }
            else if (!haveMore)
            {
                // Nothing cooking, nothing done, nothing to load ? truly idle.
                _idleTimer += TEND_CHECK_INTERVAL;
                if (_idleTimer >= IDLE_GIVE_UP_TIME)
                {
                    LogVerbose($"Idle for {_idleTimer:F0}s with nothing on the fire ï¿½ finishing");
                    TryTransitionToDeposit();
                    return false;
                }
            }
            // else: raw food is available but slots are all full / station busy ï¿½ keep waiting.

            // Clean-finish: everything collected and nothing more to cook.
            if (!anyCooking && !stillNeedCollect && !haveMore)
            {
                LogVerbose("All food collected and nothing left to cook ï¿½ depositing");
                TryTransitionToDeposit();
                return false;
            }

            return false;
        }

        private bool UpdateMovingToDeposit()
        {
            if (_toDeposit.Count == 0 || _depositChests.Count == 0)
            {
                SetPhase(CookPhase.Complete);
                return false;
            }

            var chest = _depositChests[_depositIndex];
            if (chest == null)
            {
                AdvanceToNextChest();
                return false;
            }

            if (DistanceTo(chest.transform.position) <= INTERACTION_DIST || ContinueMovement())
            {
                StopMovement();
                FaceTarget(chest.transform.position);
                SetPhase(CookPhase.Depositing);
            }
            return false;
        }

        private bool UpdateDepositing()
        {
            if (_depositIndex >= _depositChests.Count || _toDeposit.Count == 0)
            {
                SetPhase(CookPhase.Complete);
                return false;
            }

            var chest = _depositChests[_depositIndex];
            if (chest == null) { AdvanceToNextChest(); return false; }

            StopMovement();
            FaceTarget(chest.transform.position);

            if (TimeInCurrentPhase < 0.1f) PlayInteractAnimation();
            if (TimeInCurrentPhase < 0.5f) return false;

            var chestInv = chest.GetInventory();
            var storage  = GetStorageInventory();
            if (chestInv == null || storage == null) { AdvanceToNextChest(); return false; }

            // Deposit as many cooked items as fit.
            bool depositedAny = false;
            var remaining = new List<ItemDrop.ItemData>(_toDeposit);
            foreach (var item in remaining)
            {
                if (item == null) continue;
                if (!IsCookedFood(item)) continue;
                if (!chestInv.CanAddItem(item)) continue;

                var clone = item.Clone();
                if (chestInv.AddItem(clone))
                {
                    storage.RemoveItem(item);
                    _toDeposit.Remove(item);
                    _itemsDeposited++;
                    depositedAny = true;
                    LogVerbose($"Deposited {item.m_shared.m_name} to chest");
                }
            }

            if (depositedAny)
            {
                SaveInventory();
                SmartStorageOrganizer.SaveContainer(chest);
            }

            if (_toDeposit.Count == 0)
                SetPhase(CookPhase.Complete);
            else
                AdvanceToNextChest();   // this chest is full ï¿½ try the next one

            return false;
        }

        private bool CompleteAndNotify()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            ReleaseStation();

            if (_itemsCooked > 0)
            {
                string msg = _itemsDeposited > 0
                    ? $"cooked and stored {_itemsCooked} items"
                    : $"cooked {_itemsCooked} items";
                CompanionChatHelper.NotifyTaskComplete(Companion, msg);
            }

            Complete();
            return true;
        }

        // ?? helpers ï¿½ deposit ????????????????????????????????????????????????

        private void TryTransitionToDeposit()
        {
            ReleaseStation();

            // Collect cooked food currently in inventory.
            var storage = GetStorageInventory();
            _toDeposit.Clear();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                    if (item != null && IsCookedFood(item))
                        _toDeposit.Add(item);
            }

            if (_toDeposit.Count == 0 || !Resources.HasNearbyChests)
            {
                SetPhase(CookPhase.Complete);
                return;
            }

            // Build deposit chest list ordered by SmartStorageOrganizer score.
            _depositChests = BuildDepositChestList();
            _depositIndex  = 0;

            if (_depositChests.Count == 0)
            {
                SetPhase(CookPhase.Complete);
                return;
            }

            SetPhase(CookPhase.MovingToDeposit);
            MoveToPosition(InteractionPointHelper.GetContainerInteractionPoint(
                _depositChests[0], Transform.position, INTERACTION_DIST));
        }

        private List<Container> BuildDepositChestList()
        {
            var result = new List<Container>();
            if (!Resources.HasNearbyChests) return result;

            var center = _currentStation?.transform.position ?? Transform.position;

            // Order chests by SmartStorageOrganizer score for cooked food.
            var scored = new List<(Container chest, float score)>();
            foreach (var chest in Resources.NearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null || inv.GetEmptySlots() == 0) continue;

                // Prefer chests that already contain cooked food.
                int cookedCount = inv.GetAllItems().Count(i => i != null && IsCookedFood(i));
                float score = cookedCount * 200f;
                score += Mathf.Max(0f, 50f - Vector3.Distance(chest.transform.position, center));
                scored.Add((chest, score));
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));
            foreach (var (chest, _) in scored)
                result.Add(chest);
            return result;
        }

        private void AdvanceToNextChest()
        {
            _depositIndex++;
            if (_depositIndex >= _depositChests.Count)
            {
                SetPhase(CookPhase.Complete);
                return;
            }

            SetPhase(CookPhase.MovingToDeposit);
            var nextChest = _depositChests[_depositIndex];
            if (nextChest != null)
                MoveToPosition(InteractionPointHelper.GetContainerInteractionPoint(
                    nextChest, Transform.position, INTERACTION_DIST));
        }

        // ?? helpers ï¿½ stations ???????????????????????????????????????????????

        private List<CookingStation> FindCookingStations()
        {
            float radius  = GetEffectiveSearchRadius(STATION_SCAN_RADIUS);
            var result    = new List<CookingStation>();
            var seen      = new HashSet<CookingStation>();
            var colliders = Physics.OverlapSphere(SearchCenter, radius);

            foreach (var col in colliders)
            {
                if (col == null) continue;
                var station = col.GetComponent<CookingStation>()
                           ?? col.GetComponentInParent<CookingStation>();
                if (station == null || seen.Contains(station)) continue;
                if (!InteractableOccupancyManager.CanUseInteractable(station.gameObject, Character)) continue;
                seen.Add(station);
                result.Add(station);
            }

            // Nearest first.
            result.Sort((a, b) =>
                DistanceTo(a.transform.position).CompareTo(DistanceTo(b.transform.position)));
            return result;
        }

        private void ReleaseStation()
        {
            if (_currentStation != null)
            {
                InteractableOccupancyManager.Release(_currentStation.gameObject, Character);
                _currentStation = null;
            }
        }

        // ?? helpers ï¿½ slot state ?????????????????????????????????????????????

        private bool HasFreeSlot(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            for (int i = 0; i < station.m_slots.Length; i++)
            {
                int status = nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY);
                if (status == STATUS_EMPTY) return true;
            }
            return false;
        }

        private bool HasDoneItems(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            for (int i = 0; i < station.m_slots.Length; i++)
                if (nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY) == STATUS_DONE)
                    return true;
            return false;
        }

        private bool HasBurnedItems(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            for (int i = 0; i < station.m_slots.Length; i++)
                if (nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY) == STATUS_BURNED)
                    return true;
            return false;
        }

        private bool AnySlotCooking(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            for (int i = 0; i < station.m_slots.Length; i++)
            {
                int status = nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY);
                if (status == STATUS_COOKING || status == STATUS_DONE)
                    return true;
            }
            return false;
        }

        // ?? helpers ï¿½ raw food ???????????????????????????????????????????????

        private bool HasRawFoodInInventory()
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
                if (item != null && IsRawCookable(item))
                    return true;
            return false;
        }

        private bool HasRawFoodForAnyStation(List<CookingStation> stations)
        {
            var storage = GetStorageInventory();
            foreach (var s in stations)
            {
                if (storage != null && HasRawFoodForStation(s, storage))
                    return true;
                // Also check chests.
                if (FindChestWithRawFood(s) != null)
                    return true;
            }
            return false;
        }

        /// <summary>Returns true if <paramref name="inv"/> contains at least one item that
        /// <paramref name="station"/> can cook.</summary>
        private bool HasRawFoodForStation(CookingStation station, Inventory inv)
        {
            if (station == null || inv == null) return false;
            foreach (var item in inv.GetAllItems())
                if (item != null && IsRawFor(station, item))
                    return true;
            return false;
        }

        /// <summary>Finds the first raw-food item in <paramref name="inv"/> that fits the station.</summary>
        private ItemDrop.ItemData FindRawFoodForStation(CookingStation station, Inventory inv)
        {
            if (station == null || inv == null) return null;
            foreach (var item in inv.GetAllItems())
                if (item != null && IsRawFor(station, item))
                    return item;
            return null;
        }

        /// <summary>Is this item raw input for any cooking conversion the station supports?</summary>
        private bool IsRawFor(CookingStation station, ItemDrop.ItemData item)
        {
            if (station?.m_conversion == null || item == null) return false;
            string prefab = item.m_dropPrefab?.name ?? "";
            foreach (var conv in station.m_conversion)
                if (conv.m_from != null && conv.m_from.gameObject.name == prefab)
                    return true;
            return false;
        }

        /// <summary>Is this item a "cookable" raw item for any known station type?
        /// Used for InventoryPriority without needing a specific station reference.</summary>
        private bool IsRawCookable(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            // Anything the SmartStorageOrganizer classifies as raw meat or fish.
            var cat = SmartStorageOrganizer.GetItemCategory(item);
            return cat == SmartStorageOrganizer.ItemCategory.RawMeat
                || cat == SmartStorageOrganizer.ItemCategory.RawFish;
        }

        /// <summary>Is this item a cooked food result (edible, not raw)?</summary>
        private bool IsCookedFood(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (item.m_shared.m_food <= 0f) return false;           // must restore health/stamina
            if (item.m_equipped) return false;
            var cat = SmartStorageOrganizer.GetItemCategory(item);
            return cat == SmartStorageOrganizer.ItemCategory.CookedFood;
        }

        /// <summary>Finds the nearest chest (within ChestSearchRadius from config) that
        /// contains raw food <paramref name="station"/> can cook.</summary>
        private Container FindChestWithRawFood(CookingStation station)
        {
            if (station == null || !Resources.HasNearbyChests) return null;

            Container best     = null;
            float     bestDist = float.MaxValue;

            foreach (var chest in Resources.NearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                if (!HasRawFoodForStation(station, inv)) continue;

                float dist = DistanceTo(chest.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best     = chest;
                }
            }
            return best;
        }

        /// <summary>Transfers raw food from <paramref name="chest"/> to the companion inventory.
        /// Pulls enough to fill all free slots on <paramref name="station"/>.</summary>
        private int PullRawFoodFromChest(Container chest, CookingStation station)
        {
            if (chest == null || station == null) return 0;

            var chestInv  = chest.GetInventory();
            var storage   = GetStorageInventory();
            if (chestInv == null || storage == null) return 0;

            int freeSlots = CountFreeSlots(station);
            if (freeSlots <= 0) return 0;

            int pulled = 0;
            var items  = new List<ItemDrop.ItemData>(chestInv.GetAllItems());

            foreach (var item in items)
            {
                if (pulled >= freeSlots) break;
                if (item == null || !IsRawFor(station, item)) continue;

                // Pull one at a time.
                int toPull = Mathf.Min(item.m_stack, freeSlots - pulled);
                for (int n = 0; n < toPull; n++)
                {
                    var clone = item.Clone();
                    clone.m_stack = 1;
                    if (!storage.AddItem(clone)) break;
                    chestInv.RemoveOneItem(item);
                    pulled++;
                }
            }

            if (pulled > 0)
            {
                SaveInventory();
                SmartStorageOrganizer.SaveContainer(chest);
            }

            return pulled;
        }

        private int CountFreeSlots(CookingStation station)
        {
            if (station == null) return 0;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            int free = 0;
            for (int i = 0; i < station.m_slots.Length; i++)
                if (nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY) == STATUS_EMPTY)
                    free++;
            return free;
        }

        // ?? slot status counter ?????????????????????????????????????????????

        private int CountSlotsWithStatus(CookingStation station, int targetStatus)
        {
            if (station == null) return 0;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            int count = 0;
            for (int i = 0; i < station.m_slots.Length; i++)
                if (nview.GetZDO().GetInt("slotstatus" + i, STATUS_EMPTY) == targetStatus)
                    count++;
            return count;
        }

        // ?? cooking skill (custom, stored on companion ZDO) ?????????????????
        // Valheim doesn't have a vanilla "Cooking" SkillType so we keep our own
        // 0ï¿½100 value on the companion's ZDO.  Higher skill = lower per-item
        // fumble chance during the batched collection pass; at 80+ the companion
        // never burns food.

        private const string COOKING_SKILL_KEY = "companion_cooking_skill";
        private const float COOKING_SKILL_MAX = 100f;
        private const float COOKING_SKILL_XP_PER_ITEM = 0.5f; // ~200 successful cooks ? mastery

        private float GetCookingSkill()
        {
            if (Companion == null) return 0f;
            var nview = Companion.GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo?.GetFloat(COOKING_SKILL_KEY, 0f) ?? 0f;
        }

        private void RaiseCookingSkill(float amount)
        {
            if (Companion == null || amount <= 0f) return;
            var nview = Companion.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (!nview.IsOwner()) nview.ClaimOwnership();
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            float current = zdo.GetFloat(COOKING_SKILL_KEY, 0f);
            float next = Mathf.Clamp(current + amount, 0f, COOKING_SKILL_MAX);
            if (next != current)
            {
                zdo.Set(COOKING_SKILL_KEY, next);
                LogVerbose($"Cooking skill: {current:F1} ? {next:F1}");
            }
        }

        /// <summary>
        /// Per-item fumble chance based on cooking skill.  Tuned so:
        /// <list type="bullet">
        /// <item>Skill 0   ? 25% chance to leave any given Done item on the fire (it will burn next tick)</item>
        /// <item>Skill 25  ? ~15%</item>
        /// <item>Skill 50  ? ~7%</item>
        /// <item>Skill 75  ? ~2%</item>
        /// <item>Skill 80+ ? 0% (perfect, the behavior we always had)</item>
        /// </list>
        /// </summary>
        private static float ComputeCookingFumbleChance(float skill)
        {
            if (skill >= 80f) return 0f;
            // Smooth quadratic falloff from 0.25 at skill=0 to 0 at skill=80.
            float t = Mathf.Clamp01(skill / 80f);
            float ease = 1f - (t * t);
            return 0.25f * ease;
        }
    }
}
