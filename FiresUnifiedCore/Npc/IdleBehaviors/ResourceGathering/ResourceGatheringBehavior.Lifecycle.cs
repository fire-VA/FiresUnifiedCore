using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Lifecycle methods: Start, Update, Cancel, phase routing.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        public override void Start()
        {
            base.Start();
            
            _currentPhase = GatherPhase.FindingResource;
            _phaseStartTime = Time.time;
            _resourcesGathered = 0;
            _consecutiveNoColliderHits = 0;
            _wasTargetingTree = false;
            
            // Reset stump tracking
            _wasTargetingStump = false;
            _targetStumpName = null;
            _targetStumpPosition = Vector3.zero;
            
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
                    
                    // IMPROVEMENT: Check if we need to retrieve a tool from chests first
                    if (_targetResource.RequiresCombat && !HasToolEquippedOrInInventory(_targetResource))
                    {
                        // Try to find the tool in nearby chests
                        if (TryFindToolInNearbyChests(_targetResource))
                        {
                            // Found tool in chest - go get it first
                            SetPhase(GatherPhase.MovingToChestForTool);
                            
                            // Calculate proper interaction point in front of chest
                            Vector3 chestInteractionPoint = Core.InteractionPointHelper.GetContainerInteractionPoint(
                                _toolChest, Transform.position, CHEST_INTERACTION_DISTANCE);
                            MoveToPosition(chestInteractionPoint);
                            
                            CompanionChatHelper.ShowWorkingStatus(Companion, $"Getting tool from chest...");
                            
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} needs to get {_toolToPull?.m_shared?.m_name} from chest first");
                        }
                        else if (CanCraftToolForResource(_targetResource))
                        {
                            _craftWorkbench = FindNearestWorkbench();
                            if (_craftWorkbench != null)
                            {
                                SetPhase(GatherPhase.MovingToWorkbench);
                                MoveToPosition(_craftWorkbench.transform.position);
                                CompanionChatHelper.ShowWorkingStatus(Companion, "Crafting a tool...");

                                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[ResourceGathering] {Companion?.companionName} no tool in chests - will craft one at workbench");
                            }
                            else
                            {
                                Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} cannot gather {_targetResource.Name} - no tool or workbench");
                                CompanionChatHelper.QuickMessages.CantDoTask(Companion, $"I need a {_targetResource.RequiredTool.ToString().ToLower()}");
                                SetPhase(GatherPhase.Complete);
                            }
                        }
                        else
                        {
                            // No tool available anywhere - can't gather this resource
                            Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} cannot gather {_targetResource.Name} - no {_targetResource.RequiredTool} available");
                            CompanionChatHelper.QuickMessages.CantDoTask(Companion, $"I need a {_targetResource.RequiredTool.ToString().ToLower()}");
                            SetPhase(GatherPhase.Complete);
                        }
                    }
                    else
                    {
                        // Have tool or don't need one - proceed normally
                        if (_targetResource.RequiresCombat)
                        {
                            EquipBestToolForResource(_targetResource);
                        }
                        
                        SetPhase(GatherPhase.MovingToResource);
                        MoveToPosition(_targetPosition);
                        
                        CompanionChatHelper.QuickMessages.GatheringResources(Companion, _targetResource.Name);
                    }
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    {
                        string resourceType = _targetResource.IsPickable ? "pickable" : "destructible";
                        string toolInfo = _targetResource.RequiredTool != ResourceDataHelper.ToolType.None 
                            ? $", requires {_targetResource.RequiredTool} tier {_targetResource.MinToolTier}" 
                            : "";
                        Debug.Log($"[ResourceGathering] {Companion.companionName} targeting {_targetResource.Name} ({resourceType}{toolInfo})");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} - Invalid target resource");
                    SetPhase(GatherPhase.Complete);
                }
            }
            
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut())
            {
                Complete();
                return true;
            }
            
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());
            
            switch (_currentPhase)
            {
                case GatherPhase.FindingResource:
                    return UpdateFindingResource();
                    
                case GatherPhase.MovingToChestForTool:
                    return UpdateMovingToChestForTool();
                    
                case GatherPhase.RetrievingToolFromChest:
                    return UpdateRetrievingToolFromChest();

                case GatherPhase.MovingToWorkbench:
                    return UpdateMovingToWorkbench();

                case GatherPhase.CraftingTool:
                    return UpdateCraftingTool();

                case GatherPhase.MovingToResource:
                    return UpdateMovingToResource();
                    
                case GatherPhase.Interacting:
                    return UpdateInteracting();
                    
                case GatherPhase.Attacking:
                    return UpdateAttacking();
                    
                case GatherPhase.Repositioning:
                    return UpdateRepositioning();
                    
                case GatherPhase.WaitingForLogs:
                    return UpdateWaitingForLogs();
                    
                case GatherPhase.WaitingForDrops:
                    return UpdateWaitingForDrops();
                    
                case GatherPhase.DepositingToChests:
                    return UpdateDepositingToChests();
                    
                case GatherPhase.MovingToChest:
                    return UpdateMovingToChest();
                    
                case GatherPhase.Complete:
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();
            
            NotifyOwner();
            
            base.Cancel();
        }
        
        private bool UpdateFindingResource()
        {
            Complete();
            return true;
        }
        
        private bool UpdateMovingToResource()
        {
            if (_targetResource == null || !_targetResource.IsValid || _targetResource.GameObject == null)
            {
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            _targetPosition = _targetResource.InteractionPosition;
            float dist = Vector3.Distance(Transform.position, _targetPosition);
            
            float requiredRange = _targetResource.IsPickable ? PICKABLE_RANGE : ATTACK_RANGE;
            
            if (dist < requiredRange)
            {
                StopMovement();
                
                if (_targetResource.IsPickable)
                {
                    SetPhase(GatherPhase.Interacting);
                }
                else
                {
                    SetPhase(GatherPhase.Attacking);
                }
                return false;
            }
            
            MoveToPosition(_targetPosition);
            
            float timeInPhase = Time.time - _phaseStartTime;
            float maxMoveTime = 20f + (dist / 3f);
            
            if (timeInPhase > maxMoveTime)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} couldn't reach resource (dist: {dist:F1}m)");
                
                if (dist < requiredRange * 2f)
                {
                    if (_targetResource.IsPickable)
                        SetPhase(GatherPhase.Interacting);
                    else
                        SetPhase(GatherPhase.Attacking);
                }
                else
                {
                    SetPhase(GatherPhase.Complete);
                }
            }
            
            return false;
        }
        
        private bool UpdateRepositioning()
        {
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            Vector3 resourceCenter = _targetResource.InteractionPosition;
            Vector3 currentDir = (Transform.position - resourceCenter).normalized;
            
            float angle = Random.Range(90f, 120f) * (Random.value > 0.5f ? 1f : -1f);
            Vector3 newDir = Quaternion.Euler(0, angle, 0) * currentDir;
            Vector3 newPosition = resourceCenter + newDir * ATTACK_RANGE * 0.8f;
            
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(newPosition, out groundHeight))
                {
                    newPosition.y = groundHeight;
                }
            }
            
            float distToNewPos = Vector3.Distance(Transform.position, newPosition);
            
            if (distToNewPos < 1f || Time.time - _phaseStartTime > 5f)
            {
                StopMovement();
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} repositioned, resuming attack");
                
                SetPhase(GatherPhase.Attacking);
                return false;
            }
            
            MoveToPosition(newPosition);
            return false;
        }
        
        private bool UpdateMovingToWorkbench()
        {
            if (_craftWorkbench == null)
            {
                SetPhase(GatherPhase.Complete);
                return true;
            }

            const float WORKBENCH_INTERACT_DIST = 2f;
            float dist = Vector3.Distance(Transform.position, _craftWorkbench.transform.position);
            if (dist <= WORKBENCH_INTERACT_DIST)
            {
                StopMovement();
                FaceTarget(_craftWorkbench.transform.position);
                _zanim?.SetBool("crafting", true);
                _zanim?.SetBool("Working", true);
                SetPhase(GatherPhase.CraftingTool);
                return false;
            }

            MoveToPosition(_craftWorkbench.transform.position);

            if (Time.time - _phaseStartTime > 30f)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} timeout walking to workbench");
                SetPhase(GatherPhase.Complete);
            }

            return false;
        }

        private bool UpdateCraftingTool()
        {
            if (_craftWorkbench != null)
                FaceTarget(_craftWorkbench.transform.position);

            const float CRAFT_DURATION = 3f;
            if (Time.time - _phaseStartTime < CRAFT_DURATION)
                return false;

            _zanim?.SetBool("crafting", false);
            _zanim?.SetBool("Working", false);

            bool crafted = TryCraftToolForResource(_targetResource);
            _craftWorkbench = null;

            if (crafted)
            {
                EquipBestToolForResource(_targetResource);
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} crafted tool, heading to resource");
                SetPhase(GatherPhase.MovingToResource);
                MoveToPosition(_targetPosition);
                CompanionChatHelper.QuickMessages.GatheringResources(Companion, _targetResource?.Name ?? "resource");
            }
            else
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} failed to craft tool");
                SetPhase(GatherPhase.Complete);
            }

            return false;
        }

        private void TryFindNextResource()
        {
            if (_resourcesGathered < 5 && Random.value < CONTINUE_GATHERING_CHANCE)
            {
                var nextResource = FindNearbyResource();
                if (nextResource != null)
                {
                    _targetResource = nextResource;
                    _targetPosition = nextResource.InteractionPosition;
                    
                    if (nextResource.RequiresCombat)
                    {
                        EquipBestToolForResource(nextResource);
                    }
                    
                    SetPhase(GatherPhase.MovingToResource);
                    return;
                }
            }
            
            NotifyOwner();
            SetPhase(GatherPhase.Complete);
        }
        
        private void NotifyOwner()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            
            var collectedItems = _autoPickup?.EndGatheringSession();
            
            var owner = Companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            if (collectedItems != null && collectedItems.Count > 0)
            {
                var parts = new System.Collections.Generic.List<string>();
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
                    message = $"{Companion.GetDisplayName()} collected {totalItems} items ({parts.Count} types)";
                }
                
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
            else if (_resourcesGathered > 0)
            {
                string message = $"{Companion.GetDisplayName()} destroyed {_resourcesGathered} resources";
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }
    }
}
