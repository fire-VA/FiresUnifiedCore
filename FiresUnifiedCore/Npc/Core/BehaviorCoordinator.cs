using System;
using UnityEngine;
using FiresCore.Npc;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// One view of a companion's behavior state across CompanionIdleBehavior, CompanionCombatMovement and
    /// CompanionStateController: it tracks the current state, coordinates idle-combat-idle transitions and raises
    /// lifecycle events. Reached through CompanionController.GetBehaviorCoordinator.
    /// </summary>
    public class BehaviorCoordinator : MonoBehaviour
    {
        #region State Enum
        
        /// <summary>
        /// High-level behavior states for the companion.
        /// </summary>
        public enum BehaviorState
        {
            /// <summary>Standing idle, no active behavior</summary>
            Idle,
            /// <summary>Following the owner</summary>
            Following,
            /// <summary>In combat with an enemy</summary>
            Combat,
            /// <summary>Executing a sub-behavior (smelting, gathering, etc.)</summary>
            Working,
            /// <summary>Sitting on a chair or playing an emote</summary>
            Relaxing,
            /// <summary>Executing a player command (move to, attack target)</summary>
            PlayerCommand,
            /// <summary>Frozen for UI interaction</summary>
            UIInteraction,
            /// <summary>Defeated/dead, waiting for respawn</summary>
            Defeated
        }
        
        #endregion
        
        #region Fields
        
        private CompanionController _companion;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionCombatMovement _combatMovement;
        private CompanionStateController _stateController;
        private AI.CompanionAI _companionAI;
        
        private BehaviorState _currentState = BehaviorState.Idle;
        private BehaviorState _previousState = BehaviorState.Idle;
        private float _stateStartTime;
        private string _stateReason;
        
        // Work session tracking
        private WorkSessionManager _workSessionManager;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// The current high-level behavior state.
        /// </summary>
        public BehaviorState CurrentState => _currentState;
        
        /// <summary>
        /// The previous state before the current one.
        /// </summary>
        public BehaviorState PreviousState => _previousState;
        
        /// <summary>
        /// Time when the current state started.
        /// </summary>
        public float StateStartTime => _stateStartTime;
        
        /// <summary>
        /// How long we've been in the current state.
        /// </summary>
        public float TimeInCurrentState => Time.time - _stateStartTime;
        
        /// <summary>
        /// Reason for the current state (for debugging).
        /// </summary>
        public string StateReason => _stateReason;
        
        /// <summary>
        /// The currently active sub-behavior, if any.
        /// </summary>
        public IdleSubBehavior ActiveSubBehavior => _idleBehavior?.ActiveSubBehavior;
        
        /// <summary>
        /// Whether the companion is currently in combat.
        /// </summary>
        public bool IsInCombat => _currentState == BehaviorState.Combat;
        
        /// <summary>
        /// Whether the companion is currently working (sub-behavior active).
        /// </summary>
        public bool IsWorking => _currentState == BehaviorState.Working;
        
        /// <summary>
        /// Whether the companion is available for new behaviors.
        /// </summary>
        public bool IsAvailable => _currentState == BehaviorState.Idle || _currentState == BehaviorState.Following;
        
        /// <summary>
        /// The work session manager for tracking long-running work.
        /// </summary>
        public WorkSessionManager WorkSession => _workSessionManager;
        
        #endregion
        
        #region Events
        
        /// <summary>
        /// Fired when the behavior state changes.
        /// Parameters: (oldState, newState, reason)
        /// </summary>
        public event Action<BehaviorState, BehaviorState, string> OnStateChanged;
        
        /// <summary>
        /// Fired when a sub-behavior starts.
        /// </summary>
        public event Action<IdleSubBehavior> OnBehaviorStarted;
        
        /// <summary>
        /// Fired when a sub-behavior ends.
        /// Parameters: (behavior, wasSuccessful, reason)
        /// </summary>
        public event Action<IdleSubBehavior, bool, string> OnBehaviorEnded;
        
        /// <summary>
        /// Fired when combat starts.
        /// </summary>
        public event Action OnCombatStarted;
        
        /// <summary>
        /// Fired when combat ends.
        /// </summary>
        public event Action OnCombatEnded;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _stateController = GetComponent<CompanionStateController>();
            _companionAI = GetComponent<AI.CompanionAI>();
            
            _workSessionManager = new WorkSessionManager(this);
            _stateStartTime = Time.time;
        }
        
        private void Update()
        {
            if (_companion == null || !_companion.isTamed) return;
            
            // Update state based on component states
            UpdateStateFromComponents();
            
            // Update work session
            _workSessionManager?.Update();
        }
        
        #endregion
        
        #region State Management
        
        /// <summary>
        /// Updates the coordinator state based on component states.
        /// Called each frame to keep state in sync.
        /// </summary>
        private void UpdateStateFromComponents()
        {
            BehaviorState newState = DetermineCurrentState();
            
            if (newState != _currentState)
            {
                TransitionTo(newState, GetStateReason(newState));
            }
        }
        
        /// <summary>
        /// Determines what state we should be in based on component states.
        /// </summary>
        private BehaviorState DetermineCurrentState()
        {
            // Priority 1: Defeated
            if (_companion != null && _companion.isDefeated)
            {
                return BehaviorState.Defeated;
            }
            
            // Priority 2: UI Interaction
            if (_stateController != null && _stateController.CurrentState == CompanionStateController.CompanionState.UIInteraction)
            {
                return BehaviorState.UIInteraction;
            }
            
            // Priority 3: Player Command
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
            {
                // Check if it's a work command or movement command
                if (_idleBehavior?.ActiveSubBehavior != null && _idleBehavior.ActiveSubBehavior.IsCommandInitiated)
                {
                    return BehaviorState.Working;
                }
                return BehaviorState.PlayerCommand;
            }
            
            // Priority 4: Combat
            if (_combatMovement != null && _combatMovement.IsInCombat)
            {
                return BehaviorState.Combat;
            }
            
            // Priority 5: Working (sub-behavior)
            if (_idleBehavior?.ActiveSubBehavior != null)
            {
                return BehaviorState.Working;
            }
            
            // Priority 6: Relaxing (emote/sitting)
            if (_idleBehavior != null && (_idleBehavior.IsPlayingEmote || _idleBehavior.IsSitting))
            {
                return BehaviorState.Relaxing;
            }
            
            // Priority 7: Following
            if (_companion != null && _companion.ShouldBeFollowing)
            {
                return BehaviorState.Following;
            }
            
            // Default: Idle
            return BehaviorState.Idle;
        }
        
        /// <summary>
        /// Gets a reason string for the given state.
        /// </summary>
        private string GetStateReason(BehaviorState state)
        {
            switch (state)
            {
                case BehaviorState.Defeated:
                    return "Companion defeated";
                case BehaviorState.UIInteraction:
                    return "Player interacting with UI";
                case BehaviorState.PlayerCommand:
                    return "Executing player command";
                case BehaviorState.Combat:
                    var target = _companionAI?.GetTargetCreature();
                    return target != null ? $"Fighting {target.m_name}" : "In combat";
                case BehaviorState.Working:
                    var behavior = _idleBehavior?.ActiveSubBehavior;
                    return behavior != null ? behavior.BehaviorName : "Working";
                case BehaviorState.Relaxing:
                    if (_idleBehavior?.IsSitting == true) return "Sitting";
                    if (_idleBehavior?.IsPlayingEmote == true) return "Emoting";
                    return "Relaxing";
                case BehaviorState.Following:
                    return "Following owner";
                case BehaviorState.Idle:
                default:
                    return "Idle";
            }
        }
        
        /// <summary>
        /// Transitions to a new state.
        /// </summary>
        public void TransitionTo(BehaviorState newState, string reason = null)
        {
            if (newState == _currentState) return;
            
            BehaviorState oldState = _currentState;
            _previousState = oldState;
            _currentState = newState;
            _stateStartTime = Time.time;
            _stateReason = reason ?? GetStateReason(newState);
            
            if (VerboseLogging)
            {
                Debug.Log($"[BehaviorCoordinator] {_companion?.companionName} state: {oldState} -> {newState} ({_stateReason})");
            }
            
            // Fire local event
            OnStateChanged?.Invoke(oldState, newState, _stateReason);
            
            // Fire global event
            if (_companion != null)
            {
                CompanionEvents.FireStateChanged(_companion, oldState.ToString(), newState.ToString());
            }
            
            // Handle specific transitions
            HandleStateTransition(oldState, newState);
        }
        
        /// <summary>
        /// Handles specific state transitions.
        /// </summary>
        private void HandleStateTransition(BehaviorState oldState, BehaviorState newState)
        {
            // Combat transitions
            if (newState == BehaviorState.Combat && oldState != BehaviorState.Combat)
            {
                OnCombatStarted?.Invoke();
                if (_companion != null)
                {
                    CompanionEvents.FireCombatStarted(_companion);
                    
                    // Notify GroupCombatCoordinator for multi-companion coordination
                    GroupCombatCoordinator.Instance?.OnCompanionEnteredCombat(_companion);
                }
            }
            else if (oldState == BehaviorState.Combat && newState != BehaviorState.Combat)
            {
                OnCombatEnded?.Invoke();
                if (_companion != null)
                {
                    CompanionEvents.FireCombatEnded(_companion);
                    
                    // Notify GroupCombatCoordinator that this companion left combat
                    GroupCombatCoordinator.Instance?.OnCompanionLeftCombat(_companion);
                }
            }
        }
        
        #endregion
        
        #region Behavior Management
        
        /// <summary>
        /// Attempts to start a sub-behavior.
        /// </summary>
        /// <typeparam name="T">The type of sub-behavior to start</typeparam>
        /// <returns>True if the behavior was started</returns>
        public bool TryStartBehavior<T>() where T : IdleSubBehavior
        {
            if (!IsAvailable)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[BehaviorCoordinator] {_companion?.companionName} cannot start behavior - not available (state: {_currentState})");
                }
                return false;
            }
            
            if (_idleBehavior == null) return false;
            
            return _idleBehavior.TryStartSubBehavior<T>();
        }
        
        /// <summary>
        /// Cancels the current sub-behavior.
        /// </summary>
        public void CancelCurrentBehavior(string reason = "Cancelled")
        {
            var behavior = _idleBehavior?.ActiveSubBehavior;
            if (behavior != null)
            {
                _idleBehavior.CancelAllIdleBehaviors();
                
                OnBehaviorEnded?.Invoke(behavior, false, reason);
                if (_companion != null)
                {
                    CompanionEvents.FireBehaviorCancelled(_companion, behavior, reason);
                }
            }
        }
        
        /// <summary>
        /// Called by sub-behaviors when they start.
        /// </summary>
        public void NotifyBehaviorStarted(IdleSubBehavior behavior)
        {
            if (behavior == null) return;
            
            OnBehaviorStarted?.Invoke(behavior);
            if (_companion != null)
            {
                CompanionEvents.FireBehaviorStarted(_companion, behavior);
            }
            
            // Start work session for all behaviors (WorkBehaviorBase is generic so we check by name/duration)
            _workSessionManager?.StartSession(behavior, behavior.MaxDuration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[BehaviorCoordinator] {_companion?.companionName} started behavior: {behavior.BehaviorName}");
            }
        }
        
        /// <summary>
        /// Called by sub-behaviors when they complete.
        /// </summary>
        public void NotifyBehaviorCompleted(IdleSubBehavior behavior, bool success, string message = null)
        {
            if (behavior == null) return;
            
            OnBehaviorEnded?.Invoke(behavior, success, message ?? (success ? "Completed" : "Failed"));
            if (_companion != null)
            {
                CompanionEvents.FireBehaviorCompleted(_companion, behavior, success);
            }
            
            // End work session
            _workSessionManager?.EndSession(success, message);
            
            if (VerboseLogging)
            {
                Debug.Log($"[BehaviorCoordinator] {_companion?.companionName} completed behavior: {behavior.BehaviorName} (success: {success})");
            }
        }
        
        /// <summary>
        /// Forces all behaviors to stop immediately.
        /// </summary>
        public void ForceStopAll(string reason = "Forced stop")
        {
            CancelCurrentBehavior(reason);
            
            // Note: CancelAllIdleBehaviors is called in ForceStopAll context
            // Use the public method from CompanionIdleBehavior
            _idleBehavior?.CancelAllIdleBehaviors();
            
            if (_combatMovement != null)
            {
                _combatMovement.ClearCommandPriority();
                _combatMovement.UnlockMovement();
            }
            
            TransitionTo(BehaviorState.Idle, reason);
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// Gets a description of the current state for display.
        /// </summary>
        public string GetStateDescription()
        {
            switch (_currentState)
            {
                case BehaviorState.Working:
                    return _idleBehavior?.ActiveSubBehavior?.GetStatusDescription() ?? "Working";
                case BehaviorState.Combat:
                    return _stateReason ?? "In combat";
                case BehaviorState.Relaxing:
                    return _idleBehavior?.GetIdleStateDescription() ?? "Relaxing";
                default:
                    return _stateReason ?? _currentState.ToString();
            }
        }
        
        /// <summary>
        /// Checks if a specific behavior type is currently active.
        /// </summary>
        public bool IsBehaviorActive<T>() where T : IdleSubBehavior
        {
            return _idleBehavior?.ActiveSubBehavior is T;
        }
        
        /// <summary>
        /// Gets the current active behavior as a specific type.
        /// </summary>
        public T GetActiveBehavior<T>() where T : IdleSubBehavior
        {
            return _idleBehavior?.ActiveSubBehavior as T;
        }
        
        #endregion
    }
}
