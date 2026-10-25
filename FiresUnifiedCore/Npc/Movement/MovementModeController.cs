using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Stop, walk, jog and run movement modes with velocity clamping, split out of CompanionCombatMovement. Walk
    /// and run changes are only sent when the mode actually changes, and movement is held until the animator
    /// reaches locomotion so companions don't slide.
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
        private const float MoveDirChangeThreshold = 0.05f;

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

            if (!_moveDirSet || Vector3.Distance(moveDir, _lastSetMoveDir) > MoveDirChangeThreshold)
     {
              _character.SetMoveDir(moveDir);
_lastSetMoveDir = moveDir;
    _moveDirSet = true;
        }
  }

        /// <summary>
        /// No longer writes the rigidbody's velocity. Kept as a no-op for binary
        /// compatibility; callers use <see cref="WantsSlowWalk"/>.
        /// </summary>
        [System.Obsolete("Use WantsSlowWalk: speed comes from SetMoveDir / SetWalk, never a velocity write.")]
        public void ApplyVelocityClamping(MovementMode mode, float targetDistance, bool isStrafing) { }

        /// <summary>
        /// True when the body should walk instead of run: close to the target in Walk mode, or strafing. The vanilla way to slow
        /// down is Character.SetWalk (UpdateWalking's walk speed). Stopping is the movement authority's SetMoveDir(0), which
        /// UpdateWalking brakes. The old version clamped and killed the rigidbody's velocity here.
        /// </summary>
        public bool WantsSlowWalk(MovementMode mode, float targetDistance, bool isStrafing)
        {
            if (_character == null || mode == MovementMode.Stop) return false;
            if (mode == MovementMode.Walk && targetDistance < SlowdownDistance && targetDistance > 0) return true;
            return isStrafing;
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
