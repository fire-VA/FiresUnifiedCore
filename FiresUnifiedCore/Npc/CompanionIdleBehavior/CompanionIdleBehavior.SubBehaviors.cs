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
                CancelActiveSubBehavior();
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
                    
                    CancelActiveSubBehavior();
                    return;
                }
            }

            // Update the active sub-behavior
            bool isComplete = _activeSubBehavior.Update();

            if (isComplete)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_activeSubBehavior.BehaviorName} completed for {_companion?.companionName}");
                
                // Notify coordinator
                BehaviorCoordinator?.NotifyBehaviorCompleted(_activeSubBehavior, true, "Completed");
                
                _activeSubBehavior = null;
                SetIdleState(IdleState.Standing);
            }
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
            float effectiveCooldown = isStaying ? subBehaviorCooldown * 0.3f : subBehaviorCooldown;
            
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
            float effectiveChance = isStaying ? Mathf.Min(subBehaviorChance * 3f, 0.8f) : subBehaviorChance;
            
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
                if (VerboseLogging && isStaying)
                {
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} tried {_subBehaviors.Count} behaviors, none could start (staying mode)");
                }
                return false;
            }

            // TASK VARIETY BIAS: penalize behaviors that nearby companions are already
            // running. This stops the whole pack from picking the same task at once
            // (e.g., everyone trying to operate the kiln).  We compute an effective
            // priority score = InventoryPriority - (nearby_count * POPULARITY_PENALTY),
            // then sort by that.  Same priority is randomly shuffled for variety.
            const float POPULARITY_RADIUS = 30f;
            const int   POPULARITY_PENALTY = 25; // each nearby companion subtracts 25 from priority
            var scored = new List<(IdleSubBehavior beh, int score)>(availableBehaviors.Count);
            foreach (var b in availableBehaviors)
            {
                int popularity = CountNearbyCompanionsRunningBehavior(b.BehaviorName, POPULARITY_RADIUS);
                int score = b.InventoryPriority - popularity * POPULARITY_PENALTY;
                scored.Add((b, score));
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
            _isRotating = false;      // sub-behavior owns rotation; stop any look-around that would fight it
            _isLookingAround = false;
            _activeSubBehavior = selectedBehavior;
            _activeSubBehavior.Start();
            SetIdleState(IdleState.SubBehavior);
            
            // Notify coordinator
            BehaviorCoordinator?.NotifyBehaviorStarted(_activeSubBehavior);

            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} started sub-behavior: {selectedBehavior.BehaviorName} (priority={selectedBehavior.InventoryPriority})");

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
            var a = GetComponent<FiresCore.Npc.Patrol.PatrolAssignment>();
            return a != null && a.HasRoute;
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

        private void CancelActiveSubBehavior()
        {
            if (_activeSubBehavior != null)
            {
                var behavior = _activeSubBehavior;
                _activeSubBehavior.Cancel();
                _activeSubBehavior = null;
                
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
            SetIdleState(IdleState.SubBehavior);
            
            // Notify coordinator
            BehaviorCoordinator?.NotifyBehaviorStarted(_activeSubBehavior);
            
            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} STARTED sub-behavior {behavior.BehaviorName} - _activeSubBehavior is now SET, IsInSubBehavior={IsInSubBehavior}");
            
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
