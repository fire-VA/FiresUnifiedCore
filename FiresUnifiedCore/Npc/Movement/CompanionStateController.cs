using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Centralized state controller for companion movement and animations.
    /// 
    /// PURPOSE:
    /// This class is the single source of truth for what "mode" a companion is in.
    /// All movement and animation systems should check this controller before acting.
    /// 
    /// DESIGN PRINCIPLES:
    /// 1. EXCLUSIVE STATES: Only one major state can be active at a time
    /// 2. PRIORITY: Higher priority states override lower priority ones
    /// 3. MOVEMENT LOCKING: When in certain states, movement systems are completely disabled
    /// 4. CENTRALIZED CALLBACKS: All systems get notified of state changes
    /// 5. COMMAND AUTHORITY: Player commands have absolute priority until completed or cancelled
    /// 
    /// STATE HIERARCHY (highest to lowest priority):
    /// 1. UIInteraction - Player has a UI panel open for this companion
    /// 2. PlayerCommand - Executing a player-issued command (ABSOLUTE authority)
    /// 3. Emote - Playing a persistent emote (sit, dance, etc.)
    /// 4. ChairSit - Sitting on a chair/furniture
    /// 5. SubBehavior - Complex idle behavior (training, etc.)
    /// 6. Combat - In combat mode
    /// 7. Following - Following the player
    /// 8. Idle - Default idle/wander state
    /// 
    /// USAGE:
    /// - Call TryEnterState() to request a state change
    /// - Call ExitState() when leaving a state
    /// - Check IsMovementAllowed before processing movement
    /// - Subscribe to OnStateChanged for notifications
    /// 
    /// INTEGRATION:
    /// Other systems should call these methods to check if they can act:
    /// - IsMovementAllowed: Can AI/follow systems issue movement commands?
    /// - ShouldSkipAIUpdate: Should CompanionAI skip its Update entirely?
    /// - ShouldSkipCombatMovement: Should CompanionCombatMovement skip processing?
    /// - HasActiveCommand: Is there an active player command?
    /// </summary>
    public class CompanionStateController : MonoBehaviour
    {
        // Verbose per-command / per-state-transition tracing from the movement/patrol debugging era. Off by
        // default (const false → the guarded Debug.Log calls compile out), so it can't spam the server log.
        private const bool VerboseLog = false;

        #region Enums
        
        /// <summary>
        /// The primary states a companion can be in.
        /// Higher values = higher priority.
        /// </summary>
        public enum CompanionState
        {
            Idle = 0,
            Following = 10,
            Combat = 20,
            SubBehavior = 30,
            ChairSit = 40,
            Emote = 50,
            PlayerCommand = 75,  // Player commands have very high priority
            UIInteraction = 100
        }
        
        /// <summary>
        /// Types of player commands that can be issued.
        /// </summary>
        public enum CommandType
        {
            None,
            Move,           // Move to position (may pick up loot) - ABSOLUTE PRIORITY
            Attack,         // Attack a target - ABSOLUTE PRIORITY
            Sit,            // Sit on furniture
            Train,          // Train at archery target
            Gather,         // Gather resources
            Interact,       // Generic interaction
            SubBehavior     // Delegate to a sub-behavior (smelter, fire, etc.)
        }
        
        /// <summary>
        /// Movement priority levels - higher priority inputs override lower ones.
        /// When a higher priority movement is active, lower priority inputs are COMPLETELY IGNORED.
        /// This prevents movement conflicts, sliding, and animation glitches.
        /// 
        /// CRITICAL: Player commands (Command) have ABSOLUTE priority over AI decisions.
        /// When a player tells their companion to do something, it MUST happen.
        /// Move and Attack commands interrupt EVERYTHING including combat.
        /// </summary>
        public enum MovementPriority
        {
            None = 0,           // No movement requested
            Idle = 10,          // Background wandering
            SubBehavior = 20,   // Sub-behavior movement (chores, training) - lower than Following now
            Following = 30,     // Following owner - higher than sub-behaviors
            Combat = 40,        // Combat movement (AI-initiated)
            Animation = 50,     // Animation-driven movement (attacks, emotes)
            Command = 100,      // Player-issued command - ABSOLUTE priority (higher than Forced)
            Forced = 90         // Forced movement (knockback, teleport) - Commands can override this
        }
        
        /// <summary>
        /// Animation states that affect movement.
        /// Standing animations completely lock movement to prevent sliding.
        /// </summary>
        public enum AnimationState
        {
            None,           // No specific animation - movement allowed
            Idle,           // Idle standing - movement allowed but should transition smoothly
            Standing,       // Standing still pose - NO MOVEMENT until transition
            Walking,        // Walking animation playing
            Running,        // Running animation playing
            Attacking,      // Attack animation in progress - NO MOVEMENT
            Blocking,       // Block stance - limited movement
            Staggered,      // Hit stagger - NO MOVEMENT
            Jumping,        // Jump animation - physics-driven
            Emoting,        // Emote animation - NO MOVEMENT
            Sitting,        // Sitting on furniture - NO MOVEMENT
            Interacting     // Interacting with object - NO MOVEMENT
        }
        
        #endregion
        
        #region Events
        
        /// <summary>
        /// Fired when the companion state changes.
        /// </summary>
        public event Action<CompanionState, CompanionState> OnStateChanged;
        
        /// <summary>
        /// Fired when movement is locked or unlocked.
        /// </summary>
        public event Action<bool> OnMovementLockChanged;
        
        #endregion
        
        #region Properties
        
        /// <summary>Current primary state.</summary>
        public CompanionState CurrentState { get; private set; } = CompanionState.Idle;
        
        /// <summary>Previous state before the current one.</summary>
        public CompanionState PreviousState { get; private set; } = CompanionState.Idle;
        
        /// <summary>Time when the current state was entered.</summary>
        public float StateEnteredTime { get; private set; }
        
        /// <summary>Duration the companion has been in the current state.</summary>
        public float TimeInCurrentState => Time.time - StateEnteredTime;
        
        /// <summary>Whether movement is currently allowed.</summary>
        public bool IsMovementAllowed => !_isMovementLocked && !IsInFrozenState;
        
        /// <summary>
        /// Whether the companion is in a state that freezes all movement.
        /// CRITICAL: This does NOT include PlayerCommand - commands need movement!
        /// Frozen states are: ChairSit, Emote, UIInteraction
        /// </summary>
        public bool IsInFrozenState => CurrentState == CompanionState.ChairSit || 
                                        CurrentState == CompanionState.Emote ||
                                        CurrentState == CompanionState.UIInteraction;
        
        /// <summary>Whether AI decision-making is allowed.</summary>
        public bool IsAIAllowed => CurrentState < CompanionState.SubBehavior;
        
        /// <summary>Whether idle behaviors are allowed.</summary>
        public bool IsIdleBehaviorAllowed => CurrentState == CompanionState.Idle || CurrentState == CompanionState.Following;
        
        /// <summary>The current emote being played, if any.</summary>
        public string CurrentEmote => _currentEmote;
        
        /// <summary>Whether currently playing a persistent emote.</summary>
        public bool IsPlayingPersistentEmote => CurrentState == CompanionState.Emote && _isPersistentEmote;
        
        /// <summary>Whether there is an active player command.</summary>
        public bool HasActiveCommand => _activeCommandType != CommandType.None;
        
        /// <summary>The current command type being executed.</summary>
        public CommandType ActiveCommandType => _activeCommandType;
        
        /// <summary>The target position for the current command.</summary>
        public Vector3 CommandTargetPosition => _commandTargetPosition;
        
        /// <summary>The target object for the current command (may be null).</summary>
        public GameObject CommandTargetObject => _commandTargetObject;
        
        /// <summary>Time when the current command was issued.</summary>
        public float CommandStartTime => _commandStartTime;
        
        /// <summary>
        /// Whether a player command should completely override AI behavior.
        /// When true, the AI should skip ALL decision-making and let the command execute.
        /// CRITICAL: This returns true even during combat - player commands have ABSOLUTE priority.
        /// </summary>
        public bool IsPlayerCommandActive => HasActiveCommand || CurrentState == CompanionState.PlayerCommand;
        
        /// <summary>
        /// Returns true if a player Move or Attack command is active.
        /// These commands have ABSOLUTE priority and interrupt EVERYTHING including combat.
        /// Other systems MUST check this and yield control when true.
        /// </summary>
        public bool HasAbsolutePriorityCommand => HasActiveCommand && 
            (_activeCommandType == CommandType.Move || _activeCommandType == CommandType.Attack);
        
        /// <summary>Current movement priority level.</summary>
        public MovementPriority CurrentMovementPriority => _currentMovementPriority;
        
        /// <summary>Current animation state.</summary>
        public AnimationState CurrentAnimationState => _currentAnimationState;
        
        /// <summary>
        /// Whether movement input is currently allowed.
        /// Returns false if in a standing/frozen animation or if movement is locked.
        /// CRITICAL: Also returns false if animator is in idle but we're trying to move,
        /// to prevent the "sliding while standing" visual glitch.
        /// </summary>
        public bool CanAcceptMovementInput => !IsAnimationBlocking && !_isMovementLocked && !IsInFrozenState && !IsInStandingSlideState;
        
        /// <summary>
        /// Returns true if we're in the dangerous "standing slide" state where
        /// velocity has been applied but the animator hasn't transitioned to locomotion yet.
        /// Movement should be blocked until the animation catches up.
        /// </summary>
        public bool IsInStandingSlideState
        {
            get
            {
                if (_animator == null || _rigidbody == null) return false;
                if (_rigidbody.isKinematic) return false;
                
                // Get current horizontal velocity
                Vector3 vel = _rigidbody.linearVelocity;
                float horizontalSpeed = new Vector3(vel.x, 0, vel.z).magnitude;
                
                // If we have significant velocity but animator isn't in locomotion,
                // we're in the slide state
                if (horizontalSpeed > 0.3f && !IsAnimatorInLocomotion())
                {
                    return true;
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Whether the current animation blocks all movement.
        /// Standing, attacking, staggered, emoting, sitting, and interacting block movement.
        /// </summary>
        public bool IsAnimationBlocking => _currentAnimationState == AnimationState.Standing ||
                                           _currentAnimationState == AnimationState.Attacking ||
                                           _currentAnimationState == AnimationState.Staggered ||
                                           _currentAnimationState == AnimationState.Emoting ||
                                           _currentAnimationState == AnimationState.Sitting ||
                                           _currentAnimationState == AnimationState.Interacting;
        
        /// <summary>
        /// Should CompanionAI skip its entire Update loop?
        /// True when in frozen states (sitting/emoting/UI) but NOT for commands.
        /// Commands need AI to run for pathfinding - they just override target selection.
        /// </summary>
        public bool ShouldSkipAIUpdate => IsInFrozenState || IsAnimationBlocking;
        
        /// <summary>
        /// Should CompanionCombatMovement skip its movement processing?
        /// True when in frozen states or blocking animations.
        /// </summary>
        public bool ShouldSkipCombatMovement => IsInFrozenState || IsAnimationBlocking;
        
        /// <summary>
        /// Should BaseAI.UpdateAI() return early?
        /// True when in frozen states.
        /// </summary>
        public bool ShouldSkipBaseAI => IsInFrozenState;
        
        public static bool VerboseLogging = false;  // Disabled to reduce log spam

        // Mecanim trigger that the looping-emote state machine watches for to
        // exit (Dance â†’ Movement, Sit â†’ Movement, etc â€” see the Animator
        // controller's "emote_stop" condition on the emoteâ†’Movement
        // transitions). Without firing this, clearing the emote bool alone
        // is not enough to actually leave the looping state. Hashed once at
        // type-init for cheap lookup at runtime.
        private static readonly int EmoteStopTriggerHash = Animator.StringToHash("emote_stop");

        #endregion

        #region Fields
        
        private CompanionController _companion;
        private Character _character;
        private Rigidbody _rigidbody;
        private ZSyncAnimation _zanim;
        private Animator _animator;
        private UnifiedMovementAuthority _movementAuthority;
        
        // Movement lock state
        private bool _isMovementLocked;
        private float _movementLockEndTime;
        private string _movementLockReason;
        
        // Emote state
        private string _currentEmote;
        private bool _isPersistentEmote;
        private float _emoteEndTime;
        
        // Command state - centralized tracking for player commands
        private CommandType _activeCommandType = CommandType.None;
        private Vector3 _commandTargetPosition;
        private GameObject _commandTargetObject;
        private Character _commandTargetCharacter;
        private float _commandStartTime;
        private float _commandTimeout;
        private System.Action _onCommandComplete;
        private System.Action<string> _onCommandFailed;
        
        // Movement priority system - prevents conflicting inputs
        private MovementPriority _currentMovementPriority = MovementPriority.None;
        private MovementPriority _requestedMovementPriority = MovementPriority.None;
        private float _movementPriorityEndTime;
        private string _movementPriorityOwner;
        
        // Animation state tracking - links animations to movement permission
        private AnimationState _currentAnimationState = AnimationState.None;
        private float _animationStateEndTime;
        private float _lastAnimationStateCheck;
        private const float ANIMATION_STATE_CHECK_INTERVAL = 0.1f;
        
        // State timeout (for stuck prevention)
        private float _stateTimeout;
        private bool _hasStateTimeout;
        
        // Registered systems that need to be notified
        private List<ICompanionStateListener> _listeners = new List<ICompanionStateListener>();
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _character = GetComponent<Character>();
            _rigidbody = GetComponent<Rigidbody>();
            _zanim = GetComponent<ZSyncAnimation>();
            _animator = GetComponentInChildren<Animator>(true);
            _movementAuthority = GetComponent<UnifiedMovementAuthority>();
        }
        
        private void Update()
        {
            // Check for movement lock expiration
            if (_isMovementLocked && Time.time >= _movementLockEndTime)
            {
                UnlockMovement();
            }
            
            // Check for state timeout
            if (_hasStateTimeout && Time.time >= _stateTimeout)
            {
                HandleStateTimeout();
            }
            
            // Update command state (check timeout, completion, etc.)
            UpdateCommandState();
            
            // Update animation state from animator
            UpdateAnimationState();
            
            // Check movement priority expiration
            if (_currentMovementPriority != MovementPriority.None && Time.time >= _movementPriorityEndTime)
            {
                ReleaseMovementPriority(_movementPriorityOwner);
            }
            
            // Enforce freeze for frozen states OR blocking animations
            if (IsInFrozenState || IsAnimationBlocking)
            {
                EnforceFreeze();
            }
        }
        
        #endregion
        
        #region State Management
        
        /// <summary>
        /// Attempts to enter a new state. Returns true if successful.
        /// States can only be entered if they have equal or higher priority than current.
        /// </summary>
        public bool TryEnterState(CompanionState newState, float timeout = 0f, string reason = null)
        {
            // Can only enter states with equal or higher priority
            if ((int)newState < (int)CurrentState && CurrentState != CompanionState.Idle)
            {
                // Rate-limit this log to prevent spam when repeatedly trying to enter a blocked state
                if (ShouldLogVerbose() && ShouldLogStateRejection(newState))
                    if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} cannot enter {newState} - currently in {CurrentState}");
                return false;
            }
            
            CompanionState oldState = CurrentState;
            PreviousState = oldState;
            CurrentState = newState;
            StateEnteredTime = Time.time;
            
            // Set timeout if specified
            if (timeout > 0f)
            {
                _stateTimeout = Time.time + timeout;
                _hasStateTimeout = true;
            }
            else
            {
                _hasStateTimeout = false;
            }
            
            // Lock movement for frozen states (but NOT PlayerCommand - commands need movement!)
            if (IsInFrozenState && !_isMovementLocked && newState != CompanionState.PlayerCommand)
            {
                LockMovement(reason ?? newState.ToString(), timeout > 0f ? timeout + 1f : 999f);
            }
            
            // Notify listeners
            NotifyStateChanged(oldState, newState);
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} state: {oldState} -> {newState}" + 
                    (timeout > 0f ? $" (timeout: {timeout:F1}s)" : ""));
            
            return true;
        }
        
        /// <summary>
        /// Exits the current state and returns to the specified state (or Idle if not specified).
        /// </summary>
        public void ExitState(CompanionState returnTo = CompanionState.Idle)
        {
            if (CurrentState == returnTo) return;
            
            CompanionState oldState = CurrentState;
            PreviousState = oldState;
            CurrentState = returnTo;
            StateEnteredTime = Time.time;
            _hasStateTimeout = false;
            
            // Clear emote data
            if (oldState == CompanionState.Emote)
            {
                ForceStopEmote();
            }
            
            // Unlock movement when leaving frozen states
            if (oldState >= CompanionState.ChairSit && returnTo < CompanionState.ChairSit)
            {
                UnlockMovement();
            }
            
            NotifyStateChanged(oldState, returnTo);
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} exited {oldState} -> {returnTo}");
        }
        
        /// <summary>
        /// Forces an immediate return to Idle state, clearing all locks and emotes.
        /// Use this for emergency resets (combat start, player command, etc.)
        /// </summary>
        public void ForceReset()
        {
            CompanionState oldState = CurrentState;

            // Always call ForceStopEmote regardless of current state ï¿½ the emote
            // animation can be stuck even after the state has drifted away from Emote.
            ForceStopEmote();

            // Reset all state
            PreviousState = oldState;
            CurrentState = CompanionState.Idle;
            StateEnteredTime = Time.time;
            _hasStateTimeout = false;

            // Unlock movement
            UnlockMovement();

            // Reset animation state
            ResetAnimationState();

            NotifyStateChanged(oldState, CompanionState.Idle);

            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} FORCE RESET from {oldState}");
        }
        
        #endregion
        
        #region Emote Management
        
        /// <summary>
        /// Starts playing an emote. The companion will enter Emote state.
        /// </summary>
        public bool StartEmote(string emoteName, float duration, bool isPersistent = false)
        {
            if (string.IsNullOrEmpty(emoteName)) return false;
            
            // Try to enter emote state
            if (!TryEnterState(CompanionState.Emote, duration, "Emote:" + emoteName))
                return false;
            
            _currentEmote = emoteName;
            _isPersistentEmote = isPersistent;
            _emoteEndTime = Time.time + duration;
            
            // Play the animation
            if (isPersistent)
            {
                if (_zanim != null)
                    _zanim.SetBool(emoteName, true);
                else if (_animator != null && HasAnimatorParameter(emoteName))
                    _animator.SetBool(emoteName, true);
            }
            else
            {
                if (_zanim != null)
                    _zanim.SetTrigger(emoteName);
                else if (_animator != null)
                    _animator.SetTrigger(emoteName);
            }
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} started emote: {emoteName} " +
                    $"(persistent: {isPersistent}, duration: {duration:F1}s)");
            
            return true;
        }
        
        /// <summary>
        /// Stops the current emote and returns to Idle state.
        /// CRITICAL: This must fully reset animations before allowing movement.
        /// </summary>
        public void StopEmote()
        {
            ForceStopEmote();
        }

        /// <summary>
        /// Clears any active emote animation regardless of current state by calling
        /// the vanilla <c>Character.StopEmote()</c> method, which resets both the
        /// ZSyncAnimator bool AND the internal <c>m_emoteID</c> field that the
        /// Mecanim state machine uses to keep a looping emote alive.  Falls back to
        /// manually replicating those two steps if reflection can't find the method.
        ///
        /// Use this instead of <see cref="StopEmote"/> whenever an external event
        /// (player interaction, command, radial menu) must guarantee the companion
        /// is no longer playing an emote, even if the state controller has already
        /// transitioned away from <see cref="CompanionState.Emote"/>.
        /// </summary>
        public void ForceStopEmote()
        {
            // Call vanilla StopEmote() ï¿½ this sets the ZSyncAnimator emote bool to
            // false AND clears m_emoteID so the Mecanim state machine actually exits.
            if (_character != null)
            {
                var stopEmote = _character.GetType().GetMethod(
                    "StopEmote",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (stopEmote != null)
                {
                    stopEmote.Invoke(_character, null);
                }
                else
                {
                    // Fallback: replicate exactly what StopEmote does internally.
                    if (!string.IsNullOrEmpty(_currentEmote) && _zanim != null)
                        _zanim.SetBool(_currentEmote, false);

                    var emoteIdField = typeof(Character).GetField(
                        "m_emoteID",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    emoteIdField?.SetValue(_character, "");
                }
            }

            // Fire the emote_stop trigger on both the local Animator and
            // ZSyncAnimation. Looping emote states (Dance, Sit, Headbang,
            // Kneel, ...) have their exit transition gated on this trigger;
            // without it, clearing the bool alone leaves Mecanim parked in
            // the emote state and the visual keeps playing.
            if (_animator != null)
                _animator.SetTrigger(EmoteStopTriggerHash);
            if (_zanim != null)
                _zanim.SetTrigger("emote_stop");

            // Also clean up our own state-controller tracking so CurrentState,
            // _currentEmote, and _isPersistentEmote stay consistent.
            if (!string.IsNullOrEmpty(_currentEmote) || CurrentState == CompanionState.Emote)
            {
                ClearEmoteState();
                if (CurrentState == CompanionState.Emote)
                    ExitState(CompanionState.Idle);
            }
        }
        
        private void ClearEmoteState()
        {
            // CRITICAL: Clear the specific emote bool if it was persistent
            if (!string.IsNullOrEmpty(_currentEmote))
            {
                if (_zanim != null)
                    _zanim.SetBool(_currentEmote, false);
                if (_animator != null && HasAnimatorParameter(_currentEmote))
                    _animator.SetBool(_currentEmote, false);
            }
            
            _currentEmote = null;
            _isPersistentEmote = false;
            _emoteEndTime = 0f;
            
            // CRITICAL: Clear ALL common sitting/emote bools to ensure nothing is stuck
            // This is redundant with the specific clear above but ensures complete cleanup
            ClearCommonEmoteBools();
            
            // Also reset the animation state tracking
            if (_currentAnimationState == AnimationState.Emoting)
            {
                _currentAnimationState = AnimationState.None;
                _animationStateEndTime = 0f;
            }
        }
        
        private void ClearCommonEmoteBools()
        {
            // COMPREHENSIVE list of ALL emote and pose bools that could lock movement
            // This MUST include every possible emote to prevent sliding/stuck animations
            string[] emoteNames = new string[]
            {
                // Sitting and resting poses
                "sitting", "resting", "sleeping",
                
                // Persistent emotes (held for extended time) - emote_relax removed as it doesn't reset correctly
                "emote_sit", "emote_rest", "emote_vibe",
                "emote_kneel", "emote_despair", "emote_headbang", "emote_dance",
                
                // Quick emotes (one-shot but may have bool variants)
                "emote_point", "emote_wave", "emote_challenge",
                "emote_cheer", "emote_nonono", "emote_thumbsup", "emote_flex",
                "emote_laugh", "emote_shrug", "emote_blowkiss", "emote_bow",
                "emote_cry", "emote_comehere", "emote_roar", "emote_toast", "emote_loveyou",
                
                // Attach animations (chairs, ships, etc.)
                "attach_chair", "attach_stool", "attach_bed", "attach_mast",
                
                // Movement states that could be stuck
                "forward", "backward", "left", "right",
                "run", "walk", "crouch", "jump", "inwater"
            };
            
            // Clear all bools in both animation systems
            if (_zanim != null)
            {
                foreach (var emote in emoteNames)
                {
                    _zanim.SetBool(emote, false);
                }
                
                // Also zero movement floats
                _zanim.SetFloat("statef", 0f);
                _zanim.SetFloat("statei", 0f);
            }
            
            if (_animator != null)
            {
                foreach (var emote in emoteNames)
                {
                    if (HasAnimatorParameter(emote))
                        _animator.SetBool(emote, false);
                }
                
                // Zero locomotion speeds
                if (HasAnimatorParameter("forward_speed"))
                    _animator.SetFloat("forward_speed", 0f);
                if (HasAnimatorParameter("sideways_speed"))
                    _animator.SetFloat("sideways_speed", 0f);
                if (HasAnimatorParameter("turn_speed"))
                    _animator.SetFloat("turn_speed", 0f);
            }
        }
        
        #endregion
        
        #region Movement Lock
        
        /// <summary>
        /// Locks all movement for the specified duration.
        /// </summary>
        public void LockMovement(string reason, float duration)
        {
            _isMovementLocked = true;
            _movementLockEndTime = Time.time + duration;
            _movementLockReason = reason;
            
            OnMovementLockChanged?.Invoke(true);
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} movement locked: {reason} ({duration:F1}s)");
        }
        
        /// <summary>
        /// Unlocks movement immediately.
        /// Also unfreezes UnifiedMovementAuthority.
        /// </summary>
        public void UnlockMovement()
        {
            if (!_isMovementLocked) return;
            
            _isMovementLocked = false;
            _movementLockReason = null;
            
            // Sync unfreeze to UnifiedMovementAuthority
            if (_movementAuthority != null && _movementAuthority.IsMovementFrozen)
            {
                _movementAuthority.UnfreezeMovement();
            }
            
            OnMovementLockChanged?.Invoke(false);
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} movement unlocked");
        }
        
        /// <summary>
        /// Checks if movement is locked and returns the reason if so.
        /// </summary>
        public bool IsMovementLocked(out string reason)
        {
            reason = _movementLockReason;
            return _isMovementLocked;
        }
        
        /// <summary>
        /// Checks if movement is locked.
        /// </summary>
        public bool IsLocked => _isMovementLocked;
        
        #endregion
        
        #region Freeze Enforcement
        
        /// <summary>
        /// Enforces freeze state - called every frame when in a frozen state.
        /// This is the AUTHORITATIVE freeze - all other systems should check
        /// IsInFrozenState and skip their movement logic.
        /// Also notifies UnifiedMovementAuthority to freeze.
        /// </summary>
        private void EnforceFreeze()
        {
            // Sync freeze state to UnifiedMovementAuthority
            // The authority is responsible for calling SetMoveDir
            if (_movementAuthority != null && !_movementAuthority.IsMovementFrozen)
            {
                string reason = CurrentState.ToString();
                _movementAuthority.FreezeMovement(reason, 999f);
            }
            
            // Zero velocity if not kinematic
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            // Character movement is handled by UnifiedMovementAuthority.EnforceFreeze()
            // We don't call SetMoveDir directly anymore
        }
        
        #endregion
        
        #region State Timeout
        
        private void HandleStateTimeout()
        {
            _hasStateTimeout = false;
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} state timeout in {CurrentState}");
            
            // Handle timeout based on state
            switch (CurrentState)
            {
                case CompanionState.Emote:
                    ForceStopEmote();
                    break;
                    
                case CompanionState.ChairSit:
                case CompanionState.SubBehavior:
                    ExitState(CompanionState.Idle);
                    break;
                    
                case CompanionState.UIInteraction:
                    // UI states should be exited externally, but timeout as safety
                    ExitState(CompanionState.Idle);
                    break;
                    
                default:
                    ExitState(CompanionState.Idle);
                    break;
            }
        }
        
        #endregion
        
        #region Animation Reset
        
        /// <summary>
        /// Forces a complete reset of all animation state.
        /// This is the AUTHORITATIVE animation reset - call this to ensure clean state.
        /// </summary>
        public void ResetAnimationState()
        {
            // Use the comprehensive clear that covers ALL emote/movement bools
            ClearCommonEmoteBools();
            
            if (_zanim != null)
            {
                // Force idle trigger to transition to proper idle state
                _zanim.SetTrigger("idle");
            }
            
            if (_animator != null)
            {
                // Force animator to update immediately so changes take effect
                _animator.Update(0f);
            }
            
            // Reset internal animation state tracking
            _currentAnimationState = AnimationState.None;
            _animationStateEndTime = 0f;
            
            // Zero physics - only if NOT kinematic (Unity 6 compatibility)
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            // Character movement is handled by UnifiedMovementAuthority
            // We don't call SetMoveDir directly anymore
            // Force release any movement authority
            if (_movementAuthority != null)
            {
                _movementAuthority.ForceReleaseAllAuthority();
            }
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} animation state forcefully reset");
        }
        
        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }
        
        #endregion
        
        #region Listener System
        
        /// <summary>
        /// Registers a listener for state changes.
        /// </summary>
        public void RegisterListener(ICompanionStateListener listener)
        {
            if (!_listeners.Contains(listener))
                _listeners.Add(listener);
        }
        
        /// <summary>
        /// Unregisters a listener.
        /// </summary>
        public void UnregisterListener(ICompanionStateListener listener)
        {
            _listeners.Remove(listener);
        }
        
        private void NotifyStateChanged(CompanionState oldState, CompanionState newState)
        {
            OnStateChanged?.Invoke(oldState, newState);
            
            foreach (var listener in _listeners)
            {
                try
                {
                    listener.OnCompanionStateChanged(oldState, newState);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionStateController] Listener error: {ex.Message}");
                }
            }
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// Returns true if the companion can currently perform combat actions.
        /// </summary>
        public bool CanPerformCombat()
        {
            return CurrentState < CompanionState.ChairSit && !IsAnimationBlocking;
        }
        
        /// <summary>
        /// Returns true if the companion can currently be given commands.
        /// </summary>
        public bool CanReceiveCommands()
        {
            return CurrentState < CompanionState.UIInteraction;
        }
        
        /// <summary>
        /// Returns true if the companion is currently interacting (sitting, emoting, etc.)
        /// </summary>
        public bool IsInteracting()
        {
            return CurrentState >= CompanionState.ChairSit;
        }
        
        /// <summary>
        /// Returns true if the companion is in a state where it should be completely frozen.
        /// Other systems should call this and skip ALL movement processing when true.
        /// </summary>
        public bool ShouldFreeze()
        {
            return IsInFrozenState || IsAnimationBlocking;
        }
        
        #endregion
        
        #region Movement Priority System
        
        /// <summary>
        /// Attempts to acquire movement priority at the specified level.
        /// Returns true if priority was acquired or caller already has higher priority.
        /// Returns false if a higher priority movement is already active.
        /// 
        /// CRITICAL: Command priority (100) is ABSOLUTE and always granted.
        /// It overrides everything including animations and other priorities.
        /// </summary>
        public bool TryAcquireMovementPriority(MovementPriority priority, string owner, float duration = 5f)
        {
            // CRITICAL: Command priority is ABSOLUTE - always grant it
            if (priority == MovementPriority.Command)
            {
                // Force clear any animation blocking
                if (IsAnimationBlocking)
                {
                    SetAnimationState(AnimationState.None);
                }
                
                _currentMovementPriority = priority;
                _movementPriorityOwner = owner;
                _movementPriorityEndTime = Time.time + duration;
                return true;
            }
            
            // If animation is blocking, only Command priority can override (handled above)
            if (IsAnimationBlocking && priority < MovementPriority.Command)
            {
                if (ShouldLogVerbose())
                    if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} {owner} denied priority {priority} - animation blocking");
                return false;
            }
            
            // If current priority is Command, deny all non-Command requests
            if (_currentMovementPriority == MovementPriority.Command && priority != MovementPriority.Command)
            {
                if (ShouldLogVerbose())
                    if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} {owner} denied priority {priority} - Command priority active");
                return false;
            }
            
            // If current priority is higher, deny
            if ((int)_currentMovementPriority > (int)priority)
            {
                if (ShouldLogVerbose())
                    if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} {owner} denied priority {priority} - current {_currentMovementPriority} by {_movementPriorityOwner}");
                return false;
            }
            
            // Same or higher priority - grant it
            _currentMovementPriority = priority;
            _movementPriorityOwner = owner;
            _movementPriorityEndTime = Time.time + duration;
            
            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} {owner} acquired priority {priority} for {duration:F1}s");
            
            return true;
        }
        
        /// <summary>
        /// Releases movement priority if the caller is the current owner.
        /// </summary>
        public void ReleaseMovementPriority(string owner)
        {
            if (_movementPriorityOwner != owner && !string.IsNullOrEmpty(_movementPriorityOwner))
            {
                // Not the owner - can't release
                return;
            }
            
            if (ShouldLogVerbose() && _currentMovementPriority != MovementPriority.None)
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} {owner} released priority {_currentMovementPriority}");
            
            _currentMovementPriority = MovementPriority.None;
            _movementPriorityOwner = null;
            _movementPriorityEndTime = 0f;
        }
        
        /// <summary>
        /// Checks if a movement input should be accepted based on priority.
        /// Call this BEFORE issuing any movement commands.
        /// 
        /// CRITICAL: Command priority ALWAYS succeeds - player commands are absolute.
        /// </summary>
        public bool ShouldAcceptMovement(MovementPriority priority)
        {
            // CRITICAL: Command priority is ABSOLUTE
            if (priority == MovementPriority.Command)
                return true;
            
            // If Command priority is active, deny all other movement
            if (_currentMovementPriority == MovementPriority.Command)
                return false;
            
            // Animation blocking prevents all but Command movement
            if (IsAnimationBlocking && priority < MovementPriority.Command)
                return false;
            
            // Frozen state prevents non-Command movement
            if (IsInFrozenState)
                return false;
            
            // Movement lock prevents non-Command movement
            if (_isMovementLocked)
                return false;
            
            // Check priority - only same or higher can proceed
            return (int)priority >= (int)_currentMovementPriority;
        }
        
        /// <summary>
        /// Forces all movement priority to be cleared.
        /// Use sparingly - this interrupts any ongoing movement.
        /// </summary>
        public void ForceReleaseAllMovementPriority()
        {
            if (VerboseLogging && _currentMovementPriority != MovementPriority.None)
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} FORCE released all movement priority");
            
            _currentMovementPriority = MovementPriority.None;
            _movementPriorityOwner = null;
            _movementPriorityEndTime = 0f;
        }
        
        #endregion
        
        #region Animation State Tracking
        
        /// <summary>
        /// Updates animation state by reading from the animator.
        /// Called every frame to detect when animations start/end.
        /// </summary>
        private void UpdateAnimationState()
        {
            if (Time.time - _lastAnimationStateCheck < ANIMATION_STATE_CHECK_INTERVAL)
                return;
            _lastAnimationStateCheck = Time.time;
            
            // Check for animation state timeout
            if (_animationStateEndTime > 0 && Time.time >= _animationStateEndTime)
            {
                SetAnimationState(AnimationState.None);
            }
            
            // Detect animation state from animator/character
            AnimationState detectedState = DetectAnimationState();
            
            // Only update if state changed and we're not in a manually-set blocking state
            if (detectedState != _currentAnimationState && _animationStateEndTime <= 0)
            {
                SetAnimationState(detectedState);
            }
        }
        
        /// <summary>
        /// Detects current animation state from Character and Animator.
        /// CRITICAL: This now checks the actual animator state, not just velocity.
        /// This prevents the "sliding while standing" bug where velocity is applied
        /// before the locomotion animation has actually started.
        /// </summary>
        private AnimationState DetectAnimationState()
        {
            if (_character == null) return AnimationState.None;
            
            // Check for attack animation
            if (_character.InAttack())
                return AnimationState.Attacking;
            
            // Check for emote/sitting via state controller
            if (CurrentState == CompanionState.Emote)
                return AnimationState.Emoting;
            if (CurrentState == CompanionState.ChairSit)
                return AnimationState.Sitting;
            
            // CRITICAL FIX: Check actual animator state for locomotion
            // This is more accurate than velocity because it tells us if the CHARACTER
            // is visually in a walking/running animation, not just if physics is moving them
            if (_animator != null)
            {
                bool isInLocomotion = IsAnimatorInLocomotion();
                
                // Get velocity for additional context
                Vector3 velocity = Vector3.zero;
                if (_rigidbody != null)
                    velocity = _rigidbody.linearVelocity;
                float horizontalSpeed = new Vector3(velocity.x, 0, velocity.z).magnitude;
                
                // If animator is in idle/standing state but we have velocity,
                // we're in the dangerous "sliding" state - report as Standing to block movement
                if (!isInLocomotion && horizontalSpeed > 0.3f)
                {
                    return AnimationState.Standing; // Block movement until animation catches up
                }
                
                // If animator is in locomotion, use velocity to determine walk/run
                if (isInLocomotion)
                {
                    if (horizontalSpeed > 4f)
                        return AnimationState.Running;
                    if (horizontalSpeed > 0.3f)
                        return AnimationState.Walking;
                }
                
                // Animator is idle and no significant velocity - we're properly idle
                return AnimationState.Idle;
            }
            
            // Fallback for no animator - use velocity only
            Vector3 vel = Vector3.zero;
            if (_rigidbody != null)
                vel = _rigidbody.linearVelocity;
            
            float speed = new Vector3(vel.x, 0, vel.z).magnitude;
            
            if (speed < 0.1f)
                return AnimationState.Idle;
            if (speed > 4f)
                return AnimationState.Running;
            if (speed > 0.5f)
                return AnimationState.Walking;
            
            return AnimationState.Idle;
        }
        
        /// <summary>
        /// Checks if the animator is currently in a locomotion state (walking/running).
        /// This checks the actual animator state machine, not just parameters.
        /// </summary>
        /// <returns>True if the animator is playing a locomotion animation</returns>
        public bool IsAnimatorInLocomotion()
        {
            if (_animator == null) return false;
            
            // Get the current animator state info for the base layer (layer 0)
            AnimatorStateInfo stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            
            // Check if we're in a locomotion state by checking common locomotion tags/names
            // Valheim humanoids use these state names/tags
            if (stateInfo.IsTag("Locomotion") || stateInfo.IsTag("locomotion"))
                return true;
            
            // Also check by state name hash for common locomotion states
            // These are the typical state names in Valheim's humanoid animator
            int stateHash = stateInfo.shortNameHash;
            
            // Check for walk/run states
            if (stateHash == Animator.StringToHash("Walk") ||
                stateHash == Animator.StringToHash("Run") ||
                stateHash == Animator.StringToHash("walk") ||
                stateHash == Animator.StringToHash("run") ||
                stateHash == Animator.StringToHash("Locomotion") ||
                stateHash == Animator.StringToHash("locomotion") ||
                stateHash == Animator.StringToHash("Movement") ||
                stateHash == Animator.StringToHash("move"))
            {
                return true;
            }
            
            // Check normalized time - if we're in an idle state but forward_speed > 0,
            // we're transitioning TO locomotion
            if (_animator.GetFloat("forward_speed") > 0.1f || 
                _animator.GetFloat("sideways_speed") > 0.1f)
            {
                // There's movement intent, check if we're past the transition threshold
                // A very small normalized time in idle means we JUST started the transition
                return stateInfo.normalizedTime > 0.1f || stateInfo.IsTag("Locomotion");
            }
            
            return false;
        }
        
        /// <summary>
        /// Manually sets the animation state.
        /// Use this when you know an animation is starting (e.g., attack, emote).
        /// </summary>
        public void SetAnimationState(AnimationState state, float duration = 0f)
        {
            AnimationState oldState = _currentAnimationState;
            _currentAnimationState = state;
            
            if (duration > 0f)
                _animationStateEndTime = Time.time + duration;
            else
                _animationStateEndTime = 0f;
            
            // If entering a blocking state, enforce freeze immediately
            if (IsAnimationBlocking && !IsAnimationBlockingState(oldState))
            {
                EnforceFreeze();
                
                // Only log for companions actively following the player to reduce spam
                if (VerboseLogging && IsActivelyFollowing())
                    if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} animation state: {oldState} -> {state} (BLOCKING)");
            }
        }
        
        /// <summary>
        /// Returns true if this companion is actively following the player.
        /// Used to filter logging to only show relevant companions.
        /// </summary>
        private bool IsActivelyFollowing()
        {
            if (_companion == null) return false;
            return _companion.ShouldBeFollowing;
        }
        
        /// <summary>
        /// Returns true if verbose logging should be shown for this companion.
        /// Only logs for companions that are actively following the player.
        /// This reduces log spam from stayed/idle companions.
        /// </summary>
        private bool ShouldLogVerbose()
        {
            return VerboseLogging && IsActivelyFollowing();
        }
        
        // Rate limiting for state rejection logs to prevent spam
        private CompanionState _lastRejectedState;
        private float _lastRejectionLogTime;
        private const float STATE_REJECTION_LOG_INTERVAL = 5f; // Log same rejection at most every 5 seconds
        
        /// <summary>
        /// Rate-limits state rejection logs to prevent spam when repeatedly trying to enter a blocked state.
        /// </summary>
        private bool ShouldLogStateRejection(CompanionState rejectedState)
        {
            // If it's a different state than last rejection, always log
            if (rejectedState != _lastRejectedState)
            {
                _lastRejectedState = rejectedState;
                _lastRejectionLogTime = Time.time;
                return true;
            }
            
            // Same state - rate limit
            if (Time.time - _lastRejectionLogTime >= STATE_REJECTION_LOG_INTERVAL)
            {
                _lastRejectionLogTime = Time.time;
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a given animation state blocks movement.
        /// </summary>
        /// </summary>
        private bool IsAnimationBlockingState(AnimationState state)
        {
            return state == AnimationState.Standing ||
                   state == AnimationState.Attacking ||
                   state == AnimationState.Staggered ||
                   state == AnimationState.Emoting ||
                   state == AnimationState.Sitting ||
                   state == AnimationState.Interacting;
        }
        
        /// <summary>
        /// Notifies the state controller that an attack is starting.
        /// This sets AnimationState.Attacking and blocks movement.
        /// </summary>
        public void NotifyAttackStarted(float attackDuration)
        {
            SetAnimationState(AnimationState.Attacking, attackDuration);
        }
        
        /// <summary>
        /// Notifies the state controller that the companion was hit and is staggering.
        /// </summary>
        public void NotifyStaggered(float staggerDuration)
        {
            SetAnimationState(AnimationState.Staggered, staggerDuration);
        }
        
        /// <summary>
        /// Notifies the state controller that an animation has ended.
        /// </summary>
        public void NotifyAnimationEnded()
        {
            if (_currentAnimationState == AnimationState.Attacking ||
                _currentAnimationState == AnimationState.Staggered)
            {
                SetAnimationState(AnimationState.None);
            }
        }
        
        #endregion
        
        #region Command Management
        
        /// <summary>
        /// Starts a new player command. This clears any existing command and gives
        /// the new command absolute authority over the companion.
        /// 
        /// CRITICAL: Move and Attack commands have ABSOLUTE priority.
        /// They will interrupt EVERYTHING including:
        /// - Combat (companion will disengage and move to destination)
        /// - Sub-behaviors (chores, training)
        /// - Sitting/emotes
        /// - Any animation locks
        /// 
        /// This is by design - when the player tells their companion to do something,
        /// it could be to save their life. The companion MUST obey immediately.
        /// </summary>
        /// <param name="type">The type of command</param>
        /// <param name="targetPosition">The target position</param>
        /// <param name="targetObject">Optional target object</param>
        /// <param name="targetCharacter">Optional target character</param>
        /// <param name="timeout">Command timeout in seconds (0 = no timeout)</param>
        /// <param name="onComplete">Callback when command completes successfully</param>
        /// <param name="onFailed">Callback when command fails (receives reason string)</param>
        /// <returns>True if command was started</returns>
        public bool StartCommand(
            CommandType type,
            Vector3 targetPosition,
            GameObject targetObject = null,
            Character targetCharacter = null,
            float timeout = 60f,
            System.Action onComplete = null,
            System.Action<string> onFailed = null)
        {
            string companionName = _companion?.companionName ?? "Unknown";
            bool isAbsolutePriority = (type == CommandType.Move || type == CommandType.Attack);

            // ?? IDEMPOTENT RE-ISSUE ????????????????????????????????????????????
            // The command pipeline currently calls StartCommand from THREE layers
            // for a single user click:
            //   1) CompanionCommandSystem.IssueCommand
            //   2) CompanionCommandSystem.ExecuteMoveCommand
            //   3) CompanionCombatMovement.SetMoveDestination
            // and then any monitor coroutine / repeated input fires it again on
            // subsequent frames.  Each call used to tear down the existing
            // command (CancelCommand ? ForceReleaseAllMovementPriority ? ForceReset
            // ? re-acquire authority ? re-enter PlayerCommand state) which is a
            // huge amount of wasted work AND it caused the "behaviors fighting
            // each other" symptoms the owner reported ï¿½ every tear-down briefly
            // returned authority to lower-priority systems before grabbing it
            // back.
            //
            // If the new request is the SAME command (same type, same target
            // within 1 m, same object/character refs) as the one already running,
            // we just extend the timeout and refresh authority ï¿½ no destroy/rebuild,
            // no log spam, no priority release.  This is the player's command
            // staying their command.
            if (HasActiveCommand
                && _activeCommandType == type
                && (_commandTargetObject == targetObject)
                && (_commandTargetCharacter == targetCharacter)
                && (_commandTargetPosition - targetPosition).sqrMagnitude < 1.0f) // ?1 m drift
            {
                _commandTimeout = timeout > 0 ? Time.time + timeout : float.MaxValue;
                if (onComplete != null) _onCommandComplete = onComplete;
                if (onFailed != null) _onCommandFailed = onFailed;

                // Refresh authority lease so it doesn't expire mid-command ï¿½ but
                // do NOT release-and-reacquire (that's what caused the priority
                // ping-pong).  TryAcquireMovementPriority is a silent extension
                // when the same owner re-acquires the same level.
                TryAcquireMovementPriority(MovementPriority.Command, "PlayerCommand", timeout);

                // No log line on the refresh path ï¿½ this used to be the source of
                // the [COMMAND] StartCommand spam during held-input / coroutine
                // re-issues.
                return true;
            }
            // ????????????????????????????????????????????????????????????????????

            // Cancel any existing command first
            if (HasActiveCommand)
            {
                CancelCommand("New command issued", silent: true);
            }
            
            // For Move/Attack commands, FORCE INTERRUPT EVERYTHING
            if (isAbsolutePriority)
            {
                // Cancel all sub-behaviors immediately
                NotifySubBehaviorsCancelled("Player command issued");
                
                // Force release all movement priorities - we're taking over
                ForceReleaseAllMovementPriority();
                
                // Force reset from ANY state (sitting, emoting, combat, anything)
                ForceReset();
            }
            else
            {
                // Non-absolute commands still reset frozen states
                if (IsInFrozenState)
                {
                    ForceReset();
                }
                
                // Clear animation blocking state
                if (IsAnimationBlocking)
                {
                    SetAnimationState(AnimationState.None);
                }
            }
            
            _activeCommandType = type;
            _commandTargetPosition = targetPosition;
            _commandTargetObject = targetObject;
            _commandTargetCharacter = targetCharacter;
            _commandStartTime = Time.time;
            _commandTimeout = timeout > 0 ? Time.time + timeout : float.MaxValue;
            _onCommandComplete = onComplete;
            _onCommandFailed = onFailed;
            
            // Acquire movement priority at Command level (highest)
            TryAcquireMovementPriority(MovementPriority.Command, "PlayerCommand", timeout);
            
            // Enter PlayerCommand state
            TryEnterState(CompanionState.PlayerCommand, timeout, $"Command:{type}");

            return true;
        }
        
        // Event for notifying sub-behaviors they need to cancel
        public event System.Action<string> OnSubBehaviorsCancelled;
        
        /// <summary>
        /// Notifies all sub-behaviors that they should cancel immediately.
        /// Called when a player command takes absolute priority.
        /// </summary>
        private void NotifySubBehaviorsCancelled(string reason)
        {
            OnSubBehaviorsCancelled?.Invoke(reason);
        }
        
        /// <summary>
        /// Called when the current command completes successfully.
        /// </summary>
        public void CompleteCommand()
        {
            if (!HasActiveCommand) return;

            var type = _activeCommandType;
            var onComplete = _onCommandComplete;
            var extraHooks = _commandCompletionHooks;
            _commandCompletionHooks = null; // one-shot

            ClearCommandState();

            // Exit PlayerCommand state
            if (CurrentState == CompanionState.PlayerCommand)
            {
                ExitState(CompanionState.Idle);
            }

            if (ShouldLogVerbose())
                if (VerboseLog) Debug.Log($"[CompanionStateController] {_companion?.companionName} completed command: {type}");

            onComplete?.Invoke();
            extraHooks?.Invoke();
        }

        // Chain of additional completion hooks for the currently active command.
        // Used when callers further down the command pipeline need to attach a
        // post-arrival action (e.g. loot pickup) without overwriting the
        // primary onComplete callback that StartCommand installed.  Reset to null
        // on every command terminate (Complete / Fail / Cancel / ClearCommandState).
        private System.Action _commandCompletionHooks;

        /// <summary>
        /// Attaches an additional hook that will run alongside the primary
        /// <c>onComplete</c> callback when the currently active command finishes
        /// successfully.  No-op if no command is active.  Used by
        /// CompanionCommandSystem.ExecuteMoveCommand to chain on-arrival loot
        /// pickup without re-issuing StartCommand (which used to tear down +
        /// rebuild PlayerCommand authority redundantly).
        /// </summary>
        public void RegisterCommandCompletionHook(System.Action hook)
        {
            if (!HasActiveCommand || hook == null) return;
            _commandCompletionHooks += hook;
        }
        
        /// <summary>
        /// Called when the current command fails.
        /// Shows a message to the player explaining why.
        /// </summary>
        public void FailCommand(string reason)
        {
            if (!HasActiveCommand) return;
            
            var type = _activeCommandType;
            var onFailed = _onCommandFailed;
            string companionName = _companion?.companionName ?? "Companion";
            
            ClearCommandState();
            
            // Exit PlayerCommand state
            if (CurrentState == CompanionState.PlayerCommand)
            {
                ExitState(CompanionState.Idle);
            }
            
            // Show message to player
            ShowCommandFailure(companionName, type, reason);
            
            if (VerboseLog) Debug.Log($"[CompanionStateController] {companionName} command {type} FAILED: {reason}");
            
            onFailed?.Invoke(reason);
        }
        
        /// <summary>
        /// Cancels the current command without showing an error.
        /// </summary>
        public void CancelCommand(string reason = "Cancelled", bool silent = false)
        {
            if (!HasActiveCommand) return;
            
            var type = _activeCommandType;
            string companionName = _companion?.companionName ?? "Companion";
            
            ClearCommandState();
            
            // Exit PlayerCommand state
            if (CurrentState == CompanionState.PlayerCommand)
            {
                ExitState(CompanionState.Idle);
            }
            
            if (!silent && VerboseLogging)
                if (VerboseLog) Debug.Log($"[CompanionStateController] {companionName} command {type} cancelled: {reason}");
        }
        
        private void ClearCommandState()
        {
            _activeCommandType = CommandType.None;
            _commandTargetPosition = Vector3.zero;
            _commandTargetObject = null;
            _commandTargetCharacter = null;
            _commandStartTime = 0f;
            _commandTimeout = 0f;
            _onCommandComplete = null;
            _onCommandFailed = null;
            _commandCompletionHooks = null;
        }
        
        private void ShowCommandFailure(string companionName, CommandType type, string reason)
        {
            string message = type switch
            {
                CommandType.Move => $"{companionName}: Can't reach destination - {reason}",
                CommandType.Attack => $"{companionName}: Can't attack - {reason}",
                CommandType.Sit => $"{companionName}: Can't sit - {reason}",
                CommandType.Train => $"{companionName}: Can't train - {reason}",
                CommandType.Gather => $"{companionName}: Can't gather - {reason}",
                CommandType.Interact => $"{companionName}: Can't interact - {reason}",
                CommandType.SubBehavior => $"{companionName}: Can't do that - {reason}",
                _ => $"{companionName}: Command failed - {reason}"
            };
            
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }
        
        /// <summary>
        /// Updates command state - checks for timeout and completion.
        /// Called from Update().
        /// </summary>
        private void UpdateCommandState()
        {
            if (!HasActiveCommand) return;
            
            // Check timeout
            if (Time.time >= _commandTimeout)
            {
                FailCommand("Timed out");
                return;
            }
            
            // Check if target object was destroyed
            if (_commandTargetObject != null && !_commandTargetObject)
            {
                // Object was destroyed - could be success (gathered) or failure
                if (_activeCommandType == CommandType.Gather)
                {
                    CompleteCommand(); // Resource was gathered
                }
                else
                {
                    FailCommand("Target no longer exists");
                }
                return;
            }
            
            // Check if target character died
            if (_commandTargetCharacter != null && _commandTargetCharacter.IsDead())
            {
                if (_activeCommandType == CommandType.Attack)
                {
                    CompleteCommand(); // Target was killed
                }
                else
                {
                    FailCommand("Target is dead");
                }
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Interface for systems that want to be notified of companion state changes.
    /// </summary>
    public interface ICompanionStateListener
    {
        void OnCompanionStateChanged(CompanionStateController.CompanionState oldState, 
            CompanionStateController.CompanionState newState);
    }
}
