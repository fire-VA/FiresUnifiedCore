using UnityEngine;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles stuck detection and jump logic for companions.
    /// 
    /// RESPONSIBILITIES:
    /// - Detect when companion is stuck (not making progress)
    /// - Determine when to jump over obstacles
    /// - Track grounded state
    /// - Execute jumps when needed
    /// 
    /// DESIGN:
    /// This is a helper class, not a MonoBehaviour. It's instantiated and owned
    /// by the movement coordinator which calls its methods as needed.
    /// 
    /// STUCK DETECTION PHILOSOPHY:
    /// - Only trigger when actually trying to follow (not idle, not in combat)
    /// - Requires extended movement failure before considering stuck
    /// - Conservative - false positives cause weird jumping
    /// </summary>
    public class StuckDetectionHandler
    {
        #region Settings
        
        public float StuckDetectionTime { get; set; } = 8.0f;
        public float StuckMovementThreshold { get; set; } = 2.0f;
        public float JumpCooldown { get; set; } = 5.0f;
        public float JumpForce { get; set; } = 8f;
        public float JumpForwardBoost { get; set; } = 3f;
        public float MaxJumpableHeight { get; set; } = 1.5f;
        public float ObstacleCheckDistance { get; set; } = 1.5f;
        public float MinOwnerHeightDiffForJump { get; set; } = 1.0f;
        public int StuckChecksBeforeJump { get; set; } = 3;
        public float JumpAnimationDuration { get; set; } = 0.8f;
        
        #endregion
        
        #region Dependencies
        
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly Rigidbody _rigidbody;
        private readonly CompanionController _companion;
        private readonly FollowBehavior _followBehavior;
        
        // CombatContext is set lazily since it's obtained via reflection
        private CombatContext _combatContext;
        
        #endregion
        
        #region State
        
        private float _lastJumpTime = -10f;
        private Vector3 _stuckCheckStartPos;
        private float _stuckCheckStartTime;
        private int _consecutiveStuckChecks;
        private bool _isGrounded;
        
        private bool _shouldCheckStuck;
        private float _lastStuckEvaluation;
        
        private const float STUCK_EVAL_INTERVAL = 2.0f;
        private const float GROUNDED_CHECK_INTERVAL = 0.2f;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Returns true if the companion is on the ground.</summary>
        public bool IsGrounded => _isGrounded;
        
        /// <summary>Returns true if stuck checking is currently enabled.</summary>
        public bool ShouldCheckStuck
        {
            get => _shouldCheckStuck;
            set => _shouldCheckStuck = value;
        }
        
        /// <summary>Returns the number of consecutive stuck checks.</summary>
        public int ConsecutiveStuckChecks => _consecutiveStuckChecks;
        
        #endregion
        
        #region Constructor
        
        public StuckDetectionHandler(
            Transform transform,
            Character character,
            Rigidbody rigidbody,
            CompanionController companion,
            FollowBehavior followBehavior)
        {
            _transform = transform;
            _character = character;
            _rigidbody = rigidbody;
            _companion = companion;
            _followBehavior = followBehavior;
            
            _stuckCheckStartPos = transform.position;
            _stuckCheckStartTime = Time.time;
        }
        
        /// <summary>
        /// Sets the combat context (obtained lazily via reflection).
        /// </summary>
        public void SetCombatContext(CombatContext context)
        {
            _combatContext = context;
        }
        
        #endregion
        
        #region Grounded State
        
        /// <summary>
        /// Updates the grounded state.
        /// Call this every frame.
        /// </summary>
        public void UpdateGroundedState()
        {
            _followBehavior?.UpdateGroundedState();
            _isGrounded = _followBehavior?.IsGrounded ?? _character?.IsOnGround() ?? false;
        }
        
        #endregion
        
        #region Stuck Detection
        
        /// <summary>
        /// Updates stuck detection logic.
        /// Call this every frame when not in combat.
        /// </summary>
        /// <param name="isInCombat">Whether currently in combat</param>
        /// <param name="isInTransition">Whether in a state transition</param>
        /// <param name="isIdle">Whether currently idle</param>
        public void UpdateStuckDetection(bool isInCombat, bool isInTransition, bool isIdle)
        {
            if (isInCombat) return;
            if (!_shouldCheckStuck) return;
            if (isInTransition) return;
            if (isIdle) return;
            
            if (Time.time - _lastStuckEvaluation < STUCK_EVAL_INTERVAL) return;
            _lastStuckEvaluation = Time.time;
            
            float timeSinceStuckCheck = Time.time - _stuckCheckStartTime;
            if (timeSinceStuckCheck < StuckDetectionTime) return;
            
            float distanceMoved = Vector3.Distance(_transform.position, _stuckCheckStartPos);
            
            if (distanceMoved < StuckMovementThreshold)
            {
                _consecutiveStuckChecks++;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[StuckDetectionHandler] Stuck check {_consecutiveStuckChecks}/{StuckChecksBeforeJump} " +
                        $"(moved {distanceMoved:F2}m in {timeSinceStuckCheck:F1}s)");
                }
            }
            else
            {
                _consecutiveStuckChecks = 0;
            }
            
            _stuckCheckStartPos = _transform.position;
            _stuckCheckStartTime = Time.time;
        }
        
        /// <summary>
        /// Resets stuck detection state.
        /// Call this after a jump or when movement resumes.
        /// </summary>
        public void ResetStuckDetection()
        {
            _consecutiveStuckChecks = 0;
            _stuckCheckStartPos = _transform.position;
            _stuckCheckStartTime = Time.time;
        }
        
        #endregion
        
        #region Jump Logic
        
        /// <summary>
        /// Updates jump logic and executes jump if needed.
        /// Call this every frame when not in combat.
        /// </summary>
        /// <param name="isInCombat">Whether currently in combat</param>
        /// <param name="isIdle">Whether currently idle</param>
        /// <param name="isMoving">Whether actively moving</param>
        /// <returns>True if a jump was executed</returns>
        public bool UpdateJumpLogic(bool isInCombat, bool isIdle, bool isMoving)
        {
            if (isInCombat) return false;
            if (isIdle) return false;
            
            if (!_isGrounded || Time.time - _lastJumpTime < JumpCooldown) return false;
            
            if (_combatContext != null && !_combatContext.CanPlayAnimation(CombatContext.AnimationPriority.Jump))
                return false;
            
            bool shouldJump = false;
            string jumpReason = "";
            
            // Check if stuck
            if (_consecutiveStuckChecks >= StuckChecksBeforeJump)
            {
                shouldJump = true;
                jumpReason = "stuck";
                _consecutiveStuckChecks = 0;
            }
            
            // Check for obstacle ahead
            if (!shouldJump && _shouldCheckStuck && HasSignificantObstacleAhead(isMoving))
            {
                shouldJump = true;
                jumpReason = "obstacle";
            }
            
            // Check if owner is above us
            if (!shouldJump && ShouldJumpToFollowOwner())
            {
                shouldJump = true;
                jumpReason = "following owner up";
            }
            
            if (shouldJump)
            {
                ExecuteJump(isMoving);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[StuckDetectionHandler] Jumped! Reason: {jumpReason}");
                }
                
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if there's a significant obstacle ahead.
        /// </summary>
        private bool HasSignificantObstacleAhead(bool isMoving)
        {
            if (!isMoving) return false;
            return _followBehavior?.HasSignificantObstacleAhead() ?? false;
        }
        
        /// <summary>
        /// Checks if we should jump to follow the owner up.
        /// </summary>
        private bool ShouldJumpToFollowOwner()
        {
            if (_companion == null || !_companion.IsFollowing) return false;
            var owner = _companion.GetOwner();
            if (owner == null) return false;
            return _followBehavior?.ShouldJumpToFollowOwner(owner.transform.position) ?? false;
        }
        
        /// <summary>
        /// Executes a jump.
        /// </summary>
        private void ExecuteJump(bool isMoving)
        {
            _lastJumpTime = Time.time;
            ResetStuckDetection();
            
            if (_combatContext != null)
            {
                if (!_combatContext.TryLockAnimation("jump", JumpAnimationDuration, CombatContext.AnimationPriority.Jump))
                    return;
            }
            
            _followBehavior?.ExecuteJump(isMoving);
        }
        
        /// <summary>
        /// Forces a jump if conditions allow.
        /// </summary>
        public void ForceJump()
        {
            if (_isGrounded && Time.time - _lastJumpTime >= JumpCooldown)
            {
                ExecuteJump(true);
            }
        }
        
        #endregion
    }
}
