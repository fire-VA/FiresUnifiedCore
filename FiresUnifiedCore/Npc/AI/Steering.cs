using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>What a sensed thing is to a body on its way somewhere (Fire, 2026-09-29: "register what is around them for objects
    /// in order to properly avoid them. Or find them if they're going to harvest them").</summary>
    public enum ObstacleKind { Obstacle, Harvest, Piece, Creature }

    /// <summary>One frame's steering answer. No strings, so a caller can ask every frame and log only when it changes.</summary>
    public struct SteerResult
    {
        /// <summary>The flat direction to move in (unit), or zero when every direction within the fan is blocked.</summary>
        public Vector3 Direction;
        /// <summary>Degrees turned away from the wanted direction (signed, + = to the right).</summary>
        public float Turn;
        /// <summary>The nearest thing that closed the wanted direction, or null when it was clear.</summary>
        public Collider Blocker;
        /// <summary>Metres to it along the wanted direction.</summary>
        public float BlockerDistance;
        /// <summary>Every direction within the fan was blocked.</summary>
        public bool Boxed;
    }

    /// <summary>
    /// Local steering for any body (the bot's walk and the companions' movement): the navmesh gives the next corner, this
    /// bends the step around what is actually there - rocks, stumps, trees, pieces, a cart, a voxel bump - that the navmesh
    /// missed or that appeared since it was built. Nine directions over +-80 degrees round the wanted one, each cast at knee
    /// height against <see cref="Perception.ObstacleMask"/> (things lower than <see cref="Perception.StepHeight"/> are
    /// stepped over) and at chest height against the same plus the terrain (an overhang, a voxel wall). A direction is
    /// closed when something is within <see cref="Closed"/> metres; the best open one scores alignment x clearance. The thing
    /// the body is walking TO (a tree to chop, a rock to mine, a pickable) never closes a direction: the walk arrives at it.
    /// Colliders a body got stuck on can be remembered for a while (<see cref="Remember"/>); they close the directions that
    /// point at them, and <see cref="Detour"/> gives a point round them on the side with more room.
    /// No allocations per call.
    /// </summary>
    public static class Steering
    {
        public const float Reach = 3.5f;
        public const float Closed = 1.2f;
        public const float FanDegrees = 80f;
        public const int FanSteps = 9;
        public const float KneeHeight = 0.45f;
        public const float ChestHeight = 1.3f;
        public const float CastRadius = 0.3f;
        public const float DetourMargin = 1.2f;
        private const float RememberedReach = 3f;
        private const float RememberedCone = 30f;

        private static int s_chestMask;
        private static int ChestMask
        {
            get
            {
                if (s_chestMask == 0) s_chestMask = Perception.ObstacleMask | LayerMask.GetMask("terrain");
                return s_chestMask;
            }
        }

        private struct Remembered
        {
            public Collider Collider;
            public float Until;
        }

        private static readonly Remembered[] s_remembered = new Remembered[8];

        /// <summary>What a collider is to a walking body: something to harvest, a built piece, a creature, or just in the way.</summary>
        public static ObstacleKind KindOf(Collider collider)
        {
            if (collider == null) return ObstacleKind.Obstacle;
            if (collider.GetComponentInParent<Character>() != null) return ObstacleKind.Creature;
            if (collider.GetComponentInParent<Pickable>() != null || collider.GetComponentInParent<TreeBase>() != null
                || collider.GetComponentInParent<TreeLog>() != null || collider.GetComponentInParent<MineRock>() != null
                || collider.GetComponentInParent<MineRock5>() != null || collider.GetComponentInParent<ItemDrop>() != null)
                return ObstacleKind.Harvest;
            Destructible destructible = collider.GetComponentInParent<Destructible>();
            if (destructible != null && collider.GetComponentInParent<Piece>() == null) return ObstacleKind.Harvest;
            if (collider.GetComponentInParent<Piece>() != null) return ObstacleKind.Piece;
            return ObstacleKind.Obstacle;
        }

        /// <summary>
        /// The direction to take this step toward <paramref name="desired"/> (flat, any length), steering round what is there.
        /// <paramref name="target"/> is the object being walked to, never avoided (null when walking to a point).
        /// </summary>
        public static SteerResult Steer(Character self, Vector3 desired, GameObject target = null)
        {
            var result = new SteerResult();
            if (self == null) return result;
            Vector3 want = new Vector3(desired.x, 0f, desired.z);
            if (want.sqrMagnitude < 0.0001f) return result;
            want.Normalize();
            Vector3 feet = self.transform.position;
            Transform targetRoot = target != null ? target.transform : null;
            float now = Time.time;

            float bestScore = -1f;
            Vector3 best = Vector3.zero;
            float bestTurn = 0f;
            for (int i = 0; i < FanSteps; i++)
            {
                // Order 0, +20, -20, +40, -40 ...: ties keep the smaller turn.
                int k = (i + 1) / 2;
                float turn = (i % 2 == 1 ? 1f : -1f) * k * (2f * FanDegrees / (FanSteps - 1));
                Vector3 dir = Quaternion.AngleAxis(turn, Vector3.up) * want;
                float clearance = Clearance(self, feet, dir, targetRoot, out Collider hitCollider);
                clearance = Mathf.Min(clearance, RememberedClearance(feet, dir, now));
                if (i == 0 && clearance < Reach)
                {
                    result.Blocker = hitCollider;
                    result.BlockerDistance = clearance;
                }
                if (clearance < Closed) continue;
                float align = Mathf.Max(0f, Vector3.Dot(dir, want));
                float score = align * (clearance / Reach);
                if (score > bestScore + 0.0001f)
                {
                    bestScore = score;
                    best = dir;
                    bestTurn = turn;
                }
            }
            if (bestScore < 0f)
            {
                result.Boxed = true;
                return result;
            }
            result.Direction = best;
            result.Turn = bestTurn;
            return result;
        }

        // Metres free along dir: the nearer of the knee cast (things above the step height) and the chest cast (terrain too).
        private static float Clearance(Character self, Vector3 feet, Vector3 dir, Transform target, out Collider hitCollider)
        {
            hitCollider = null;
            float free = Reach;
            RaycastHit hit;
            if (Physics.SphereCast(feet + Vector3.up * KneeHeight, CastRadius, dir, out hit, Reach, Perception.ObstacleMask, QueryTriggerInteraction.Ignore)
                && !Ignored(self, hit.collider, target) && hit.collider.bounds.max.y - feet.y > Perception.StepHeight && hit.distance < free)
            {
                free = hit.distance;
                hitCollider = hit.collider;
            }
            if (Physics.SphereCast(feet + Vector3.up * ChestHeight, CastRadius, dir, out hit, Reach, ChestMask, QueryTriggerInteraction.Ignore)
                && !Ignored(self, hit.collider, target) && hit.distance < free)
            {
                free = hit.distance;
                hitCollider = hit.collider;
            }
            return free;
        }

        private static bool Ignored(Character self, Collider collider, Transform target)
        {
            if (collider == null) return true;
            Transform t = collider.transform;
            if (t.IsChildOf(self.transform)) return true;
            if (target != null && (t == target || t.IsChildOf(target))) return true;
            Rigidbody body = collider.attachedRigidbody;
            return body != null && body.GetComponent<Character>() != null;
        }

        private static float RememberedClearance(Vector3 feet, Vector3 dir, float now)
        {
            float free = Reach;
            for (int i = 0; i < s_remembered.Length; i++)
            {
                Collider c = s_remembered[i].Collider;
                if (c == null || s_remembered[i].Until < now) continue;
                // Concave meshes answer ClosestPoint with the query point (R69): their bounds instead.
                Vector3 knee = feet + Vector3.up * KneeHeight;
                Vector3 closest = !(c is MeshCollider mesh) || mesh.convex ? c.ClosestPoint(knee) : c.bounds.ClosestPoint(knee);
                Vector3 flat = new Vector3(closest.x - feet.x, 0f, closest.z - feet.z);
                float distance = flat.magnitude;
                if (distance > RememberedReach || distance < 0.001f) continue;
                if (Vector3.Angle(dir, flat) <= RememberedCone && distance < free) free = distance;
            }
            return free;
        }

        /// <summary>A collider the body got stuck on closes the directions pointing at it for <paramref name="seconds"/>.</summary>
        public static void Remember(Collider collider, float seconds)
        {
            if (collider == null) return;
            float now = Time.time;
            int slot = 0;
            float oldest = float.MaxValue;
            for (int i = 0; i < s_remembered.Length; i++)
            {
                if (s_remembered[i].Collider == collider) { slot = i; break; }
                if (s_remembered[i].Collider == null || s_remembered[i].Until < now) { slot = i; break; }
                if (s_remembered[i].Until < oldest) { oldest = s_remembered[i].Until; slot = i; }
            }
            s_remembered[slot].Collider = collider;
            s_remembered[slot].Until = now + seconds;
        }

        /// <summary>
        /// A point round the nearest remembered blocker between the body and <paramref name="goal"/>: past its bounds plus
        /// <see cref="DetourMargin"/>, on the side with more room (and, between equals, the one nearer the goal). False when
        /// nothing remembered is in the way.
        /// </summary>
        public static bool Detour(Character self, Vector3 goal, out Vector3 point)
        {
            point = Vector3.zero;
            if (self == null) return false;
            Vector3 feet = self.transform.position;
            Vector3 toGoal = new Vector3(goal.x - feet.x, 0f, goal.z - feet.z);
            if (toGoal.sqrMagnitude < 0.01f) return false;
            Vector3 ahead = toGoal.normalized;
            float now = Time.time;
            Collider blocker = null;
            float nearest = float.MaxValue;
            for (int i = 0; i < s_remembered.Length; i++)
            {
                Collider c = s_remembered[i].Collider;
                if (c == null || s_remembered[i].Until < now) continue;
                Vector3 centre = c.bounds.center;
                Vector3 flat = new Vector3(centre.x - feet.x, 0f, centre.z - feet.z);
                if (Vector3.Dot(flat, ahead) <= 0f) continue;
                float d = flat.magnitude;
                if (d < nearest) { nearest = d; blocker = c; }
            }
            if (blocker == null) return false;
            Bounds b = blocker.bounds;
            Vector3 side = Vector3.Cross(Vector3.up, ahead);
            float half = Mathf.Max(b.extents.x, b.extents.z) + DetourMargin;
            Vector3 centreFlat = new Vector3(b.center.x, feet.y, b.center.z);
            Vector3 right = centreFlat + side * half;
            Vector3 left = centreFlat - side * half;
            float roomRight = Room(feet, right), roomLeft = Room(feet, left);
            bool useRight = Mathf.Abs(roomRight - roomLeft) > 0.25f ? roomRight > roomLeft
                : (right - goal).sqrMagnitude <= (left - goal).sqrMagnitude;
            point = useRight ? right : left;
            return true;
        }

        // How far a body could go from feet toward p before something (not the ground) is in the way, capped at the distance.
        private static float Room(Vector3 feet, Vector3 p)
        {
            Vector3 to = new Vector3(p.x - feet.x, 0f, p.z - feet.z);
            float length = to.magnitude;
            if (length < 0.01f) return 0f;
            RaycastHit hit;
            return Physics.SphereCast(feet + Vector3.up * KneeHeight, CastRadius, to / length, out hit, length, Perception.ObstacleMask, QueryTriggerInteraction.Ignore)
                ? hit.distance : length;
        }
    }
}
