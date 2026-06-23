using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Base class for idle sub-behaviors that companions can perform while idle.
    /// Sub-behaviors are more complex activities like training, crafting, or interacting with objects.
    /// 
    /// LIFECYCLE:
    /// 1. CanStart() - Called to check if this behavior can begin (equipment, nearby objects, etc.)
    /// 2. Start() - Initialize the behavior, set up state
    /// 3. Update() - Called each frame while active, returns true when complete
    /// 4. Cancel() - Called if interrupted (combat, owner command, etc.)
    /// 
    /// INVENTORY AWARENESS:
    /// Behaviors can override InventoryPriority to influence selection order:
    /// - Priority > 0: Should run BEFORE other behaviors (e.g., deposit when full)
    /// - Priority = 0: Normal priority (default)
    /// - Priority < 0: Should run AFTER other behaviors
    /// 
    /// Behaviors can also use GetInventoryStatus() to check inventory state
    /// and make decisions (e.g., don't start gathering if inventory is full).
    /// 
    /// COMBAT INTERRUPTION AND RESUMPTION:
    /// Long-form work behaviors (smelter operation, kiln tending, etc.) support being interrupted
    /// by combat and resumed afterwards. Override SupportsResumption and SaveState/RestoreState
    /// to enable this for your behavior.
    /// 
    /// COMMAND AUTHORITY:
    /// When a behavior is started via player command (IsCommandInitiated = true), it has ABSOLUTE
    /// authority over the companion. Normal AI behaviors (following, combat targeting, idle activities)
    /// are completely suppressed until the command completes or is cancelled by another command.
    /// 
    /// UNIFIED MOVEMENT AUTHORITY:
    /// All behaviors should use the movement helpers (TryMoveToPosition, StopMovement) which
    /// integrate with UnifiedMovementAuthority to prevent movement conflicts.
    /// 
    /// DESIGN:
    /// - Sub-behaviors are self-contained and manage their own state
    /// - They communicate with CompanionIdleBehavior through events
    /// - They can request movement to specific positions
    /// - They should have a hard timeout to prevent getting stuck
    /// </summary>
    public abstract class IdleSubBehavior
    {
        protected CompanionController Companion { get; private set; }
        protected CompanionIdleBehavior IdleBehavior { get; private set; }
        protected Transform Transform => Companion?.transform;
        
        /// <summary>
        /// Cached CompanionInventory reference for inventory checks.
        /// </summary>
        protected CompanionInventory CompanionInventory { get; private set; }
        
        /// <summary>
        /// Unified movement authority for this behavior.
        /// Use TryMoveToPosition() and StopMovement() helpers instead of direct access.
        /// </summary>
        protected UnifiedMovementAuthority MovementAuthority { get; private set; }
      
        protected float StartTime { get; private set; }
        
        /// <summary>
        /// Maximum duration for this behavior before it times out.
        /// Set this in Initialize() or Start() for behavior-specific timeouts.
        /// </summary>
        public float MaxDuration { get; protected set; } = 120f; // Default 2 minute timeout
 
        public bool IsActive { get; private set; }
        
        /// <summary>
        /// True if this behavior was started by a player command.
        /// When true, the behavior has absolute authority and normal AI is suppressed.
        /// </summary>
        public bool IsCommandInitiated { get; private set; }
        
        /// <summary>
        /// True if this behavior was interrupted by combat and should resume after.
        /// </summary>
        public bool WasInterruptedByCombat { get; protected set; }
        
        /// <summary>
        /// Override to return true if this behavior supports being resumed after combat.
        /// Long-form work behaviors like smelter operation should return true.
        /// </summary>
        public virtual bool SupportsResumption => false;
        
        /// <summary>
        /// Override to return true if this behavior should be available during idle rotation
        /// for companions that are set to "Stay" mode (not following).
        /// </summary>
        public virtual bool AvailableForIdleRotation => false;
        
        /// <summary>
        /// Override to return a priority value for inventory-aware behavior selection.
        /// Higher values = higher priority (will be tried first).
        /// 
        /// Use this to:
        /// - Make deposit behaviors high priority when inventory is full (return 100)
        /// - Make gathering behaviors low priority when inventory is full (return -100)
        /// - Normal behaviors return 0
        /// 
        /// The priority is checked AFTER CanStart() returns true.
        /// </summary>
        public virtual int InventoryPriority => 0;
        
        /// <summary>
        /// Override to return true if this behavior requires inventory space to operate.
        /// If true, CanStart() will automatically return false when inventory is full.
        /// Default is false - behaviors must opt-in to this check.
        /// </summary>
        public virtual bool RequiresInventorySpace => false;
        
        /// <summary>
        /// Override to return a multiplier for allowed wander distance during this behavior.
        /// Work behaviors like resource gathering may need larger wander radius to reach
        /// resources further from home. Default is 1.0 (normal wander radius).
        /// </summary>
        public virtual float WanderRadiusMultiplier => 1.0f;
        
        public abstract string BehaviorName { get; }
        
        /// <summary>
        /// Gets the search center position for finding nearby objects.
        /// For staying companions, this returns the home position (where they were commanded to stay).
        /// For following companions, this returns their current position.
        /// This ensures that staying companions search for work near their designated area.
        /// </summary>
        protected Vector3 SearchCenter
        {
            get
            {
                // If we have a home position set (staying mode), search around home
                if (IdleBehavior != null && IdleBehavior.HasHomePosition)
                {
                    return IdleBehavior.HomePosition;
                }
                
                // Otherwise search around current position
                return Transform?.position ?? Vector3.zero;
            }
        }
        
        /// <summary>
        /// Gets the effective search radius for finding nearby objects.
        /// 
        /// For STAYING companions (set to stay mode):
        /// - Uses CompanionSettings.GetStayModeWorkSearchRadius() which returns 50m by default
        /// - If in a territory, uses the larger of 50m or territory bounds
        /// - This allows companions to find work anywhere in their designated area
        /// 
        /// For FOLLOWING companions:
        /// - Uses the base radius provided (typically 10-15m)
        /// - No multiplier applied - following companions only interact with nearby objects
        /// 
        /// The baseRadius parameter is used as a MINIMUM - staying companions will always
        /// search at least the StayModeWorkRadius (50m), but will use the larger of
        /// baseRadius or StayModeWorkRadius.
        /// </summary>
        /// <param name="baseRadius">The behavior's default search radius (used for following companions)</param>
        /// <returns>The effective search radius to use</returns>
        protected float GetEffectiveSearchRadius(float baseRadius)
        {
            if (IdleBehavior != null && IdleBehavior.HasHomePosition && Companion != null && !Companion.ShouldBeFollowing)
            {
                float stayModeRadius = CompanionSettings.GetStayModeWorkSearchRadius(SearchCenter);

                // Respect the configured wander radius as a hard cap so task search
                // never sends the companion beyond their designated boundary.
                float wanderRadius = IdleBehavior.maxWanderRadius;
                if (wanderRadius > 0f)
                    stayModeRadius = Mathf.Min(stayModeRadius, wanderRadius);

                return Mathf.Max(stayModeRadius, baseRadius);
            }

            return baseRadius;
        }

        // Scratch buffer for path queries â€” safe as static since Unity is single-threaded.
        private static readonly List<Vector3> _reachabilityBuffer = new List<Vector3>();

        // Per-companion TTL cache: target position (rounded to 1 m grid) â†’ (reachable, timestamp).
        // Keeps the expensive GetPath call to at most once per REACH_CACHE_TTL seconds per target,
        // even when scanning phases run every frame across many companions.
        // Must be per-instance (not static) because reachability depends on where THIS companion stands.
        private readonly Dictionary<Vector3Int, (bool ok, float checkedAt)> _reachCache =
            new Dictionary<Vector3Int, (bool ok, float checkedAt)>();
        private const float REACH_CACHE_TTL = 5f;

        /// <summary>
        /// Returns true if Valheim's pathfinding can find a route from the companion's
        /// current position to <paramref name="targetPos"/>. Results are cached per target
        /// position for <see cref="REACH_CACHE_TTL"/> seconds so scanning phases that run
        /// every frame don't trigger a full A* query on each tick.
        /// Falls back to true when <see cref="Pathfinding.instance"/> is unavailable.
        /// </summary>
        protected bool IsReachable(Vector3 targetPos)
        {
            if (Transform == null || Pathfinding.instance == null) return true;

            var key = Vector3Int.RoundToInt(targetPos);
            if (_reachCache.TryGetValue(key, out var cached) && Time.time - cached.checkedAt < REACH_CACHE_TTL)
                return cached.ok;

            _reachabilityBuffer.Clear();
            bool ok = Pathfinding.instance.GetPath(
                Transform.position,
                targetPos,
                _reachabilityBuffer,
                Pathfinding.AgentType.Humanoid,
                cleanup: true);

            _reachCache[key] = (ok, Time.time);
            return ok;
        }
     
        /// <summary>
        /// Initialize the sub-behavior with required references.
        /// </summary>
        public virtual void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            Companion = companion;
            IdleBehavior = idleBehavior;
            
            // Get unified movement authority and inventory
            if (companion != null)
            {
                MovementAuthority = companion.GetMovementAuthority();
                CompanionAI = companion.GetComponent<CompanionAI>();
                CompanionInventory = companion.GetComponent<CompanionInventory>();
            }
        }
        
        #region Inventory Status Helpers
        
        /// <summary>
        /// Gets the detailed inventory status for this companion.
        /// This uses CompanionInventory.GetInventoryStatus() which is the SINGLE SOURCE OF TRUTH.
        /// 
        /// Use this to:
        /// - Check if inventory is full before starting gathering
        /// - Decide if deposit should take priority
        /// - Display inventory capacity in status
        /// </summary>
        protected CompanionInventory.InventoryStatus GetInventoryStatus()
        {
            if (CompanionInventory == null)
            {
                // Return a "full" status if no inventory exists
                return new CompanionInventory.InventoryStatus 
                { 
                    IsFull = true, 
                    Reason = "No inventory component" 
                };
            }
            
            return CompanionInventory.GetInventoryStatus();
        }
        
        /// <summary>
        /// Quick check if inventory needs deposit before gathering more items.
        /// Returns true if inventory is full, nearly full, or overweight.
        /// </summary>
        protected bool InventoryNeedsDeposit()
        {
            var status = GetInventoryStatus();
            return status.NeedsDeposit;
        }
        
        /// <summary>
        /// Quick check if companion can pick up more items.
        /// </summary>
        /// <param name="estimatedWeight">Optional weight of item being considered</param>
        protected bool CanPickUpMoreItems(float estimatedWeight = 1f)
        {
            if (CompanionInventory == null) return false;
            return CompanionInventory.CanPickUpMoreItems(estimatedWeight);
        }
        
        /// <summary>
        /// Checks if inventory is completely full (no room at all).
        /// This is different from NeedsDeposit which triggers at 80%.
        /// </summary>
        protected bool IsInventoryCompletelyFull()
        {
            var status = GetInventoryStatus();
            return status.IsFull;
        }
        
        /// <summary>
        /// Base implementation of CanStart that includes inventory checks.
        /// Subclasses that override CanStart should call base.CanStartBase() 
        /// to include these checks, or handle inventory manually.
        /// </summary>
        protected bool CanStartBase()
        {
            // If this behavior requires inventory space, check it
            if (RequiresInventorySpace)
            {
                if (IsInventoryCompletelyFull())
                {
                    if (ShouldLogVerbose())
                        Debug.Log($"[{BehaviorName}] Cannot start - inventory is full");
                    return false;
                }
            }
            
            return true;
        }
        
        #endregion
        
        #region Movement Authority Helpers
        
        /// <summary>
        /// CompanionAI reference for vanilla pathfinding.
        /// </summary>
        protected CompanionAI CompanionAI { get; private set; }
        
        /// <summary>
        /// Tries to move to a position using VANILLA PATHFINDING via CompanionAI.
        /// 
        /// CORRECT ARCHITECTURE (Post-Bug #8 Fix):
        /// - Authority = coordination (decides WHO can move)
        /// - Vanilla MoveTo() = pathfinding (handles obstacles)
        /// 
        /// Call this every frame until destination is reached.
        /// Returns true if movement was successfully requested (NOT when destination reached).
        /// Check distance separately to know when you've arrived.
        /// </summary>
        /// <param name="position">Target position to move to</param>
        /// <param name="walk">Use walking speed</param>
        /// <param name="run">Use running speed</param>
        protected bool TryMoveToPosition(Vector3 position, bool walk = true, bool run = false)
        {
            // Get CompanionAI if we don't have it
            if (CompanionAI == null && Companion != null)
            {
                CompanionAI = Companion.GetComponent<CompanionAI>();
            }
            
            if (CompanionAI == null)
            {
                Debug.LogWarning($"[{BehaviorName}] {Companion?.companionName} TryMoveToPosition - no CompanionAI, using fallback");
                return TryMoveToPositionDirect(position, walk, run);
            }
            
            // Determine authority source
            var source = IsCommandInitiated 
                ? UnifiedMovementAuthority.MovementSource.PlayerCommand 
                : UnifiedMovementAuthority.MovementSource.SubBehavior;
            
            // OPTIMIZATION (Bug #12 Fix): Check if we already have authority before acquiring
            // This prevents log spam and redundant authority acquisition every frame
            if (MovementAuthority != null)
            {
                bool hasAuthority = MovementAuthority.HasAuthority(BehaviorName);
                if (!hasAuthority)
                {
                    // Try to acquire authority
                    if (!MovementAuthority.TryAcquireAuthority(source, BehaviorName, 5f))
                    {
                        // Can't acquire authority - another system has priority
                        return false;
                    }
                }
            }
            
            // Use CompanionAI's vanilla pathfinding
            // This returns true when destination is REACHED, false while still moving
            bool reached = CompanionAI.RequestPathfindingMovement(
                position, 
                run, 
                reachDistance: 2.0f,
                authoritySource: source,
                authorityOwner: BehaviorName
            );
            
            // Return true = movement request accepted (we're moving or arrived)
            // The caller should check distance to know if we've actually arrived
            return true;
        }
        
        /// <summary>
        /// Direct movement without pathfinding (fallback only).
        /// NOTE: This should only be used when CompanionAI is unavailable.
        /// </summary>
        private bool TryMoveToPositionDirect(Vector3 position, bool walk, bool run)
        {
            // ALWAYS LOG movement attempts for debugging
            Debug.Log($"[{BehaviorName}] {Companion?.companionName} TryMoveToPositionDirect (FALLBACK): dest={position}, walk={walk}");
            
            if (MovementAuthority == null)
            {
                // No authority system - try combat movement as last resort
                var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
                if (combatMovement != null)
                {
                    combatMovement.SetMoveDestination(position, useWalk: walk, skipCommandOverride: true);
                    return true;
                }
                
                Debug.LogWarning($"[{BehaviorName}] No movement system available for {Companion?.companionName}");
                return false;
            }
            
            var source = IsCommandInitiated 
                ? UnifiedMovementAuthority.MovementSource.PlayerCommand 
                : UnifiedMovementAuthority.MovementSource.SubBehavior;
            
            if (!MovementAuthority.TryAcquireAuthority(source, BehaviorName, 5f))
            {
                Debug.Log($"[{BehaviorName}] {Companion?.companionName} TryMoveToPositionDirect - authority acquisition failed");
                return false;
            }
            
            MovementAuthority.SetMoveDestination(BehaviorName, position, walk, run);
            return true;
        }
        
        /// <summary>
        /// Stops movement and releases authority.
        /// </summary>
        protected void StopMovement()
        {
            // Release via CompanionAI if available
            if (CompanionAI != null)
            {
                CompanionAI.ReleasePathfindingMovement(BehaviorName);
                return;
            }
            
            // Fallback to authority system
            if (MovementAuthority != null && MovementAuthority.HasAuthority(BehaviorName))
            {
                MovementAuthority.ClearDestination(BehaviorName);
                MovementAuthority.SetMoveDirection(BehaviorName, Vector3.zero);
                MovementAuthority.ReleaseAuthority(BehaviorName);
            }
            else
            {
                // Fallback - use combat movement system
                var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
                combatMovement?.ClearMoveDestination();
            }
        }
        
        /// <summary>
        /// Checks if this behavior has movement authority.
        /// </summary>
        protected bool HasMovementAuthority()
        {
            if (MovementAuthority == null) return true; // No system = always allowed
            return MovementAuthority.HasAuthority(BehaviorName) || 
                   MovementAuthority.CanAcquireAuthority(
                       IsCommandInitiated 
                           ? UnifiedMovementAuthority.MovementSource.PlayerCommand 
                           : UnifiedMovementAuthority.MovementSource.SubBehavior);
        }
        
        /// <summary>
        /// Releases any held movement authority.
        /// Called automatically on Cancel() and Complete().
        /// </summary>
        protected void ReleaseMovementAuthority()
        {
            // Release via CompanionAI if available
            if (CompanionAI != null)
            {
                CompanionAI.ReleasePathfindingMovement(BehaviorName);
            }
            
            // Also release via authority system
            MovementAuthority?.ReleaseAuthority(BehaviorName);
        }
        
        #endregion
     
        /// <summary>
        /// Check if this behavior can start given current conditions.
        /// Override to check for required equipment, nearby objects, etc.
        /// </summary>
        public abstract bool CanStart();
        
        /// <summary>
        /// Called when the behavior starts. Set up initial state.
        /// </summary>
        public virtual void Start()
        {
            IsActive = true;
            StartTime = Time.time;
          
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[{BehaviorName}] Started for {Companion?.companionName} (command={IsCommandInitiated})");
        }
        
        /// <summary>
        /// Starts the behavior as a player command with absolute authority.
        /// Normal AI will be suppressed until this behavior completes.
        /// </summary>
        public virtual void StartAsCommand()
        {
            IsCommandInitiated = true;
            Start();
            
            // Set command priority for the full duration
            var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.SetCommandPriorityDuration(MaxDuration + 10f);
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[{BehaviorName}] Started as command for {Companion?.companionName}");
        }
        
        /// <summary>
        /// Called each frame while active. Return true when the behavior is complete.
        /// </summary>
        public abstract bool Update();
      
        /// <summary>
        /// Called when the behavior is interrupted (combat, commands, etc.)
        /// </summary>
        public virtual void Cancel()
        {
            bool wasCommand = IsCommandInitiated;
            IsActive = false;
            IsCommandInitiated = false;
            WasInterruptedByCombat = false;
            
            // CRITICAL: Release movement authority
            ReleaseMovementAuthority();
            StopMovement();
            
            // CRITICAL: Force reset ALL animation states to prevent stuck emotes
            // This ensures any emotes played during the behavior are properly cleared
            ForceResetAnimationState();
            
            // Clear command priority when cancelled
            if (wasCommand)
            {
                var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
                combatMovement?.ClearCommandPriority();
                
                // Also notify state controller
                var stateController = Companion?.GetComponent<Movement.CompanionStateController>();
                stateController?.CancelCommand("Behavior cancelled");
            }
         
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[{BehaviorName}] Cancelled for {Companion?.companionName}");
        }
        
        /// <summary>
        /// Forces a complete animation state reset to prevent stuck emotes.
        /// Called on Cancel() and Complete() to ensure clean state.
        /// </summary>
        protected void ForceResetAnimationState()
        {
            var stateController = Companion?.GetComponent<Movement.CompanionStateController>();
            if (stateController != null)
            {
                stateController.ForceStopEmote();
                return;
            }

            // Fallback when no state controller: call vanilla StopEmote via reflection.
            var character = Companion?.GetComponent<Character>();
            if (character != null)
            {
                var stopEmote = character.GetType().GetMethod(
                    "StopEmote",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                stopEmote?.Invoke(character, null);
            }
        }
        
        /// <summary>
        /// Checks if animator has a parameter with the given name.
        /// </summary>
        private bool HasAnimatorParameter(Animator animator, string paramName)
        {
            if (animator == null) return false;
            foreach (var param in animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Called when combat starts to interrupt this behavior.
        /// If SupportsResumption is true, the behavior state will be saved for later resumption.
        /// </summary>
        public virtual void InterruptForCombat()
        {
            if (!IsActive) return;
            
            if (SupportsResumption)
            {
                WasInterruptedByCombat = true;
                SaveState();
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[{BehaviorName}] Interrupted by combat for {Companion?.companionName} - state saved for resumption");
            }
            
            // Pause the behavior but don't fully cancel
            IsActive = false;
            
            // CRITICAL: Force reset ALL animation states to prevent stuck emotes during combat
            ForceResetAnimationState();
            
            // Unlock movement for combat
            var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
            combatMovement?.UnlockMovement();
        }
        
        /// <summary>
        /// Called after combat ends to resume an interrupted behavior.
        /// Only called if WasInterruptedByCombat is true and SupportsResumption is true.
        /// </summary>
        public virtual void ResumeAfterCombat()
        {
            if (!WasInterruptedByCombat || !SupportsResumption) return;
            
            RestoreState();
            IsActive = true;
            WasInterruptedByCombat = false;
            
            // Extend the timeout since we were interrupted
            StartTime = Time.time - (MaxDuration * 0.5f); // Give half the duration back
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[{BehaviorName}] Resumed after combat for {Companion?.companionName}");
        }
        
        /// <summary>
        /// Override to save behavior-specific state before combat interruption.
        /// Called by InterruptForCombat() if SupportsResumption is true.
        /// </summary>
        protected virtual void SaveState() { }
        
        /// <summary>
        /// Override to restore behavior-specific state after combat ends.
        /// Called by ResumeAfterCombat() if state was saved.
        /// </summary>
        protected virtual void RestoreState() { }
    
        /// <summary>
        /// Called when the behavior completes normally.
        /// Clears command priority and allows normal AI to resume.
        /// </summary>
        protected virtual void Complete()
        {
            bool wasCommand = IsCommandInitiated;
            IsActive = false;
            IsCommandInitiated = false;
            
            // CRITICAL: Release movement authority
            ReleaseMovementAuthority();
            StopMovement();
            
            // CRITICAL: Force reset ALL animation states to prevent stuck emotes
            ForceResetAnimationState();
            
            // Clear command priority when completed
            if (wasCommand)
            {
                var combatMovement = Companion?.GetComponent<CompanionCombatMovement>();
                combatMovement?.ClearCommandPriority();
                
                // Also notify state controller
                var stateController = Companion?.GetComponent<Movement.CompanionStateController>();
                stateController?.CompleteCommand();
            }
   
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[{BehaviorName}] Completed for {Companion?.companionName}");
        }
        
        /// <summary>
        /// Check if the behavior has exceeded its maximum duration.
        /// </summary>
        protected bool IsTimedOut()
        {
            return Time.time - StartTime > MaxDuration;
        }
        
        /// <summary>
        /// Returns true if verbose logging should be shown for this companion.
        /// Only logs for tamed companions, not wild ones.
        /// This reduces log spam from wild companion spawns.
        /// </summary>
        protected bool ShouldLogVerbose()
        {
            if (!CompanionIdleBehavior.VerboseLogging) return false;
            if (Companion == null) return false;
            // Only log for tamed companions to reduce spam from wild companions
            return Companion.isTamed;
        }
        
        /// <summary>
        /// Gets a description of what the companion is currently doing.
        /// </summary>
        public abstract string GetStatusDescription();
    }
}
