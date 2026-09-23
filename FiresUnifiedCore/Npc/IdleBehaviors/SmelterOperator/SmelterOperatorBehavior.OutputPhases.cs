using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    // Output collection phases: Waiting, moving to pickup area, picking up, depositing
    public partial class SmelterOperatorBehavior
    {
        #region Waiting For Output
        
        private bool UpdateWaitingForOutput()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.Complete);
                return true;
            }
            
            // CRITICAL: Lock movement during waiting to prevent jitter from AI systems
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("SmelterOperator_WaitingForOutput", OutputWaitTime + 5f);
            }
            
            StopMovement();
            StopAllMovement(); // Also zero rigidbody velocity
            FaceTarget(_targetPosition);
            
            // Play random emotes while waiting to give personality
            TryPlayWaitingEmote();
            
            // Get the output point position - this is where bars/items spawn
            Vector3 outputPos = _targetSmelter.m_outputPoint != null 
                ? _targetSmelter.m_outputPoint.position 
                : _targetPosition;
            
            // Check EVERY FRAME for ground items near output point (bars pop out frequently)
            // This is more aggressive than the periodic check to ensure we don't miss items
            ItemDrop nearbyItem = FindNearestPickupItem(outputPos);
            if (nearbyItem != null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Detected item near output point - moving to pickup: {nearbyItem.m_itemData?.m_shared?.m_name}");
                
                // Unlock movement before transitioning
                _combatMovement?.UnlockMovement();
                
                // Set pickup target and move to it
                _currentPickupTarget = nearbyItem;
                _currentPickupTargetPosition = nearbyItem.transform.position;
                _pickupAreaPosition = _currentPickupTargetPosition;
                SetPhase(OperatePhase.MovingToPickupArea);
                MoveToPosition(_currentPickupTargetPosition);
                return false;
            }
            
            // Check if inventory is getting full - offload to chests
            if (IsInventoryNearlyFull())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Inventory nearly full during wait - offloading to chests");
                
                _combatMovement?.UnlockMovement();
                OffloadExcessInventory();
            }
            
            // Periodically check for other output conditions
            if (Time.time - _lastOperationCheck >= OperationCheckInterval)
            {
                _lastOperationCheck = Time.time;
                
                // Check ZDO output
                if (HasOutput())
                {
                    _combatMovement?.UnlockMovement();
                    SetPhase(OperatePhase.CollectingOutput);
                    return false;
                }
                
                // Also try to add more materials while waiting
                if (CanAddMore() && HasMaterials())
                {
                    _combatMovement?.UnlockMovement();
                    SetPhase(OperatePhase.FillingStation);
                    return false;
                }
                
                // Check if we have nothing left to do
                bool stillProcessing = IsProcessing();
                if (!stillProcessing)
                {
                    // Try to get more materials
                    if (HasMaterialsInChests())
                    {
                        _combatMovement?.UnlockMovement();
                        SetPhase(OperatePhase.PullingFromChests);
                        return false;
                    }
                    
                    // Truly done
                    _operationCycles++;
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Completed operation cycle {_operationCycles}");
                    
                    _combatMovement?.UnlockMovement();
                    SetPhase(OperatePhase.DepositingToChests);
                }
            }
            
            // Timeout on waiting (but only if nothing is processing)
            if (Time.time - _phaseStartTime > OutputWaitTime && !IsProcessing())
            {
                // Unlock movement before transitioning
                _combatMovement?.UnlockMovement();
                SetPhase(OperatePhase.DepositingToChests);
            }
            
            return false;
        }
        
        #endregion
        
        #region Moving To Pickup Area
        
        private bool UpdateMovingToPickupArea()
        {
            // If we don't have a current target, find the nearest item
            if (_currentPickupTarget == null || !_currentPickupTarget)
            {
                _currentPickupTarget = FindNearestPickupItem(_pickupAreaPosition);
                if (_currentPickupTarget != null)
                {
                    _currentPickupTargetPosition = _currentPickupTarget.transform.position;
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Found item to pickup: {_currentPickupTarget.m_itemData?.m_shared?.m_name} at {_currentPickupTargetPosition}");
                }
                else
                {
                    // No items found - go to pickup phase to check again
                    SetPhase(OperatePhase.PickingUpOutput);
                    return false;
                }
            }
            
            // Check if target was destroyed or picked up by someone else
            if (_currentPickupTarget == null || !_currentPickupTarget || !_currentPickupTarget.gameObject.activeInHierarchy)
            {
                _currentPickupTarget = null;
                // Find next item
                _currentPickupTarget = FindNearestPickupItem(_pickupAreaPosition);
                if (_currentPickupTarget != null)
                {
                    _currentPickupTargetPosition = _currentPickupTarget.transform.position;
                }
                else
                {
                    SetPhase(OperatePhase.PickingUpOutput);
                }
                return false;
            }
            
            float dist = Vector3.Distance(Transform.position, _currentPickupTargetPosition);
            
            if (dist < PickupDistance)
            {
                StopMovement();
                SetPhase(OperatePhase.PickingUpOutput);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            TryMoveToPosition(_currentPickupTargetPosition, walk: true, run: false);
            
            // Timeout - just proceed to pickup anyway
            if (Time.time - _phaseStartTime > 15f)
            {
                StopMovement();
                SetPhase(OperatePhase.PickingUpOutput);
            }
            
            return false;
        }
        
        #endregion
        
        #region Picking Up Output
        
        private bool UpdatePickingUpOutput()
        {
            StopMovement();
            
            // If we have a current target, face it and try to pick it up
            if (_currentPickupTarget != null && _currentPickupTarget && _currentPickupTarget.gameObject.activeInHierarchy)
            {
                FaceTarget(_currentPickupTargetPosition);
                
                // Try to pick up this specific item
                if (TryPickupSpecificItem(_currentPickupTarget))
                {
                    _itemsCollected++;
                    _smelterIsFullWaitingForOutput = false;
                    PlayInteractAnimation();
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Picked up item at {_currentPickupTargetPosition}");
                }
                
                // Clear the target (either picked up or failed)
                _currentPickupTarget = null;
            }
            else
            {
                FaceTarget(_pickupAreaPosition);
            }
            
            // Also try to pick up any items within immediate reach
            int picked = PickupNearbyGroundItems(Transform.position);
            if (picked > 0)
            {
                _smelterIsFullWaitingForOutput = false;
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Picked up {picked} nearby ground items");
            }
            
            // Brief pause before looking for more items
            if (Time.time - _phaseStartTime > 0.5f)
            {
                // Find the next item to pick up - search in wider area around the station
                Vector3 searchCenter = _targetSmelter != null ? _targetPosition : _pickupAreaPosition;
                ItemDrop nextItem = FindNearestPickupItem(searchCenter);
                
                if (nextItem != null)
                {
                    // More items to collect - move to the next one
                    _currentPickupTarget = nextItem;
                    _currentPickupTargetPosition = nextItem.transform.position;
                    _pickupAreaPosition = _currentPickupTargetPosition;
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Found next item to pickup at {_currentPickupTargetPosition}");
                    
                    SetPhase(OperatePhase.MovingToPickupArea);
                    MoveToPosition(_currentPickupTargetPosition);
                    return false;
                }
                
                // No more items - check if station is still processing
                _currentPickupTarget = null;
                
                if (IsProcessing())
                {
                    SetPhase(OperatePhase.WaitingForOutput);
                    MoveToPosition(_targetPosition);
                }
                else if (CanAddMore() && (HasMaterials() || HasMaterialsInChests()))
                {
                    // Can add more materials
                    _operationCycles++;
                    SetPhase(OperatePhase.PullingFromChests);
                    MoveToPosition(_targetPosition);
                }
                else
                {
                    // Done - deposit and complete
                    SetPhase(OperatePhase.DepositingToChests);
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Collecting Output
        
        private bool UpdateCollectingOutput()
        {
            if (_targetSmelter == null)
            {
                SetPhase(OperatePhase.DepositingToChests);
                return false;
            }
            
            StopMovement();
            FaceTarget(_targetPosition);
            
            // First, try to pickup ground items near output point
            Vector3 outputPos = _targetSmelter.m_outputPoint != null 
                ? _targetSmelter.m_outputPoint.position 
                : _targetPosition;
            int picked = PickupNearbyGroundItems(outputPos);
            
            // ANTI-SPAM: We picked up output, so reset the "full waiting for output" flag
            if (picked > 0)
            {
                _smelterIsFullWaitingForOutput = false;
            }
            
            // Empty output the station holds (windmill stack)
            if (TryCollectOutput())
            {
                _itemsCollected++;
                _smelterIsFullWaitingForOutput = false; // Also reset here
                PlayInteractAnimation();
            }
            
            // Move on once the held output is out, or if the station's owner never answers the empty request
            if (!HasOutput() || Time.time - _phaseStartTime > OutputWaitTime)
            {
                // Check for more ground items one more time
                PickupNearbyGroundItems(outputPos);
                
                // Continue the cycle - fill more or deposit
                if (CanAddMore() && (HasMaterials() || HasMaterialsInChests()))
                {
                    _operationCycles++;
                    SetPhase(OperatePhase.PullingFromChests);
                }
                else
                {
                    SetPhase(OperatePhase.DepositingToChests);
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Depositing To Chests
        
        private bool UpdateDepositingToChests()
        {
            StopMovement();
            FaceTarget(_targetPosition);
            
            // Check if we should use physical chest interaction for deposit
            if (UsePhysicalChestInteraction && _nearbyChests.Count > 0)
            {
                if (TryStartPhysicalChestDeposit())
                {
                    return false; // Started physical interaction
                }
                // If no physical deposit needed/possible, fall through to instant
            }
            
            // Instant deposit (fallback or when physical interaction disabled)
            DepositToChests();
            
            return FinishDepositAndContinue();
        }
        
        /// <summary>
        /// Tries to start a physical chest deposit operation.
        /// Returns true if started, false if nothing to deposit or no chest found.
        /// </summary>
        private bool TryStartPhysicalChestDeposit()
        {
            // Check if we have items to deposit
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null) return false;
            
            var depositableItems = ChestHelper.GetDepositableItems(storageInv);
            if (depositableItems.Count == 0) return false;
            
            // Find best chest to deposit to
            _targetChestForDeposit = ChestHelper.FindBestDepositChest(Transform.position, _nearbyChests, storageInv);
            if (_targetChestForDeposit == null)
            {
                // Try any chest with room
                _targetChestForDeposit = ChestHelper.FindClosestChestWithRoom(Transform.position, CHEST_SEARCH_RADIUS);
            }
            
            if (_targetChestForDeposit == null) return false;
            
            // Check if we're already close enough
            float distToChest = Vector3.Distance(Transform.position, _targetChestForDeposit.transform.position);
            if (distToChest <= InteractionDistance)
            {
                SetPhase(OperatePhase.InteractingWithChestForDeposit);
            }
            else
            {
                SetPhase(OperatePhase.MovingToChestForDeposit);
                MoveToPosition(_targetChestForDeposit.transform.position);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Starting physical deposit to chest at {_targetChestForDeposit.transform.position}");
            
            return true;
        }
        
        /// <summary>
        /// Update phase: Moving to chest to deposit items.
        /// </summary>
        private bool UpdateMovingToChestForDeposit()
        {
            if (_targetChestForDeposit == null)
            {
                // Chest disappeared - fall back to instant deposit
                DepositToChests();
                return FinishDepositAndContinue();
            }
            
            float dist = Vector3.Distance(Transform.position, _targetChestForDeposit.transform.position);
            
            if (dist <= InteractionDistance)
            {
                StopMovement();
                SetPhase(OperatePhase.InteractingWithChestForDeposit);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            TryMoveToPosition(_targetChestForDeposit.transform.position, walk: true, run: false);
            
            // Timeout check
            if (Time.time - _phaseStartTime > 30f)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Timeout walking to deposit chest - using instant deposit");
                StopMovement();
                DepositToChests();
                return FinishDepositAndContinue();
            }
            
            return false;
        }
        
        /// <summary>
        /// Update phase: Interacting with chest (open animation, deposit items, close).
        /// Now properly shows chest opening and closing for all nearby clients.
        /// </summary>
        private bool UpdateInteractingWithChestForDeposit()
        {
            if (_targetChestForDeposit == null)
            {
                return FinishDepositAndContinue();
            }
            
            StopMovement();
            FaceTarget(_targetChestForDeposit.transform.position);
            
            // Phase timing:
            // 0.0 - 0.1: Open chest + play animation
            // 0.1 - 0.6: Wait for open animation
            // 0.6 - 0.7: Deposit items
            // 0.7 - 1.2: Wait for close animation
            // 1.2+: Done, move on
            
            float phaseTime = Time.time - _phaseStartTime;
            
            // At start: Play interact animation and OPEN chest visually
            if (phaseTime < 0.1f)
            {
                PlayInteractAnimation();
                TryOpenChestVisually(_targetChestForDeposit);
                return false;
            }
            
            // Wait for chest open animation
            if (phaseTime < 0.6f)
            {
                return false;
            }
            
            // Deposit items (only once, around 0.6-0.7s mark)
            if (phaseTime >= 0.6f && phaseTime < 0.7f)
            {
                var storageInv = _inventory?.GetStorageInventory();
                if (storageInv != null)
                {
                    // Deposit all depositable items to this chest (with overflow to others)
                    var result = InventoryTransferService.DepositAllDepositable(_inventory, _nearbyChests);
                    
                    if (result.Success)
                    {
                        _itemsDeposited += result.AmountTransferred;
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Deposited {result.AmountTransferred} items to chests");
                        
                        Events.CompanionEvents.FireItemDeposited(Companion, "various", result.AmountTransferred);
                    }
                }
                return false;
            }
            
            // Close chest (around 0.7s mark)
            if (phaseTime >= 0.7f && phaseTime < 0.8f)
            {
                TryCloseChestVisually(_targetChestForDeposit);
                return false;
            }
            
            // Wait for close animation
            if (phaseTime < 1.2f)
            {
                return false;
            }
            
            // Clear deposit target
            _targetChestForDeposit = null;
            
            // Return to smelter and continue operation
            return FinishDepositAndContinue();
        }
        
        /// <summary>
        /// Finishes deposit phase and decides whether to continue operation.
        /// </summary>
        private bool FinishDepositAndContinue()
        {
            // CONTINUOUS OPERATION: Check if we should continue
            // Only stop if:
            // 1. No more materials in chests or inventory
            // 2. Smelter is full and processing
            
            bool hasMaterials = HasMaterialsInChests() || HasMaterials();
            bool canAddMore = CanAddMore();
            
            if (hasMaterials && canAddMore)
            {
                // Continue the loop!
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Continuing operation - materials available, station can accept more");
                
                // Return to station first
                SetPhase(OperatePhase.MovingToStation);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // Check if station is still processing - wait for it
            if (IsProcessing())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Station still processing - waiting for output");
                
                SetPhase(OperatePhase.MovingToStation);
                MoveToPosition(_targetPosition);
                return false;
            }
            
            // Truly complete - no more materials and nothing processing
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Operation complete after {_operationCycles} cycles");
            
            SetPhase(OperatePhase.Complete);
            return true;
        }
        
        #endregion
        
        #region Inventory Helpers
        
        /// <summary>
        /// Checks if companion's inventory is nearly full (>80% weight capacity or <3 free slots).
        /// </summary>
        private bool IsInventoryNearlyFull()
        {
            if (_inventory == null) return false;
            return ChestHelper.IsInventoryFullOrNearlyFull(_inventory);
        }
        
        /// <summary>
        /// Gets detailed inventory status for decision making.
        /// Uses ResourceAccessService when available for consistent behavior.
        /// </summary>
        private new ChestHelper.InventoryStatus GetInventoryStatus()
        {
            if (_resources != null)
            {
                return _resources.GetInventoryStatus();
            }
            return ChestHelper.CheckInventoryStatus(_inventory);
        }
        
        /// <summary>
        /// Checks if inventory is completely full and needs immediate deposit.
        /// </summary>
        private new bool IsInventoryCompletelyFull()
        {
            var status = ChestHelper.CheckInventoryStatus(_inventory);
            return status.IsFull || status.IsOverweight;
        }
        
        /// <summary>
        /// Offloads excess items (non-essential) to nearby chests.
        /// Keeps food, weapons, and essential crafting materials.
        /// Uses the new ChestHelper.SmartDeposit for intelligent stacking.
        /// </summary>
        private void OffloadExcessInventory()
        {
            if (_inventory == null || _nearbyChests.Count == 0) return;
            
            // Use the new smart deposit which handles stacking automatically
            var result = ChestHelper.SmartDeposit(_inventory, Transform.position, ChestHelper.DEPOSIT_SEARCH_RADIUS, _nearbyChests);
            
            if (result.Success)
            {
                _itemsDeposited += result.ItemsDeposited;
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {result.Message}");
            }
        }
        
        /// <summary>
        /// Deposits one carried stack into a nearby chest the companion may write, preferring one that already holds it.
        /// </summary>
        private bool TryDepositItem(ItemDrop.ItemData item, Inventory fromInventory)
        {
            if (item == null || fromInventory == null) return false;

            var preferred = ChestHelper.FindChestWithItem(_nearbyChests, item);
            if (preferred != null && TryDepositInto(preferred, item, fromInventory)) return true;

            foreach (var container in _nearbyChests)
            {
                if (container != null && container != preferred && TryDepositInto(container, item, fromInventory))
                    return true;
            }
            return false;
        }

        private bool TryDepositInto(Container chest, ItemDrop.ItemData item, Inventory fromInventory)
        {
            if (!ChestHelper.TryClaimForWrite(chest, Companion)) return false;
            var containerInv = chest.GetInventory();
            if (containerInv == null || !containerInv.CanAddItem(item)) return false;
            int stack = item.m_stack;
            return ChestHelper.MoveItem(fromInventory, containerInv, item, stack) == stack;
        }
        
        #endregion
    }
}
