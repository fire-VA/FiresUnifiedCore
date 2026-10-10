using System;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Interface for behavior phase management.
    /// All work behaviors should implement this to ensure consistent phase handling.
    /// </summary>
    /// <typeparam name="TPhase">The enum type representing the behavior's phases</typeparam>
    public interface IBehaviorPhase<TPhase> where TPhase : Enum
    {
        /// <summary>
        /// The current phase of the behavior.
        /// </summary>
        TPhase CurrentPhase { get; }
        
        /// <summary>
        /// Time when the current phase started (Time.time).
        /// </summary>
        float PhaseStartTime { get; }
        
        /// <summary>
        /// Maximum time allowed for the current phase before timeout.
        /// </summary>
        float PhaseTimeout { get; }
        
        /// <summary>
        /// Transitions to a new phase, resetting the phase timer.
        /// </summary>
        void SetPhase(TPhase phase);
        
        /// <summary>
        /// Checks if the current phase has exceeded its timeout.
        /// </summary>
        bool IsPhaseTimedOut();
        
        /// <summary>
        /// Gets a human-readable description of the current phase.
        /// </summary>
        string GetPhaseDescription();
        
        /// <summary>
        /// Gets the timeout duration for a specific phase.
        /// </summary>
        float GetPhaseTimeout(TPhase phase);
    }
}
