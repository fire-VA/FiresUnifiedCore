using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.AI;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Service for physically interacting with chests - pathfinding to them, opening, 
    /// pulling/depositing items, and returning.
    /// 
    /// This is the PHYSICAL INTERACTION layer on top of ResourceAccessService.
    /// - ResourceAccessService: Data operations (find chests, check items, move items between inventories)
    /// - ChestInteractionService: Physical operations (pathfind to chest, open, interact, return)
    /// 
    /// CHEST INTERACTION FLOW:
    /// 1. FindingChest: Locate a chest with the needed items (via ContainerRegistry)
    /// 2. MovingToChest: Pathfind using vanilla BaseAI.MoveTo() through CompanionAI
    /// 3. OpeningChest: Face chest, play interact animation, set ZDO "InUse" flag
    /// 4. Interacting: Transfer items between inventories
    /// 5. ClosingChest: Clear ZDO "InUse" flag, wait for close animation
    /// 6. ReturningToOrigin: Pathfind back to starting position
    /// 
    /// PROPER CHEST OPEN/CLOSE:
    /// - Uses ZDO.Set(ZDOVars.s_inUse, 1) to mark chest as "in use" (prevents other players)
    /// - Uses ZDO.Set(ZDOVars.s_inUse, 0) to release the chest when done
    /// - This is the same mechanism Valheim uses when players open containers
    /// 
    /// PATHFINDING:
    /// - Uses CompanionAI.RequestPathfindingMovement() which wraps vanilla BaseAI.MoveTo()
    /// - This ensures proper obstacle avoidance using Valheim's NavMesh system
    /// - Movement authority is coordinated via UnifiedMovementAuthority
    /// 
    /// USAGE IN BEHAVIORS:
    /// 1. Create ChestInteractionService with companion references
    /// 2. Call StartPullOperation() or StartDepositOperation() to begin
    /// 3. Call Update() every frame - returns true when complete
    /// 4. Check LastResult for what happened
    /// 
    /// WORKFLOW EXAMPLE (SmelterOperator needs coal):
    /// 1. SmelterOperator detects it needs coal from chests
    /// 2. Calls chestService.StartPullOperation("Coal", 10)
    /// 3. ChestInteractionService:
    ///    a. Finds nearest chest with coal (via ContainerRegistry)
    ///    b. Pathfinds to chest (using vanilla MoveTo)
    ///    c. Opens chest (sets ZDO InUse flag)
    ///    d. Pulls coal from chest inventory
    ///    e. Closes chest (clears ZDO InUse flag)
    ///    f. Returns to original position
    /// 4. SmelterOperator continues with coal in inventory
    /// </summary>
    public class ChestInteractionService
    {
        #region Enums
        
        /// <summary>
        /// Current phase of chest interaction.
        /// </summary>
        public enum InteractionPhase
        {
            Idle,                   // Not doing anything
            FindingChest,           // Looking for a chest with required items
            MovingToChest,          // Pathfinding to the chest
            OpeningChest,           // Playing open animation
            Interacting,            // Pulling or depositing items
            ClosingChest,           // Playing close animation (optional)
            ReturningToOrigin,      // Pathfinding back to original position
            Complete,               // Operation finished
            Failed                  // Operation failed
        }
        
        /// <summary>
        /// Type of chest operation.
        /// </summary>
        public enum OperationType
        {
            None,
            Pull,       // Pull items FROM chest TO inventory
            Deposit,    // Deposit items FROM inventory TO chest
            PullMultiple,  // Pull multiple item types
            DepositAll     // Deposit all depositable items
        }
        
        #endregion
        
        #region Result Class
        
        /// <summary>
        /// Result of a chest interaction operation.
        /// </summary>
        public class InteractionResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public int ItemsTransferred { get; set; }
            public string ItemPrefabName { get; set; }
            public Container ChestUsed { get; set; }
            public float Duration { get; set; }
            public InteractionPhase FinalPhase { get; set; }
            
            public static InteractionResult Succeeded(int items, string prefab, Container chest, float duration)
            {
                return new InteractionResult
                {
                    Success = true,
                    Message = $"Transferred {items}x {prefab}",
                    ItemsTransferred = items,
                    ItemPrefabName = prefab,
                    ChestUsed = chest,
                    Duration = duration,
                    FinalPhase = InteractionPhase.Complete
                };
            }
            
            public static InteractionResult Failed(string reason, InteractionPhase phase)
            {
                return new InteractionResult
                {
                    Success = false,
                    Message = reason,
                    FinalPhase = phase
                };
            }
        }
        
        #endregion
        
        #region Fields
        
        // Core references
        private readonly CompanionController _companion;
        private readonly CompanionAI _companionAI;
        private readonly CompanionInventory _inventory;
        private readonly ResourceAccessService _resources;
        private readonly string _ownerName;
        
        // Operation state
        private OperationType _currentOperation = OperationType.None;
        private InteractionPhase _currentPhase = InteractionPhase.Idle;
        private float _operationStartTime;
        private float _phaseStartTime;
        
        // Target tracking
        private Container _targetChest;
        private ZNetView _targetChestNView;
        private Vector3 _targetChestPosition;
        private Vector3 _returnPosition;
        private bool _shouldReturnToOrigin = true;
        private bool _chestWasOpened = false;  // Track if we opened the chest (so we can close it)
        
        // Operation parameters
        private string _targetItemPrefab;
        private string[] _targetItemPrefabs;
        private int _targetAmount;
        private int _itemsTransferred;
        
        // Interaction point - calculated position to stand at (in front of chest)
        private Vector3 _interactionPoint;
        
        // Timing constants
        private const float OPEN_ANIMATION_DURATION = 0.8f;   // Time for chest lid to open
        private const float CLOSE_ANIMATION_DURATION = 0.5f;  // Time for chest lid to close
        private const float INTERACT_DURATION = 0.3f;         // Time to transfer items
        private const float PHASE_TIMEOUT = 30f;
        private const float MOVEMENT_TIMEOUT = 60f;
        
        // Logging
        public bool VerboseLogging { get; set; } = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Current phase of the interaction.
        /// </summary>
        public InteractionPhase CurrentPhase => _currentPhase;
        
        /// <summary>
        /// Whether an operation is currently in progress.
        /// </summary>
        public bool IsOperationInProgress => _currentPhase != InteractionPhase.Idle && 
                                              _currentPhase != InteractionPhase.Complete && 
                                              _currentPhase != InteractionPhase.Failed;
        
        /// <summary>
        /// The current operation type.
        /// </summary>
        public OperationType CurrentOperation => _currentOperation;
        
        /// <summary>
        /// The last result (available after Complete or Failed).
        /// </summary>
        public InteractionResult LastResult { get; private set; }
        
        /// <summary>
        /// Time spent in current phase.
        /// </summary>
        public float TimeInCurrentPhase => Time.time - _phaseStartTime;
        
        /// <summary>
        /// Total time spent on current operation.
        /// </summary>
        public float TotalOperationTime => Time.time - _operationStartTime;
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// Creates a new ChestInteractionService.
        /// </summary>
        /// <param name="companion">The companion controller</param>
        /// <param name="resources">The resource access service (for chest scanning and item operations)</param>
        /// <param name="ownerName">Name for logging (usually behavior name)</param>
        public ChestInteractionService(CompanionController companion, ResourceAccessService resources, string ownerName)
        {
            _companion = companion;
            _companionAI = companion?.GetComponent<CompanionAI>();
            _inventory = companion?.GetComponent<CompanionInventory>();
            _resources = resources;
            _ownerName = ownerName;
        }
        
        #endregion
        
        #region Public API - Start Operations
        
        /// <summary>
        /// Starts an operation to pull items from a nearby chest.
        /// </summary>
        /// <param name="itemPrefab">The item prefab name to pull</param>
        /// <param name="amount">Maximum amount to pull</param>
        /// <param name="returnToOrigin">Whether to return to starting position after</param>
        /// <returns>True if operation started, false if cannot start</returns>
        public bool StartPullOperation(string itemPrefab, int amount, bool returnToOrigin = true)
        {
            if (IsOperationInProgress)
            {
                Log($"Cannot start pull - operation already in progress");
                return false;
            }
            
            if (_companionAI == null)
            {
                Log($"Cannot start pull - no CompanionAI");
                return false;
            }
            
            // Refresh chest list
            _resources.RefreshNearbyChests(true);
            
            // Find a chest with the item
            _targetChest = FindChestWithItem(itemPrefab);
            if (_targetChest == null)
            {
                Log($"No chest found with {itemPrefab}");
                return false;
            }
            
            // Initialize operation
            _currentOperation = OperationType.Pull;
            _targetItemPrefab = itemPrefab;
            _targetAmount = amount;
            _itemsTransferred = 0;
            _shouldReturnToOrigin = returnToOrigin;
            _returnPosition = _companion.transform.position;
            _targetChestPosition = _targetChest.transform.position;
            
            // Calculate proper interaction point (in front of chest, not behind it)
            _interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, _companion.transform.position);
            
            _operationStartTime = Time.time;
            
            SetPhase(InteractionPhase.MovingToChest);
            
            Log($"Starting PULL operation: {amount}x {itemPrefab} from chest at {_targetChestPosition}, standing at {_interactionPoint}");
            return true;
        }
        
        /// <summary>
        /// Starts an operation to pull the first available item from a list of prefabs.
        /// </summary>
        public bool StartPullFirstAvailableOperation(string[] itemPrefabs, int amount, bool returnToOrigin = true)
        {
            if (IsOperationInProgress) return false;
            if (_companionAI == null) return false;
            
            _resources.RefreshNearbyChests(true);
            
            // Find first available item
            foreach (string prefab in itemPrefabs)
            {
                _targetChest = FindChestWithItem(prefab);
                if (_targetChest != null)
                {
                    _targetItemPrefab = prefab;
                    break;
                }
            }
            
            if (_targetChest == null)
            {
                Log($"No chest found with any of: {string.Join(", ", itemPrefabs)}");
                return false;
            }
            
            _currentOperation = OperationType.PullMultiple;
            _targetItemPrefabs = itemPrefabs;
            _targetAmount = amount;
            _itemsTransferred = 0;
            _shouldReturnToOrigin = returnToOrigin;
            _returnPosition = _companion.transform.position;
            _targetChestPosition = _targetChest.transform.position;
            
            // Calculate proper interaction point (in front of chest)
            _interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, _companion.transform.position);
            
            _operationStartTime = Time.time;
            
            SetPhase(InteractionPhase.MovingToChest);
            
            Log($"Starting PULL operation: {amount}x {_targetItemPrefab}, standing at {_interactionPoint}");
            return true;
        }
        
        /// <summary>
        /// Starts an operation to deposit items to a nearby chest.
        /// </summary>
        /// <param name="itemPrefab">The item prefab name to deposit</param>
        /// <param name="returnToOrigin">Whether to return to starting position after</param>
        /// <returns>True if operation started, false if cannot start</returns>
        public bool StartDepositOperation(string itemPrefab, bool returnToOrigin = true)
        {
            if (IsOperationInProgress) return false;
            if (_companionAI == null) return false;
            
            // Check if we have the item
            if (!_resources.HasItemInInventory(itemPrefab))
            {
                Log($"No {itemPrefab} in inventory to deposit");
                return false;
            }
            
            _resources.RefreshNearbyChests(true);
            
            // Find a chest that can accept the item (has space or already has this item)
            _targetChest = FindChestForDeposit(itemPrefab);
            if (_targetChest == null)
            {
                Log($"No chest found that can accept {itemPrefab}");
                return false;
            }
            
            _currentOperation = OperationType.Deposit;
            _targetItemPrefab = itemPrefab;
            _targetAmount = 0; // Deposit all
            _itemsTransferred = 0;
            _shouldReturnToOrigin = returnToOrigin;
            _returnPosition = _companion.transform.position;
            _targetChestPosition = _targetChest.transform.position;
            
            // Calculate proper interaction point (in front of chest)
            _interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, _companion.transform.position);
            
            _operationStartTime = Time.time;
            
            SetPhase(InteractionPhase.MovingToChest);
            
            Log($"Starting DEPOSIT operation: {itemPrefab} to chest at {_targetChestPosition}, standing at {_interactionPoint}");
            return true;
        }
        
        /// <summary>
        /// Starts an operation to deposit all depositable items.
        /// </summary>
        public bool StartDepositAllOperation(bool returnToOrigin = true)
        {
            if (IsOperationInProgress) return false;
            if (_companionAI == null) return false;
            
            _resources.RefreshNearbyChests(true);
            
            if (!_resources.HasNearbyChests)
            {
                Log($"No nearby chests for deposit");
                return false;
            }
            
            // Find nearest chest
            _targetChest = FindNearestChest();
            if (_targetChest == null) return false;
            
            _currentOperation = OperationType.DepositAll;
            _targetItemPrefab = null;
            _targetAmount = 0;
            _itemsTransferred = 0;
            _shouldReturnToOrigin = returnToOrigin;
            _returnPosition = _companion.transform.position;
            _targetChestPosition = _targetChest.transform.position;
            
            // Calculate proper interaction point (in front of chest)
            _interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                _targetChest, _companion.transform.position);
            
            _operationStartTime = Time.time;
            
            SetPhase(InteractionPhase.MovingToChest);
            
            Log($"Starting DEPOSIT ALL operation to chest at {_targetChestPosition}, standing at {_interactionPoint}");
            return true;
        }
        
        /// <summary>
        /// Cancels the current operation.
        /// </summary>
        public void Cancel()
        {
            if (!IsOperationInProgress) return;
            
            Log($"Operation cancelled in phase {_currentPhase}");
            
            // Close chest if we opened it
            TryCloseChest();
            
            // Release pathfinding
            _companionAI?.ReleasePathfindingMovement(_ownerName);
            
            LastResult = InteractionResult.Failed("Cancelled", _currentPhase);
            SetPhase(InteractionPhase.Idle);
            _currentOperation = OperationType.None;
        }
        
        #endregion
        
        #region Update - Called Every Frame
        
        /// <summary>
        /// Updates the chest interaction state machine.
        /// Call this every frame while operation is in progress.
        /// </summary>
        /// <returns>True when operation is complete (check LastResult for success/failure)</returns>
        public bool Update()
        {
            if (!IsOperationInProgress)
                return true;
            
            // Check for timeout
            if (TimeInCurrentPhase > GetPhaseTimeout())
            {
                Log($"Phase {_currentPhase} timed out after {TimeInCurrentPhase:F1}s");
                FailOperation($"Timeout in {_currentPhase}");
                return true;
            }
            
            switch (_currentPhase)
            {
                case InteractionPhase.MovingToChest:
                    UpdateMovingToChest();
                    break;
                    
                case InteractionPhase.OpeningChest:
                    UpdateOpeningChest();
                    break;
                    
                case InteractionPhase.Interacting:
                    UpdateInteracting();
                    break;
                    
                case InteractionPhase.ClosingChest:
                    UpdateClosingChest();
                    break;
                    
                case InteractionPhase.ReturningToOrigin:
                    UpdateReturningToOrigin();
                    break;
                    
                case InteractionPhase.Complete:
                case InteractionPhase.Failed:
                    return true;
            }
            
            return _currentPhase == InteractionPhase.Complete || _currentPhase == InteractionPhase.Failed;
        }
        
        #endregion
        
        #region Phase Updates
        
        private void UpdateMovingToChest()
        {
            if (_targetChest == null)
            {
                FailOperation("Target chest destroyed");
                return;
            }
            
            // Use the calculated interaction point, not the chest center
            float distance = Vector3.Distance(_companion.transform.position, _interactionPoint);
            
            if (distance <= InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                // Arrived at interaction point
                Log($"Arrived at chest interaction point (dist: {distance:F1}m)");
                _companionAI.ReleasePathfindingMovement(_ownerName);
                SetPhase(InteractionPhase.OpeningChest);
                return;
            }
            
            // Request pathfinding to the INTERACTION POINT (in front of chest), not chest center
            _companionAI.RequestPathfindingMovement(
                _interactionPoint,
                run: false,
                reachDistance: InteractionPointHelper.ARRIVAL_THRESHOLD,
                authoritySource: UnifiedMovementAuthority.MovementSource.SubBehavior,
                authorityOwner: _ownerName
            );
        }
        
        private void UpdateOpeningChest()
        {
            // Face the chest
            FaceTarget(_targetChestPosition);
            
            // On first frame, try to open the chest
            if (TimeInCurrentPhase < 0.1f)
            {
                // Check if chest is in use by someone else
                if (_targetChest.IsInUse())
                {
                    Log($"Chest is in use by another player, waiting...");
                    // Could add retry logic here or fail
                }
                
                // Play companion's interact animation
                PlayInteractAnimation();
                
                // Actually open the chest via RPC (like a player would)
                TryOpenChest();
            }
            
            // Wait for open animation duration
            if (TimeInCurrentPhase >= OPEN_ANIMATION_DURATION)
            {
                SetPhase(InteractionPhase.Interacting);
            }
        }
        
        /// <summary>
        /// Opens the chest properly, triggering:
        /// - The chest lid animation (m_open/m_closed GameObjects)
        /// - The open sound effects
        /// - Sets the "InUse" ZDO state
        /// - Forces immediate ZDO sync to all nearby clients
        /// 
        /// HOW VALHEIM'S CONTAINER SYNC WORKS:
        /// 1. Owner sets m_inUse = true and calls UpdateUseVisual()
        /// 2. UpdateUseVisual() sets ZDO s_inUse flag and toggles m_open/m_closed GameObjects
        /// 3. Non-owners poll ZDO every 1 second in CheckForChanges() and update visuals
        /// 4. For IMMEDIATE sync, use ZDOMan.ForceSendZDO() to push ZDO to nearby clients
        /// </summary>
        private void TryOpenChest()
        {
            if (_targetChest == null) return;
            
            _targetChestNView = _targetChest.GetComponent<ZNetView>();
            if (_targetChestNView == null || !_targetChestNView.IsValid())
            {
                Log("Cannot open chest - no valid ZNetView");
                return;
            }
            
            // Check if already in use by someone else
            if (_targetChest.IsInUse())
            {
                Log("Chest is already in use by someone else");
                return;
            }
            
            // Claim ownership - SetInUse() requires ownership to work
            if (!_targetChestNView.IsOwner())
            {
                _targetChestNView.ClaimOwnership();
                // Small delay to ensure ownership is established
            }
            
            // SetInUse(true) does the following (when we're owner):
            // 1. Sets m_inUse = true
            // 2. Calls UpdateUseVisual() which:
            //    - Sets ZDO s_inUse = 1
            //    - Toggles m_open.SetActive(true) and m_closed.SetActive(false)
            // 3. Creates m_openEffects (sound/particles)
            _targetChest.SetInUse(true);
            _chestWasOpened = true;
            
            // FALLBACK: For local player, directly ensure the visual state is correct
            // This handles cases where SetInUse doesn't immediately update visuals
            EnsureChestVisualState(true);
            
            // Force immediate ZDO sync to all nearby players
            // This is what Valheim does in RPC_RequestOpen for instant visual updates
            var zdo = _targetChestNView.GetZDO();
            if (zdo != null && ZNet.instance != null)
            {
                foreach (var peer in ZNet.instance.GetConnectedPeers())
                {
                    ZDOMan.instance.ForceSendZDO(peer.m_uid, zdo.m_uid);
                }
            }
            
            Log($"Opened chest - ownership claimed, SetInUse(true) called, ZDO synced");
        }
        
        private void UpdateInteracting()
        {
            // Wait for interact duration then perform the operation
            if (TimeInCurrentPhase >= INTERACT_DURATION)
            {
                PerformItemTransfer();
                
                // Always close the chest after interacting
                SetPhase(InteractionPhase.ClosingChest);
            }
        }
        
        private void UpdateClosingChest()
        {
            // Close the chest on first frame
            if (TimeInCurrentPhase < 0.1f)
            {
                TryCloseChest();
            }
            
            // Wait for close animation
            if (TimeInCurrentPhase >= CLOSE_ANIMATION_DURATION)
            {
                // Decide next phase
                if (_shouldReturnToOrigin)
                {
                    SetPhase(InteractionPhase.ReturningToOrigin);
                }
                else
                {
                    CompleteOperation();
                }
            }
        }
        
        /// <summary>
        /// Closes the chest properly, triggering:
        /// - The chest lid closing animation (m_open/m_closed GameObjects)
        /// - The close sound effects
        /// - Clears the "InUse" ZDO state
        /// - Forces immediate ZDO sync to all nearby clients
        /// </summary>
        private void TryCloseChest()
        {
            if (!_chestWasOpened) return;
            if (_targetChest == null) return;
            
            // Ensure we have ownership to call SetInUse
            if (_targetChestNView != null && _targetChestNView.IsValid())
            {
                if (!_targetChestNView.IsOwner())
                {
                    _targetChestNView.ClaimOwnership();
                }
                
                // SetInUse(false) does the following (when we're owner):
                // 1. Sets m_inUse = false
                // 2. Calls UpdateUseVisual() which:
                //    - Sets ZDO s_inUse = 0
                //    - Toggles m_open.SetActive(false) and m_closed.SetActive(true)
                // 3. Creates m_closeEffects (sound/particles)
                _targetChest.SetInUse(false);
                
                // FALLBACK: For local player, directly ensure the visual state is correct
                EnsureChestVisualState(false);
                
                // Force immediate ZDO sync to all nearby players
                var zdo = _targetChestNView.GetZDO();
                if (zdo != null && ZNet.instance != null)
                {
                    foreach (var peer in ZNet.instance.GetConnectedPeers())
                    {
                        ZDOMan.instance.ForceSendZDO(peer.m_uid, zdo.m_uid);
                    }
                }
                
                Log($"Closed chest - SetInUse(false) called, ZDO synced");
            }
            
            _chestWasOpened = false;
        }
        
        /// <summary>
        /// Ensures the chest's visual state (open/closed lid) matches the expected state.
        /// This is a fallback for when SetInUse() doesn't immediately update visuals for the local player.
        /// </summary>
        private void EnsureChestVisualState(bool open)
        {
            if (_targetChest == null) return;
            
            // Use reflection to access m_open and m_closed GameObjects if SetInUse didn't work
            // Container has public fields: m_open, m_closed
            try
            {
                var openObj = _targetChest.m_open;
                var closedObj = _targetChest.m_closed;
                
                if (openObj != null && closedObj != null)
                {
                    if (openObj.activeSelf != open || closedObj.activeSelf != !open)
                    {
                        openObj.SetActive(open);
                        closedObj.SetActive(!open);
                        Log($"Chest visual state manually set: open={open}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log($"Failed to set chest visual state: {ex.Message}");
            }
        }
        
        private void UpdateReturningToOrigin()
        {
            float distance = Vector3.Distance(_companion.transform.position, _returnPosition);
            
            if (distance <= InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                // Arrived back
                Log($"Returned to origin");
                _companionAI.ReleasePathfindingMovement(_ownerName);
                CompleteOperation();
                return;
            }
            
            // Request pathfinding movement back
            _companionAI.RequestPathfindingMovement(
                _returnPosition,
                run: false,
                reachDistance: InteractionPointHelper.ARRIVAL_THRESHOLD,
                authoritySource: UnifiedMovementAuthority.MovementSource.SubBehavior,
                authorityOwner: _ownerName
            );
        }
        
        #endregion
        
        #region Item Transfer
        
        private void PerformItemTransfer()
        {
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null)
            {
                Log("No storage inventory available!");
                _itemsTransferred = 0;
                return;
            }
            
            switch (_currentOperation)
            {
                case OperationType.Pull:
                case OperationType.PullMultiple:
                    // Use InventoryTransferService for reliable partial transfer handling
                    var pullResult = InventoryTransferService.PullItemFromContainers(
                        _resources.NearbyChests,
                        storageInv,
                        _targetItemPrefab,
                        _targetAmount
                    );
                    _itemsTransferred = pullResult.AmountTransferred;
                    
                    if (pullResult.IsPartial)
                    {
                        Log($"Pulled {_itemsTransferred}/{_targetAmount}x {_targetItemPrefab} (partial)");
                    }
                    else
                    {
                        Log($"Pulled {_itemsTransferred}x {_targetItemPrefab}");
                    }
                    
                    // Save companion inventory
                    _inventory?.SaveToZDO();
                    break;
                    
                case OperationType.Deposit:
                    var depositResult = InventoryTransferService.DepositItemSmart(
                        storageInv,
                        _resources.NearbyChests,
                        _targetItemPrefab
                    );
                    _itemsTransferred = depositResult.AmountTransferred;
                    Log($"Deposited {_itemsTransferred}x {_targetItemPrefab}");
                    
                    _inventory?.SaveToZDO();
                    break;
                    
                case OperationType.DepositAll:
                    var depositAllResult = InventoryTransferService.DepositAllDepositable(
                        _inventory,
                        _resources.NearbyChests
                    );
                    _itemsTransferred = depositAllResult.AmountTransferred;
                    Log($"Deposited {_itemsTransferred} items total");
                    break;
            }
            
            // Fire event
            if (_itemsTransferred > 0)
            {
                switch (_currentOperation)
                {
                    case OperationType.Pull:
                    case OperationType.PullMultiple:
                        Events.CompanionEvents.FireItemPulled(_companion, _targetItemPrefab, _itemsTransferred);
                        break;
                    case OperationType.Deposit:
                    case OperationType.DepositAll:
                        Events.CompanionEvents.FireItemDeposited(_companion, _targetItemPrefab ?? "various", _itemsTransferred);
                        break;
                }
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        private void SetPhase(InteractionPhase phase)
        {
            if (_currentPhase != phase)
            {
                Log($"Phase: {_currentPhase} -> {phase}");
                _currentPhase = phase;
                _phaseStartTime = Time.time;
            }
        }
        
        private void CompleteOperation()
        {
            LastResult = InteractionResult.Succeeded(
                _itemsTransferred,
                _targetItemPrefab,
                _targetChest,
                TotalOperationTime
            );
            
            Log($"Operation COMPLETE: {_itemsTransferred}x {_targetItemPrefab} in {TotalOperationTime:F1}s");
            
            SetPhase(InteractionPhase.Complete);
            _currentOperation = OperationType.None;
        }
        
        private void FailOperation(string reason)
        {
            // Close chest if we opened it
            TryCloseChest();
            
            _companionAI?.ReleasePathfindingMovement(_ownerName);
            
            LastResult = InteractionResult.Failed(reason, _currentPhase);
            
            Log($"Operation FAILED: {reason}");
            
            SetPhase(InteractionPhase.Failed);
            _currentOperation = OperationType.None;
        }
        
        private float GetPhaseTimeout()
        {
            switch (_currentPhase)
            {
                case InteractionPhase.MovingToChest:
                case InteractionPhase.ReturningToOrigin:
                    return MOVEMENT_TIMEOUT;
                default:
                    return PHASE_TIMEOUT;
            }
        }
        
        private Container FindChestWithItem(string prefabName)
        {
            Container nearest = null;
            float nearestDist = float.MaxValue;
            Vector3 myPos = _companion.transform.position;
            
            foreach (var chest in _resources.NearbyChests)
            {
                if (chest == null) continue;
                
                var inv = chest.GetInventory();
                if (inv == null) continue;
                
                // Check if chest has the item
                bool hasItem = false;
                foreach (var item in inv.GetAllItems())
                {
                    string dropName = item?.m_dropPrefab?.name ?? "";
                    if (dropName.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasItem = true;
                        break;
                    }
                }
                
                if (hasItem)
                {
                    float dist = Vector3.Distance(myPos, chest.transform.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = chest;
                    }
                }
            }
            
            return nearest;
        }
        
        private Container FindChestForDeposit(string prefabName)
        {
            Container nearest = null;
            float nearestDist = float.MaxValue;
            Vector3 myPos = _companion.transform.position;
            
            foreach (var chest in _resources.NearbyChests)
            {
                if (chest == null) continue;
                
                var inv = chest.GetInventory();
                if (inv == null) continue;
                
                // Check if chest has space or already has this item type (for stacking)
                bool hasItem = false;
                foreach (var existingItem in inv.GetAllItems())
                {
                    if (existingItem?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        hasItem = true;
                        break;
                    }
                }
                bool canAccept = inv.HaveEmptySlot() || hasItem;
                
                if (canAccept)
                {
                    float dist = Vector3.Distance(myPos, chest.transform.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = chest;
                    }
                }
            }
            
            return nearest;
        }
        
        private Container FindNearestChest()
        {
            Container nearest = null;
            float nearestDist = float.MaxValue;
            Vector3 myPos = _companion.transform.position;
            
            foreach (var chest in _resources.NearbyChests)
            {
                if (chest == null) continue;
                
                float dist = Vector3.Distance(myPos, chest.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = chest;
                }
            }
            
            return nearest;
        }
        
        private void FaceTarget(Vector3 targetPos)
        {
            if (_companion == null) return;
            
            Vector3 dir = (targetPos - _companion.transform.position).normalized;
            dir.y = 0;
            
            if (dir.sqrMagnitude > 0.01f)
            {
                Quaternion targetRot = Quaternion.LookRotation(dir);
                _companion.transform.rotation = Quaternion.Slerp(
                    _companion.transform.rotation, 
                    targetRot, 
                    Time.deltaTime * 5f
                );
            }
        }
        
        private void PlayInteractAnimation()
        {
            var zanim = _companion?.GetComponent<ZSyncAnimation>();
            zanim?.SetTrigger("interact");
        }
        
        private void Log(string message)
        {
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[ChestInteraction:{_ownerName}] {_companion?.companionName} {message}");
            }
        }
        
        #endregion
    }
}
