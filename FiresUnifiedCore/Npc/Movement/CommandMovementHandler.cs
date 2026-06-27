using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles player command movement for companions.
    /// 
    /// RESPONSIBILITIES:
    /// - Move-to-position commands
    /// - Attack target commands  
    /// - Priority target tracking
    /// - Command priority system
    /// 
    /// DESIGN:
    /// This is a helper class, not a MonoBehaviour. It's instantiated and owned
    /// by CompanionCombatMovement which calls its methods as needed.
    /// 
    /// COMMAND PRIORITY:
    /// Player commands have ABSOLUTE priority over AI decisions.
    /// When a player tells their companion to do something, it MUST happen.
    /// Move and Attack commands interrupt EVERYTHING including combat.
    /// </summary>
    public class CommandMovementHandler
    {
        #region Dependencies
        
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly CompanionController _companion;
        private readonly CompanionAI _companionAI;
        private readonly CompanionIdleBehavior _idleBehavior;
        
        #endregion
        
        #region State - Command Destination
        
        private Vector3 _commandDestination;
        private bool _hasCommandDestination;
        private bool _useWalkForCommand;
        
        #endregion
        
        #region State - Command Priority
        
        private bool _hasCommandPriority;
        private float _commandPriorityEndTime;
        
        #endregion
        
        #region State - Priority Target
        
        private Character _priorityTarget;
        private float _priorityTargetEndTime;
        
        #endregion
        
        public static bool VerboseLogging = false;
        
        #region Properties
        
        /// <summary>Returns true if companion has an active command movement destination.</summary>
        public bool HasMoveDestination => _hasCommandDestination;
        
        /// <summary>Gets the current command movement destination.</summary>
        public Vector3 MoveDestination => _commandDestination;
        
        /// <summary>Returns true if the current command should use walking speed.</summary>
        public bool ShouldWalkForCommand => _useWalkForCommand;
        
        /// <summary>Returns true if a priority target is active and not expired.</summary>
        public bool HasPriorityTarget => _priorityTarget != null && 
            !_priorityTarget.IsDead() && 
            Time.time < _priorityTargetEndTime;
        
        /// <summary>Gets the current priority target (may be null or expired).</summary>
        public Character PriorityTarget => HasPriorityTarget ? _priorityTarget : null;
        
        /// <summary>Returns true if companion has an active player command that should override normal behavior.</summary>
        public bool HasCommandPriority 
        { 
            get 
            {
                bool hasPriority = _hasCommandPriority && Time.time < _commandPriorityEndTime;
                
                // Auto-clear when priority expires
                if (_hasCommandPriority && !hasPriority)
                {
                    _hasCommandPriority = false;
                    if (VerboseLogging)
                        Debug.Log($"[CommandMovementHandler] Command priority expired");
                }
                
                return hasPriority;
            }
        }
        
        #endregion
        
        #region Constructor
        
        public CommandMovementHandler(
            Transform transform,
            Character character,
            CompanionController companion,
            CompanionAI companionAI,
            CompanionIdleBehavior idleBehavior)
        {
            _transform = transform;
            _character = character;
            _companion = companion;
            _companionAI = companionAI;
            _idleBehavior = idleBehavior;
        }
        
        #endregion
        
        #region Move Destination
        
        /// <summary>
        /// Sets a movement destination for the companion to move to.
        /// IMPORTANT: This preserves the follow state - companion will resume following after reaching destination.
        /// </summary>
        public void SetMoveDestination(Vector3 destination, bool useWalk = false)
        {
            _commandDestination = destination;
            _hasCommandDestination = true;
            _hasCommandPriority = true;
            _commandPriorityEndTime = Time.time + 60f; // 60 second timeout for move commands
            _useWalkForCommand = useWalk;
            
            // Clear any priority target - move takes priority now
            _priorityTarget = null;
            _priorityTargetEndTime = 0f;
            
            // Tell CompanionAI about the command destination WITHOUT changing follow state
            if (_companionAI != null)
            {
                _companionAI.SetCommandDestination(destination);
            }
            
            if (VerboseLogging)
                Debug.Log($"[CommandMovementHandler] Move destination set: {destination} (walk: {useWalk})");
        }
        
        /// <summary>
        /// Clears the command movement destination.
        /// </summary>
        public void ClearMoveDestination()
        {
            _hasCommandDestination = false;
            _commandDestination = Vector3.zero;
            _useWalkForCommand = false;
            
            // Stop the move animation WITHOUT releasing authority — the command is still in control and
            // must keep its movement slot through the move->work handoff. (Releasing here let Following
            // re-acquire and the companion oscillated between the player and the command target, never
            // settling to work. When the SetMoveDir gate ships, route this through Hold(owner) instead.)
            if (_character != null)
            {
                _character.SetWalk(false);
                _character.SetRun(false);
                _character.SetMoveDir(Vector3.zero);
            }
            
            // If we were in command priority mode just for movement, clear it
            if (!HasPriorityTarget)
            {
                _hasCommandPriority = false;
            }
            
            // Clear command destination in CompanionAI
            if (_companionAI != null)
            {
                _companionAI.ClearCommandDestination();
            }
            
            if (VerboseLogging)
                Debug.Log($"[CommandMovementHandler] Move destination cleared");
        }
        
        /// <summary>
        /// Returns the distance to the command destination.
        /// </summary>
        public float GetDistanceToDestination()
        {
            if (!_hasCommandDestination) return float.MaxValue;
            return Vector3.Distance(_transform.position, _commandDestination);
        }
        
        /// <summary>
        /// Returns true if we've reached the command destination.
        /// </summary>
        public bool HasReachedDestination(float threshold = 2f)
        {
            return GetDistanceToDestination() <= threshold;
        }
        
        #endregion
        
        #region Priority Target
        
        /// <summary>
        /// Sets a priority target that the companion will focus on attacking.
        /// This overrides normal target selection for the specified duration.
        /// </summary>
        public void SetPriorityTarget(Character target, float duration)
        {
            if (target == null || target.IsDead()) return;
            
            _priorityTarget = target;
            _priorityTargetEndTime = Time.time + duration;
            _hasCommandPriority = true;
            _commandPriorityEndTime = Time.time + duration;
            
            // Clear any move destination - attack takes priority
            _hasCommandDestination = false;
            
            // Notify idle behavior that combat is starting
            _idleBehavior?.OnCombatStarted();
            
            // Force CompanionAI to pursue
            if (_companionAI != null)
            {
                _companionAI.ForceTarget(target);
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CommandMovementHandler] Priority target set: {target.m_name} for {duration}s");
            }
        }
        
        /// <summary>
        /// Clears the priority target, returning to normal target selection.
        /// </summary>
        public void ClearPriorityTarget()
        {
            _priorityTarget = null;
            _priorityTargetEndTime = 0f;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CommandMovementHandler] Priority target cleared");
            }
        }
        
        #endregion
        
        #region Command Priority
        
        /// <summary>
        /// Sets command priority for a specified duration.
        /// Used by sub-behaviors like bow training to prevent AI interruption.
        /// </summary>
        public void SetCommandPriorityDuration(float duration)
        {
            _hasCommandPriority = true;
            _commandPriorityEndTime = Time.time + duration;
            
            if (VerboseLogging)
                Debug.Log($"[CommandMovementHandler] Command priority set for {duration:F1}s");
        }
        
        /// <summary>
        /// Clears command priority, allowing normal AI behavior to resume.
        /// </summary>
        public void ClearCommandPriority()
        {
            if (VerboseLogging)
                Debug.Log($"[CommandMovementHandler] Command priority cleared");
            
            _hasCommandPriority = false;
            _commandPriorityEndTime = 0f;
            ClearPriorityTarget();
            ClearMoveDestination();
        }
        
        #endregion
    }
}
