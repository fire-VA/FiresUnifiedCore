using UnityEngine;
using FiresCore.Npc.Archetypes.Effects;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// Draws telegraphs on Core's ground rings. Line and cone degrade to a circle that covers the same ground,
    /// and hazard borrows the damage colour, until the ring system grows those shapes.
    /// </summary>
    public class GroundRingTelegraph : ICombatTelegraph
    {
        private const float LineCoverFraction = 0.5f;
        private const float MinimumConeRadiusFraction = 0.5f;
        private const float StraightAheadConeDegrees = 60f;

        public bool Supports(TelegraphShape shape) => shape == TelegraphShape.Circle;

        public bool Show(TelegraphRequest request)
        {
            if (request.Duration <= 0f) return false;

            bool aiming = request.Audience == TelegraphAudience.Aiming;
            long casterPlayerId = CasterIdFor(request.Source, aiming);

            ResolveCircle(request, out Vector3 center, out float radius);
            if (radius <= 0f) return false;

            var ring = GroundRingManager.Show(KindFor(request.Intent), center, radius, request.Duration,
                casterPlayerId, aiming);
            return ring != null;
        }

        /// <summary>
        /// A placed telegraph reports caster id 0, which is the ring system's "a monster cast this" and draws
        /// for every viewer. Only an aiming preview is attributed to its player.
        /// </summary>
        private static long CasterIdFor(Character source, bool aiming)
        {
            if (!aiming) return 0L;
            var player = source as Player;
            return player != null ? player.GetPlayerID() : 0L;
        }

        private static GroundRingKind KindFor(TelegraphIntent intent)
        {
            switch (intent)
            {
                case TelegraphIntent.Heal: return GroundRingKind.Heal;
                case TelegraphIntent.Buff: return GroundRingKind.Buff;
                default: return GroundRingKind.Damage;
            }
        }

        /// <summary>
        /// The circle that stands in for the requested shape. A line becomes a circle over its middle, wide
        /// enough to cover the lane; a cone becomes a circle over the ground it sweeps.
        /// </summary>
        private static void ResolveCircle(TelegraphRequest request, out Vector3 center, out float radius)
        {
            center = request.Center;

            switch (request.Shape)
            {
                case TelegraphShape.Line:
                {
                    Vector3 direction = FlatDirection(request.Direction);
                    center = request.Center + direction * (request.Length * LineCoverFraction);
                    radius = Mathf.Max(request.Length * LineCoverFraction, request.Width * LineCoverFraction);
                    return;
                }
                case TelegraphShape.Cone:
                {
                    Vector3 direction = FlatDirection(request.Direction);
                    float angle = request.ConeAngleDegrees > 0f ? request.ConeAngleDegrees : StraightAheadConeDegrees;
                    float coverFraction = Mathf.Clamp(angle / 360f, MinimumConeRadiusFraction, 1f);
                    center = request.Center + direction * (request.Radius * (1f - coverFraction));
                    radius = request.Radius * coverFraction;
                    return;
                }
                default:
                    radius = request.Radius;
                    return;
            }
        }

        private static Vector3 FlatDirection(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }
    }
}
