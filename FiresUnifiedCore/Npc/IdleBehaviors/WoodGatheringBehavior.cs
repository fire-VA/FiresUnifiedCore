using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc;
using FiresCore.Npc.Core;
using FiresCore.Npc.Animation;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Wood gathering: gets an axe from inventory or a nearby chest, equips it, chops trees with the proper attack
    /// animation and damage, works through the logs and stumps left behind, and collects the drops.
    /// </summary>
    public class WoodGatheringBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "WoodGathering";
        public override bool SupportsResumption => true;
        public override bool AvailableForIdleRotation => true;
        public override float WanderRadiusMultiplier => 3f;
        public override bool RequiresInventorySpace => true;

        #region Constants

        private const float TreeSearchRadius = 20f;
        private const float AttackRange = 2.5f;
        private const float AttackInterval = 1.8f;
        private const float DamageDelay = 0.6f;
        private const float MaxGatherTime = 120f;
        private const float LogCheckWait = 3f;
        private const float LogSearchRadius = 15f;
        private const float ChestInteractionDistance = 1.5f;
        private const int MaxNoColliderHits = 3;
        private const float AoeDamageRadius = 3f;
        // Consecutive hits where the tool tier is too low before we act.
        private const int IneffectiveHitThreshold = 3;

        // Tool crafting: best axe first
        private static readonly string[] CraftableAxes = { "AxeFlint", "AxeStone" };
        private const float WorkbenchSearchRadius = 30f;
        private const float WorkbenchInteractionDistance = 2f;
        private const float CraftDuration = 3f;

        #endregion

        #region Phase Enum

        private enum GatherPhase
        {
            FindingTree,
            MovingToChestForTool,
            RetrievingToolFromChest,
            MovingToWorkbench,        // craft a tool when none found in chests
            CraftingTool,             // crafting animation + material consumption
            MovingToTree,
            ChoppingTree,
            WaitingForLogs,
            ProcessingLogs,
            WaitingForDrops,
            Complete
        }

        #endregion

        #region State

        private GatherPhase _currentPhase = GatherPhase.FindingTree;
        private float _phaseStartTime;
        private float _lastAttackTime;
        private int _resourcesGathered;
        private int _consecutiveNoColliderHits;
        private int _ineffectiveHitCount;   // hits where tool tier < tree requirement

        // Target tracking
        private GameObject _commandedTarget;
        private ResourceDataHelper.ResourceData _targetResource;
        private Vector3 _targetPosition;
        private Vector3 _lastTreePosition;
        private bool _wasTargetingTree;

        // Stump tracking for sapling spawning
        private bool _wasTargetingStump;
        private string _targetStumpName;
        private Vector3 _targetStumpPosition;

        // Tool retrieval state
        private Container _toolChest;
        private ItemDrop.ItemData _toolToPull;

        // Crafting state
        private Recipe _craftRecipe;
        private CraftingStation _craftWorkbench;

        private readonly ResourceDataHelper.AttackChain _swingChain = new ResourceDataHelper.AttackChain();

        // Components
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionCombatMovement _combatMovement;
        private CompanionInventory _inventory;
        private CompanionAutoPickup _autoPickup;
        private Rigidbody _rigidbody;

        public static bool VerboseLogging = false;

        #endregion

        #region Initialization

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);

            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _animator = companion.GetComponentInChildren<Animator>(true);
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _autoPickup = companion.GetComponent<CompanionAutoPickup>();
            _rigidbody = companion.GetComponent<Rigidbody>();

            MaxDuration = MaxGatherTime + 30f;
        }

        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }

        #endregion

        #region CanStart

        public override bool CanStart()
        {
            if (Companion == null) return false;

            bool commanded = _commandedTarget != null;

            // Autonomous wood gathering is gated by the radial-menu toggle (shared with general gathering).
            // A player-COMMANDED chop always runs regardless of the toggle.
            if (!commanded && !CompanionBehaviorToggles.IsGatherEnabled(Companion))
            {
                LogVerbose("CanStart: autonomous gather disabled by radial toggle");
                return false;
            }

            // Inventory-space gate applies to AUTONOMOUS gathering only. When the player explicitly
            // commands a chop, run it even if storage is full — the wood overflows/drops rather than the
            // command being silently refused (which left the companion oscillating in the fallback path).
            if (!commanded && !CanStartBase())
            {
                LogVerbose("CanStart: autonomous gather blocked by CanStartBase (likely inventory full)");
                return false;
            }

            if (commanded)
            {
                // Verify it's actually a tree/log
                var treeBase = _commandedTarget.GetComponent<TreeBase>() ?? _commandedTarget.GetComponentInParent<TreeBase>();
                var treeLog = _commandedTarget.GetComponent<TreeLog>() ?? _commandedTarget.GetComponentInParent<TreeLog>();

                if (treeBase == null && treeLog == null)
                {
                    // Explicit (not verbose) so a misrouted target is visible in the log, not silent.
                    Debug.LogWarning($"[WoodGathering] {Companion?.companionName} CanStart: commanded target '{_commandedTarget.name}' is not a tree or log");
                    return false;
                }

                // Check if we have an axe available (inventory, equipment, or chests)
                if (!HasAxeAvailable())
                {
                    Debug.LogWarning($"[WoodGathering] {Companion?.companionName} cannot gather wood - no axe available");
                    CompanionChatHelper.QuickMessages.CantDoTask(Companion, "I need an axe");
                    return false;
                }

                return true;
            }

            // Autonomous mode - find trees near home
            if (IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing)
            {
                var tree = FindNearbyTree(IdleBehavior.HomePosition, CompanionSettings.GetStayModeWorkSearchRadius(IdleBehavior.HomePosition));
                if (tree != null && HasAxeAvailable())
                {
                    _commandedTarget = tree;
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Lifecycle

        public override void Start()
        {
            base.Start();

            _currentPhase = GatherPhase.FindingTree;
            _phaseStartTime = Time.time;
            _resourcesGathered = 0;
            _consecutiveNoColliderHits = 0;
            _wasTargetingTree = false;
            _wasTargetingStump = false;

            _autoPickup?.StartGatheringSession();

            if (_commandedTarget != null)
            {
                _targetResource = ResourceDataHelper.GetResourceData(_commandedTarget);
                _commandedTarget = null;

                if (_targetResource != null && _targetResource.IsValid)
                {
                    _targetPosition = _targetResource.InteractionPosition;
                    _wasTargetingTree = _targetResource.TreeBase != null || _targetResource.TreeLog != null;
                    if (_wasTargetingTree)
                    {
                        _lastTreePosition = _targetResource.InteractionPosition;
                    }

                    // Check if we need to get axe from chest first
                    if (!HasAxeEquippedOrInInventory())
                    {
                        if (TryFindAxeInNearbyChests())
                        {
                            SetPhase(GatherPhase.MovingToChestForTool);
                            Vector3 chestInteractionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                                _toolChest, Transform.position, ChestInteractionDistance);
                            MoveToPosition(chestInteractionPoint);
                            CompanionChatHelper.ShowWorkingStatus(Companion, "Getting axe from chest...");
                            LogVerbose($"Need to get axe from chest first");
                        }
                        else if (TryPlanAxeCraft(_targetResource.MinToolTier))
                        {
                            BeginAxeCraft();
                            CompanionChatHelper.ShowWorkingStatus(Companion, "Crafting an axe...");
                            LogVerbose($"No axe in chests - will craft {_craftRecipe.m_item.name}");
                        }
                        else
                        {
                            Debug.LogWarning($"[WoodGathering] {Companion?.companionName} cannot gather - no axe available");
                            CompanionChatHelper.QuickMessages.CantDoTask(Companion, "I need an axe");
                            SetPhase(GatherPhase.Complete);
                        }
                    }
                    else
                    {
                        // Have axe - equip it and go to tree
                        EquipBestAxe();
                        SetPhase(GatherPhase.MovingToTree);
                        MoveToPosition(_targetPosition);
                        CompanionChatHelper.QuickMessages.GatheringResources(Companion, "wood");
                    }

                    LogVerbose($"Targeting {_targetResource.Name} at {_targetPosition}");
                }
                else
                {
                    Debug.LogWarning($"[WoodGathering] {Companion?.companionName} - Invalid target resource");
                    SetPhase(GatherPhase.Complete);
                }
            }
            else
            {
                SetPhase(GatherPhase.Complete);
            }

            _combatMovement?.SetCommandPriorityDuration(MaxDuration);
        }

        public override bool Update()
        {
            if (!IsActive) return true;

            if (IsTimedOut())
            {
                Debug.LogWarning($"[WoodGathering] {Companion?.companionName} Phase {_currentPhase} timed out");
                Complete();
                return true;
            }

            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());

            switch (_currentPhase)
            {
                case GatherPhase.FindingTree:
                    Complete();
                    return true;

                case GatherPhase.MovingToChestForTool:
                    return UpdateMovingToChestForTool();

                case GatherPhase.RetrievingToolFromChest:
                    return UpdateRetrievingToolFromChest();

                case GatherPhase.MovingToWorkbench:
                    return UpdateMovingToWorkbench();

                case GatherPhase.CraftingTool:
                    return UpdateCraftingTool();

                case GatherPhase.MovingToTree:
                    return UpdateMovingToTree();

                case GatherPhase.ChoppingTree:
                    return UpdateChoppingTree();

                case GatherPhase.WaitingForLogs:
                    return UpdateWaitingForLogs();

                case GatherPhase.ProcessingLogs:
                    return UpdateProcessingLogs();

                case GatherPhase.WaitingForDrops:
                    return UpdateWaitingForDrops();

                case GatherPhase.Complete:
                    Complete();
                    return true;
            }

            return false;
        }

        public override void Cancel()
        {
            StopCraftingPose();
            CompanionChatHelper.ClearWorkingStatus(Companion);
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();
            NotifyOwner();
            base.Cancel();
        }

        protected override void Complete()
        {
            StopCraftingPose();
            base.Complete();
        }

        protected override void SaveState()
        {
            StopCraftingPose();
        }

        protected override void RestoreState()
        {
            if (_currentPhase == GatherPhase.CraftingTool && _craftWorkbench != null)
                PlayerAnimationCatalog.SetCrafting(_zanim, null, PlayerAnimationCatalog.CraftingFor(_craftWorkbench));
        }

        /// <summary>The station work pose is only held during CraftingTool; any exit from it has to end the pose.</summary>
        private void StopCraftingPose()
        {
            if (_currentPhase == GatherPhase.CraftingTool)
                PlayerAnimationCatalog.SetCrafting(_zanim, null, PlayerAnimationCatalog.NoCrafting);
        }

        public override string GetStatusDescription()
        {
            return _currentPhase switch
            {
                GatherPhase.MovingToChestForTool => "Getting axe from chest",
                GatherPhase.RetrievingToolFromChest => "Retrieving axe",
                GatherPhase.MovingToWorkbench => "Going to workbench",
                GatherPhase.CraftingTool => "Crafting an axe",
                GatherPhase.MovingToTree => $"Walking to tree",
                GatherPhase.ChoppingTree => "Chopping tree",
                GatherPhase.WaitingForLogs => "Waiting for logs",
                GatherPhase.ProcessingLogs => "Processing logs",
                GatherPhase.WaitingForDrops => "Collecting drops",
                _ => "Gathering wood"
            };
        }

        #endregion

        #region Phase Updates

        private bool UpdateMovingToChestForTool()
        {
            if (_toolChest == null || _toolToPull == null)
            {
                Debug.LogWarning($"[WoodGathering] {Companion?.companionName} tool chest or tool no longer valid");
                SetPhase(GatherPhase.Complete);
                return true;
            }

            Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _toolChest, Transform.position, ChestInteractionDistance);
            float dist = Vector3.Distance(Transform.position, interactionPoint);

            if (dist <= InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                StopMovement();
                SetPhase(GatherPhase.RetrievingToolFromChest);
                return false;
            }

            MoveToPosition(interactionPoint);

            if (Time.time - _phaseStartTime > 30f)
            {
                Debug.LogWarning($"[WoodGathering] {Companion?.companionName} timeout walking to tool chest");
                if (TryPullAxeFromChest())
                {
                    EquipBestAxe();
                    SetPhase(GatherPhase.MovingToTree);
                    MoveToPosition(_targetPosition);
                }
                else
                {
                    CompanionChatHelper.QuickMessages.CantDoTask(Companion, "Couldn't get the axe");
                    SetPhase(GatherPhase.Complete);
                }
            }

            return false;
        }

        private bool UpdateRetrievingToolFromChest()
        {
            if (_toolChest == null)
            {
                SetPhase(GatherPhase.Complete);
                return true;
            }

            StopMovement();
            FaceTarget(_toolChest.transform.position);

            // Play interact animation
            if (Time.time - _phaseStartTime < 0.1f)
            {
                PlayInteractAnimation();
            }

            // Wait for animation
            if (Time.time - _phaseStartTime < 0.5f)
            {
                return false;
            }

            // Pull the axe
            bool success = TryPullAxeFromChest();

            if (success)
            {
                EquipBestAxe();
                LogVerbose("Retrieved and equipped axe, heading to tree");
                SetPhase(GatherPhase.MovingToTree);
                MoveToPosition(_targetPosition);
                CompanionChatHelper.QuickMessages.GatheringResources(Companion, "wood");
            }
            else
            {
                Debug.LogWarning($"[WoodGathering] {Companion?.companionName} failed to retrieve axe");
                CompanionChatHelper.QuickMessages.CantDoTask(Companion, "I need an axe");
                SetPhase(GatherPhase.Complete);
            }

            _toolChest = null;
            _toolToPull = null;

            return false;
        }

        private bool UpdateMovingToWorkbench()
        {
            if (_craftWorkbench == null)
            {
                SetPhase(GatherPhase.Complete);
                return true;
            }

            float dist = Vector3.Distance(Transform.position, _craftWorkbench.transform.position);
            if (dist <= WorkbenchInteractionDistance)
            {
                StopMovement();
                FaceTarget(_craftWorkbench.transform.position);
                PlayerAnimationCatalog.SetCrafting(_zanim, null, PlayerAnimationCatalog.CraftingFor(_craftWorkbench));
                SetPhase(GatherPhase.CraftingTool);
                return false;
            }

            MoveToPosition(_craftWorkbench.transform.position);

            if (Time.time - _phaseStartTime > 30f)
            {
                LogVerbose("Timeout walking to workbench");
                SetPhase(GatherPhase.Complete);
            }

            return false;
        }

        private bool UpdateCraftingTool()
        {
            if (_craftWorkbench != null)
                FaceTarget(_craftWorkbench.transform.position);

            if (Time.time - _phaseStartTime < CraftDuration)
                return false;

            StopCraftingPose();

            bool crafted = CraftPlannedAxe();
            _craftRecipe = null;
            _craftWorkbench = null;

            if (crafted)
            {
                EquipBestAxe();
                LogVerbose("Crafted and equipped axe, heading to tree");
                SetPhase(GatherPhase.MovingToTree);
                MoveToPosition(_targetPosition);
                CompanionChatHelper.QuickMessages.GatheringResources(Companion, "wood");
            }
            else
            {
                LogVerbose("Failed to craft axe (missing materials?)");
                SetPhase(GatherPhase.Complete);
            }

            return false;
        }

        private bool UpdateMovingToTree()
        {
            if (_targetResource == null || !_targetResource.IsValid || _targetResource.GameObject == null)
            {
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }

            _targetPosition = _targetResource.InteractionPosition;
            float dist = Vector3.Distance(Transform.position, _targetPosition);

            if (dist < AttackRange)
            {
                StopMovement();
                SetPhase(GatherPhase.ChoppingTree);
                return false;
            }

            MoveToPosition(_targetPosition);

            float maxMoveTime = 20f + (dist / 3f);
            if (Time.time - _phaseStartTime > maxMoveTime)
            {
                LogVerbose($"Couldn't reach tree (dist: {dist:F1}m)");
                if (dist < AttackRange * 2f)
                {
                    SetPhase(GatherPhase.ChoppingTree);
                }
                else
                {
                    SetPhase(GatherPhase.Complete);
                }
            }

            return false;
        }

        private bool UpdateChoppingTree()
        {
            // Track stump info before it's destroyed
            if (_targetResource != null && _targetResource.GameObject != null && !_wasTargetingStump)
            {
                if (ResourceDataHelper.IsTreeStump(_targetResource.GameObject))
                {
                    _wasTargetingStump = true;
                    _targetStumpName = Utils.GetPrefabName(_targetResource.GameObject);
                    _targetStumpPosition = _targetResource.InteractionPosition;
                    LogVerbose($"Targeting stump: {_targetStumpName}");
                }
            }

            // Check if target destroyed
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                if (_wasTargetingStump)
                {
                    LogVerbose($"Destroyed stump: {_targetStumpName}");
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                    _targetStumpName = null;
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }

                if (_wasTargetingTree)
                {
                    LogVerbose($"Tree destroyed, waiting for logs");
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }

                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }

            // Check if destructible gone but GameObject exists
            if (_targetResource.Destructible == null)
            {
                if (_wasTargetingStump)
                {
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                }
                
                if (_wasTargetingTree)
                {
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }

                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }

            // Reposition if stuck
            if (_consecutiveNoColliderHits >= MaxNoColliderHits)
            {
                LogVerbose($"Repositioning after {_consecutiveNoColliderHits} missed hits");
                _consecutiveNoColliderHits = 0;
                RepositionAroundTarget();
                return false;
            }

            // Movement and facing
            bool isAttacking = _character != null && _character.InAttack();
            if (!isAttacking)
            {
                float dist = Vector3.Distance(Transform.position, _targetResource.InteractionPosition);
                if (dist > AttackRange)
                {
                    MoveToPosition(_targetResource.InteractionPosition);
                }
                else
                {
                    StopMovement();
                }
                FaceTarget(_targetResource.InteractionPosition);
            }
            else
            {
                StopMovement();
            }

            // Attack
            if (Time.time - _lastAttackTime >= AttackInterval)
            {
                AttackTree();
                _lastAttackTime = Time.time;
            }

            // Timeout
            if (Time.time - _phaseStartTime > 60f)
            {
                LogVerbose("Chopping phase timeout");
                SetPhase(GatherPhase.WaitingForDrops);
            }

            return false;
        }

        private bool UpdateWaitingForLogs()
        {
            _combatMovement?.LockMovement("WaitingForLogs", LogCheckWait + 2f);
            StopMovement();

            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }

            FaceTarget(_lastTreePosition);

            if (Time.time - _phaseStartTime < LogCheckWait)
            {
                return false;
            }

            _combatMovement?.UnlockMovement();

            // Find logs
            var logs = FindAllNearbyLogs(_lastTreePosition);
            if (logs.Count > 0)
            {
                ResourceDataHelper.ResourceData closestLog = null;
                float closestDist = float.MaxValue;
                foreach (var log in logs)
                {
                    float dist = Vector3.Distance(Transform.position, log.InteractionPosition);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestLog = log;
                    }
                }

                if (closestLog != null)
                {
                    LogVerbose($"Found {logs.Count} logs, targeting closest");
                    _targetResource = closestLog;
                    _targetPosition = closestLog.InteractionPosition;
                    _consecutiveNoColliderHits = 0;
                    _wasTargetingTree = true;
                    SetPhase(GatherPhase.MovingToTree);
                    MoveToPosition(_targetPosition);
                    return false;
                }
            }

            // Check for stumps
            var stump = ResourceDataHelper.FindNearestTreeStump(_lastTreePosition, LogSearchRadius);
            if (stump != null)
            {
                var stumpData = ResourceDataHelper.GetResourceData(stump);
                if (stumpData != null && stumpData.IsValid)
                {
                    LogVerbose($"Found stump to clear");
                    _targetResource = stumpData;
                    _targetPosition = stumpData.InteractionPosition;
                    _consecutiveNoColliderHits = 0;
                    _wasTargetingTree = true;
                    SetPhase(GatherPhase.MovingToTree);
                    MoveToPosition(_targetPosition);
                    return false;
                }
            }

            SetPhase(GatherPhase.WaitingForDrops);
            return false;
        }

        private bool UpdateProcessingLogs()
        {
            // Same as UpdateChoppingTree but for logs
            return UpdateChoppingTree();
        }

        private bool UpdateWaitingForDrops()
        {
            StopMovement();

            if (Time.time - _phaseStartTime < 2f)
            {
                return false;
            }

            NotifyOwner();
            SetPhase(GatherPhase.Complete);
            return false;
        }

        #endregion

        #region Attack Logic

        private void AttackTree()
        {
            if (_targetResource == null || _targetResource.GameObject == null || _humanoid == null) return;

            var weapon = GetEquippedAxe();
            if (weapon == null)
            {
                LogVerbose("No axe equipped during attack!");
                if (!TryEquipAxeFromStorage())
                {
                    Debug.LogWarning($"[WoodGathering] {Companion?.companionName} lost axe during chopping");
                    SetPhase(GatherPhase.Complete);
                    return;
                }
                weapon = GetEquippedAxe();
            }

            // Before swinging, check whether our axe tier is sufficient.
            // If not, count the failure and act after IneffectiveHitThreshold.
            if (weapon != null && !ResourceDataHelper.IsToolAppropriate(
                    weapon, ResourceDataHelper.ToolRequirement.Axe, _targetResource.MinToolTier))
            {
                _ineffectiveHitCount++;
                LogVerbose($"Tool tier too low for {_targetResource.Name} (hit {_ineffectiveHitCount}/{IneffectiveHitThreshold})");
                if (_ineffectiveHitCount >= IneffectiveHitThreshold)
                {
                    _ineffectiveHitCount = 0;
                    HandleIneffectiveTool();
                }
                return;
            }

            _ineffectiveHitCount = 0;
            PlaySwingAnimation(weapon);
            Companion.StartCoroutine(ApplyDamageDelayed(weapon, DamageDelay));
        }

        private IEnumerator ApplyDamageDelayed(ItemDrop.ItemData weapon, float delay)
        {
            yield return new WaitForSeconds(delay);

            if (_targetResource == null || _targetResource.GameObject == null || _targetResource.Destructible == null)
            {
                yield break;
            }

            // Raycast to find hit collider
            Collider hitCollider = null;
            Vector3 hitPoint = _targetResource.InteractionPosition;
            Vector3 hitDir = (hitPoint - Transform.position).normalized;

            Vector3 rayOrigin = Transform.position + Vector3.up * 1.0f;
            Vector3 rayDir = (_targetResource.InteractionPosition - rayOrigin).normalized;
            float rayDistance = Vector3.Distance(rayOrigin, _targetResource.InteractionPosition) + 1f;

            int hitMask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain", "piece_nonsolid");

            RaycastHit[] hits = Physics.RaycastAll(rayOrigin, rayDir, rayDistance, hitMask);
            foreach (var hit in hits)
            {
                if (hit.collider != null)
                {
                    if (hit.collider.transform == _targetResource.GameObject.transform ||
                        hit.collider.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(hit.collider.transform))
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }

                    var parentDestructible = hit.collider.GetComponentInParent<IDestructible>();
                    if (parentDestructible != null && parentDestructible == _targetResource.Destructible)
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }
                }
            }

            // Fallback: sphere overlap
            if (hitCollider == null)
            {
                Collider[] colliders = Physics.OverlapSphere(_targetResource.InteractionPosition, 0.5f, hitMask);
                foreach (var collider in colliders)
                {
                    if (collider.transform == _targetResource.GameObject.transform ||
                        collider.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(collider.transform))
                    {
                        hitCollider = collider;
                        hitPoint = collider.bounds.center;
                        break;
                    }
                }
            }

            if (hitCollider == null)
            {
                _consecutiveNoColliderHits++;
                LogVerbose($"Hit without collider (count: {_consecutiveNoColliderHits})");
            }
            else
            {
                _consecutiveNoColliderHits = 0;
            }

            // Create and apply damage
            var hitData = ResourceDataHelper.CreateResourceHitData(
                _targetResource,
                _character,
                weapon,
                hitPoint,
                hitDir,
                hitCollider
            );

            _targetResource.Destructible?.Damage(hitData);

            // Raise woodcutting skill
            var skills = Companion?.GetSkills();
            if (skills != null)
            {
                skills.RaiseSkill(Skills.SkillType.WoodCutting, 1f);
            }

            LogVerbose($"Hit {_targetResource.Name} with {weapon?.m_shared?.m_name ?? "axe"}");
        }

        private void PlaySwingAnimation(ItemDrop.ItemData weapon)
        {
            var targetType = _targetResource?.Destructible?.GetDestructibleType() ?? DestructibleType.Default;
            string trigger = _swingChain.Swing(_zanim, _animator, weapon, _character.GetTimeSinceLastAttack(), targetType);
            LogVerbose($"Playing animation: {trigger}");
        }

        private void PlayInteractAnimation()
        {
            if (_animator != null) _animator.SetTrigger("interact");
            if (_zanim != null) _zanim.SetTrigger("interact");
        }

        private void RepositionAroundTarget()
        {
            if (_targetResource == null || _targetResource.GameObject == null) return;

            Vector3 resourceCenter = _targetResource.InteractionPosition;
            Vector3 currentDir = (Transform.position - resourceCenter).normalized;

            float angle = Random.Range(90f, 120f) * (Random.value > 0.5f ? 1f : -1f);
            Vector3 newDir = Quaternion.Euler(0, angle, 0) * currentDir;
            Vector3 newPosition = resourceCenter + newDir * AttackRange * 0.8f;

            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(newPosition, out groundHeight))
                {
                    newPosition.y = groundHeight;
                }
            }

            MoveToPosition(newPosition);
        }

        #endregion

        #region Tool Management

        private bool HasAxeAvailable()
        {
            if (HasAxeEquippedOrInInventory()) return true;
            return TryFindAxeInNearbyChests();
        }

        /// <summary>
        /// Called after IneffectiveHitThreshold consecutive swings dealt no damage
        /// because the equipped axe tier is below the tree's requirement.
        /// Priority order:
        ///   1. Fetch a better axe from a nearby chest.
        ///   2. Find a weaker tree we can actually chop.
        ///   3. Give up (complete the behavior).
        /// </summary>
        private void HandleIneffectiveTool()
        {
            int currentTier = GetEquippedAxe()?.m_shared?.m_toolTier ?? 0;
            int requiredTier = _targetResource?.MinToolTier ?? 1;

            Debug.Log($"[WoodGathering] {Companion?.companionName} tool tier {currentTier} < required {requiredTier} - looking for upgrade or easier target");

            // 1. Try to find a better axe in nearby chests.
            if (TryFindBetterAxeInChests(requiredTier))
            {
                SetPhase(GatherPhase.MovingToChestForTool);
                Vector3 chestPoint = InteractionPointHelper.GetContainerInteractionPoint(
                    _toolChest, Transform.position, ChestInteractionDistance);
                MoveToPosition(chestPoint);
                CompanionChatHelper.ShowWorkingStatus(Companion, "Getting a better axe...");
                return;
            }

            // 2. Try to craft an axe good enough for this tree.
            if (TryPlanAxeCraft(requiredTier))
            {
                BeginAxeCraft();
                CompanionChatHelper.ShowWorkingStatus(Companion, "Crafting an axe...");
                return;
            }

            // 3. Find a nearby tree we can chop with the axe we have.
            var easierTree = FindWeakerNearbyTree(currentTier);
            if (easierTree != null)
            {
                Debug.Log($"[WoodGathering] {Companion?.companionName} switching to easier tree '{easierTree.Name}' (tier {easierTree.MinToolTier})");
                _targetResource = easierTree;
                _wasTargetingTree = true;
                _lastTreePosition = easierTree.InteractionPosition;
                SetPhase(GatherPhase.MovingToTree);
                MoveToPosition(easierTree.InteractionPosition);
                return;
            }

            // 4. Nothing we can do — finish up.
            Debug.Log($"[WoodGathering] {Companion?.companionName} no upgrade or easier tree found - stopping");
            SetPhase(GatherPhase.Complete);
        }

        /// <summary>
        /// Searches nearby chests for an axe with tier >= <paramref name="requiredTier"/>
        /// that is better than what the companion currently holds.
        /// Populates _toolChest / _toolToPull on success.
        /// </summary>
        private bool TryFindBetterAxeInChests(int requiredTier)
        {
            Vector3 position = Transform.position;
            float searchRadius = CompanionSettings.ChestSearchRadius;

            var nearbyChests = ChestHelper.FindNearbyChests(position, searchRadius);
            if (nearbyChests == null || nearbyChests.Count == 0) return false;

            // Must meet the requirement and beat what we hold.
            ItemDrop.ItemData bestAxe = GetEquippedAxe();
            Container bestChest = null;

            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (ResourceDataHelper.IsToolAppropriate(item, ResourceDataHelper.ToolType.Axe, requiredTier) &&
                        ResourceDataHelper.IsBetterTool(item, bestAxe, ResourceDataHelper.ToolType.Axe))
                    {
                        bestAxe = item;
                        bestChest = chest;
                    }
                }
            }

            if (bestChest == null) return false;

            _toolToPull = bestAxe;
            _toolChest  = bestChest;
            LogVerbose($"Found better axe {bestAxe.m_shared?.m_name} (tier {bestAxe.m_shared.m_toolTier}) in chest");
            return true;
        }

        /// <summary>
        /// Finds the nearest tree whose MinToolTier is <= <paramref name="axeTier"/>,
        /// excluding the current (too-hard) target.
        /// </summary>
        private ResourceDataHelper.ResourceData FindWeakerNearbyTree(int axeTier)
        {
            var homePos      = IdleBehavior?.HomePosition ?? Transform.position;
            float searchRadius = CompanionSettings.GetStayModeWorkSearchRadius(homePos);

            return ResourceDataHelper.FindNearestResource(
                Transform.position,
                searchRadius,
                res =>
                {
                    if (res.GameObject == null) return false;
                    if (_targetResource != null && res.GameObject == _targetResource.GameObject) return false;
                    if (res.MinToolTier > axeTier) return false;
                    // Must be a tree or log, not a rock/ore node.
                    bool isWood = res.RequiredTool == ResourceDataHelper.ToolType.Axe
                               || res.RequiredTool == ResourceDataHelper.ToolType.None;
                    return isWood;
                }
            );
        }

        private static readonly CompanionInventory.EquipmentSlot[] AxeCarrySlots =
        {
            CompanionInventory.EquipmentSlot.RightHand,
            CompanionInventory.EquipmentSlot.RightBack,
            CompanionInventory.EquipmentSlot.LeftBack
        };

        private bool HasAxeEquippedOrInInventory()
        {
            // Check equipped weapon
            var weapon = GetEquippedAxe();
            if (weapon != null) return true;

            // Check equipment slots
            if (_inventory != null)
            {
                foreach (var slot in AxeCarrySlots)
                {
                    var item = _inventory.GetEquippedItem(slot);
                    if (IsAxe(item)) return true;
                }

                // Check storage
                var storage = _inventory.GetStorageInventory();
                if (storage != null)
                {
                    foreach (var item in storage.GetAllItems())
                    {
                        if (IsAxe(item)) return true;
                    }
                }
            }

            return false;
        }

        private bool TryFindAxeInNearbyChests()
        {
            Vector3 position = Transform.position;
            float searchRadius = CompanionSettings.ChestSearchRadius;

            var nearbyChests = ChestHelper.FindNearbyChests(position, searchRadius);
            if (nearbyChests == null || nearbyChests.Count == 0)
            {
                LogVerbose($"No chests found within {searchRadius}m");
                return false;
            }

            LogVerbose($"Searching {nearbyChests.Count} chests for axe");

            ItemDrop.ItemData bestAxe = null;
            Container bestChest = null;

            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;

                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;

                foreach (var item in chestInv.GetAllItems())
                {
                    if (IsAxe(item) && ResourceDataHelper.IsBetterTool(item, bestAxe, ResourceDataHelper.ToolType.Axe))
                    {
                        bestAxe = item;
                        bestChest = chest;
                    }
                }
            }

            if (bestAxe != null && bestChest != null)
            {
                _toolToPull = bestAxe;
                _toolChest = bestChest;
                LogVerbose($"Found {bestAxe.m_shared?.m_name} (tier {bestAxe.m_shared.m_toolTier}) in chest");
                return true;
            }

            LogVerbose("No axe found in any chest");
            return false;
        }

        private bool TryPullAxeFromChest()
        {
            if (_toolChest == null || _toolToPull == null || _inventory == null) return false;

            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;

            var chestInv = _toolChest.GetInventory();
            if (chestInv == null) return false;

            // Verify axe still in chest
            bool found = false;
            foreach (var item in chestInv.GetAllItems())
            {
                if (item == _toolToPull)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // Try to find another axe
                if (TryFindAxeInNearbyChests() && _toolChest != null)
                {
                    chestInv = _toolChest.GetInventory();
                    if (chestInv == null) return false;
                }
                else
                {
                    return false;
                }
            }

            // Transfer
            var clone = _toolToPull.Clone();
            chestInv.RemoveOneItem(_toolToPull);

            if (storage.AddItem(clone))
            {
                _inventory.SaveToZDO();
                LogVerbose($"Pulled {clone.m_shared?.m_name} from chest");
                CompanionEvents.FireItemPulled(Companion, clone.m_dropPrefab?.name ?? "axe", 1);
                return true;
            }
            else
            {
                chestInv.AddItem(_toolToPull);
                Debug.LogWarning($"[WoodGathering] {Companion?.companionName} couldn't add axe to storage");
                return false;
            }
        }

        private bool TryEquipAxeFromStorage()
        {
            if (_inventory == null) return false;

            var storage = _inventory.GetStorageInventory();
            if (storage == null) return false;

            ItemDrop.ItemData bestAxe = null;

            foreach (var item in storage.GetAllItems())
            {
                if (IsAxe(item) && ResourceDataHelper.IsBetterTool(item, bestAxe, ResourceDataHelper.ToolType.Axe))
                    bestAxe = item;
            }

            if (bestAxe == null) return false;

            if (!ResourceDataHelper.TryEquipInRightHand(_inventory, bestAxe, null, holsterWeaponOnBack: false))
            {
                LogVerbose($"No room in storage for {_inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand)?.m_shared?.m_name} - keeping it equipped");
                return false;
            }

            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();

            LogVerbose($"Equipped {bestAxe.m_shared?.m_name} from storage");
            return true;
        }

        private void EquipBestAxe()
        {
            if (_inventory == null) return;

            ItemDrop.ItemData bestAxe = null;
            CompanionInventory.EquipmentSlot? bestSlot = null;

            foreach (var slot in AxeCarrySlots)
            {
                var equipped = _inventory.GetEquippedItem(slot);
                if (IsAxe(equipped) && ResourceDataHelper.IsBetterTool(equipped, bestAxe, ResourceDataHelper.ToolType.Axe))
                {
                    bestAxe = equipped;
                    bestSlot = slot;
                }
            }

            var storage = _inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (IsAxe(item) && ResourceDataHelper.IsBetterTool(item, bestAxe, ResourceDataHelper.ToolType.Axe))
                    {
                        bestAxe = item;
                        bestSlot = null;
                    }
                }
            }

            if (bestAxe == null)
            {
                LogVerbose("No axe found to equip");
                return;
            }

            // Already equipped in right hand
            if (bestSlot == CompanionInventory.EquipmentSlot.RightHand)
            {
                LogVerbose($"Already have {bestAxe.m_shared?.m_name} equipped");
                return;
            }

            // Holster left hand item if needed
            HolsterLeftHand();

            if (!ResourceDataHelper.TryEquipInRightHand(_inventory, bestAxe, bestSlot, holsterWeaponOnBack: true))
            {
                LogVerbose($"No room to put away {_inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand)?.m_shared?.m_name} - keeping it equipped");
                return;
            }

            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();

            LogVerbose($"Equipped {bestAxe.m_shared?.m_name} (tier {bestAxe.m_shared.m_toolTier})");
        }

        private void HolsterLeftHand()
        {
            if (_inventory == null) return;

            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand == null) return;

            var itemType = leftHand.m_shared.m_itemType;
            bool shouldHolster = itemType == ItemDrop.ItemData.ItemType.Bow ||
                                 itemType == ItemDrop.ItemData.ItemType.Shield ||
                                 itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                                 itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;

            if (!shouldHolster) return;

            if (!ResourceDataHelper.TryStowEquipped(_inventory, CompanionInventory.EquipmentSlot.LeftHand, CompanionInventory.EquipmentSlot.LeftBack))
                LogVerbose($"No room to holster {leftHand.m_shared?.m_name} - keeping it in hand");

            _inventory.ApplyVisualEquipment();
        }

        /// <summary>The best axe tier the companion holds, carries, can pull from a nearby chest or can craft now.</summary>
        private int ReachableAxeTier()
        {
            var axes = new List<ItemDrop.ItemData>();
            var equipped = GetEquippedAxe();
            if (equipped != null) axes.Add(equipped);
            var storage = _inventory?.GetStorageInventory();
            if (storage != null) axes.AddRange(storage.GetAllItems());
            foreach (var chest in ChestHelper.FindNearbyChests(Transform.position, CompanionSettings.ChestSearchRadius))
            {
                var chestInv = chest != null ? chest.GetInventory() : null;
                if (chestInv != null) axes.AddRange(chestInv.GetAllItems());
            }
            return Mathf.Max(ResourceDataHelper.BestToolTier(axes, ResourceDataHelper.ToolType.Axe),
                ResourceDataHelper.BestCraftableToolTier(CraftableAxes, ResourceDataHelper.ToolType.Axe, storage, Transform.position, WorkbenchSearchRadius));
        }

        private ItemDrop.ItemData GetEquippedAxe()
        {
            if (_inventory == null) return null;

            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (IsAxe(rightHand)) return rightHand;

            return null;
        }

        private static bool IsAxe(ItemDrop.ItemData item)
        {
            return ResourceDataHelper.IsToolAppropriate(item, ResourceDataHelper.ToolType.Axe, 0);
        }

        /// <summary>
        /// Picks an axe of at least <paramref name="minTier"/> the companion can craft from its vanilla recipe and sets
        /// _craftRecipe and _craftWorkbench, the station that recipe needs (null when it needs none).
        /// </summary>
        private bool TryPlanAxeCraft(int minTier)
        {
            _craftRecipe = ResourceDataHelper.FindCraftableTool(CraftableAxes, ResourceDataHelper.ToolType.Axe, minTier,
                _inventory?.GetStorageInventory(), Transform.position, WorkbenchSearchRadius, out _craftWorkbench);
            return _craftRecipe != null;
        }

        private void BeginAxeCraft()
        {
            if (_craftWorkbench != null)
            {
                SetPhase(GatherPhase.MovingToWorkbench);
                MoveToPosition(_craftWorkbench.transform.position);
                return;
            }

            StopMovement();
            SetPhase(GatherPhase.CraftingTool);
        }

        private bool CraftPlannedAxe()
        {
            var storage = _inventory?.GetStorageInventory();
            if (_craftRecipe == null || storage == null || !ResourceDataHelper.CraftTool(_craftRecipe, _craftWorkbench, storage))
                return false;

            _inventory.SaveToZDO();
            Debug.Log($"[WoodGathering] {Companion?.companionName} crafted {_craftRecipe.m_item.name}");
            return true;
        }

        #endregion

        #region Tree Finding

        private GameObject FindNearbyTree(Vector3 position, float radius)
        {
            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            var processed = new HashSet<GameObject>();
            int axeTier = ReachableAxeTier();

            foreach (var collider in colliders)
            {
                if (collider == null) continue;

                var treeBase = collider.GetComponent<TreeBase>() ?? collider.GetComponentInParent<TreeBase>();
                var treeLog = collider.GetComponent<TreeLog>() ?? collider.GetComponentInParent<TreeLog>();

                GameObject target = null;
                if (treeBase != null && !processed.Contains(treeBase.gameObject))
                {
                    target = treeBase.gameObject;
                    processed.Add(target);
                }
                else if (treeLog != null && !processed.Contains(treeLog.gameObject))
                {
                    target = treeLog.gameObject;
                    processed.Add(target);
                }

                // A tree no reachable axe can cut (a tier-2 birch nearest home) would block chopping for good.
                if (target == null || !ResourceDataHelper.CanChop(target, axeTier)) continue;

                float dist = Vector3.Distance(position, target.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = target;
                }
            }

            // Also check for stumps
            if (closest == null)
            {
                closest = ResourceDataHelper.FindNearestTreeStump(position, radius);
            }

            return closest;
        }

        private List<ResourceDataHelper.ResourceData> FindAllNearbyLogs(Vector3 position)
        {
            var results = new List<ResourceDataHelper.ResourceData>();
            var processed = new HashSet<GameObject>();

            Collider[] colliders = Physics.OverlapSphere(position, LogSearchRadius);

            foreach (var collider in colliders)
            {
                if (collider == null) continue;

                var treeLog = collider.GetComponent<TreeLog>() ?? collider.GetComponentInParent<TreeLog>();
                if (treeLog != null && !processed.Contains(treeLog.gameObject))
                {
                    processed.Add(treeLog.gameObject);
                    var logData = ResourceDataHelper.GetResourceData(treeLog.gameObject);
                    if (logData != null && logData.IsValid)
                    {
                        results.Add(logData);
                    }
                }
            }

            return results;
        }

        private void OnStumpDestroyed(Vector3 stumpPosition, string stumpName)
        {
            // 70% chance to spawn sapling
            if (Random.value > 0.70f)
            {
                LogVerbose("Stump cleared - no sapling");
                return;
            }

            string saplingPrefab = ResourceDataHelper.GetSaplingForStump(stumpName);
            if (string.IsNullOrEmpty(saplingPrefab))
            {
                LogVerbose($"No matching sapling for: {stumpName}");
                return;
            }

            SpawnSapling(saplingPrefab, stumpPosition);
        }

        private void SpawnSapling(string prefabName, Vector3 position)
        {
            if (ZNetScene.instance == null) return;

            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                LogVerbose($"Could not find sapling prefab: {prefabName}");
                return;
            }

            Vector3 spawnPos = position;
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
                {
                    spawnPos.y = groundHeight;
                }
            }

            spawnPos.x += Random.Range(-0.3f, 0.3f);
            spawnPos.z += Random.Range(-0.3f, 0.3f);

            var sapling = CompanionNetworkHelper.Spawn(prefab, spawnPos, Quaternion.identity);

            if (sapling != null)
            {
                Debug.Log($"[WoodGathering] {Companion?.companionName} planted a {prefabName}!");

                var owner = Companion?.GetOwner();
                if (owner != null && owner == Player.m_localPlayer)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                        $"{Companion.GetDisplayName()} planted a sapling");
                }
            }
        }

        #endregion

        #region Helpers

        private void SetPhase(GatherPhase phase)
        {
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }

        private void MoveToPosition(Vector3 position)
        {
            _targetPosition = position;
            TryMoveToPosition(position, walk: true, run: false);
        }

        private new void StopMovement()
        {
            base.StopMovement();
            _combatMovement?.ClearMoveDestination();

            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
        }

        private void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - Transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) return;

            // Single facing-writer: face the tree through the FacingAuthority (SubBehavior); combat
            // preempts if a fight interrupts. Direct write fallback only.
            var facing = Companion != null ? Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, 0.4f))
                    facing.SetLookDirection(BehaviorName, dir);
                return;
            }

            dir.Normalize();
            Transform.rotation = Quaternion.LookRotation(dir);
        }

        private void NotifyOwner()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);

            var collectedItems = _autoPickup?.EndGatheringSession();

            var owner = Companion?.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;

            if (collectedItems != null && collectedItems.Count > 0)
            {
                var parts = new List<string>();
                int totalItems = 0;

                foreach (var kvp in collectedItems)
                {
                    parts.Add($"{kvp.Value} {kvp.Key}");
                    totalItems += kvp.Value;
                }

                string message;
                if (parts.Count <= 3)
                {
                    message = $"{Companion.GetDisplayName()} collected {string.Join(", ", parts)}";
                }
                else
                {
                    message = $"{Companion.GetDisplayName()} collected {totalItems} items";
                }

                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
            else if (_resourcesGathered > 0)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{Companion.GetDisplayName()} destroyed {_resourcesGathered} trees");
            }
        }

        private void LogVerbose(string message)
        {
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[WoodGathering] {Companion?.companionName} {message}");
            }
        }

        #endregion
    }
}
