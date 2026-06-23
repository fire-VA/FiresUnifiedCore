using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
 /// Handles movement speed modes (Walk/Jog/Run) and velocity clamping.
    /// Extracted from CompanionCombatMovement for maintainability.
    /// 
    /// MOVEMENT MODES:
  /// - Stop: No movement, velocity clamped to near zero
    /// - Walk: Slow, careful movement (blocking, strafing, precision)
    /// - Jog: Normal movement speed
    /// - Run: Fast movement (combat approach, retreat, following)
    /// 
    /// ANTI-SPAM:
    /// Only sends SetWalk/SetRun commands when the mode actually changes.
    /// This prevents overwhelming Character with redundant calls.
    /// 
    /// ANIMATION SYNC:
    /// Movement is synchronized with animation state to prevent the
    /// "sliding while standing" visual glitch. Movement commands are
    /// blocked if the animator hasn't transitioned to locomotion yet.
    /// </summary>
    public class MovementModeController
    {
   public enum MovementMode
        {
            Stop,
         Walk,
    Jog,
 Run
        }

        private readonly Character _character;
        private readonly Rigidbody _rigidbody;
        private readonly CompanionStateController _stateController;
        
   // Tracking to avoid spam
        private bool _lastWalkState = false;
        private bool _lastRunState = false;
        private bool _movementModeSet = false;
        
        private Vector3 _lastSetMoveDir = Vector3.zero;
        private bool _moveDirSet = false;
        private const float MOVE_DIR_CHANGE_THRESHOLD = 0.05f;

     // Settings
        public float SlowWalkSpeedMultiplier { get; set; } = 0.5f;
  public float SlowdownDistance { get; set; } = 3f;
      public float MovementBlendSpeed { get; set; } = 5f;

        public MovementModeController(Character character, Rigidbody rigidbody)
        {
  _character = character;
 _rigidbody = rigidbody;
            _stateController = character?.GetComponent<CompanionStateController>();
        }

/// <summary>
        /// Applies the specified movement mode to the character.
        /// Only sends commands if the mode has changed.
        /// </summary>
  public void ApplyMode(MovementMode mode)
     {
         if (_character == null) return;

            bool newWalk = false;
       bool newRun = false;

            switch (mode)
     {
          case MovementMode.Stop:
                    newWalk = false;
        newRun = false;
break;
  case MovementMode.Walk:
    newWalk = true;
        newRun = false;
        break;
      case MovementMode.Jog:
              newWalk = false;
        newRun = false;
             break;
    case MovementMode.Run:
      newWalk = false;
 newRun = true;
  break;
            }

    SetWalkRunSafe(newWalk, newRun);
    }

       /// <summary>
     /// Sets walk/run state only if it changed.
     /// Skips if rigidbody is kinematic to avoid Unity 6 warnings.
      /// </summary>
        public void SetWalkRunSafe(bool walk, bool run)
   {
  if (_character == null) return;
  
  // Skip if rigidbody is kinematic - Character internally sets velocity which causes Unity 6 warnings
  if (_rigidbody != null && _rigidbody.isKinematic) return;

       if (!_movementModeSet || walk != _lastWalkState || run != _lastRunState)
          {
       _character.SetWalk(walk);
            _character.SetRun(run);
           _lastWalkState = walk;
          _lastRunState = run;
   _movementModeSet = true;
       }
        }

        /// <summary>
        /// Sets move direction only if it significantly changed.
        /// Skips if rigidbody is kinematic to avoid Unity 6 warnings.
        /// 
        /// ============================================================
        /// ANIMATION SYNC: Movement is blocked if the animator hasn't
        /// transitioned to locomotion yet. This prevents the "sliding
        /// while standing" visual glitch.
        /// ============================================================
        /// </summary>
        public void SetMoveDirSafe(Vector3 moveDir)
        {
       if (_character == null) return;
       
       // Skip if rigidbody is kinematic - Character internally sets velocity which causes Unity 6 warnings
       if (_rigidbody != null && _rigidbody.isKinematic) return;
       
       // ANIMATION SYNC CHECK: Block movement if animator isn't in locomotion
       // This prevents the "sliding while standing" visual glitch
       if (_stateController != null && _stateController.IsInStandingSlideState)
       {
           // We're in a slide state - don't apply more movement
           return;
       }

            if (!_moveDirSet || Vector3.Distance(moveDir, _lastSetMoveDir) > MOVE_DIR_CHANGE_THRESHOLD)
     {
              _character.SetMoveDir(moveDir);
_lastSetMoveDir = moveDir;
    _moveDirSet = true;
        }
  }

        /// <summary>
        /// Clamps velocity based on movement mode and target distance.
   /// </summary>
        public void ApplyVelocityClamping(MovementMode mode, float targetDistance, bool isStrafing)
        {
            if (_rigidbody == null || _character == null) return;
            
            // CRITICAL: Skip if rigidbody is kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
            if (_rigidbody.isKinematic) return;
            
            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVel = new Vector3(velocity.x, 0, velocity.z);
            float currentSpeed = horizontalVel.magnitude;
            
            bool shouldSlowDown = false;
            float maxAllowedSpeed = float.MaxValue;
            
            if (mode == MovementMode.Stop)
            {
                shouldSlowDown = true;
                maxAllowedSpeed = 0.05f; // Much lower threshold for stopping
                
                // AGGRESSIVE STOP: When mode is Stop, immediately kill horizontal velocity
                // This prevents jittering when companions should be standing still
                if (currentSpeed > 0.1f)
                {
                    _rigidbody.linearVelocity = new Vector3(0, velocity.y, 0);
                }
            }
            else if (mode == MovementMode.Walk && targetDistance < SlowdownDistance && targetDistance > 0)
            {
                shouldSlowDown = true;
                float slowdownFactor = Mathf.Clamp01(targetDistance / SlowdownDistance);
                float walkSpeed = _character.m_walkSpeed;
                maxAllowedSpeed = walkSpeed * slowdownFactor * SlowWalkSpeedMultiplier;
                maxAllowedSpeed = Mathf.Max(maxAllowedSpeed, 0.5f);
            }
            else if (isStrafing)
            {
                shouldSlowDown = true;
                maxAllowedSpeed = _character.m_walkSpeed * 0.7f;
            }
            
            // Apply velocity clamping for non-stop modes
            if (shouldSlowDown && currentSpeed > maxAllowedSpeed && mode != MovementMode.Stop)
            {
                Vector3 clampedHorizontal = horizontalVel.normalized * maxAllowedSpeed;
                _rigidbody.linearVelocity = new Vector3(clampedHorizontal.x, velocity.y, clampedHorizontal.z);
            }
        }

        /// <summary>
 /// Resets the movement mode tracking state.
        /// Call this when entering/exiting combat to force re-evaluation.
    /// </summary>
        public void ResetTracking()
        {
        _movementModeSet = false;
         _moveDirSet = false;
        }

        /// <summary>
        /// Forces a movement direction reset - clears cached state.
   /// </summary>
 public void ForceMoveDirUpdate()
        {
      _moveDirSet = false;
        }

        /// <summary>
        /// Gets the current cached move direction.
/// </summary>
        public Vector3 LastMoveDir => _lastSetMoveDir;

 /// <summary>
        /// Whether the move direction has been set this frame.
        /// </summary>
  public bool MoveDirSet => _moveDirSet;
 }
}
