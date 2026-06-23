using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles non-combat following behavior, stuck detection, and jumping.
    /// Extracted from CompanionCombatMovement for maintainability.
    /// 
    /// FOLLOWING PHILOSOPHY:
    /// - Use distance thresholds to determine speed (walk, jog, run, sprint)
    /// - Let BaseAI (via CompanionAI) handle pathfinding - we just set the speed
    /// - Only check for stuck when actively trying to follow
    /// - Jump when stuck or when owner is above us
    /// 
    /// STUCK DETECTION:
    /// Conservative approach - only trigger after extended failure.
    /// False positives cause annoying random jumps.
    /// </summary>
    public class FollowBehavior
  {
        // Distance thresholds - each has a buffer zone to prevent oscillation
        // Companion enters a speed mode at the outer threshold, exits at inner threshold
        // TIGHTENED: Companions now stick closer to the player for better protection
        public float StopDistanceInner { get; set; } = 2f;      // Start stopping when closer than this
        public float StopDistanceOuter { get; set; } = 3.5f;    // Stop moving when closer than this (was 4)
        public float WalkDistanceInner { get; set; } = 3.5f;    // Transition from walk to stop (was 4)
        public float WalkDistanceOuter { get; set; } = 5f;      // Start walking when further than this (was 7)
        public float JogDistanceInner { get; set; } = 6f;       // Transition from jog to walk (was 8)
        public float JogDistanceOuter { get; set; } = 10f;      // Start jogging when further than this (was 12)
        public float RunDistanceInner { get; set; } = 12f;      // Transition from run to jog (was 15)
        public float RunDistanceOuter { get; set; } = 18f;      // Start running when further than this (was 25)
        public float SprintDistance { get; set; } = 30f;        // Sprint threshold (emergency catch-up) (was 40)
        
        // Catch-up speed boost - when companion falls behind, they move faster to catch up
        public float CatchUpSpeedMultiplier { get; set; } = 1.25f;  // Speed boost when catching up
        public float CatchUpThreshold { get; set; } = 12f;          // Start catch-up boost beyond this distance
        
        // Current movement state for hysteresis
        private MovementModeController.MovementMode _currentMode = MovementModeController.MovementMode.Stop;

        // Stuck detection settings
        public float StuckDetectionTime { get; set; } = 8.0f;
        public float StuckMovementThreshold { get; set; } = 2.0f;
        public int StuckChecksBeforeJump { get; set; } = 3;
        
        // Jump settings
        public float JumpCooldown { get; set; } = 5.0f;
        public float JumpForce { get; set; } = 8f;
        public float JumpForwardBoost { get; set; } = 3f;
        public float MaxJumpableHeight { get; set; } = 1.5f;
        public float ObstacleCheckDistance { get; set; } = 1.5f;
        public float MinOwnerHeightDiffForJump { get; set; } = 1.0f;
        public float JumpAnimationDuration { get; set; } = 0.8f;

        // State
        private float _lastJumpTime = -10f;
        private Vector3 _stuckCheckStartPos;
        private float _stuckCheckStartTime;
        private int _consecutiveStuckChecks;
        private bool _shouldCheckStuck = false;
        private float _lastStuckEvaluation;
        private const float STUCK_EVAL_INTERVAL = 2.0f;

        // Grounded state
        private bool _isGrounded;
        private float _lastGroundedCheck;
        private const float GROUNDED_CHECK_INTERVAL = 0.2f;

        // References
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly Rigidbody _rigidbody;
        private readonly ZSyncAnimation _zanim;
        private readonly Animator _animator;

        public static bool VerboseLogging = false;

        public bool IsGrounded => _isGrounded;
        public bool ShouldCheckStuck => _shouldCheckStuck;

        public FollowBehavior(Transform transform, Character character, Rigidbody rigidbody, 
     ZSyncAnimation zanim, Animator animator)
        {
       _transform = transform;
        _character = character;
        _rigidbody = rigidbody;
            _zanim = zanim;
       _animator = animator;
            
_stuckCheckStartPos = transform.position;
 _stuckCheckStartTime = Time.time;
        }

        /// <summary>
        /// Determines the appropriate movement mode based on distance to owner.
        /// Uses hysteresis (buffer zones) to prevent oscillation between modes.
        /// Each mode has an "enter" threshold (outer) and "exit" threshold (inner).
        /// Returns mode, whether to check for stuck, and a speed multiplier for catch-up.
        /// </summary>
        public (MovementModeController.MovementMode mode, bool checkStuck, float speedMultiplier) EvaluateFollowState(
            float distanceToOwner, bool ownerMoving, bool canSprint)
        {
            // Determine new mode using hysteresis
            // We use different thresholds for entering vs exiting each mode
            MovementModeController.MovementMode newMode = _currentMode;
            bool checkStuck = false;
            float speedMultiplier = 1.0f;  // Base speed, no boost
            
            // Calculate catch-up speed boost when falling behind
            // The further behind, the faster we move (up to max multiplier)
            if (distanceToOwner > CatchUpThreshold)
            {
                // Scale multiplier from 1.0 at CatchUpThreshold to CatchUpSpeedMultiplier at SprintDistance
                float t = Mathf.InverseLerp(CatchUpThreshold, SprintDistance, distanceToOwner);
                speedMultiplier = Mathf.Lerp(1.0f, CatchUpSpeedMultiplier, t);
            }
            
            // SPRINT: Emergency catch-up (no hysteresis needed - one-way threshold)
            if (distanceToOwner > SprintDistance)
            {
                newMode = MovementModeController.MovementMode.Run; // Sprint uses Run mode
                checkStuck = true;
                speedMultiplier = CatchUpSpeedMultiplier;  // Max catch-up speed
            }
            // RUN: Far from owner
            else if (distanceToOwner > RunDistanceOuter)
            {
                // Enter run mode
                newMode = MovementModeController.MovementMode.Run;
                checkStuck = true;
            }
            else if (_currentMode == MovementModeController.MovementMode.Run && distanceToOwner > RunDistanceInner)
            {
                // Stay in run mode (within buffer)
                newMode = MovementModeController.MovementMode.Run;
                checkStuck = true;
            }
            // JOG: Medium-far distance
            else if (distanceToOwner > JogDistanceOuter)
            {
                // Enter jog mode
                newMode = MovementModeController.MovementMode.Jog;
                checkStuck = distanceToOwner > JogDistanceOuter + 3f;
            }
            else if (_currentMode == MovementModeController.MovementMode.Jog && distanceToOwner > JogDistanceInner)
            {
                // Stay in jog mode (within buffer)
                newMode = MovementModeController.MovementMode.Jog;
            }
            // WALK: Close but need to keep up
            else if (distanceToOwner > WalkDistanceOuter && ownerMoving)
            {
                // Enter walk mode
                newMode = MovementModeController.MovementMode.Walk;
            }
            else if (_currentMode == MovementModeController.MovementMode.Walk && distanceToOwner > WalkDistanceInner && ownerMoving)
            {
                // Stay in walk mode (within buffer)
                newMode = MovementModeController.MovementMode.Walk;
            }
            // STOP: Close enough, or owner not moving
            else if (distanceToOwner <= StopDistanceInner)
            {
                // Definitely stop - very close
                newMode = MovementModeController.MovementMode.Stop;
                speedMultiplier = 1.0f;  // No catch-up needed when close
            }
            else if (_currentMode == MovementModeController.MovementMode.Stop && distanceToOwner <= StopDistanceOuter)
            {
                // Stay stopped (within buffer)
                newMode = MovementModeController.MovementMode.Stop;
                speedMultiplier = 1.0f;
            }
            else if (!ownerMoving && distanceToOwner <= WalkDistanceOuter)
            {
                // Owner is idle and we're reasonably close - stop
                newMode = MovementModeController.MovementMode.Stop;
                speedMultiplier = 1.0f;
            }
            else
            {
                // Default: walk slowly toward owner
                newMode = ownerMoving ? MovementModeController.MovementMode.Walk : MovementModeController.MovementMode.Stop;
            }
            
            _currentMode = newMode;
            _shouldCheckStuck = checkStuck;
            
            return (newMode, checkStuck, speedMultiplier);
        }
        
        /// <summary>
        /// Legacy overload for backward compatibility - returns tuple without speed multiplier.
        /// </summary>
        public (MovementModeController.MovementMode mode, bool checkStuck) EvaluateFollowStateSimple(
            float distanceToOwner, bool ownerMoving, bool canSprint)
        {
            var (mode, checkStuck, _) = EvaluateFollowState(distanceToOwner, ownerMoving, canSprint);
            return (mode, checkStuck);
        }

        /// <summary>
        /// Updates grounded state check.
        /// </summary>
  public void UpdateGroundedState()
  {
            if (Time.time - _lastGroundedCheck < GROUNDED_CHECK_INTERVAL) return;
            _lastGroundedCheck = Time.time;

      if (_character != null)
    {
   _isGrounded = _character.IsOnGround();
       return;
 }

            _isGrounded = Physics.Raycast(_transform.position + Vector3.up * 0.1f, Vector3.down, 0.3f);
        }

        /// <summary>
        /// Updates stuck detection. Returns true if stuck.
     /// </summary>
        public bool UpdateStuckDetection(bool isInCombat, bool isTransitioning, bool isIdle)
        {
   if (isInCombat) return false;
        if (!_shouldCheckStuck) return false;
        if (isTransitioning) return false;
   if (isIdle) return false;

         if (Time.time - _lastStuckEvaluation < STUCK_EVAL_INTERVAL) return false;
     _lastStuckEvaluation = Time.time;

       float timeSinceStuckCheck = Time.time - _stuckCheckStartTime;
      if (timeSinceStuckCheck < StuckDetectionTime) return false;

        float distanceMoved = Vector3.Distance(_transform.position, _stuckCheckStartPos);

            if (distanceMoved < StuckMovementThreshold)
            {
      _consecutiveStuckChecks++;

 if (VerboseLogging)
       {
          Debug.Log($"[FollowBehavior] Stuck check {_consecutiveStuckChecks}/{StuckChecksBeforeJump} " +
               $"(moved {distanceMoved:F2}m in {timeSinceStuckCheck:F1}s)");
           }
            }
            else
    {
             _consecutiveStuckChecks = 0;
       }

            _stuckCheckStartPos = _transform.position;
     _stuckCheckStartTime = Time.time;

            return _consecutiveStuckChecks >= StuckChecksBeforeJump;
        }

   /// <summary>
        /// Checks if there's a significant obstacle ahead that can be jumped over.
        /// </summary>
   public bool HasSignificantObstacleAhead()
        {
            Vector3 forward = _transform.forward;
    Vector3 originLow = _transform.position + Vector3.up * 0.2f;
       Vector3 originMid = _transform.position + Vector3.up * 0.5f;

    if (Physics.Raycast(originLow, forward, out RaycastHit hitLow, ObstacleCheckDistance))
            {
     bool hitMid = Physics.Raycast(originMid, forward, out RaycastHit hitMid2, ObstacleCheckDistance);

 if (!hitMid)
      {
           float obstacleHeight = hitLow.point.y - _transform.position.y;
       if (obstacleHeight > 0.3f && obstacleHeight <= MaxJumpableHeight)
   {
            Vector3 topCheck = hitLow.point + Vector3.up * MaxJumpableHeight;
   if (!Physics.Raycast(topCheck, Vector3.down, MaxJumpableHeight * 0.5f))
    {
           return true;
      }
    }
           }
  }

         return false;
        }

        /// <summary>
/// Checks if we should jump to follow the owner (owner is above us).
        /// </summary>
        public bool ShouldJumpToFollowOwner(Vector3 ownerPosition)
  {
            float heightDiff = ownerPosition.y - _transform.position.y;
  float horizontalDist = Vector3.Distance(
      new Vector3(_transform.position.x, 0, _transform.position.z),
         new Vector3(ownerPosition.x, 0, ownerPosition.z)
        );

     return heightDiff > MinOwnerHeightDiffForJump &&
             heightDiff <= MaxJumpableHeight &&
         horizontalDist < 4f;
        }

        /// <summary>
        /// Checks if a jump can be performed (grounded and cooldown ready).
        /// </summary>
        public bool CanJump()
        {
            return _isGrounded && Time.time - _lastJumpTime >= JumpCooldown;
        }

     /// <summary>
        /// Executes a jump.
    /// </summary>
    public void ExecuteJump(bool movingForward)
        {
            _lastJumpTime = Time.time;
  _consecutiveStuckChecks = 0;
      _stuckCheckStartPos = _transform.position;
         _stuckCheckStartTime = Time.time;

            // Play animation
  if (_zanim != null)
       {
   _zanim.SetTrigger("jump");
        }
    else if (_animator != null && HasAnimatorParameter("jump"))
       {
     _animator.SetTrigger("jump");
   }

    // Apply force
       ApplyJumpForce(movingForward);

  if (VerboseLogging)
      {
                Debug.Log("[FollowBehavior] Executed jump!");
       }
      }

private void ApplyJumpForce(bool movingForward)
        {
if (_character != null)
{
        try
                {
 var jumpMethod = typeof(Character).GetMethod("Jump",
              System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | 
     System.Reflection.BindingFlags.Instance);

           if (jumpMethod != null)
         {
   jumpMethod.Invoke(_character, null);
   return;
               }
                }
                catch { }
            }

  // Only apply jump force if rigidbody is NOT kinematic
            // Unity 6 doesn't allow AddForce on kinematic bodies
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
         Vector3 jumpVelocity = Vector3.up * JumpForce;

  if (movingForward)
        {
           jumpVelocity += _transform.forward * JumpForwardBoost;
        }

      _rigidbody.AddForce(jumpVelocity, ForceMode.VelocityChange);
  }
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

/// <summary>
        /// Sets whether stuck checking should be active.
        /// </summary>
      public void SetStuckCheckEnabled(bool enabled)
        {
      _shouldCheckStuck = enabled;
        }

        /// <summary>
        /// Resets stuck detection state.
        /// </summary>
 public void ResetStuckDetection()
        {
            _consecutiveStuckChecks = 0;
        _stuckCheckStartPos = _transform.position;
            _stuckCheckStartTime = Time.time;
        }
    }
}
