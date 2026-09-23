using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    // Lifecycle methods: Start, Update, Cancel, GetStatusDescription
    public partial class SmelterOperatorBehavior
    {
        #region Combat Resumption State
        
        // Saved state for combat resumption
        private OperatePhase _savedPhase;
        private Smelter _savedSmelter;
        private Vector3 _savedTargetPosition;
        private int _savedOperationCycles;
        private bool _savedIsKilnOperation;
        
        /// <summary>
        /// Saves the current state before combat interruption.
        /// </summary>
        protected override void SaveState()
        {
            _savedPhase = _currentPhase;
            _savedSmelter = _targetSmelter;
            _savedTargetPosition = _targetPosition;
            _savedOperationCycles = _operationCycles;
            _savedIsKilnOperation = _isKilnOperation;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Saved state: phase={_savedPhase}, smelter={_savedSmelter?.name}, cycles={_savedOperationCycles}");
        }
        
        /// <summary>
        /// Restores the saved state after combat ends.
        /// </summary>
        protected override void RestoreState()
        {
            // Restore the saved state
            _targetSmelter = _savedSmelter;
            _targetPosition = _savedTargetPosition;
            _operationCycles = _savedOperationCycles;
            _isKilnOperation = _savedIsKilnOperation;
            
            // Validate that the smelter still exists
            if (_targetSmelter == null || !_targetSmelter.gameObject.activeInHierarchy)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Cannot restore - smelter no longer exists");
                SetPhase(OperatePhase.Complete);
                return;
            }
            
            // Re-find nearby chests (they may have changed)
            FindNearbyChests();
            
            // Try to re-occupy the smelter
            if (!InteractableOccupancyManager.TryOccupy(_targetSmelter.gameObject, _character, MaxOperateTime))
            {
                // Someone else grabbed it while we were fighting
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Cannot restore - smelter is now occupied");
                SetPhase(OperatePhase.Complete);
                return;
            }
            
            // Start gathering session again
            _autoPickup?.StartGatheringSession();
            
            // Set command priority again
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
            
            // Resume from where we were, or start moving back to station
            float dist = Vector3.Distance(Transform.position, _targetPosition);
            if (dist > InteractionDistance)
            {
                // Need to walk back to the smelter first
                SetPhase(OperatePhase.MovingToStation);
                MoveToPosition(_targetPosition);
            }
            else
            {
                // We're close enough - resume the saved phase
                // But skip certain phases that don't make sense to resume
                switch (_savedPhase)
                {
                    case OperatePhase.WaitingForOutput:
                    case OperatePhase.WaitingForKilnOutput:
                        // These are fine to resume
                        SetPhase(_savedPhase);
                        break;
                    case OperatePhase.MovingToPickupArea:
                    case OperatePhase.PickingUpOutput:
                    case OperatePhase.CollectingOutput:
                        // Check if there's still output to collect
                        if (HasOutput() || CountNearbyGroundItems(_targetPosition) > 0)
                        {
                            SetPhase(OperatePhase.CollectingOutput);
                        }
                        else
                        {
                            SetPhase(OperatePhase.FillingStation);
                        }
                        break;
                    default:
                        // Default to filling station
                        SetPhase(OperatePhase.FillingStation);
                        break;
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Restored state: resuming from phase={_currentPhase}");
        }
        
        #endregion
        
        #region Start
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = OperatePhase.FindingStation;
            _phaseStartTime = Time.time;
            _itemsAdded = 0;
            _itemsCollected = 0;
            _itemsDeposited = 0;
            _operationCycles = 0;
            _nearbyChests.Clear();
            _needsCoalFromKiln = false;
            _nearbyKiln = null;
            _isKilnOperation = false;
            _primarySmelter = null;
            _primarySmelterPosition = Vector3.zero;
            _lastSmelterOutputCheck = 0f;
            
            // Reset anti-spam tracking
            _smelterIsFullWaitingForOutput = false;
            _lastOreAddTime = 0f;
            _lastFuelAddTime = 0f;
            _lastAnyAddTime = 0f;
            _lastEmptyTime = 0f;
            
            // Reset circuit breaker for pull attempts
            _consecutiveFailedPulls = 0;
            
            // Reset resource gathering flags
            _needsResourceGathering = false;
            _resourceGatheringMaterial = "";
            
            // Reset pickup tracking
            _currentPickupTarget = null;
            _pendingPickupItems.Clear();
            
            // Reset progress tracking for stuck detection
            _lastProgressPosition = Transform.position;
            _lastProgressTime = Time.time;
            _noProgressDuration = 0f;
            
            if (_commandedTarget != null)
            {
                _targetSmelter = _commandedTarget.GetComponent<Smelter>();
                
                // CRITICAL: Calculate the proper STANDING position near the smelter
                // We need to find the interactable point (Switch position) and then calculate
                // where the companion should STAND to interact with it.
                if (_targetSmelter != null)
                {
                    // Get the actual interactable point (Switch position)
                    Vector3 interactablePoint = Core.InteractionPointHelper.FindSmelterInteractablePoint(_targetSmelter);
                    
                    // Calculate standing position - offset from interactable toward companion
                    // This is where we pathfind TO, not the interactable itself
                    _targetPosition = Core.InteractionPointHelper.GetSmelterInteractionPoint(
                        _targetSmelter, 
                        Transform.position,
                        Core.InteractionPointHelper.DEFAULT_INTERACTION_DISTANCE);
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                    {
                        Debug.Log($"[SmelterOperator] Interactable point (Switch): {interactablePoint}");
                        Debug.Log($"[SmelterOperator] Standing position (target): {_targetPosition}");
                        Debug.Log($"[SmelterOperator] Prefab center was: {_commandedTarget.transform.position}");
                        Debug.Log($"[SmelterOperator] Companion position: {Transform.position}");
                    }
                }
                else
                {
                    // Fallback to transform position if no smelter component
                    _targetPosition = _commandedTarget.transform.position;
                }
                
                // Check if this IS a kiln (kilns are also Smelter components)
                if (_targetSmelter != null)
                {
                    _isKilnOperation = PieceDataHelper.IsCharcoalKiln(_targetSmelter);
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Target is {GetStationName()}, isKiln={_isKilnOperation}");
                }
                
                _commandedTarget = null;
                
                if (_targetSmelter != null)
                {
                    // EARLY RESERVATION: Claim the smelter as soon as we commit to walking
                    // to it, so other companions evaluating CanStart this frame don't all
                    // pick the same station and converge into a cluster.
                    if (!InteractableOccupancyManager.TryOccupy(_targetSmelter.gameObject, _character, MaxOperateTime))
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] {Companion.companionName} could not reserve {_targetSmelter.m_name} at Start - already taken by another companion");
                        // Force a fresh scan next time so we pick a different station
                        _cachedAutonomousSmelter = null;
                        _lastAutonomousScanTime = -999f;
                        _targetSmelter = null;
                        SetPhase(OperatePhase.Complete);
                        return;
                    }

                    SetPhase(OperatePhase.MovingToStation);
                    MoveToPosition(_targetPosition);

                    // Chat feedback
                    CompanionChatHelper.QuickMessages.OperatingSmelter(Companion);
                }
            }
            
            if (_combatMovement != null)
            {
                _combatMovement.SetCommandPriorityDuration(MaxDuration);
            }
            
            // Start gathering session for auto-pickup tracking
            _autoPickup?.StartGatheringSession();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] {Companion.companionName} starting smelter operation (continuous mode)");
        }
        
        #endregion
        
        #region Update
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            if (IsTimedOut())
            {
                Complete();
                return true;
            }
            
            // IMPROVEMENT: Check if we need to switch to resource gathering
            // This happens when CheckResourceAvailability() determined we need to gather materials
            if (_needsResourceGathering)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} transitioning to resource gathering for {_resourceGatheringMaterial}");
                
                // Release smelter occupancy so we can come back later
                if (_targetSmelter != null)
                {
                    InteractableOccupancyManager.Release(_targetSmelter.gameObject, _character);
                }
                
                // Complete this behavior - IdleBehavior will evaluate and start ResourceGathering next
                // The companion will gather resources, deposit them, then can try smelter again
                _needsResourceGathering = false;
                _resourceGatheringMaterial = "";
                Complete();
                
                // The gathering behavior should naturally start since there are resources nearby
                // and no materials in chests for smelting
                return true;
            }
            
            // Show current status above companion's head
            ShowCurrentStatus();
            
            switch (_currentPhase)
            {
                case OperatePhase.FindingStation:
                    return UpdateFindingStation();
                    
                case OperatePhase.MovingToStation:
                    return UpdateMovingToStation();
                    
                case OperatePhase.PullingFromChests:
                    return UpdatePullingFromChests();
                    
                // Physical chest interaction phases
                case OperatePhase.MovingToChestForPull:
                    return UpdateMovingToChestForPull();
                    
                case OperatePhase.InteractingWithChestForPull:
                    return UpdateInteractingWithChestForPull();
                    
                case OperatePhase.MovingToChestForDeposit:
                    return UpdateMovingToChestForDeposit();
                    
                case OperatePhase.InteractingWithChestForDeposit:
                    return UpdateInteractingWithChestForDeposit();
                    
                case OperatePhase.FillingStation:
                    return UpdateFillingStation();
                    
                case OperatePhase.MovingToFuelSwitch:
                    return UpdateMovingToFuelSwitch();
                    
                case OperatePhase.AddingFuel:
                    return UpdateAddingFuel();
                    
                case OperatePhase.MovingToOreSwitch:
                    return UpdateMovingToOreSwitch();
                    
                case OperatePhase.AddingOre:
                    return UpdateAddingOre();
                    
                case OperatePhase.WaitingForOutput:
                    return UpdateWaitingForOutput();
                    
                case OperatePhase.MovingToPickupArea:
                    return UpdateMovingToPickupArea();
                    
                case OperatePhase.PickingUpOutput:
                    return UpdatePickingUpOutput();
                    
                case OperatePhase.CollectingOutput:
                    return UpdateCollectingOutput();
                    
                case OperatePhase.DepositingToChests:
                    return UpdateDepositingToChests();
                    
                // Kiln coordination phases
                case OperatePhase.FindingKiln:
                    return UpdateFindingKiln();
                    
                case OperatePhase.MovingToKiln:
                    return UpdateMovingToKiln();
                    
                case OperatePhase.FillingKiln:
                    return UpdateFillingKiln();
                    
                case OperatePhase.WaitingForKilnOutput:
                    return UpdateWaitingForKilnOutput();
                    
                case OperatePhase.CollectingKilnOutput:
                    return UpdateCollectingKilnOutput();
                    
                case OperatePhase.ReturningToSmelter:
                    return UpdateReturningToSmelter();
                    
                case OperatePhase.Complete:
                    NotifyOwner();
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Cancel
        
        public override void Cancel()
        {
            // Clear the status display
            CompanionChatHelper.ClearWorkingStatus(Companion);
            ReleaseStationVisuals();
            
            // Release occupancy when cancelled
            if (_targetSmelter != null)
            {
                InteractableOccupancyManager.Release(_targetSmelter.gameObject, _character);
            }
            
            // Also release kiln if we were using it
            if (_nearbyKiln != null)
            {
                InteractableOccupancyManager.Release(_nearbyKiln.gameObject, _character);
            }
            
            // End gathering session
            _autoPickup?.EndGatheringSession();
            
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();
            
            // CRITICAL: Release movement authority when cancelled
            // We don't release during normal stop-and-interact cycles, only at the end
            ReleaseMovementAuthorityFinal();
            
            NotifyOwner();
            base.Cancel();
        }
        
        protected override void Complete()
        {
            ReleaseStationVisuals();
            base.Complete();
        }
        
        public override void InterruptForCombat()
        {
            ReleaseStationVisuals();
            base.InterruptForCombat();
        }
        
        #endregion
        
        #region GetStatusDescription
        
        public override string GetStatusDescription()
        {
            string stationName = GetStationName();
            return _currentPhase switch
            {
                OperatePhase.MovingToStation => $"Walking to {stationName}",
                OperatePhase.PullingFromChests => "Getting materials from chests",
                OperatePhase.MovingToChestForPull => "Walking to chest for materials",
                OperatePhase.InteractingWithChestForPull => "Opening chest",
                OperatePhase.MovingToChestForDeposit => "Walking to chest to deposit",
                OperatePhase.InteractingWithChestForDeposit => "Storing items",
                OperatePhase.FillingStation => $"Checking {stationName}",
                OperatePhase.MovingToFuelSwitch => "Walking to add fuel",
                OperatePhase.AddingFuel => "Adding fuel",
                OperatePhase.MovingToOreSwitch => "Walking to add ore",
                OperatePhase.AddingOre => "Adding ore",
                OperatePhase.WaitingForOutput => $"Waiting for {stationName}",
                OperatePhase.MovingToPickupArea => "Walking to collect output",
                OperatePhase.PickingUpOutput => "Picking up items",
                OperatePhase.CollectingOutput => "Collecting output",
                OperatePhase.DepositingToChests => "Storing items in chests",
                OperatePhase.FindingKiln => "Looking for kiln",
                OperatePhase.MovingToKiln => "Walking to kiln",
                OperatePhase.FillingKiln => "Adding wood to kiln",
                OperatePhase.WaitingForKilnOutput => "Waiting for coal",
                OperatePhase.CollectingKilnOutput => "Collecting coal",
                OperatePhase.ReturningToSmelter => $"Returning to {stationName}",
                _ => $"Operating {stationName}"
            };
        }
        
        #endregion
    }
}
