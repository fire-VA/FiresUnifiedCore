using UnityEngine;
using FiresCore.Npc.Formation;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles player idle detection and companion relaxed behavior.
    /// 
    /// RESPONSIBILITIES:
    /// - Track when player is stationary
    /// - Manage companion relaxed following state
    /// - Handle relaxed wander behavior
    /// - Determine when companion should relax vs stay alert
    /// 
    /// DESIGN:
    /// This is a helper class, not a MonoBehaviour. It's instantiated and owned
    /// by CompanionCombatMovement which calls its methods as needed.
    /// 
    /// RELAXED FOLLOWING:
    /// When the player is idle for a period, the companion can:
    /// - Stop at a comfortable distance
    /// - Look around occasionally
    /// - Wander briefly within a zone
    /// This makes companions feel more natural and less robotic.
    /// </summary>
    public class PlayerIdleHandler
    {
        #region Settings
        
        public float PlayerIdleTime { get; set; } = 3f;
        public float IdleStopDistance { get; set; } = 6f;
        public float IdleLookAroundChance { get; set; } = 0.15f;
        public float IdleWanderChance { get; set; } = 0.05f;
        public float IdleMaxWanderDistance { get; set; } = 6f;
        public float IdleStopTimeMin { get; set; } = 2f;
        public float IdleStopTimeMax { get; set; } = 10f;
        
        #endregion
        
        #region Dependencies
        
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly CompanionController _companion;
        
        #endregion
        
        #region State
        
        // Player idle tracking
        private float _playerIdleStartTime = -100f;
        private Vector3 _lastPlayerPosition = Vector3.zero;
        private float _lastPlayerMovementCheck;
        private bool _isPlayerIdle;
        
        // Companion relaxed state
        private bool _isRelaxedFollowing;
        private float _relaxedIdleActionTime;
        private Vector3 _relaxedWanderTarget;
        private bool _hasRelaxedWanderTarget;
        
        // Constants
        private const float PLAYER_IDLE_CHECK_INTERVAL = 0.5f;
        private const float PLAYER_MOVEMENT_THRESHOLD = 0.3f;
        private const float IDLE_WANDER_RADIUS = 8f;
        private const float IDLE_REFOLLOW_DISTANCE = 10f;
        
        // Damage tracking (to prevent relaxing during combat)
        private float _lastDamageTime = -100f;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Returns true if the player has been idle long enough.</summary>
        public bool IsPlayerIdle => _isPlayerIdle;
        
        /// <summary>Returns true if companion is in relaxed following mode.</summary>
        public bool IsRelaxedFollowing => _isRelaxedFollowing;
        
        /// <summary>Returns true if companion has an active wander target.</summary>
        public bool HasRelaxedWanderTarget => _hasRelaxedWanderTarget;
        
        /// <summary>The current relaxed wander target position.</summary>
        public Vector3 RelaxedWanderTarget => _relaxedWanderTarget;
        
        /// <summary>The idle refollow distance threshold.</summary>
        public float IdleRefollowDistance => IDLE_REFOLLOW_DISTANCE;
        
        #endregion
        
        #region Constructor
        
        public PlayerIdleHandler(
            Transform transform,
            Character character,
            CompanionController companion)
        {
            _transform = transform;
            _character = character;
            _companion = companion;
        }
        
        #endregion
        
        #region Update
        
        /// <summary>
        /// Updates player idle state and companion relaxed following state.
        /// Call this every frame (or at regular intervals).
        /// </summary>
        public void Update()
        {
            if (Time.time - _lastPlayerMovementCheck < PLAYER_IDLE_CHECK_INTERVAL) return;
            _lastPlayerMovementCheck = Time.time;
            
            var owner = _companion?.GetOwner();
            if (owner == null)
            {
                _isPlayerIdle = false;
                _isRelaxedFollowing = false;
                return;
            }
            
            Vector3 currentPos = owner.transform.position;
            float movementDist = Vector3.Distance(currentPos, _lastPlayerPosition);
            float distToOwner = Vector3.Distance(_transform.position, currentPos);
            
            // Also check player's actual velocity for more responsive detection
            float playerVelocity = owner.GetVelocity().magnitude;
            bool playerIsMoving = movementDist > PLAYER_MOVEMENT_THRESHOLD || playerVelocity > 0.5f;
            
            // Check if player has moved
            if (playerIsMoving)
            {
                // Player is moving - IMMEDIATELY reset idle timer and exit relaxed state
                _playerIdleStartTime = Time.time;
                
                // CRITICAL: Immediately exit relaxed state when player starts moving
                // This ensures companions snap back to following behavior
                if (_isPlayerIdle || _isRelaxedFollowing)
                {
                    if (VerboseLogging)
                        Debug.Log($"[PlayerIdleHandler] Player started moving - exiting idle/relaxed state immediately");
                }
                
                _isPlayerIdle = false;
                _isRelaxedFollowing = false;
                _hasRelaxedWanderTarget = false;
            }
            else
            {
                // Player is stationary - check if idle threshold reached
                if (Time.time - _playerIdleStartTime >= PlayerIdleTime)
                {
                    _isPlayerIdle = true;
                    
                    // Enter relaxed following if we're close enough to owner
                    if (distToOwner <= IdleStopDistance)
                    {
                        _isRelaxedFollowing = true;
                    }
                }
            }
            
            // Exit relaxed state if we're too far from owner
            if (_isRelaxedFollowing && distToOwner > IdleStopDistance * 1.5f)
            {
                _isRelaxedFollowing = false;
                _hasRelaxedWanderTarget = false;
            }
            
            _lastPlayerPosition = currentPos;
        }
        
        #endregion
        
        #region Relaxed Following Behavior
        
        /// <summary>
        /// Updates relaxed following behavior (wander, look around, etc).
        /// Returns the wander target direction if wandering, otherwise Vector3.zero.
        /// </summary>
        /// <param name="shouldStop">Output: true if companion should stop moving.</param>
        /// <returns>Movement direction if wandering, Vector3.zero otherwise.</returns>
        public Vector3 UpdateRelaxedBehavior(out bool shouldStop)
        {
            shouldStop = false;
            
            var owner = _companion?.GetOwner();
            if (owner == null)
            {
                shouldStop = true;
                return Vector3.zero;
            }
            
            // If we have an active wander target, move toward it
            if (_hasRelaxedWanderTarget)
            {
                float distToWanderTarget = Vector3.Distance(_transform.position, _relaxedWanderTarget);
                
                if (distToWanderTarget < 1.5f)
                {
                    // Reached wander target, stop for a variable amount of time
                    _hasRelaxedWanderTarget = false;
                    shouldStop = true;
                    _relaxedIdleActionTime = Time.time + UnityEngine.Random.Range(IdleStopTimeMin, IdleStopTimeMax);
                    
                    if (VerboseLogging)
                        Debug.Log($"[PlayerIdleHandler] Reached relaxed wander target, stopping");
                    
                    return Vector3.zero;
                }
                else
                {
                    // Still walking to wander target
                    Vector3 moveDir = (_relaxedWanderTarget - _transform.position).normalized;
                    return moveDir;
                }
            }
            
            // Not wandering - should stop
            shouldStop = true;
            
            // Periodically decide on idle actions
            if (Time.time < _relaxedIdleActionTime) return Vector3.zero;
            
            // Random chance to do something
            float actionRoll = UnityEngine.Random.value;
            
            // Look around
            if (actionRoll < IdleLookAroundChance)
            {
                _relaxedIdleActionTime = Time.time + UnityEngine.Random.Range(IdleStopTimeMin, IdleStopTimeMax);
                
                if (VerboseLogging)
                    Debug.Log($"[PlayerIdleHandler] Looking around");
            }
            // Wander a bit
            else if (actionRoll < IdleLookAroundChance + IdleWanderChance)
            {
                TryStartRelaxedWander(owner.transform.position);
                _relaxedIdleActionTime = Time.time + UnityEngine.Random.Range(IdleStopTimeMax, IdleStopTimeMax * 1.5f);
            }
            else
            {
                // Just stand still
                _relaxedIdleActionTime = Time.time + UnityEngine.Random.Range(IdleStopTimeMin, IdleStopTimeMax);
                
                if (VerboseLogging)
                    Debug.Log($"[PlayerIdleHandler] Standing still");
            }
            
            return Vector3.zero;
        }
        
        /// <summary>
        /// Tries to pick a nearby wander point.
        /// Uses formation system's idle spread positions when available for coordinated spacing,
        /// falls back to random wander when no formation controller is present.
        /// </summary>
        private void TryStartRelaxedWander(Vector3 ownerPos)
        {
            // FORMATION SYSTEM: Use coordinated idle spread positions when available
            // This ensures companions spread naturally around the player instead of randomly
            var formationController = _companion?.GetFormationController();
            if (formationController != null)
            {
                Vector3 spreadTarget = formationController.GetIdleSpreadTarget();
                if (spreadTarget != Vector3.zero)
                {
                    _relaxedWanderTarget = spreadTarget;
                    _hasRelaxedWanderTarget = true;
                    
                    if (VerboseLogging)
                        Debug.Log($"[PlayerIdleHandler] Using formation idle spread target: {spreadTarget}");
                    return;
                }
            }
            
            // Fallback: random wander direction
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(2f, IdleMaxWanderDistance);
            
            Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * distance;
            Vector3 targetPos = _transform.position + offset;
            
            // Make sure we don't wander too far from owner
            float distFromOwner = Vector3.Distance(targetPos, ownerPos);
            if (distFromOwner > IdleStopDistance)
            {
                Vector3 toOwner = (ownerPos - targetPos).normalized;
                targetPos += toOwner * (distFromOwner - IdleStopDistance + 1f);
            }
            
            // Get ground height
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(targetPos, out groundHeight))
                {
                    targetPos.y = groundHeight;
                }
            }
            
            _relaxedWanderTarget = targetPos;
            _hasRelaxedWanderTarget = true;
            
            if (VerboseLogging)
                Debug.Log($"[PlayerIdleHandler] Wandering to {targetPos}");
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// Returns true if companion should enter relaxed/idle mode due to player being idle.
        /// Only applies when not actively being attacked.
        /// </summary>
        public bool ShouldRelaxDueToPlayerIdle()
        {
            if (!_isPlayerIdle) return false;
            
            // Don't relax if we've been damaged recently
            if (Time.time - _lastDamageTime < 5f) return false;
            
            // Don't relax if there's an enemy very close
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_transform.position, character.transform.position);
                if (dist < 10f) // Enemy too close to relax
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Notifies the handler that damage was taken.
        /// This prevents relaxing while under attack.
        /// </summary>
        public void OnDamageTaken()
        {
            _lastDamageTime = Time.time;
        }
        
        /// <summary>
        /// Clears the player idle state.
        /// Call when player starts moving or interacting.
        /// </summary>
        public void ResetPlayerIdleState()
        {
            _isPlayerIdle = false;
            _isRelaxedFollowing = false;
            _hasRelaxedWanderTarget = false;
            _playerIdleStartTime = Time.time;
            
            if (VerboseLogging)
                Debug.Log($"[PlayerIdleHandler] Reset player idle state - companion should resume following");
        }
        
        /// <summary>
        /// Clears the relaxed following state.
        /// </summary>
        public void ClearRelaxedState()
        {
            _isRelaxedFollowing = false;
            _hasRelaxedWanderTarget = false;
        }
        
        /// <summary>
        /// Force-checks if player is currently moving (for immediate response).
        /// Returns true if player is moving.
        /// </summary>
        public bool CheckPlayerMovingNow()
        {
            var owner = _companion?.GetOwner();
            if (owner == null) return false;
            
            float playerVelocity = owner.GetVelocity().magnitude;
            return playerVelocity > 0.5f;
        }
        
        #endregion
    }
}
