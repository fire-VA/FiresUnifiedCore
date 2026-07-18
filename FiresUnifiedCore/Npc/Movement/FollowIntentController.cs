using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles follow intent determination with hysteresis to prevent oscillation.
    /// Uses buffer zones (inner/outer thresholds) for smooth speed transitions.
    /// Extracted from CompanionCombatMovement to reduce file size.
    /// </summary>
    public class FollowIntentController
    {
        // Distance thresholds with hysteresis (inner = exit, outer = enter). TIGHT so a following companion
        // sticks close and runs/sprints to catch up promptly. This is the intent controller that gates
        // follow-vs-idle for CompanionCombatMovement; it MUST agree with CompanionAI.Idle's DetermineFollowSpeed
        // (the speed tier) or the two fight (companion decides "idle" here while the other says "run"). These
        // used to sit at run=18 / sprint=30 - far too lenient, so the companion trailed and never caught up.
        public float StopDistanceInner { get; set; } = 1.5f;
        public float StopDistanceOuter { get; set; } = 2.5f;
        public float WalkDistanceInner { get; set; } = 2.5f;
        public float WalkDistanceOuter { get; set; } = 4f;
        public float JogDistanceInner { get; set; } = 4f;
        public float JogDistanceOuter { get; set; } = 6f;
        public float RunDistanceInner { get; set; } = 6f;
        public float RunDistanceOuter { get; set; } = 8f;
        public float SprintDistance { get; set; } = 12f;
        
        // Timing
        private const float INTENT_MIN_DURATION = 2.0f;
        private const float IDLE_REFOLLOW_DISTANCE = 10f;
        
        // State
        private FollowIntent _currentMode = FollowIntent.Idle;
        private FollowIntent _lastIntent = FollowIntent.Idle;
        private float _intentChangeTime = -10f;
        
        public static bool VerboseLogging = false;
        
        // Properties
        public FollowIntent CurrentMode => _currentMode;
        public FollowIntent LastIntent => _lastIntent;
        
        /// <summary>
        /// Determines if the companion should be checking for stuck state.
        /// </summary>
        public bool ShouldCheckStuck(float distToOwner)
        {
            return _currentMode == FollowIntent.Run || 
                   _currentMode == FollowIntent.Sprint ||
                   (_currentMode == FollowIntent.Jog && distToOwner > JogDistanceOuter + 3f);
        }
        
        /// <summary>
        /// Determines if companion should stop based on distance and owner movement.
        /// </summary>
        public bool ShouldStop(float distToOwner, bool ownerMoving)
        {
            if (distToOwner <= StopDistanceInner) return true;
            if (!ownerMoving && distToOwner <= StopDistanceOuter) return true;
            return false;
        }
        
        /// <summary>
        /// Determines if companion is within idle wander zone (player stationary).
        /// </summary>
        public bool IsInIdleWanderZone(float distToOwner, bool ownerMoving)
        {
            return !ownerMoving && distToOwner < IDLE_REFOLLOW_DISTANCE;
        }
        
        /// <summary>
        /// Determines the desired follow intent using hysteresis buffer zones.
        /// </summary>
        public FollowIntent DetermineIntent(float distToOwner, bool ownerMoving, bool canSprint)
        {
            FollowIntent newIntent = _currentMode;
            
            // SPRINT: Emergency catch-up
            if (distToOwner > SprintDistance)
            {
                newIntent = canSprint ? FollowIntent.Sprint : FollowIntent.Run;
            }
            // RUN: Far from owner
            else if (distToOwner > RunDistanceOuter)
            {
                newIntent = FollowIntent.Run;
            }
            else if (_currentMode == FollowIntent.Run && distToOwner > RunDistanceInner)
            {
                newIntent = FollowIntent.Run; // Stay in run (buffer)
            }
            else if (_currentMode == FollowIntent.Sprint && distToOwner > RunDistanceInner)
            {
                newIntent = FollowIntent.Run; // Slow from sprint to run
            }
            // JOG: Medium distance
            else if (distToOwner > JogDistanceOuter)
            {
                newIntent = FollowIntent.Jog;
            }
            else if (_currentMode == FollowIntent.Jog && distToOwner > JogDistanceInner)
            {
                newIntent = FollowIntent.Jog; // Stay in jog (buffer)
            }
            else if (_currentMode == FollowIntent.Run && distToOwner > JogDistanceInner)
            {
                newIntent = FollowIntent.Jog; // Slow from run to jog
            }
            // WALK: Close but keeping up
            else if (distToOwner > WalkDistanceOuter && ownerMoving)
            {
                newIntent = FollowIntent.Walk;
            }
            else if (_currentMode == FollowIntent.Walk && distToOwner > WalkDistanceInner && ownerMoving)
            {
                newIntent = FollowIntent.Walk; // Stay in walk (buffer)
            }
            else if (_currentMode == FollowIntent.Jog && distToOwner > WalkDistanceInner && ownerMoving)
            {
                newIntent = FollowIntent.Walk; // Slow from jog to walk
            }
            // STOP: Close enough or owner idle
            else if (distToOwner <= StopDistanceInner)
            {
                newIntent = FollowIntent.Idle;
            }
            else if (_currentMode == FollowIntent.Idle && distToOwner <= StopDistanceOuter)
            {
                newIntent = FollowIntent.Idle; // Stay stopped (buffer)
            }
            else if (!ownerMoving && distToOwner <= WalkDistanceOuter)
            {
                newIntent = FollowIntent.Idle; // Owner idle, close enough
            }
            else
            {
                // Default: walk if owner moving, stop if idle
                newIntent = ownerMoving ? FollowIntent.Walk : FollowIntent.Idle;
            }
            
            _currentMode = newIntent;
            return newIntent;
        }
        
        /// <summary>
        /// Determines if we should change intent, applying hysteresis.
        /// </summary>
        public bool ShouldChangeIntent(FollowIntent desiredIntent, float distToOwner)
        {
            // Always allow starting movement from idle
            if (_lastIntent == FollowIntent.Idle && desiredIntent != FollowIntent.Idle)
                return true;
            
            // Always allow stopping in buffer zone
            if (desiredIntent == FollowIntent.Idle && distToOwner <= StopDistanceOuter)
                return true;
            
            // Always allow urgent speed increases
            if (IsMoreUrgent(desiredIntent, _lastIntent))
                return true;
            
            // Require minimum commitment time for slowing down
            float timeSinceChange = Time.time - _intentChangeTime;
            if (timeSinceChange < INTENT_MIN_DURATION)
                return false;
            
            // Apply hysteresis for transitions
            if (desiredIntent == FollowIntent.Idle && _lastIntent != FollowIntent.Idle)
                return distToOwner <= StopDistanceOuter;
            
            if (desiredIntent == FollowIntent.Walk && _lastIntent == FollowIntent.Jog)
                return distToOwner <= JogDistanceInner;
            
            if (desiredIntent == FollowIntent.Jog && _lastIntent == FollowIntent.Run)
                return distToOwner <= RunDistanceInner;
            
            return true;
        }
        
        /// <summary>
        /// Sets the follow intent and tracks change time.
        /// </summary>
        public void SetIntent(FollowIntent intent)
        {
            if (intent != _lastIntent)
            {
                _intentChangeTime = Time.time;
                _lastIntent = intent;
                
                if (VerboseLogging)
                    Debug.Log($"[FollowIntentController] Intent changed to {intent}");
            }
        }
        
        /// <summary>
        /// Returns true if newIntent is more urgent than currentIntent.
        /// </summary>
        public static bool IsMoreUrgent(FollowIntent newIntent, FollowIntent currentIntent)
        {
            return GetUrgency(newIntent) > GetUrgency(currentIntent);
        }
        
        private static int GetUrgency(FollowIntent intent) => intent switch
        {
            FollowIntent.Idle => 0,
            FollowIntent.Walk => 1,
            FollowIntent.Jog => 2,
            FollowIntent.Run => 3,
            FollowIntent.Sprint => 4,
            _ => 0
        };
        
        /// <summary>
        /// Resets the controller state.
        /// </summary>
        public void Reset()
        {
            _currentMode = FollowIntent.Idle;
            _lastIntent = FollowIntent.Idle;
            _intentChangeTime = -10f;
        }
    }
    
    /// <summary>Simplified follow intent for distance-based following.</summary>
    public enum FollowIntent
    {
        Idle,   // Stop - very close
        Walk,   // Close but moving
        Jog,    // Medium distance
        Run,    // Far
        Sprint  // Emergency catch-up
    }
}
