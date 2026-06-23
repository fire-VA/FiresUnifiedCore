using UnityEngine;
using FiresCore.Npc.Combat;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles ranged weapon movement requests from BowBehavior/CrossbowBehavior.
    /// Extracted from CompanionCombatMovement to reduce file size.
    /// </summary>
    public class RangedMovementHandler
    {
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly CompanionController _companion;
        private readonly TerrainAwareness _terrainAwareness;
        
        // Dependencies set after construction
        private StaminaManager _staminaManager;
        private CompanionAI _companionAI;
        
        // Settings
        public float ApproachCommitmentDuration { get; set; } = 1.5f;
        public float StrafeCommitmentDuration { get; set; } = 1.0f;
        
        // State
        private bool _hasRangedMovementRequest;
        private Vector3 _rangedRequestedDirection;
        private float _rangedRequestTime;
        private const float RANGED_REQUEST_TIMEOUT = 0.5f;
        
        public static bool VerboseLogging = false;
        
        // Properties
        public bool HasRangedMovementRequest => _hasRangedMovementRequest && 
            Time.time - _rangedRequestTime <= RANGED_REQUEST_TIMEOUT;
        public Vector3 RequestedDirection => _rangedRequestedDirection;
        
        public RangedMovementHandler(
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
        
        public void SetDependencies(StaminaManager staminaManager, CompanionAI companionAI)
        {
            _staminaManager = staminaManager;
            _companionAI = companionAI;
        }
        
        /// <summary>Clears expired requests. Call every frame.</summary>
        public void Update()
        {
            if (_hasRangedMovementRequest && Time.time - _rangedRequestTime > RANGED_REQUEST_TIMEOUT)
                _hasRangedMovementRequest = false;
        }
        
        /// <summary>Clears the current ranged movement request.</summary>
        public void ClearRequest()
        {
            _hasRangedMovementRequest = false;
        }
        
        /// <summary>
        /// Handles bow movement request. Returns null if request should be ignored,
        /// otherwise returns the movement result to apply.
        /// </summary>
        public RangedMovementResult HandleBowRequest(BowBehavior.MovementRequest request, Vector3 direction, bool isInCombat)
        {
            if (!isInCombat) return null;
            
            // Ignore during flee
            if (_companionAI != null && _companionAI.CurrentState == CompanionAI.AIState.Fleeing)
            {
                if (VerboseLogging)
                    Debug.Log($"[RangedMovementHandler] {_companion?.companionName} IGNORING bow movement - fleeing");
                return null;
            }
            
            // Ignore during critical stamina recovery
            if (_staminaManager != null && (_staminaManager.IsInCriticalRecovery() || _staminaManager.ShouldTreatAsLowHealth()))
            {
                if (VerboseLogging)
                    Debug.Log($"[RangedMovementHandler] {_companion?.companionName} IGNORING bow movement - critical stamina");
                return null;
            }
            
            _hasRangedMovementRequest = true;
            _rangedRequestedDirection = direction;
            _rangedRequestTime = Time.time;
            
            return ConvertBowRequest(request, direction);
        }
        
        /// <summary>
        /// Handles crossbow movement request. Returns null if request should be ignored.
        /// </summary>
        public RangedMovementResult HandleCrossbowRequest(CrossbowBehavior.MovementRequest request, Vector3 direction, bool isInCombat)
        {
            if (!isInCombat) return null;
            
            // Ignore during flee
            if (_companionAI != null && _companionAI.CurrentState == CompanionAI.AIState.Fleeing)
            {
                if (VerboseLogging)
                    Debug.Log($"[RangedMovementHandler] {_companion?.companionName} IGNORING crossbow movement - fleeing");
                return null;
            }
            
            // Ignore during critical stamina recovery
            if (_staminaManager != null && (_staminaManager.IsInCriticalRecovery() || _staminaManager.ShouldTreatAsLowHealth()))
            {
                if (VerboseLogging)
                    Debug.Log($"[RangedMovementHandler] {_companion?.companionName} IGNORING crossbow movement - critical stamina");
                return null;
            }
            
            _hasRangedMovementRequest = true;
            _rangedRequestedDirection = direction;
            _rangedRequestTime = Time.time;
            
            return ConvertCrossbowRequest(request, direction);
        }
        
        private RangedMovementResult ConvertBowRequest(BowBehavior.MovementRequest request, Vector3 direction)
        {
            var result = new RangedMovementResult();
            
            switch (request)
            {
                case BowBehavior.MovementRequest.Stop:
                    result.Intent = MovementIntentType.PlantedFiring;
                    result.Direction = Vector3.zero;
                    result.Duration = 2f;
                    result.ShouldStop = true;
                    break;
                    
                case BowBehavior.MovementRequest.RunAway:
                    result.Intent = MovementIntentType.Retreat;
                    result.Direction = GetSafeDirection(direction);
                    result.Duration = ApproachCommitmentDuration;
                    result.UseRun = true;
                    break;
                    
                case BowBehavior.MovementRequest.Backpedal:
                    result.Intent = MovementIntentType.Reposition;
                    result.Direction = GetSafeDirection(direction) * 0.5f;
                    result.Duration = 1f;
                    result.UseWalk = true;
                    break;
                    
                case BowBehavior.MovementRequest.Strafe:
                    result.Intent = MovementIntentType.Strafe;
                    result.Direction = direction * 0.7f;
                    result.Duration = StrafeCommitmentDuration;
                    result.StrafeDirection = direction.x > 0 ? 1 : -1;
                    break;
                    
                case BowBehavior.MovementRequest.Approach:
                    result.Intent = MovementIntentType.Approach;
                    result.Direction = direction;
                    result.Duration = ApproachCommitmentDuration;
                    break;
                    
                default:
                    return null;
            }
            
            if (VerboseLogging)
                Debug.Log($"[RangedMovementHandler] Bow request: {request} -> {result.Intent}");
            
            return result;
        }
        
        private RangedMovementResult ConvertCrossbowRequest(CrossbowBehavior.MovementRequest request, Vector3 direction)
        {
            var result = new RangedMovementResult();
            
            switch (request)
            {
                case CrossbowBehavior.MovementRequest.Stop:
                    result.Intent = MovementIntentType.PlantedFiring;
                    result.Direction = Vector3.zero;
                    result.Duration = 2f;
                    result.ShouldStop = true;
                    break;
                    
                case CrossbowBehavior.MovementRequest.RunAway:
                    result.Intent = MovementIntentType.Retreat;
                    result.Direction = GetSafeDirection(direction);
                    result.Duration = ApproachCommitmentDuration;
                    result.UseRun = true;
                    break;
                    
                case CrossbowBehavior.MovementRequest.Approach:
                    result.Intent = MovementIntentType.Approach;
                    result.Direction = direction;
                    result.Duration = ApproachCommitmentDuration;
                    break;
                    
                case CrossbowBehavior.MovementRequest.Strafe:
                    result.Intent = MovementIntentType.Strafe;
                    result.Direction = direction * 0.7f;
                    result.Duration = StrafeCommitmentDuration;
                    result.StrafeDirection = direction.x > 0 ? 1 : -1;
                    break;
                    
                case CrossbowBehavior.MovementRequest.Dodge:
                    result.Intent = MovementIntentType.Dodge;
                    result.Direction = direction;
                    result.Duration = 0.5f;
                    break;
                    
                default:
                    return null;
            }
            
            if (VerboseLogging)
                Debug.Log($"[RangedMovementHandler] Crossbow request: {request} -> {result.Intent}");
            
            return result;
        }
        
        private Vector3 GetSafeDirection(Vector3 direction)
        {
            if (_terrainAwareness != null)
                return _terrainAwareness.GetSafeMovementDirection(direction);
            return direction;
        }
        
        /// <summary>
        /// Checks if a given intent should be blocked during critical stamina recovery.
        /// Only retreat intents are allowed during critical recovery.
        /// </summary>
        public bool ShouldBlockIntent(MovementIntentType intent)
        {
            if (_staminaManager == null) return false;
            if (!_staminaManager.IsInCriticalRecovery() && !_staminaManager.ShouldTreatAsLowHealth()) return false;
            
            // Only retreat is allowed during critical recovery
            if (intent != MovementIntentType.Retreat)
            {
                if (VerboseLogging)
                    Debug.Log($"[RangedMovementHandler] BLOCKED intent {intent} - critical stamina recovery");
                return true;
            }
            return false;
        }
    }
    
    /// <summary>Result of processing a ranged movement request.</summary>
    public class RangedMovementResult
    {
        public MovementIntentType Intent { get; set; }
        public Vector3 Direction { get; set; }
        public float Duration { get; set; }
        public bool ShouldStop { get; set; }
        public bool UseWalk { get; set; }
        public bool UseRun { get; set; }
        public int StrafeDirection { get; set; }
    }
    
    /// <summary>Simplified intent types for ranged movement.</summary>
    public enum MovementIntentType
    {
        None,
        PlantedFiring,
        Retreat,
        Reposition,
        Strafe,
        Approach,
        Dodge
    }
}
