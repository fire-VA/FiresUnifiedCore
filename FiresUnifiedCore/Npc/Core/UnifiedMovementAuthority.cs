using UnityEngine;
using System;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// UNIFIED MOVEMENT AUTHORITY - Single source of truth for ALL companion movement.
    /// 
    /// CRITICAL ARCHITECTURE:
    /// This is the ONLY component that should call Character.SetMoveDir() for companions.
    /// All other systems MUST go through this authority - no exceptions.
    /// 
    /// THE SLIDING BUG ROOT CAUSE:
    /// When multiple systems call SetMoveDir() in the same frame:
    /// 1. System A sets direction/velocity
    /// 2. System B overwrites with different values
    /// 3. Animator receives inconsistent speed input
    /// 4. Visual animation doesn't match actual movement = SLIDING
    /// 
    /// THE SOLUTION:
    /// 1. This component is the ONLY thing that calls Character.SetMoveDir()
    /// 2. All other systems request movement through TryAcquireAuthority() + SetMoveDirection()
    /// 3. Only ONE source can have authority at a time
    /// 4. Authority handoffs include a clean stop before the new source takes over
    /// 5. Movement is blocked until animator transitions to locomotion state
    /// 
    /// MOVEMENT SOURCES (Priority order - higher wins):
    /// 100 - Forced: Knockback, teleport, ragdoll
    ///  90 - PlayerCommand: Direct player commands (ping move, attack)
    ///  80 - Animation: Root motion, attack animations
    ///  70 - Combat: Combat AI movement (dodge, strafe, approach)
    ///  60 - SubBehavior: Idle sub-behaviors (fire tending, smelting)
    ///  50 - Following: Following the owner
    ///  40 - IdleWander: Idle wandering
    ///   0 - None: No movement
    /// 
    /// HOW OTHER SYSTEMS SHOULD USE THIS:
    /// 
    /// // WRONG - Never do this:
    /// _character.SetMoveDir(direction);
    /// 
    /// // RIGHT - Always do this:
    /// var authority = companion.GetMovementAuthority();
    /// if (authority.TryAcquireAuthority(MovementSource.Combat, "CombatMovement", 2f))
    /// {
    ///     authority.SetMoveDirection("CombatMovement", direction, walk, run);
    /// }
    /// // When done:
    /// authority.ReleaseAuthority("CombatMovement");
    /// </summary>
    public class UnifiedMovementAuthority : MonoBehaviour
    {
        #region Enums
        
        /// <summary>
        /// Movement source identifiers with priority values.
        /// Higher values = higher priority.
        /// </summary>
        public enum MovementSource
        {
            None = 0,
            IdleWander = 40,
            Following = 50,
            SubBehavior = 60,
            Combat = 70,
            Animation = 80,
            PlayerCommand = 90,
            Forced = 100
        }
        
        /// <summary>
        /// Current movement state for external queries.
        /// </summary>
        public enum MovementState
        {
            Stopped,        // No movement requested
            Starting,       // Movement requested, waiting for animation
            Moving,         // Actively moving with matching animation
            Stopping        // Stopping, waiting for animation to settle
        }
        
        #endregion
        
        #region Events
        
        /// <summary>Fired when authority changes hands.</summary>
        public event Action<MovementSource, MovementSource> OnAuthorityChanged;
        
        /// <summary>Fired when movement state changes.</summary>
        public event Action<MovementState, MovementState> OnMovementStateChanged;
        
        #endregion
        
        #region Properties
        
        /// <summary>Current movement source with authority.</summary>
        public MovementSource CurrentAuthority { get; private set; } = MovementSource.None;
        
        /// <summary>Name of the system that currently has authority.</summary>
        public string CurrentAuthorityOwner { get; private set; } = "";
        
        /// <summary>Current movement state.</summary>
        public MovementState CurrentMovementState { get; private set; } = MovementState.Stopped;
        
        /// <summary>Time when current authority was acquired.</summary>
        public float AuthorityAcquiredTime { get; private set; }
        
        /// <summary>Duration the current authority will last (0 = indefinite).</summary>
        public float AuthorityDuration { get; private set; }
        
        /// <summary>Current target position if using destination-based movement.</summary>
        public Vector3 CurrentDestination { get; private set; }
        
        /// <summary>Current move direction if using direction-based movement.</summary>
        public Vector3 CurrentMoveDirection { get; private set; }
        
        /// <summary>Whether destination-based movement is active.</summary>
        public bool HasDestination { get; private set; }
        
        /// <summary>Whether any movement is currently requested.</summary>
        public bool IsMovementRequested => CurrentMoveDirection.sqrMagnitude > 0.01f || HasDestination;
        
        /// <summary>Whether the animator is ready for movement (in locomotion state).</summary>
        public bool IsAnimatorReady => !_isAnimationBlocking && (_animator == null || IsAnimatorInLocomotion());
        
        /// <summary>Whether movement input should be applied this frame.</summary>
        public bool ShouldApplyMovement => IsMovementRequested && IsAnimatorReady && !_isMovementFrozen;
        
        /// <summary>Whether movement is completely frozen (sitting, emoting, etc.).</summary>
        public bool IsMovementFrozen => _isMovementFrozen;
        
        /// <summary>
        /// CRITICAL: Returns true if external SetMoveDir calls should be blocked.
        /// CompanionPatches uses this to intercept and block direct SetMoveDir calls.
        /// </summary>
        public bool ShouldBlockExternalMovement => _blockExternalMovement;
        
        /// <summary>
        /// Last direction that was actually applied to the character.
        /// Used by CompanionPatches to allow through the legitimate calls from this authority.
        /// </summary>
        public Vector3 LastAppliedDirection => _lastAppliedDirection;
        
        /// <summary>
        /// Frame number of the last legitimate SetMoveDir call from this authority.
        /// Used by CompanionPatches to distinguish our calls from external calls.
        /// </summary>
        public int LastAppliedFrame => _lastAppliedFrame;
        
        /// <summary>
        /// CRITICAL: True only during the exact moment SetMoveDir is being called by the authority.
        /// This flag is set before calling SetMoveDir and cleared after.
        /// CompanionPatches checks this to reliably identify authority calls.
        /// </summary>
        public bool IsCurrentlyApplying => _isCurrentlyApplying;
        
        public static bool VerboseLogging = false; // Set to true for debugging movement issues
        
        #endregion
        
        #region Fields
        
        // Core components
        private CompanionController _companion;
        private Character _character;
        private Humanoid _humanoid;
        private Rigidbody _rigidbody;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionStateController _stateController;
        
        // LOG THROTTLING - Only log significant state changes, not every frame
        private Vector3 _lastLoggedDirection;
        private MovementSource _lastLoggedAuthority = MovementSource.None;
        private string _lastLoggedOwner = "";
        private float _lastDirectionLogTime;
        private const float DIRECTION_LOG_INTERVAL = 2.0f;  // Only log direction changes every 2 seconds
        private const float DIRECTION_CHANGE_THRESHOLD = 0.3f;  // Only log if direction changed significantly
        
        // DENIED log throttling - don't spam logs when authority is denied
        private MovementSource _lastDeniedSource = MovementSource.None;
        private string _lastDeniedOwner = "";
        private float _lastDeniedLogTime;
        private const float DENIED_LOG_INTERVAL = 5.0f;  // Only log denied once per 5 seconds per source/owner
        
        // Authority timeout
        private float _authorityTimeoutTime;
        
        // Movement state machine
        private bool _isMovementFrozen;
        private string _freezeReason;
        private float _freezeEndTime;

        // Suspend/resume (generalized "park"): a higher-priority interrupter saves the preempted
        // owner's authority SLOT and reserves the ladder band at/below _suspendFloor so nothing
        // lower can grab the body during the interrupt. Resume() restores the saved slot, so an
        // interrupted behavior comes back exactly where it left off instead of re-racing from scratch.
        // The save is a slot (not a boolean), so nested interrupts compose: the save-stack IS the
        // priority ladder. NOTE: nothing calls SuspendBelow until the suspend/resume wiring step, so
        // _suspendActive is false and every guard below is inert until then (purely additive for now).
        private bool _suspendActive;
        private MovementSource _suspendFloor;
        private string _suspendResumer = "";
        private MovementSource _suspendedSource;
        private string _suspendedOwner = "";
        private float _suspendedRemaining;
        
        // Animation blocking
        private bool _isAnimationBlocking;
        private float _animationBlockEndTime;
        
        // Smooth movement application
        private Vector3 _targetMoveDirection;
        private Vector3 _appliedMoveDirection;
        private float _movementBlendSpeed = 10f;
        
        // Movement mode
        private bool _useWalk;
        private bool _useRun;
        
        // Frame tracking for duplicate calls
        private int _lastAuthorityFrame;
        private MovementSource _lastAttemptedSource;
        
        // Destination tracking
        private float _destinationReachedThreshold = 1.5f;
        
        // CRITICAL: External movement blocking
        // ENABLED - All movement systems are now integrated to use the authority
        // CompanionCombatMovement and CompanionAI route through this authority
        private bool _blockExternalMovement = true; // ENABLED - systems are integrated
        private Vector3 _lastAppliedDirection;
        private int _lastAppliedFrame;
        
        // CRITICAL: Flag set ONLY during the exact moment we're calling SetMoveDir
        // This is more reliable than frame comparison which can have timing issues
        private bool _isCurrentlyApplying = false;
        
        // Track last walk/run state to avoid redundant calls
        private bool _lastWalkState;
        private bool _lastRunState;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _character = GetComponent<Character>();
            _humanoid = GetComponent<Humanoid>();
            _rigidbody = GetComponent<Rigidbody>();
            _animator = GetComponentInChildren<Animator>(true);
            _zanim = GetComponent<ZSyncAnimation>();
            _stateController = GetComponent<CompanionStateController>();
        }
        
        private void Update()
        {
            // Check authority timeout
            if (CurrentAuthority != MovementSource.None && AuthorityDuration > 0)
            {
                if (Time.time >= _authorityTimeoutTime)
                {
                    if (VerboseLogging)
                        Debug.Log($"[MovementAuthority] {_companion?.companionName} authority timeout for {CurrentAuthorityOwner}");
                    ReleaseAuthority(CurrentAuthorityOwner);
                }
            }
            
            // Check freeze timeout
            if (_isMovementFrozen && _freezeEndTime > 0 && Time.time >= _freezeEndTime)
            {
                UnfreezeMovement();
            }
            
            // Check animation block timeout
            if (_isAnimationBlocking && _animationBlockEndTime > 0 && Time.time >= _animationBlockEndTime)
            {
                _isAnimationBlocking = false;
            }
            
            // Update movement state machine
            UpdateMovementState();
            
            // Apply movement - THIS IS THE ONLY PLACE SetMoveDir SHOULD BE CALLED
            ApplyMovement();
        }
        
        private void LateUpdate()
        {
            // Enforce freeze state in LateUpdate to override any errant movement
            if (_isMovementFrozen)
            {
                EnforceFreeze();
            }
        }
        
        #endregion
        
        #region Authority Management
        
        /// <summary>
        /// Attempts to acquire movement authority at the specified level.
        /// Returns true if authority was granted, false if blocked by higher priority.
        /// 
        /// CRITICAL: If authority is acquired, all previous movement is stopped before
        /// the new source can begin moving. This prevents sliding during handoffs.
        /// </summary>
        public bool TryAcquireAuthority(MovementSource source, string owner, float duration = 5f)
        {
            // Prevent duplicate acquisition in same frame
            if (Time.frameCount == _lastAuthorityFrame && source == _lastAttemptedSource)
            {
                return source == CurrentAuthority && owner == CurrentAuthorityOwner;
            }
            _lastAuthorityFrame = Time.frameCount;
            _lastAttemptedSource = source;
            
            // Check if frozen (only Forced can override freeze)
            if (_isMovementFrozen && source != MovementSource.Forced)
            {
                // Throttle DENIED logs
                if (source != _lastDeniedSource || owner != _lastDeniedOwner || Time.time - _lastDeniedLogTime > DENIED_LOG_INTERVAL)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} ({source}) DENIED - movement frozen ({_freezeReason})");
                    _lastDeniedSource = source;
                    _lastDeniedOwner = owner;
                    _lastDeniedLogTime = Time.time;
                }
                return false;
            }

            // SUSPEND FLOOR: while a higher source has suspended a band of the ladder, deny any
            // source at/below the floor that is not the resumer — so a lower behavior can't steal
            // the body during an interrupt, and the suspended owner resumes cleanly. (Inert until
            // the suspend/resume wiring step; _suspendActive is false before then.)
            if (_suspendActive && source != MovementSource.Forced &&
                owner != _suspendResumer && (int)source <= (int)_suspendFloor)
            {
                if (source != _lastDeniedSource || owner != _lastDeniedOwner || Time.time - _lastDeniedLogTime > DENIED_LOG_INTERVAL)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} ({source}) DENIED - band suspended below {_suspendFloor} by {_suspendResumer}");
                    _lastDeniedSource = source;
                    _lastDeniedOwner = owner;
                    _lastDeniedLogTime = Time.time;
                }
                return false;
            }

            // Check priority
            if ((int)source < (int)CurrentAuthority)
            {
                // Throttle DENIED logs - only log once per 5 seconds for same source/owner
                if (source != _lastDeniedSource || owner != _lastDeniedOwner || Time.time - _lastDeniedLogTime > DENIED_LOG_INTERVAL)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} ({source}) DENIED - {CurrentAuthorityOwner} ({CurrentAuthority}) has higher priority");
                    _lastDeniedSource = source;
                    _lastDeniedOwner = owner;
                    _lastDeniedLogTime = Time.time;
                }
                return false;
            }

            // EQUAL-PRIORITY INCUMBENCY GUARD
            // If a different owner already holds authority at the SAME priority level,
            // we deny rather than steal.  Without this guard, two systems at the same
            // priority (e.g. CompanionAI and CompanionCombatMovement, both at
            // MovementSource.Combat) call TryAcquireAuthority every frame and alternately
            // steal authority from each other in an infinite ping-pong loop:
            //
            //   Frame N   : CompanionAI ACQUIRED Combat (from CompanionCombatMovement)
            //   Frame N+1 : CompanionCombatMovement ACQUIRED Combat (from CompanionAI)
            //   Frame N+2 : CompanionAI ACQUIRED Combat (from CompanionCombatMovement)
            //   …
            //
            // Each swap calls StopMovementImmediate() and OnAuthorityChanged on the main
            // thread, which is enough work to lock the loading screen during a dungeon
            // teleport.  The correct rule for SAME-priority handoffs is: incumbent keeps
            // it until they explicitly call ReleaseAuthority (or the duration timeout
            // fires).  Strictly-higher-priority sources can still preempt as before.
            if ((int)source == (int)CurrentAuthority &&
                CurrentAuthority != MovementSource.None &&
                !string.IsNullOrEmpty(CurrentAuthorityOwner) &&
                owner != CurrentAuthorityOwner)
            {
                if (source != _lastDeniedSource || owner != _lastDeniedOwner || Time.time - _lastDeniedLogTime > DENIED_LOG_INTERVAL)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} ({source}) DENIED - {CurrentAuthorityOwner} already holds same-priority {CurrentAuthority} authority (no equal-priority preemption)");
                    _lastDeniedSource = source;
                    _lastDeniedOwner = owner;
                    _lastDeniedLogTime = Time.time;
                }
                return false;
            }

            // Same source re-acquiring is OK - silent extension
            if (source == CurrentAuthority && owner == CurrentAuthorityOwner)
            {
                // Just extend the duration - NO LOGGING (this is called every frame)
                if (duration > 0)
                {
                    AuthorityDuration = duration;
                    _authorityTimeoutTime = Time.time + duration;
                }
                return true;
            }
            
            // New authority - stop previous movement first
            if (CurrentAuthority != MovementSource.None && CurrentAuthority != source)
            {
                StopMovementImmediate();
            }
            
            // Grant authority
            var previousSource = CurrentAuthority;
            var previousOwner = CurrentAuthorityOwner;
            
            CurrentAuthority = source;
            CurrentAuthorityOwner = owner;
            AuthorityAcquiredTime = Time.time;
            AuthorityDuration = duration;
            _authorityTimeoutTime = duration > 0 ? Time.time + duration : float.MaxValue;
            
            // Only log when authority ACTUALLY changes hands (not silent extensions)
            // Suppress routine IdleWander acquisitions unless VerboseLogging is on
            if (previousSource != source || previousOwner != owner)
            {
                if (source != MovementSource.IdleWander || VerboseLogging)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} ACQUIRED {source} authority" +
                        (previousOwner != "" ? $" (from {previousOwner})" : ""));
                }
            }
            
            OnAuthorityChanged?.Invoke(previousSource, source);
            
            return true;
        }
        
        /// <summary>
        /// Releases movement authority if the caller is the current owner.
        /// Movement will be stopped cleanly.
        /// </summary>
        public void ReleaseAuthority(string owner)
        {
            if (CurrentAuthorityOwner != owner && !string.IsNullOrEmpty(CurrentAuthorityOwner))
            {
                return; // Not the owner
            }

            // If this owner had suspended a lower behavior, releasing (or timing out, which calls
            // this) must RESUME that behavior rather than dropping authority to None — otherwise the
            // suspended behavior is orphaned and stays parked forever (frozen). This is the single
            // most important freeze-safety of the suspend mechanism.
            if (_suspendActive && _suspendResumer == owner)
            {
                Resume(owner);
                return;
            }

            if (CurrentAuthority == MovementSource.None)
            {
                return; // Already released
            }
            
            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} released {CurrentAuthority} authority");
            
            // Stop movement before releasing
            StopMovementImmediate();
            
            var previousSource = CurrentAuthority;
            CurrentAuthority = MovementSource.None;
            CurrentAuthorityOwner = "";
            AuthorityDuration = 0;
            HasDestination = false;
            CurrentDestination = Vector3.zero;
            CurrentMoveDirection = Vector3.zero;
            
            OnAuthorityChanged?.Invoke(previousSource, MovementSource.None);
        }
        
        /// <summary>
        /// Forces release of all authority regardless of owner.
        /// Use sparingly - this interrupts any ongoing movement.
        /// </summary>
        public void ForceReleaseAllAuthority()
        {
            if (CurrentAuthority == MovementSource.None) return;
            
            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} FORCE release from {CurrentAuthorityOwner}");
            
            StopMovementImmediate();
            
            var previousSource = CurrentAuthority;
            CurrentAuthority = MovementSource.None;
            CurrentAuthorityOwner = "";
            AuthorityDuration = 0;
            HasDestination = false;
            CurrentDestination = Vector3.zero;
            CurrentMoveDirection = Vector3.zero;
            
            OnAuthorityChanged?.Invoke(previousSource, MovementSource.None);
        }
        
        /// <summary>
        /// Checks if a specific source can acquire authority without actually acquiring it.
        /// </summary>
        public bool CanAcquireAuthority(MovementSource source)
        {
            if (_isMovementFrozen && source != MovementSource.Forced)
                return false;
            return (int)source >= (int)CurrentAuthority;
        }
        
        /// <summary>
        /// Checks if the specified owner currently has authority.
        /// </summary>
        public bool HasAuthority(string owner)
        {
            return CurrentAuthorityOwner == owner && CurrentAuthority != MovementSource.None;
        }

        /// <summary>
        /// The universal "may I drive the body this frame?" query. A movement loop calls this at the
        /// TOP of its per-frame method and PARKS (returns, writes nothing) when it returns false —
        /// instead of writing a fallback zero to hold position. This is the generalized form of the
        /// proven `if (_idleBehavior.IsInSubBehavior) return;` park. Denial == do nothing.
        /// </summary>
        public bool CanWrite(string owner)
        {
            if (CurrentAuthority == MovementSource.None) return false;
            if (CurrentAuthorityOwner != owner) return false;   // someone else owns it (incl. a suspender)
            if (_isMovementFrozen) return false;                // a frozen state owns the standstill
            return true;
        }

        /// <summary>
        /// Interrupt that preempts the current (lower-priority) owner AND remembers its slot so it can
        /// be restored later by Resume. Use this instead of a per-frame stop on the incumbent: the
        /// interrupter takes the body, the incumbent's loop parks (CanWrite goes false for it), and the
        /// band at/below <paramref name="floor"/> is reserved so nothing lower steals the slot meanwhile.
        /// Only takes effect if newSource strictly outranks the current owner (mirrors normal priority).
        /// </summary>
        public void SuspendBelow(MovementSource newSource, string newOwner, MovementSource floor, float duration = 0f)
        {
            // Save the slot we're suspending — but only if a strictly-lower source currently holds it.
            if (CurrentAuthority != MovementSource.None && (int)CurrentAuthority < (int)newSource)
            {
                _suspendedSource = CurrentAuthority;
                _suspendedOwner = CurrentAuthorityOwner;
                _suspendedRemaining = AuthorityDuration > 0 ? Mathf.Max(0f, _authorityTimeoutTime - Time.time) : 0f;
            }
            else
            {
                _suspendedSource = MovementSource.None;
                _suspendedOwner = "";
                _suspendedRemaining = 0f;
            }

            _suspendActive = true;
            _suspendFloor = floor;
            _suspendResumer = newOwner;

            // Clean stop on handoff, then take authority for the interrupter.
            if (CurrentAuthority != MovementSource.None && CurrentAuthority != newSource)
                StopMovementImmediate();

            var previousSource = CurrentAuthority;
            CurrentAuthority = newSource;
            CurrentAuthorityOwner = newOwner;
            AuthorityAcquiredTime = Time.time;
            AuthorityDuration = duration;
            _authorityTimeoutTime = duration > 0 ? Time.time + duration : float.MaxValue;

            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} {newOwner} ({newSource}) SUSPENDED below {floor}" +
                    (_suspendedOwner != "" ? $", saved slot {_suspendedSource}/{_suspendedOwner}" : ", nothing to save"));

            OnAuthorityChanged?.Invoke(previousSource, newSource);
        }

        /// <summary>
        /// Ends a SuspendBelow and restores the saved slot to its original owner (or releases to None
        /// if nothing was saved). Only the suspender may resume. Called explicitly by the interrupter
        /// when done, and automatically from ReleaseAuthority if the suspender's authority times out.
        /// </summary>
        public void Resume(string owner)
        {
            if (!_suspendActive || _suspendResumer != owner) return;

            _suspendActive = false;
            _suspendFloor = MovementSource.None;
            _suspendResumer = "";

            StopMovementImmediate();

            var previousSource = CurrentAuthority;
            CurrentAuthority = _suspendedSource;
            CurrentAuthorityOwner = _suspendedOwner;
            AuthorityDuration = _suspendedRemaining;
            _authorityTimeoutTime = _suspendedRemaining > 0 ? Time.time + _suspendedRemaining : float.MaxValue;
            HasDestination = false;
            CurrentMoveDirection = Vector3.zero;

            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} RESUMED slot {CurrentAuthority}/{CurrentAuthorityOwner} (from {previousSource}/{owner})");

            _suspendedSource = MovementSource.None;
            _suspendedOwner = "";
            _suspendedRemaining = 0f;

            OnAuthorityChanged?.Invoke(previousSource, CurrentAuthority);
        }
        
        /// <summary>
        /// Temporarily disables external movement blocking.
        /// Use this when you NEED to allow direct SetMoveDir calls (e.g., BaseAI pathfinding).
        /// Call EnableExternalBlocking() when done!
        /// </summary>
        public void DisableExternalBlocking()
        {
            _blockExternalMovement = false;
        }
        
        /// <summary>
        /// Re-enables external movement blocking.
        /// </summary>
        public void EnableExternalBlocking()
        {
            _blockExternalMovement = true;
        }
        
        #endregion
        
        #region Movement Commands
        
        /// <summary>
        /// Sets the movement direction for direction-based movement.
        /// Only works if caller has authority.
        /// </summary>
        public void SetMoveDirection(string owner, Vector3 direction, bool walk = false, bool run = false)
        {
            if (CurrentAuthorityOwner != owner)
            {
                // Only log denied calls once per owner change to avoid spam
                if (VerboseLogging)
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} SetMoveDirection DENIED - current owner is '{CurrentAuthorityOwner}'");
                return;
            }
            
            // THROTTLED LOGGING: Only log significant direction changes
            bool shouldLog = false;
            if (direction.sqrMagnitude > 0.01f)
            {
                // Log if direction changed significantly OR it's been a while
                float dirChange = Vector3.Distance(direction.normalized, _lastLoggedDirection.normalized);
                bool directionChanged = dirChange > DIRECTION_CHANGE_THRESHOLD;
                bool timeElapsed = Time.time - _lastDirectionLogTime > DIRECTION_LOG_INTERVAL;
                
                // Also log if we just started moving from stopped
                bool startedMoving = _lastLoggedDirection.sqrMagnitude < 0.01f;
                
                shouldLog = VerboseLogging && (directionChanged || timeElapsed || startedMoving);
                
                if (shouldLog)
                {
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} moving: dir={direction}, walk={walk}, run={run}");
                    _lastLoggedDirection = direction;
                    _lastDirectionLogTime = Time.time;
                }
            }
            else if (_lastLoggedDirection.sqrMagnitude > 0.01f)
            {
                // Log when stopping
                if (VerboseLogging)
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} stopping");
                _lastLoggedDirection = Vector3.zero;
            }
            
            HasDestination = false;
            _targetMoveDirection = direction;
            CurrentMoveDirection = direction;
            _useWalk = walk;
            _useRun = run;
        }
        
        /// <summary>
        /// Sets a destination for destination-based movement.
        /// Movement direction will be calculated automatically.
        /// Only works if caller has authority.
        /// </summary>
        public void SetMoveDestination(string owner, Vector3 destination, bool walk = false, bool run = false)
        {
            if (CurrentAuthorityOwner != owner)
            {
                if (VerboseLogging)
                    Debug.Log($"[MovementAuthority] {_companion?.companionName} {owner} SetMoveDestination DENIED");
                return;
            }
            
            // Only log new destinations
            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} destination set: {destination}");
            
            HasDestination = true;
            CurrentDestination = destination;
            _useWalk = walk;
            _useRun = run;
            
            // Calculate initial direction
            UpdateDestinationDirection();
        }
        
        /// <summary>
        /// Clears the current destination (stops destination-based movement).
        /// Movement will stop unless a new direction is set.
        /// </summary>
        public void ClearDestination(string owner)
        {
            if (CurrentAuthorityOwner != owner) return;

            HasDestination = false;
            CurrentDestination = Vector3.zero;
            _targetMoveDirection = Vector3.zero;
            CurrentMoveDirection = Vector3.zero;
        }

        /// <summary>
        /// OWNED STANDSTILL — the correct replacement for a behavior's per-frame SetMoveDir(0) hold.
        /// The owner commands stillness THROUGH the single writer and keeps its slot warm, so nothing
        /// lower can grab the body while it holds. Higher-priority sources (Combat/Command/Forced) can
        /// still preempt — unlike FreezeMovement, which blocks everything but Forced. Use Hold for
        /// behavior standstills (workstation, gather/loot finish, bow aim); use FreezeMovement for true
        /// frozen states (emote/chair/UI/root). A non-owner calling this is a no-op (it must park).
        /// </summary>
        public void Hold(string owner, string reason = "Hold")
        {
            if (CurrentAuthorityOwner != owner) return;

            HasDestination = false;
            _targetMoveDirection = Vector3.zero;
            CurrentMoveDirection = Vector3.zero;

            // Keep the authority warm so a finite duration doesn't time out mid-hold and drop the
            // body to a lower source.
            if (AuthorityDuration > 0)
                _authorityTimeoutTime = Time.time + AuthorityDuration;
        }
        
        /// <summary>
        /// Immediately stops all movement. Used during authority transitions.
        /// </summary>
        private void StopMovementImmediate()
        {
            _targetMoveDirection = Vector3.zero;
            _appliedMoveDirection = Vector3.zero;
            CurrentMoveDirection = Vector3.zero;
            HasDestination = false;
            
            // Stop character - THIS is the only legitimate SetMoveDir call
            ApplyMoveDirectionInternal(Vector3.zero, false, false);
            
            // Zero velocity
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
            
            SetMovementState(MovementState.Stopped);
        }
        
        #endregion
        
        #region Freeze Management
        
        /// <summary>
        /// Completely freezes movement for the specified duration.
        /// Only Forced authority can override a freeze.
        /// </summary>
        public void FreezeMovement(string reason, float duration = 0)
        {
            _isMovementFrozen = true;
            _freezeReason = reason;
            _freezeEndTime = duration > 0 ? Time.time + duration : 0;
            
            StopMovementImmediate();
            
            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} movement frozen: {reason}" +
                    (duration > 0 ? $" ({duration:F1}s)" : ""));
        }
        
        /// <summary>
        /// Unfreezes movement, allowing authority to be acquired again.
        /// </summary>
        /// <summary>
        /// Unfreezes only if the active freeze reason matches the caller's — so one system's unfreeze
        /// can't clobber a DIFFERENT system's freeze that overwrote the single freeze slot in between
        /// (e.g. a teleport/knockback freeze applied while a monk was meditating).
        /// </summary>
        public void UnfreezeMovement(string reason)
        {
            if (!_isMovementFrozen) return;
            if (_freezeReason != reason) return;
            UnfreezeMovement();
        }

        public void UnfreezeMovement()
        {
            if (!_isMovementFrozen) return;

            if (VerboseLogging)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} movement unfrozen (was: {_freezeReason})");
            
            _isMovementFrozen = false;
            _freezeReason = null;
            _freezeEndTime = 0;
        }
        
        /// <summary>
        /// Enforces freeze state - called every frame when frozen.
        /// </summary>
        private void EnforceFreeze()
        {
            ApplyMoveDirectionInternal(Vector3.zero, false, false);
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }
        
        #endregion
        
        #region Animation Blocking
        
        /// <summary>
        /// Notifies that an animation is starting that should block movement.
        /// </summary>
        public void NotifyAnimationStart(float duration)
        {
            _isAnimationBlocking = true;
            _animationBlockEndTime = Time.time + duration;
            
            // If we were moving, transition to stopping state
            if (CurrentMovementState == MovementState.Moving)
            {
                SetMovementState(MovementState.Stopping);
            }
        }
        
        /// <summary>
        /// Notifies that a blocking animation has ended.
        /// </summary>
        public void NotifyAnimationEnd()
        {
            _isAnimationBlocking = false;
            _animationBlockEndTime = 0;
        }
        
        /// <summary>
        /// Checks if the animator is in a locomotion state (walking/running).
        /// </summary>
        private bool IsAnimatorInLocomotion()
        {
            if (_animator == null) return true; // No animator = allow movement
            
            AnimatorStateInfo stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            
            // Check for locomotion tags
            if (stateInfo.IsTag("Locomotion") || stateInfo.IsTag("locomotion"))
                return true;
            
            // Check by common state names
            int stateHash = stateInfo.shortNameHash;
            if (stateHash == Animator.StringToHash("Walk") ||
                stateHash == Animator.StringToHash("Run") ||
                stateHash == Animator.StringToHash("walk") ||
                stateHash == Animator.StringToHash("run") ||
                stateHash == Animator.StringToHash("Locomotion") ||
                stateHash == Animator.StringToHash("locomotion"))
            {
                return true;
            }
            
            // Check speed parameters as fallback
            if (_animator.GetFloat("forward_speed") > 0.1f)
                return true;
            
            return false;
        }
        
        #endregion
        
        #region Movement State Machine
        
        private void UpdateMovementState()
        {
            // SIMPLIFIED: State is now updated directly in ApplyMovement()
            // This method just handles freeze checks
            
            // If frozen, force stopped state
            if (_isMovementFrozen && CurrentMovementState != MovementState.Stopped)
            {
                SetMovementState(MovementState.Stopped);
            }
        }
        
        private void SetMovementState(MovementState newState)
        {
            var oldState = CurrentMovementState;
            CurrentMovementState = newState;
            
            if (VerboseLogging && _companion?.isTamed == true)
                Debug.Log($"[MovementAuthority] {_companion?.companionName} state: {oldState} -> {newState}");
            
            OnMovementStateChanged?.Invoke(oldState, newState);
        }
        
        #endregion
        
        #region Movement Application
        
        private void ApplyMovement()
        {
            // If frozen, don't apply any movement
            if (_isMovementFrozen)
            {
                if (_appliedMoveDirection.sqrMagnitude > 0.01f)
                {
                    ApplyMoveDirectionInternal(Vector3.zero, false, false);
                    _appliedMoveDirection = Vector3.zero;
                }
                return;
            }
            
            // Update destination direction if using destination-based movement
            if (HasDestination)
            {
                UpdateDestinationDirection();
                
                // Check if reached destination
                float distToDest = Vector3.Distance(transform.position, CurrentDestination);
                if (distToDest < _destinationReachedThreshold)
                {
                    if (VerboseLogging)
                        Debug.Log($"[MovementAuthority] {_companion?.companionName} reached destination");
                    HasDestination = false;
                    _targetMoveDirection = Vector3.zero;
                    CurrentMoveDirection = Vector3.zero;
                }
            }
            
            // SIMPLIFIED: Just apply movement directly without complex state machine
            if (_targetMoveDirection.sqrMagnitude > 0.01f)
            {
                // Blend toward target direction for smooth movement
                _appliedMoveDirection = Vector3.Lerp(
                    _appliedMoveDirection, 
                    _targetMoveDirection, 
                    Time.deltaTime * _movementBlendSpeed);
                
                ApplyMoveDirectionInternal(_appliedMoveDirection, _useWalk, _useRun);
                
                // Update state to Moving
                if (CurrentMovementState != MovementState.Moving)
                {
                    SetMovementState(MovementState.Moving);
                }
            }
            else
            {
                // Stopping - blend to zero
                if (_appliedMoveDirection.sqrMagnitude > 0.01f)
                {
                    _appliedMoveDirection = Vector3.Lerp(
                        _appliedMoveDirection, 
                        Vector3.zero, 
                        Time.deltaTime * _movementBlendSpeed);
                    
                    if (_appliedMoveDirection.sqrMagnitude < 0.01f)
                    {
                        _appliedMoveDirection = Vector3.zero;
                    }
                    
                    ApplyMoveDirectionInternal(_appliedMoveDirection, false, false);
                    
                    if (CurrentMovementState != MovementState.Stopping)
                    {
                        SetMovementState(MovementState.Stopping);
                    }
                }
                else
                {
                    // Fully stopped
                    if (CurrentMovementState != MovementState.Stopped)
                    {
                        ApplyMoveDirectionInternal(Vector3.zero, false, false);
                        SetMovementState(MovementState.Stopped);
                    }
                }
            }
        }
        
        private void UpdateDestinationDirection()
        {
            if (!HasDestination) return;
            
            Vector3 toDestination = CurrentDestination - transform.position;
            toDestination.y = 0; // Keep movement horizontal
            
            if (toDestination.sqrMagnitude > 0.1f)
            {
                _targetMoveDirection = toDestination.normalized;
                CurrentMoveDirection = _targetMoveDirection;
            }
            else
            {
                _targetMoveDirection = Vector3.zero;
                CurrentMoveDirection = Vector3.zero;
            }
        }
        
        /// <summary>
        /// THE ONLY METHOD THAT SHOULD CALL Character.SetMoveDir()
        /// All other code paths should go through the authority system.
        /// </summary>
        private void ApplyMoveDirectionInternal(Vector3 direction, bool walk, bool run)
        {
            if (_character == null) return;
            if (_rigidbody != null && _rigidbody.isKinematic) return;
            
            // Track this call so CompanionPatches can distinguish our calls from external ones
            _lastAppliedDirection = direction;
            _lastAppliedFrame = Time.frameCount;
            
            // REMOVED: Per-frame logging was too spammy
            // Direction changes are already logged in SetMoveDirection()
            
            // CRITICAL: Set flag BEFORE calling SetMoveDir so Harmony patch can identify our call
            _isCurrentlyApplying = true;
            try
            {
                // Actually apply movement
                _character.SetMoveDir(direction);
            }
            finally
            {
                // CRITICAL: Clear flag AFTER SetMoveDir returns
                _isCurrentlyApplying = false;
            }
            
            // Only update walk/run if changed
            if (direction.sqrMagnitude > 0.01f)
            {
                if (walk != _lastWalkState)
                {
                    _character.SetWalk(walk);
                    _lastWalkState = walk;
                }
                if (run != _lastRunState)
                {
                    _character.SetRun(run);
                    _lastRunState = run;
                }
            }
            else
            {
                if (_lastWalkState)
                {
                    _character.SetWalk(false);
                    _lastWalkState = false;
                }
                if (_lastRunState)
                {
                    _character.SetRun(false);
                    _lastRunState = false;
                }
            }
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// Gets the current horizontal velocity magnitude.
        /// </summary>
        public float GetCurrentSpeed()
        {
            if (_rigidbody == null || _rigidbody.isKinematic) return 0f;
            
            Vector3 vel = _rigidbody.linearVelocity;
            return new Vector3(vel.x, 0, vel.z).magnitude;
        }
        
        /// <summary>
        /// Returns true if the companion is currently standing still (animation + velocity).
        /// </summary>
        public bool IsStandingStill()
        {
            return CurrentMovementState == MovementState.Stopped && GetCurrentSpeed() < 0.1f;
        }
        
        /// <summary>
        /// Returns debug info about current movement state.
        /// </summary>
        public string GetDebugInfo()
        {
            return $"Auth: {CurrentAuthority} ({CurrentAuthorityOwner})\n" +
                   $"State: {CurrentMovementState}\n" +
                   $"Frozen: {_isMovementFrozen} ({_freezeReason})\n" +
                   $"AnimBlock: {_isAnimationBlocking}\n" +
                   $"Speed: {GetCurrentSpeed():F2}\n" +
                   $"Dir: {CurrentMoveDirection}";
        }
        
        #endregion
    }
}
