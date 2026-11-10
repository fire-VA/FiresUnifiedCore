using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Core;

namespace FiresCore.Npc
{
    // Sub-behavior system: Initialize, Update, Start, Cancel sub-behaviors
    public partial class CompanionIdleBehavior : MonoBehaviour
    {
        #region Sub-Behavior Fields
        
        /// <summary>
        /// Gets the BehaviorCoordinator for this companion.
        /// </summary>
        private BehaviorCoordinator _behaviorCoordinator;
        private BehaviorCoordinator BehaviorCoordinator
        {
            get
            {
                if (_behaviorCoordinator == null)
                    _behaviorCoordinator = GetComponent<BehaviorCoordinator>();
                return _behaviorCoordinator;
            }
        }

        // Where the owner stood when the current chore began, and the chore it belongs to, so a new chore
        // re-anchors. CompanionLeash.ShouldBreakForFollow measures the owner's displacement from it.
        private IdleSubBehavior _ownerAnchorFor;
        private Vector3 _ownerAnchor;
        private float _lastFollowBreakCheck;

        #endregion
        
        #region Sub-Behavior System

        private void InitializeSubBehaviors()
        {
            // Patrol is the one sub-behavior that ALSO applies to static placed NPCs (no
            // CompanionController): it's force-started by an explicit route assignment and resolves its
            // dependencies off the NPC's own GameObject, so create it FIRST — before the static-NPC early
            // return below. (Patrol is independent of AllowIdleWander, which only governs homesteading chores.)
            var patrol = new PatrolBehavior();
            patrol.Initialize(_companion, this);
            _subBehaviors.Add(patrol);

            // The remaining sub-behaviors are homesteading chores that require a CompanionController for their
            // GetComponent calls, so static NPCs (which otherwise only do basic wandering / emotes / sitting,
            // handled directly by CompanionIdleBehavior) skip them.
            if (_companion == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] Static NPC: only Patrol sub-behavior initialized (no CompanionController)");
                return;
            }

            // Create and initialize all sub-behaviors
            // NOTE: V2 behaviors use WorkBehaviorBase with standardized phase management

            var bowTraining = new BowTrainingBehavior();
            bowTraining.Initialize(_companion, this);
            _subBehaviors.Add(bowTraining);

            // Workstation interaction (crafting benches) - V2 version
            var workstationInteraction = new WorkstationInteractionBehaviorV2();
            workstationInteraction.Initialize(_companion, this);
            _subBehaviors.Add(workstationInteraction);

            // Loot pickup (collecting enemy drops) - V2 version
            var lootPickup = new LootPickupBehaviorV2();
            lootPickup.Initialize(_companion, this);
            _subBehaviors.Add(lootPickup);

            // Fire tending (adding fuel, cooking) - V2 version
            var fireTending = new FireTendingBehaviorV2();
            fireTending.Initialize(_companion, this);
            _subBehaviors.Add(fireTending);

            // 0.2.873 (Fire 10-27): taming wild tameables near home the vanilla way (feed their own consume items, let Tameable count)
            var taming = new TamingBehavior();
            taming.Initialize(_companion, this);
            _subBehaviors.Add(taming);

            // Crafting upgrades (auto-upgrade equipment) - V2 version
            var craftingUpgrade = new CraftingUpgradeBehaviorV2();
            craftingUpgrade.Initialize(_companion, this);
            _subBehaviors.Add(craftingUpgrade);

            // Wood gathering - dedicated behavior for trees with proper axe handling
            var woodGathering = new WoodGatheringBehavior();
            woodGathering.Initialize(_companion, this);
            _subBehaviors.Add(woodGathering);

            // Resource gathering - for ores, stones, pickable items
            var resourceGathering = new ResourceGatheringBehavior();
            resourceGathering.Initialize(_companion, this);
            _subBehaviors.Add(resourceGathering);

            // Smelter operation - available for idle rotation during Stay mode
            var smelterOperator = new SmelterOperatorBehavior();
            smelterOperator.Initialize(_companion, this);
            _subBehaviors.Add(smelterOperator);

            // Farming - beehive harvesting, crop harvesting, seed planting
            var farming = new FarmingBehavior();
            farming.Initialize(_companion, this);
            _subBehaviors.Add(farming);

            // Chest deposit - V2 version with smart storage
            var chestDeposit = new ChestDepositBehaviorV2();
            chestDeposit.Initialize(_companion, this);
            _subBehaviors.Add(chestDeposit);

            // Cooking - dedicated behavior for cooking stations
            var cooking = new CompanionCookingBehavior();
            cooking.Initialize(_companion, this);
            _subBehaviors.Add(cooking);

            // Building repair - detect and repair damaged player-placed structures
            var buildingRepair = new BuildingRepairBehavior();
            buildingRepair.Initialize(_companion, this);
            _subBehaviors.Add(buildingRepair);

            // Fishing - find water near home, cast rod, catch fish
            var fishing = new FishingBehavior();
            fishing.Initialize(_companion, this);
            _subBehaviors.Add(fishing);

            // Haul - carries out CompanionController.StartHaul; never in the idle rotation
            var haul = new HaulBehavior();
            haul.Initialize(_companion, this);
            _subBehaviors.Add(haul);

            // companions gear up from base chests / craft their own armour
            var gearUp = new GearUpBehavior();
            gearUp.Initialize(_companion, this);
            _subBehaviors.Add(gearUp);

            // [ghost] 0.2.836: companions repair their own worn / broken gear at the right station (ChoreBrain.NextRepair)
            var gearRepair = new GearRepairBehavior();
            gearRepair.Initialize(_companion, this);
            _subBehaviors.Add(gearRepair);

            // hunting / fishing trips: on command, or the companion's saved "every morning" plan
            var trip = new TripBehavior();
            trip.Initialize(_companion, this);
            _subBehaviors.Add(trip);
            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] Initialized {_subBehaviors.Count} sub-behaviors");
        }

        private void UpdateActiveSubBehavior()
        {
            if (_activeSubBehavior == null) return;

            // COMBAT OWNS MOVEMENT. Cancel a stationed NPC's sub-behavior (patrol) the moment it enters combat:
            // this stops it ticking AND releases its movement-authority lease, so it never fights the combat
            // mover (which made the NPC slide instead of attacking). It auto-restarts once combat ends, since
            // TryStartPatrolIfAssigned runs every tick. Gated to stationed NPCs so companion work behaviors,
            // which use the interrupt/resume path, are untouched.
            if (_companionAI != null && _companionAI.IsInCombat
                && _npcModule != null && _npcModule.IsStationedAsNpc)
            {
                CancelActiveSubBehavior("combat (a stationed NPC)");
                return;
            }

            // Check for absolute priority player commands (Move/Attack)
            // These MUST cancel any active sub-behavior immediately
            if (_stateController != null && _stateController.HasAbsolutePriorityCommand)
            {
                // Only cancel if the active command is NOT SubBehavior
                if (_stateController.ActiveCommandType != CompanionStateController.CommandType.SubBehavior)
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cancelling sub-behavior {_activeSubBehavior.BehaviorName} - player command has absolute priority");

                    CancelActiveSubBehavior($"a {_stateController.ActiveCommandType} command");
                    return;
                }
            }

            // A follower drops its chore when its owner walks off. CompanionAI.UpdateAI returns before the FSM for
            // the whole time a sub-behavior is active, so a companion that started work while the owner was AFK
            // never followed, never mirrored their crouch and never ran back. A player-commanded chore is an order
            // and keeps going; a stay companion's work is the point and CompanionLeash leaves it alone.
            if (!_activeSubBehavior.IsCommandInitiated)
            {
                if (_activeSubBehavior != _ownerAnchorFor)
                {
                    _ownerAnchorFor = _activeSubBehavior;
                    _ownerAnchor = _companion?.GetOwner()?.transform.position ?? transform.position;
                }
                else if (Time.time - _lastFollowBreakCheck >= CompanionLeash.BusyBreakCheckInterval)
                {
                    _lastFollowBreakCheck = Time.time;
                    if (CompanionLeash.ShouldBreakForFollow(_companion, _ownerAnchor))
                    {
                        if (VerboseLogging || Config.ConfigManager.Instance?.configCompanionFollowDiag?.Value == true)
                            Debug.Log($"[CompanionFollowDiag] {_companion?.companionName} dropping {_activeSubBehavior.BehaviorName} — owner has moved off");
                        CancelActiveSubBehavior("its owner moved off (following)");
                        return;
                    }
                }
            }

            // A job never runs under a sit/rest/emote pose - stand up first
            if (!ReferenceEquals(_jobPoseCheckedFor, _activeSubBehavior)) { _jobPoseCheckedFor = _activeSubBehavior; EndPoseForJob(_activeSubBehavior.BehaviorName); }   // its first tick only: the job's own emotes (kneel, ...) come later

            // Update the active sub-behavior
            // 0.2.854: whatever this chore puts into a chest is reported as stored by this companion (JobReport.Actor)
            bool isComplete;
            FiresCore.Npc.JobBoard.JobReport.Actor = _companion?.companionName;
            try { isComplete = _activeSubBehavior.Update(); }
            finally { FiresCore.Npc.JobBoard.JobReport.Actor = null; }

            if (isComplete)
            {
                NoteSubBehaviorEnd(_activeSubBehavior, "done");
                // a behaviour that says "done" without its own Complete() keeps its reservations and movement authority
                // (SmelterOperator once did). Say so once per finish, always on, so the next one shows in the log.
                if (_activeSubBehavior.IsActive)
                    Debug.LogWarning($"[CompanionIdleBehavior] {_companion?.companionName} sub-behavior {_activeSubBehavior.BehaviorName} returned done while still active (its Complete() did not run)");

                // Notify coordinator
                BehaviorCoordinator?.NotifyBehaviorCompleted(_activeSubBehavior, true, "Completed");
                
                _activeSubBehavior = null;
                SetIdleState(IdleState.Standing);
            }
        }

        // When the running sub-behaviour started, and one always-on
        // line when it ends, "[CompanionIdleBehavior] <name> ended sub-behavior: <behaviour> after N s (done | cancelled)".
        private float _subStartedAt = -1f;
        private string _lastPickedBehavior;   // 0.2.913: the +4 hysteresis on need-based picks

        private void NoteSubBehaviorStart() => _subStartedAt = Time.time;

        private void NoteSubBehaviorEnd(IdleSubBehavior behavior, string how)
        {
            if (behavior == null) return;
            string took = _subStartedAt >= 0f ? $"{Time.time - _subStartedAt:0} s" : "? s";
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionIdleBehavior] {_companion?.companionName} ended sub-behavior: {behavior.BehaviorName} after {took} ({how})");
            if (how == "done") FiresCore.Npc.JobBoard.JobReport.Record(_companion?.companionName, "chore", behavior.BehaviorName, 1);   // 0.2.854
            _subStartedAt = -1f;
            // 0.2.897 (round #20: the logging run ended with the stump left standing, so the stump hook never planted): a camp run that ends
            // well plants its remaining saplings where it stands, if inside the zone
            if (HasWorkZone && how == "done" && (WorkZoneFocus == "chop" || WorkZoneFocus == "mine")) FiresCore.Npc.IdleBehaviors.ZoneReplant.AtRunEnd(_companion, this);
            if (HasWorkZone && (WorkZoneFocus == "chop" || WorkZoneFocus == "mine")) FiresCore.Npc.IdleBehaviors.ZoneReplant.RunSummary(_companion, this, how);   // 0.2.897
            ClearWorkZone(how);   // Fire 10-27: one zone run per board pick
        }

        private bool TryStartSubBehavior()
        {
            // CRITICAL: Don't start a new behavior if one is already active!
            if (_activeSubBehavior != null)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} TryStartSubBehavior blocked - already running {_activeSubBehavior.BehaviorName}");
                return false;
            }
            
            // Reduce cooldown significantly for staying companions
            bool isStaying = _hasHomePosition || (_companion != null && !_companion.ShouldBeFollowing);
            // [ghost] 0.2.843 (Fire: "all mostly idle"): a staying companion checks for work 4 s after its last check (was 18 s), but
            // after a round that found nothing it waits EmptyRoundBackoff, so the CanStart scans don't run every few seconds for nothing
            float effectiveCooldown = isStaying ? 4f : subBehaviorCooldown;
            if (isStaying && Time.time < _emptyRoundUntil)
                return false;

            // Check cooldown
            if (Time.time - _lastSubBehaviorAttempt < effectiveCooldown)
                return false;

            _lastSubBehaviorAttempt = Time.time;

            // ============================================================
            // IMPORTANT: DO NOT CHANGE THIS RANDOM CHANCE TO 100%!
            // ============================================================
            // The random chance is INTENTIONAL to make companions feel organic
            // and not like robots that work 100% of the time.
            // ============================================================
            // [ghost] 0.2.843: a staying companion at base is there to work (Fire: "run more fluidly"); followers keep the organic chance
            float effectiveChance = isStaying ? 0.95f : subBehaviorChance;
            
            if (UnityEngine.Random.value > effectiveChance)
                return false;
            // ============================================================

            // Build list of behaviors that can start
            var availableBehaviors = new List<IdleSubBehavior>();
            foreach (var behavior in _subBehaviors)
            {
                // Skip behaviors not available for idle rotation (unless staying)
                if (!behavior.AvailableForIdleRotation && !isStaying)
                    continue;
                    
                if (behavior.CanStart())
                {
                    availableBehaviors.Add(behavior);
                }
                else if (VerboseLogging)
                {
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cannot start {behavior.BehaviorName}: CanStart() returned false");
                }
            }
            
            if (availableBehaviors.Count == 0)
            {
                _lastRoundFoundWork = false;
                if (isStaying) _emptyRoundUntil = Time.time + EmptyRoundBackoff;
                if (VerboseLogging && isStaying)
                {
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} tried {_subBehaviors.Count} behaviors, none could start (staying mode)");
                }
                return false;
            }

            // TASK VARIETY BIAS: penalize behaviors that nearby companions are already
            // running. This stops the whole pack from picking the same task at once
            // (e.g., everyone trying to operate the kiln).  We compute an effective
            // priority score = InventoryPriority - (nearby_count * PopularityPenalty),
            // then sort by that.  Same priority is randomly shuffled for variety.
            const float PopularityRadius = 30f;
            const int   PopularityPenalty = 25; // each nearby companion subtracts 25 from priority
            var scored = new List<(IdleSubBehavior beh, int score)>(availableBehaviors.Count);
            var needWhys = new Dictionary<string, string>();   // 0.2.913: the need part of each score, for the pick line
            foreach (var candidate in availableBehaviors)
            {
                int popularity = CountNearbyCompanionsRunningBehavior(candidate.BehaviorName, PopularityRadius);
                int score = candidate.InventoryPriority - popularity * PopularityPenalty;
                // 0.2.913 (Fire: "priority levels should be dynamic based off of what the homestead actually needs"): + weight x need from
                // the homestead needs model ("[Homestead Needs] Need-based chore scores", default off)
                if (isStaying)
                {
                    int need = FiresCore.Npc.JobBoard.HomesteadNeeds.NeedFor(candidate.BehaviorName, out string needWhy);
                    if (need > 0) { score += need; needWhys[candidate.BehaviorName] = $"need +{need}: {needWhy}"; }
                }
                scored.Add((candidate, score));
            }
            scored.Sort((a, b) => b.score.CompareTo(a.score));

            // If top behaviors have same effective score, shuffle them for variety
            int topScore = scored[0].score;
            int samepriorityCount = 0;
            for (int i = 0; i < scored.Count && scored[i].score == topScore; i++)
            {
                samepriorityCount++;
            }

            if (samepriorityCount > 1)
            {
                for (int i = samepriorityCount - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    var temp = scored[i];
                    scored[i] = scored[j];
                    scored[j] = temp;
                }
            }

            // Start the highest effective-score (or randomly selected among same score) behavior
            var selectedBehavior = scored[0].beh;
            // 0.2.913 (plan section 5, anti-thrash): with need scores on, the chore picked last time is kept unless another beats it by 4+
            string kept = null;
            if (FiresCore.Npc.JobBoard.HomesteadNeeds.ScoresOn && _lastPickedBehavior != null && selectedBehavior.BehaviorName != _lastPickedBehavior)
            {
                foreach (var x in scored)
                    if (x.beh.BehaviorName == _lastPickedBehavior && x.score > scored[0].score - 4) { kept = $"kept {x.beh.BehaviorName} ({x.score} vs {scored[0].beh.BehaviorName} {scored[0].score}, under the +4 to switch)"; selectedBehavior = x.beh; break; }
            }
            _lastPickedBehavior = selectedBehavior.BehaviorName;
            // 0.2.864 (lead for Fire 10-27 "still not a lot of mining"): the candidates and their scores (priority - 25 per nearby
            // companion already on it); mining lives inside ResourceGathering, whose own "[Chore] pick" line says wood vs ore and why
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] pick {_companion?.companionName}: candidates {string.Join(", ", System.Linq.Enumerable.Select(scored, x => x.beh.BehaviorName + "=" + x.score))}; chose {selectedBehavior.BehaviorName}{string.Concat(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(scored, x => x.beh.PriorityReason != null), x => " (" + x.beh.PriorityReason + ")"))}{string.Concat(System.Linq.Enumerable.Select(needWhys, kv => " (" + kv.Key + " " + kv.Value + ")"))}{(kept != null ? " (" + kept + ")" : "")}");
            _lastRoundFoundWork = true;
            _isRotating = false;      // sub-behavior owns rotation; stop any look-around that would fight it
            _activeSubBehavior = selectedBehavior;
            _activeSubBehavior.Start();
            NoteSubBehaviorStart();
            SetIdleState(IdleState.SubBehavior);
            
            // Notify coordinator
            BehaviorCoordinator?.NotifyBehaviorStarted(_activeSubBehavior);

            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionIdleBehavior] {_companion?.companionName} started sub-behavior: {selectedBehavior.BehaviorName} (priority={selectedBehavior.InventoryPriority})");

            return true;
        }

        /// <summary>
        /// Counts how many other companions within <paramref name="radius"/> meters of
        /// this companion are currently running a sub-behavior with the given name.
        /// Used by TryStartSubBehavior to spread tasks across the group instead of
        /// having everyone pile onto the same one.
        /// </summary>
        private int CountNearbyCompanionsRunningBehavior(string behaviorName, float radius)
        {
            if (string.IsNullOrEmpty(behaviorName)) return 0;

            int count = 0;
            float radiusSq = radius * radius;
            Vector3 myPos = transform.position;

            foreach (var other in CompanionController.AllCompanions)
            {
                if (other == null) continue;
                if (other == _companion) continue;
                if ((other.transform.position - myPos).sqrMagnitude > radiusSq) continue;

                var idle = other.GetComponent<CompanionIdleBehavior>();
                var active = idle?._activeSubBehavior;
                if (active != null && active.IsActive && active.BehaviorName == behaviorName)
                    count++;
            }
            return count;
        }

        // True if this NPC carries a PatrolAssignment that resolves to a real (>=2 point) saved route.
        private bool HasPatrolRoute()
        {
            var assignment = GetComponent<FiresCore.Npc.Patrol.PatrolAssignment>();
            return assignment != null && assignment.HasRoute;
        }

        // Force-starts PatrolBehavior (as a command, so it owns AI authority) whenever the NPC has a
        // route and isn't already busy. Called every Update tick so patrol resumes after combat/commands.
        private void TryStartPatrolIfAssigned()
        {
            if (_activeSubBehavior != null) return;
            if (!HasPatrolRoute()) return;
            if (_companionAI != null && _companionAI.IsInCombat) return;   // don't resume patrol mid-combat (incl. buffer)
            if (_stateController != null && _stateController.IsPlayerCommandActive) return;
            TryStartSubBehavior<PatrolBehavior>();
        }

        // Every cancel names its reason on the end line.
        /// <summary>Ends whatever sub-behavior runs (keep-alive: the companion is simulated elsewhere now).</summary>
        public void StopActiveSubBehavior(string why) => CancelActiveSubBehavior(why);

        private void CancelActiveSubBehavior(string why = "unnamed")
        {
            if (_activeSubBehavior != null)
            {
                var behavior = _activeSubBehavior;
                _activeSubBehavior.Cancel();
                _activeSubBehavior = null;
                NoteSubBehaviorEnd(behavior, $"cancelled: {why}");
                
                // Notify coordinator
                BehaviorCoordinator?.NotifyBehaviorCompleted(behavior, false, "Cancelled");
            }
        }

        #endregion

        #region Sub-Behavior Public API
        
        /// <summary>
        /// Attempts to start bow training immediately as a player command.
        /// Returns true if training was started successfully.
        /// </summary>
        public bool TryStartBowTraining()
        {
            CancelAllIdleBehaviors();
            
            foreach (var behavior in _subBehaviors)
            {
                if (behavior is BowTrainingBehavior bowTraining)
                {
                    if (bowTraining.CanStart())
                    {
                        _activeSubBehavior = bowTraining;
                        _activeSubBehavior.StartAsCommand();
                        NoteSubBehaviorStart();
                        SetIdleState(IdleState.SubBehavior);
                        
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} started bow training via command");
                        
                        return true;
                    }
                    else
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cannot start bow training - CanStart() returned false");
                        return false;
                    }
                }
            }
            
            Debug.LogWarning($"[CompanionIdleBehavior] No BowTrainingBehavior found for {_companion?.companionName}");
            return false;
        }
        
        /// <summary>
        /// Gets a sub-behavior by type. Used by command system to configure behaviors before starting.
        /// </summary>
        public T GetSubBehavior<T>() where T : IdleSubBehavior
        {
            foreach (var behavior in _subBehaviors)
            {
                if (behavior is T typedBehavior)
                {
                    return typedBehavior;
                }
            }
            return null;
        }
        
        /// <summary>
        /// Starts a specific sub-behavior immediately as a player command.
        /// The behavior will have absolute authority until it completes.
        /// </summary>
        public bool TryStartSubBehavior<T>() where T : IdleSubBehavior
        {
            var behavior = GetSubBehavior<T>();
            if (behavior == null)
            {
                Debug.LogWarning($"[CompanionIdleBehavior] {_companion?.companionName} TryStartSubBehavior<{typeof(T).Name}> FAILED - behavior is null");
                return false;
            }
            
            if (!behavior.CanStart())
            {
                Debug.LogWarning($"[CompanionIdleBehavior] {_companion?.companionName} TryStartSubBehavior<{typeof(T).Name}> FAILED - CanStart() returned false");
                return false;
            }
            
            CancelAllIdleBehaviors();
            
            _activeSubBehavior = behavior;
            _activeSubBehavior.StartAsCommand();
            NoteSubBehaviorStart();
            SetIdleState(IdleState.SubBehavior);
            
            // Notify coordinator
            BehaviorCoordinator?.NotifyBehaviorStarted(_activeSubBehavior);
            
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionIdleBehavior] {_companion?.companionName} STARTED sub-behavior {behavior.BehaviorName} - _activeSubBehavior is now SET, IsInSubBehavior={IsInSubBehavior}");
            
            return true;
        }
        
        /// <summary>
        /// Gets the maximum duration for a sub-behavior type.
        /// </summary>
        public float GetSubBehaviorMaxDuration<T>() where T : IdleSubBehavior
        {
            var behavior = GetSubBehavior<T>();
            return behavior?.MaxDuration ?? 120f;
        }
        
        /// <summary>
        /// Sets a specific look-at target for the companion's head.
        /// Used by sub-behaviors like bow training to control where companion looks.
        /// </summary>
        public void SetLookAtTarget(Transform target, float duration = 5f)
        {
            _lookAtTarget = target;
            _lookAtEndTime = Time.time + duration;
            _targetHeadLookWeight = 1f;
        }
        
        /// <summary>
        /// Clears the current look-at target.
        /// </summary>
        public void ClearLookAtTarget()
        {
            _lookAtTarget = null;
            _targetHeadLookWeight = 0f;
        }

        #endregion
    }
}
