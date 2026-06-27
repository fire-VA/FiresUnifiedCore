using System;
using UnityEngine;
using FiresCore.Npc.Core;
using FiresCore.Npc.Animation;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Enhanced base class for work behaviors (smelter operation, resource gathering, etc.).
    /// Provides standardized phase management, resource access, and animation integration.
    /// 
    /// FEATURES:
    /// - Built-in phase management with automatic timeout handling
    /// - Integrated ResourceAccessService for chest/inventory operations
    /// - Animation controller integration for emotes and work animations
    /// - Standardized movement helpers
    /// - Automatic stuck detection and recovery
    /// 
    /// USAGE:
    /// 1. Create your behavior class extending WorkBehaviorBase&lt;YourPhaseEnum&gt;
    /// 2. Override InitializePhaseManager() to configure phase timeouts
    /// 3. Override UpdatePhase() to handle each phase
    /// 4. Use Resources and Animation properties for common operations
    /// </summary>
    /// <typeparam name="TPhase">Enum type for behavior phases</typeparam>
    public abstract class WorkBehaviorBase<TPhase> : IdleSubBehavior where TPhase : Enum
    {
        #region Properties
        
        /// <summary>
        /// Phase manager for standardized state transitions.
        /// </summary>
        protected BehaviorPhaseManager<TPhase> PhaseManager { get; private set; }
        
        /// <summary>
        /// Resource access service for chest/inventory operations.
        /// </summary>
        protected ResourceAccessService Resources { get; private set; }
        
        /// <summary>
        /// Animation controller for emotes and work animations.
        /// </summary>
        protected CompanionAnimationController AnimationController { get; private set; }
        
        /// <summary>
        /// Current phase (shortcut to PhaseManager.CurrentPhase).
        /// </summary>
        protected TPhase CurrentPhase => PhaseManager != null ? PhaseManager.CurrentPhase : default(TPhase);
        
        /// <summary>
        /// Time in current phase (shortcut to PhaseManager.TimeInCurrentPhase).
        /// </summary>
        protected float TimeInCurrentPhase => PhaseManager != null ? PhaseManager.TimeInCurrentPhase : 0f;
        
        #endregion
        
        #region Components
        
        protected Character Character { get; private set; }
        protected Humanoid Humanoid { get; private set; }
        protected CompanionInventory Inventory { get; private set; }
        protected CompanionCombatMovement CombatMovement { get; private set; }
        protected Rigidbody Rigidbody { get; private set; }
        protected ZSyncAnimation ZAnim { get; private set; }
        
        #endregion
        
        #region Abstract Members
        
        /// <summary>
        /// The initial phase when the behavior starts.
        /// </summary>
        protected abstract TPhase InitialPhase { get; }
        
        /// <summary>
        /// Updates the current phase. Return true when the behavior is complete.
        /// </summary>
        protected abstract bool UpdatePhase(TPhase phase);
        
        /// <summary>
        /// Gets the timeout duration for a specific phase.
        /// Return 0 or negative to disable timeout for that phase.
        /// </summary>
        protected abstract float GetPhaseTimeout(TPhase phase);
        
        /// <summary>
        /// Gets a human-readable description for a phase.
        /// </summary>
        protected abstract string GetPhaseDescription(TPhase phase);
        
        #endregion
        
        #region Virtual Members
        
        /// <summary>
        /// Called when phase manager is initialized. Override to customize settings.
        /// </summary>
        protected virtual void InitializePhaseManager(BehaviorPhaseManager<TPhase> manager)
        {
            // Default: use abstract methods for configuration
            manager.CustomPhaseTimeouts = phase => 
            {
                float timeout = GetPhaseTimeout(phase);
                return timeout > 0 ? timeout : (float?)null;
            };
            manager.PhaseDescriptions = GetPhaseDescription;
        }
        
        /// <summary>
        /// Called when a phase transition occurs.
        /// </summary>
        protected virtual void OnPhaseChanged(TPhase fromPhase, TPhase toPhase)
        {
            // Override for custom phase transition handling
        }
        
        /// <summary>
        /// Called when a phase times out. Return the phase to transition to,
        /// or the same phase to stay (and handle it in UpdatePhase).
        /// </summary>
        protected virtual TPhase OnPhaseTimeout(TPhase timedOutPhase)
        {
            // Default: log warning and stay in same phase
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.LogWarning($"[{BehaviorName}] {Companion?.companionName} phase {timedOutPhase} timed out after {TimeInCurrentPhase:F1}s");
            }
            return timedOutPhase;
        }
        
        /// <summary>
        /// Gets the search radius for resource operations.
        /// Default uses CompanionSettings.ChestSearchRadius.
        /// </summary>
        protected virtual float GetResourceSearchRadius()
        {
            return CompanionSettings.ChestSearchRadius;
        }
        
        #endregion
        
        #region Lifecycle
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            // Cache components
            Character = companion.GetComponent<Character>();
            Humanoid = companion.GetComponent<Humanoid>();
            Inventory = companion.GetComponent<CompanionInventory>();
            CombatMovement = companion.GetComponent<CompanionCombatMovement>();
            Rigidbody = companion.GetComponent<Rigidbody>();
            ZAnim = companion.GetComponent<ZSyncAnimation>();
            AnimationController = companion.GetComponent<CompanionAnimationController>();
            
            // Create resource access service
            Resources = new ResourceAccessService(
                Inventory,
                () => Transform?.position ?? Vector3.zero,
                GetResourceSearchRadius()
            );
            Resources.BehaviorName = BehaviorName;
            Resources.VerboseLogging = CompanionIdleBehavior.VerboseLogging;
        }
        
        public override void Start()
        {
            base.Start();
            
            // Create and configure phase manager
            PhaseManager = new BehaviorPhaseManager<TPhase>(InitialPhase, BehaviorName, Companion?.companionName);
            PhaseManager.VerboseLogging = CompanionIdleBehavior.VerboseLogging;
            PhaseManager.DefaultPhaseTimeout = 30f;
            
            // Hook up events
            PhaseManager.OnPhaseChanged = (from, to) => OnPhaseChanged(from, to);
            PhaseManager.OnPhaseTimeout = (phase, time) =>
            {
                TPhase nextPhase = OnPhaseTimeout(phase);
                if (!nextPhase.Equals(phase))
                {
                    SetPhase(nextPhase);
                }
            };
            
            // Allow subclass customization
            InitializePhaseManager(PhaseManager);
            
            // Refresh nearby chests
            Resources.RefreshNearbyChests(true);
            
            // Set command priority for the behavior duration
            if (CombatMovement != null)
            {
                CombatMovement.SetCommandPriorityDuration(MaxDuration);
            }
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            // Check global timeout
            if (IsTimedOut())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                {
                    Debug.Log($"[{BehaviorName}] {Companion?.companionName} global timeout after {MaxDuration}s");
                }
                Complete();
                return true;
            }
            
            // Check phase timeout
            if (PhaseManager.IsPhaseTimedOut())
            {
                // OnPhaseTimeout callback will handle transition
            }
            
            // Update current phase
            return UpdatePhase(CurrentPhase);
        }
        
        public override void Cancel()
        {
            // Stop animations
            if (AnimationController != null)
            {
                AnimationController.StopAllAnimations();
            }
            
            // Clear movement
            CombatMovement?.UnlockMovement();
            CombatMovement?.ClearCommandPriority();
            
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            return PhaseManager?.GetPhaseDescription() ?? BehaviorName;
        }
        
        #endregion
        
        #region Phase Management
        
        /// <summary>
        /// Transitions to a new phase.
        /// </summary>
        protected void SetPhase(TPhase newPhase)
        {
            PhaseManager?.SetPhase(newPhase);
        }
        
        /// <summary>
        /// Resets the current phase timer (gives more time).
        /// </summary>
        protected void ResetPhaseTimer()
        {
            PhaseManager?.ResetPhaseTimer();
        }
        
        /// <summary>
        /// Extends the current phase timer.
        /// </summary>
        protected void ExtendPhaseTimer(float additionalTime)
        {
            PhaseManager?.ExtendPhaseTimer(additionalTime);
        }
        
        #endregion
        
        #region Movement Helpers
        
        /// <summary>
        /// Current movement target for continuous pathfinding.
        /// </summary>
        protected Vector3? CurrentMoveTarget { get; private set; }
        
        /// <summary>
        /// Whether movement is currently active.
        /// </summary>
        protected bool IsMoving => CurrentMoveTarget.HasValue;
        
        /// <summary>
        /// Sets a movement target. Call this once to start moving.
        /// The movement will continue every frame until stopped or destination reached.
        /// Uses base class TryMoveToPosition for authority-aware movement with vanilla pathfinding.
        /// </summary>
        protected void MoveToPosition(Vector3 position, bool useWalk = true)
        {
            CurrentMoveTarget = position;
            
            // Initial movement request
            if (!base.TryMoveToPosition(position, useWalk, !useWalk))
            {
                if (CompanionIdleBehavior.VerboseLogging)
                {
                    Debug.Log($"[{BehaviorName}] {Companion?.companionName} MoveToPosition blocked - no authority");
                }
            }
        }
        
        /// <summary>
        /// Continues movement toward the current target.
        /// MUST be called every frame while in a "Moving" phase to keep vanilla pathfinding working.
        /// Returns true if destination reached.
        /// </summary>
        protected bool ContinueMovement()
        {
            if (!CurrentMoveTarget.HasValue)
                return true;  // No target = done
            
            // Check if arrived - use forgiving threshold since pathfinding may not reach exact spot
            float dist = DistanceTo(CurrentMoveTarget.Value);
            if (dist < InteractionPointHelper.ARRIVAL_THRESHOLD)
            {
                CurrentMoveTarget = null;
                return true;
            }
            
            // CRITICAL: Call TryMoveToPosition EVERY FRAME for vanilla pathfinding to work!
            // Vanilla MoveTo() needs continuous calls to follow the path waypoints.
            base.TryMoveToPosition(CurrentMoveTarget.Value, walk: true, run: false);
            
            return false;
        }
        
        /// <summary>
        /// Stops all movement and releases authority.
        /// Uses base class StopMovement for proper authority cleanup.
        /// </summary>
        protected new void StopMovement()
        {
            CurrentMoveTarget = null;
            
            // Use base class authority-aware stop
            base.StopMovement();
            
            // Also zero velocity directly for immediate effect
            if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Vector3 vel = Rigidbody.linearVelocity;
                Rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
        }
        
        /// <summary>
        /// Faces a target position.
        /// </summary>
        protected void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - Transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) return; // Too close to target, don't rotate

            // Single facing-writer: face the station/target through the FacingAuthority at SubBehavior
            // priority. Combat (70) still preempts if a fight interrupts the work. Direct write fallback.
            var facing = Companion != null ? Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, 0.4f))
                    facing.SetLookDirection(BehaviorName, dir);
                return;
            }

            dir.Normalize();
            Quaternion targetRot = Quaternion.LookRotation(dir);
            if (float.IsNaN(targetRot.x) || float.IsNaN(targetRot.y) || float.IsNaN(targetRot.z) || float.IsNaN(targetRot.w))
            {
                return;
            }
            Transform.rotation = Quaternion.Slerp(Transform.rotation, targetRot, Time.deltaTime * 5f);
        }
        
        /// <summary>
        /// Gets the distance to a position.
        /// </summary>
        protected float DistanceTo(Vector3 position)
        {
            return Vector3.Distance(Transform.position, position);
        }
        
        /// <summary>
        /// Checks if we're within interaction distance of a position.
        /// Uses InteractionPointHelper.MAX_INTERACTION_RANGE as the default.
        /// </summary>
        protected bool IsWithinInteractionDistance(Vector3 position, float interactionDistance = -1f)
        {
            if (interactionDistance < 0)
                interactionDistance = InteractionPointHelper.MAX_INTERACTION_RANGE;
            return DistanceTo(position) < interactionDistance;
        }
        
        #endregion
        
        #region Animation Helpers
        
        /// <summary>
        /// Plays an interaction animation.
        /// </summary>
        protected void PlayInteractAnimation()
        {
            if (AnimationController != null)
            {
                AnimationController.PlayInteractAnimation();
            }
            else if (ZAnim != null)
            {
                ZAnim.SetTrigger("interact");
            }
        }
        
        /// <summary>
        /// Plays work/crafting animation.
        /// </summary>
        protected void PlayWorkAnimation(bool enable)
        {
            if (AnimationController != null)
            {
                AnimationController.PlayWorkAnimation(enable);
            }
            else if (ZAnim != null)
            {
                ZAnim.SetBool("crafting", enable);
                ZAnim.SetBool("Working", enable);
            }
        }
        
        /// <summary>
        /// Plays an emote with timeout.
        /// </summary>
        protected void PlayEmote(string emoteName, float duration = 3f, bool persistent = false)
        {
            if (AnimationController != null)
            {
                AnimationController.PlayEmote(emoteName, duration, persistent);
            }
            else if (ZAnim != null)
            {
                ZAnim.SetTrigger(emoteName);
            }
        }
        
        #endregion
        
        #region Inventory Helpers
        
        /// <summary>
        /// Gets the companion's storage inventory.
        /// </summary>
        protected Inventory GetStorageInventory()
        {
            return Inventory?.GetStorageInventory();
        }
        
        /// <summary>
        /// Checks if inventory is full or nearly full.
        /// </summary>
        protected bool IsInventoryFull()
        {
            return Resources?.IsInventoryFullOrNearlyFull() ?? false;
        }
        
        /// <summary>
        /// Saves the inventory to ZDO.
        /// </summary>
        protected void SaveInventory()
        {
            Inventory?.SaveToZDO();
        }
        
        #endregion
        
        #region Logging
        
        /// <summary>
        /// Logs a message if verbose logging is enabled.
        /// </summary>
        protected void LogVerbose(string message)
        {
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] {Companion?.companionName} {message}");
            }
        }
        
        /// <summary>
        /// Logs a warning.
        /// </summary>
        protected void LogWarning(string message)
        {
            Debug.LogWarning($"[{BehaviorName}] {Companion?.companionName} {message}");
        }
        
        #endregion
    }
}
