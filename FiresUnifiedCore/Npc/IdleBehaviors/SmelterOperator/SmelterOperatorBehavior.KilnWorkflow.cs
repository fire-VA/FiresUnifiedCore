using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    // Kiln coordination phases: Finding kiln, filling, waiting, collecting coal, returning
    public partial class SmelterOperatorBehavior
    {
        #region Finding Kiln
        
        private bool UpdateFindingKiln()
        {
            _nearbyKiln = FindNearbyKiln();
            
            if (_nearbyKiln == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] No nearby kiln found - cannot produce coal");
                
                // No kiln - return to waiting
                SetPhase(OperatePhase.WaitingForOutput);
                return false;
            }
            
            // Found a kiln - move to it
            SetPhase(OperatePhase.MovingToKiln);
            MoveToPosition(_nearbyKiln.transform.position);
            
            return false;
        }
        
        #endregion
        
        #region Moving To Kiln
        
        private bool UpdateMovingToKiln()
        {
            if (_nearbyKiln == null)
            {
                SetPhase(OperatePhase.WaitingForOutput);
                return false;
            }
            
            float dist = Vector3.Distance(Transform.position, _nearbyKiln.transform.position);
            
            if (dist < INTERACTION_DISTANCE)
            {
                StopMovement();
                
                // Occupy the kiln
                if (!InteractableOccupancyManager.TryOccupy(_nearbyKiln.gameObject, _character, 120f))
                {
                    SetPhase(OperatePhase.WaitingForOutput);
                    return false;
                }
                
                SetPhase(OperatePhase.FillingKiln);
                return false;
            }
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            TryMoveToPosition(_nearbyKiln.transform.position, walk: true, run: false);
            
            // Timeout
            if (Time.time - _phaseStartTime > 20f)
            {
                SetPhase(OperatePhase.WaitingForOutput);
            }
            
            return false;
        }
        
        #endregion
        
        #region Filling Kiln
        
        private bool UpdateFillingKiln()
        {
            if (_nearbyKiln == null)
            {
                SetPhase(OperatePhase.ReturningToSmelter);
                return false;
            }
            
            StopMovement();
            FaceTarget(_nearbyKiln.transform.position);
            
            // Fill kiln with wood
            bool addedAnything = false;
            int addedCount = 0;
            
            while (addedCount < 5) // Limit per frame
            {
                if (TryAddWoodToKiln())
                {
                    _itemsAdded++;
                    addedCount++;
                    addedAnything = true;
                    PlayInteractAnimation();
                }
                else
                {
                    break;
                }
            }
            
            if (addedAnything && CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Added {addedCount} wood to kiln");
            
            // Check if kiln is full or we're out of wood
            if (!CanAddToKiln() || !HasWoodForKiln())
            {
                // Wait for kiln output
                SetPhase(OperatePhase.WaitingForKilnOutput);
            }
            
            return false;
        }
        
        /// <summary>
        /// Tries to add wood to the kiln.
        /// </summary>
        private bool TryAddWoodToKiln()
        {
            if (_nearbyKiln == null || _inventory == null) return false;
            
            var storageInv = _inventory.GetStorageInventory();
            if (storageInv == null) return false;
            
            var nview = _nearbyKiln.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // Check kiln capacity
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            if (queued >= _nearbyKiln.m_maxOre) return false;
            
            // Find wood in inventory
            string[] woodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
            
            foreach (var item in storageInv.GetAllItems())
            {
                if (item == null) continue;
                if (item.m_stack <= 0) continue; // Skip empty stacks
                
                string dropName = item.m_dropPrefab?.name ?? "";
                if (string.IsNullOrEmpty(dropName)) continue;
                
                foreach (string woodType in woodTypes)
                {
                    if (dropName.Equals(woodType, System.StringComparison.OrdinalIgnoreCase))
                    {
                        // CRITICAL FIX: Remove from inventory FIRST, then call RPC
                        // This matches TryAddOre() behavior and prevents item duplication
                        string itemName = item.m_shared?.m_name ?? dropName;
                        int stackBefore = item.m_stack;
                        
                        if (!storageInv.RemoveOneItem(item))
                        {
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.LogWarning($"[SmelterOperator] Failed to remove wood from inventory: {itemName}");
                            continue; // Try next item
                        }
                        
                        // Successfully removed from inventory - now add to kiln via RPC
                        nview.InvokeRPC("RPC_AddOre", dropName);
                        _inventory.SaveToZDO();
                        
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Added wood to kiln: {itemName} (stack was {stackBefore}, now {stackBefore - 1})");
                        
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if kiln can accept more wood.
        /// </summary>
        private bool CanAddToKiln()
        {
            if (_nearbyKiln == null) return false;
            
            var nview = _nearbyKiln.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            return queued < _nearbyKiln.m_maxOre;
        }
        
        #endregion
        
        #region Waiting For Kiln Output
        
        private bool UpdateWaitingForKilnOutput()
        {
            if (_nearbyKiln == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Kiln became null during waiting - returning to smelter");
                SetPhase(OperatePhase.ReturningToSmelter);
                return false;
            }
            
            // CRITICAL: Lock movement during waiting to prevent jitter from AI systems
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("SmelterOperator_WaitingForKilnOutput", OUTPUT_WAIT_TIME + 5f);
            }
            
            StopMovement();
            StopAllMovement(); // Also zero rigidbody velocity
            FaceTarget(_nearbyKiln.transform.position);
            
            // Play random emotes while waiting to give personality
            TryPlayWaitingEmote();
            
            // IMPORTANT: Check for smelter output periodically during kiln workflow
            // This prevents ignoring smelter output while tending the kiln
            if (_primarySmelter != null && Time.time - _lastSmelterOutputCheck >= SMELTER_OUTPUT_CHECK_INTERVAL)
            {
                _lastSmelterOutputCheck = Time.time;
                
                Vector3 smelterOutputPos = _primarySmelter.m_outputPoint != null 
                    ? _primarySmelter.m_outputPoint.position 
                    : _primarySmelterPosition;
                
                int smelterItems = CountNearbyGroundItems(smelterOutputPos);
                if (smelterItems > 0)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Detected {smelterItems} items near SMELTER output while waiting at kiln - will collect after kiln");
                    
                    // Don't interrupt kiln workflow, but mark that we have smelter output waiting
                    // We'll handle it when returning to smelter
                }
            }
            
            // Check for ground items (coal) near kiln output EVERY UPDATE
            Vector3 kilnOutputPos = _nearbyKiln.m_outputPoint != null 
                ? _nearbyKiln.m_outputPoint.position 
                : _nearbyKiln.transform.position;
            
            ItemDrop nearbyItem = FindNearestPickupItem(kilnOutputPos);
            if (nearbyItem != null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Detected item near kiln output - moving to pickup: {nearbyItem.m_itemData?.m_shared?.m_name}");
                
                // Unlock movement before transitioning
                _combatMovement?.UnlockMovement();
                
                // Move to pickup the specific item
                _currentPickupTarget = nearbyItem;
                _currentPickupTargetPosition = nearbyItem.transform.position;
                _pickupAreaPosition = _currentPickupTargetPosition;
                SetPhase(OperatePhase.CollectingKilnOutput);
                MoveToPosition(_currentPickupTargetPosition);
                return false;
            }
            
            // Periodically check if kiln is done
            if (Time.time - _lastOperationCheck >= OPERATION_CHECK_INTERVAL)
            {
                _lastOperationCheck = Time.time;
                
                // Check if kiln is still processing
                var nview = _nearbyKiln.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
                    if (queued == 0)
                    {
                        // Kiln is done - check for ground items one more time
                        int groundItems = CountNearbyGroundItems(kilnOutputPos);
                        if (groundItems > 0)
                        {
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[SmelterOperator] Kiln done, {groundItems} items on ground - collecting");
                            _combatMovement?.UnlockMovement();
                            SetPhase(OperatePhase.CollectingKilnOutput);
                            return false;
                        }
                        else
                        {
                            // Truly done
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[SmelterOperator] Kiln done, no items on ground - returning to smelter");
                            _combatMovement?.UnlockMovement();
                            SetPhase(OperatePhase.ReturningToSmelter);
                            return false;
                        }
                    }
                    else if (CompanionIdleBehavior.VerboseLogging)
                    {
                        Debug.Log($"[SmelterOperator] Kiln still processing: {queued} items queued");
                    }
                }
            }
            
            // FAILSAFE: Timeout - don't wait forever
            if (Time.time - _phaseStartTime > OUTPUT_WAIT_TIME)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Kiln wait timeout - forcing collection check");
                
                // Unlock movement before transitioning
                _combatMovement?.UnlockMovement();
                
                // Try to collect any items that might be there
                int groundItems = CountNearbyGroundItems(kilnOutputPos);
                if (groundItems > 0)
                {
                    SetPhase(OperatePhase.CollectingKilnOutput);
                }
                else
                {
                    // Just return to smelter
                    SetPhase(OperatePhase.ReturningToSmelter);
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Collecting Kiln Output
        
        private bool UpdateCollectingKilnOutput()
        {
            // Get kiln output position - use kiln position if kiln is still valid
            Vector3 kilnOutputPos;
            if (_nearbyKiln != null)
            {
                kilnOutputPos = _nearbyKiln.m_outputPoint != null 
                    ? _nearbyKiln.m_outputPoint.position 
                    : _nearbyKiln.transform.position;
            }
            else
            {
                // Kiln became null - try using pickup area position or return to smelter
                if (_pickupAreaPosition != Vector3.zero)
                {
                    kilnOutputPos = _pickupAreaPosition;
                }
                else
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Kiln null and no pickup position - returning to smelter");
                    SetPhase(OperatePhase.ReturningToSmelter);
                    MoveToPosition(_targetPosition);
                    return false;
                }
            }
            
            // If we have a current target, move to it and pick it up
            if (_currentPickupTarget != null && _currentPickupTarget && _currentPickupTarget.gameObject.activeInHierarchy)
            {
                float dist = Vector3.Distance(Transform.position, _currentPickupTargetPosition);
                
                if (dist > PICKUP_DISTANCE)
                {
                    // CRITICAL FIX (Bug #11): Continue movement every frame!
                    TryMoveToPosition(_currentPickupTargetPosition, walk: true, run: false);
                    
                    // FAILSAFE: Timeout moving to single item
                    if (Time.time - _phaseStartTime > 15f)
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Timeout moving to kiln output item - trying nearby pickup");
                        _currentPickupTarget = null;
                    }
                    return false;
                }
                
                StopMovement();
                FaceTarget(_currentPickupTargetPosition);
                
                // Try to pick up this specific item
                if (TryPickupSpecificItem(_currentPickupTarget))
                {
                    _itemsCollected++;
                    _smelterIsFullWaitingForOutput = false;
                    PlayInteractAnimation();
                    
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] Picked up kiln output item - coal collected!");
                }
                
                _currentPickupTarget = null;
            }
            else
            {
                FaceTarget(kilnOutputPos);
            }
            
            // Also pick up any items within immediate reach
            int pickedUp = PickupNearbyGroundItems(Transform.position);
            if (pickedUp > 0)
            {
                _smelterIsFullWaitingForOutput = false;
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] Picked up {pickedUp} nearby items from kiln area");
            }
            
            // Find more items near kiln (search wider area)
            ItemDrop nextItem = FindNearestPickupItem(kilnOutputPos);
            if (nextItem != null)
            {
                _currentPickupTarget = nextItem;
                _currentPickupTargetPosition = nextItem.transform.position;
                MoveToPosition(_currentPickupTargetPosition);
                _phaseStartTime = Time.time; // Reset timeout for new item
                return false;
            }
            
            // No more items - check if we have coal now
            bool hasCoal = HasFuel();
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator] Kiln collection complete - HasCoal={hasCoal}");
            
            // Release kiln occupancy
            if (_nearbyKiln != null)
            {
                InteractableOccupancyManager.Release(_nearbyKiln.gameObject, _character);
            }
            _nearbyKiln = null;
            _currentPickupTarget = null;
            
            // Return to smelter
            SetPhase(OperatePhase.ReturningToSmelter);
            MoveToPosition(_targetPosition);
            
            return false;
        }
        
        #endregion
        
        #region Returning To Smelter
        
        private bool UpdateReturningToSmelter()
        {
            // CRITICAL: Use the PRIMARY smelter position, not the current _targetPosition
            // which may have been changed during kiln workflow
            Vector3 smelterPos = _primarySmelterPosition != Vector3.zero ? _primarySmelterPosition : _targetPosition;
            
            // Restore the target smelter reference if we have the primary
            if (_primarySmelter != null && _targetSmelter != _primarySmelter)
            {
                _targetSmelter = _primarySmelter;
                _targetPosition = _primarySmelterPosition;
                
                // Re-detect switch positions for the smelter
                DetectSwitchPositions();
            }
            
            float dist = Vector3.Distance(Transform.position, smelterPos);
            
            // CRITICAL FIX (Bug #11): Continue movement every frame!
            if (dist > INTERACTION_DISTANCE)
            {
                TryMoveToPosition(smelterPos, walk: true, run: false);
            }
            
            if (dist < INTERACTION_DISTANCE)
            {
                StopMovement();
                
                // Check for smelter output first before trying to fill more
                if (_targetSmelter != null)
                {
                    Vector3 smelterOutputPos = _targetSmelter.m_outputPoint != null 
                        ? _targetSmelter.m_outputPoint.position 
                        : _targetPosition;
                    
                    int outputItems = CountNearbyGroundItems(smelterOutputPos);
                    if (outputItems > 0)
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator] Detected {outputItems} items at smelter output after returning - collecting first");
                        
                        _pickupAreaPosition = smelterOutputPos;
                        SetPhase(OperatePhase.MovingToPickupArea);
                        MoveToPosition(_pickupAreaPosition);
                        return false;
                    }
                }
                
                // Clear primary smelter reference since we're back at the smelter
                _primarySmelter = null;
                _primarySmelterPosition = Vector3.zero;
                
                // Continue filling smelter
                SetPhase(OperatePhase.FillingStation);
                return false;
            }
            
            // Timeout - teleport if needed
            if (Time.time - _phaseStartTime > 20f)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} timeout returning to smelter - teleporting");
                
                Vector3 teleportPos = FindSafeTeleportPosition(smelterPos);
                Transform.position = teleportPos;
                
                var nview = Companion?.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    nview.GetZDO().SetPosition(teleportPos);
                }
                
                _primarySmelter = null;
                _primarySmelterPosition = Vector3.zero;
                SetPhase(OperatePhase.FillingStation);
            }
            
            return false;
        }
        
        #endregion
    }
}
