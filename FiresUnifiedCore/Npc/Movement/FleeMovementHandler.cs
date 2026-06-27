using UnityEngine;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles flee/kiting movement for companions during emergencies.
    /// 
    /// RESPONSIBILITIES:
    /// - Execute flee movement toward owner
    /// - Execute flee movement away from enemies
    /// - Handle terrain-aware escape routes
    /// - Provide fallback flee behavior
    /// 
    /// DESIGN:
    /// This is a helper class, not a MonoBehaviour. It's instantiated and owned
    /// by CompanionCombatMovement which calls its methods as needed.
    /// 
    /// FLEE PHILOSOPHY:
    /// - Always run (never walk during flee)
    /// - Prefer running toward owner for safety
    /// - Use terrain awareness to avoid obstacles
    /// - Direct movement control (bypasses pathfinding for reliability)
    /// </summary>
    public class FleeMovementHandler
    {
        #region Dependencies
        
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly CompanionController _companion;
        private readonly TerrainAwareness _terrainAwareness;
        
        #endregion
        
        #region State
        
        private bool _hasFleeRequest;
        private Vector3 _fleeTargetPosition;
        private float _fleeRequestTime;
        
        private const float FLEE_REQUEST_TIMEOUT = 0.5f;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Returns true if there's an active flee request.</summary>
        public bool HasFleeRequest => _hasFleeRequest && Time.time - _fleeRequestTime < FLEE_REQUEST_TIMEOUT;
        
        /// <summary>The target position for flee movement.</summary>
        public Vector3 FleeTargetPosition => _fleeTargetPosition;
        
        #endregion
        
        #region Constructor
        
        public FleeMovementHandler(
            Transform transform,
            Character character,
            CompanionController companion,
            TerrainAwareness terrainAwareness)
        {
            _transform = transform;
            _character = character;
            _companion = companion;
            _terrainAwareness = terrainAwareness;
        }
        
        #endregion
        
        #region Request Management
        
        /// <summary>
        /// Requests flee movement toward a target position.
        /// Called by CompanionAI when entering Fleeing state.
        /// </summary>
        public void RequestFleeMovement(Vector3 targetPosition)
        {
            _hasFleeRequest = true;
            _fleeTargetPosition = targetPosition;
            _fleeRequestTime = Time.time;
            
            if (VerboseLogging)
            {
                Debug.Log($"[FleeMovementHandler] Flee movement requested to {targetPosition}");
            }
        }
        
        /// <summary>
        /// Clears the flee movement request.
        /// </summary>
        public void ClearFleeRequest()
        {
            _hasFleeRequest = false;
        }
        
        #endregion
        
        #region Flee Execution
        
        /// <summary>
        /// Executes flee movement toward the requested position.
        /// Uses direct character control for reliable movement during emergencies.
        /// Returns the movement direction that was applied.
        /// </summary>
        public Vector3 ExecuteFleeMovement()
        {
            if (_character == null) return Vector3.zero;
            
            Vector3 myPos = _transform.position;
            Vector3 moveDir = (_fleeTargetPosition - myPos).normalized;
            moveDir.y = 0;
            
            // Apply terrain awareness if available
            if (_terrainAwareness != null && !_terrainAwareness.IsDirectionSafe(moveDir))
            {
                moveDir = _terrainAwareness.GetSafeMovementDirection(moveDir);
            }
            
            if (moveDir.sqrMagnitude > 0.01f)
            {
                // Single-writer: compute the flee direction only. The owning coordinator
                // (CompanionCombatMovement, which holds Combat authority) drives it through UMA via
                // SetMoveDirSafe — a raw SetMoveDir here would race UMA's ApplyMovement and slide.
                if (VerboseLogging)
                {
                    Debug.Log($"[FleeMovementHandler] FLEE direction computed: dir={moveDir}, target={_fleeTargetPosition}");
                }

                return moveDir;
            }

            return Vector3.zero;
        }
        
        /// <summary>
        /// Fallback flee movement when no specific target is provided.
        /// Runs toward owner or away from nearest enemy.
        /// Returns the movement direction that was applied.
        /// </summary>
        public Vector3 ExecuteFallbackFleeMovement()
        {
            if (_character == null) return Vector3.zero;
            
            Vector3 moveDir = Vector3.zero;
            
            // Try to run toward owner
            var owner = _companion?.GetOwner();
            if (owner != null)
            {
                moveDir = (owner.transform.position - _transform.position).normalized;
                moveDir.y = 0;
            }
            else
            {
                // No owner - run away from nearest enemy
                moveDir = CalculateEscapeFromNearestEnemy();
            }
            
            // Apply terrain awareness
            if (_terrainAwareness != null && moveDir.sqrMagnitude > 0.01f)
            {
                if (!_terrainAwareness.IsDirectionSafe(moveDir))
                {
                    moveDir = _terrainAwareness.GetSafeMovementDirection(moveDir);
                }
            }
            
            if (moveDir.sqrMagnitude > 0.01f)
            {
                // Single-writer: compute only; the coordinator drives through UMA (see ExecuteFleeMovement).
                return moveDir;
            }

            return Vector3.zero;
        }

        /// <summary>
        /// Calculates escape direction away from the nearest enemy.
        /// </summary>
        private Vector3 CalculateEscapeFromNearestEnemy()
        {
            Character nearestEnemy = null;
            float nearestDist = float.MaxValue;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_transform.position, character.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestEnemy = character;
                }
            }
            
            if (nearestEnemy != null)
            {
                Vector3 moveDir = (_transform.position - nearestEnemy.transform.position).normalized;
                moveDir.y = 0;
                return moveDir;
            }
            
            return Vector3.zero;
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// Returns the distance to the flee target.
        /// </summary>
        public float GetDistanceToFleeTarget()
        {
            if (!_hasFleeRequest) return float.MaxValue;
            return Vector3.Distance(_transform.position, _fleeTargetPosition);
        }
        
        /// <summary>
        /// Returns true if we've reached (or are very close to) the flee target.
        /// </summary>
        public bool HasReachedFleeTarget(float threshold = 3f)
        {
            return GetDistanceToFleeTarget() <= threshold;
        }
        
        #endregion
    }
}
