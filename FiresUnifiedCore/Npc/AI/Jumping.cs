using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>One look ahead for a jump. No strings, so a caller can ask each step and log only when it jumps.</summary>
    public struct JumpAdvice
    {
        /// <summary>Jump now (the caller still checks its own stamina, water and cooldown).</summary>
        public bool Jump;
        /// <summary>How far the ground or a thing ahead rises above the feet (m).</summary>
        public float LipHeight;
        /// <summary>How far ahead that rise is (m, flat).</summary>
        public float LipDistance;
        /// <summary>The slope of the ground over the first <see cref="Jumping.SlopeRun"/> m ahead (degrees, + uphill).</summary>
        public float SlopeDegrees;
        /// <summary>The jump was for a stuck body going uphill, not for a lip.</summary>
        public bool ForStuck;
        /// <summary>The rise ahead is a wall: higher than <see cref="Jumping.MaxLip"/> (or a steep face going past the cast), not jumpable.</summary>
        public bool Wall;
    }

    /// <summary>The way out of a hole: the heading whose rim is lowest (<see cref="Jumping.LowestExit"/>).</summary>
    public struct JumpExit
    {
        /// <summary>A heading was measured at all.</summary>
        public bool Found;
        /// <summary>The lowest rim is jumpable (&lt;= <see cref="Jumping.MaxLip"/>).</summary>
        public bool Jumpable;
        /// <summary>Flat unit heading to that rim.</summary>
        public Vector3 Direction;
        /// <summary>That rim's height above the feet (m).</summary>
        public float Lip;
        /// <summary>How far away it is (m, flat).</summary>
        public float Distance;
        /// <summary>The highest rim seen round the body (m): with <see cref="Lip"/>, how deep the hole is.</summary>
        public float HighestLip;
        /// <summary>Headings measured.</summary>
        public int Headings;
    }

    /// <summary>
    /// When a walking body should jump (Fire, 2026-09-29: the bots "get stuck in holes and on hills constantly. They need to know to
    /// jump when they're going uphill"). The ground ahead along the walk is sampled every <see cref="SampleStep"/> m out to
    /// <see cref="LookAhead"/> m (terrain and solid things, rays straight down): a lip that rises more than a step
    /// (<see cref="Perception.StepHeight"/>) but no more than <see cref="MaxLip"/> within <see cref="LipReach"/> m is a jump; so is a
    /// thing in the way that is jumpable but not steppable (a low rock, a log); and a body that is stuck while the way ahead goes up
    /// (a hole's rim, a steep bank) jumps too. The caller owns stamina, water and the cooldown (no bunny-hopping).
    /// No allocations per call.
    /// </summary>
    public static class Jumping
    {
        public const float LookAhead = 1.8f;
        public const float SampleStep = 0.6f;
        public const float LipReach = 1.2f;
        public const float MaxLip = 1.4f;
        public const float SlopeRun = 1.2f;
        public const float StuckRise = 0.2f;
        /// <summary>
        /// Rays start this far above the feet (R61: from feet + 2.4 m a 3 m hole wall read as a 1.2-1.4 m "lip", because the ray
        /// started below the rim and hit the steep wall face; the bot jumped at it six times).
        /// </summary>
        public const float CastAbove = 6f;
        /// <summary>A hit whose normal is steeper than this (its y below) is a wall face, not ground to land on.</summary>
        public const float GroundNormalY = 0.5f;
        /// <summary>How far round a body <see cref="LowestExit"/> looks for the rim (m, flat).</summary>
        public const float ExitReach = 3f;

        private static int s_groundMask;
        private static int GroundMask
        {
            get
            {
                if (s_groundMask == 0) s_groundMask = Perception.ObstacleMask | LayerMask.GetMask("terrain");
                return s_groundMask;
            }
        }

        /// <summary>
        /// Should <paramref name="self"/> jump walking along <paramref name="direction"/> (flat)? <paramref name="stuck"/> = the caller
        /// saw no progress for a while.
        /// </summary>
        public static JumpAdvice Check(Character self, Vector3 direction, bool stuck)
        {
            var advice = new JumpAdvice();
            if (self == null) return advice;
            Vector3 dir = new Vector3(direction.x, 0f, direction.z);
            if (dir.sqrMagnitude < 0.0001f) return advice;
            dir.Normalize();
            Vector3 feet = self.transform.position;

            float slopeRise = 0f;
            float totalRise = 0f;
            float previous = feet.y;
            for (float d = SampleStep; d <= LookAhead + 0.001f; d += SampleStep)
            {
                if (!Ground(feet + dir * d, feet.y, out float h, out bool face)) continue;
                if ((face && h - feet.y > Perception.StepHeight) || h - feet.y > MaxLip)
                {
                    // A wall within reach: nothing a jump gets over. Report its height and stop looking past it.
                    if (d <= LipReach + 0.001f || face)
                    {
                        advice.Wall = true;
                        advice.LipHeight = h - feet.y;
                        advice.LipDistance = d;
                        advice.SlopeDegrees = Mathf.Atan2(h - feet.y, d) * Mathf.Rad2Deg;
                        return advice;
                    }
                }
                float rise = h - feet.y;
                if (d <= SlopeRun + 0.001f) slopeRise = rise;
                if (rise > totalRise) totalRise = rise;
                // A lip is an abrupt rise between two neighbouring samples; a walkable slope rises a little at each step and must
                // not make the body hop all the way up a hill ([generator] review).
                float step = h - previous;
                previous = h;
                if (step > advice.LipHeight)
                {
                    advice.LipHeight = step;
                    advice.LipDistance = d;
                }
            }
            advice.SlopeDegrees = Mathf.Atan2(slopeRise, SlopeRun) * Mathf.Rad2Deg;

            bool lip = advice.LipHeight > Perception.StepHeight && advice.LipHeight <= MaxLip && advice.LipDistance <= LipReach;
            bool uphillStuck = stuck && totalRise > StuckRise && totalRise <= MaxLip + SampleStep;
            if (!lip && uphillStuck) advice.LipHeight = totalRise;
            advice.Jump = lip || uphillStuck;
            advice.ForStuck = !lip && uphillStuck;
            return advice;
        }

        /// <summary>
        /// The way out of a hole or a pocket (R61: the bot jumped six times at the same 3 m wall and never tried another heading).
        /// <paramref name="headings"/> directions round <paramref name="self"/> are each scanned out to <see cref="ExitReach"/> m for
        /// the rim (the highest ground within reach along that heading); the heading with the LOWEST rim is the way out. Jumpable
        /// when that rim is at most <see cref="MaxLip"/> above the feet. No allocations.
        /// </summary>
        public static JumpExit LowestExit(Character self, int headings = 12)
        {
            var exit = new JumpExit { Lip = float.MaxValue };
            if (self == null || headings < 1) return exit;
            Vector3 feet = self.transform.position;
            float step = 360f / headings;
            for (int i = 0; i < headings; i++)
            {
                float a = i * step * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                float rim = float.MinValue, at = 0f;
                bool any = false;
                for (float d = SampleStep; d <= ExitReach + 0.001f; d += SampleStep)
                {
                    if (!Ground(feet + dir * d, feet.y, out float h, out _)) continue;
                    any = true;
                    float rise = h - feet.y;
                    if (rise > rim) { rim = rise; at = d; }
                }
                if (!any) continue;
                exit.Headings++;
                if (rim > exit.HighestLip) exit.HighestLip = rim;
                if (rim < exit.Lip) { exit.Lip = rim; exit.Direction = dir; exit.Distance = at; exit.Found = true; }
            }
            if (!exit.Found) exit.Lip = 0f;
            exit.Jumpable = exit.Found && exit.Lip <= MaxLip;
            return exit;
        }

        // The top surface at p (flat position), cast down from CastAbove over the feet; only surfaces the body could land on count.
        // face = the hit is a steep wall face (the ray grazed a wall rising past the cast, or an overhang): not ground.
        private static bool Ground(Vector3 p, float feetY, out float height, out bool face)
        {
            height = 0f;
            face = false;
            RaycastHit hit;
            Vector3 from = new Vector3(p.x, feetY + CastAbove, p.z);
            if (!Physics.Raycast(from, Vector3.down, out hit, CastAbove + 3f, GroundMask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return false;
            height = hit.point.y;
            face = hit.normal.y < GroundNormalY;
            return true;
        }
    }
}
