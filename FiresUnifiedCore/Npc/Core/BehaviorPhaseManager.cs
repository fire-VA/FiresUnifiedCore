using System;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Generic phase manager that handles state transitions, timeouts, and logging.
    /// Use this in behaviors to standardize phase handling across the codebase.
    /// 
    /// USAGE:
    /// 1. Create an enum for your behavior's phases
    /// 2. Create a BehaviorPhaseManager with your enum type
    /// 3. Use SetPhase() to transition between phases
    /// 4. Check IsPhaseTimedOut() in your Update loop
    /// 5. Override GetPhaseTimeout() to set per-phase timeouts
    /// </summary>
    /// <typeparam name="TPhase">The enum type representing behavior phases</typeparam>
    public class BehaviorPhaseManager<TPhase> : IBehaviorPhase<TPhase> where TPhase : Enum
    {
        #region Properties
        
        public TPhase CurrentPhase { get; private set; }
        public float PhaseStartTime { get; private set; }
        public float PhaseTimeout => GetPhaseTimeout(CurrentPhase);
        
        /// <summary>
        /// The previous phase before the current one (for debugging/logging).
        /// </summary>
        public TPhase PreviousPhase { get; private set; }
        
        /// <summary>
        /// How long we've been in the current phase.
        /// </summary>
        public float TimeInCurrentPhase => Time.time - PhaseStartTime;
        
        /// <summary>
        /// Time remaining before phase timeout (negative if already timed out).
        /// </summary>
        public float TimeRemainingInPhase => PhaseTimeout - TimeInCurrentPhase;
        
        #endregion
        
        #region Configuration
        
        /// <summary>
        /// Name of the behavior (for logging).
        /// </summary>
        public string BehaviorName { get; set; } = "Unknown";
        
        /// <summary>
        /// Name of the companion (for logging).
        /// </summary>
        public string CompanionName { get; set; } = "Unknown";
        
        /// <summary>
        /// Whether to log phase transitions.
        /// </summary>
        public bool VerboseLogging { get; set; } = false;
        
        /// <summary>
        /// Default timeout for phases that don't have a specific timeout set.
        /// </summary>
        public float DefaultPhaseTimeout { get; set; } = 30f;
        
        /// <summary>
        /// Function to get custom timeout for specific phases.
        /// Return null to use DefaultPhaseTimeout.
        /// </summary>
        public Func<TPhase, float?> CustomPhaseTimeouts { get; set; }
        
        /// <summary>
        /// Function to get human-readable description of phases.
        /// </summary>
        public Func<TPhase, string> PhaseDescriptions { get; set; }
        
        /// <summary>
        /// Called when a phase transition occurs.
        /// Parameters: (fromPhase, toPhase)
        /// </summary>
        public Action<TPhase, TPhase> OnPhaseChanged { get; set; }
        
        /// <summary>
        /// Called when a phase times out.
        /// Parameters: (phase, timeInPhase)
        /// </summary>
        public Action<TPhase, float> OnPhaseTimeout { get; set; }
        
        #endregion
        
        #region Constructor
        
        public BehaviorPhaseManager(TPhase initialPhase)
        {
            CurrentPhase = initialPhase;
            PreviousPhase = initialPhase;
            PhaseStartTime = Time.time;
        }
        
        public BehaviorPhaseManager(TPhase initialPhase, string behaviorName, string companionName = null)
            : this(initialPhase)
        {
            BehaviorName = behaviorName;
            CompanionName = companionName ?? "Unknown";
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Transitions to a new phase, resetting the phase timer.
        /// </summary>
        public void SetPhase(TPhase newPhase)
        {
            if (CurrentPhase.Equals(newPhase)) return;
            
            PreviousPhase = CurrentPhase;
            TPhase oldPhase = CurrentPhase;
            CurrentPhase = newPhase;
            PhaseStartTime = Time.time;
            
            if (VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] {CompanionName} phase: {oldPhase} -> {newPhase}");
            }
            
            OnPhaseChanged?.Invoke(oldPhase, newPhase);
        }
        
        /// <summary>
        /// Checks if the current phase has exceeded its timeout.
        /// </summary>
        public bool IsPhaseTimedOut()
        {
            float timeout = GetPhaseTimeout(CurrentPhase);
            if (timeout <= 0) return false; // No timeout for this phase
            
            bool timedOut = TimeInCurrentPhase > timeout;
            
            if (timedOut)
            {
                OnPhaseTimeout?.Invoke(CurrentPhase, TimeInCurrentPhase);
            }
            
            return timedOut;
        }
        
        /// <summary>
        /// Checks if timed out and logs a warning if so.
        /// </summary>
        public bool IsPhaseTimedOutWithWarning()
        {
            if (IsPhaseTimedOut())
            {
                if (VerboseLogging)
                {
                    Debug.LogWarning($"[{BehaviorName}] {CompanionName} phase {CurrentPhase} timed out after {TimeInCurrentPhase:F1}s (limit: {PhaseTimeout:F1}s)");
                }
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Gets the timeout duration for a specific phase.
        /// </summary>
        public float GetPhaseTimeout(TPhase phase)
        {
            // Check custom timeouts first
            if (CustomPhaseTimeouts != null)
            {
                float? customTimeout = CustomPhaseTimeouts(phase);
                if (customTimeout.HasValue)
                {
                    return customTimeout.Value;
                }
            }
            
            return DefaultPhaseTimeout;
        }
        
        /// <summary>
        /// Gets a human-readable description of the current phase.
        /// </summary>
        public string GetPhaseDescription()
        {
            return GetPhaseDescription(CurrentPhase);
        }
        
        /// <summary>
        /// Gets a human-readable description of a specific phase.
        /// </summary>
        public string GetPhaseDescription(TPhase phase)
        {
            if (PhaseDescriptions != null)
            {
                return PhaseDescriptions(phase);
            }
            
            // Default: convert enum name to readable string
            return phase.ToString().Replace("_", " ");
        }
        
        /// <summary>
        /// Resets the phase timer without changing the phase.
        /// Useful when you want to give more time in the current phase.
        /// </summary>
        public void ResetPhaseTimer()
        {
            PhaseStartTime = Time.time;
            
            if (VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] {CompanionName} reset timer for phase {CurrentPhase}");
            }
        }
        
        /// <summary>
        /// Extends the phase timer by a specific amount.
        /// </summary>
        public void ExtendPhaseTimer(float additionalTime)
        {
            PhaseStartTime += additionalTime;
            
            if (VerboseLogging)
            {
                Debug.Log($"[{BehaviorName}] {CompanionName} extended phase {CurrentPhase} by {additionalTime:F1}s");
            }
        }
        
        /// <summary>
        /// Returns a debug string with current phase state.
        /// </summary>
        public string GetDebugString()
        {
            return $"[{BehaviorName}] Phase={CurrentPhase}, Time={TimeInCurrentPhase:F1}s/{PhaseTimeout:F1}s, Prev={PreviousPhase}";
        }
        
        #endregion
    }
}
