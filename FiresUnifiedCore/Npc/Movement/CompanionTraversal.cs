using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Ground traversal for a companion: grounded state, conservative stuck detection, and jumping over an obstacle or
    /// up to an owner standing above. Follow SPEED and steering are CompanionAI's (CompanionAI.Idle) - this class used
    /// to carry a second set of follow distance tiers that nothing called, which is why there appeared to be two follow
    /// systems. Only StuckDetectionHandler drives what is left.
    /// </summary>
    public class CompanionTraversal
  {

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
        private const float StuckEvalInterval = 2.0f;

        // Grounded state
        private bool _isGrounded;
        private float _lastGroundedCheck;
        private const float GroundedCheckInterval = 0.2f;

        // References
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly Rigidbody _rigidbody;
        private readonly ZSyncAnimation _zanim;
        private readonly Animator _animator;

        public static bool VerboseLogging = false;

        public bool IsGrounded => _isGrounded;
        public bool ShouldCheckStuck => _shouldCheckStuck;

        public CompanionTraversal(Transform transform, Character character, Rigidbody rigidbody, 
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
        /// Updates grounded state check.
        /// </summary>
  public void UpdateGroundedState()
  {
            if (Time.time - _lastGroundedCheck < GroundedCheckInterval) return;
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

         if (Time.time - _lastStuckEvaluation < StuckEvalInterval) return false;
     _lastStuckEvaluation = Time.time;

       float timeSinceStuckCheck = Time.time - _stuckCheckStartTime;
      if (timeSinceStuckCheck < StuckDetectionTime) return false;

        float distanceMoved = Vector3.Distance(_transform.position, _stuckCheckStartPos);

            if (distanceMoved < StuckMovementThreshold)
            {
      _consecutiveStuckChecks++;

 if (VerboseLogging)
       {
          Debug.Log($"[CompanionTraversal] Stuck check {_consecutiveStuckChecks}/{StuckChecksBeforeJump} " +
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
                Debug.Log("[CompanionTraversal] Executed jump!");
       }
      }

private void ApplyJumpForce(bool movingForward)
        {
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
