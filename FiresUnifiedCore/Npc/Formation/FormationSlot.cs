using UnityEngine;

namespace FiresCore.Npc.Formation
{
    /// <summary>
    /// Represents a companion's assigned position within the group formation.
    /// This is a lightweight data container — all computation is done by GroupFormationManager.
    /// </summary>
    public class FormationSlot
    {
        public string CompanionId;
        public int SlotIndex;             // 0 = tank/leader, 1+ = others
        public Vector3 LocalOffset;       // Offset relative to player forward direction
        public Vector3 WorldPosition;     // Computed world-space position (cached)
        public float LastUpdated;         // Time.time of last computation
        public FormationMode Mode;        // Current formation mode
        
        /// <summary>
        /// Whether this slot has a valid, recently-computed world position.
        /// Stale after 2 seconds.
        /// </summary>
        public bool IsValid => (Time.time - LastUpdated) < 2f;
    }

    /// <summary>
    /// The formation mode determines how companion offsets are calculated.
    /// </summary>
    public enum FormationMode
    {
        /// <summary>Not in formation — companion is in Stay mode, working, or otherwise independent.</summary>
        None,
        /// <summary>V-formation behind the player during travel.</summary>
        Following,
        /// <summary>Spread randomly around the stopped player (±3m).</summary>
        IdleSpread,
        /// <summary>Role-based positioning during combat (tank front, healer back, etc.).</summary>
        Combat
    }
}
