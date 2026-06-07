using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// State machine that manages the high-level movement states for companions.
    /// Replaces the complex branching logic in CompanionCombatMovement.Update().
    /// </summary>
    public class MovementStateMachine
    {
        public enum State
        {
            Disabled,       // Movement processing disabled (null checks failed)
            Skipped,        // State controller says skip combat movement
            Fleeing,        // CompanionAI is fleeing
            CommandPriority,// Player command has absolute priority
            EmoteFrozen,    // Playing an emote
            MovementLocked, // Movement is locked by a behavior
            Combat,         // In active combat
            Transitioning,  // Transitioning between states
            CombatCooldown, // Just exited combat, cooling down
            Idle,           // Idle behavior active
            Following       // Following owner
        }
        
        private State _currentState = State.Disabled;
        private State _previousState = State.Disabled;
        private float _stateEnterTime;
        
        public static bool VerboseLogging = false;
        
        // Properties
        public State CurrentState => _currentState;
        public State PreviousState => _previousState;
        public float TimeInCurrentState => Time.time - _stateEnterTime;
        
        // Callbacks for state changes
        public System.Action<State, State> OnStateChanged;
        
        /// <summary>
        /// Evaluates the current situation and returns the appropriate state.
        /// This replaces the complex if-else chain in Update().
        /// </summary>
        public State EvaluateState(MovementStateContext ctx)
        {
            // Priority 1: Null checks - Disabled state
            if (ctx.Companion == null || ctx.CompanionAI == null)
                return State.Disabled;
            
            // Priority 2: State controller says skip
            if (ctx.StateController != null && ctx.StateController.ShouldSkipCombatMovement)
                return State.Skipped;
            
            // Priority 3: Fleeing
            if (ctx.CompanionAI.CurrentState == CompanionAI.AIState.Fleeing)
                return State.Fleeing;
            
            // Priority 4: Absolute priority command
            if (ctx.StateController != null && ctx.StateController.HasAbsolutePriorityCommand)
                return State.CommandPriority;
            
            // Priority 5: Emote frozen
            if (ctx.IdleBehavior != null && ctx.IdleBehavior.IsEmoteFrozen)
                return State.EmoteFrozen;
            
            // Priority 6: Movement locked
            if (ctx.IsMovementLocked)
                return State.MovementLocked;
            
            // Priority 7: Animation locked - still process but limited
            if (ctx.IsAnimationLocked)
                return _currentState; // Stay in current state during animation lock
            
            // Priority 8: Combat states
            if (ctx.IsInCombat)
                return State.Combat;
            
            // Priority 9: Transitioning
            if (ctx.IsInTransition)
                return State.Transitioning;
            
            // Priority 10: Combat cooldown
            if (ctx.IsInCombatCooldown)
                return State.CombatCooldown;
            
            // Priority 11: Idle or Following
            if (ctx.CurrentIntent == CompanionCombatMovement.MovementIntent.Idle)
                return State.Idle;
            
            return State.Following;
        }
        
        /// <summary>
        /// Transitions to a new state if different from current.
        /// </summary>
        public bool TransitionTo(State newState, string companionName = null)
        {
            if (newState == _currentState)
                return false;
            
            _previousState = _currentState;
            _currentState = newState;
            _stateEnterTime = Time.time;
            
            if (VerboseLogging && companionName != null)
            {
                Debug.Log($"[MovementStateMachine] {companionName}: {_previousState} -> {_currentState}");
            }
            
            OnStateChanged?.Invoke(_previousState, _currentState);
            return true;
        }
        
        /// <summary>
        /// Returns true if state change would be a significant transition.
        /// </summary>
        public bool IsSignificantTransition(State newState)
        {
            // Combat entry/exit is significant
            if (_currentState == State.Combat && newState != State.Combat)
                return true;
            if (_currentState != State.Combat && newState == State.Combat)
                return true;
            
            // Fleeing is significant
            if (newState == State.Fleeing || _currentState == State.Fleeing)
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Returns true if currently in a state where movement should be processed.
        /// </summary>
        public bool ShouldProcessMovement()
        {
            return _currentState switch
            {
                State.Disabled => false,
                State.Skipped => false,
                State.EmoteFrozen => false,
                State.MovementLocked => false,
                _ => true
            };
        }
        
        /// <summary>
        /// Returns true if currently in a combat-related state.
        /// </summary>
        public bool IsInCombatState()
        {
            return _currentState == State.Combat || 
                   _currentState == State.Transitioning ||
                   _currentState == State.CombatCooldown;
        }
        
        /// <summary>
        /// Resets the state machine to initial state.
        /// </summary>
        public void Reset()
        {
            _previousState = _currentState;
            _currentState = State.Disabled;
            _stateEnterTime = Time.time;
        }
    }
    
    /// <summary>
    /// Context data passed to the state machine for evaluation.
    /// Avoids passing many parameters to EvaluateState().
    /// </summary>
    public class MovementStateContext
    {
        public CompanionController Companion;
        public CompanionAI CompanionAI;
        public CompanionStateController StateController;
        public CompanionIdleBehavior IdleBehavior;
        
        public bool IsMovementLocked;
        public bool IsAnimationLocked;
        public bool IsInCombat;
        public bool IsInTransition;
        public bool IsInCombatCooldown;
        public CompanionCombatMovement.MovementIntent CurrentIntent;
        
        /// <summary>
        /// Creates a context from the current state of CompanionCombatMovement.
        /// </summary>
        public static MovementStateContext Create(
            CompanionController companion,
            CompanionAI companionAI,
            CompanionStateController stateController,
            CompanionIdleBehavior idleBehavior,
            bool isMovementLocked,
            bool isAnimationLocked,
            bool isInCombat,
            bool isInTransition,
            bool isInCombatCooldown,
            CompanionCombatMovement.MovementIntent currentIntent)
        {
            return new MovementStateContext
            {
                Companion = companion,
                CompanionAI = companionAI,
                StateController = stateController,
                IdleBehavior = idleBehavior,
                IsMovementLocked = isMovementLocked,
                IsAnimationLocked = isAnimationLocked,
                IsInCombat = isInCombat,
                IsInTransition = isInTransition,
                IsInCombatCooldown = isInCombatCooldown,
                CurrentIntent = currentIntent
            };
        }
    }
}
