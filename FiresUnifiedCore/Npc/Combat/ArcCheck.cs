using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>What <see cref="ArcCheck.Blocked"/> found on a projectile's path.</summary>
    public struct ArcBlock
    {
        /// <summary>Something solid lies on the arc before the target.</summary>
        public bool Blocked;
        /// <summary>What it is (null when clear).</summary>
        public Collider Blocker;
        /// <summary>How far from the launch point the arc meets it (m, along the ground).</summary>
        public float Distance;
        /// <summary>Where it meets it.</summary>
        public Vector3 Point;
    }

    /// <summary>
    /// Is a bow or crossbow shot's arc clear to the target? (R61 pvp: 4 of 6 of the bot's head arrows hit a voxel ledge on the way,
    /// "obstructed by FiresVoxelTerrain x4"; a real archer holds the shot while the arc is blocked.) The launch direction, speed and
    /// gravity trace the ballistic path in short segments; each is a linecast over solids (terrain, pieces, static things, not
    /// characters) until it passes the target. Shared by the test bot and companions with bows. No allocations.
    /// </summary>
    public static class ArcCheck
    {
        /// <summary>Seconds per traced segment (at ~50 m/s about 2 m).</summary>
        public const float StepSeconds = 0.04f;
        /// <summary>Longest flight traced (s).</summary>
        public const float MaxSeconds = 3f;
        /// <summary>A blocker this close to the target (m) counts as the target's own ground/cover edge, not the lane.</summary>
        public const float TargetSlack = 0.6f;

        private static int s_mask;

        /// <summary>Solids an arrow stops on (no characters).</summary>
        public static int Mask => s_mask != 0 ? s_mask
            : (s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));

        /// <summary>
        /// Traces the arc from <paramref name="from"/> along <paramref name="direction"/> (unit, the launch direction) at
        /// <paramref name="speed"/> m/s under <paramref name="gravity"/> m/s² down, until it passes <paramref name="target"/>
        /// (by flat distance). Blocked when a solid is hit first, more than <see cref="TargetSlack"/> short of the target.
        /// </summary>
        public static ArcBlock Blocked(Vector3 from, Vector3 direction, float speed, float gravity, Vector3 target)
        {
            var result = new ArcBlock();
            if (speed <= 0f || direction.sqrMagnitude < 0.0001f) return result;
            Vector3 velocity = direction.normalized * speed;
            Vector3 flatTarget = new Vector3(target.x - from.x, 0f, target.z - from.z);
            float reach = flatTarget.magnitude;
            Vector3 previous = from;
            for (float t = StepSeconds; t <= MaxSeconds + 0.0001f; t += StepSeconds)
            {
                Vector3 next = from + velocity * t + Vector3.down * (0.5f * gravity * t * t);
                float flat = new Vector2(next.x - from.x, next.z - from.z).magnitude;
                if (Physics.Linecast(previous, next, out RaycastHit hit, Mask, QueryTriggerInteraction.Ignore))
                {
                    float hitFlat = new Vector2(hit.point.x - from.x, hit.point.z - from.z).magnitude;
                    if (hitFlat < reach - TargetSlack)
                    {
                        result.Blocked = true;
                        result.Blocker = hit.collider;
                        result.Distance = hitFlat;
                        result.Point = hit.point;
                    }
                    return result;
                }
                if (flat >= reach) return result;
                previous = next;
            }
            return result;
        }
    }
}
