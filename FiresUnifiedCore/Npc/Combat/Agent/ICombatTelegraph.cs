using UnityEngine;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>What a telegraph means to whoever can see it. Drives its colour and pattern.</summary>
    public enum TelegraphIntent
    {
        Damage,
        Heal,
        Buff,

        /// <summary>Ground left dangerous after the cast - molten, frostbound, plagued.</summary>
        Hazard
    }

    /// <summary>The shape a telegraph covers.</summary>
    public enum TelegraphShape
    {
        Circle,

        /// <summary>A charge lane: Length along Direction, Width across it.</summary>
        Line,

        /// <summary>A breath or spray: Radius long, ConeAngleDegrees wide, about Direction.</summary>
        Cone
    }

    /// <summary>
    /// Who a telegraph is drawn for. The distinction is the point of this seam: an aiming preview belongs to
    /// the caster, but anything committed to the ground has to be visible to everyone it can hurt, hostile
    /// PvP players included. A telegraph nobody can see is not a telegraph.
    /// </summary>
    public enum TelegraphAudience
    {
        /// <summary>Still choosing where to put it. The caster's own preview.</summary>
        Aiming,

        /// <summary>Committed to the ground. Everyone who can be hurt by it sees it.</summary>
        Placed
    }

    /// <summary>One telegraph to draw. Radius covers Circle and Cone; Length and Width cover Line.</summary>
    public struct TelegraphRequest
    {
        public Character Source;
        public Vector3 Center;
        public Vector3 Direction;
        public TelegraphShape Shape;
        public TelegraphIntent Intent;
        public TelegraphAudience Audience;
        public float Radius;
        public float Length;
        public float Width;
        public float ConeAngleDegrees;
        public float Duration;
    }

    /// <summary>
    /// Draws ground telegraphs. Implemented over Core's ground rings; monsters and behaviours only ever speak
    /// this interface, so the drawing system stays replaceable.
    /// </summary>
    public interface ICombatTelegraph
    {
        /// <summary>
        /// Draws the request for this viewer. Returns false when this viewer is not shown it, which is normal
        /// for an aiming preview belonging to somebody else - never branch on it as if it were a failure.
        /// </summary>
        bool Show(TelegraphRequest request);

        /// <summary>True when the drawer can render this shape rather than falling back to a circle.</summary>
        bool Supports(TelegraphShape shape);
    }
}
