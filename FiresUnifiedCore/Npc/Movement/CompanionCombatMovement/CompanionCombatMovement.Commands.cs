using UnityEngine;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Core;

namespace FiresCore.Npc
{
    /// <summary>
    /// Command handling, priority targets, movement lock, and flee movement system.
    /// Uses UnifiedMovementAuthority for all movement operations.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Movement Lock System

        /// <summary>
        /// Locks all movement for this companion. Delegates to CompanionStateController.
        /// Uses UnifiedMovementAuthority to freeze movement properly.
        /// </summary>
        public void LockMovement(string reason, float duration = 999f)
        {
            if (_stateController != null)
            {
                _stateController.LockMovement(reason, duration);
            }
            else
            {
                _localMovementLocked = true;
                _localMovementLockReason = reason;
                _localMovementLockEndTime = Time.time + duration;
            }
            
            // Use UnifiedMovementAuthority to freeze movement
            if (_movementAuthority != null)
            {
                _movementAuthority.FreezeMovement(reason, duration);
            }
            // No fallback - the authority system is required
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = new Vector3(0, _rigidbody.linearVelocity.y, 0);
            }
            
            _targetMoveDirection = Vector3.zero;
            _currentMoveDirection = Vector3.zero;
            _committedMoveDirection = Vector3.zero;
            _lastSetMoveDir = Vector3.zero;
            _moveDirSet = false;
            _movementModeSet = false;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} movement LOCKED via StateController: {reason} for {duration}s");
        }
        
        /// <summary>
        /// Unlocks movement, allowing normal following/combat behavior to resume.
        /// </summary>
        public void UnlockMovement()
        {
            if (_stateController != null)
            {
                _stateController.UnlockMovement();
            }
            else if (_localMovementLocked)
            {
                _localMovementLocked = false;
                _localMovementLockReason = "";
                _localMovementLockEndTime = 0f;
            }
            
            // Unfreeze via UnifiedMovementAuthority
            _movementAuthority?.UnfreezeMovement();
            
            _moveDirSet = false;
            _movementModeSet = false;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} movement UNLOCKED via StateController");
        }
        
        /// <summary>
        /// Returns true if movement is currently locked.
        /// </summary>
        public bool IsMovementLocked
        {
            get
            {
                if (_stateController != null)
                {
                    return _stateController.IsMovementLocked(out _);
                }
                return _localMovementLocked && Time.time < _localMovementLockEndTime;
            }
        }
        
        /// <summary>
        /// Gets the reason for current movement lock.
        /// </summary>
        public string MovementLockReason
        {
            get
            {
                if (_stateController != null && _stateController.IsMovementLocked(out string reason))
                {
                    return reason;
                }
                return _localMovementLockReason;
            }
        }

        #endregion
        
        #region Flee Movement System
        
        /// <summary>
        /// Called by CompanionAI to request flee movement toward a target position.
        /// </summary>
        public void RequestFleeMovement(Vector3 targetPosition)
        {
            _fleeHandler?.RequestFleeMovement(targetPosition);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} flee movement requested to {targetPosition}");
        }
        
        /// <summary>
        /// Clears the flee movement request.
        /// </summary>
        public void ClearFleeRequest()
        {
            _fleeHandler?.ClearFleeRequest();
        }
        
        #endregion

        #region Priority Target System

        /// <summary>
        /// Sets a priority target that the companion will focus on attacking.
        /// </summary>
        public void SetPriorityTarget(Character target, float duration)
        {
            if (target == null || target.IsDead()) return;
            
            _commandHandler?.SetPriorityTarget(target, duration);
            
            _committedTarget = target;
            _currentTarget = target;
            _hasActiveCommitment = false;
            _isInCombat = true;
            
            if (_companionAI != null)
            {
                _companionAI.ForceTarget(target);
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} priority target set: {target.m_name} for {duration}s");
            }
        }

        /// <summary>
        /// Clears the priority target, returning to normal target selection.
        /// </summary>
        public void ClearPriorityTarget()
        {
            _commandHandler?.ClearPriorityTarget();
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} priority target cleared");
            }
        }

        /// <summary>
        /// Returns true if a priority target is active and not expired.
        /// </summary>
        public bool HasPriorityTarget => _commandHandler?.HasPriorityTarget ?? false;

        /// <summary>
        /// Gets the current priority target (may be null or expired).
        /// </summary>
        public Character PriorityTarget => _commandHandler?.PriorityTarget;

        #endregion

        #region Command Movement

        /// <summary>
        /// Returns true if companion has an active player command that should override normal behavior.
        /// </summary>
        public bool HasCommandPriority => _commandHandler?.HasCommandPriority ?? false;

        /// <summary>
        /// Clears command priority, allowing normal AI behavior to resume.
        /// </summary>
        public void ClearCommandPriority()
        {
            _commandHandler?.ClearCommandPriority();
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} command priority cleared");
        }

        /// <summary>
        /// Sets command priority for a specified duration.
        /// </summary>
        public void SetCommandPriorityDuration(float duration)
        {
            _commandHandler?.SetCommandPriorityDuration(duration);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} command priority set for {duration:F1}s");
        }

        /// <summary>
        /// Sets a movement destination for the companion to move to.
        /// </summary>
        public void SetMoveDestination(Vector3 destination)
        {
            SetMoveDestination(destination, useWalk: false, skipCommandOverride: false);
        }
        
        /// <summary>
        /// Sets a destination for the companion to move to.
        /// </summary>
        public void SetMoveDestination(Vector3 destination, bool useWalk)
        {
            SetMoveDestination(destination, useWalk, skipCommandOverride: false);
        }
        
        /// <summary>
        /// Sets a destination for the companion to move to.
        /// skipCommandOverride prevents sub-behaviors from cancelling parent commands.
        /// </summary>
        public void SetMoveDestination(Vector3 destination, bool useWalk, bool skipCommandOverride)
        {
            if (_stateController != null && !skipCommandOverride)
            {
                _stateController.StartCommand(
                    CompanionStateController.CommandType.Move,
                    destination,
                    targetObject: null,
                    targetCharacter: null,
                    timeout: 120f,
                    onComplete: () => {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} reached move destination");
                    },
                    onFailed: (reason) => {
                        Debug.LogWarning($"[CompanionCombatMovement] {_companion?.companionName} failed to reach destination: {reason}");
                    }
                );
            }
            
            _commandHandler?.SetMoveDestination(destination, useWalk);
            
            if (!skipCommandOverride)
            {
                UnlockMovement();
                
                if (_isInCombat)
                {
                    _isInCombat = false;
                    _currentTarget = null;
                    _committedTarget = null;
                    _hasActiveCommitment = false;
                }
                
                _hasRangedMovementRequest = false;
            }
            
            _moveDirSet = false;
            _movementModeSet = false;
            
            if (VerboseLogging)
            {
                string priority = skipCommandOverride ? "sub-behavior" : "ABSOLUTE priority";
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} move destination set ({priority}): {destination} (walk: {useWalk})");
            }
        }

        /// <summary>
        /// Clears the command movement destination.
        /// Routes through UnifiedMovementAuthority.
        /// </summary>
        public void ClearMoveDestination()
        {
            _commandHandler?.ClearMoveDestination();
            
            // Clear movement through authority
            if (_movementAuthority != null)
            {
                _movementAuthority.ClearDestination(AUTHORITY_OWNER);
                _movementAuthority.ReleaseAuthority(AUTHORITY_OWNER);
            }
            // No direct SetMoveDir fallback - authority system is required
            
            if (_stateController != null && _stateController.HasActiveCommand && 
                _stateController.ActiveCommandType == CompanionStateController.CommandType.Move)
            {
                _stateController.CompleteCommand();
            }
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} move destination cleared");
        }

        /// <summary>
        /// Returns true if companion has a command movement destination.
        /// </summary>
        public bool HasMoveDestination => _commandHandler?.HasMoveDestination ?? false;
        
        /// <summary>
        /// Returns true if the current command should use walking speed.
        /// </summary>
        public bool ShouldWalkForCommand => _commandHandler?.ShouldWalkForCommand ?? false;

        /// <summary>
        /// Gets the current command movement destination.
        /// </summary>
        public Vector3 MoveDestination => _commandHandler?.MoveDestination ?? Vector3.zero;

        #endregion
    }
}
